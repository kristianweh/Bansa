using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bansa.Models;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;

namespace Bansa.Services;

/// <summary>
/// Subscribes to the Windows kernel network ETW provider to capture
/// per-process send/recv byte counts in real time. Combined with
/// ProcessEnumerator for connection listings.
///
/// Reversibility: the ETW session is in-memory only. When this class is
/// disposed (or the process exits), the kernel session is torn down.
/// No state is persisted to disk by this monitor.
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    private const string SessionName = "Bansa-KernelNetSession";

    private TraceEventSession? _session;
    private Task? _processingTask;
    private CancellationTokenSource? _cts;
    private readonly object _restartLock = new();

    // Timestamp of the previous EmitSample call — used to compute actual elapsed time
    // rather than assuming exactly 500 ms (Task.Delay is a minimum, not a guarantee).
    private DateTime _lastSampleTime = DateTime.MinValue;

    // Running tallies per PID, in bytes, since monitor started.
    private readonly ConcurrentDictionary<int, ProcessTally> _tallies = new();

    private class ProcessTally
    {
        public long BytesIn;         // total accumulated by ETW callbacks
        public long BytesOut;
        public long PrevBytesIn;     // value at previous sample tick
        public long PrevBytesOut;
        public bool IsFirstSample = true;  // discard the first delta (ETW startup backlog)
        public string LastKnownName = "";  // cached so dead PIDs keep their display name
        public string LastKnownPath = "";

        // Rolling-window simple moving average (SMA).
        //
        // WHY NOT EMA: an exponential moving average has two fundamental problems for
        // bandwidth display:
        //   • Slow ramp-up — a low rise-alpha (e.g. 0.15) takes 5-10 ticks to reach
        //     the actual rate when a download starts, so the number climbs slowly
        //     instead of appearing at the correct value immediately.
        //   • Sticky wind-down — after a burst the inflated value bleeds out across
        //     many ticks (that "slowly winds down to actual usage" behaviour).
        //
        // SMA fixes both:
        //   • First tick shows the EXACT bytes/second for that tick — no ramp-up.
        //   • When a burst tick exits the window it drops out abruptly — no wind-down.
        //   • Steady downloads always show the true average rate.
        //
        // Window = SmaWindow ticks × ~500 ms/tick = ~2 seconds of history.
        // Rate = Σ(bytes in window) / Σ(actual elapsed seconds in window).
        // Using actual elapsed time (not a fixed 0.5 s) keeps rate correct even when
        // Task.Delay fires late or a GC pause stretches a tick.
        //
        // DownloadThrottler reads raw ETW byte counters via GetRawBytesIn (100 ms
        // cadence), so it is unaffected by this display-only change.
        private const int SmaWindow = 4;   // ~2 seconds

        private readonly long[]   _ringIn      = new long[SmaWindow];
        private readonly long[]   _ringOut     = new long[SmaWindow];
        private readonly double[] _ringElapsed = new double[SmaWindow];
        private int    _ringHead;
        private long   _sumIn;
        private long   _sumOut;
        private double _sumElapsed;

        public void Push(long deltaIn, long deltaOut, double elapsedSec)
        {
            if (elapsedSec < 0.05) elapsedSec = 0.5;
            if (elapsedSec > 5.0)  elapsedSec = 0.5;

            // Remove the oldest slot from the running totals before overwriting it.
            _sumIn      -= _ringIn[_ringHead];
            _sumOut     -= _ringOut[_ringHead];
            _sumElapsed -= _ringElapsed[_ringHead];

            // Write the new sample.
            _ringIn[_ringHead]      = deltaIn;
            _ringOut[_ringHead]     = deltaOut;
            _ringElapsed[_ringHead] = elapsedSec;

            _sumIn      += deltaIn;
            _sumOut     += deltaOut;
            _sumElapsed += elapsedSec;

            _ringHead = (_ringHead + 1) % SmaWindow;
        }

        // When _sumElapsed == 0 the ring is still empty (no data yet) → return 0.
        public long SmoothInPerSec  => _sumElapsed > 0 ? (long)(_sumIn  / _sumElapsed) : 0;
        public long SmoothOutPerSec => _sumElapsed > 0 ? (long)(_sumOut / _sumElapsed) : 0;
    }

    public event Action<IReadOnlyList<ProcessNetInfo>>? SampleReady;

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;

        if (!TraceEventSession.IsElevated() ?? false)
        {
            throw new InvalidOperationException(
                "Bansa needs to run as administrator to capture per-process network traffic via ETW.");
        }

        // Clean up any orphaned session from a previous crashed run.
        try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

        _session = CreateEtwSession();
        _cts = new CancellationTokenSource();

        var capturedSession = _session;
        _processingTask = Task.Run(() => RunEtwSession(capturedSession));

        // Sampling loop: every 500ms — twice per second for snappier updates.
        // Rate math uses actual elapsed time so bytes/sec is correct regardless of interval.
        _ = Task.Run(() => SampleLoop(_cts.Token));

        // Restart the ETW session automatically on wake-from-sleep.
        // The kernel ETW NetworkTCPIP provider silently dies when the machine sleeps —
        // rates go to zero and never recover until we tear down and re-create the session.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        IsRunning = true;
    }

    /// <summary>
    /// Creates and configures a fresh ETW kernel session with all TCP/UDP handlers wired.
    /// All handlers capture <c>_tallies</c> by reference (it is readonly on the instance),
    /// so they remain valid across session restarts.
    /// </summary>
    private TraceEventSession CreateEtwSession()
    {
        var session = new TraceEventSession(SessionName) { StopOnDispose = true };
        session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

        // The Windows kernel ETW TCP/IP provider uses PACKET-DIRECTION addressing for
        // receive events — meaning daddr = the packet's destination = OUR OWN IP for
        // incoming traffic. On a typical home/office machine whose IP is 192.168.x.x,
        // checking only daddr on recv would always return true (RFC-1918 private) and
        // filter out every single download. Chrome, which is almost entirely TCP HTTPS,
        // would show zero bytes.
        //
        // Fix: skip a packet only when BOTH endpoints are local. This is convention-
        // agnostic — it correctly filters loopback (127.→127.) and pure LAN traffic
        // (192.168.→192.168.) while counting anything that touches a public IP on
        // either side, regardless of direction.
        // ── Option C: add IP/transport header overhead to match wire usage.
        // ETW data.size = TCP/UDP payload only; headers are never reported.
        //   IPv4 TCP: IP(20) + TCP(20)   = +40 bytes
        //   IPv4 UDP: IP(20) + UDP(8)    = +28 bytes
        //   IPv6 TCP: IPv6(40) + TCP(20) = +60 bytes
        //   IPv6 UDP: IPv6(40) + UDP(8)  = +48 bytes
        // Pure TCP ACKs (0-payload) are still not counted — inherent ETW limitation.
        session.Source.Kernel.TcpIpRecv += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesIn, data.size + 40);
        };
        session.Source.Kernel.TcpIpSend += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesOut, data.size + 40);
        };
        session.Source.Kernel.UdpIpRecv += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesIn, data.size + 28);
        };
        session.Source.Kernel.UdpIpSend += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesOut, data.size + 28);
        };

        // ── IPv6 variants — Chrome (QUIC/HTTP3), Discord, Steam CDN.
        // IPv6 base header is 40 bytes (vs 20 for IPv4).
        session.Source.Kernel.TcpIpRecvIPV6 += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesIn, data.size + 60);
        };
        session.Source.Kernel.TcpIpSendIPV6 += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesOut, data.size + 60);
        };
        session.Source.Kernel.UdpIpRecvIPV6 += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesIn, data.size + 48);
        };
        session.Source.Kernel.UdpIpSendIPV6 += data =>
        {
            if (IsLocalIP(data.saddr) && IsLocalIP(data.daddr)) return;
            var t = _tallies.GetOrAdd(data.ProcessID, _ => new ProcessTally());
            Interlocked.Add(ref t.BytesOut, data.size + 48);
        };

        return session;
    }

    private static void RunEtwSession(TraceEventSession session)
    {
        try { session.Source.Process(); }
        catch (Exception ex) { Debug.WriteLine($"NetworkMonitor: ETW session ended: {ex.Message}"); }
    }

    /// <summary>
    /// Restarts the ETW kernel session after an unexpected failure (sleep/wake, driver crash).
    /// Called from the SampleLoop watchdog and from the PowerModeChanged handler.
    /// Thread-safe: guarded by <c>_restartLock</c>. Re-entrant calls are no-ops.
    /// </summary>
    private void TryRestartEtwSession()
    {
        lock (_restartLock)
        {
            // Guard: only restart if the task actually died and we're still supposed to be running.
            if (_processingTask?.IsCompleted != true || _cts?.IsCancellationRequested == true) return;
            try
            {
                _session?.Stop();
                _session?.Dispose();
                _session = null;

                // Kill any zombie session before creating a new one.
                try { TraceEventSession.GetActiveSession(SessionName)?.Stop(); } catch { }

                _session = CreateEtwSession();
                var capturedSession = _session;
                _processingTask = Task.Run(() => RunEtwSession(capturedSession));
                Debug.WriteLine("NetworkMonitor: ETW session restarted successfully.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"NetworkMonitor: ETW session restart failed: {ex.Message}");
            }
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // Brief delay: let the NIC and network stack fully re-initialize after wake
        // before we try to re-attach the ETW kernel session.
        _ = Task.Delay(2500).ContinueWith(_ => TryRestartEtwSession());
    }

    private async Task SampleLoop(CancellationToken token)
    {
        _lastSampleTime = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token);

                // Watchdog: if the ETW processing task has exited without being cancelled
                // (sleep/wake killed the kernel session, driver crash, etc.), restart it.
                // This is a cheap IsCompleted check on every tick — no allocation.
                if (_processingTask?.IsCompleted == true && !token.IsCancellationRequested)
                    TryRestartEtwSession();

                var now = DateTime.UtcNow;
                double elapsed = (now - _lastSampleTime).TotalSeconds;
                _lastSampleTime = now;
                EmitSample(elapsed);
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex) { Debug.WriteLine($"Sample loop error: {ex.Message}"); }
        }
    }

    private void EmitSample(double elapsedSec = 0.5)
    {
        var now = DateTime.UtcNow;

        var connections = ProcessEnumerator.GetConnectionsByPid();
        var results = new List<ProcessNetInfo>();

        // All PIDs we know about — from ETW traffic OR from active connections.
        var allPids = new HashSet<int>(_tallies.Keys);
        foreach (var pid in connections.Keys) allPids.Add(pid);

        foreach (var pid in allPids)
        {
            var tally = _tallies.GetOrAdd(pid, _ => new ProcessTally());

            long bytesIn  = Interlocked.Read(ref tally.BytesIn);
            long bytesOut = Interlocked.Read(ref tally.BytesOut);

            if (tally.IsFirstSample)
            {
                // First time we're sampling this PID: ETW may have accumulated a
                // backlog since the session started.  Snapshot the current totals so
                // the NEXT tick's delta starts from here (no backlog spike).
                // With SMA we do NOT push a dummy zero — the ring starts empty and
                // SmoothInPerSec returns 0 until the first real delta arrives, which
                // then shows at the correct rate immediately (no ramp-up).
                tally.PrevBytesIn  = bytesIn;
                tally.PrevBytesOut = bytesOut;
                tally.IsFirstSample = false;
            }
            else
            {
                long deltaIn  = Math.Max(0, bytesIn  - tally.PrevBytesIn);
                long deltaOut = Math.Max(0, bytesOut - tally.PrevBytesOut);
                tally.PrevBytesIn  = bytesIn;
                tally.PrevBytesOut = bytesOut;
                tally.Push(deltaIn, deltaOut, elapsedSec);
            }

            // Resolve name — cache it so dead PIDs keep their human-readable name.
            var (name, path) = ProcessEnumerator.GetProcessInfo(pid);
            if (!string.IsNullOrEmpty(name))
            {
                tally.LastKnownName = name;
                tally.LastKnownPath = path;
            }
            else if (!string.IsNullOrEmpty(tally.LastKnownName))
            {
                name = tally.LastKnownName;
                path = tally.LastKnownPath;
            }

            connections.TryGetValue(pid, out var conns);

            // Prune phantom PIDs: process is gone and has no lingering connections.
            // Removing from _tallies stops it from appearing in future samples.
            if (string.IsNullOrEmpty(name) && (conns == null || conns.Count == 0))
            {
                _tallies.TryRemove(pid, out _);
                continue;
            }

            results.Add(new ProcessNetInfo
            {
                Pid            = pid,
                Name           = name,
                ImagePath      = path,
                BytesInPerSec  = tally.SmoothInPerSec,
                BytesOutPerSec = tally.SmoothOutPerSec,
                TotalBytesIn   = bytesIn,
                TotalBytesOut  = bytesOut,
                Connections    = conns ?? new List<ConnectionInfo>(),
                LastSeen       = now,
            });
        }

        try { SampleReady?.Invoke(results); }
        catch (Exception ex) { Debug.WriteLine($"SampleReady handler threw: {ex.Message}"); }
    }

    /// <summary>
    /// Returns true for loopback, link-local, RFC-1918 private ranges (IPv4),
    /// and the equivalent private / link-local scopes for native IPv6.
    /// IPv4-mapped IPv6 addresses (::ffff:x.x.x.x) are unwrapped before testing.
    /// </summary>
    private static bool IsLocalIP(System.Net.IPAddress addr)
    {
        if (System.Net.IPAddress.IsLoopback(addr)) return true;

        // Unwrap IPv4-mapped IPv6 (e.g. ::ffff:192.168.1.1) so IPv4 byte checks apply.
        var ip = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;
        var b  = ip.GetAddressBytes();

        if (b.Length == 4)
        {
            if (b[0] == 10) return true;                                   // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16-31.0/12
            if (b[0] == 192 && b[1] == 168) return true;                   // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;                   // 169.254.0.0/16 link-local
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64.0.0/10  CGNAT (RFC 6598) — many ISPs
            if (b[0] >= 224) return true;                                  // 224.0.0.0/4 multicast + 240+ reserved
            //   (covers mDNS 224.0.0.251, SSDP 239.255.255.250, broadcast 255.255.255.255)
        }
        else if (b.Length == 16)
        {
            // fe80::/10 — IPv6 link-local (never routed beyond the local segment)
            if (b[0] == 0xfe && (b[1] & 0xc0) == 0x80) return true;
            // fc00::/7  — IPv6 unique-local (RFC 4193; covers fc00:: and fd00:: blocks)
            if ((b[0] & 0xfe) == 0xfc) return true;
            // ff00::/8  — IPv6 multicast (mDNS ff02::fb, all-nodes ff02::1, etc.)
            if (b[0] == 0xff) return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the current total accumulated raw bytes received (NOT SMA-smoothed)
    /// for every process whose image path matches <paramref name="imagePath"/>.
    /// Thread-safe: ConcurrentDictionary iteration + Interlocked.Read on the counter.
    /// Used by DownloadThrottler.InnerTick (100 ms cadence) to measure the bytes
    /// that actually arrived in the last window.
    /// </summary>
    public long GetRawBytesIn(string imagePath)
    {
        long total = 0;
        foreach (var tally in _tallies.Values)
        {
            if (string.Equals(tally.LastKnownPath, imagePath, StringComparison.OrdinalIgnoreCase))
                total += Interlocked.Read(ref tally.BytesIn);
        }
        return total;
    }

    /// <summary>
    /// Returns the current total accumulated raw bytes sent for every process whose
    /// image path matches <paramref name="imagePath"/>.
    /// Used by DownloadThrottler.InnerTick for upload throttling (outbound block rules).
    /// </summary>
    public long GetRawBytesOut(string imagePath)
    {
        long total = 0;
        foreach (var tally in _tallies.Values)
        {
            if (string.Equals(tally.LastKnownPath, imagePath, StringComparison.OrdinalIgnoreCase))
                total += Interlocked.Read(ref tally.BytesOut);
        }
        return total;
    }

    /// <summary>
    /// Returns the best-matching currently-tracked image path for <paramref name="imagePath"/>.
    /// Exact match is always preferred. Falls back to filename-only matching so that apps
    /// updated via Squirrel / Steam (which change the version sub-directory) keep working
    /// without the user needing to re-apply limits after an auto-update.
    /// </summary>
    public string ResolveImagePath(string imagePath)
    {
        foreach (var tally in _tallies.Values)
            if (string.Equals(tally.LastKnownPath, imagePath, StringComparison.OrdinalIgnoreCase))
                return imagePath;

        var fileName = Path.GetFileName(imagePath);
        if (string.IsNullOrEmpty(fileName)) return imagePath;
        foreach (var tally in _tallies.Values)
            if (string.Equals(Path.GetFileName(tally.LastKnownPath), fileName, StringComparison.OrdinalIgnoreCase))
                return tally.LastKnownPath;

        return imagePath;
    }

    public void Stop()
    {
        if (!IsRunning) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { _cts?.Cancel(); } catch { }
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        IsRunning = false;
    }

    public void Dispose() => Stop();
}

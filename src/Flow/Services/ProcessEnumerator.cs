using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Flow.Models;

namespace Flow.Services;

/// <summary>
/// Enumerates active TCP/UDP connections and maps them to owning processes
/// via the Windows IP Helper API (read-only, no state created).
/// </summary>
public static class ProcessEnumerator
{
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        TCP_TABLE_CLASS TableClass,
        int Reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        UDP_TABLE_CLASS TableClass,
        int Reserved);

    private const int AF_INET  = 2;
    private const int AF_INET6 = 23;

    private enum TCP_TABLE_CLASS
    {
        TCP_TABLE_OWNER_PID_ALL = 5
    }

    private enum UDP_TABLE_CLASS
    {
        UDP_TABLE_OWNER_PID = 1
    }

    // ── IPv4 structs ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public byte localPort1, localPort2, localPort3, localPort4;
        public uint remoteAddr;
        public byte remotePort1, remotePort2, remotePort3, remotePort4;
        public uint owningPid;

        public int LocalPort  => (localPort1  << 8) + localPort2;
        public int RemotePort => (remotePort1 << 8) + remotePort2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint localAddr;
        public byte localPort1, localPort2, localPort3, localPort4;
        public uint owningPid;

        public int LocalPort => (localPort1 << 8) + localPort2;
    }

    // ── IPv6 structs ──────────────────────────────────────────────────────────
    // Port bytes are in network (big-endian) order: port = (b0 << 8) | b1.
    // The extra 2 padding bytes in each 4-byte port field are ignored.

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr6;
        public uint   LocalScopeId;
        public byte   localPort1, localPort2, localPort3, localPort4;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteAddr6;
        public uint   RemoteScopeId;
        public byte   remotePort1, remotePort2, remotePort3, remotePort4;
        public uint   State;
        public uint   OwningPid;

        public int LocalPort  => (localPort1  << 8) + localPort2;
        public int RemotePort => (remotePort1 << 8) + remotePort2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] LocalAddr6;
        public uint   LocalScopeId;
        public byte   localPort1, localPort2, localPort3, localPort4;
        public uint   OwningPid;

        public int LocalPort => (localPort1 << 8) + localPort2;
    }

    public static Dictionary<int, List<ConnectionInfo>> GetConnectionsByPid()
    {
        var result = new Dictionary<int, List<ConnectionInfo>>();

        // TCP
        try
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count = Marshal.ReadInt32(buf);
                    IntPtr row = buf + 4;
                    int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                    for (int i = 0; i < count; i++)
                    {
                        var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                        var conn = new ConnectionInfo
                        {
                            Protocol = "TCP",
                            LocalAddress = new IPAddress(r.localAddr).ToString(),
                            LocalPort = r.LocalPort,
                            RemoteAddress = new IPAddress(r.remoteAddr).ToString(),
                            RemotePort = r.RemotePort,
                            State = StateName(r.state),
                        };
                        AddConn(result, (int)r.owningPid, conn);
                        row += rowSize;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Debug.WriteLine($"TCP enum failed: {ex.Message}"); }

        // UDP IPv4
        try
        {
            int size = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size, true, AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedUdpTable(buf, ref size, true, AF_INET, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count = Marshal.ReadInt32(buf);
                    IntPtr row = buf + 4;
                    int rowSize = Marshal.SizeOf<MIB_UDPROW_OWNER_PID>();
                    for (int i = 0; i < count; i++)
                    {
                        var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(row);
                        var conn = new ConnectionInfo
                        {
                            Protocol = "UDP",
                            LocalAddress = new IPAddress(r.localAddr).ToString(),
                            LocalPort = r.LocalPort,
                            RemoteAddress = "",
                            RemotePort = 0,
                            State = "",
                        };
                        AddConn(result, (int)r.owningPid, conn);
                        row += rowSize;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex) { Debug.WriteLine($"UDP enum failed: {ex.Message}"); }

        // TCP IPv6 — Chrome, Discord, Steam CDN, and most modern apps prefer IPv6.
        // Without this, their connection count shows 0 in the UI even when they are
        // actively downloading, and IsLocalOnly is evaluated with an incomplete picture.
        try
        {
            int size6 = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size6, true, AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
            IntPtr buf6 = Marshal.AllocHGlobal(size6);
            try
            {
                if (GetExtendedTcpTable(buf6, ref size6, true, AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0) == 0)
                {
                    int count6 = Marshal.ReadInt32(buf6);
                    IntPtr row6 = buf6 + 4;
                    int rowSize6 = Marshal.SizeOf<MIB_TCP6ROW_OWNER_PID>();
                    for (int i = 0; i < count6; i++)
                    {
                        var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(row6);
                        var conn = new ConnectionInfo
                        {
                            Protocol = "TCP6",
                            LocalAddress  = r.LocalAddr6  != null ? new IPAddress(r.LocalAddr6).ToString()  : "",
                            LocalPort     = r.LocalPort,
                            RemoteAddress = r.RemoteAddr6 != null ? new IPAddress(r.RemoteAddr6).ToString() : "",
                            RemotePort    = r.RemotePort,
                            State         = StateName(r.State),
                        };
                        AddConn(result, (int)r.OwningPid, conn);
                        row6 += rowSize6;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf6); }
        }
        catch (Exception ex) { Debug.WriteLine($"TCP6 enum failed: {ex.Message}"); }

        // UDP IPv6 — QUIC (HTTP/3) used by Chrome and Discord runs as UDP over IPv6.
        try
        {
            int size6 = 0;
            GetExtendedUdpTable(IntPtr.Zero, ref size6, true, AF_INET6, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0);
            IntPtr buf6 = Marshal.AllocHGlobal(size6);
            try
            {
                if (GetExtendedUdpTable(buf6, ref size6, true, AF_INET6, UDP_TABLE_CLASS.UDP_TABLE_OWNER_PID, 0) == 0)
                {
                    int count6 = Marshal.ReadInt32(buf6);
                    IntPtr row6 = buf6 + 4;
                    int rowSize6 = Marshal.SizeOf<MIB_UDP6ROW_OWNER_PID>();
                    for (int i = 0; i < count6; i++)
                    {
                        var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(row6);
                        var conn = new ConnectionInfo
                        {
                            Protocol      = "UDP6",
                            LocalAddress  = r.LocalAddr6 != null ? new IPAddress(r.LocalAddr6).ToString() : "",
                            LocalPort     = r.LocalPort,
                            RemoteAddress = "",
                            RemotePort    = 0,
                            State         = "",
                        };
                        AddConn(result, (int)r.OwningPid, conn);
                        row6 += rowSize6;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf6); }
        }
        catch (Exception ex) { Debug.WriteLine($"UDP6 enum failed: {ex.Message}"); }

        return result;
    }

    private static void AddConn(Dictionary<int, List<ConnectionInfo>> map, int pid, ConnectionInfo c)
    {
        if (!map.TryGetValue(pid, out var list))
        {
            list = new List<ConnectionInfo>();
            map[pid] = list;
        }
        list.Add(c);
    }

    private static string StateName(uint s) => s switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => $"({s})",
    };

    public static (string name, string path) GetProcessInfo(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { }
            return (p.ProcessName, path);
        }
        catch
        {
            return (pid == 0 ? "System Idle" : pid == 4 ? "System" : $"PID {pid}", "");
        }
    }
}

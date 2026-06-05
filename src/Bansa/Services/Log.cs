using System;
using System.IO;

namespace Bansa.Services;

/// <summary>
/// Minimal best-effort logger. Appends to %LocalAppData%\Bansa\crash.log — the same file
/// App writes unhandled exceptions to — so failures we swallow on purpose still leave a
/// trail instead of vanishing. Never throws: logging must not become its own failure mode.
///
/// Reserved for INFREQUENT operations (startup, persistence, periodic jobs, user actions).
/// Per-tick hot paths (the ~2 Hz sampling loop) intentionally keep empty catches so a
/// persistent fault can't spam the log thousands of times.
/// </summary>
public static class Log
{
    private static readonly object _gate = new();

    public static void Debug(string context, Exception ex) => Write($"{context}: {ex}");
    public static void Debug(string message) => Write(message);

    private static void Write(string line)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(
                    Path.Combine(App.DataFolder, "crash.log"),
                    $"[{DateTime.Now:O}] {line}\n");
        }
        catch { /* best-effort; never throw */ }
    }
}

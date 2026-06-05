using System;
using System.Collections.Generic;

namespace Bansa.Services;

/// <summary>
/// Shared chart helpers used by the Network chart, the tray popup, and the floating graph.
/// </summary>
public static class ChartFx
{
    /// <summary>
    /// Default smoothing radius for the rate sparklines (samples on EACH side of a point).
    /// 6 ≈ a 13-sample (~6.5 s) centred window — noticeably flatter on steady downloads.
    /// </summary>
    public const int DefaultRadius = 6;

    /// <summary>
    /// Display-only centred moving-average smoothing applied to the rate series before it
    /// is plotted. The per-app SMA in <see cref="NetworkMonitor"/> (~2 s) keeps the table
    /// numbers responsive; this extra pass exists purely to flatten the residual ripple
    /// caused by ETW delivering bytes in ~1 s flush bursts, so a steady download draws as a
    /// near-flat line (matching NetBalancer's look).
    ///
    /// Centred (averages neighbours on BOTH sides) so interior points carry ZERO phase lag —
    /// the line tracks reality, it just loses the jitter. The live edge has only past
    /// neighbours available, so the newest point is lightly smoothed and the live dot stays
    /// close to the instantaneous rate. Radius ≤ 0 (or &lt; 3 samples) returns the input as-is.
    ///
    /// The raw series is left untouched, so the crosshair tooltip still reports exact
    /// per-instant byte counts.
    /// </summary>
    public static IReadOnlyList<(long Down, long Up)> Smooth(
        IReadOnlyList<(long Down, long Up)> series, int radius = DefaultRadius)
    {
        int n = series.Count;
        if (radius <= 0 || n < 3) return series;

        var result = new (long Down, long Up)[n];
        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - radius);
            int hi = Math.Min(n - 1, i + radius);
            long sumDown = 0, sumUp = 0;
            for (int j = lo; j <= hi; j++)
            {
                sumDown += series[j].Down;
                sumUp   += series[j].Up;
            }
            int count = hi - lo + 1;
            result[i] = (sumDown / count, sumUp / count);
        }
        return result;
    }
}

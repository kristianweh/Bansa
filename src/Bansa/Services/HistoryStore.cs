using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using Bansa.Models;

namespace Bansa.Services;

/// <summary>
/// Local SQLite store for historical bandwidth samples.
/// Lives at %LocalAppData%\Bansa\Bansa.db — uninstall = delete that file.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public HistoryStore()
    {
        var dbPath = Path.Combine(App.DataFolder, "bansa.db");
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        // WAL mode allows two concurrent readers/writers (MainViewModel + HistoryView)
        // to access the same db file without locking each other out.
        using (var wCmd = _conn.CreateCommand())
        {
            wCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            wCmd.ExecuteNonQuery();
        }
        Init();
    }

    private void Init()
    {
        const string sql = @"
            CREATE TABLE IF NOT EXISTS samples (
                ts          INTEGER NOT NULL,        -- unix seconds
                process     TEXT    NOT NULL,
                bytes_in    INTEGER NOT NULL,
                bytes_out   INTEGER NOT NULL,
                PRIMARY KEY (ts, process)
            );
            CREATE INDEX IF NOT EXISTS idx_samples_process ON samples(process);
            CREATE INDEX IF NOT EXISTS idx_samples_ts ON samples(ts);

            CREATE TABLE IF NOT EXISTS hourly (
                hour        INTEGER NOT NULL,
                process     TEXT    NOT NULL,
                bytes_in    INTEGER NOT NULL,
                bytes_out   INTEGER NOT NULL,
                PRIMARY KEY (hour, process)
            );

            CREATE TABLE IF NOT EXISTS activity_log (
                ts      INTEGER NOT NULL,
                app     TEXT    NOT NULL,
                action  TEXT    NOT NULL,
                detail  TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity_log(ts);
        ";
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void RecordSample(IReadOnlyList<ProcessNetInfo> snapshot)
    {
        if (snapshot.Count == 0) return;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT OR REPLACE INTO samples(ts, process, bytes_in, bytes_out)
            VALUES ($ts, $process, $in, $out)";
        var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
        var pName = cmd.CreateParameter(); pName.ParameterName = "$process"; cmd.Parameters.Add(pName);
        var pIn = cmd.CreateParameter(); pIn.ParameterName = "$in"; cmd.Parameters.Add(pIn);
        var pOut = cmd.CreateParameter(); pOut.ParameterName = "$out"; cmd.Parameters.Add(pOut);

        foreach (var p in snapshot)
        {
            if (p.BytesInPerSec == 0 && p.BytesOutPerSec == 0) continue;
            pTs.Value = ts;
            pName.Value = p.Name;
            pIn.Value = p.BytesInPerSec;
            pOut.Value = p.BytesOutPerSec;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        } // lock
    }

    /// <summary>
    /// Returns per-process totals (bytes in / out) over a time window.
    /// Unions the high-resolution samples table (last 24 h) with the hourly rollup table —
    /// Rollup() moves anything older than 24 h into hourly, so querying samples alone
    /// silently drops everything beyond the last day.
    /// </summary>
    public List<(string Name, long BytesIn, long BytesOut)> GetTotals(DateTime fromUtc, DateTime toUtc)
    {
        var from = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        var to = new DateTimeOffset(toUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        lock (_lock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT process, SUM(bi), SUM(bo) FROM (
                SELECT process, bytes_in AS bi, bytes_out AS bo
                FROM samples WHERE ts BETWEEN $from AND $to
                UNION ALL
                SELECT process, bytes_in, bytes_out
                FROM hourly WHERE hour BETWEEN $from AND $to
            )
            GROUP BY process
            ORDER BY SUM(bi) + SUM(bo) DESC";
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to", to);

        var list = new List<(string, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        return list;
        } // lock
    }

    /// <summary>
    /// Returns total bytes (in, out) across all processes over a time window,
    /// spanning both the samples and hourly rollup tables. Used by the
    /// "This month" usage tile.
    /// </summary>
    public (long BytesIn, long BytesOut) GetRangeTotals(DateTime fromUtc, DateTime toUtc)
    {
        var from = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        var to   = new DateTimeOffset(toUtc,   TimeSpan.Zero).ToUnixTimeSeconds();
        lock (_lock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(bi), 0), COALESCE(SUM(bo), 0) FROM (
                SELECT bytes_in AS bi, bytes_out AS bo
                FROM samples WHERE ts BETWEEN $from AND $to
                UNION ALL
                SELECT bytes_in, bytes_out
                FROM hourly WHERE hour BETWEEN $from AND $to
            )";
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to",   to);

        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : (0, 0);
        } // lock
    }

    /// <summary>
    /// Returns hourly bandwidth totals for a single app over a time range.
    /// Merges the high-resolution samples table (last 24 h) and the hourly rollup table.
    /// </summary>
    public List<(long HourTs, long BytesIn, long BytesOut)> GetAppHourly(
        string name, DateTime fromUtc, DateTime toUtc)
    {
        var from = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        var to   = new DateTimeOffset(toUtc,   TimeSpan.Zero).ToUnixTimeSeconds();
        lock (_lock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT (ts / 3600) * 3600 AS hour, SUM(bytes_in), SUM(bytes_out)
            FROM samples
            WHERE process = $name AND ts BETWEEN $from AND $to
            GROUP BY hour
            UNION ALL
            SELECT hour, bytes_in, bytes_out
            FROM hourly
            WHERE process = $name AND hour BETWEEN $from AND $to
            ORDER BY hour";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to",   to);

        var list = new List<(long, long, long)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)));
        return list;
        } // lock
    }

    /// <summary>
    /// Returns total bytes transferred today (midnight local time → now).
    /// The samples table stores bytes/sec rates at 500 ms intervals, so we multiply
    /// the sum by 0.5 to convert to actual bytes.  Only the samples table is queried
    /// because data recorded today will not yet have been rolled to hourly.
    /// </summary>
    public (long BytesIn, long BytesOut) GetTodayTotals()
    {
        var todayLocal = DateTime.Today;                                        // local midnight
        var from = new DateTimeOffset(todayLocal,
                       TimeZoneInfo.Local.GetUtcOffset(todayLocal))
                       .ToUnixTimeSeconds();
        var to   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        lock (_lock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(bytes_in), 0), COALESCE(SUM(bytes_out), 0)
            FROM samples
            WHERE ts BETWEEN $from AND $to";
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to",   to);

        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            // Each row stores a bytes/sec rate, but ts = ToUnixTimeSeconds() (whole seconds)
            // combined with INSERT OR REPLACE means two 500 ms ticks in the same second
            // overwrite each other → effectively ~1 sample/second in the DB.
            // Therefore SUM(bytes_in) ≈ Σ(rate × 1 s) = actual bytes — no ÷2 needed.
            // This matches what the History tab computes via GetTotals() (also no ÷2).
            return (r.GetInt64(0), r.GetInt64(1));
        }
        return (0, 0);
        } // lock
    }

    // ── Activity log ────────────────────────────────────────────────────────

    /// <summary>
    /// Record a user-initiated action (block, limit, priority, etc.) for display
    /// in the History → Activity Log section.
    /// </summary>
    public void LogActivity(string app, string action, string? detail = null)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            lock (_lock)
            {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO activity_log(ts, app, action, detail)
                VALUES ($ts, $app, $action, $detail)";
            cmd.Parameters.AddWithValue("$ts",     ts);
            cmd.Parameters.AddWithValue("$app",    app);
            cmd.Parameters.AddWithValue("$action", action);
            cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            } // lock
        }
        catch { }
    }

    /// <summary>
    /// Returns the most recent activity log entries within the time range,
    /// newest first, capped at 500 rows.
    /// </summary>
    public List<(DateTime When, string App, string Action, string? Detail)> GetActivityLog(
        DateTime fromUtc, DateTime toUtc)
    {
        var from = new DateTimeOffset(fromUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        var to   = new DateTimeOffset(toUtc,   TimeSpan.Zero).ToUnixTimeSeconds();
        lock (_lock)
        {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ts, app, action, detail
            FROM activity_log
            WHERE ts BETWEEN $from AND $to
            ORDER BY ts DESC
            LIMIT 500";
        cmd.Parameters.AddWithValue("$from", from);
        cmd.Parameters.AddWithValue("$to",   to);

        var list = new List<(DateTime, string, string, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var dt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime;
            list.Add((dt, r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        return list;
        } // lock
    }

    // ── Rollup ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Aggregate per-second samples older than 24h into hourly rows
    /// and delete the originals. Keeps the DB bounded over time.
    /// </summary>
    public void Rollup()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
        lock (_lock)
        {
        using var tx = _conn.BeginTransaction();
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT OR REPLACE INTO hourly(hour, process, bytes_in, bytes_out)
                SELECT (ts / 3600) * 3600 AS hour, process, SUM(bytes_in), SUM(bytes_out)
                FROM samples
                WHERE ts < $cutoff
                GROUP BY hour, process";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM samples WHERE ts < $cutoff";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        } // lock
    }

    public void Dispose()
    {
        try { _conn.Close(); } catch { }
        _conn.Dispose();
    }
}

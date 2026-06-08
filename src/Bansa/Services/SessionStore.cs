using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Bansa.Services;

/// <summary>One persisted hardware-monitoring session (metadata + samples).</summary>
public sealed class SavedSession
{
    public string Id { get; set; } = "";          // file stem, e.g. 20260608-081415
    public DateTime StartedAt { get; set; }
    public DateTime StoppedAt { get; set; }
    public List<SessionRecorder.Sample> Samples { get; set; } = new();

    public TimeSpan Duration => StoppedAt > StartedAt ? StoppedAt - StartedAt : TimeSpan.Zero;
}

/// <summary>
/// Persists recorded sessions as individual JSON files under
/// <c>%AppData%/Bansa/sessions/</c> so they survive restarts and can be revisited.
/// Dependency-free (System.Text.Json, same as settings).
/// </summary>
public static class SessionStore
{
    private static string Dir => Path.Combine(App.DataFolder, "sessions");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    /// <summary>Fires after a session is saved or deleted (marshal to UI in the handler).</summary>
    public static event Action? Changed;

    /// <summary>Persists a session and returns its id. No-op (returns "") for &lt; 2 samples.</summary>
    public static string Save(DateTime startedAt, DateTime stoppedAt, List<SessionRecorder.Sample> samples)
    {
        if (samples.Count < 2) return "";
        try
        {
            Directory.CreateDirectory(Dir);
            string id = startedAt.ToString("yyyyMMdd-HHmmss");
            var session = new SavedSession
            {
                Id = id, StartedAt = startedAt, StoppedAt = stoppedAt, Samples = samples
            };
            File.WriteAllText(Path.Combine(Dir, id + ".json"), JsonSerializer.Serialize(session, Opts));
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Debug("SessionStore.Changed", ex); }
            return id;
        }
        catch (Exception ex) { Log.Debug("SessionStore.Save", ex); return ""; }
    }

    /// <summary>All saved sessions, newest first.</summary>
    public static List<SavedSession> List()
    {
        var result = new List<SavedSession>();
        try
        {
            if (!Directory.Exists(Dir)) return result;
            foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(file));
                    if (s is not null && s.Samples.Count >= 2) result.Add(s);
                }
                catch (Exception ex) { Log.Debug($"SessionStore.List({file})", ex); }
            }
        }
        catch (Exception ex) { Log.Debug("SessionStore.List", ex); }
        return result.OrderByDescending(s => s.StartedAt).ToList();
    }

    public static SavedSession? Load(string id)
    {
        try
        {
            var file = Path.Combine(Dir, id + ".json");
            return File.Exists(file)
                ? JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(file))
                : null;
        }
        catch (Exception ex) { Log.Debug("SessionStore.Load", ex); return null; }
    }

    public static void Delete(string id)
    {
        try
        {
            var file = Path.Combine(Dir, id + ".json");
            if (File.Exists(file)) File.Delete(file);
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Debug("SessionStore.Changed", ex); }
        }
        catch (Exception ex) { Log.Debug("SessionStore.Delete", ex); }
    }
}

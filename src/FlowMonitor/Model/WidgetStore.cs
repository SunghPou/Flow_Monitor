using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowMonitor.Model;

/// <summary>
/// One JSON file per widget in the first writable store root
/// (<c>%LOCALAPPDATA%\FlowMonitor\widgets</c>, else next to the binary). A file per widget
/// rather than one array means a corrupt or half-written file can only ever cost the user that
/// one widget, never the whole layout.
/// </summary>
public static class WidgetStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Directory { get; } = ResolveDirectory();

    /// <summary>Test-only store root; null in the app. Set before a store round trip.</summary>
    internal static string? DirectoryOverride;

    /// <summary>Root the store settles on, after a writable probe and a config-count tiebreak.</summary>
    internal static string ResolvedDirectory => DirectoryOverride ?? Directory;

    /// <summary>
    /// Picks the store root: every candidate must survive a real write probe, and a
    /// candidate that already holds configs beats one that does not, so an existing
    /// layout is never abandoned for an empty directory.
    /// </summary>
    static string ResolveDirectory()
    {
        string? firstWritable = null;
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string dir = Path.Combine(root, "FlowMonitor", "widgets");
            if (!Writable(dir)) continue;
            if (CountConfigs(dir) > 0) return dir;
            firstWritable ??= dir;
        }
        if (firstWritable is not null) return firstWritable;
        return Path.Combine(AppContext.BaseDirectory, "widgets");
    }

    /// <summary>Create + write + delete a probe file: an existing directory is not proof of access.</summary>
    static bool Writable(string dir)
    {
        string probe = Path.Combine(dir, $".probe-{Environment.ProcessId}");
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            File.WriteAllText(probe, "0");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static int CountConfigs(string dir)
    {
        try { return System.IO.Directory.Exists(dir) ? System.IO.Directory.GetFiles(dir, "*.json").Length : 0; }
        catch { return 0; }
    }

    static string PathFor(string id) => Path.Combine(ResolvedDirectory, Sanitize(id) + ".json");

    /// <summary>Keeps a caller-supplied id from escaping the widgets directory.</summary>
    static string Sanitize(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "widget";
        var chars = id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "widget" : new string(chars);
    }

    public static void Save(WidgetConfig cfg)
    {
        try
        {
            string path = PathFor(cfg.Id);
            // Unique temp name: a leftover or held temp file can never block a save.
            string tmp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
            // Replace atomically so a crash mid-write cannot leave a truncated config behind.
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("WidgetStore.Save failed: " + ex.Message);
        }
    }

    public static void Delete(string id)
    {
        try { File.Delete(PathFor(id)); }
        catch (Exception ex) { Log.Warn("WidgetStore.Delete failed: " + ex.Message); }
    }

    /// <summary>All saved widgets. Unreadable files are skipped and logged, not fatal.</summary>
    public static List<WidgetConfig> LoadAll()
    {
        var result = new List<WidgetConfig>();
        try
        {
            if (!System.IO.Directory.Exists(ResolvedDirectory)) return result;
            foreach (string file in System.IO.Directory.EnumerateFiles(ResolvedDirectory, "*.json"))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(file), Options);
                    if (cfg is null) continue;
                    // Saved configs keep enum values stable, so a widget saved on the retired
                    // per-engine GPU card comes back as the single GPU card.
                    if (cfg.Graph == GraphKind.GpuCores) cfg.Graph = GraphKind.Gpu;
                    result.Add(cfg);
                }
                catch (Exception ex)
                {
                    Log.Warn("skipping unreadable widget config " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("WidgetStore.LoadAll failed: " + ex.Message);
        }
        Log.Info($"widget store: {ResolvedDirectory} ({result.Count} config(s))");
        return result;
    }
}

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

    /// <summary>Previous bin-adjacent root, kept for the one-time migration below. Test seam.</summary>
    internal static string? LegacyDirectoryOverride;

    /// <summary>Root the store settles on, after a writable probe and a config-count tiebreak.</summary>
    internal static string ResolvedDirectory => DirectoryOverride ?? Directory;

    static string LegacyDirectory => LegacyDirectoryOverride
        ?? Path.Combine(AppContext.BaseDirectory, "FlowMonitor", "widgets");

    /// <summary>
    /// The store lives in exactly one place (%LOCALAPPDATA%\FlowMonitor\widgets), so a
    /// stray file can never hijack widget resolution the way the old two-root tiebreak
    /// allowed. Next to the binary is only a fallback when LocalAppData is not writable.
    /// </summary>
    static string ResolveDirectory()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlowMonitor", "widgets");
        if (!string.IsNullOrEmpty(local) && Writable(local)) return local;
        string bin = LegacyDirectory;
        if (Writable(bin)) return bin;
        return bin;
    }

    /// <summary>
    /// One-time move of configs saved next to the binary into the single store root.
    /// Runs only into an empty local store, so it can never merge over real user data;
    /// leftovers stay put when a name collides. Override-aware, so the self test runs it
    /// against temp directories.
    /// </summary>
    internal static void MigrateLegacyStore()
    {
        try
        {
            string target = ResolvedDirectory;
            string legacy = LegacyDirectory;
            if (string.Equals(legacy, target, StringComparison.OrdinalIgnoreCase)) return;
            if (!System.IO.Directory.Exists(legacy) || CountConfigs(target) > 0) return;
            System.IO.Directory.CreateDirectory(target);
            int moved = 0;
            foreach (string file in System.IO.Directory.EnumerateFiles(legacy, "*.json"))
            {
                string dest = Path.Combine(target, Path.GetFileName(file));
                try
                {
                    if (System.IO.File.Exists(dest)) continue;
                    System.IO.File.Move(file, dest);
                    moved++;
                }
                catch (Exception ex)
                {
                    Log.Warn("store migration skipped " + Path.GetFileName(file) + ": " + ex.Message);
                }
            }
            if (moved > 0) Log.Info($"widget store: migrated {moved} config(s) next to the binary");
        }
        catch (Exception ex)
        {
            Log.Warn("store migration failed: " + ex.Message);
        }
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
                    // The retired Full click-through (2) behaved like LeftClickOnly;
                    // normalise it so the dead value never round-trips back to disk.
                    if (!Enum.IsDefined(cfg.ClickThrough))
                        cfg.ClickThrough = ClickThroughMode.LeftClickOnly;
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

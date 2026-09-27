using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowMonitor.Model;

/// <summary>
/// One JSON file per widget under <c>%LOCALAPPDATA%\FlowMonitor\widgets</c>. A file per widget
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

    static string ResolveDirectory()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                     AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(root)) continue;
            try
            {
                string dir = Path.Combine(root, "FlowMonitor", "widgets");
                System.IO.Directory.CreateDirectory(dir);
                return dir;
            }
            catch { /* try the next candidate */ }
        }
        return Path.Combine(AppContext.BaseDirectory, "widgets");
    }

    static string PathFor(string id) => Path.Combine(Directory, Sanitize(id) + ".json");

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
            string tmp = path + ".tmp";
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
            if (!System.IO.Directory.Exists(Directory)) return result;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    var cfg = JsonSerializer.Deserialize<WidgetConfig>(File.ReadAllText(file), Options);
                    if (cfg is not null) result.Add(cfg);
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
        return result;
    }
}

namespace FlowMonitor.Host;

/// <summary>
/// Start with Windows via a shortcut in the per-user Startup folder: no admin, visible
/// as a file, and the checked state reads back off the link target. The value name is a
/// parameter so the self test can round-trip its own link.
/// </summary>
internal static class AutoStart
{
    const string FileName = "FlowMonitor.lnk";

    static string StartupDir(string? dir = null)
        => dir ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static string LinkPath(string? dir = null)
        => Path.Combine(StartupDir(dir), FileName);

    public static bool Enabled => EnabledFor(FileName);

    public static bool EnabledFor(string fileName)
    {
        try
        {
            string? target = ShellLink.ReadTarget(Path.Combine(StartupDir(), fileName));
            string exe = Environment.ProcessPath ?? "";
            return target is not null && exe.Length > 0
                && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Set(bool on) => SetFor(FileName, on);

    public static void SetFor(string fileName, bool on)
    {
        try
        {
            string link = Path.Combine(StartupDir(), fileName);
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                ShellLink.Write(link, exe, Path.GetDirectoryName(exe) ?? "", "FlowMonitor desktop widgets");
            }
            else File.Delete(link);
        }
        catch (Exception ex)
        {
            Log.Warn("auto-start: " + ex.Message);
        }
    }
}

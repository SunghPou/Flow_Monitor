namespace FlowMonitor.Host;

/// <summary>
/// One Start Menu link to this exe, re-asserted on every launch: the install path is
/// stable across restarts, so the link never goes stale. Per-user Programs folder, no
/// admin, failures only logged. Native IShellLink, no WSH layer.
/// </summary>
internal static class StartMenuLink
{
    public static string LinkPath(string? programsDir = null)
        => Path.Combine(programsDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            "FlowMonitor.lnk");

    public static void Ensure() => EnsureInto(LinkPath());

    internal static void EnsureInto(string linkPath)
    {
        // The Programs folder is watched by Explorer and the indexer, so the save
        // can lose a sharing race; a few retries beat a missing link.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                string have = ShellLink.ReadTarget(linkPath) ?? "";
                if (string.Equals(have, exe, StringComparison.OrdinalIgnoreCase)) return;
                ShellLink.Write(linkPath, exe, Path.GetDirectoryName(exe) ?? "",
                    "FlowMonitor desktop widgets");
                return;
            }
            catch (Exception ex)
            {
                if (i == 4) Log.Warn("start menu link: " + ex.Message);
                else System.Threading.Thread.Sleep(200);
            }
        }
    }
}

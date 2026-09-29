using System.Reflection;

namespace FlowMonitor.Host;

/// <summary>
/// One Start Menu link to this exe, re-asserted on every launch: the build output path
/// is stable across rebuilds, so the link never goes stale. Per-user Programs folder,
/// no admin, failures only logged. WScript.Shell via reflection, so no COM reference.
/// </summary>
internal static class StartMenuLink
{
    public static string LinkPath(string? programsDir = null)
        => Path.Combine(programsDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            "FlowMonitor.lnk");

    public static void Ensure()
    {
        // The Programs folder is watched by Explorer and the indexer, so the save
        // can lose a sharing race; a few retries beat a missing link.
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Write();
                return;
            }
            catch (Exception ex)
            {
                if (i == 4) Log.Warn("start menu link: " + (ex.InnerException ?? ex).Message);
                else System.Threading.Thread.Sleep(200);
            }
        }
    }

    static void Write()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;
        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;
        object shell = Activator.CreateInstance(shellType)!;
        object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
            null, shell, [LinkPath()])!;
        Type t = lnk.GetType();
        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, [exe]);
        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk,
            [Path.GetDirectoryName(exe)]);
        t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk,
            ["FlowMonitor desktop widgets"]);
        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
    }
}

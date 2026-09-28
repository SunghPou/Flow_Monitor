namespace FlowMonitor;

/// <summary>
/// Append-only diagnostic log under %LOCALAPPDATA%\FlowMonitor. The product shows no windows of
/// its own, so this is the only way a failure is observable. Writes are best-effort and never throw.
/// </summary>
public static class Log
{
    static bool _enabled = Environment.GetEnvironmentVariable("FLOWMONITOR_LOG") != "0";

    public static string Directory
    {
        get
        {
            foreach (string root in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppContext.BaseDirectory,
            })
            {
                if (string.IsNullOrEmpty(root)) continue;
                try
                {
                    string dir = System.IO.Path.Combine(root, "FlowMonitor");
                    System.IO.Directory.CreateDirectory(dir);
                    return dir;
                }
                catch { }
            }
            return AppContext.BaseDirectory;
        }
    }

    public static string Path1 => System.IO.Path.Combine(Directory, "flowmonitor.log");

    public static void Write(string level, string message)
    {
        if (!_enabled) return;
        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";

            // Two destinations on purpose: the roaming-friendly one under %LOCALAPPDATA%, and a
            // copy next to the executable so a portable copy that cannot write to the profile
            // still leaves a trace.
            foreach (string p in new[] { Path1, System.IO.Path.Combine(AppContext.BaseDirectory, "flowmonitor.log") })
            {
                try { System.IO.File.AppendAllText(p, line); } catch { }
            }
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Fatal(Exception ex)
    {
        Write("FATAL", ex.ToString());
    }
}

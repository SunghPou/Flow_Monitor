namespace FlowMonitor.Host;

/// <summary>
/// Start with Windows via the per-user Run key (no admin, no shortcut file to go stale).
/// The value name is a parameter so the self test can round-trip its own key.
/// </summary>
internal static class AutoStart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Value = "FlowMonitor";

    public static bool Enabled => EnabledFor(Value);

    public static bool EnabledFor(string value)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(value) is string;
        }
        catch { return false; }
    }

    public static void Set(bool on) => SetFor(Value, on);

    public static void SetFor(string value, bool on)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key);
            if (k is null) return;
            if (on)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) return;
                k.SetValue(value, "\"" + exe + "\"");
            }
            else k.DeleteValue(value, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("auto-start: " + ex.Message);
        }
    }
}

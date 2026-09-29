using System.Runtime.InteropServices;

namespace FlowMonitor.Host;

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
sealed class ShellLinkObject { }

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
    void GetIDList(out IntPtr ppidl);
    void SetIDList(IntPtr pidl);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
    void GetHotkey(out short pwHotkey);
    void SetHotkey(short wHotkey);
    void GetShowCmd(out int piShowCmd);
    void SetShowCmd(int iShowCmd);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
    void Resolve(IntPtr hwnd, int fFlags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IPersistFile
{
    void GetClassID(out Guid pClassID);
    void IsDirty();
    void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
    void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
}

/// <summary>
/// Shortcut files via native IShellLink, no WSH layer: WScript.Shell refuses to save
/// from some process locations while the shell API itself may still work.
/// </summary>
internal static class ShellLink
{
    /// <summary>Writes a shortcut; throws on failure so callers can retry or warn.</summary>
    public static void Write(string linkPath, string target, string workDir, string description)
    {
        var link = (IShellLinkW)(object)new ShellLinkObject();
        link.SetPath(target);
        if (workDir.Length > 0) link.SetWorkingDirectory(workDir);
        if (description.Length > 0) link.SetDescription(description);
        link.SetIconLocation(target, 0);
        ((IPersistFile)link).Save(linkPath, true);
    }

    /// <summary>Reads a shortcut's target, or null when it cannot be read.</summary>
    public static string? ReadTarget(string linkPath)
    {
        try
        {
            var link = (IShellLinkW)(object)new ShellLinkObject();
            ((IPersistFile)link).Load(linkPath, 0);
            var sb = new System.Text.StringBuilder(260);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            string target = sb.ToString();
            return target.Length > 0 ? target : null;
        }
        catch
        {
            return null;
        }
    }
}

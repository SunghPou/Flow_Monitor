using System.Runtime.InteropServices;
using FlowMonitor.Interop;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Host;

/// <summary>
/// Notification-area icon with a native popup menu: New widget, Restart, Start with
/// Windows (checked), Exit. The control window owns the callback; the menu runs on the
/// UI thread with a returned command id, so no extra window or loop is needed.
/// </summary>
internal static class TrayIcon
{
    public const uint CallbackMessage = WM_APP + 4;

    const int CmdNewWidget = 1;
    const int CmdRestart = 2;
    const int CmdAutoStart = 3;
    const int CmdExit = 4;

    const uint Id = 1;
    static IntPtr _icon;
    static bool _ownIcon;
    static bool _added;
    static uint _taskbarCreated;

    /// <summary>Broadcast when Explorer restarts; the icon must be re-added after it.</summary>
    public static uint TaskbarCreated => _taskbarCreated != 0
        ? _taskbarCreated
        : (_taskbarCreated = RegisterWindowMessageW("TaskbarCreated"));

    public static void Add(IntPtr owner)
    {
        if (_added) return;
        _ = TaskbarCreated;
        if (_icon == IntPtr.Zero)
        {
            string exe = Environment.ProcessPath ?? "";
            if (exe.Length > 0) _icon = ExtractIconW(GetModuleHandle(null), exe, 0);
            _ownIcon = _icon != IntPtr.Zero;
            if (!_ownIcon) _icon = LoadIconW(IntPtr.Zero, new IntPtr(32512)); // IDI_APPLICATION
        }
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = owner,
            uID = Id,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = "FlowMonitor",
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!_added) Log.Warn("tray: Shell_NotifyIcon failed " + Marshal.GetLastWin32Error());
    }

    public static void ReAdd(IntPtr owner)
    {
        _added = false;
        Add(owner);
    }

    public static void Remove()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                uID = Id,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_ownIcon && _icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; _ownIcon = false; }
    }

    /// <summary>Handles the tray callback; true when the message was a tray event.</summary>
    public static bool OnCallback(DesktopHost host, IntPtr lParam)
    {
        int m = lParam.ToInt32();
        if (m != WM_LBUTTONUP && m != WM_RBUTTONUP) return false;
        ShowMenu(host);
        return true;
    }

    static void ShowMenu(DesktopHost host)
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenuW(menu, MF_STRING, CmdNewWidget, "New widget");
            AppendMenuW(menu, MF_SEPARATOR, 0, "");
            AppendMenuW(menu, MF_STRING, CmdRestart, "Restart");
            AppendMenuW(menu, MF_STRING | (AutoStart.Enabled ? MF_CHECKED : 0), CmdAutoStart, "Start with Windows");
            AppendMenuW(menu, MF_SEPARATOR, 0, "");
            AppendMenuW(menu, MF_STRING, CmdExit, "Exit");
            GetCursorPos(out var at);
            // Foreground ownership + a null message afterwards: the menu dismisses when
            // the user clicks elsewhere instead of sticking around.
            SetForegroundWindow(host.ControlHandle);
            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                at.X, at.Y, 0, host.ControlHandle, IntPtr.Zero);
            PostMessage(host.ControlHandle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            switch (cmd)
            {
                case CmdNewWidget: host.AddWidgetFromTray(); break;
                case CmdRestart: host.Restart(); break;
                case CmdAutoStart: AutoStart.Set(!AutoStart.Enabled); break;
                case CmdExit: host.Exit(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}

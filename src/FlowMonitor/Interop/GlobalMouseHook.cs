using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// A system-wide left-click watcher, so the picker's eyedropper can hear the click that
/// lands on some other application's window. A window message cannot do this: after the
/// card hides itself the click is delivered to whatever window is under the cursor, on
/// that window's thread, so it never reaches our message queue. WH_MOUSE_LL is the
/// documented way to see it - the hook runs on our thread whenever we pump messages.
/// </summary>
internal static class GlobalMouseHook
{
    const int WmMouseMove = 0x0200, WmLButtonDown = 0x0201;
    const int WhMouseLl = 14;
    const int HcAction = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct MsllHookStruct { public Point Pt; public uint MouseData; public uint Flags; public uint Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct Point { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    /// <summary>Raised on our thread, inside our own message pump, when the left button goes down.</summary>
    public static event Action<int, int>? Clicked;

    static HookProc? _proc;   // the delegate must outlive the hook
    static IntPtr _hook;

    public static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Warn("eyedropper: SetWindowsHookEx failed " + Marshal.GetLastWin32Error());
    }

    public static void Remove()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string? name);

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HcAction)
        {
            int msg = (int)wParam;
            if (msg == WmLButtonDown)
            {
                var s = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                Clicked?.Invoke(s.Pt.X, s.Pt.Y);
            }
            else if (msg == WmMouseMove)
            {
                // Every window under the cursor re-asserts its own cursor on each move,
                // so the pipette has to be pushed back or the user sees an arrow.
                Interop.EyedropperCursor.Apply(true);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
}

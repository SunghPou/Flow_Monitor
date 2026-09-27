using FlowMonitor.Interop;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Host;

/// <summary>
/// Locates Explorer's WorkerW window that sits *behind* the desktop icons. Parenting widgets
/// to that window is what makes them real desktop widgets: they render under the icons,
/// never appear in the taskbar or Alt+Tab, and never take focus.
/// </summary>
internal static class Desktop
{
    public static IntPtr FindWorkerW()
    {
        var progman = FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            // Ask Explorer to (re)create its WorkerW host.
            SendMessageTimeoutW(progman, WM_052C_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out _);
        }

        IntPtr frontMost = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            if (GetClassName(hwnd) != "WorkerW") return true;

            // A WorkerW that owns a SHELLDLL_DefView is the one showing the desktop icons.
            var defView = FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
                frontMost = hwnd;   // keep the last (front-most) one

            return true;
        }, IntPtr.Zero);

        if (frontMost == IntPtr.Zero)
        {
            // No SHELLDLL_DefView anywhere: parent to Progman itself, which is still safe.
            return progman;
        }

        // The WorkerW immediately *after* the icons window in Z-order is the one behind the icons.
        var behind = FindWindowExW(IntPtr.Zero, frontMost, "WorkerW", null);
        if (behind != IntPtr.Zero) return behind;

        // Some Windows 11 builds have no separate WorkerW; the icons window itself accepts children.
        return frontMost;
    }

    /// <summary>
    /// Sanity-check a candidate desktop parent before SetParent.
    /// Only WorkerW / Progman are accepted; anything else is rejected.
    /// </summary>
    public static bool IsDesktopLayer(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!IsWindow(hwnd)) return false;

        string cls = GetClassName(hwnd);
        if (cls == "WorkerW") return true;
        if (cls == "Progman") return true;

        // Anything else is not a desktop host, whatever FindWindowW happened to hand back.
        Log.Write("WARN", $"IsDesktopLayer rejected 0x{hwnd:X}: class is '{cls}'.");
        return false;
    }

    /// <summary>Virtual screen bounds in pixels, for clamping widget positions.</summary>
    public static RECT VirtualScreen()
    {
        const int SM_XVIRTUALSCREEN = 76;
        const int SM_YVIRTUALSCREEN = 77;
        const int SM_CXVIRTUALSCREEN = 78;
        const int SM_CYVIRTUALSCREEN = 79;

        return new RECT
        {
            Left = GetSystemMetrics(SM_XVIRTUALSCREEN),
            Top = GetSystemMetrics(SM_YVIRTUALSCREEN),
            Right = GetSystemMetrics(SM_XVIRTUALSCREEN) + GetSystemMetrics(SM_CXVIRTUALSCREEN),
            Bottom = GetSystemMetrics(SM_YVIRTUALSCREEN) + GetSystemMetrics(SM_CYVIRTUALSCREEN),
        };
    }

    /// <summary>Screen coordinates of the WorkerW client origin, for virtual-to-child conversion.</summary>
    public static POINT ClientOrigin(IntPtr workerW)
    {
        var p = new POINT(0, 0);
        ClientToScreen(workerW, ref p);
        return p;
    }
}

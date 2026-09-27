using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// One-pixel screen colour probe for the picker's eyedropper. GDI only: the
/// picker needs a single pixel, not a screenshot.
/// </summary>
internal static class ScreenPick
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    const int VK_LBUTTON = 0x01;

    /// <summary>
    /// Waits for the next left-button press, then reads the pixel under the cursor.
    /// Returns null if the probe cannot run (no DC, no press).
    /// </summary>
    public static Widgets.Rgba? PickUnderCursor(Native.POINT at)
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) Thread.Sleep(10);
            uint c = GetPixel(hdc, at.X, at.Y);
            if (c == 0xFFFFFFFF) return null;
            return new Widgets.Rgba((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }
}

using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// One-pixel screen colour probe for the picker's eyedropper. The picker keeps the
/// mouse capture while the user aims, so the click arrives there and the probe only
/// has to read the pixel it was handed. GDI only: one pixel, not a screenshot.
/// </summary>
internal static class ScreenPick
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>Colour of the screen pixel at (x, y) in virtual-screen coordinates, or null if unreadable.</summary>
    public static Widgets.Rgba? SampleAt(int x, int y)
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            uint c = GetPixel(hdc, x, y);
            if (c == 0xFFFFFFFF) return null;
            return new Widgets.Rgba(
                (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }
}

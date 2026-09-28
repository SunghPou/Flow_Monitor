using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// One-pixel screen colour probe for the picker's eyedropper: while the user moves
/// over the desktop the pointer is the pipette, the next left click samples the
/// pixel under the cursor at that moment, and Esc cancels. GDI only, the picker
/// needs a single pixel, not a screenshot.
/// </summary>
internal static class ScreenPick
{
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr hdc, int x, int y);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    const int VK_LBUTTON = 0x01;
    const int VK_ESCAPE = 0x1B;

    /// <summary>
    /// Blocks until the user clicks a pixel (returns it) or presses Esc (null).
    /// The pipette cursor is re-applied every poll because Windows hands
    /// WM_SETCURSOR to whichever window is under the mouse, not to us.
    /// </summary>
    public static Widgets.Rgba? PickUnderCursor()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return null;
        try
        {
            while (true)
            {
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) return null;
                if ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                {
                    // The press position, not where the dropper field was clicked.
                    Native.GetCursorPos(out var at);
                    uint c = GetPixel(hdc, at.X, at.Y);
                    if (c == 0xFFFFFFFF) return null;
                    return new Widgets.Rgba(
                        (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
                }
                EyedropperCursor.Apply(true);
                Thread.Sleep(10);
            }
        }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
    }
}

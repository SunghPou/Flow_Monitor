using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// The eyedropper pointer, drawn at runtime instead of shipped as a resource.
/// Blender swaps in a dedicated eyedropper cursor while the picker is up
/// (source/blender/windowmanager/wm_cursors.hh, WM_CURSOR_EYEDROPPER); Windows has
/// no stock equivalent, so the mask is built here: a 45 degree pipette whose hot
/// spot is the tip, exactly like the glyph the picker paints in its dropper field.
/// </summary>
public static class EyedropperCursor
{
    const int Size = 32;

    static readonly Lazy<IntPtr> LazyHandle = new(() => Build());
    public static IntPtr Handle => LazyHandle.Value;

    /// <summary>Shows the pipette over the dropper field, or the system arrow elsewhere.</summary>
    public static void Apply(bool over)
    {
        if (!over) { Native.SetCursor(IntPtr.Zero); return; }
        var h = Handle;
        Native.SetCursor(h == IntPtr.Zero ? LoadArrow() : h);
    }

    static IntPtr LoadArrow()
        => Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW);

    // ------------------------------------------------------------------ shape

    // Shared with the painted glyph in ColorPickerWindow: bulb, collar, shaft,
    // barrel, taper, all along the 45 degree axis (along, across) in pixels.
    static bool Pipette(double along, double across)
    {
        if (Math.Abs(across) > 4.0) return false;
        if ((along + 4.6) * (along + 4.6) + across * across <= 3.1 * 3.1) return true;   // bulb
        if ((along + 0.9) * (along + 0.9) + across * across <= 1.7 * 1.7) return true;   // collar
        if (along >= -2.4 && along <= 4.2 && Math.Abs(across) <= 1.5) return true;      // shaft
        if (along > 4.2 && along <= 5.6)                                                // taper
            return Math.Abs(across) <= 1.5 * (5.6 - along) / 1.4;
        return false;
    }

    // The tip is the hot spot, so the pixel the user is sampling sits under the point.
    const double Centre = 15.5;
    const double TipAlong = 5.6;

    static bool ShapeAt(int x, int y)
    {
        double dx = x - Centre, dy = y - Centre;
        double k = Math.Sqrt(0.5);
        return Pipette((dx + dy) * k, (dx - dy) * k);
    }

    static int HotX => (int)Math.Round(Centre + TipAlong * Math.Sqrt(0.5));
    static int HotY => HotX;

    // ------------------------------------------------------------------ build

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize, biWidth, biHeight, biPlanes, biBitCount, biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    const uint BI_RGB = 0;
    const uint DIB_RGB_COLORS = 0;

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreateCursorW(IntPtr instance, IntPtr andPlane, IntPtr xorPlane,
        int hotX, int hotY);

    static IntPtr Build()
    {
        // One 1bpp DIB holding both masks: the XOR plane on top, the AND plane below.
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = Size,
                biHeight = Size * 2,
                biPlanes = 1,
                biBitCount = 1,
                biCompression = BI_RGB,
            },
        };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return IntPtr.Zero;
        IntPtr bmp = IntPtr.Zero, bits = IntPtr.Zero, cursor = IntPtr.Zero;
        try
        {
            bmp = CreateDIBSection(dc, ref info, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (bmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
            var old = SelectObject(dc, bmp);
            try { Fill(bits); }
            finally { SelectObject(dc, old); }

            cursor = CreateCursorW(Native.GetModuleHandle(null), bmp, bmp, HotX, HotY);
            if (cursor == IntPtr.Zero) return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Log.Warn("eyedropper cursor: " + ex.Message);
            cursor = IntPtr.Zero;
        }
        finally
        {
            if (cursor == IntPtr.Zero && bmp != IntPtr.Zero) DeleteObject(bmp);
            else if (bmp != IntPtr.Zero) DeleteObject(bmp);   // the cursor copies the masks
            DeleteDC(dc);
        }
        return cursor;
    }

    /// <summary>
    /// Monochrome cursor planes: the XOR bit is the ink (white), the AND bit marks
    /// the pixels kept (0). Everything else is transparent black.
    /// </summary>
    static unsafe void Fill(IntPtr bits)
    {
        int stride = (Size + 31) / 32 * 4;
        for (int y = 0; y < Size; y++)
        {
            uint xorWord = 0, andWord = 0;
            for (int x = 0; x < Size; x++)
            {
                bool on = ShapeAt(x, y);
                if (!on) continue;
                xorWord |= 1u << (31 - (x & 31));
                andWord &= ~(1u << (31 - (x & 31)));
            }
            for (int i = 0; i < stride; i++) ((byte*)bits)[(y * stride) + i] = 0;
            byte* row = (byte*)bits + y * stride;
            row[0] = (byte)(xorWord >> 24);
            row[1] = (byte)(xorWord >> 16);
            row[2] = (byte)(xorWord >> 8);
            row[3] = (byte)xorWord;
            byte* arow = (byte*)bits + (y + Size) * stride;
            arow[0] = (byte)(andWord >> 24);
            arow[1] = (byte)(andWord >> 16);
            arow[2] = (byte)(andWord >> 8);
            arow[3] = (byte)andWord;
        }
    }
}

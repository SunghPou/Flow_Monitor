using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// The eyedropper pointer, drawn at runtime instead of shipped as a resource.
/// Blender swaps in a dedicated eyedropper cursor while sampling (wm_cursors.hh,
/// WM_CURSOR_EYEDROPPER) and Windows has no stock equivalent, so the mask is built
/// here from <see cref="BlenderDropper"/>: Blender's own eyedropper icon, with the
/// hot spot on the pipette tip so the sampled pixel sits under the point.
/// </summary>
public static class EyedropperCursor
{
    const int Size = BlenderDropper.Size;

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

    // Blender's icon raster, so the pointer and the painted field icon match.
    static bool ShapeAt(int x, int y) => BlenderDropper.Lit(x, y);

    static int HotX => BlenderDropper.HotX;
    static int HotY => BlenderDropper.HotY;

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
    static extern IntPtr CreateCursor(IntPtr instance, int hotX, int hotY,
        int width, int height, IntPtr andPlane, IntPtr xorPlane);

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
            Fill(bits);
            // The DIB holds the XOR plane first and the AND plane below it; the cursor
            // copies both bit planes, so the section is deleted right after.
            int stride = (Size + 31) / 32 * 4;
            IntPtr xorBits = bits, andBits = IntPtr.Add(bits, Size * stride);
            cursor = CreateCursor(Native.GetModuleHandle(null), HotX, HotY,
                Size, Size, andBits, xorBits);
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
            // Transparent everywhere (AND keeps the screen, XOR draws nothing);
            // ink pixels become white (AND clears, XOR sets).
            uint xorWord = 0, andWord = 0xFFFFFFFFu;
            for (int x = 0; x < Size; x++)
            {
                if (!ShapeAt(x, y)) continue;
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

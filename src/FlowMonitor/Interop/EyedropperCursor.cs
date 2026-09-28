using System.Drawing;
using System.Runtime.InteropServices;

namespace FlowMonitor.Interop;

/// <summary>
/// The eyedropper pointer as a 32bpp alpha cursor, rendered with GDI+ anti-aliasing
/// from the same Blender eyedropper walk as the card icon. A 1bpp monochrome cursor
/// can only draw jagged white pixels; the colour cursor carries smooth edges with a
/// black outline, so it stays crisp over any desktop background. The OS cursor API
/// takes only bitmaps, so this is the one place the vector art is baked to pixels.
/// </summary>
public static class EyedropperCursor
{
    const int Size = 32;

    static readonly Lazy<IntPtr> LazyHandle = new(() => Build());
    public static IntPtr Handle => LazyHandle.Value;

    /// <summary>Hot spot on the pipette tip, scanned off the rendered ink.</summary>
    public static int HotX { get; private set; } = 11;
    public static int HotY { get; private set; } = 22;

    /// <summary>Opaque/white/dark pixel counts of the last render, for the self-test.</summary>
    internal static int OpaquePixels { get; private set; }
    internal static int WhitePixels { get; private set; }
    internal static int DarkPixels { get; private set; }

    /// <summary>Shows the pipette over the dropper field, or the system arrow elsewhere.</summary>
    public static void Apply(bool over)
    {
        // SetCursor(NULL) does NOT restore the arrow: it removes the cursor from the
        // screen, so every non-dropper WM_SETCURSOR hid the pointer over the card.
        var h = over ? Handle : IntPtr.Zero;
        Native.SetCursor(h != IntPtr.Zero ? h : LoadArrow());
    }

    static IntPtr LoadArrow()
        => Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW);

    // ------------------------------------------------------------------ build

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        // biPlanes and biBitCount are WORDs: as uints the header is 44 bytes
        // instead of 40 and CreateDIBSection refuses it.
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    const uint BI_RGB = 0;
    const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyCursor(IntPtr cursor);

    static IntPtr Build()
    {
        byte[]? px = Render(out int stride);
        if (px is null) return IntPtr.Zero;
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) { Log.Warn("eyedropper cursor: no compatible dc"); return IntPtr.Zero; }
        IntPtr colorBmp = IntPtr.Zero, maskBmp = IntPtr.Zero;
        IntPtr colorBits = IntPtr.Zero, maskBits = IntPtr.Zero;
        IntPtr cursor = IntPtr.Zero;
        try
        {
            var colorInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = Size,
                    biHeight = -Size,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };
            colorBmp = CreateDIBSection(dc, ref colorInfo, DIB_RGB_COLORS, out colorBits, IntPtr.Zero, 0);
            var maskInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = Size,
                    biHeight = -Size,
                    biPlanes = 1,
                    biBitCount = 1,
                    biCompression = BI_RGB,
                },
            };
            maskBmp = CreateDIBSection(dc, ref maskInfo, DIB_RGB_COLORS, out maskBits, IntPtr.Zero, 0);
            if (colorBmp == IntPtr.Zero || maskBmp == IntPtr.Zero)
            {
                Log.Warn("eyedropper cursor: no DIB section " + Marshal.GetLastWin32Error());
                return IntPtr.Zero;
            }
            FillPlanes(px, stride, colorBits, maskBits);
            var info = new ICONINFO
            {
                fIcon = false,
                xHotspot = HotX,
                yHotspot = HotY,
                hbmMask = maskBmp,
                hbmColor = colorBmp,
            };
            cursor = CreateIconIndirect(ref info);
            if (cursor == IntPtr.Zero)
                Log.Warn("eyedropper cursor: CreateIconIndirect failed " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex)
        {
            Log.Warn("eyedropper cursor: " + ex.Message);
            cursor = IntPtr.Zero;
        }
        finally
        {
            // The cursor keeps referencing both planes: they stay mapped until exit.
            if (cursor == IntPtr.Zero)
            {
                if (colorBmp != IntPtr.Zero) DeleteObject(colorBmp);
                if (maskBmp != IntPtr.Zero) DeleteObject(maskBmp);
            }
            else { _keptColor = colorBmp; _keptMask = maskBmp; }
            DeleteDC(dc);
        }
        return cursor;
    }

    /// <summary>Pins the planes behind the live cursor; never freed.</summary>
    static IntPtr _keptColor, _keptMask;

    /// <summary>
    /// Colour plane copied verbatim; mask bit is 1 where the pixel is transparent.
    /// The hot spot is the bottom-leftmost opaque pixel, which is the pipette tip.
    /// </summary>
    static unsafe void FillPlanes(byte[] px, int stride, IntPtr colorBits, IntPtr maskBits)
    {
        int maskStride = (Size + 31) / 32 * 4;
        for (int i = 0; i < Size * maskStride; i++) ((byte*)maskBits)[i] = 0xFF;
        Marshal.Copy(px, 0, colorBits, px.Length);
        int opaque = 0, white = 0, dark = 0, best = int.MaxValue, hx = 11, hy = 22;
        int lo = 255, hi = 0;
        fixed (byte* p = px)
        {
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    byte* c = p + y * stride + x * 4;
                    if (c[3] < 128)
                    {
                        // Transparent: mask keeps the screen, colour plane ignored.
                        continue;
                    }
                    ((byte*)maskBits)[y * maskStride + (x >> 3)] &= (byte)~(0x80 >> (x & 7));
                    opaque++;
                    lo = Math.Min(lo, Math.Min(c[0], Math.Min(c[1], c[2])));
                    hi = Math.Max(hi, Math.Max(c[0], Math.Max(c[1], c[2])));
                    if (c[0] > 200 && c[1] > 200 && c[2] > 200) white++;
                    else if (c[0] < 128 && c[1] < 128 && c[2] < 128) dark++;
                    int tip = x - y;
                    if (tip < best) { best = tip; hx = x; hy = y; }
                }
        }
        OpaquePixels = opaque;
        WhitePixels = white;
        DarkPixels = dark;
        Darkest = lo;
        Brightest = hi;
        HotX = hx;
        HotY = hy;
    }

    // ------------------------------------------------------------------ vector art

    // The 32px canvas holds the art at half size, centred, so the on-screen ink
    // matches the card icon (26px box * 0.56 fill ~= 14.6px).
    const float ArtC = 16f, ArtSide = 16f;

    // Same box the card uses: the art's own bounding box spans X 195.146..208.990 and
    // Y 598.5..613.05, so keeping the y scale on the x scale (1:1) makes the pipette
    // as tall as it is wide instead of stretched. The card's GlyphGeometry.Dropper
    // stretches it the same way, which is why only the cursor needed this.
    static PointF P(float x, float y) => new(
        ArtC + ((x - 195.146f) / (208.990f - 195.146f) - 0.5f) * ArtSide,
        ArtC + ((y - 605.775f) / (208.990f - 195.146f) - 0.5f) * ArtSide);

    /// <summary>
    /// The same Blender eyedropper walk the card paints (Widgets.GlyphGeometry.Dropper),
    /// as a GDI+ path so the cursor bakes anti-aliased pixels instead of a 1-bit mask.
    /// </summary>
    static System.Drawing.Drawing2D.GraphicsPath BuildPath()
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        PointF cur = P(206.727f, 599.000f);
        void Line(float x, float y) { var p = P(x, y); path.AddLine(cur, p); cur = p; }
        void Bez(float ax, float ay, float bx, float by, float x, float y)
        {
            var p = P(x, y);
            path.AddBezier(cur, P(ax, ay), P(bx, by), p);
            cur = p;
        }
        Bez(206.533f, 598.996f, 206.340f, 599.013f, 206.152f, 599.051f);
        Bez(205.402f, 599.201f, 204.705f, 599.629f, 204.146f, 600.188f);
        Line(202.002f, 602.295f);
        Line(200.854f, 601.146f);
        Line(200.700f, 601.018f);
        Line(200.562f, 601.088f);
        Line(200.262f, 601.008f);
        Line(200.075f, 600.999f);
        Line(199.899f, 601.060f);
        Line(199.757f, 601.180f);
        Line(199.669f, 601.345f);
        Line(199.648f, 601.530f);
        Line(199.695f, 601.710f);
        Line(199.806f, 601.860f);
        Line(199.963f, 601.960f);
        Line(200.147f, 601.994f);
        Line(201.793f, 603.500f);
        Line(197.145f, 608.189f);
        Line(196.918f, 608.123f);
        Line(196.745f, 608.051f);
        Line(196.557f, 608.049f);
        Line(196.381f, 608.116f);
        Line(196.243f, 608.243f);
        Line(196.161f, 608.412f);
        Line(196.148f, 608.600f);
        Line(196.205f, 608.779f);
        Line(196.323f, 608.924f);
        Line(196.488f, 609.016f);
        Line(196.674f, 609.040f);
        Line(196.856f, 608.994f);
        Line(197.008f, 608.884f);
        Line(197.109f, 608.726f);
        Line(197.145f, 608.541f);
        Line(197.000f, 609.334f);
        Line(195.146f, 611.187f);
        Line(195.054f, 611.337f);
        Line(195.009f, 611.505f);
        Line(195.024f, 611.678f);
        Line(195.097f, 611.835f);
        Line(195.220f, 611.958f);
        Line(195.378f, 612.031f);
        Line(195.551f, 612.046f);
        Line(195.718f, 612.001f);
        Line(195.861f, 611.902f);
        Line(197.854f, 609.895f);
        Line(198.081f, 609.960f);
        Line(198.254f, 610.031f);
        Line(198.442f, 610.033f);
        Line(198.618f, 609.966f);
        Line(198.756f, 609.838f);
        Line(198.837f, 609.669f);
        Line(198.850f, 609.482f);
        Line(198.793f, 609.303f);
        Line(198.674f, 609.157f);
        Line(198.510f, 609.066f);
        Line(198.324f, 609.042f);
        Line(198.142f, 609.088f);
        Line(197.990f, 609.198f);
        Line(197.889f, 609.357f);
        Line(197.854f, 609.541f);
        Line(198.000f, 608.746f);
        Line(202.502f, 604.209f);
        Line(203.795f, 605.502f);
        Line(199.291f, 610.041f);
        Line(198.500f, 610.041f);
        Line(198.298f, 610.065f);
        Line(198.147f, 610.041f);
        Line(196.146f, 612.187f);
        Line(196.054f, 612.337f);
        Line(196.009f, 612.505f);
        Line(196.024f, 612.678f);
        Line(196.097f, 612.835f);
        Line(196.220f, 612.958f);
        Line(196.378f, 613.031f);
        Line(196.551f, 613.046f);
        Line(196.718f, 613.001f);
        Line(196.861f, 612.902f);
        Line(198.707f, 611.041f);
        Line(199.500f, 611.041f);
        Line(199.703f, 611.017f);
        Line(199.855f, 611.041f);
        Line(204.502f, 606.209f);
        Line(206.146f, 607.854f);
        Line(206.302f, 607.969f);
        Line(206.493f, 608.007f);
        Line(206.684f, 607.969f);
        Line(206.847f, 607.861f);
        Line(206.955f, 607.698f);
        Line(206.993f, 607.507f);
        Line(206.955f, 607.316f);
        Line(206.847f, 607.153f);
        Line(205.711f, 606.004f);
        Line(207.850f, 603.898f);
        Line(207.854f, 603.894f);
        Bez(208.413f, 603.336f, 208.840f, 602.639f, 208.990f, 601.889f);
        Bez(209.140f, 601.138f, 208.971f, 600.305f, 208.354f, 599.687f);
        Bez(207.890f, 599.224f, 207.306f, 599.013f, 206.727f, 599.000f);
        path.CloseFigure();
        return path;
    }

    /// <summary>White fill with a black outline, anti-aliased, on transparency.</summary>
    static byte[]? Render(out int stride)
    {
        stride = Size * 4;
        try
        {
            using var bmp = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var path = BuildPath();
                // Halo behind the fill, not a centered stroke: the white interior
                // stays pixel-identical to the button icon, and only a dark rim
                // outside the silhouette keeps it readable on light backgrounds.
                using var halo = new Pen(Color.Black, 2f)
                {
                    LineJoin = System.Drawing.Drawing2D.LineJoin.Round,
                };
                g.DrawPath(halo, path);
                g.FillPath(Brushes.White, path);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, Size, Size),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var px = new byte[Size * data.Stride];
                Marshal.Copy(data.Scan0, px, 0, px.Length);
                stride = data.Stride;
                return px;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("eyedropper cursor render: " + ex.Message);
            return null;
        }
    }

    /// <summary>Darkest reachable pixel of the pipette, for the self-test.</summary>
    internal static int Darkest { get; private set; } = 255;
    /// <summary>Brightest reachable pixel of the pipette, for the self-test.</summary>
    internal static int Brightest { get; private set; }

    /// <summary>8x preview on a checkerboard, so --capture shows the art as seen.</summary>
    internal static void SavePreview(string path)
    {
        byte[]? px = Render(out int stride);
        if (px is null) return;
        using var src = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bits = src.LockBits(new Rectangle(0, 0, Size, Size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try { Marshal.Copy(px, 0, bits.Scan0, px.Length); }
        finally { src.UnlockBits(bits); }
        using var bmp = new Bitmap(Size * 8, Size * 8, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    using (var b = new SolidBrush((x + y) % 2 == 0 ? Color.LightGray : Color.White))
                        g.FillRectangle(b, x * 32, y * 32, 32, 32);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(src, 0, 0, Size * 8, Size * 8);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}

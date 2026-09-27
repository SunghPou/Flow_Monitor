using System;
using System.Drawing;
using System.Runtime.InteropServices;
using FlowMonitor.Interop;
using FlowMonitor.Rendering;
using FlowMonitor.Widgets;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;
using Picker = FlowMonitor.Widgets.ColorPickerLayout;
using V2 = System.Numerics.Vector2;
using SizeI = Vortice.Mathematics.SizeI;

namespace FlowMonitor.Host;

/// <summary>
/// Modal colour picker: hue ring, SV disc, alpha track and read-only hex/R/G/B
/// fields on a card that follows the system theme. Top-level (WS_EX_TOOLWINDOW)
/// and reuses WidgetSurface from the shared device.
/// THREADING: Show runs on the UI thread with its own modal loop; blocking, and
/// owns the GPU via SetMenuOpen while up.
/// </summary>
internal static class ColorPickerWindow
{
    internal const string ClassName = "FlowMonitor.ColorPicker.Wnd";

    internal delegate IntPtr PickerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Ring and disc have no conic/radial gradient in D2D, so they are CPU bitmaps.
    static ID2D1Bitmap1? _ringBmp;
    static ID2D1Bitmap1? _discBmp;
    static int _discHue = -1;
    static ID2D1BitmapBrush? _checker;
    static Hsv _hsv;
    static double _alpha = 1.0;
    static PickerPart _drag = PickerPart.None;
    static bool _cancelled;
    static bool _up;

    static WidgetSurface? _surface;
    static IRenderHost? _host;
    static ResourceCache? _res;
    static WidgetWindow? _owner;
    static IntPtr _hwnd;
    static float _scale = 1f;

    internal static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case Native.WM_NCCREATE:
                case Native.WM_CREATE:
                    return new IntPtr(1);

                case Native.WM_ERASEBKGND:
                    return new IntPtr(1);

                case Native.WM_PAINT:
                    Paint();
                    return new IntPtr(0);

                case Native.WM_MOUSEMOVE:
                    if (_drag != PickerPart.None) { Apply(lParam); Paint(); }
                    return new IntPtr(0);

                case Native.WM_LBUTTONDOWN:
                    Down(lParam);
                    return new IntPtr(0);

                case Native.WM_LBUTTONUP:
                    _drag = PickerPart.None;
                    return new IntPtr(0);

                case Native.WM_KEYDOWN:
                    if ((wParam.ToInt64() & 0xFFFF) == Native.VK_ESCAPE) { _cancelled = true; Close(); return new IntPtr(0); }
                    break;

                case Native.WM_CANCELMODE:
                    Close();
                    return new IntPtr(0);

                case Native.WM_ACTIVATE:
                    if ((short)(wParam.ToInt64() & 0xFFFF) == 0) Close();
                    return new IntPtr(0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("picker: exception in wndproc, closing: " + ex.Message);
            Close();
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    static void Close() => Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Shows the picker near (x, y) in screen coordinates seeded with
    /// <paramref name="startHex"/>. Returns the chosen #RRGGBB, or null if cancelled.
    /// </summary>
    internal static string? Show(IRenderHost host, WidgetWindow widget, int x, int y, string startHex)
    {
        SystemTheme.Refresh();
        _host = host;
        _owner = widget;
        _up = false;
        _cancelled = false;
        _drag = PickerPart.None;
        var start = Rgba.FromHex(startHex);
        _hsv = start.ToHsv();
        _alpha = start.A / 255.0;

        host.SetMenuOpen(true);
        try { Run(host, widget, x, y); }
        finally { host.SetMenuOpen(false); }

        if (_cancelled || !_up) return null;
        return Current().ToHex();
    }

    static Rgba Current()
    {
        var (r, g, b) = _hsv.ToRgb();
        return new Rgba(r, g, b, (byte)Math.Clamp(Math.Round(_alpha * 255.0), 0.0, 255.0));
    }

    /// <summary>Fully opaque current hue/saturation/value, used to tint the alpha track.</summary>
    static Rgba Pure()
    {
        var (r, g, b) = _hsv.ToRgb();
        return new Rgba(r, g, b);
    }

    static void Run(IRenderHost host, WidgetWindow widget, int x, int y)
    {
        RegisterClass();
        _scale = widget.Config.Dpi;
        int w = (int)MathF.Round(Picker.CardW * _scale);
        int h = (int)MathF.Round(Picker.CardH * _scale);
        var (sx, sy) = ClampToWorkArea(x, y, w, h);

        int styleBits = Native.WS_POPUP | Native.WS_CLIPSIBLINGS;
        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            ClassName, "Color Picker",
            unchecked((uint)styleBits),
            sx, sy, w, h,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Warn($"picker: CreateWindowExW failed win32={Marshal.GetLastWin32Error()}");
            return;
        }

        // No DWM transient backdrop: the card paints its own themed background, and the
        // system backdrop is drawn with its own corner shape, which boxed the popup's
        // rounded corners against a dark page.
        _surface = new WidgetSurface(host.Device, _hwnd, w, h);
        Native.ShowWindow(_hwnd, 5);
        Native.SetCapture(_hwnd);
        Native.SetForegroundWindow(_hwnd);
        Paint();

        Native.MSG msg;
        while (true)
        {
            int r = Native.GetMessageW(out msg, IntPtr.Zero, 0, 0);
            if (r <= 0) break;
            if (msg.message == Native.WM_CLOSE) { Native.PostQuitMessage(0); continue; }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
        Teardown();
    }

    static void Teardown()
    {
        _up = true;
        try { if (_hwnd != IntPtr.Zero) Native.ReleaseCapture(); } catch { }
        try { _surface?.Dispose(); } catch (Exception ex) { Log.Warn("picker: surface dispose: " + ex.Message); }
        _surface = null;
        try { if (Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd); } catch { }
        _hwnd = IntPtr.Zero;
        _host = null;
        _owner = null;
    }

    static (int x, int y) ClampToWorkArea(int cx, int cy, int w, int h)
    {
        int left, top, right, bottom;
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        var mon = Native.MonitorFromPoint(new Native.POINT { X = cx, Y = cy }, 2);
        if (mon != IntPtr.Zero && Native.GetMonitorInfoW(mon, ref mi))
        {
            left = mi.rcWork.Left; top = mi.rcWork.Top;
            right = mi.rcWork.Right; bottom = mi.rcWork.Bottom;
        }
        else
        {
            var vs = Desktop.VirtualScreen();
            left = vs.Left; top = vs.Top; right = vs.Right; bottom = vs.Bottom;
        }
        int x = cx, y = cy;
        if (x + w > right) x = Math.Max(left, cx - w);
        if (y + h > bottom) y = Math.Max(top, cy - h);
        return (x, y);
    }

    // ------------------------------------------------------------------ input

    static (float X, float Y) ToLogical(IntPtr lParam)
    {
        int px = (short)(lParam.ToInt64() & 0xFFFF);
        int py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        return (px / _scale, py / _scale);
    }

    static void Down(IntPtr lParam)
    {
        var (x, y) = ToLogical(lParam);
        var part = Picker.HitTest(x, y);
        if (part == PickerPart.None) { Close(); return; }
        if (part == PickerPart.Eyedropper) { PickScreen(); return; }
        _drag = part;
        Apply(lParam);
        Paint();
    }

    static void Apply(IntPtr lParam)
    {
        var (x, y) = ToLogical(lParam);
        if (_drag == PickerPart.Ring) _hsv = new Hsv(Picker.HueFromPoint(x, y), _hsv.S, _hsv.V);
        else if (_drag == PickerPart.Disc)
        {
            var (s, v) = Picker.SvFromPoint(x, y);
            _hsv = new Hsv(_hsv.H, s, v);
        }
        else if (_drag == PickerPart.Alpha) _alpha = Picker.AlphaFromX(x);
        _hsv = _hsv.Normalized();
    }

    /// <summary>Hides the card, samples the pixel the user clicks, brings the card back.</summary>
    static void PickScreen()
    {
        Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        Native.GetCursorPos(out var at);
        Thread.Sleep(60);
        var picked = Interop.ScreenPick.PickUnderCursor(at);
        if (picked is Rgba rgb)
        {
            _hsv = rgb.ToHsv();
            _alpha = 1.0;
        }
        Native.ShowWindow(_hwnd, 5 /* SW_SHOW */);
        Native.SetForegroundWindow(_hwnd);
        Native.SetCapture(_hwnd);
        Paint();
    }

    // ------------------------------------------------------------------ paint

    /// <summary>
    /// Offscreen render for the selftest sheet: the live path is <see cref="Paint"/>,
    /// which reads the same statics this sets.
    /// </summary>
    internal static void PaintTo(WidgetSurface surface, ResourceCache res, float scale, Hsv hsv, double alpha)
    {
        _surface = surface;
        _res = res;
        _scale = scale;
        _hsv = hsv;
        _alpha = alpha;
        Paint();
    }

    static void Paint()
    {
        var surface = _surface;
        var res = _res ?? _host?.Resources;
        if (surface == null || res == null) return;
        float s = _scale;

        surface.BeginDraw(s * 96f);
        var dc = surface.Context;
        dc.Clear(new Color4(0f, 0f, 0f, 0f));
        var cardBrush = res.Brush(SystemTheme.Card);
        var card = new RoundedRectangle(S(Picker.Card, s), Picker.Radius * s, Picker.Radius * s);
        dc.FillRoundedRectangle(card, cardBrush);
        dc.DrawRoundedRectangle(card, res.Brush(SystemTheme.Hairline), 1f * s);

        dc.DrawText("Color Picker",
            res.Format("Segoe UI Variable Display", 28f, Vortice.DirectWrite.FontWeight.Normal),
            VR(S(Picker.Title, s)), res.Brush(SystemTheme.Ink));

        EnsureBitmaps(dc);

        // The disc is smaller than the ring hole, so a white gap separates them.
        if (_discBmp != null)
            dc.DrawBitmap(_discBmp, (RectangleF?)Square(Picker.Center, Picker.DiscR, s), 1f,
                BitmapInterpolationMode.Linear, (RectangleF?)null);
        if (_ringBmp != null)
            dc.DrawBitmap(_ringBmp, (RectangleF?)Square(Picker.Center, Picker.RingOuter, s), 1f,
                BitmapInterpolationMode.Linear, (RectangleF?)null);

        var pure = Pure();
        DrawMarkers(dc, res, s, pure);
        DrawAlpha(dc, res, s, pure);
        DrawFields(dc, res, s);
        DrawEyedropper(dc, res, s);

        surface.EndDrawAndPresent();
    }

    static void DrawMarkers(ID2D1DeviceContext dc, ResourceCache res, float s, Rgba pure)
    {
        var hue = Picker.PointFromHue((float)_hsv.H);
        var sv = Picker.PointFromSv(_hsv.S, _hsv.V);
        var white = res.Brush(new Color4(1f, 1f, 1f, 1f));

        // Hue marker: a filled disc of the pure colour, ringed in white, riding the band.
        float rr = Picker.RingMarkerR * s;
        dc.FillEllipse(new Ellipse(new V2(hue.X * s, hue.Y * s), rr, rr), res.Brush(pure.ToColor4()));
        Ring(dc, (hue.X - Picker.RingMarkerR) * s, (hue.Y - Picker.RingMarkerR) * s,
            rr * 2f, white, 2.2f * s);
        // SV marker: a small hollow white ring.
        Ring(dc, (sv.X - Picker.MarkerR) * s, (sv.Y - Picker.MarkerR) * s,
            Picker.MarkerR * 2 * s, white, Picker.MarkerStroke * s);
    }

    static void Ring(ID2D1DeviceContext dc, float x, float y, float d, ID2D1Brush brush, float width)
        => dc.DrawEllipse(new Ellipse(new V2(x + d / 2f, y + d / 2f), d / 2f, d / 2f), brush, width);

    static void DrawAlpha(ID2D1DeviceContext dc, ResourceCache res, float s, Rgba pure)
    {
        var track = S(Picker.AlphaTrack, s);

        if (_checker != null) dc.FillRectangle(track, _checker);
        else dc.FillRectangle(track, res.Brush(new Color4(1f, 1f, 1f, 0.2f)));

        // Transparent on the left, the colour on the right: the checkerboard reads through.
        var props = new LinearGradientBrushProperties(
            new V2(track.Left, 0f), new V2(track.Right, 0f));
        using var stops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0f, new Color4(pure.R / 255f, pure.G / 255f, pure.B / 255f, 0f)),
            new GradientStop(1f, new Color4(pure.R / 255f, pure.G / 255f, pure.B / 255f, 1f)),
        ], Gamma.Linear, ExtendMode.Clamp);
        using var grad = dc.CreateLinearGradientBrush(props, stops);
        dc.FillRectangle(track, grad);

        float hx = Picker.XFromAlpha(_alpha) * s;
        float hy = track.Top + track.Height / 2f;
        var white = res.Brush(new Color4(1f, 1f, 1f, 1f));
        Ring(dc, hx - Picker.MarkerR * s, hy - Picker.MarkerR * s, Picker.MarkerR * 2 * s, white, Picker.MarkerStroke * s);
    }

    static void DrawFields(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var cur = Current();
        string[] values = [cur.ToHex(), cur.R.ToString(), cur.G.ToString(), cur.B.ToString()];
        var valueFmt = res.Format("Segoe UI Variable Text", 12f, Vortice.DirectWrite.FontWeight.Normal);
        var labelFmt = res.Format("Segoe UI Variable Text", 15f, Vortice.DirectWrite.FontWeight.Normal);
        for (int i = 0; i < 4; i++)
        {
            var pill = S(Picker.Pill(i), s);
            dc.FillRoundedRectangle(
                new RoundedRectangle(pill, 6f * s, 6f * s), res.Brush(SystemTheme.Pill));
            dc.DrawText(values[i], valueFmt, new Rect(pill.X, pill.Y, pill.Width, pill.Height),
                res.Brush(SystemTheme.Ink));
            var label = S(Picker.PillLabel(i), s);
            dc.DrawText(Picker.PillLabels[i], labelFmt,
                new Rect(label.X, label.Y, label.Width, label.Height),
                res.Brush(SystemTheme.MutedInk));
        }

        var div = S(new RectangleF(0f, Picker.DividerY, Picker.CardW, 1f), s);
        dc.FillRectangle(div, res.Brush(SystemTheme.Hairline));
    }

    static void DrawEyedropper(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var box = S(Picker.Eyedropper, s);
        dc.FillRoundedRectangle(new RoundedRectangle(box, 6f * s, 6f * s), res.Brush(SystemTheme.Pill));
        var ink = res.Brush(SystemTheme.Ink);
        float cx = box.X + box.Width / 2f, cy = box.Y + box.Height / 2f;
        float r = 5f * s;
        dc.DrawEllipse(new Ellipse(new V2(cx, cy), r, r), ink, 1.4f * s);
        dc.DrawLine(new V2(cx, cy - r - 2.5f * s), new V2(cx, cy - r), ink, 1.4f * s);
        dc.DrawLine(new V2(cx, cy + r), new V2(cx, cy + r + 2.5f * s), ink, 1.4f * s);
        dc.DrawLine(new V2(cx - r - 2.5f * s, cy), new V2(cx - r, cy), ink, 1.4f * s);
        dc.DrawLine(new V2(cx + r, cy), new V2(cx + r + 2.5f * s, cy), ink, 1.4f * s);
    }

    static RectangleF S(RectangleF r, float s) => new(r.X * s, r.Y * s, r.Width * s, r.Height * s);

    /// DrawText wants the Vortice rect; layout keeps RectangleF.
    static Rect VR(RectangleF r) => new(r);

    static RectangleF Square(PointF center, float half, float s)
    {
        float d = half * 2f * s;
        return new RectangleF((center.X - half) * s, (center.Y - half) * s, d, d);
    }

    // ------------------------------------------------------------------ bitmaps

    // Bitmaps are generated at device pixels and drawn 1:1 with DrawBitmap: a bitmap
    // brush re-maps the source through DPI and produced smeared bands here.
    static void EnsureBitmaps(ID2D1DeviceContext dc)
    {
        int ringPx = (int)MathF.Ceiling(Picker.RingOuter * 2f * _scale);
        if (_ringBmp == null || _ringPx != ringPx)
        {
            _ringBmp?.Dispose();
            _ringBmp = MakeRing(dc, ringPx);
            _ringPx = ringPx;
        }

        // The disc bakes the hue in, so it is rebuilt whenever the hue leaves its bucket.
        int discPx = (int)MathF.Ceiling(Picker.DiscR * 2f * _scale);
        int hueBucket = (int)Math.Round(_hsv.H / 2.0);
        if (_discBmp == null || _discHue != hueBucket || _discPx != discPx)
        {
            _discBmp?.Dispose();
            _discBmp = MakeDisc(dc, discPx);
            _discHue = hueBucket;
            _discPx = discPx;
        }

        if (_checker == null)
        {
            _checker = dc.CreateBitmapBrush(MakeChecker(dc), new BitmapBrushProperties
            {
                ExtendModeX = ExtendMode.Wrap,
                ExtendModeY = ExtendMode.Wrap,
                InterpolationMode = BitmapInterpolationMode.NearestNeighbor,
            });
        }
    }
    static int _ringPx, _discPx;

    static ID2D1Bitmap1? MakeRing(ID2D1DeviceContext dc, int n)
    {
        float outer = Picker.RingOuter * _scale, inner = Picker.RingInner * _scale;
        var buf = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                if (r < inner || r > outer) continue;
                float deg = MathF.Atan2(dy, dx) * 180f / MathF.PI;
                // Same mapping as ColorPickerLayout.HueFromPoint: red at 3 o'clock.
                float hue = ((-deg) % 360f + 360f) % 360f;
                var (cr, cg, cb) = new Hsv(hue, 1, 1).ToRgb();
                Put(buf, (y * n + x) * 4, cr, cg, cb);
            }
        return Upload(dc, n, n, buf);
    }

    static ID2D1Bitmap1? MakeDisc(ID2D1DeviceContext dc, int n)
    {
        float rMax = n / 2f;
        var buf = new byte[n * n * 4];
        var (pr, pg, pb) = new Hsv(_hsv.H, 1, 1).ToRgb();
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - rMax, dy = y + 0.5f - rMax;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                if (r > rMax) continue;
                double s = Math.Clamp(r / rMax, 0.0, 1.0);
                // Value falls from the top down, matching PointFromSv (v = 1 at the top).
                double v = Math.Clamp(1.0 - (y + 0.5) / n, 0.0, 1.0);
                // White centre, hue at the rim, black at the bottom: (hue*(1-s) + white*s) * v.
                byte cr = (byte)Math.Round((pr * (1 - s) + 255 * s) * v);
                byte cg = (byte)Math.Round((pg * (1 - s) + 255 * s) * v);
                byte cb = (byte)Math.Round((pb * (1 - s) + 255 * s) * v);
                Put(buf, (y * n + x) * 4, cr, cg, cb);
            }
        return Upload(dc, n, n, buf);
    }

    static ID2D1Bitmap1? MakeChecker(ID2D1DeviceContext dc)
    {
        const int cell = 8;
        int n = cell * 2;
        var buf = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool light = ((x / cell) + (y / cell)) % 2 == 0;
                byte v = light ? (byte)0xCC : (byte)0xFF;
                Put(buf, (y * n + x) * 4, v, v, v);
            }
        return Upload(dc, n, n, buf);
    }

    static void Put(byte[] buf, int at, byte r, byte g, byte b)
    {
        // B8G8R8A8_UNorm, premultiplied; these bitmaps are opaque wherever they are not clear.
        buf[at] = b; buf[at + 1] = g; buf[at + 2] = r; buf[at + 3] = 255;
    }

    // The 3-argument CreateBitmap carries no pixel format and fails with
    // WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT, so the format is stated explicitly.
    // Every pixel uploaded here is fully opaque, so premultiplied is exact.
    static ID2D1Bitmap1? Upload(ID2D1DeviceContext dc, int w, int h, byte[] pixels)
    {
        IntPtr mem = Marshal.AllocHGlobal(pixels.Length);
        try
        {
            Marshal.Copy(pixels, 0, mem, pixels.Length);
            var props = new BitmapProperties1
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(
                    Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                DpiX = 96f,
                DpiY = 96f,
                BitmapOptions = BitmapOptions.None,
            };
            return dc.CreateBitmap(new SizeI(w, h), mem, (uint)(w * 4), props);
        }
        catch (Exception ex)
        {
            Log.Warn("picker: bitmap upload failed: " + ex.Message);
            return null;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    static void RegisterClass()
    {
        if (_classRegistered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0x0008,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
            hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
        };
        if (Native.RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) { Log.Warn($"picker: RegisterClassExW failed win32={err}"); return; }
        }
        _classRegistered = true;
    }

    static bool _classRegistered;

    // Static so the WndProc pointer stays valid after Show returns.
    static readonly PickerWndProc WndProcDelegate = WndProc;
}

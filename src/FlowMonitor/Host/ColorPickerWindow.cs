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
/// Modal colour picker shaped after Blender's colour picker: one full-bleed wheel
/// where the angle is hue and the radius is saturation, a value slider under it, and
/// read-only hex/R/G/B fields on a card that follows the system theme. Top-level
/// (WS_EX_TOOLWINDOW) and reuses WidgetSurface from the shared device.
/// THREADING: Show runs on the UI thread with its own modal loop; blocking, and
/// owns the GPU via SetMenuOpen while up.
/// </summary>
internal static class ColorPickerWindow
{
    internal const string ClassName = "FlowMonitor.ColorPicker.Wnd";

    internal delegate IntPtr PickerWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // D2D has no polar gradient, so the wheel is a CPU bitmap.
    static ID2D1Bitmap1? _wheelBmp;
    static int _wheelPx;
    static int _wheelValue = -1;
    static Hsv _hsv;
    static PickerPart _drag = PickerPart.None;
    static bool _cancelled;
    static bool _up;

    /// <summary>Wheel notch size for the value component (Blender's colorpicker_wheel_cb).</summary>
    const double WheelStep = 0.05;

    static WidgetSurface? _surface;
    static IRenderHost? _host;
    /// <summary>Live-preview sink: called with the current colour on every change.</summary>
    static Action<string>? _onChanged;
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

                case Native.WM_MOUSEWHEEL:
                    Wheel(wParam);
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
    internal static string? Show(IRenderHost host, WidgetWindow widget, int x, int y, string startHex,
        Action<string>? onChanged = null)
    {
        SystemTheme.Refresh();
        _host = host;
        _owner = widget;
        _up = false;
        _cancelled = false;
        _drag = PickerPart.None;
        _onChanged = onChanged;
        _hsv = Rgba.FromHex(startHex).ToHsv();

        host.SetMenuOpen(true);
        try { Run(host, widget, x, y); }
        finally { host.SetMenuOpen(false); _onChanged = null; }

        if (_cancelled || !_up) return null;
        return Current().ToHex();
    }

    /// <summary>Live preview: the owner widget repaints with this colour, unsaved.</summary>
    static void NotifyChanged() => _onChanged?.Invoke(Current().ToHex());

    static Rgba Current()
    {
        var (r, g, b) = _hsv.ToRgb();
        return new Rgba(r, g, b);
    }

    /// <summary>Fully saturated current hue at the current value, used to tint the value track.</summary>
    static Rgba Pure()
    {
        var (r, g, b) = new Hsv(_hsv.H, 1.0, _hsv.V).ToRgb();
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
        if (_drag == PickerPart.Wheel)
        {
            var (h, s) = Picker.HsFromPoint(x, y);
            _hsv = new Hsv(h, s, _hsv.V);
        }
        else if (_drag == PickerPart.Value)
        {
            _hsv = new Hsv(_hsv.H, _hsv.S, Picker.ValueFromX(x));
        }
        _hsv = _hsv.Normalized();
        NotifyChanged();
    }

    /// <summary>
    /// Wheel over the card nudges the value component, and a wheel turn outside it
    /// commits and closes, both as Blender's colorpicker_wheel_cb does.
    /// </summary>
    static void Wheel(IntPtr wParam)
    {
        int delta = (short)(wParam.ToInt64() >> 16);
        int px = (short)(wParam.ToInt64() & 0xFFFF);
        int py = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

        if (px < 0 || py < 0 || px >= Picker.CardW * _scale || py >= Picker.CardH * _scale)
        {
            Close();
            return;
        }
        double step = delta >= 0 ? WheelStep : -WheelStep;
        _hsv = new Hsv(_hsv.H, _hsv.S, _hsv.V + step).Normalized();
        NotifyChanged();
        Paint();
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
            NotifyChanged();
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
    internal static void PaintTo(WidgetSurface surface, ResourceCache res, float scale, Hsv hsv)
    {
        _surface = surface;
        _res = res;
        _scale = scale;
        _hsv = hsv;
        Paint();
    }

    static void Paint()
    {
        var surface = _surface;
        var res = _res ?? _host?.Resources;
        if (surface == null || res == null) return;
        // The render thread draws the widgets on the same device; one at a time.
        var gpu = _host?.Device.GpuLock;
        if (gpu is null) { PaintBody(surface, res); return; }
        lock (gpu) PaintBody(surface, res);
    }

    static void PaintBody(WidgetSurface surface, ResourceCache res)
    {
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

        EnsureWheel(dc);
        if (_wheelBmp != null)
            dc.DrawBitmap(_wheelBmp, (RectangleF?)Square(Picker.Center, Picker.WheelR, s), 1f,
                BitmapInterpolationMode.Linear, (RectangleF?)null);

        DrawWheelMarker(dc, res, s);
        DrawValue(dc, res, s);
        DrawFields(dc, res, s);
        DrawEyedropper(dc, res, s);

        surface.EndDrawAndPresent();
    }

    static void DrawWheelMarker(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var at = Picker.PointFromHs(_hsv.H, _hsv.S);
        float d = Picker.MarkerR * 2f * s;
        float x = (at.X - Picker.MarkerR) * s, y = (at.Y - Picker.MarkerR) * s;
        dc.FillEllipse(new Ellipse(new V2(at.X * s, at.Y * s), Picker.MarkerR * s, Picker.MarkerR * s),
            res.Brush(Current().ToColor4()));
        // Dark outline under a white one, so the ring reads on any wheel colour.
        Ring(dc, x, y, d, res.Brush(new Color4(0f, 0f, 0f, 0.45f)), 3f * s);
        Ring(dc, x, y, d, res.Brush(new Color4(1f, 1f, 1f, 1f)), Picker.MarkerStroke * s);
    }

    static void Ring(ID2D1DeviceContext dc, float x, float y, float d, ID2D1Brush brush, float width)
        => dc.DrawEllipse(new Ellipse(new V2(x + d / 2f, y + d / 2f), d / 2f, d / 2f), brush, width);

    static void DrawValue(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var track = S(Picker.ValueTrack, s);
        var pure = Pure();

        // Black on the left, the hue at the current value on the right.
        var props = new LinearGradientBrushProperties(
            new V2(track.Left, 0f), new V2(track.Right, 0f));
        using var stops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0f, new Color4(0f, 0f, 0f, 1f)),
            new GradientStop(1f, pure.ToColor4()),
        ], Gamma.Linear, ExtendMode.Clamp);
        using var grad = dc.CreateLinearGradientBrush(props, stops);
        dc.FillRoundedRectangle(new RoundedRectangle(track, track.Height / 2f, track.Height / 2f), grad);

        float hx = Picker.XFromValue(_hsv.V) * s;
        float hy = track.Top + track.Height / 2f;
        float d = Picker.MarkerR * 2 * s;
        var white = res.Brush(new Color4(1f, 1f, 1f, 1f));
        Ring(dc, hx - Picker.MarkerR * s, hy - Picker.MarkerR * s, d, white, Picker.MarkerStroke * s);
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

    // ------------------------------------------------------------------ bitmap

    // The wheel is generated at device pixels and drawn 1:1 with DrawBitmap: a bitmap
    // brush re-maps the source through DPI and produced smeared bands here.
    static void EnsureWheel(ID2D1DeviceContext dc)
    {
        int n = (int)MathF.Ceiling(Picker.WheelR * 2f * _scale);
        // The wheel bakes value in, so it is rebuilt whenever value leaves its bucket.
        int valueBucket = (int)Math.Round(_hsv.V * 40.0);
        if (_wheelBmp == null || _wheelPx != n || _wheelValue != valueBucket)
        {
            _wheelBmp?.Dispose();
            _wheelBmp = MakeWheel(dc, n);
            _wheelPx = n;
            _wheelValue = valueBucket;
        }
    }

    static ID2D1Bitmap1? MakeWheel(ID2D1DeviceContext dc, int n)
    {
        float rMax = n / 2f;
        var buf = new byte[n * n * 4];
        double v = _hsv.V;
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - rMax, dy = y + 0.5f - rMax;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                if (r > rMax) continue;                       // clear outside the rim
                // Same mapping as ColorPickerLayout.HsFromPoint: hue 0 at 12 o'clock, clockwise.
                double hue = Math.Atan2(dx, -dy) / (2.0 * Math.PI) * 360.0;
                hue = (hue % 360.0 + 360.0) % 360.0;
                var (cr, cg, cb) = new Hsv(hue, Math.Clamp(r / rMax, 0.0, 1.0), v).ToRgb();
                Put(buf, (y * n + x) * 4, cr, cg, cb);
            }
        return Upload(dc, n, n, buf);
    }

    static void Put(byte[] buf, int at, byte r, byte g, byte b)
    {
        // B8G8R8A8_UNorm, premultiplied; the wheel is opaque wherever it is not clear.
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

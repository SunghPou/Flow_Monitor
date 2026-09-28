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
/// Modal colour picker laid out after Blender's colour picker: a hue/saturation wheel
/// with a thin vertical value bar beside it, one number-slider row per RGB channel,
/// then the hex field and the eyedropper. Card follows the system theme. Top-level
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
    static Hsv _hsv;
    static PickerPart _drag = PickerPart.None;
    static bool _cancelled;
    static bool _up;
    /// <summary>A desktop pick is in flight: the click belongs to the eyedropper.</summary>
    static bool _picking;

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
                    // WM_SETCURSOR carries screen coordinates, so the last client
                    // position is tracked here and read back for the cursor shape.
                    _mouse = ToLogical(lParam);
                    if (_drag != PickerPart.None) { Apply(lParam); Paint(); }
                    return new IntPtr(0);

                case Native.WM_LBUTTONDOWN:
                    Down(lParam);
                    return new IntPtr(0);

                case Native.WM_LBUTTONUP:
                    _drag = PickerPart.None;
                    return new IntPtr(0);

                case Native.WM_MOUSEWHEEL:
                    Wheel(wParam, lParam);
                    return new IntPtr(0);

                case Native.WM_SETCURSOR:
                    // Blender shows its eyedropper cursor over the dropper field, and
                    // everywhere on screen while a pick is in flight.
                    Interop.EyedropperCursor.Apply(
                        _picking || Picker.HitTest(_mouse.X, _mouse.Y) == PickerPart.Eyedropper);
                    return new IntPtr(1);

                case Native.WM_KEYDOWN:
                    if ((wParam.ToInt64() & 0xFFFF) == Native.VK_ESCAPE) { _cancelled = true; Close(); return new IntPtr(0); }
                    break;

                case Native.WM_CANCELMODE:
                    Close();
                    return new IntPtr(0);

                case Native.WM_ACTIVATE:
                    // A pick in flight owns the mouse: losing activation to the window
                    // under the cursor is the normal case, not a cancel.
                    if (_picking) return new IntPtr(0);
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

    static void Close()
    {
        Interop.EyedropperCursor.Apply(false);
        Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static (float X, float Y) _mouse;

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
        _picking = false;
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
        // The surface drives the shared device, so building it takes the same lock the
        // render thread holds while it draws the widgets: IDCompositionDevice is an
        // apartment object and touching it from two threads is an access violation
        // inside dcomp.dll that no managed try/catch can catch.
        lock (host.Device.GpuLock) { _surface = new WidgetSurface(host.Device, _hwnd, w, h); }
        Log.Info($"picker: open hwnd=0x{_hwnd.ToInt64():X} start={_hsv.ToRgb()}");
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
        // Released under the render thread's lock, same as it was taken in Run.
        try { lock (_host?.Device.GpuLock ?? new object()) { _surface?.Dispose(); } }
        catch (Exception ex) { Log.Warn("picker: surface dispose: " + ex.Message); }
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
        // While sampling the desktop the click belongs to the eyedropper, not the card.
        if (_picking) { FinishPick(); return; }

        var (x, y) = ToLogical(lParam);
        var part = Picker.HitTest(x, y);
        if (part == PickerPart.None) { Close(); return; }
        if (part == PickerPart.Eyedropper) { PickScreen(); return; }
        if (part == PickerPart.Hex) return;             // read-only field: no drag, no commit
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
            _hsv = new Hsv(_hsv.H, _hsv.S, Picker.ValueFromY(y));
        }
        else
        {
            int channel = (int)_drag - (int)PickerPart.ChannelR;
            if (channel is >= 0 and < 3)
            {
                var (r, g, b) = _hsv.ToRgb();
                byte v = Picker.ChannelFromX(channel, x);
                if (channel == 0) r = v; else if (channel == 1) g = v; else b = v;
                _hsv = new Rgba(r, g, b).ToHsv();
            }
        }
        _hsv = _hsv.Normalized();
        NotifyChanged();
    }

    /// <summary>
    /// Wheel over the card nudges the value component, and a wheel turn outside it
    /// commits and closes, both as Blender's colorpicker_wheel_cb does. The cursor
    /// position arrives in lParam; wParam carries the delta in its high word.
    /// </summary>
    static void Wheel(IntPtr wParam, IntPtr lParam)
    {
        int delta = (short)(wParam.ToInt64() >> 16);
        int px = (short)(lParam.ToInt64() & 0xFFFF);
        int py = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

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

    /// <summary>
    /// Hands the desktop to the user: the card steps aside, the pipette cursor follows
    /// the mouse anywhere, the next click samples that pixel, Esc cancels. The window
    /// KEEPS the mouse capture, so every move and the click come back here even though
    /// the cursor is over some other window; that is what makes the pipette cursor and
    /// the sample land where the user aimed. A poll loop could do neither: it starved
    /// the message pump, so Windows reset the cursor and the click was lost.
    /// </summary>
    static void PickScreen()
    {
        _picking = true;
        Native.ShowWindow(_hwnd, 0 /* SW_HIDE */);
        try
        {
            Native.GetCursorPos(out _);
            Native.SetCursor(Interop.EyedropperCursor.Handle);
            while (true)
            {
                int r = Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0);
                if (r <= 0) break;
                if (msg.message == Native.WM_LBUTTONDOWN) { FinishPick(); break; }
                if (msg.message == Native.WM_KEYDOWN
                    && (msg.wParam.ToInt64() & 0xFFFF) == Native.VK_ESCAPE) { _cancelled = true; _picking = false; break; }
                Native.TranslateMessage(ref msg);
                Native.DispatchMessageW(ref msg);
            }
        }
        finally
        {
            _picking = false;
            Interop.EyedropperCursor.Apply(false);
        }
        if (_hwnd == IntPtr.Zero) return;               // the pick loop tore the card down
        Native.ShowWindow(_hwnd, 5 /* SW_SHOW */);
        Native.SetForegroundWindow(_hwnd);
        Native.SetCapture(_hwnd);
        Paint();
    }

    /// <summary>Samples the pixel under the mouse and returns it to the card.</summary>
    static void FinishPick()
    {
        Native.GetCursorPos(out var at);
        var picked = Interop.ScreenPick.SampleAt(at.X, at.Y);
        if (picked is Rgba rgb)
        {
            _hsv = rgb.ToHsv();
            NotifyChanged();
        }
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

        EnsureWheel(dc);
        if (_wheelBmp != null)
            dc.DrawBitmap(_wheelBmp, (RectangleF?)Square(Picker.Center, Picker.WheelR, s), 1f,
                BitmapInterpolationMode.Linear, (RectangleF?)null);
        DrawValueVeil(dc, res, s);

        DrawWheelMarker(dc, res, s);
        DrawValueBar(dc, res, s);
        DrawChannels(dc, res, s);
        DrawHex(dc, res, s);
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

    /// <summary>
    /// Blender's value bar for the circle picker: a thin vertical gradient immediately
    /// right of the wheel, not a full-width horizontal one (GRAD_V_ALT, PICKER_BAR wide).
    /// </summary>
    static void DrawValueBar(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var track = S(Picker.ValueTrack, s);
        var pure = Pure();

        // Black at the top, the full hue at the bottom.
        var props = new LinearGradientBrushProperties(
            new V2(0f, track.Top), new V2(0f, track.Bottom));
        using var stops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0f, new Color4(0f, 0f, 0f, 1f)),
            new GradientStop(1f, pure.ToColor4()),
        ], Gamma.Linear, ExtendMode.Clamp);
        using var grad = dc.CreateLinearGradientBrush(props, stops);
        float r = track.Width / 2f;
        dc.FillRoundedRectangle(new RoundedRectangle(track, r, r), grad);

        float hx = track.Left + track.Width / 2f;
        float hy = Picker.YFromValue(_hsv.V) * s;
        float d = Picker.MarkerR * 2 * s;
        var white = res.Brush(new Color4(1f, 1f, 1f, 1f));
        Ring(dc, hx - Picker.MarkerR * s, hy - Picker.MarkerR * s, d, white, Picker.MarkerStroke * s);
    }

    /// <summary>
    /// One number-slider row per RGB channel, the way Blender lays them out under the
    /// wheel: the label on the left, a groove filled to the channel's value with a ring
    /// handle, and the number on the right.
    /// </summary>
    static void DrawChannels(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var cur = Current();
        byte[] values = [cur.R, cur.G, cur.B];
        Color4[] tints = [new(cur.R / 255f, 0.42f, 0.42f, 1f),
                          new(0.42f, cur.G / 255f, 0.42f, 1f),
                          new(0.42f, 0.42f, cur.B / 255f, 1f)];
        var labelFmt = res.Format("Segoe UI Variable Text", 12f, Vortice.DirectWrite.FontWeight.Normal);
        var valueFmt = res.Format("Segoe UI Variable Text", 12f, Vortice.DirectWrite.FontWeight.Normal);

        for (int i = 0; i < 3; i++)
        {
            var row = S(Picker.Channel(i), s);
            dc.FillRoundedRectangle(new RoundedRectangle(row, 8f * s, 8f * s), res.Brush(SystemTheme.Pill));
            dc.DrawText(Picker.ChannelLabels[i], labelFmt, new Rect(row.X, row.Y, row.Width, row.Height),
                res.Brush(SystemTheme.MutedInk));

            var groove = S(Picker.Groove(i), s);
            float hr = groove.Height / 2f;
            dc.FillRoundedRectangle(new RoundedRectangle(groove, hr, hr), res.Brush(SystemTheme.Field));
            float fill = Picker.XFromChannel(i, values[i]) * s + hr - groove.Left;
            if (fill > 0.5f)
                dc.FillRoundedRectangle(
                    new RoundedRectangle(new RectangleF(groove.Left, groove.Top, fill, groove.Height), hr, hr),
                    res.Brush(tints[i]));

            float hx = Picker.XFromChannel(i, values[i]) * s;
            float d = 7f * s;
            var white = res.Brush(new Color4(1f, 1f, 1f, 1f));
            Ring(dc, hx - d / 2f, groove.Top + groove.Height / 2f - d / 2f, d, white, 1.6f * s);

            // The number sits right-aligned, FieldPad clear of the box edge.
            float pad = 8f * s;
            dc.DrawText(values[i].ToString(), valueFmt,
                new Rect(row.Right - Picker.ChanValueW * s, row.Y, Picker.ChanValueW * s - pad, row.Height),
                res.Brush(SystemTheme.Ink));
        }
    }

    /// <summary>The hex field. Read-only, like the read-only fields we ship.</summary>
    static void DrawHex(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var box = S(Picker.Hex, s);
        dc.FillRoundedRectangle(new RoundedRectangle(box, 6f * s, 6f * s), res.Brush(SystemTheme.Pill));
        var fmt = res.Format("Segoe UI Variable Text", 12f, Vortice.DirectWrite.FontWeight.Normal);
        float pad = 8f * s;
        dc.DrawText(Current().ToHex(), fmt,
            new Rect(box.X + pad, box.Y, box.Width - pad * 2f, box.Height),
            res.Brush(SystemTheme.Ink));
    }

    /// <summary>
    /// Blender's eyedropper icon, the same raster the Windows pointer uses, tinted with
    /// the theme's ink. The pipette tip points down into where the sample lands.
    /// </summary>
    static void DrawEyedropper(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        var box = S(Picker.Eyedropper, s);
        dc.FillRoundedRectangle(new RoundedRectangle(box, 6f * s, 6f * s), res.Brush(SystemTheme.Pill));

        // The icon is square; centre it in the pill and keep a hair of air around it.
        float d = MathF.Min(box.Width, box.Height) - 4f * s;
        float side = d * Interop.BlenderDropper.Size / 30f;   // the art's own 1px margin
        float cell = side / Interop.BlenderDropper.Size;       // device px per mask pixel
        float ox = box.X + (box.Width - side) / 2f, oy = box.Y + (box.Height - side) / 2f;
        var brush = res.Brush(SystemTheme.Ink);
        for (int y = 0; y < Interop.BlenderDropper.Size; y++)
            for (int x = 0; x < Interop.BlenderDropper.Size; x++)
                if (Interop.BlenderDropper.Lit(x, y))
                    dc.FillRectangle(new RectangleF(ox + x * cell, oy + y * cell, cell + 0.5f, cell + 0.5f), brush);
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
    // brush re-maps the source through DPI and produced smeared bands here. The wheel
    // is baked at full value and dimmed with an overlay, so dragging the value slider
    // never rebuilds it (rebuilding per value step was the stutter).
    static void EnsureWheel(ID2D1DeviceContext dc)
    {
        int n = (int)MathF.Ceiling(Picker.WheelR * 2f * _scale);
        if (_wheelBmp == null || _wheelPx != n)
        {
            _wheelBmp?.Dispose();
            _wheelBmp = MakeWheel(dc, n);
            _wheelPx = n;
        }
    }

    static ID2D1Bitmap1? MakeWheel(ID2D1DeviceContext dc, int n)
    {
        float rMax = n / 2f;
        var buf = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = x + 0.5f - rMax, dy = y + 0.5f - rMax;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                if (r > rMax) continue;                       // clear outside the rim
                // Same mapping as ColorPickerLayout.HsFromPoint: hue 0 at 12 o'clock, clockwise.
                double hue = Math.Atan2(dx, -dy) / (2.0 * Math.PI) * 360.0;
                hue = (hue % 360.0 + 360.0) % 360.0;
                var (cr, cg, cb) = new Hsv(hue, Math.Clamp(r / rMax, 0.0, 1.0), 1.0).ToRgb();
                Put(buf, (y * n + x) * 4, cr, cg, cb);
            }
        return Upload(dc, n, n, buf);
    }

    /// <summary>Black veil over the rim: value is the wheel's brightness.</summary>
    static void DrawValueVeil(ID2D1DeviceContext dc, ResourceCache res, float s)
    {
        float dim = 1f - (float)Math.Clamp(_hsv.V, 0.0, 1.0);
        if (dim <= 0.002f) return;
        dc.FillEllipse(new Ellipse(new V2(Picker.Center.X * s, Picker.Center.Y * s),
            Picker.WheelR * s, Picker.WheelR * s),
            res.Brush(new Color4(0f, 0f, 0f, dim)));
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

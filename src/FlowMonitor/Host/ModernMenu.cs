using System;
using System.Drawing;
using System.Runtime.InteropServices;
using FlowMonitor.Interop;
using FlowMonitor.Model;
using FlowMonitor.Rendering;
using FlowMonitor.Widgets;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FlowMonitor.Host;

/// <summary>
/// Widget customization menu shell: single acrylic borderless popup, zero rows.
/// Reuses WidgetSurface from the shared device. Top-level (WS_EX_TOOLWINDOW).
/// THREADING: Show runs on the UI thread with its own modal loop; blocking.
/// Owns the GPU via SetMenuOpen while up; backdrop is best-effort.
/// </summary>
internal static class ModernMenu
{
    internal const string ClassName = "FlowMonitor.Menu.Wnd";

    /// <summary>Menu geometry, in unscaled pixels. Scaled by the widget's DPI at paint time.</summary>
    const float ShellWidth = 300f;
    const float ShellHeight = 132f;
    const float Pad = 16f;
    const float Radius = 12f;

    // Card fill: more opaque than a widget; backdrop supplies blur behind it.
    static readonly Color4 CardFill = new(0.086f, 0.090f, 0.106f, 0.92f);
    static readonly Color4 Hairline = new(1f, 1f, 1f, 0.09f);
    static readonly Color4 TitleInk = new(1f, 1f, 1f, 0.92f);
    static readonly Color4 MutedInk = new(1f, 1f, 1f, 0.45f);

    static bool _classRegistered;
    static WidgetWindow? _owner;
    static IRenderHost? _host;
    static WidgetSurface? _surface;
    static Native.POINT _anchor;

    // Own delegate type; the menu has a separate window class and lifetime.
    internal delegate IntPtr MenuWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    internal static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                // Both must return TRUE; returning 0 aborts CreateWindowExW.
                case Native.WM_NCCREATE:
                case Native.WM_CREATE:
                    return new IntPtr(1);

                case Native.WM_ERASEBKGND:
                    // Cleared to transparent in the paint pass; skip system erase to avoid flash.
                    return new IntPtr(1);

                case Native.WM_PAINT:
                    Paint(hwnd);
                    return new IntPtr(0);

                case Native.WM_LBUTTONDOWN:
                case Native.WM_RBUTTONDOWN:
                    // Zero rows: any click dismisses.
                    Dismiss();
                    return new IntPtr(0);

                case Native.WM_KEYDOWN:
                    if ((wParam.ToInt64() & 0xFFFF) == Native.VK_ESCAPE) { Dismiss(); return new IntPtr(0); }
                    break;

                case Native.WM_CANCELMODE:
                    Dismiss();
                    return new IntPtr(0);

                // Dismiss on deactivate/cancel so the menu cannot be orphaned on screen.
                case Native.WM_ACTIVATE:
                    if (LoWord(wParam) == 0) Dismiss();
                    return new IntPtr(0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("menu: exception in wndproc, dismissing: " + ex.Message);
            Dismiss();
        }
        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Sign-extending low word (GET_X_LPARAM semantics).</summary>
    static int LoWord(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);

    static void Dismiss()
    {
        Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    static IntPtr _hwnd;

    /// <summary>
    /// Shows the menu shell near (x, y) in screen coordinates. Returns when the menu dismisses.
    /// </summary>
    internal static void Show(IRenderHost host, WidgetWindow widget, int x, int y)
    {
        // Menu paints on the UI thread while widgets render on the render thread; both share
        // the process-wide D3D11/D2D device and cached brushes with no internal serialisation.
        // Widgets therefore do not render while the modal loop is up (SetMenuOpen).
        host.SetMenuOpen(true);
        try
        {
            ShowCore(host, widget, x, y);
        }
        finally
        {
            host.SetMenuOpen(false);
        }
    }

    static void ShowCore(IRenderHost host, WidgetWindow widget, int x, int y)
    {
        _host = host;
        _owner = widget;
        Native.GetCursorPos(out var cursor);
        _anchor.X = x;
        _anchor.Y = y;

        RegisterClass();

        var device = host.Device;
        var res = host.Resources;
        float s = widget.Config.Dpi;

        int w = (int)MathF.Round(ShellWidth * s);
        int h = (int)MathF.Round(ShellHeight * s);

        // Keep the whole shell on the cursor's monitor; flip past the anchor if it would fall off.
        var (sx, sy) = ClampToWorkArea(cursor.X, cursor.Y, w, h);

        // WS_POPUP is negative as int; assign to a local before the unchecked cast to uint.
        int styleBits = Native.WS_POPUP | Native.WS_CLIPSIBLINGS;
        uint style = unchecked((uint)styleBits);

        _hwnd = Native.CreateWindowExW(
            Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST,
            ClassName, "Flow Monitor",
            style,
            sx, sy, w, h,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            Log.Warn($"menu: CreateWindowExW failed win32={Marshal.GetLastWin32Error()}");
            return;
        }

        // Best-effort; pre-22000 shells return E_INVALIDARG.
        Native.SetTransientBackdrop(_hwnd);

        _surface = new WidgetSurface(device, _hwnd, w, h);
        Native.ShowWindow(_hwnd, 5 /* SW_SHOW */);

        // Capture the mouse so any click dismisses instead of going underneath.
        Native.SetCapture(_hwnd);
        Native.SetForegroundWindow(_hwnd);

        Paint(_hwnd);

        // Modal message loop on the UI thread.
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
        try { if (_hwnd != IntPtr.Zero) Native.ReleaseCapture(); } catch { }
        try { _surface?.Dispose(); } catch (Exception ex) { Log.Warn("menu: surface dispose: " + ex.Message); }
        _surface = null;
        try { if (Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd); } catch { }
        _hwnd = IntPtr.Zero;
        _host = null;
        _owner = null;
    }

    static (int x, int y) ClampToWorkArea(int cx, int cy, int w, int h)
    {
        int left, top, right, bottom;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        var mon = Native.MonitorFromPoint(new Native.POINT { X = cx, Y = cy }, 2 /*MONITOR_DEFAULTTONEAREST*/);
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

        // Anchor below-right of the click, then clamp inside the work area.
        int x = cx, y = cy;
        if (x + w > right) x = Math.Max(left, cx - w);
        if (y + h > bottom) y = Math.Max(top, cy - h);
        return (x, y);
    }

    static void Paint(IntPtr hwnd)
    {
        var surface = _surface;
        if (surface == null || _host == null) return;
        var res = _host.Resources;
        var cfg = _owner?.Config;
        float s = cfg?.Dpi ?? 1f;

        surface.BeginDraw(s * 96f);
        var dc = surface.Context;
        // Clear fully transparent to avoid hard corners outside the rounded card.
        dc.Clear(new Color4(0f, 0f, 0f, 0f));

        float w = surface.Width, h = surface.Height;

        var card = new RoundedRectangle(new RectangleF(0f, 0f, w, h), Radius * s, Radius * s);
        dc.FillRoundedRectangle(card, res.Brush(CardFill));
        // Floating surface needs an edge on light wallpaper; very low alpha.
        dc.DrawRoundedRectangle(card, res.Brush(Hairline), 1f * s);

        // Header; rows land underneath (none yet).
        string title = "Flow Monitor";
        dc.DrawText(title, res.Bold, new Rect(Pad * s, Pad * s, w - Pad * 2f * s, 20f * s), res.Brush(TitleInk));

        string sub = cfg == null ? "" : Describe(cfg);
        dc.DrawText(sub, res.Micro, new Rect(Pad * s, (Pad + 21f) * s, w - Pad * 2f * s, 16f * s), res.Brush(MutedInk));

        surface.EndDrawAndPresent();
    }

    /// <summary>One-line description of the widget for the shell header.</summary>
    static string Describe(WidgetConfig c) => $"{c.Graph}  ·  {c.Width}x{c.Height}";

    static void RegisterClass()
    {
        if (_classRegistered) return;
        var wc = new Native.WNDCLASSEX
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
            style = 0x0008 /* CS_DBLCLKS */,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcDelegate),
            hInstance = Native.GetModuleHandle(null),
            lpszClassName = ClassName,
            hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
        };
        if (Native.RegisterClassExW(ref wc) == 0)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            {
                Log.Warn($"menu: RegisterClassExW failed win32={err}");
                return;
            }
        }
        _classRegistered = true;
    }

    // Static so the WndProc pointer stays valid after Show returns.
    static readonly MenuWndProc WndProcDelegate = WndProc;
}

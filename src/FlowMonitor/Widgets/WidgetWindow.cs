using System.Numerics;
using System.Runtime.InteropServices;
using FlowMonitor.Interop;
using FlowMonitor.Host;
using FlowMonitor.Model;
using FlowMonitor.Rendering;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Widgets;

public enum ResizeEdge
{
    None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight,
}

/// <summary>
/// One desktop widget: a child HWND parented into Explorer's WorkerW (behind the icons)
/// with its own DirectComposition target, composition swap chain and Direct2D device context.
/// </summary>
public sealed class WidgetWindow
{
    public const int ResizeBorder = 7;
    public const int CheckMarkSize = (int)HeaderLayout.BadgeBox;   // badge hit box, also the glyph size

    readonly Host.IRenderHost _host;
    WidgetSurface? _surface;
    ChartRenderer? _charts;
    int _width = 1, _height = 1;

    // interaction state (UI thread only)
    bool _dragging, _resizing, _hover;
    bool _checkHoverTarget, _closeHoverTarget, _chipHoverTarget;
    float _checkHover, _closeHover, _chipHover;
    ResizeEdge _edge;
    POINT _dragOrigin;
    RECT _dragStartRect;
    POINT _dragScreenOrigin;
    int _dragSeq;

    /// <summary>Per-move drag tracing, off unless FLOWMONITOR_DRAGLOG=1.</summary>
    static readonly bool DragTraceOn =
        Environment.GetEnvironmentVariable("FLOWMONITOR_DRAGLOG") == "1";

    void Trace(string s) => Log.Info("drag: " + s);
    int _cursorShape = -1;

    // animation state (render thread)
    volatile bool _redrawRequested = true;
    volatile bool _editing;

    float _hoverAmount;
    float _checkAmount;
    bool _wasEditing;

    public IntPtr Handle { get; private set; }
    public WidgetConfig Config { get; private set; }

    public WidgetWindow(Host.IRenderHost host, WidgetConfig config)
    {
        _host = host;
        Config = config;
        _width = Math.Max(120, config.Width);
        _height = Math.Max(80, config.Height);
        config.Dpi = 1f;
    }

    // ------------------------------------------------------------------ lifecycle

    public void Create(IntPtr parent)
    {
        Handle = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            DesktopHost.WidgetClassName,
            "FlowMonitor.Widget",
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            Config.X, Config.Y, _width, _height,
            parent, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);

        if (Handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

        _charts = new ChartRenderer(_host.Device, _host.Resources);
        // DComp target is bound to this HWND; create after final parenting.
        _surface = new WidgetSurface(_host.Device, Handle, _width, _height);
        ApplyBackdrop();
        UpdateDpi();
        ApplyInteractionMode();
        // New children start at the bottom of the parent z-order, beneath opaque shell windows.
        RaiseToTopOfDesktopLayer();
        // RenderFrame skips unchanged frames; request one so the first frame presents immediately.
        RequestRedraw();
    }

    /// <summary>
    /// Re-parent onto a new WorkerW after Explorer restarts, and rebuild the composition target
    /// to match. The DComp target holds a hard reference to the HWND it was made for, so a
    /// SetParent without a target rebuild leaves the widget alive but permanently blank.
    /// </summary>
    public void Reparent(IntPtr newParent)
    {
        if (Handle == IntPtr.Zero) return;
        SetParent(Handle, newParent);
        _surface?.RenewTargetForHwnd(_host.Device, Handle);
        SetWindowPos(Handle, IntPtr.Zero, Config.X, Config.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        RaiseToTopOfDesktopLayer();
        ApplyBackdrop();
        UpdateDpi();
        RequestRedraw();
    }

    /// <summary>
    /// Raise above opaque shell icon windows. New children start at the bottom of the
    /// z-order; SWP_NOZORDER must not be set or this is a no-op.
    /// </summary>
    public void RaiseToTopOfDesktopLayer()
    {
        if (Handle == IntPtr.Zero) return;
        // HWND_TOP is IntPtr.Zero, and SWP_NOZORDER must NOT be set or this is a no-op.
        SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Push click-through mode onto window style; safe to call on every edit-mode transition.
    /// Never sets WS_EX_TRANSPARENT (window would stop receiving WM_RBUTTONUP);
    /// left-button fall-through uses explicit forwarding instead.
    /// </summary>
    public void ApplyInteractionMode()
    {
        if (Handle == IntPtr.Zero) return;
        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        bool has = (ex & WS_EX_TRANSPARENT) != 0;
        if (!has) return;
        long next = ex & ~WS_EX_TRANSPARENT;
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(next));
    }

    /// <summary>
    /// Forwards left-button to the window underneath when locked, so icons stay selectable.
    /// Runs for LeftClickOnly and Full; Off swallows, editing never forwards. Target found via
    /// temporary WS_EX_TRANSPARENT + WindowFromPoint, translated to client coords.
    /// </summary>
    public void ForwardLeftClickToWindowBelow(bool isUp)
    {
        if (_editing) return;
        if (Config.ClickThrough == ClickThroughMode.Off) return;
        if (Handle == IntPtr.Zero) return;

        long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT));

        IntPtr below;
        try
        {
            GetCursorPos(out var p);
            below = WindowFromPoint(p);
        }
        finally
        {
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
        }

        if (below == IntPtr.Zero || below == Handle) return;

        GetCursorPos(out var screen);
        if (!ScreenToClient(below, ref screen)) return;

        int lp = (screen.Y << 16) | (screen.X & 0xFFFF);
        uint msg = (uint)(isUp ? WM_LBUTTONUP : WM_LBUTTONDOWN);
        SendMessageW(below, msg, (IntPtr)(isUp ? 0 : 1), new IntPtr(lp));
    }

    void ApplyBackdrop()
    {
        // Subtle system backdrop (acrylic) so the widget reads as a native desktop surface.
        var data = new ACCENT_POLICY_AND_DATA
        {
            Policy = new ACCENT_POLICY
            {
                AccentState = ACCENT_ENABLE_HOSTBACKDROP,
                AccentFlags = 2,
                GradientColor = 0,
                AnimationId = 0,
            },
            Data = 0,
        };
        int size = Marshal.SizeOf<ACCENT_POLICY_AND_DATA>();
        var attr = new WINDOWCOMPOSITIONATTRIBDATA
        {
            Attribute = WCA_ACCENT_POLICY,
            Data = Marshal.AllocHGlobal(size),
            SizeOfData = size,
        };
        try
        {
            Marshal.StructureToPtr(data, attr.Data, false);
            SetWindowCompositionAttribute(Handle, ref attr);
        }
        catch { /* backdrop is a nicety, never fatal */ }
        finally { Marshal.FreeHGlobal(attr.Data); }
    }

    public void UpdateDpi()
    {
        if (Handle == IntPtr.Zero) return;
        uint dpi = GetDpiForWindow(Handle);
        Config.Dpi = (dpi == 0 ? 96f : dpi) / 96f;
    }

    // 1 while the render thread is inside this widget's frame body; read by teardown.
    int _inFrame;
    int _surfaceReleased;

    /// <summary>Frees all D2D/D3D objects. RENDER THREAD ONLY; UI thread uses the destroy queue.</summary>
    public void ReleaseGpuResources()
    {
        // Destroys are rare; always log overlap state.
        Log.Info($"destroy: widget 0x{Handle:X} inFrame={Interlocked.CompareExchange(ref _inFrame, 0, 0)}");
        if (Interlocked.Exchange(ref _surfaceReleased, 1) == 1) return;
        _surface?.Dispose();
        _surface = null;
    }

    /// <summary>
    /// Destroys the window. UI THREAD ONLY; GPU resources already released on render thread.
    /// </summary>
    public void DestroyWindowOnly()
    {
        if (Handle != IntPtr.Zero) { DestroyWindow(Handle); Handle = IntPtr.Zero; }
    }

    /// <summary>Full teardown. Only safe after the render thread joined; otherwise use destroy queue.</summary>
    public void Destroy()
    {
        _inFrame = 0;
        ReleaseGpuResources();
        DestroyWindowOnly();
    }

    // ------------------------------------------------------------------ geometry

    public void MoveTo(int x, int y)
    {
        Config.X = x; Config.Y = y;
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public void ResizeTo(int w, int h)
    {
        w = Math.Max(120, w);
        h = Math.Max(80, h);
        if (w == _width && h == _height) return;
        _width = w; _height = h;
        Config.Width = w; Config.Height = h;
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        // Size applied on render thread via WidgetSurface.ApplyPendingResize.
        _surface?.RequestResize(w, h);
        RequestRedraw();
    }

    public IntPtr Parent => GetParent(Handle);

    /// <summary>Composition surface. Exposed for self-test pixel reads.</summary>
    internal WidgetSurface? Surface => _surface;

    public void Raise()
    {
        if (Handle != IntPtr.Zero)
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ------------------------------------------------------------------ edit mode

    public bool IsEditing => _editing;

    public void BeginEdit()
    {
        _editing = true;
        ApplyInteractionMode();
        RequestRedraw();
    }

    public void EndEdit()
    {
        _editing = false;
        ApplyInteractionMode();
        RequestRedraw();
    }

    public void RequestRedraw() => _redrawRequested = true;

    public bool IsVisible => Handle != IntPtr.Zero && IsWindow(Handle);

    public void ConsumeRedrawRequest()
    {
        _redrawRequested = false;
    }

    public bool RedrawRequested => _redrawRequested;

    // ------------------------------------------------------------------ input

    public void OnMouseMove(int x, int y)
    {
        if (!_hover) { _hover = true; RequestRedraw(); }
        bool ck = _editing && IsInCheckMark(x, y);
        bool cl = _editing && IsInCloseButton(x, y);
        bool ch = _editing && IsInColorChip(x, y);
        if (ck != _checkHoverTarget || cl != _closeHoverTarget || ch != _chipHoverTarget)
        {
            _checkHoverTarget = ck; _closeHoverTarget = cl; _chipHoverTarget = ch;
            RequestRedraw();
        }
        if (_dragging) OnDragMove(x, y);
        else if (_resizing) OnDragMove(x, y);
        else UpdateCursorForPosition(x, y);
    }

    public void OnMouseLeave()
    {
        if (_hover) { _hover = false; RequestRedraw(); }
        if (_checkHoverTarget || _closeHoverTarget || _chipHoverTarget)
        {
            _checkHoverTarget = _closeHoverTarget = false;
            RequestRedraw();
        }
        // Do not touch cursor; pointer belongs to window underneath. Clear cache to re-evaluate on re-enter.
        _cursorShape = -1;
    }

    public void OnLeftButtonDown(int x, int y)
    {
        if (!_editing)
        {
            // Locked. Hand the click to whatever is underneath instead of eating it, then let the
            // up message follow so a plain click on a desktop icon selects it as usual.
            ForwardLeftClickToWindowBelow(isUp: false);
            return;
        }

        // X left, checkmark right; boxes adjacent, non-overlapping.
        if (IsInCloseButton(x, y))
        {
            _host.CloseWidget(this, deleteSaved: true);
            return;
        }

        if (IsInCheckMark(x, y))
        {
            EndEdit();
            return;
        }

        if (IsInColorChip(x, y))
        {
            OpenColorPicker();
            return;
        }

        // Metric switcher flanking the centred title; edit mode only.
        if (IsInPrevMetric(x, y)) { CycleMetric(-1); return; }
        if (IsInNextMetric(x, y)) { CycleMetric(+1); return; }

        GetCursorPos(out var screen);
        var p = screen;
        ScreenToClient(Handle, ref p);

        _edge = HitTestEdge(x, y);
        _resizing = _edge != ResizeEdge.None;
        _dragging = !_resizing;

        if (_dragging || _resizing)
        {
            _dragOrigin = p;
            _dragScreenOrigin = screen;
            _dragSeq = 0;
            GetWindowRect(Handle, out _dragStartRect);
            SetCapture(Handle);
            RequestRedraw();
        }

        if (DragTraceOn) Trace($"down mode={(_resizing ? "resize:" + _edge : "drag")} " +
            $"cursorScreen=({screen.X},{screen.Y}) clientOrigin=({p.X},{p.Y}) " +
            $"startRect=({_dragStartRect.Left},{_dragStartRect.Top})-({_dragStartRect.Right},{_dragStartRect.Bottom})");
    }

    public void OnLeftButtonUp()
    {
        if (_dragging || _resizing)
        {
            _dragging = false; _resizing = false; _edge = ResizeEdge.None;
            ReleaseCapture();
            // Notify host once when gesture finishes; nothing queued during gesture.
            _host.OnWidgetGeometryChanged(this, Config.X, Config.Y, Config.Width, Config.Height);
            RequestRedraw();
            if (DragTraceOn)
            {
                GetCursorPos(out var s);
                GetWindowRect(Handle, out var r);
                Trace($"up cursorScreen=({s.X},{s.Y}) finalRect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) " +
                      $"moves={_dragSeq}");
            }
            _dragSeq = 0;
            return;
        }

        // Not a drag we owned - close out the click we forwarded on the way down.
        if (!_editing) ForwardLeftClickToWindowBelow(isUp: true);
    }

    /// <summary>
    /// Converts screen point to parent client space for <see cref="MoveTo"/>/<see cref="ResizeTo"/>
    /// and <c>Config.X</c>/<c>Config.Y</c>. Drag math stays in screen coords, converted once here.
    /// </summary>
    bool ScreenToParent(int sx, int sy, out int px, out int py)
    {
        px = sx; py = sy;
        var parent = GetParent(Handle);
        if (parent == IntPtr.Zero) return false;
        var p = new POINT { X = sx, Y = sy };
        if (!ScreenToClient(parent, ref p)) return false;
        px = p.X; py = p.Y;
        return true;
    }

    void OnDragMove(int x, int y)
    {
        // Delta in screen space vs button-down snapshot. Client space moves with the
        // window, so it understates motion; absolute samples also tolerate coalesced moves.
        GetCursorPos(out var sc);
        int dx = sc.X - _dragScreenOrigin.X;
        int dy = sc.Y - _dragScreenOrigin.Y;

        if (DragTraceOn)
        {
            // client logged for diagnostic contrast only.
            _dragSeq++;
            GetWindowRect(Handle, out var rc);
            Trace($"n={_dragSeq} client=({x},{y}) screen=({sc.X},{sc.Y}) " +
                  $"screenDelta=({dx},{dy}) rect=({rc.Left},{rc.Top}) " +
                  $"targetScreen=({_dragStartRect.Left + dx},{_dragStartRect.Top + dy})");
        }

        if (_dragging)
        {
            // Applied synchronously on the window-owner thread.
            int sx = _dragStartRect.Left + dx, sy = _dragStartRect.Top + dy;
            if (!ScreenToParent(sx, sy, out int px, out int py)) { px = sx; py = sy; }
            MoveTo(px, py);
        }
        else
        {
            int left = _dragStartRect.Left, top = _dragStartRect.Top;
            int right = _dragStartRect.Right, bottom = _dragStartRect.Bottom;

            if (_edge is ResizeEdge.Left or ResizeEdge.TopLeft or ResizeEdge.BottomLeft)
            { left += dx; if (right - left < 120) left = right - 120; }
            if (_edge is ResizeEdge.Right or ResizeEdge.TopRight or ResizeEdge.BottomRight)
            { right += dx; if (right - left < 120) right = left + 120; }
            if (_edge is ResizeEdge.Top or ResizeEdge.TopLeft or ResizeEdge.TopRight)
            { top += dy; if (bottom - top < 80) top = bottom - 80; }
            if (_edge is ResizeEdge.Bottom or ResizeEdge.BottomLeft or ResizeEdge.BottomRight)
            { bottom += dy; if (bottom - top < 80) bottom = top + 80; }

            // Resize synchronously as well.
            MoveTo(left, top);
            ResizeTo(right - left, bottom - top);
        }
    }

    public ResizeEdge HitTestEdge(int x, int y)
    {
        bool l = x < ResizeBorder;
        bool r = x >= _width - ResizeBorder;
        bool t = y < ResizeBorder;
        bool b = y >= _height - ResizeBorder;
        if (t && l) return ResizeEdge.TopLeft;
        if (t && r) return ResizeEdge.TopRight;
        if (b && l) return ResizeEdge.BottomLeft;
        if (b && r) return ResizeEdge.BottomRight;
        if (l) return ResizeEdge.Left;
        if (r) return ResizeEdge.Right;
        if (t) return ResizeEdge.Top;
        if (b) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    // Last painted chevron and badge boxes in logical px; paint and hit-test share HeaderLayout.
    System.Drawing.RectangleF _prevMetricRect, _nextMetricRect, _closeRect, _checkRect, _chipRect;

    /// <summary>Badge rects from the last painted frame (HeaderLayout).</summary>
    internal System.Drawing.RectangleF CloseBadgeRect => _closeRect;
    internal System.Drawing.RectangleF CheckBadgeRect => _checkRect;
    internal System.Drawing.RectangleF ColorChipRect => _chipRect;

    /// Opens the modal picker under the chip and commits the colour on accept.
    void OpenColorPicker()
    {
        float d = Config.Dpi;
        var r = _chipRect;
        GetClientRect(Handle, out var client);
        int sx = (int)(r.Left * d) - (int)(r.Width * d * 0.5f);
        int sy = (int)((r.Top + r.Height) * d) + 8;
        var screen = new Native.POINT(sx, sy);
        ClientToScreen(Handle, ref screen);
        string start = Config.LineColorFor(Config.Graph);
        // Live preview: every drag change repaints this widget with the new colour
        // (unsaved). Commit writes the config; cancel puts the original back.
        string? picked = ColorPickerWindow.Show(_host, this, screen.X, screen.Y, start, live =>
        {
            Config.SetLineColor(Config.Graph, live);
            _host.OnWidgetConfigChanged(this, livePreview: true);
        });
        if (picked == null)
        {
            Config.SetLineColor(Config.Graph, start);
            _host.OnWidgetConfigChanged(this, livePreview: true);
            return;
        }
        Config.SetLineColor(Config.Graph, picked);
        _host.OnWidgetConfigChanged(this, livePreview: false);
    }

    static void GetClientRect(IntPtr h, out RECT r) => Native.GetClientRect(h, out r);

    static void ClientToScreen(IntPtr h, ref POINT p) => Native.ClientToScreen(h, ref p);

    bool IsInColorChip(int x, int y)
    {
        if (!_editing || _chipRect.Width <= 0) return false;
        var d = Config.Dpi;
        System.Drawing.RectangleF r = _chipRect;
        return x >= r.Left * d && x <= (r.Left + r.Width) * d && y >= r.Top * d && y <= (r.Top + r.Height) * d;
    }

    bool IsInCheckMark(int x, int y)
    {
        System.Drawing.RectangleF r = _checkRect;
        return r.Width > 0 && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    /// <summary>X badge at the head of the tick column, mirrored against the check.</summary>
    bool IsInCloseButton(int x, int y)
    {
        System.Drawing.RectangleF r = _closeRect;
        return r.Width > 0 && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    /// <summary>
    /// Metric switcher rects from the last painted frame (HeaderLayout). Empty before
    /// the first frame; edit mode always paints before it accepts clicks.
    /// </summary>
    internal System.Drawing.RectangleF PrevMetricRect => _prevMetricRect;
    internal System.Drawing.RectangleF NextMetricRect => _nextMetricRect;

    bool IsInPrevMetric(int x, int y)
    {
        var r = _prevMetricRect;
        return r.Width > 0 && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    bool IsInNextMetric(int x, int y)
    {
        var r = _nextMetricRect;
        return r.Width > 0 && x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // One card per metric the user needs. GpuCores is not offered: the Gpu card already
    // shows actual engine usage, and a second GPU card split the meaning in two.
    /// <summary>Cards the user can pick, in menu order. Excludes retired enum members.</summary>
    internal static readonly GraphKind[] CardSheetKinds =
    [
        GraphKind.Cpu, GraphKind.CpuCores, GraphKind.Memory, GraphKind.Disk,
        GraphKind.Network, GraphKind.Gpu, GraphKind.Fans, GraphKind.Vram,
    ];

    static readonly GraphKind[] MetricCycle =
    [
        GraphKind.Cpu, GraphKind.CpuCores, GraphKind.Memory, GraphKind.Disk,
        GraphKind.Network, GraphKind.Gpu, GraphKind.Vram,
    ];

    /// <summary>Next supported metric in the cycle, wrapping around.</summary>
    internal static GraphKind CycleGraphKind(GraphKind current, int dir)
    {
        int i = Array.IndexOf(MetricCycle, current);
        if (i < 0) return dir < 0 ? MetricCycle[^1] : MetricCycle[0];
        return MetricCycle[(i + dir + MetricCycle.Length) % MetricCycle.Length];
    }

    void CycleMetric(int dir)
    {
        var cfg = Config.Clone();
        cfg.Graph = CycleGraphKind(cfg.Graph, dir);
        UpdateConfig(cfg);
        _host.OnWidgetConfigChanged(this, livePreview: false);
    }

    void UpdateCursorForPosition(int x, int y)
    {
        // IDC_ARROW, never 0 (NULL removes the cursor).
        if (!_editing) { SetCursorShape(IDC_ARROW); return; }
        // Hand cursor over badges.
        if (IsInCloseButton(x, y) || IsInCheckMark(x, y) || IsInColorChip(x, y)) { SetCursorShape(IDC_HAND); return; }
        if (IsInPrevMetric(x, y) || IsInNextMetric(x, y)) { SetCursorShape(IDC_HAND); return; }
        SetCursorShape(HitTestEdge(x, y) switch
        {
            ResizeEdge.Left or ResizeEdge.Right => IDC_SIZEWE,
            ResizeEdge.Top or ResizeEdge.Bottom => IDC_SIZENS,
            _ => IDC_ARROW,
        });
    }

    /// <summary>
    /// Drives cursor from WM_SETCURSOR (re-issued whenever cursor needs re-establishing).
    /// x/y ignored: WM_SETCURSOR lParam holds hit-test + mouse message, not coords.
    /// </summary>
    public void OnSetCursor(int x, int y)
    {
        _ = x; _ = y;
        if (!GetCursorPos(out var screen)) return;
        var p = screen;
        if (!ScreenToClient(Handle, ref p)) return;
        UpdateCursorForPosition(p.X, p.Y);
    }

    void SetCursorShape(int shape)
    {
        if (_cursorShape == shape) return;
        _cursorShape = shape;
        SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(shape)));
    }

    /// <summary>Right-click re-enters edit mode when locked, in all click-through modes.</summary>
    public void OnRightButtonUp(int x, int y)
    {
        _ = x; _ = y;
        if (_editing) return;
        BeginEdit();
    }

    // ------------------------------------------------------------------ render

    /// <summary>Renders one frame if anything changed. Returns true when a frame was presented.</summary>
    public bool RenderFrame(double now, float dt)
    {
        if (_surface is null || _charts is null) return false;

        // Marks frame body live; ReleaseGpuResources reads this.
        Interlocked.Exchange(ref _inFrame, 1);
        try
        {
        return RenderFrameCore(now, dt);
        }
        finally { Interlocked.Exchange(ref _inFrame, 0); }
    }

    bool RenderFrameCore(double now, float dt)
    {
        // Local copy; surface only released outside frame body.
        var surface = _surface;
        if (surface is null || _charts is null) return false;

        bool editing = _editing;
        float s = Config.Dpi;

        float hoverTarget = _hover || _dragging || _resizing ? 1f : 0f;
        float prevHover = _hoverAmount;
        _hoverAmount += (hoverTarget - _hoverAmount) * Math.Clamp(dt * 14f, 0f, 1f);
        if (Math.Abs(hoverTarget - _hoverAmount) > 0.004f) _redrawRequested = true;

        float prevCheck = _checkAmount;
        if (editing)
        {
            _checkAmount = Math.Min(1f, _checkAmount + dt * 8f);
            _redrawRequested = true;
        }
        else if (_checkAmount > 0f)
        {
            _checkAmount = Math.Max(0f, _checkAmount - dt * 10f);
            _redrawRequested = true;
        }

        float ease = Math.Clamp(dt * 14f, 0f, 1f);
        float prevCheckHov = _checkHover, prevCloseHov = _closeHover, prevChipHov = _chipHover;
        _checkHover += ((_checkHoverTarget && editing ? 1f : 0f) - _checkHover) * ease;
        _chipHover += ((_chipHoverTarget && editing ? 1f : 0f) - _chipHover) * ease;
        _closeHover += ((_closeHoverTarget && editing ? 1f : 0f) - _closeHover) * ease;
        if (Math.Abs(_checkHover - prevCheckHov) > 0.004f
            || Math.Abs(_closeHover - prevCloseHov) > 0.004f
            || Math.Abs(_chipHover - prevChipHov) > 0.004f) _redrawRequested = true;

        // Reset transform on edit-mode exit.
        if (_wasEditing && !editing) surface.SetTransform(Matrix3x2.Identity);
        _wasEditing = editing;

        bool chromeAnimating = Math.Abs(prevHover - _hoverAmount) > 0.0005f
                            || Math.Abs(prevCheck - _checkAmount) > 0.0005f
                            || Math.Abs(prevCheckHov - _checkHover) > 0.0005f
                            || Math.Abs(prevCloseHov - _closeHover) > 0.0005f
                            || Math.Abs(prevChipHov - _chipHover) > 0.0005f;

        if (!_redrawRequested && !chromeAnimating && !_charts.FrameChanged) return false;

        var model = _host.BuildChart(Config, now);

        // This frame's header is laid out ONCE here: the boxes stored below drive the
        // hit test, and the same result is handed to the renderer that paints the
        // title and value, so paint and hit-test cannot disagree
        // (docs/design.md header rule 14). RENDER THREAD: apply pending resize before
        // reading the surface size, so the header and the chart lay out against the
        // size actually being painted.
        model.EditChrome = editing;
        surface.ApplyPendingResize();
        HeaderLayout header;
        {
            var res = _host.Resources;
            string valueText = ChartRenderer.HeaderValueText(Config, model);
            float titleW = res.Measure(model.Title, res.Title, Config.Dpi).Width / Config.Dpi;
            float valueW = res.Measure(valueText, res.HeaderValue, Config.Dpi).Width / Config.Dpi;
            // The badges ride the tick column and the whole row is centred in the band
            // above the chart, so the header needs the plot geometry: its left edge is
            // the tick column, its top edge is the first horizontal line.
            var geo = _charts.Geometry(Config, model, Config.Dpi, surface.Width, surface.Height);
            float rowCenter = HeaderLayout.RowCenterFor(geo.Rect.Top / Config.Dpi);
            header = HeaderLayout.Compute(_width, titleW, valueW, editing, geo.LabelColumnX, rowCenter);
            _prevMetricRect = header.Prev;
            _nextMetricRect = header.Next;
            _closeRect = header.Close;
            _checkRect = header.Check;
            _chipRect = header.Chip;
        }
        var dc = surface.Context;
        // Paint against surface real pixel size; _width/_height are logical units.
        float w = surface.Width, h = surface.Height;

        surface.BeginDraw(Config.Dpi * 96f);
        // Clear transparent; card is rounded so previous frame remains in corners.
        dc.Clear(new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
        WidgetPainter.PaintBackground(dc, _host.Resources, Config, w, h, _hoverAmount, _checkAmount, editing);
        _charts.Draw(dc, Config, model, Config.Dpi * 96f, now, surface.Width, surface.Height, header);
        WidgetPainter.PaintCloseButton(dc, _host.Resources, Config, w, h, _closeRect, _checkAmount, _closeHover);
        WidgetPainter.PaintCheckMark(dc, _host.Resources, Config, w, h, _checkRect, _checkAmount, _checkHover);
        WidgetPainter.PaintColorChip(dc, _host.Resources, Config, w, h, _checkAmount, _chipRect,
            Rgba.FromHex(Config.LineColorFor(Config.Graph)).ToColor4());
        if (editing) WidgetPainter.PaintMetricArrows(dc, _host.Resources, Config, w, h, _checkAmount,
            _prevMetricRect, _nextMetricRect);
        if (editing) WidgetPainter.PaintResizeAffordance(dc, _host.Resources, Config, w, h,
            _hoverAmount, _charts.HasAxisLabelColumn(model));
        surface.EndDrawAndPresent();

        _redrawRequested = false;
        return true;
    }

    public void ApplyOpacity()
    {
        if (_surface is null) return;
        var surface = _surface;
        float o = Config.IdleOpacity + (Config.HoverOpacity - Config.IdleOpacity) * _hoverAmount;
        surface.SetOpacity(Math.Clamp(o, 0.05f, 1f));
    }

    public void CommitComposition() => _surface?.Commit(_host.Device);

    public void UpdateConfig(WidgetConfig cfg)
    {
        cfg.Dpi = Config.Dpi;
        Config = cfg;
        // Click-through lives in EXSTYLE; push style update immediately.
        ApplyInteractionMode();
        RequestRedraw();
    }
}

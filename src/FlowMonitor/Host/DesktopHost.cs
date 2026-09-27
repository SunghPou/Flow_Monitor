using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Globalization;
using FlowMonitor.Interop;
using FlowMonitor.Model;
using FlowMonitor.Rendering;
using FlowMonitor.Metrics;
using FlowMonitor.Widgets;
using Vortice.Mathematics;
using Color4 = Vortice.Mathematics.Color4;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Host;

public interface IRenderHost
{
    RenderDevice Device { get; }
    ResourceCache Resources { get; }
    ChartModel BuildChart(WidgetConfig cfg, double now);
    void OnWidgetGeometryChanged(WidgetWindow w, int x, int y, int width, int height);
    void SetMenuOpen(bool open);
    void ShowContextMenu(WidgetWindow widget, int x, int y);
    void CloseWidget(WidgetWindow widget);
    void CloseWidget(WidgetWindow widget, bool deleteSaved);

    /// <summary>
    /// Called by the context menu whenever it mutates a widget's configuration.
    /// <paramref name="livePreview"/> is true while the user is dragging a slider or a colour
    /// swatch, so the host applies the change without touching disk; it is false on commit,
    /// which is the only time the configuration is written out.
    /// </summary>
    void OnWidgetConfigChanged(WidgetWindow widget, bool livePreview);
}

/// <summary>
/// Owns the process: window classes, the WorkerW desktop parent, every widget, the render
/// loop thread and the telemetry clock.
/// </summary>
public sealed class DesktopHost : IRenderHost, IDisposable
{
    public const string WidgetClassName = "FlowMonitor.Widget.Wnd";
    public static IntPtr ModuleHandle { get; } = Native.GetModuleHandle(null);

    readonly ConcurrentDictionary<IntPtr, WidgetWindow> _byHandle = new();
    readonly List<WidgetWindow> _widgets = new();
    readonly Telemetry _telemetry = new();

    IntPtr _workerW;
    Thread? _renderThread;
    volatile bool _running;
    double _frameInterval;
    long _nextWorkerWCheck;
    readonly List<(WidgetWindow w, int x, int y, int width, int height)> _pendingGeometry = new();
    readonly object _pendingGate = new();

    // Control channel: message-only window plus named events for --kill / --new-widget.
    // The control window is never shown, parented, or enumerated by the shell.
    const string ControlClassName = "FlowMonitor.Control.Wnd";
    const string KillEventName = @"Local\FlowMonitor.Kill.v1";
    const string NewWidgetEventName = @"Local\FlowMonitor.NewWidget.v1";
    IntPtr _controlWnd;
    IntPtr _killEvent;
    IntPtr _newWidgetEvent;
    Thread? _controlThread;
    int _boundedSeconds;                 // 0 = run until told to stop
    volatile bool _workerWBusy;          // re-entrancy guard for WorkerW re-parenting
    readonly RenderFaultTracker<WidgetWindow> _renderFaults = new(RenderFaultsBeforeRetire);

    /// <summary>
    /// Signal the already-running instance to shut down. Returns false when nothing is running,
    /// which is the honest answer rather than pretending a kill happened.
    /// </summary>
    public static bool SignalRunningInstanceToExit()
        => SignalEvent(KillEventName);

    /// <summary>Ask the running instance to spawn one more widget, in edit mode.</summary>
    public static bool SignalNewWidgetRequest()
        => SignalEvent(NewWidgetEventName);

    /// <summary>
    /// Signal a named event that another instance is (or should be) waiting on.
    /// Internal so the self test can exercise the kill / new-widget round-trip.
    /// </summary>
    internal static bool SignalEvent(string name)
    {
        IntPtr ev = OpenEventW(EVENT_MODIFY_STATE, false, name);
        if (ev == IntPtr.Zero) return false;
        bool ok = SetEvent(ev);
        CloseHandle(ev);
        return ok;
    }

    public RenderDevice Device { get; } = null!;
    public ResourceCache Resources { get; } = null!;

    public DesktopHost()
    {
        // Every sample tick wakes all widgets. Locked widgets skip frames while the curve
        // is flat, so a redraw request is the only thing that restarts their polling.
        _telemetry.Sampled += WakeWidgetsForNewSample;
        try
        {
            Device = new RenderDevice();
            Resources = new ResourceCache(Device);
            _frameInterval = 1.0 / DwmApi.CompositionRefreshRate();
            Log.Info($"RenderDevice up. composition refresh = {1.0 / _frameInterval:F1} Hz");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex);
            throw;
        }
    }

    // ------------------------------------------------------------------ startup

    public void Run(string[] args)
    {
        _current = this;
        RegisterClasses();
        CreateControlWindow();
        _telemetry.Start();

        _workerW = Desktop.FindWorkerW();
        if (_workerW == IntPtr.Zero)
            throw new InvalidOperationException("Could not locate the Explorer desktop (WorkerW).");
        Log.Info("WorkerW desktop parent = 0x" + _workerW.ToString("X"));

        // Saved widgets always come back locked; a fresh default (no saves on disk)
        // starts in edit mode.
        var saved = WidgetStore.LoadAll();
        if (saved.Count == 0)
        {
            if (args.Contains("--restore"))
                Log.Info("--restore found no saved widgets; starting with one default.");
            AddWidget(NewDefaultConfig(), editMode: true);
        }
        else
        {
            foreach (var c in saved) AddWidget(c, editMode: false);
        }

        _running = true;
        _renderThread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "FlowMonitor.Render",
            Priority = ThreadPriority.AboveNormal,
        };
        _renderThread.Start();

        // Optional time-box: --bounded <seconds> exits on its own after the interval.
        _boundedSeconds = 0;
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--bounded" && int.TryParse(args[i + 1], out int s)) _boundedSeconds = s;

        _controlThread = new Thread(ControlLoop)
        {
            IsBackground = true,
            Name = "FlowMonitor.Control",
            Priority = ThreadPriority.BelowNormal,
        };
        _controlThread.Start();

        Log.Info(_boundedSeconds > 0
            ? $"Running time-boxed: will exit on its own after {_boundedSeconds}s, or immediately on --kill."
            : "Running until --kill is signalled or the process is ended.");

        MessageLoop();
    }

    // ------------------------------------------------------------------ control channel

    /// <summary>
    /// A message-only window for cross-thread shutdown requests.
    /// PostQuitMessage only works on the owning thread, so other threads post here.
    /// </summary>
    void CreateControlWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = ControlWndProcThunk,
            hInstance = ModuleHandle,
            lpszClassName = ControlClassName,
        };
        if (RegisterClassExW(ref wc) == 0)
            throw new InvalidOperationException("RegisterClassEx(control) failed: " + Marshal.GetLastWin32Error());

        _controlWnd = CreateWindowExW(
            0, ControlClassName, "FlowMonitor.Control",
            0, 0, 0, 0, 0,
            new IntPtr(-3),   // HWND_MESSAGE - message-only, never enumerated by the shell
            IntPtr.Zero, ModuleHandle, IntPtr.Zero);
        if (_controlWnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx(control) failed: " + Marshal.GetLastWin32Error());

        _killEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, KillEventName);
        _newWidgetEvent = CreateEventW(IntPtr.Zero, bManualReset: true, bInitialState: false, NewWidgetEventName);
        Log.Info($"Control HWND = 0x{_controlWnd:X}; kill event {Describe(_killEvent)}, new-widget event {Describe(_newWidgetEvent)}");

        static string Describe(IntPtr h) => h == IntPtr.Zero ? "unavailable" : "0x" + h.ToString("X");
    }

    static readonly WndProcDelegate _controlWndProcDelegate = ControlWndProc;
    static IntPtr ControlWndProcThunk => _controlWndProcPtr != IntPtr.Zero
        ? _controlWndProcPtr
        : (_controlWndProcPtr = Marshal.GetFunctionPointerForDelegate(_controlWndProcDelegate));
    static IntPtr _controlWndProcPtr;

    static IntPtr ControlWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // WM_NCCREATE must return non-zero or creation aborts. Unhandled messages
        // go to DefWindowProcW.
        if (msg == WM_NCCREATE || msg == WM_CREATE)
            return new IntPtr(1);

        // WM_CLOSE posts quit; this window lives on the message-loop thread.
        if (msg == WM_CLOSE) { PostQuitMessage(0); return IntPtr.Zero; }

        if (msg == WM_APP_RETIRE)
        {
            // Render thread cannot destroy windows; queue here, tear down on UI thread.
            var host = _current;
            if (host is not null) host.DrainRetireQueue();
        }

        if (msg == WM_APP_NEW_WIDGET)
        {
            var host = _current;
            if (host is not null) host.SpawnWidgetFromRequest();
        }

        if (msg == WM_APP_DESTROY)
        {
            // GPU half already freed on the render thread; DestroyWindow runs here (creator thread).
            var host = _current;
            if (host is not null) host.DrainHwndDestroyQueue();
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Handle a "--new-widget" request: spawn one widget in edit mode, offset from existing ones.
    /// </summary>
    void SpawnWidgetFromRequest()
    {
        var cfg = NewDefaultConfig();
        lock (_widgets)
        {
            cfg.X += 28 * _widgets.Count;
            cfg.Y += 28 * _widgets.Count;
        }
        cfg.Id = Guid.NewGuid().ToString("N");
        if (AddWidget(cfg, editMode: true) is null)
            Log.Write("WARN", "--new-widget request refused: already at the widget cap.");
    }

    const uint WM_APP_RETIRE = 0x8001;

    /// <summary>Asks the UI thread to finish a teardown whose GPU half the render thread already did.</summary>
    const uint WM_APP_DESTROY = 0x8003;
    readonly List<WidgetWindow> _retireQueue = new();
    readonly object _retireGate = new();

    // ---- teardown ownership -------------------------------------------------------
    // _widgets / _byHandle / _renderFaults: UI thread removes immediately.
    // D2D context, bitmap, swap chain: render thread frees inside RenderFrame.
    // HWND: UI thread calls DestroyWindow last (creator-thread affinity).
    readonly List<WidgetWindow> _destroyQueue = new();
    readonly List<WidgetWindow> _hwndDestroyQueue = new();
    readonly object _destroyGate = new();

    /// <summary>Request that a widget be destroyed.Safe to call from the render thread.</summary>
    void QueueRetire(WidgetWindow widget)
    {
        lock (_retireGate) _retireQueue.Add(widget);
        if (_controlWnd != IntPtr.Zero) PostMessage(_controlWnd, WM_APP_RETIRE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Enqueue a widget for teardown. Safe from the UI thread. The GPU half happens on the
    /// render thread at the top of its loop; the HWND half comes back here afterwards.
    /// </summary>
    void QueueDestroy(WidgetWindow widget)
    {
        lock (_destroyGate) _destroyQueue.Add(widget);
    }

    /// <summary>
    /// RENDER THREAD. Frees the GPU half of queued widgets, then hands them to the UI thread.
    /// Runs at the top of the loop, before any frame body, so no frame is in flight.
    /// </summary>
    void DrainDestroyQueue()
    {
        WidgetWindow[] doomed;
        lock (_destroyGate)
        {
            if (_destroyQueue.Count == 0) return;
            doomed = _destroyQueue.ToArray();
            _destroyQueue.Clear();
        }
        foreach (var w in doomed) w.ReleaseGpuResources();
        lock (_destroyGate) _hwndDestroyQueue.AddRange(doomed);
        if (_controlWnd != IntPtr.Zero) PostMessage(_controlWnd, WM_APP_DESTROY, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>UI THREAD. Destroys the HWNDs whose GPU resources are already gone.</summary>
    void DrainHwndDestroyQueue()
    {
        WidgetWindow[] doomed;
        lock (_destroyGate)
        {
            if (_hwndDestroyQueue.Count == 0) return;
            doomed = _hwndDestroyQueue.ToArray();
            _hwndDestroyQueue.Clear();
        }
        foreach (var w in doomed) w.DestroyWindowOnly();
    }

    void DrainRetireQueue()
    {
        WidgetWindow[] doomed;
        lock (_retireGate)
        {
            if (_retireQueue.Count == 0) return;
            doomed = _retireQueue.ToArray();
            _retireQueue.Clear();
        }
        foreach (var w in doomed) CloseWidget(w, deleteSaved: false);
    }

    /// <summary>
    /// Watches the kill signal and time-box expiry, then winds down the message loop.
    /// Touches no widgets and renders nothing.
    /// </summary>
    void ControlLoop()
    {
        DateTime deadline = _boundedSeconds > 0
            ? DateTime.UtcNow.AddSeconds(_boundedSeconds)
            : DateTime.MaxValue;

        var killWait = _killEvent == IntPtr.Zero ? null : new RawWaitHandle(_killEvent);
        var newWidgetWait = _newWidgetEvent == IntPtr.Zero ? null : new RawWaitHandle(_newWidgetEvent);
        var signals = new List<WaitHandle>();
        if (killWait is not null) signals.Add(killWait);
        if (newWidgetWait is not null) signals.Add(newWidgetWait);
        signals.Add(_renderStop);
        WaitHandle[] waitSet = signals.ToArray();
        int killIndex = killWait is not null ? 0 : -1;
        int newWidgetIndex = newWidgetWait is not null ? (killIndex + 1) : -1;

        while (_running)
        {
            bool expired = DateTime.UtcNow >= deadline;

            if (killWait is not null || newWidgetWait is not null)
            {
                int slice = expired ? 1 : 250;
                int r = WaitHandle.WaitAny(waitSet, slice);

                if (r == newWidgetIndex)
                {
                    // Reset so bursts produce one widget each; creation runs on the UI thread.
                    ResetEvent(_newWidgetEvent);
                    PostMessage(_controlWnd, WM_APP_NEW_WIDGET, IntPtr.Zero, IntPtr.Zero);
                }
                else if (r == killIndex)
                {
                    Log.Info("Kill signal received; shutting down.");
                    break;
                }
            }
            else if (expired)
            {
                Log.Info($"Time box expired after {_boundedSeconds}s; shutting down.");
                break;
            }
            else if (_renderStop.WaitOne(200))
            {
                return;                 // host is already tearing down
            }
        }

        if (_running && _controlWnd != IntPtr.Zero) PostMessage(_controlWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    const uint WM_APP_NEW_WIDGET = 0x8002;

    static WidgetConfig NewDefaultConfig() => new()
    {
        X = 120, Y = 120, Width = 460, Height = 200,
        Graph = GraphKind.Cpu,
    };

    WidgetWindow AddWidget(WidgetConfig cfg, bool editMode)
    {
        // Hard cap: each widget owns a swap chain plus a per-frame D2D pass.
        // Refuse beyond the cap rather than throttle.
        lock (_widgets)
        {
            if (_widgets.Count >= MaxWidgets)
            {
                Log.Write("WARN", $"AddWidget refused: already at the {MaxWidgets} widget cap ({cfg.Graph}).");
                return null!;
            }
        }

        var widget = new WidgetWindow(this, cfg);
        widget.Create(_workerW);
        if (editMode) widget.BeginEdit();
        lock (_widgets) { _widgets.Add(widget); _byHandle[widget.Handle] = widget; }
        WidgetStore.Save(cfg);
        Log.Info("Widget HWND = 0x" + widget.Handle.ToString("X") + $" ({cfg.Graph}, restore={!editMode}, total={_widgets.Count}/{MaxWidgets})");
        return widget;
    }

    void RegisterClasses()
    {
        if (!EnsureWidgetClassRegistered())
            throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());
    }

    /// <summary>
    /// Register the widget window class unless it already exists.
    /// Static and idempotent so the self test can create a real widget window without
    /// booting a whole host. ERROR_CLASS_ALREADY_EXISTS counts as success.
    /// </summary>
    public static bool EnsureWidgetClassRegistered()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            style = CS_DBLCLKS,
            lpfnWndProc = WndProcThunk,
            hInstance = ModuleHandle,
            lpszClassName = WidgetClassName,
            // Class cursor required: without one Windows resets the cursor on every mouse move.
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
        };
        if (RegisterClassExW(ref wc) != 0) return true;
        const int ErrorClassAlreadyExists = 0x000005DD;
        return Marshal.GetLastWin32Error() == ErrorClassAlreadyExists;
    }

    // ------------------------------------------------------------------ message loop

    void MessageLoop()
    {
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_QUIT) break;
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    // ------------------------------------------------------------------ wndproc

    static readonly WndProcDelegate _wndProcDelegate = WndProc;
    static IntPtr WndProcThunk => _wndProcPtr != IntPtr.Zero
        ? _wndProcPtr
        : (_wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    static IntPtr _wndProcPtr;

    static DesktopHost? _current;

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var host = _current;
        if (host is not null && host._byHandle.TryGetValue(hwnd, out var widget))
        {
            if (host.HandleMessage(widget, msg, wParam, lParam)) return IntPtr.Zero;
        }

        if (msg == WM_MOUSEACTIVATE) return new IntPtr(MA_NOACTIVATE);

        if (host is null) return DefWindowProcW(hwnd, msg, wParam, lParam);

        return host.HandleMessageCore(hwnd, msg, wParam, lParam);
    }

    bool HandleMessage(WidgetWindow w, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                // Cursor shape follows the live position; handling only WM_MOUSEMOVE loses it when idle.
                w.OnSetCursor(LoWord(lParam), HiWord(lParam));
                return true;
            case WM_MOUSEMOVE:
                w.OnMouseMove(LoWord(lParam), HiWord(lParam));
                return true;
            case WM_LBUTTONDOWN:
                w.OnLeftButtonDown(LoWord(lParam), HiWord(lParam));
                return true;
            case WM_LBUTTONUP:
                w.OnLeftButtonUp();
                return true;
            case WM_RBUTTONUP:
                w.OnRightButtonUp(LoWord(lParam), HiWord(lParam));
                return true;
            case WM_MOUSELEAVE:
                w.OnMouseLeave();
                return true;
            case WM_SIZE:
                w.ResizeTo(LoWord(lParam), HiWord(lParam));
                return true;
        }
        return false;
    }

    IntPtr HandleMessageCore(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:
                ScheduleWorkerWCheck();
                return IntPtr.Zero;
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // Mouse coordinates are packed 16-bit fields and must be sign-extended
    // (GET_X_LPARAM / GET_Y_LPARAM pattern); masking without sign-extension breaks negatives.
    static int LoWord(IntPtr v) => (short)(v.ToInt64() & 0xFFFF);
    static int HiWord(IntPtr v) => (short)((v.ToInt64() >> 16) & 0xFFFF);

    // ------------------------------------------------------------------ render loop

    /// <summary>Wraps a raw Win32 HANDLE so it can be waited on with <see cref="WaitHandle.WaitAny"/>.</summary>
    sealed class RawWaitHandle : WaitHandle
    {
        public RawWaitHandle(IntPtr handle)
            => SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(handle, false);
    }

    void RenderLoop()
    {
        // Paced off a high-resolution waitable timer.
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        WaitHandle[]? handles = timer == IntPtr.Zero
            ? null
            : new WaitHandle[] { new RawWaitHandle(timer), _renderStop };
        const int WAIT_OBJECT_0 = 0;

        double last = Now();
        long counter = 0;

        while (_running)
        {
            counter++;
            if ((counter & 63) == 0) CheckWorkerW();

            // Sleep until the next frame boundary; identical frames are skipped in RenderFrame.
            double now = Now();
            double waitSec = (last + _frameInterval * _backoffFactor) - now;

            if (handles != null)
            {
                if (waitSec > 0.0002)
                {
                    long due = (long)(waitSec * 10_000_000.0);   // 100 ns units, relative
                    if (due < 1) due = 1;
                    if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                    {
                        int r = WaitHandle.WaitAny(handles, (int)(waitSec * 1000.0) + 250);
                        if (r != WAIT_OBJECT_0) break;           // stop signalled
                    }
                    else if (_renderStop.WaitOne((int)(waitSec * 1000.0) + 1)) break;
                }
            }
            else if (waitSec > 0.0002)
            {
                // No high-res timer available: coarse sleep is still far better than spinning.
                if (_renderStop.WaitOne(waitSec > 0.008 ? 8 : (int)(waitSec * 1000.0) + 1)) break;
            }
            else
            {
                // Already behind schedule - do not accumulate debt.
                last = now;
            }

            double t = Now();
            float dt = (float)Math.Clamp(t - last, 0.0, 0.25);
            last = t;

            double workStart = t;
            double budget = _frameInterval * _backoffFactor;

            try
            {
                // Teardown at the top of the loop, where no frame body is in flight.
                DrainDestroyQueue();

                // While the modal menu is open it owns the GPU; skip frames so no
                // RenderFrame / Present / Commit overlaps a menu paint.
                if (_menuOpen)
                {
                    Thread.Sleep(8);
                    continue;
                }

                FlushPendingGeometry();

                WidgetWindow[] snapshot;
                lock (_widgets) snapshot = _widgets.ToArray();

                bool any = false;
                List<WidgetWindow>? doomed = null;
                // Collect this frame's faults, then classify: a fault is widget-local only
                // if the other widgets still render fine; multi-widget faults are device-wide.
                var frameFaults = new List<WidgetWindow>();
                string? frameFaultMessage = null;
                foreach (var w in snapshot)
                {
                    if (!w.IsVisible) continue;
                    try
                    {
                        if (w.RenderFrame(_telemetry.Time, dt)) any = true;
                        _renderFaults.NoteSuccess(w);
                    }
                    catch (Exception ex)
                    {
                        // One bad widget must not stop the others; consecutive faults retire it.
                        frameFaults.Add(w);
                        frameFaultMessage ??= ex.Message;
                    }
                }

                // Classify once per frame: multi-widget faults are device-wide, never retired.
                if (frameFaults.Count > 0)
                {
                    bool deviceWide = frameFaults.Count > 1;
                    if (deviceWide)
                    {
                        if (!_deviceLostLogged)
                        {
                            _deviceLostLogged = true;
                            Log.Write("ERROR", $"{frameFaults.Count} widgets faulted on the SAME frame - " +
                                "device-wide, not per-widget. Nothing is being retired for it. " +
                                "First message: " + frameFaultMessage +
                                ". Suspect: the ResizeBuffers/present race; see WidgetSurface.ApplyPendingResize.");
                        }
                        foreach (var fw in frameFaults) _renderFaults.Forget(fw);
                    }
                    else
                    {
                        _deviceLostLogged = false;
                        foreach (var fw in frameFaults)
                        {
                            int n = _renderFaults.NoteFault(fw);
                            if (_renderFaults.ShouldLog(n))
                                Log.Write("ERROR", $"render fault on widget 0x{fw.Handle:X} (fault {n}): " +
                                    "widget-local, siblings are fine. " + frameFaultMessage);
                            if (_renderFaults.ShouldRetire(fw, n)) (doomed ??= new()).Add(fw);
                        }
                    }
                }
                else
                {
                    _deviceLostLogged = false;
                }

                if (any)
                {
                    foreach (var w in snapshot) w.ApplyOpacity();
                    Device.CompositionDevice.Commit();
                }

                if (doomed is not null)
                {
                    foreach (var w in doomed)
                    {
                        Log.Write("ERROR", $"Retiring widget 0x{w.Handle:X}: it failed {RenderFaultsBeforeRetire} consecutive frames. " +
                                           "The desktop is unaffected; the other widgets keep running.");
                        QueueRetire(w);   // teardown happens on the UI thread, not here
                    }
                }
            }
            catch (Exception ex)
            {
                // Loop-level failure: log the first few, keep the loop alive.
                if (_renderErrors++ < 5) Log.Write("ERROR", "render loop: " + ex);
            }

            // Duty-cycle governor: stretch the frame interval when over budget, relax when light.
            double workSec = Now() - workStart;
            if (workSec > _worstFrameSec) _worstFrameSec = workSec;
            _dutyEma = _dutyEma * 0.90 + (budget > 0 ? Math.Clamp(workSec / budget, 0.0, 4.0) : 0.0) * 0.10;

            if (++_dutySamples >= DutySamplesBeforeActing)
            {
                _dutySamples = 0;
                double before = _backoffFactor;
                if (_dutyEma > DutyBackoffHigh)
                    _backoffFactor = Math.Min(_backoffFactor * 1.5, DutyBackoffMax);
                else if (_dutyEma < DutyBackoffLow)
                    _backoffFactor = Math.Max(_backoffFactor / 1.25, 1.0);

                if (Math.Abs(_backoffFactor - before) > 0.01)
                    Log.Write("INFO", $"governor: duty {_dutyEma:P0} worst {_worstFrameSec * 1000:F1}ms -> backoff {before:F2}x to {_backoffFactor:F2}x " +
                                      $"(now {_frameInterval * _backoffFactor * 1000:F1}ms/frame, {_widgets.Count} widget(s))");
                if (_backoffFactor <= 1.0) _worstFrameSec = 0.0;
            }
        }

        if (handles != null)
        {
            if (timer != IntPtr.Zero) CancelWaitableTimer(timer);
            foreach (var h in handles) h.Dispose();
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    readonly ManualResetEvent _renderStop = new(false);

    int _renderErrors;

    /// <summary>Latched while a device-wide fault is active so it logs once, not per widget per frame.</summary>
    volatile bool _deviceLostLogged;

    // Duty-cycle governor: back off the frame interval when over budget, relax when light.
    const double DutyBackoffHigh = 0.50;   // above this fraction of the budget, slow down
    const double DutyBackoffLow = 0.22;    // below this, we are comfortably ahead
    const double DutyBackoffMax = 8.0;     // never stretch beyond 8x the display interval
    const int DutySamplesBeforeActing = 64;

    double _dutyEma;                        // 0..1+, fraction of budget consumed
    double _backoffFactor = 1.0;
    int _dutySamples;
    double _worstFrameSec;

    // Hard cap on live widgets: each widget owns a swap chain plus a per-frame D2D pass.
    public const int MaxWidgets = 16;

    /// <summary>Consecutive per-widget render failures before that widget is retired.</summary>
    public const int RenderFaultsBeforeRetire = 30;

    internal void FlushPendingGeometry()
    {
        lock (_pendingGate)
        {
            if (_pendingGeometry.Count == 0) return;
            foreach (var (w, x, y, width, height) in _pendingGeometry)
            {
                if (width == 0 && height == 0) w.MoveTo(x, y);
                else w.ResizeTo(width, height);
                // A drag or resize the user never applied must still survive a restart.
                WidgetStore.Save(w.Config);
            }
            _pendingGeometry.Clear();
        }
    }

    static double Now()
    {
        QueryPerformanceCounter(out long c);
        return c / (double)QueryFrequency();
    }

    static long _freq;
    static long QueryFrequency()
    {
        if (_freq == 0) QueryPerformanceFrequency(out _freq);
        return _freq;
    }

    // ------------------------------------------------------------------ workerw watchdog

    void ScheduleWorkerWCheck() => Volatile.Write(ref _nextWorkerWCheck, 1);

    void CheckWorkerW()
    {
        if (!_running) return;

        // Re-entrancy guard: Explorer restarts broadcast fresh WorkerW handles.
        if (_workerWBusy) return;
        _workerWBusy = true;
        try
        {
            var current = Desktop.FindWorkerW();
            if (current == IntPtr.Zero) return;

            // Idempotent by handle comparison: same WorkerW does nothing.
            if (current == _workerW) return;

            if (!Desktop.IsDesktopLayer(current))
            {
                Log.Write("WARN", $"Found window 0x{current:X} but it is not a desktop layer; ignoring.");
                return;
            }

            Log.Info($"WorkerW changed 0x{_workerW:X} -> 0x{current:X}; re-parenting {Widgets.Count} widget(s).");
            _workerW = current;

            WidgetWindow[] snapshot;
            lock (_widgets) snapshot = _widgets.ToArray();
            foreach (var w in snapshot) w.Reparent(_workerW);

            Device.CompositionDevice.Commit();
        }
        finally
        {
            _workerWBusy = false;
        }
    }

    // ------------------------------------------------------------------ IRenderHost

    public void OnWidgetConfigChanged(WidgetWindow widget, bool livePreview)
    {
        var cfg = widget.Config;

        // Sampling cadence is process-wide; the tightest visible request wins.
        lock (_widgets)
        {
            int fastest = 0;
            foreach (var w in _widgets)
                if (w.IsVisible && w.Config.UpdateIntervalMs < fastest) fastest = w.Config.UpdateIntervalMs;
            if (fastest == 0) fastest = cfg.UpdateIntervalMs;
            _telemetry.IntervalMs = fastest;
        }

        if (!livePreview) WidgetStore.Save(cfg);
    }

    public ChartModel BuildChart(WidgetConfig cfg, double now)
    {
        ChartModel model = BuildChartCore(cfg, now);
        // Immutable slot snapshots under the telemetry lock; the renderer never touches
        // the live rings, so a sample landing mid-frame cannot tear a read.
        _telemetry.Read(() =>
        {
            model.SampleIntervalSec = _telemetry.SampleIntervalSec;
            // Visible point count follows MC's performance-page-data-points (10-600).
            model.VisiblePoints = Math.Clamp(cfg.DataPoints, 10, ChartModel.FixedSlots);
            double newest = 0;
            foreach (var series in model.Series)
            {
                series.Snapshot = series.Data.SnapshotLatest(ChartModel.FixedSlots);
                double t = series.Data.NewestTime;
                if (t > newest) newest = t;
            }
            model.LastSampleTime = newest;
        });
        return model;
    }

    ChartModel BuildChartCore(WidgetConfig cfg, double now)
    {
        var accent = ParseColor(cfg.AccentHex, new Color4(0.298f, 0.761f, 1f, 1f));

        switch (cfg.Graph)
        {
            case GraphKind.Cpu:
            {
                var cpu = _telemetry.Cpu;
                float latest = 0;
                _telemetry.Read(() => latest = cpu.Total.Latest);
                double window = Math.Max(2, cfg.WindowSeconds);
                _telemetry.Read(() =>
                {
                    _min = cpu.Total.Min(now - window);
                    _max = cpu.Total.Peak(now - window);
                    _avg = Average(cpu.Total, now - window);
                });

                return new ChartModel
                {
                    Title = "CPU",
                    Subtitle = $"{cpu.CoreCount} logical processors",
                    Series = [new ChartSeries { Name = "CPU", Data = cpu.Total, Color = accent, Unit = "%" }],
                    AxisMax = 100,
                    PercentAxis = true,
                    ValueText = latest.ToString("0.0", CultureInfo.InvariantCulture),
                    ValueUnit = "%",
                    MinMaxText = $"Min {_min:0.0}%    Avg {_avg:0.0}%    Max {_max:0.0}%",
                    WindowSeconds = window,
                };
            }

            case GraphKind.CpuCores:
            {
                var cpu = _telemetry.Cpu;
                var list = new List<ChartSeries>();
                for (int i = 0; i < cpu.Cores.Length; i++)
                {
                    list.Add(new ChartSeries
                    {
                        Name = "Core " + i,
                        Data = cpu.Cores[i],
                        Color = new Color4(accent.R, accent.G, accent.B, 0.55f),
                        Secondary = true,
                    });
                }
                float latest = 0;
                _telemetry.Read(() => latest = cpu.Total.Latest);
                return new ChartModel
                {
                    Title = "CPU",
                    Subtitle = "per logical processor",
                    Series = list,
                    AxisMax = 100,
                    PercentAxis = true,
                    ValueText = latest.ToString("0.0", CultureInfo.InvariantCulture),
                    ValueUnit = "%",
                    WindowSeconds = Math.Max(2, cfg.WindowSeconds),
                };
            }

            case GraphKind.Memory:
            {
                var mem = _telemetry.Memory;
                long total = 0, inUse = 0, cached = 0, committed = 0;
                _telemetry.Read(() =>
                {
                    total = mem.TotalPhysical;
                    inUse = mem.LastInUse;
                    cached = mem.LastCached;
                    committed = mem.LastCommitted;
                });
                if (total <= 0) total = 1;

                // MC parity (performance_page/memory.rs): one filled 'in use' area plus
                // unfilled companion lines, all on the fixed mem_total scale. Not stacked:
                // stacked bands read as unrelated layers instead of one memory picture.
                return new ChartModel
                {
                    Title = "RAM",
                    Subtitle = $"{ChartRenderer.FormatBytes(committed)} committed",
                    Series =
                    [
                        new ChartSeries { Name = "In use", Data = mem.InUse, Color = accent },
                        new ChartSeries { Name = "Cached", Data = mem.Cached, Color = new Color4(0.36f, 0.62f, 0.85f, 1f), Fill = false, Secondary = true },
                        new ChartSeries { Name = "Committed", Data = mem.Committed, Color = new Color4(0.62f, 0.72f, 0.84f, 1f), Fill = false, Secondary = true, Dashed = true },
                    ],
                    AxisMax = total,
                    // Byte axis with labels: values are bytes, not 0-100.
                    PercentAxis = false,
                    ValueText = ChartRenderer.FormatBytes(inUse),
                    ValueUnit = "",
                    WindowSeconds = Math.Max(2, cfg.WindowSeconds),
                };
            }

            case GraphKind.Disk:
            {
                var disk = _telemetry.Disk;
                if (disk.Unavailable)
                {
                    return new ChartModel
                    {
                        Title = "Disk",
                        Subtitle = disk.UnavailableReason ?? "counters unavailable",
                        Series = [],
                        AxisMax = 100,
                        ValueText = "--",
                    };
                }

                double window = Math.Max(2, cfg.WindowSeconds);
                float r = 0, w = 0, busy = 0;
                bool bytesDead = false;
                string? bytesReason = null;
                float dMin = 0, dAvg = 0, dMax = 0;
                _telemetry.Read(() =>
                {
                    r = disk.LastRead; w = disk.LastWrite; busy = disk.LastBusy;
                    bytesDead = disk.ByteRatesUnavailable;
                    bytesReason = disk.ByteRatesUnavailableReason;
                    // Both directions share one auto scale (the renderer's ByteAxisMax
                    // reads both series), like MC's connected read/write datasets.
                    dMin = disk.ActivePercent.Min(now - window);
                    dMax = disk.ActivePercent.Peak(now - window);
                    dAvg = dMax;
                });

                // Inert byte-rate counters: show % Disk Time only rather than false zero lines.
                if (bytesDead)
                {
                    return new ChartModel
                    {
                        Title = "Disk",
                        Subtitle = bytesReason is null
                            ? (busy > 0 ? $"{busy:0}% active time" : "0% active time")
                            : $"% Disk Time only - {bytesReason}",
                        Series =
                        [
                            new ChartSeries { Name = "Active time", Data = disk.ActivePercent, Color = new Color4(0.36f, 0.66f, 0.86f, 1f) },
                        ],
                        AxisMax = 100,
                        PercentAxis = true,
                        ValueText = busy.ToString("0"),
                        ValueUnit = "%",
                        MinMaxText = $"Min {dMin:0.0}%    Max {dMax:0.0}%",
                        WindowSeconds = window,
                    };
                }

                return new ChartModel
                {
                    Title = "Disk",
                    Subtitle = busy > 0 ? $"{busy:0}% active time" : "0% active time",
                    Series =
                    [
                        new ChartSeries { Name = "Read", Data = disk.ReadBytes, Color = new Color4(0.36f, 0.66f, 0.86f, 1f) },
                        new ChartSeries { Name = "Write", Data = disk.WriteBytes, Color = new Color4(0.20f, 0.44f, 0.70f, 1f) },
                    ],
                    AutoRange = true,
                    PercentAxis = false,
                    ValueText = ChartRenderer.FormatBytes(r),
                    ValueUnit = "/s",
                    MinMaxText = $"Read {ChartRenderer.FormatBytes(r)}/s    Write {ChartRenderer.FormatBytes(w)}/s",
                    WindowSeconds = window,
                };
            }

            case GraphKind.Network:
            {
                var net = _telemetry.Network;
                double window = Math.Max(2, cfg.WindowSeconds);
                float tx = 0, rx = 0;
                string name = "Network", desc = "";
                _telemetry.Read(() =>
                {
                    tx = net.LastSend; rx = net.LastRecv;
                    if (net.Available) { name = net.InterfaceName; desc = net.InterfaceDescription; }
                });

                bool wireless = desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                             || desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                             || name.Contains("WLAN", StringComparison.OrdinalIgnoreCase);

                return new ChartModel
                {
                    Title = wireless ? "Wi-Fi" : "Ethernet",
                    Subtitle = name,
                    Series =
                    [
                        new ChartSeries { Name = "Send", Data = net.SendBytes, Color = new Color4(0.30f, 0.74f, 0.80f, 1f) },
                        new ChartSeries { Name = "Receive", Data = net.RecvBytes, Color = new Color4(0.24f, 0.55f, 0.86f, 1f) },
                    ],
                    AutoRange = true,
                    PercentAxis = false,
                    ValueText = ChartRenderer.FormatBytes(rx),
                    ValueUnit = "/s",
                    MinMaxText = $"Send {ChartRenderer.FormatBytes(tx)}/s    Receive {ChartRenderer.FormatBytes(rx)}/s",
                    WindowSeconds = window,
                };
            }

            case GraphKind.Gpu:
            {
                var gpu = _telemetry.Gpu;
                if (gpu.Unsupported)
                {
                    return new ChartModel
                    {
                        Title = "GPU",
                        Subtitle = gpu.UnsupportedReason ?? "counters unavailable",
                        Series = [],
                        AxisMax = 100,
                        ValueText = "--",
                    };
                }

                double window = Math.Max(2, cfg.WindowSeconds);
                float util = 0, committedPct = 0;
                string engines = "";
                _telemetry.Read(() =>
                {
                    util = (float)gpu.LastUtilization;
                    // No second axis: VRAM reported as a share of committed total.
                    if (gpu.LastCommitted > 0)
                        committedPct = (float)Math.Clamp(gpu.LastDedicatedUsed * 100.0 / gpu.LastCommitted, 0, 100);
                    var engineSnapshot = gpu.EngineSnapshot();
                    if (engineSnapshot.Length > 0)
                        engines = string.Join("  ", engineSnapshot.Select(e => $"{e.Name} {e.Percent:0}%"));
                });

                _telemetry.Read(() =>
                {
                    _min = gpu.Utilization.Min(now - window);
                    _max = gpu.Utilization.Peak(now - window);
                    _avg = Average(gpu.Utilization, now - window);
                });

                return new ChartModel
                {
                    Title = "GPU",
                    Subtitle = string.IsNullOrEmpty(engines) ? "engines" : engines,
                    Series = [new ChartSeries { Name = "GPU", Data = gpu.Utilization, Color = accent, Unit = "%" }],
                    AxisMax = 100,
                    PercentAxis = true,
                    ValueText = util.ToString("0.0", CultureInfo.InvariantCulture),
                    ValueUnit = "%",
                    MinMaxText = $"Min {_min:0.0}%    Avg {_avg:0.0}%    Max {_max:0.0}%",
                    WindowSeconds = window,
                };
            }

            case GraphKind.GpuCores:
            {
                var gpu = _telemetry.Gpu;
                if (gpu.Unsupported)
                {
                    return new ChartModel
                    {
                        Title = "GPU",
                        Subtitle = gpu.UnsupportedReason ?? "counters unavailable",
                        Series = [],
                        AxisMax = 100,
                        ValueText = "--",
                    };
                }

                // One curve per engine type, keyed by name; vanished engines stop being fed.
                var list = new List<ChartSeries>();
                _telemetry.Read(() =>
                {
                    int n = 0;
                    foreach (var e in gpu.EngineSnapshot())
                    {
                        var s = gpu.EngineSeries(e.Name);
                        if (s is null) continue;
                        list.Add(new ChartSeries
                        {
                            Name = e.Name,
                            Data = s,
                            Color = new Color4(accent.R, accent.G, accent.B, 0.55f),
                            Secondary = true,
                            Unit = "%",
                        });
                        n++;
                    }
                    if (n == 0) _gpuEngineCount = 0;
                    else _gpuEngineCount = n;
                });

                float util = 0;
                _telemetry.Read(() => util = (float)gpu.LastUtilization);

                return new ChartModel
                {
                    Title = "GPU",
                    Subtitle = _gpuEngineCount > 0 ? $"{_gpuEngineCount} engine type(s)" : "no engine activity",
                    Series = list,
                    AxisMax = 100,
                    PercentAxis = true,
                    ValueText = util.ToString("0.0", CultureInfo.InvariantCulture),
                    ValueUnit = "%",
                    WindowSeconds = Math.Max(2, cfg.WindowSeconds),
                };
            }

            case GraphKind.Vram:
            {
                var gpu = _telemetry.Gpu;
                if (gpu.Unsupported)
                {
                    return new ChartModel
                    {
                        Title = "VRAM",
                        Subtitle = "",
                        Series = [],
                        AxisMax = 100,
                        ValueText = "--",
                    };
                }

                long total = 0, used = 0;
                _telemetry.Read(() =>
                {
                    total = gpu.LastCommitted;
                    used = gpu.LastDedicatedUsed;
                });
                if (total <= 0) total = 1;

                return new ChartModel
                {
                    Title = "VRAM",
                    Subtitle = "",
                    Series = [new ChartSeries { Name = "VRAM", Data = gpu.DedicatedUsedBytes!, Color = accent }],
                    AxisMax = total,
                    AutoRange = true,
                    PercentAxis = false,
                    ValueText = ChartRenderer.FormatBytes(used),
                    ValueUnit = "",
                    WindowSeconds = Math.Max(2, cfg.WindowSeconds),
                };
            }

            default:
                return new ChartModel
                {
                    // A saved per-core config that is still waiting for samples must not
                    // be called "GPU": the metric is the card, and per-core is a Cpu view.
                    Title = cfg.Graph == GraphKind.GpuCores ? "CPU" : cfg.Graph.ToString(),
                    Subtitle = "",
                    Series = [],
                    AxisMax = 100,
                    ValueText = "--",
                    EmptyText = cfg.Graph == GraphKind.Fans
                        ? "no fan sensor on this machine"
                        : "waiting for the sampler",
                };
        }
    }

    double _min, _max, _avg;
    int _gpuEngineCount;

    static double Average(Graphs.TimeSeries s, double since)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < s.Count; i++)
            if (s.TimeAt(i) >= since) { sum += s.ValueAt(i); n++; }
        return n == 0 ? 0 : sum / n;
    }

    public static Color4 ParseColor(string? hex, Color4 fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            string h = hex.Trim();
            if (h.StartsWith('#')) h = h[1..];
            if (h.Length == 6) h += "FF";
            if (h.Length != 8) return fallback;
            uint v = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new Color4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, ((v >> 24) & 0xFF) / 255f);
        }
        catch { return fallback; }
    }

    public void OnWidgetGeometryChanged(WidgetWindow w, int x, int y, int width, int height)
    {
        lock (_pendingGate) _pendingGeometry.Add((w, x, y, width, height));
    }

    // Modal menu owns the GPU while open; render loop skips frames. Volatile for UI/render handoff.
    volatile bool _menuOpen;

    public void SetMenuOpen(bool open) => _menuOpen = open;

    public void ShowContextMenu(WidgetWindow widget, int x, int y) => M3ContextMenu.Show(this, widget, x, y);

    public void CloseWidget(WidgetWindow widget) => CloseWidget(widget, deleteSaved: true);

    public void CloseWidget(WidgetWindow widget, bool deleteSaved)
    {
        lock (_widgets) _widgets.Remove(widget);
        _byHandle.TryRemove(widget.Handle, out _);
        _renderFaults.Forget(widget);
        // Retired widgets keep saved config; only explicit close deletes it.
        if (deleteSaved) WidgetStore.Delete(widget.Config.Id);
        // Enqueued teardown: widget already left render-loop collections; GPU half waits for render thread.
        QueueDestroy(widget);
    }

    public IReadOnlyList<WidgetWindow> Widgets { get { lock (_widgets) return _widgets.ToArray(); } }

    public Telemetry Telemetry => _telemetry;

    /// <summary>
    /// One tick landed: request a redraw on every widget. Runs on the telemetry thread;
    /// RequestRedraw is a volatile write, so this never blocks sampling.
    /// </summary>
    void WakeWidgetsForNewSample()
    {
        WidgetWindow[] snapshot;
        lock (_widgets) snapshot = _widgets.ToArray();
        foreach (var w in snapshot) w.RequestRedraw();
    }

    /// <summary>Test seam: track (or untrack, with null) a widget so the host reaches it.</summary>
    internal void TrackWidgetForTest(WidgetWindow? w)
    {
        lock (_widgets) { if (w is null) return; _widgets.Add(w); }
    }

    /// <summary>Test seam: stop tracking a widget the test created and dropped.</summary>
    internal void ForgetWidgetForTest(WidgetWindow w)
    {
        lock (_widgets) _widgets.Remove(w);
    }

    public void Dispose()
    {
        _running = false;
        _renderStop.Set();
        // Join the control thread (parked in WaitAny on _renderStop), then the render thread.
        if (_controlThread is not null && _controlThread.IsAlive) _controlThread.Join(500);
        _renderThread?.Join(500);

        _telemetry.Sampled -= WakeWidgetsForNewSample;
        _telemetry.Dispose();
        // Close the control channel before graphics teardown.
        if (_controlWnd != IntPtr.Zero) { DestroyWindow(_controlWnd); _controlWnd = IntPtr.Zero; }
        if (_killEvent != IntPtr.Zero) { CloseHandle(_killEvent); _killEvent = IntPtr.Zero; }
        if (_newWidgetEvent != IntPtr.Zero) { CloseHandle(_newWidgetEvent); _newWidgetEvent = IntPtr.Zero; }

        foreach (var w in Widgets) w.Destroy();

        // Queued widgets never got a render-loop turn; full Destroy is safe here (single thread left).
        WidgetWindow[] stranded;
        lock (_destroyGate)
        {
            stranded = _destroyQueue.Concat(_hwndDestroyQueue).Distinct().ToArray();
            _destroyQueue.Clear();
            _hwndDestroyQueue.Clear();
        }
        foreach (var w in stranded) w.Destroy();

        Resources.Dispose();
        Device.Dispose();
    }
}

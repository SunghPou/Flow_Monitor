using System.Runtime.InteropServices;
using FlowMonitor.Graphs;
using FlowMonitor.Interop;
using FlowMonitor.Model;
using FlowMonitor.Rendering;
using FlowMonitor.Sampling;
using FlowMonitor.Widgets;
using Vortice.Direct2D1;
using Rect = Vortice.Mathematics.Rect;
using RectF = System.Drawing.RectangleF;
using V2 = System.Numerics.Vector2;
using static FlowMonitor.Interop.Native;

namespace FlowMonitor.Host;

/// <summary>
/// Bounded, headless exercise of the rendering path: device graph, hidden never-shown
/// top-level window, swap chain, a few hundred awkward frames, then teardown.
/// No desktop presence; the process exits on its own.
/// </summary>
public static class SelfTest
{
    const string SelftestClassName = "FlowMonitor.SelfTest.Wnd";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate IntPtr WndProcDelegate(IntPtr h, uint m, IntPtr w, IntPtr l);

    // Held in a static field so the GC cannot collect the delegate while user32 holds its
    // thunk; Native.WNDCLASSEX.lpfnWndProc is a raw IntPtr, not a typed delegate.
    static readonly WndProcDelegate SelfTestWndProc = DefWindowProc;

    public static int Run(string captureDir = "")
    {
        Log.Info("=== self test start ===");
        Log.Info("capture dir: " + (captureDir.Length > 0 ? captureDir : "(none - offscreen only)"));
        int failures = 0;
        RenderDevice? device = null;
        WidgetSurface? surface = null;
        var resources = default(ResourceCache);

        try
        {
            // ---- 1. device graph -------------------------------------------------
            device = new RenderDevice();
            Log.Info("device: D3D/D2D/DComp/DWrite created");

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_DBLCLKS,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SelfTestWndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = SelftestClassName,
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

            // Never shown, never activated: a pure render target host.
            IntPtr hwnd = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, SelftestClassName, "FlowMonitor SelfTest",
                unchecked((uint)WS_POPUP), 0, 0, 460, 200,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            Log.Info("hidden host hwnd = 0x" + hwnd.ToString("X") + " (never shown)");

            // ---- 2. surface + resources ----------------------------------------
            surface = new WidgetSurface(device, hwnd, 460, 200);
            resources = new ResourceCache(device);
            Log.Info("swap chain " + surface.Width + "x" + surface.Height + " + resource cache");

            // ---- 2b. colour picker sheet (--capture only) -----------------------
            // Rendered through the same Paint the picker window runs, once per theme.
            // It runs here, next to the first surface: a second composition target for
            // the hidden host hwnd is only accepted while the host is still pristine.
            if (captureDir.Length > 0)
            {
                // Its own hidden host hwnd: DComp serves one composition target per hwnd,
                // so the picker cannot share the main test surface's window.
                IntPtr pickHwnd = CreateWindowExW(
                    WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, SelftestClassName, "FlowMonitor PickerSheet",
                    unchecked((uint)WS_POPUP), 0, 0,
                    (int)ColorPickerLayout.CardW, (int)ColorPickerLayout.CardH,
                    IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
                try
                {
                    if (pickHwnd == IntPtr.Zero)
                        throw new InvalidOperationException("picker sheet hwnd: " + Marshal.GetLastWin32Error());
                    using var pick = new WidgetSurface(device, pickHwnd,
                        (int)ColorPickerLayout.CardW, (int)ColorPickerLayout.CardH);
                    foreach (bool light in new[] { false, true })
                    {
                        SystemTheme.OverrideLight = light;
                        // Flip model: the readback carries the last presented frame,
                        // so paint twice before capturing.
                        for (int f = 0; f < 2; f++)
                            ColorPickerWindow.PaintTo(pick, resources, 1f,
                                new Hsv(275, 0.62, 0.86), new TestRenderHost(device, resources));
                        pick.CaptureToBmp(device, System.IO.Path.Combine(captureDir,
                            light ? "31-picker-light.bmp" : "31-picker-dark.bmp"));
                        Log.Info($"captured picker ({(light ? "light" : "dark")} theme)");
                    }
                    // The shader's clip-space y runs up while bitmap rows run down; a
                    // sign error there mirrors the wheel and no mapping assert can see
                    // it, so the rendered pixels are checked: cyan top, red bottom.
                    var wheelPx = pick.CaptureToPixels(device);
                    int pw = pick.Width;
                    var cc = ColorPickerLayout.Center;
                    float ro = ColorPickerLayout.WheelR - 22f;
                    bool Probe(float qx, float qy, Func<byte, byte, byte, bool> ok)
                    {
                        int i = ((int)qy * pw + (int)qx) * 4;
                        return ok(wheelPx[i + 2], wheelPx[i + 1], wheelPx[i]);
                    }
                    bool topCyan = Probe(cc.X, cc.Y - ro, (r, g, b) => g > 150 && b > 150 && r < 120);
                    bool botRed = Probe(cc.X, cc.Y + ro, (r, g, b) => r > 150 && g < 120 && b < 120);
                    Check(topCyan && botRed, "rendered wheel puts cyan at the top and red at the bottom",
                        $"top ({cc.X:0},{cc.Y - ro:0}) cyan={topCyan}, bottom ({cc.X:0},{cc.Y + ro:0}) red={botRed}");
                }
                catch (Exception ex) { Log.Write("ERROR", "picker sheet: " + ex); }
                finally
                {
                    SystemTheme.OverrideLight = null;
                    if (pickHwnd != IntPtr.Zero) DestroyWindow(pickHwnd);
                }
            }

            // ---- 3. charts that hit every drawing path -------------------------
            var charts = new[]
            {
                ("sine + step", BuildSineModel(460, 200)),
                ("32 series (per-core layout)", BuildManySeriesModel(460, 200, 32)),
                ("stacked bands (memory)", BuildStackedModel(460, 200)),
                ("auto-range + bytes", BuildBytesModel(460, 200)),
                ("tiny 80x60", BuildSineModel(80, 60)),
            };

            // Fill series with real history; seed index is per-chart so captures differ.
            for (int ci = 0; ci < charts.Length; ci++)
            {
                var m = charts[ci].Item2;
                if (m.Stacked)
                {
                    // Byte-valued bands: In use, Cached, Available summing to roughly the 32GB
                    // axis max, so the stack actually fills the plot.
                    double[] gib = { 14.0, 8.0, 10.0 };
                    for (int si = 0; si < m.Series.Count && si < gib.Length; si++)
                        SeedBytes(m.Series[si].Data, SeedCount, TestNow + Headroom, si, gib[si]);
                }
                else
                {
                    for (int si = 0; si < m.Series.Count; si++)
                        Seed(m.Series[si].Data, SeedCount, TestNow + Headroom, ci * 100 + si);
                }
                Snap(m);
            }

            // Snapshot fills slots right-aligned; short histories grow from the right.
            {
                var probe = charts[1].Item2.Series[0];
                int filled = probe.Snapshot.Count(v => !float.IsNaN(v));
                Log.Info($"per-core series[0]: {filled}/{probe.Snapshot.Length} slots filled");
            }

            // ---- 3. staged captures -------------------------------------------
            // Incremental stages so a failure names the broken capability, not just "blank".
            int stage = 0;
            void Capture(string name, Action<ID2D1DeviceContext> paint)
            {
                stage++;
                string file = captureDir.Length > 0
                    ? System.IO.Path.Combine(captureDir, $"{stage:D2}-{name}.bmp")
                    : "";
                surface.BeginDraw(96f);
                surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                paint(surface.Context);
                // Read before Present: after Present the flip-model index moves.
                surface.EndDrawOnly();
                if (file.Length > 0)
                {
                    surface.CaptureToBmp(device, file);
                }
                surface.Present();
                surface.Commit(device);
                if (file.Length > 0)
                {
                    Log.Info($"stage {stage} '{name}' -> {file} ({new System.IO.FileInfo(file).Length} bytes)");
                }
                else Log.Info($"stage {stage} '{name}' ok (no capture dir)");
            }

            var accent = new Vortice.Mathematics.Color4(0.298f, 0.761f, 1f, 1f);
            var sine = BuildSineModel(surface.Width, surface.Height);
            Seed(sine.Series[0].Data, SeedCount, TestNow + Headroom);
            Snap(sine);
            var chart = new ChartRenderer(device, resources);

            Capture("clear-only", _ => { });

            Capture("card-background", dc => WidgetPainter.PaintBackground(dc, resources,
                new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                hover: 0f, checkAmount: 0f, editing: false));

            Capture("one-series", dc => chart.Draw(dc, new WidgetConfig(), sine, 96f, TestNow, surface.Width, surface.Height));

            Capture("text-only", dc =>
            {
                var fmt = resources.Format("Segoe UI Variable Display", 21f,
                    Vortice.DirectWrite.FontWeight.SemiBold);
                dc.DrawText("42.3", fmt, new Rect(20, 8, 120, 32), resources.Brush(new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f)));
                dc.DrawText("Segoe UI Variable Text", resources.Body, new Rect(20, 44, 240, 20),
                    resources.Brush(new Vortice.Mathematics.Color4(0.8f, 0.85f, 0.9f, 1f)));
            });

            Capture("full-chart", dc =>
            {
                WidgetPainter.PaintBackground(dc, resources,
                    new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                    hover: 0f, checkAmount: 0f, editing: false);
                chart.Draw(dc, new WidgetConfig(), sine, 96f, TestNow, surface.Width, surface.Height);
            });

            Capture("checkmark-editmode", dc =>
            {
                WidgetPainter.PaintBackground(dc, resources,
                    new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                    hover: 0f, checkAmount: 1f, editing: true);
                WidgetPainter.PaintCheckMark(dc, resources,
                    new WidgetConfig { CornerRadius = 8 }, surface.Width, surface.Height,
                    HeaderLayout.BadgeRects(460f, 0f).Check, 1f);
            });

            // ---- 3b. SLOT-STABILITY PROOF ------------------------------------
            // x is the snapshot slot, never the clock: the same snapshot renders identically
            // at any clock, and one new sample shifts history by exactly one slot.
            byte[] GrabModel(ChartModel model, double now, string path, WidgetConfig? cfg = null)
            {
                surface.BeginDraw(96f);
                surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                WidgetPainter.PaintBackground(surface.Context, resources,
                    new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                    hover: 0f, checkAmount: 0f, editing: false);
                chart.Draw(surface.Context, cfg ?? new WidgetConfig(), model, 96f, now, surface.Width, surface.Height);
                surface.EndDrawOnly();
                if (path.Length > 0) surface.CaptureToBmp(device, path);
                var px = surface.CaptureToPixels(device);
                surface.Present();
                surface.Commit(device);
                return px;
            }

            byte[] Grab(double now, string path) => GrabModel(sine, now, path);

            /// <summary>Renders a frame with the edit badges painted at hover brightness.</summary>
            byte[] GrabBadges(ChartModel model, HeaderLayout badges)
            {
                surface.BeginDraw(96f);
                surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                var cfg = new WidgetConfig();
                WidgetPainter.PaintBackground(surface.Context, resources, cfg, surface.Width,
                    surface.Height, hover: 0f, checkAmount: 1f, editing: true);
                chart.Draw(surface.Context, cfg, model, 96f, TestNow, surface.Width, surface.Height);
                WidgetPainter.PaintCloseButton(surface.Context, resources, cfg, surface.Width,
                    surface.Height, badges.Close, 1f, hover: 1f);
                WidgetPainter.PaintCheckMark(surface.Context, resources, cfg, surface.Width,
                    surface.Height, badges.Check, 1f, hover: 1f);
                surface.EndDrawOnly();
                var px = surface.CaptureToPixels(device);
                surface.Present();
                surface.Commit(device);
                return px;
            }

            void Check(bool ok, string label, string detail)
            {
                Log.Info($"  [{(ok ? "PASS" : "FAIL")}] {label}: {detail}");
                if (!ok) failures++;
            }

            string Cap(string name) => captureDir.Length > 0
                ? System.IO.Path.Combine(captureDir, name + ".bmp")
                : "";

            var pxA = Grab(TestNow, Cap("07-slots-t0"));
            // One tick: a single new sample lands, then the snapshot refreshes.
            sine.Series[0].Data.Add(TestNow + Headroom + SeedStep, 50f);
            Snap(sine);
            var pxB = Grab(TestNow + 0.5, Cap("08-slots-one-tick"));
            var pxC = Grab(TestNow, Cap("07-slots-repeat"));

            int accentRows = CountAccentRows(pxA, surface.Width, surface.Height);
            Check(accentRows > 20, "curve is not flat-held",
                $"{accentRows} distinct accent rows (a flat line would be 1-2)");

            bool moved = !pxA.AsSpan().SequenceEqual(pxB);
            Check(moved, "a new sample shifts the curve",
                moved
                    ? "one tick changed the pixels, so history moves per sample"
                    : "pixels identical after a new sample -- the snapshot is not refreshing");

            // A lone spike is unambiguous: one tick must move its column left by exactly
            // one full slot (~7.4px at 460px wide with 60 MC-default points), static
            // geometry, no enter offset (sliding off by default).
            double slotW = (surface.Width - 24.0) / (60 - 1);
            {
                var spiked = BuildSineModel(surface.Width, surface.Height);
                double t0 = TestNow - 60.0;
                for (int i = 0; i < 60; i++)
                    spiked.Series[0].Data.Add(t0 + i, i == 20 ? 95f : 30f);
                Snap(spiked);
                // Render at the snapshot's own sample time (slide progress 0), so the
                // columns compared are pure slot positions with no sliding offset.
                int peak1 = FindPeakColumn(GrabModel(spiked, spiked.LastSampleTime, Cap("08-spike-t0")),
                    surface.Width, surface.Height);
                spiked.Series[0].Data.Add(t0 + 60, 30f);
                Snap(spiked);
                int peak2 = FindPeakColumn(GrabModel(spiked, spiked.LastSampleTime, Cap("08-spike-one-tick")),
                    surface.Width, surface.Height);
                Check(peak1 >= 0 && peak2 >= 0 && Math.Abs((peak1 - peak2) - slotW) <= 1.5,
                    "one tick moves a peak by exactly one full slot",
                    $"spike column {peak1} -> {peak2} (slot width {slotW:0.00}px; drift would miss by more)");
            }

            bool stable = pxB.AsSpan().SequenceEqual(pxC);
            Check(stable, "render is deterministic and clock-independent",
                stable
                    ? "same snapshot at different clocks produced byte-identical pixels"
                    : "same snapshot produced different pixels -- the clock is leaking into x");

            // NaN slots render as a gap, not a bridge.
            {
                var gapped = BuildSineModel(surface.Width, surface.Height);
                Seed(gapped.Series[0].Data, SeedCount, TestNow + Headroom);
                Snap(gapped);
                var snap = gapped.Series[0].Snapshot;
                for (int i = 20; i < 30 && i < snap.Length; i++) snap[i] = float.NaN;
                byte[] pxGap = GrabModel(gapped, TestNow, Cap("09-nan-gap"));
                // Static geometry: index i sits at right-i*slotW, so probe inside
                // the NaN span (indices 20..29) with margin.
                double right = surface.Width - 12.0;
                int xLo = (int)(right - 28 * slotW), xHi = (int)(right - 22 * slotW);
                int inGap = CountAccentInX(pxGap, surface.Width, surface.Height, xLo, xHi, 50, surface.Height - 12);
                Check(inGap == 0, "NaN slots render as a gap",
                    $"{inGap} accent pixels inside the NaN span x=[{xLo}..{xHi}] (a bridge would paint many)");
            }

            // The curve passes through its samples: accent near each probed slot point.
            {
                int hits = 0, probes = 0;
                var snap = sine.Series[0].Snapshot;
                double right = surface.Width - 12.0, bottom = surface.Height - 12.0, ph = bottom - 50.0;
                // Only drawn slots can carry accent: bound by VisiblePoints, not the ring.
                int drawn = Math.Min(snap.Length, sine.VisiblePoints);
                for (int i = 2; i < drawn; i += 7)
                {
                    float v = snap[i];
                    if (float.IsNaN(v)) continue;
                    probes++;
                    // Static geometry (sliding off): slot i sits at right-i*slotW.
                    int x = (int)(right - i * slotW);
                    int y = (int)(bottom - ph * Math.Clamp(v / 100.0, 0, 1.06));
                    if (AccentNear(pxB, surface.Width, surface.Height, x, y, 2, 4)) hits++;
                }
                Check(probes > 0 && hits == probes, "smoothed curve passes through every sample",
                    $"{hits}/{probes} probed slots have accent within tolerance (misses mean overshoot or drift)");
            }

            // Fill drops from the run's own end x-values, never from the plot edge.
            // Anchoring at plot.Left drew a cross-plot diagonal whose self-intersection
            // painted fill lenses above the curve.
            {
                var plot = new RectF(12f, 50f, 436f, 138f);
                var run = new[] { new V2(448f, 100f), new V2(300f, 170f), new V2(150f, 120f) };
                var poly = ChartRenderer.AreaFillPolygon(run, plot);
                Check(poly.Length == run.Length + 2
                        && poly[0].X == run[0].X && poly[0].Y == plot.Bottom
                        && poly[^1].X == run[^1].X && poly[^1].Y == plot.Bottom,
                    "area fill drops from the curve ends, not the plot edge",
                    $"polygon x: first={poly[0].X} (run starts {run[0].X}), last={poly[^1].X} (run ends {run[^1].X}); plot.Left={plot.Left}");
            }

            // Sliding (opt-in, MC performance-sliding-graphs): same snapshot at
            // different intra-tick phases moves; the same phase repeats exactly.
            {
                var slideCfg = new WidgetConfig { SlidingGraphs = true };
                Snap(sine);
                sine.LastSampleTime = TestNow;
                sine.SampleIntervalSec = 1.0;
                byte[] s1 = GrabModel(sine, TestNow + 0.25, Cap("10-slide-a"), slideCfg);
                byte[] s1b = GrabModel(sine, TestNow + 0.25, "", slideCfg);
                byte[] s2 = GrabModel(sine, TestNow + 0.75, Cap("10-slide-b"), slideCfg);
                Check(s1.AsSpan().SequenceEqual(s1b), "sliding render is deterministic",
                    "same snapshot at the same intra-tick phase produced byte-identical pixels");
                Check(!s1.AsSpan().SequenceEqual(s2), "curve slides continuously between ticks",
                    "same snapshot at two phases differed -- with no slide it would step once per second");
            }

            // Tick continuity with sliding on: index k at progress 1 sits exactly
            // where index k+1 lands at progress 0, so the tick itself cannot snap.
            {
                var slideCfg = new WidgetConfig { SlidingGraphs = true };
                var cont = BuildSineModel(surface.Width, surface.Height);
                double t0 = TestNow - 60.0;
                for (int i = 0; i < 60; i++)
                    cont.Series[0].Data.Add(t0 + i, i == 20 ? 95f : 30f);
                Snap(cont);
                cont.SampleIntervalSec = 1.0;
                int c1 = FindPeakColumn(GrabModel(cont, cont.LastSampleTime + 1.0, Cap("11-tick-before"), slideCfg),
                    surface.Width, surface.Height);
                cont.Series[0].Data.Add(t0 + 60, 30f);
                Snap(cont);
                cont.SampleIntervalSec = 1.0;
                int c2 = FindPeakColumn(GrabModel(cont, cont.LastSampleTime, Cap("11-tick-after"), slideCfg),
                    surface.Width, surface.Height);
                Check(c1 >= 0 && c2 >= 0 && Math.Abs(c2 - c1) <= 1, "tick boundary is continuous",
                    $"peak column {c1} just before the tick -> {c2} just after (a snap would jump a full slot)");
            }

            // A mature snapshot (60 valid samples, the MC-default window) spans the
            // full plot width statically; with sliding on, the entering slot past
            // the right edge stays invisible behind the clip.
            {
                var slideCfg = new WidgetConfig { SlidingGraphs = true };
                var full = BuildSineModel(surface.Width, surface.Height);
                Seed(full.Series[0].Data, 60, TestNow + Headroom);
                Snap(full);
                byte[] pxFull = GrabModel(full, full.LastSampleTime, Cap("12-full-width"));
                var (minX, maxX) = MinMaxAccentX(pxFull, surface.Width, surface.Height, 50, surface.Height - 12);
                double rEdge = surface.Width - 12.0, lEdge = 12.0;
                Check(minX >= 0 && minX <= lEdge + 2 * slotW && maxX >= rEdge - 2 * slotW,
                    "mature curve spans the full plot width",
                    $"accent x span [{minX}..{maxX}] vs plot [{lEdge}..{rEdge}] (slot {slotW:0.00}px)");
                byte[] pxSlide = GrabModel(full, full.LastSampleTime, Cap("12-slide-clip"), slideCfg);
                int outside = CountAccentInX(pxSlide, surface.Width, surface.Height,
                    (int)rEdge, surface.Width, 0, surface.Height);
                Check(outside == 0, "entering slot is clipped at the plot edge",
                    $"{outside} accent pixels at/after x={(int)rEdge} (the glide-in must stay invisible until inside)");
            }

            // Per-frame motion with sliding on is sub-slot: one 60fps step cannot
            // move the peak a column (slotW/60 per frame).
            {
                var slideCfg = new WidgetConfig { SlidingGraphs = true };
                Snap(sine);
                sine.LastSampleTime = TestNow;
                sine.SampleIntervalSec = 1.0;
                int p1 = FindPeakColumn(GrabModel(sine, TestNow + 0.5, "", slideCfg), surface.Width, surface.Height);
                int p2 = FindPeakColumn(GrabModel(sine, TestNow + 0.5 + 1.0 / 60.0, "", slideCfg), surface.Width, surface.Height);
                Check(p1 >= 0 && p2 >= 0 && Math.Abs(p2 - p1) <= 1, "per-frame displacement stays sub-pixel",
                    $"peak column {p1} -> {p2} across one 60fps frame (a steppy renderer jumps whole slots)");
            }

            // Layout assertions: header on screen, five axis labels, real target size.
            int whitePixels = CountPixels(pxA, surface.Width, surface.Height,
                x => x >= surface.Width - 130, x => true,
                (r, g, b) => r > 200 && g > 200 && b > 200);
            Check(whitePixels >= 20, "header value renders in the top right",
                $"{whitePixels} near-white pixels (header value is missing if this is 0)");

            // Scan from the plot top so the title is excluded. Percent axes draw no
            // left-side labels; byte axes still draw all five.
            int plotTopScan = 44;
            int percentBands = CountTextBands(pxA, surface.Width, surface.Height, plotTopScan);
            Check(percentBands == 0, "percent axis draws no left-side labels",
                $"{percentBands} text bands from y={plotTopScan} (expected 0; 0%-100% labels are removed)");

            var bytesModel = BuildSineModel(surface.Width, surface.Height, percent: false, axisMax: 16e9);
            Snap(bytesModel);
            byte[] pxBytes = GrabModel(bytesModel, TestNow, Cap("07-axis-bytes"));
            int byteBands = CountTextBands(pxBytes, surface.Width, surface.Height, plotTopScan);
            Check(byteBands >= 5, "byte axis still draws all five labels",
                $"{byteBands} text bands from y={plotTopScan} (expected 5)");

            // A card with nothing to plot shows its reason dim in the plot centre,
            // so an empty card never reads as a broken widget.
            var emptyModel = new ChartModel
            {
                Title = "Fans",
                Subtitle = "",
                Series = [],
                AxisMax = 100,
                ValueText = "--",
                EmptyText = "no fan sensor on this machine",
            };
            byte[] pxEmpty = GrabModel(emptyModel, TestNow, Cap("07-empty-hint"));
            int hintPixels = CountPixels(pxEmpty, surface.Width, surface.Height,
                x => x > surface.Width / 2 - 110 && x < surface.Width / 2 + 110,
                y => y > surface.Height / 2 - 4 && y < surface.Height / 2 + 24,
                (r, g, b) => r > 40 && g > 40 && b > 40);
            Check(hintPixels >= 10, "empty card shows its reason in the plot centre",
                $"{hintPixels} dim ink pixels around the centre (a blank plot would be 0)");

            // Byte labels carry their unit on every tick, so no gridline reads as a
            // bare number; rate axes append the per-second unit. FormatBytes is
            // 1024-based, like MC's to_human_readable_nice.
            const double GiB = 1L << 30;
            Check(ChartRenderer.AxisLabel(34.1 * GiB, bytesModel) == "34.1 GB"
               && ChartRenderer.AxisLabel(0, bytesModel) == "0 B",
                "byte axis labels spell out their unit",
                $"\"{ChartRenderer.AxisLabel(34.1 * GiB, bytesModel)}\" and \"{ChartRenderer.AxisLabel(0, bytesModel)}\"");
            var rateModel = BuildSineModel(surface.Width, surface.Height, percent: false,
                axisMax: 1024, valueUnit: "/s");
            Check(ChartRenderer.AxisLabel(1024, rateModel) == "1 KB/s",
                "rate axis labels carry the per-second unit",
                $"\"{ChartRenderer.AxisLabel(1024, rateModel)}\" (expected 1 KB/s)");

            // MC rounding: RoundingSettings::Pow2. A power of two divides into clean
            // quarters for the 1024-based formatter we draw with, so ticks read
            // 128/96/64/32 KB instead of 125/93.8/62.5/31.3.
            Check(ChartRenderer.RoundUpPow2(900) == 1024
               && ChartRenderer.RoundUpPow2(1500) == 2048
               && ChartRenderer.RoundUpPow2(3000) == 4096
               && ChartRenderer.RoundUpPow2(1_500_000) == 2_097_152
               && ChartRenderer.RoundUpPow2(4_000_000) == 4_194_304
               && ChartRenderer.RoundUpPow2(4_194_304) == 4_194_304,
                "byte auto-scale rounds up to a power of two like Mission Center",
                $"900->{ChartRenderer.RoundUpPow2(900)}, 1500->{ChartRenderer.RoundUpPow2(1500)}, " +
                $"3000->{ChartRenderer.RoundUpPow2(3000)}, 1.5e6->{ChartRenderer.RoundUpPow2(1_500_000)}");

            // The quarters of a power-of-two byte scale must print as whole numbers in
            // the formatter we actually draw, or every tick reads "93.8 KB/s".
            string[] quarterTicks = new[] { 0.0, 0.25, 0.5, 0.75, 1.0 }
                .Select(f => ChartRenderer.AxisLabel(1024.0 * 1024.0 * f, rateModel)).ToArray();
            Check(quarterTicks.All(t => !t.Contains('.')),
                "power-of-two byte scale prints whole-number quarters",
                string.Join(" / ", quarterTicks));

            // A near-idle byte peak still gets whole-unit quarters, not three "0 B/s".
            Check(ChartRenderer.ByteAxisMax(0.6) == 4 && ChartRenderer.ByteAxisMax(3) == 4
               && ChartRenderer.ByteAxisMax(4) == 4 && ChartRenderer.ByteAxisMax(5) == 8,
                "tiny byte peaks floor the scale at 4 units",
                $"0.6->{ChartRenderer.ByteAxisMax(0.6)}, 3->{ChartRenderer.ByteAxisMax(3)}, " +
                $"5->{ChartRenderer.ByteAxisMax(5)}");

            // The label column is chrome-free: entering edit mode must not shift it, or the
            // whole card reads as moving sideways. Measured from rendered pixels.
            {
                var columnModel = BuildSineModel(surface.Width, surface.Height, percent: false, axisMax: 100);
                Seed(columnModel.Series[0].Data, SeedCount, TestNow + Headroom);
                Snap(columnModel);
                columnModel.EditChrome = false;
                var (lockedLeft, _) = GrayInkX(GrabModel(columnModel, TestNow, ""),
                    surface.Width, surface.Height, 0, 70, plotTopScan);
                columnModel.EditChrome = true;
                var (editLeft, _) = GrayInkX(GrabModel(columnModel, TestNow, ""),
                    surface.Width, surface.Height, 0, 70, plotTopScan);
                Check(lockedLeft > 0 && lockedLeft == editLeft,
                    "the axis-label column does not move in edit mode",
                    $"label ink starts at x={lockedLeft} locked and x={editLeft} editing " +
                    "(any difference reads as the card shifting under the user)");
            }

            // The X badge heads the tick column. Both edges are measured from rendered
            // pixels: the badge's LEFT ink edge against the LEFT ink edge of the tick
            // values themselves, so this cannot pass by echoing the layout's own anchor.
            {
                var badgeModel = BuildSineModel(surface.Width, surface.Height, percent: false, axisMax: 100);
                Seed(badgeModel.Series[0].Data, SeedCount, TestNow + Headroom);
                Snap(badgeModel);
                badgeModel.EditChrome = true;
                var geo = chart.Geometry(new WidgetConfig(), badgeModel, 1f, surface.Width, surface.Height);
                var badges = HeaderLayout.Compute(surface.Width, 45f, 60f, editing: true, geo.LabelColumnX);
                var pxBadges = GrabBadges(badgeModel, badges);
                int labelHi = (int)geo.Rect.Left + 8;
                var (labelInkL, _) = GrayInkX(pxBadges, surface.Width, surface.Height, 0, labelHi, plotTopScan);
                var (xInkL, xInkR) = BrightInkX(pxBadges, surface.Width, surface.Height, 0, labelHi,
                    (int)(HeaderLayout.RowCenter - 14f), (int)(HeaderLayout.RowCenter + 14f));
                Check(labelInkL > 0 && xInkL > 0 && Math.Abs(xInkL - labelInkL) <= 2,
                    "X badge ink starts on the tick values' left edge",
                    $"X ink spans x={xInkL}..{xInkR}px, tick values start at x={labelInkL}px " +
                    "(2px tolerance; the badge heads the column, it does not float beside it)");
                // The bracket's arms live in the card's top strip, so the X clears them
                // above and below, not by standing further right than the values.
                Check(xInkL >= (int)HeaderLayout.MinInkLeft - 1,
                    "X badge ink stays inside the card inset",
                    $"X ink starts at x={xInkL}, inset floor is {HeaderLayout.MinInkLeft:0.0}");
            }

            // The label gutter is measured from the labels themselves, so the curve can
            // never run under a label no matter how wide the unit text gets.
            var wideModel = BuildSineModel(surface.Width, surface.Height, percent: false, axisMax: 100);
            Seed(wideModel.Series[0].Data, SeedCount, TestNow + Headroom);
            Snap(wideModel);
            byte[] pxWide = GrabModel(wideModel, TestNow, "");
            var (labelMinX, labelMaxX) = GrayInkX(pxWide, surface.Width, surface.Height, 0, 70, plotTopScan);
            var (curveMinX, _) = MinMaxAccentX(pxWide, surface.Width, surface.Height, plotTopScan, surface.Height);
            Check(labelMaxX > 0 && curveMinX > 0 && curveMinX - labelMaxX >= 6,
                "byte labels stay clear of the curve",
                $"label ink ends at x={labelMaxX}, curve starts at x={curveMinX} (need >= 6px clear)");

            // ---- 3c. native struct + header geometry proofs --------------------
            // Every GetPerformanceInfo count field is in pages; one phantom field in
            // our struct shifted every offset and produced impossible totals.
            int piSize = Marshal.SizeOf<PERFORMANCE_INFORMATION>();
            Check(piSize == 104, "PERFORMANCE_INFORMATION matches the native 104-byte layout",
                $"{piSize} bytes on x64 (a phantom field shifts every reading: total, avail, cache, page size)");

            // HeaderLayout (docs/design.md): the < title > group is centred on the
            // card, the value is right-anchored, and the gaps are measured ink to ink.
            {
                foreach (var (label, valueW) in new[] { ("narrow value", 60f), ("wide RAM value", 110f) })
                {
                    var edit = HeaderLayout.Compute(460, titleW: 45, valueW: valueW, editing: true);
                    // Ink edges: glyph half-extents, not the wider hit boxes.
                    float nextInkR = edit.Next.X + edit.Next.Width / 2f + WidgetPainter.ChevronGlyphHalf;
                    float valueGap = edit.Value.Left - nextInkR;
                    float prevInkR = edit.Prev.X + edit.Prev.Width / 2f + WidgetPainter.ChevronGlyphHalf;
                    float prevGap = edit.Title.X - prevInkR;
                    float nextGap = edit.Next.X + edit.Next.Width / 2f - WidgetPainter.ChevronGlyphHalf
                        - (edit.Title.X + edit.Title.Width - 1f);   // title rect carries a 1px overhang
                    float groupC = (edit.Prev.X + edit.Next.X + edit.Next.Width) / 2f;
                    float titleW2 = edit.Title.Width - 1f;
                    // The value yields: it is fitted into ValueMax and keeps one text
                    // gap from the centred group instead of pushing the group left.
                    Check(edit.Value.Width <= edit.ValueMax + 0.01f
                            && Math.Abs(edit.Value.Width - Math.Min(valueW, edit.ValueMax)) <= 0.01f,
                        $"value yields to the centred group ({label})",
                        $"value {edit.Value.Width:0.0}px of {valueW}px, ValueMax {edit.ValueMax:0.0}px");
                    Check(valueGap >= HeaderLayout.GapText - 0.01f,
                        $"edit value keeps its gap to the > chevron ({label})",
                        $"{valueGap:0.0}px ink gap (the RAM 31.01GB overlap was ~0)");
                    Check(prevGap >= HeaderLayout.ChevGap - 0.01f && nextGap >= HeaderLayout.ChevGap - 0.01f,
                        $"chevrons keep their ink gap to the title ({label})",
                        $"< {prevGap:0.0}px / {nextGap:0.0}px >");
                    Check(Math.Abs(groupC - 230f) <= 1f && Math.Abs(titleW2 - 45f) <= 0.5f,
                        $"< title > group centers on the card, title intact ({label})",
                        $"group center {groupC:0.0} vs card center 230.0, title {titleW2:0.0}px of 45px");

                    // The check glyph is the value's right neighbour: it must not be
                    // drawn under the last digit (the RAM 31.01GB screenshot).
                    float checkInkL = HeaderLayout.BadgeInkLeft(edit.Check);
                    float badgeGap = checkInkL - edit.Value.Right;
                    Check(badgeGap >= HeaderLayout.GapChrome - 0.01f,
                        $"edit value clears the check badge ink ({label})",
                        $"{badgeGap:0.0}px ink gap, check ink starts at {checkInkL:0.0} and value ends at {edit.Value.Right:0.0}");

                    // The X in the left corner is the centred group's left neighbour.
                    float xInkR = HeaderLayout.BadgeInkRight(edit.Close);
                    float groupInkL = edit.Prev.X + edit.Prev.Width / 2f - WidgetPainter.ChevronGlyphHalf;
                    Check(groupInkL - xInkR >= HeaderLayout.GapChrome - 0.01f,
                        $"< group clears the X badge ink ({label})",
                        $"{groupInkL - xInkR:0.0}px ink gap, X ink ends at {xInkR:0.0} and the group starts at {groupInkL:0.0}");
                }

                // With no tick column (percent axes) both badges keep their floor inset,
                // mirrored about the card centre.
                {
                    var edit = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: true);
                    float xC = edit.Close.X + edit.Close.Width / 2f;
                    float cC = edit.Check.X + edit.Check.Width / 2f;
                    float xInkL = HeaderLayout.BadgeInkLeft(edit.Close);
                    float checkInkR = HeaderLayout.BadgeInkRight(edit.Check);
                    Check(Math.Abs((xC + cC) - 460f) <= 0.01f && Math.Abs(edit.Close.Y - edit.Check.Y) <= 0.01f,
                        "X and check are mirror-symmetric about the card centre",
                        $"X glyph x {xC:0.0}, check glyph x {cC:0.0} on 460px (centres must sum to the width)");
                    Check(Math.Abs(xInkL - HeaderLayout.BadgeClear) <= 0.01f
                            && Math.Abs((460f - checkInkR) - HeaderLayout.BadgeClear) <= 0.01f,
                        "no tick column: both badges sit one bracket + text gap inside their corner",
                        $"X ink {xInkL:0.0} from the left, check ink {460f - checkInkR:0.0} from the right (BadgeClear {HeaderLayout.BadgeClear:0.0})");
                    Check(edit.Prev.X >= edit.Close.Right - 0.01f && edit.Next.Right <= edit.Check.X + 0.01f,
                        "chevron hit boxes never reach the badge boxes",
                        $"prev [{edit.Prev.X:0.0}..{edit.Prev.Right:0.0}] vs X [{edit.Close.X:0.0}..{edit.Close.Right:0.0}], next [{edit.Next.X:0.0}..{edit.Next.Right:0.0}] vs check [{edit.Check.X:0.0}..{edit.Check.Right:0.0}]");
                }

                // The X rides the tick column: its ink starts on the column's left edge and
                // the check takes the same distance from the other edge (docs/design.md 5).
                foreach (float anchor in new[] { 12f, 43.7f, 60f, 96f })
                {
                    var edit = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: true, anchorX: anchor);
                    float xInkL = HeaderLayout.BadgeInkLeft(edit.Close);
                    float checkInkR = HeaderLayout.BadgeInkRight(edit.Check);
                    // The check mirrors the X, but never closer to its edge than BadgeClear.
                    float mirrored = Math.Min(460f - Math.Max(anchor, HeaderLayout.MinInkLeft),
                        460f - HeaderLayout.BadgeClear);
                    Check(Math.Abs(xInkL - Math.Max(anchor, HeaderLayout.MinInkLeft)) <= 0.01f
                            && Math.Abs(checkInkR - mirrored) <= 0.01f,
                        $"X ink starts on the tick column, check mirrors it (anchor {anchor:0.0})",
                        $"X ink starts {xInkL:0.0}, check ink ends {checkInkR:0.0} ({(460f - checkInkR):0.0} from the right edge, mirror {mirrored:0.0})");
                }

                // The top-right corner bracket and the check glyph shared pixels; the
                // badge block now starts one text gap inside the bracket strip.
                {
                    var edit = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: true);
                    float checkInkR2 = HeaderLayout.BadgeInkRight(edit.Check);
                    float cornerL = 460f - WidgetPainter.CornerSpan;
                    Check(checkInkR2 <= cornerL - HeaderLayout.GapText + 0.01f,
                        "check badge ink clears the corner bracket",
                        $"check ink ends {checkInkR2:0.0}, bracket strip starts {cornerL:0.0}");
                    Check(edit.Check.Right <= 460f && edit.Close.Left >= 0f,
                        "badge hit boxes stay inside the card",
                        $"close [{edit.Close.Left:0.0}..{edit.Close.Right:0.0}] check [{edit.Check.Left:0.0}..{edit.Check.Right:0.0}] on 460px");
                }

                var locked = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: false);
                Check(locked.Prev.Width == 0 && locked.Next.Width == 0, "locked header has no chevrons",
                    "chevron boxes are empty when not editing");
                float lockedGap = locked.Value.Left - (locked.Title.X + locked.Title.Width);
                Check(lockedGap >= HeaderLayout.GapText - 0.01f, "locked title never nears the value",
                    $"{lockedGap:0.0}px gap");
                float lockedC = locked.Title.X + locked.Title.Width / 2f;
                Check(Math.Abs(lockedC - 230f) <= 1f, "locked title centers on the card",
                    $"title center {lockedC:0.0} vs card center 230.0");
            }

            // ---- 3d. colour picker: model, ring order, hit-test, chip ---------
            {
                // Every colour we can show survives the round trip the picker uses.
                string[] hexes = ["#4CC2FF", "#FF0000", "#00FF00", "#0000FF", "#FFFFFF",
                                  "#000000", "#5823CD", "#4DA6FF"];
                static string RtHex(string h)
                {
                    var (r, g, b) = Rgba.FromHex(h).ToHsv().ToRgb();
                    return new Rgba(r, g, b).ToHex();
                }
                bool roundTrip = hexes.All(h => RtHex(h) == h);
                Check(roundTrip, "colour survives hex -> HSV -> hex",
                    $"{hexes.Count(h => RtHex(h) == h)}/{hexes.Length} round trips exact");

                Check(Rgba.FromHex("#abc").ToHex() == "#AABBCC"
                    && Rgba.FromHex("AABBCC").ToHex() == "#AABBCC"
                    && Rgba.FromHex("nonsense").ToHex() == "#FFFFFF",
                    "hex parsing takes short, bare and junk input",
                    $"#abc -> {Rgba.FromHex("#abc").ToHex()}, bare -> {Rgba.FromHex("AABBCC").ToHex()}, junk -> {Rgba.FromHex("nonsense").ToHex()}");

                // Blender's wheel: hue runs clockwise from red at 12 o'clock
                // (https://projects.blender.org/blender/blender/raw/main/source/blender/editors/interface/interface_widgets.cc
                // hsvcircle_vals_from_pos), and the radius is saturation.
                var c = ColorPickerLayout.Center;
                double hTop = ColorPickerLayout.HsFromPoint(c.X, c.Y - 90f).H;
                double hRight = ColorPickerLayout.HsFromPoint(c.X + 90f, c.Y).H;
                double hBottom = ColorPickerLayout.HsFromPoint(c.X, c.Y + 90f).H;
                Check(Math.Abs(hBottom) < 0.01 && Math.Abs(hRight - 270) < 0.01 && Math.Abs(hTop - 180) < 0.01,
                    "wheel puts red at the bottom and runs the reference order clockwise",
                    $"bottom {hBottom:0.0} deg, top {hTop:0.0}, right {hRight:0.0}");

                double satOut = ColorPickerLayout.HsFromPoint(c.X + ColorPickerLayout.WheelR + 40f, c.Y).S;
                double satMid = ColorPickerLayout.HsFromPoint(c.X, c.Y).S;
                Check(Math.Abs(satOut - 1) < 0.001 && Math.Abs(satMid) < 0.001,
                    "wheel radius is saturation, clamped at the rim",
                    $"centre S={satMid:0.00}, beyond the rim S={satOut:0.00}");

                // Saturation 0 is the wheel's centre, where hue is unrecoverable by
                // construction, so the round trip starts off-centre; a grey centre proves
                // the wheel really is achromatic there.
                float worstH = 0f, worstS = 0f;
                foreach (float hh in new[] { 0f, 37f, 90f, 145f, 200f, 268f, 310f, 359f })
                    foreach (float ss in new[] { 0.05f, 0.25f, 0.5f, 0.75f, 1f })
                    {
                        var p = ColorPickerLayout.PointFromHs(hh, ss);
                        var backHs = ColorPickerLayout.HsFromPoint(p.X, p.Y);
                        double dH = Math.Abs(backHs.H - hh); if (dH > 180) dH = 360 - dH;
                        worstH = Math.Max(worstH, (float)dH);
                        worstS = Math.Max(worstS, (float)Math.Abs(backHs.S - ss));
                    }
                Check(worstH < 0.6f && worstS < 0.002f, "wheel marker and wheel are inverses",
                    $"worst hue error {worstH:0.00} deg, worst saturation error {worstS:0.000}");

                float vy = ColorPickerLayout.YFromValue(0.62);
                Check(Math.Abs(ColorPickerLayout.ValueFromY(vy) - 0.62) < 0.005,
                    "the vertical value bar and its handle are inverses",
                    $"value 0.62 -> y {vy:0.0} -> {ColorPickerLayout.ValueFromY(vy):0.000}");

                // The reference card: white ramp end up, and the handle for a full-brightness
                // colour sits at the top. Brightness increases upward, as on any slider.
                Check(Math.Abs(ColorPickerLayout.YFromValue(1) - ColorPickerLayout.ValueTrack.Top) < 0.01f
                    && Math.Abs(ColorPickerLayout.YFromValue(0) - ColorPickerLayout.ValueTrack.Bottom) < 0.01f,
                    "the value handle runs bright at the top, like the reference card",
                    $"v=1 at y {ColorPickerLayout.YFromValue(1):0.0} (track top {ColorPickerLayout.ValueTrack.Top:0.0}), v=0 at y {ColorPickerLayout.YFromValue(0):0.0} (track bottom {ColorPickerLayout.ValueTrack.Bottom:0.0})");

                Check(ColorPickerLayout.ValueTrack.Left >= ColorPickerLayout.Center.X + ColorPickerLayout.WheelR                    && ColorPickerLayout.ValueTrack.Width < 40f,
                    "the value bar stands beside the wheel, not under it",
                    $"bar at x {ColorPickerLayout.ValueTrack.Left:0}..{ColorPickerLayout.ValueTrack.Right:0} ({ColorPickerLayout.ValueTrack.Width:0}px wide) beside a wheel ending at {ColorPickerLayout.Center.X + ColorPickerLayout.WheelR:0}");

                var g0 = ColorPickerLayout.Groove(0);
                Check(Math.Abs(ColorPickerLayout.SliderFromX(0, ColorPickerLayout.XFromSlider(0, 0.62)) - 0.62) < 0.002
                    && ColorPickerLayout.XFromSlider(0, 0) >= g0.Left - 0.01f
                    && ColorPickerLayout.XFromSlider(0, 1) <= g0.Right + 0.01f,
                    "slider groove and its handle are inverses",
                    $"slider 0.62 -> x {ColorPickerLayout.XFromSlider(0, 0.62):0.0} -> {ColorPickerLayout.SliderFromX(0, ColorPickerLayout.XFromSlider(0, 0.62)):0.000}, groove {g0.Left:0}..{g0.Right:0}");

                // The card is Blender's, row for row: the wheel row, the two mode rows,
                // four sliders, then the hex field and the dropper, all inside the card.
                float lastBottom = ColorPickerLayout.Slider(3).Bottom;
                Check(Math.Abs(ColorPickerLayout.HexY - lastBottom - 12f) < 0.01f
                    && Math.Abs(ColorPickerLayout.HexY + ColorPickerLayout.RowH + ColorPickerLayout.Pad
                        - ColorPickerLayout.CardH) < 0.01f,
                    "the card is the reference card, row for row",
                    $"wheel {ColorPickerLayout.Pad:0}..{ColorPickerLayout.Pad + ColorPickerLayout.WheelD:0}, mode rows {ColorPickerLayout.SpaceRowY:0}/{ColorPickerLayout.ModelRowY:0}, four sliders {ColorPickerLayout.SliderY0:0}..{lastBottom:0}, hex row {ColorPickerLayout.HexY:0}, card {ColorPickerLayout.CardH:0} tall");

                Check(Math.Abs(ColorTransfer.LinearToSrgb(ColorTransfer.SrgbToLinear(0.5)) - 0.5) < 0.002
                    && Math.Abs(ColorTransfer.SrgbToLinear(1.0) - 1.0) < 0.002,
                    "the Linear working space round-trips the sRGB transfer",
                    $"0.5 linear -> {ColorTransfer.LinearToSrgb(ColorTransfer.SrgbToLinear(0.5)):0.000} perceptual");
                Check(new Rgba(128, 128, 128).ToLinear().ToHex() == "#373737"
                    && Rgba.FromLinear(0x37, 0x37, 0x37).ToHex() == "#808080",
                    "a linear-space number maps to the same sRGB colour",
                    "perceptual #808080 <-> linear #373737");

                Check(ColorPickerLayout.HitTest(c.X, c.Y) == PickerPart.Wheel
                    && ColorPickerLayout.HitTest(c.X + 90f, c.Y) == PickerPart.Wheel
                    && ColorPickerLayout.HitTest(c.X + ColorPickerLayout.WheelR + 3f, c.Y) == PickerPart.None
                    && ColorPickerLayout.HitTest(ColorPickerLayout.ValueTrack.Left + 6f,
                        ColorPickerLayout.ValueTrack.Top + 8f) == PickerPart.Value
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Segment(0, 0).Left + 20f,
                        ColorPickerLayout.SpaceRowY + 5f) == PickerPart.Space
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Segment(1, 1).Left + 20f,
                        ColorPickerLayout.ModelRowY + 5f) == PickerPart.Model
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Slider(2).Left + 40f,
                        ColorPickerLayout.Slider(2).Top + 5f) == PickerPart.SliderC
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Slider(3).Left + 40f,
                        ColorPickerLayout.Slider(3).Top + 5f) == PickerPart.SliderD
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Hex.Left + 10f,
                        ColorPickerLayout.Hex.Top + 5f) == PickerPart.Hex
                    && ColorPickerLayout.HitTest(ColorPickerLayout.Eyedropper.Left + 5f,
                        ColorPickerLayout.Eyedropper.Top + 5f) == PickerPart.Eyedropper
                    && ColorPickerLayout.HitTest(4f, 4f) == PickerPart.None,
                    "hit-test agrees with every control the card draws",
                    "rim = wheel, the gap between wheel and bar = none (click-away commits), bar = value, both mode rows, four sliders, hex and dropper fields, corner = none");

                var dark = SystemTheme.CardFor(light: false);
                var light = SystemTheme.CardFor(light: true);
                Check(Math.Abs(dark.R - 66 / 255f) < 0.002f && Math.Abs(light.R - 204 / 255f) < 0.002f
                    && Math.Abs(dark.G - dark.R) < 0.002f && Math.Abs(dark.B - dark.R) < 0.002f,
                    "picker card fill follows the system theme",
                    $"dark #424242 -> {dark.R * 255:0} ({dark.G * 255:0},{dark.B * 255:0}), light #CCCCCC -> {light.R * 255:0}");

                // The pipette pointer is built at runtime from Blender's icon; a zero
                // handle means Apply silently falls back to the arrow and the cursor
                // never visibly changes, which is exactly the reported failure.
                Check(Interop.EyedropperCursor.Handle != IntPtr.Zero, "eyedropper cursor handle builds",
                    $"handle=0x{Interop.EyedropperCursor.Handle.ToInt64():X}");

                // The overlay owns the cursor for the pick: the pointer is always over
                // our window, so its WM_SETCURSOR is the whole mechanism. A global
                // readback would race any live mouse move, so the assert calls the
                // procedure directly: it must answer 1 (cursor set, stop here).
                Check(ColorPickerWindow.OverlayProc(IntPtr.Zero, Native.WM_SETCURSOR,
                        IntPtr.Zero, IntPtr.Zero) == new IntPtr(1),
                    "pick overlay answers the pipette cursor",
                    "OverlayProc(WM_SETCURSOR) must return 1 after applying the pipette");
                Interop.EyedropperCursor.Apply(false);

                // The header is the whole upper band: every element takes its y from one
                // centre, the middle of the space between the top edge and the first line.
                float band = HeaderLayout.RowCenterFor(50f);
                var bandRow = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: true,
                    anchorX: 0f, rowCenter: band);
                float[] centres = [bandRow.Close.Top + bandRow.Close.Height / 2f,
                    bandRow.Check.Top + bandRow.Check.Height / 2f,
                    bandRow.Prev.Top + bandRow.Prev.Height / 2f,
                    bandRow.Next.Top + bandRow.Next.Height / 2f,
                    bandRow.Title.Top + bandRow.Title.Height / 2f,
                    bandRow.Value.Top + bandRow.Value.Height / 2f,
                    bandRow.Chip.Top + bandRow.Chip.Height / 2f];
                Check(Math.Abs(band - 25f) < 0.01f && centres.All(c => Math.Abs(c - band) < 0.01f),
                    "every header element centres in the band above the chart",
                    $"band 0..50 -> centre {band:0.0}; element centres [{string.Join(", ", centres.Select(c => c.ToString("0.0")))}]");

                // The chip sits between the X badge and the centred group, right-aligned
                // against the group, and is dropped rather than allowed to move it.
                var chipEdit = HeaderLayout.Compute(460, titleW: 45, valueW: 110, editing: true);
                float chipClear = chipEdit.Chip.Width == 0 ? 0
                    : chipEdit.Chip.Left - HeaderLayout.BadgeInkRight(chipEdit.Close);
                Check(chipEdit.Chip.Width == HeaderLayout.ChipDia && chipClear >= HeaderLayout.GapChrome
                    && chipEdit.Chip.Right <= chipEdit.Title.Left - HeaderLayout.ChevBox / 2f,
                    "colour chip sits between the X and the centred group",
                    $"chip [{chipEdit.Chip.Left:0.0}..{chipEdit.Chip.Right:0.0}], {chipClear:0.0}px after the X ink, group starts {chipEdit.Title.Left:0.0}");
                var chipNarrow = HeaderLayout.Compute(150, titleW: 45, valueW: 110, editing: true);
                Check(chipNarrow.Chip.Width == 0, "colour chip is dropped on a card too narrow to hold it",
                    "a 150px card has no free zone, so no chip is offered");
            }

            Check(surface.Width == 460 && surface.Height == 200, "chart laid out against the real target size",
                $"surface is {surface.Width}x{surface.Height} and the chart was told the same, not the 420x180 config default");

            // ---- 4. sustained frames on the real chart -------------------------
            foreach (var (name, model) in charts)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                const int Frames = 60;
                for (int f = 0; f < Frames; f++)
                {
                    // Re-snapshot every frame, as BuildChart does in the real loop.
                    foreach (var s in model.Series) s.Snapshot = s.Data.SnapshotLatest(ChartModel.FixedSlots);
                    double now = TestNow + f / 60.0;
                    surface.BeginDraw(96f);
                    surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                    WidgetPainter.PaintBackground(surface.Context, resources,
                        new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                        hover: 0f, checkAmount: 0f, editing: false);
                    new ChartRenderer(device, resources)
                        .Draw(surface.Context, new WidgetConfig(), model, 96f, now, surface.Width, surface.Height);
                    surface.EndDrawAndPresent();
                }
                surface.Commit(device);
                sw.Stop();
                Log.Info($"drew '{name}': {Frames} frames in {sw.ElapsedMilliseconds} ms " +
                         $"({sw.Elapsed.TotalMilliseconds / Frames:0.00} ms/frame)");
            }

            // ---- 4. resize, the path drag-resize hammers ------------------------
            foreach (var (w, h) in new[] { (240, 120), (900, 400), (460, 200) })
            {
                surface.Resize(w, h);
                surface.BeginDraw(96f);
                surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                var resized = BuildSineModel(w, h);
                Seed(resized.Series[0].Data, SeedCount, TestNow + Headroom);
                Snap(resized);
                new ChartRenderer(device, resources)
                    .Draw(surface.Context, new WidgetConfig(), resized, 96f, TestNow, w, h);
                surface.EndDrawAndPresent();
                surface.Commit(device);
                Log.Info($"resized to {w}x{h} and drew a frame");
            }

            // ---- 5. capture the models that have only ever been TIMED -----------
            // Timing is not coverage: per-core legibility and stack order need images.
            if (captureDir.Length > 0)
            {
                foreach (var (label, model) in new[]
                {
                    ("11-per-core-32-series", charts[1].Item2),
                    ("12-stacked-memory-bands", charts[2].Item2),
                })
                {
                    string file = System.IO.Path.Combine(captureDir, label + ".bmp");
                    surface.Resize(460, 200);
                    surface.BeginDraw(96f);
                    surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
                    WidgetPainter.PaintBackground(surface.Context, resources,
                        new WidgetConfig { CornerRadius = 8, Transparency = 12 }, surface.Width, surface.Height,
                        hover: 0f, checkAmount: 0f, editing: false);
                    chart.Draw(surface.Context, new WidgetConfig(), model, 96f, TestNow,
                        surface.Width, surface.Height);
                    surface.EndDrawOnly();
                    surface.CaptureToBmp(device, file);
                    surface.Present();
                    surface.Commit(device);
                    Log.Info($"captured '{label}' ({model.Series.Count} series) -> {file}");
                }
            }

            // ---- 6. launch guards -------------------------------------------
            // Proves kill/new-widget round-trip, click-through transitions, target renewal,
            // and exactly-once poison retirement, on a real widget parented to the hidden host.
            // Nothing here touches the desktop, WorkerW, or Explorer.

            // --- 6a. a poisoned widget is retired EXACTLY ONCE ---
            {
                const int RetireAfter = 30;
                var faults = new RenderFaultTracker<string>(RetireAfter);
                int retireDecisions = 0;
                for (int f = 1; f <= 600; f++)   // ten seconds of a widget throwing every frame
                    if (faults.ShouldRetire("A", faults.NoteFault("A"))) retireDecisions++;

                Check(retireDecisions == 1,
                    "poisoned widget is retired exactly once, not once per frame",
                    $"600 consecutive faults produced {retireDecisions} retire decision(s), expected 1 " +
                    $"(retireAfter={RetireAfter})");

                // Alternating fault/success must never retire: the count is consecutive.
                var flaky = new RenderFaultTracker<string>(RetireAfter);
                int peak = 0, retires = 0;
                for (int f = 0; f < 600; f++)
                {
                    if (f % 2 == 0) { int n = flaky.NoteFault("C"); peak = Math.Max(peak, n); if (flaky.ShouldRetire("C", n)) retires++; }
                    else { flaky.NoteSuccess("C"); }
                }
                Check(peak < RetireAfter && retires == 0,
                    "an intermittently faulting widget is never retired",
                    $"a widget alternating fault/success over 600 frames peaked at {peak} consecutive " +
                    $"faults (threshold {RetireAfter}) and produced {retires} retire decisions");

                // Log throttling: a widget throwing 60x/second must not bury every other line.
                var throttle = new RenderFaultTracker<string>(RetireAfter);
                int logged = 0;
                for (int i = 0; i < 4; i++)          // four distinct misbehaving widgets
                    for (int f = 1; f <= 120; f++)
                    {
                        throttle.NoteFault("w" + i);
                        if (throttle.ShouldLog(f)) logged++;
                    }
                Check(logged == 3 * 120 + 2,
                    "fault logging throttles once more than three widgets are misbehaving",
                    $"{logged} lines for 4 widgets x 120 faults; the first 3 log every fault and the " +
                    $"4th logs only faults 1 and 61, so 360 + 2 = 362 is correct");

                Check(new RenderFaultTracker<string>(1) is not null,
                    "a retire threshold of 1 is accepted",
                    "guards against a zero or negative threshold silently never retiring");
                try
                {
                    _ = new RenderFaultTracker<string>(0);
                    Check(false, "a retire threshold of 0 is rejected", "constructor accepted 0");
                }
                catch (ArgumentOutOfRangeException)
                {
                    Check(true, "a retire threshold of 0 is rejected", "ArgumentOutOfRangeException, as intended");
                }
            }

            // --- 6b. the kill / new-widget signal round-trips ---
            {
                string evName = @"Local\FlowMonitor.SelfTest." + Guid.NewGuid().ToString("N");
                IntPtr ev = CreateEventW(IntPtr.Zero, true, false, evName);
                Check(ev != IntPtr.Zero, "the self test can create a named event", "CreateEventW -> 0x" + ev.ToString("X"));
                if (ev != IntPtr.Zero)
                {
                    Check(DesktopHost.SignalEvent(evName),
                        "--kill plumbing round-trips: SignalEvent reaches a waiting instance",
                        "SignalEvent on '" + evName + "' returned true");
                    // Manual-reset, so it stays signalled; a second open+set must also succeed,
                    // which is what happens when a human runs --kill twice.
                    Check(DesktopHost.SignalEvent(evName),
                        "--kill is idempotent across repeated invocations",
                        "second SignalEvent on a live event returned true");
                    Check(!DesktopHost.SignalEvent(@"Local\FlowMonitor.NoSuchEvent." + Guid.NewGuid().ToString("N")),
                        "--kill reports 'nothing was running' instead of pretending to succeed",
                        "SignalEvent on a non-existent event returned false, which Program.cs turns into exit code 2");
                    CloseHandle(ev);
                }
            }

            // --- 6c + 6d. a real widget: click-through style and target renewal ---
            {
                if (!DesktopHost.EnsureWidgetClassRegistered())
                {
                    Check(false, "widget window class registers", "EnsureWidgetClassRegistered returned false");
                }
                else
                {
                    var testHost = new TestRenderHost(device!, resources);
                    var cfg = new WidgetConfig
                    {
                        Id = "selftest",
                        X = 0,
                        Y = 0,
                        Width = 320,
                        Height = 140,
                        ClickThrough = ClickThroughMode.LeftClickOnly,
                    };

                    WidgetWindow? w = null;
                    try
                    {
                        w = new WidgetWindow(testHost, cfg);
                        w.Create(hwnd);   // parented to the HIDDEN self-test host, never the desktop
                        Log.Info("test widget hwnd = 0x" + w.Handle.ToString("X") + " parented to the hidden self-test host");

                        bool Transparent() =>
                            (GetWindowLongPtr(w.Handle, GWL_EXSTYLE).ToInt64() & WS_EX_TRANSPARENT) != 0;

                        // LeftClickOnly forwards clicks rather than going transparent (preserves right-click to edit).
                        Check(!Transparent(),
                            "a locked LeftClickOnly widget is NOT WS_EX_TRANSPARENT",
                            "bit clear as intended: transparency would break right-click to edit mode");

                        w.BeginEdit();
                        Check(!Transparent() && w.IsEditing,
                            "entering edit mode leaves the widget fully interactive",
                            $"editing={w.IsEditing} transparent={Transparent()}");

                        // Idempotence: repeated calls must not corrupt the style.
                        w.ApplyInteractionMode();
                        w.ApplyInteractionMode();
                        Check(!Transparent(),
                            "ApplyInteractionMode is idempotent",
                            "two extra calls in edit mode left the style correct");

                        w.EndEdit();
                        Check(!w.IsEditing && !Transparent(),
                            "leaving edit mode re-locks the widget without making it transparent",
                            $"editing={w.IsEditing} transparent={Transparent()}");

                        // Config changes must reach the window style immediately.
                        // Full stays hittestable so right-click still re-enters edit mode.
                        var full = cfg.Clone();
                        full.ClickThrough = ClickThroughMode.Full;
                        w.UpdateConfig(full);
                        Check(!Transparent(),
                            "Full click-through stays hittestable so right-click can re-enter edit mode",
                            "ClickThrough=Full while locked left WS_EX_TRANSPARENT clear");

                        w.UpdateConfig(cfg);
                        Check(!Transparent(),
                            "changing it back to LeftClickOnly clears the style again",
                            "bit clear after reverting");

                        // Target renewal must keep pixels flowing; verified by opaque-pixel count after rebuild.
                        // Flip-model readback can lag one frame, so render two frames and take the max.
                        bool drew1 = w.RenderFrame(TestNow, 1f / 60f);
                        w.CommitComposition();
                        int read1 = CountOpaquePixels(device!, w.Surface);
                        w.RequestRedraw();
                        bool drew2 = w.RenderFrame(TestNow + 1f / 60f, 1f / 60f);
                        w.CommitComposition();
                        int read2 = CountOpaquePixels(device!, w.Surface);
                        Log.Info($"RenderFrame returned drew1={drew1} drew2={drew2}; readback read1={read1} read2={read2}");
                        int before = Math.Max(read1, read2);

                        w.Reparent(hwnd);   // same parent: isolates the target rebuild from re-parenting
                        for (int f = 0; f < 3; f++) w.RenderFrame(TestNow + f / 60.0, 1f / 60f);
                        w.CommitComposition();
                        int afterRead = CountOpaquePixels(device!, w.Surface);
                        w.RequestRedraw();
                        w.RenderFrame(TestNow + 0.25, 1f / 60f);
                        w.CommitComposition();
                        int after = Math.Max(afterRead, CountOpaquePixels(device!, w.Surface));

                        Check(before > 500,
                            "the test widget renders content before the target rebuild",
                            $"{before} non-transparent pixels");
                        Check(after > 500 && after == before,
                            "rebuilding the composition target keeps the widget rendering",
                            $"{before} pixels before, {after} after RenewTargetForHwnd " +
                            "(0 after would be the blank-widget failure; a different count would mean the target rebuilt onto the wrong window)");

                        if (captureDir.Length > 0)
                        {
                            string file = System.IO.Path.Combine(captureDir, "13-widget-after-target-renewal.bmp");
                            w.Surface!.CaptureToBmp(device!, file);

                            Log.Info("captured '13-widget-after-target-renewal' -> " + file);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Log.Write("ERROR", "widget launch-guard test threw: " + ex);
                    }
                    finally
                    {
                        try { w?.Destroy(); } catch (Exception ex) { Log.Warn("test widget destroy: " + ex.Message); }
                    }

                    // --- 6e. a telemetry tick wakes a locked widget (freeze regression) ---
                    // Locked widgets skip frames while the curve is flat, so the tick wakeup is
                    // what restarts polling. Without it the widget freezes seconds after locking.
                    DesktopHost? fhost = null;
                    WidgetWindow? fw = null;
                    try
                    {
                        fhost = new DesktopHost();
                        fw = new WidgetWindow(fhost, new WidgetConfig
                        {
                            Id = "selftest-freeze",
                            Width = 320,
                            Height = 140,
                            Graph = GraphKind.Cpu,
                            ClickThrough = ClickThroughMode.LeftClickOnly,
                        });
                        fw.Create(hwnd);
                        fhost.TrackWidgetForTest(fw);

                        // Settle: slot capacity draws first, then the flat empty series stalls.
                        bool stalled = false;
                        for (int f = 0; f < 5; f++)
                            if (!fw.RenderFrame(TestNow + 100 + f / 60.0, 1f / 60f)) { stalled = true; break; }
                        Check(stalled,
                            "a locked widget with flat data stops presenting (the freeze precondition)",
                            stalled ? "RenderFrame went false once nothing changed" : "kept drawing with no new data");

                        fhost.Telemetry.SampleNow();
                        Check(fw.RedrawRequested,
                            "a telemetry tick requests a redraw on the locked widget",
                            fw.RedrawRequested ? "Sampled reached the widget" : "tick landed but nobody woke the widget");
                        bool drew = fw.RenderFrame(TestNow + 101, 1f / 60f);
                        Check(drew,
                            "the woken widget presents its next frame",
                            drew ? "RenderFrame true after the tick" : "still refusing to draw after being woken");

                        // --- 6f. a drag is persisted without the checkmark ---
                        // Geometry used to be applied to the window but never written back,
                        // so a widget the user moved and did not re-apply came back at its
                        // old spot on the next launch.
                        {
                            string storeDir = System.IO.Path.Combine(
                                System.IO.Path.GetTempPath(), "flowmonitor-selftest-store");
                            System.IO.Directory.CreateDirectory(storeDir);
                            string? priorStore = Model.WidgetStore.DirectoryOverride;
                            Model.WidgetStore.DirectoryOverride = storeDir;
                            try
                            {
                                var dragged = new WidgetWindow(fhost, new WidgetConfig
                                {
                                    Id = "selftest-geometry",
                                    X = 400,
                                    Y = 300,
                                    Width = 320,
                                    Height = 140,
                                    Graph = GraphKind.Cpu,
                                });
                                fhost.TrackWidgetForTest(dragged);
                                fhost.OnWidgetGeometryChanged(dragged, 1500, 640, 0, 0);
                                fhost.FlushPendingGeometry();
                                string path = System.IO.Path.Combine(storeDir, "selftest-geometry.json");
                                bool saved = System.IO.File.Exists(path)
                                    && System.IO.File.ReadAllText(path).Contains("\"X\": 1500")
                                    && System.IO.File.ReadAllText(path).Contains("\"Y\": 640");
                                Check(saved,
                                    "a drag persists without the user pressing the checkmark",
                                    saved
                                        ? "config on disk holds the dragged position 1500,640"
                                        : "config on disk does not hold the dragged position (a restart would move it back)");
                                fhost.ForgetWidgetForTest(dragged);
                                dragged.Destroy();
                            }
                            catch (Exception ex)
                            {
                                failures++;
                                Log.Write("ERROR", "geometry persistence test threw: " + ex);
                            }
                            finally { Model.WidgetStore.DirectoryOverride = priorStore; }
                        }

                        // --- 6h. GPU utilisation is real work, not a zeroed rate counter ---
                        // Presenting our own D2D frames is genuine 3D engine work, so the
                        // sampled utilisation has to exceed zero. "Utilization Percentage"
                        // is rate-based: collecting a query twice in one sample re-reads it
                        // with dt = 0 and reports 0 for the life of the process.
                        {
                            WidgetWindow? gw = null;
                            try
                            {
                                gw = new WidgetWindow(fhost, new WidgetConfig
                                {
                                    Id = "selftest-gpu-load",
                                    Width = 460,
                                    Height = 200,
                                    Graph = GraphKind.Cpu,
                                    ClickThrough = ClickThroughMode.LeftClickOnly,
                                });
                                gw.Create(hwnd);
                                fhost.TrackWidgetForTest(gw);

                                var gpu = fhost.Telemetry.Gpu;
                                if (gpu.Unsupported)
                                {
                                    Check(false, "GPU utilisation reads real engine work",
                                        "unsupported: " + gpu.UnsupportedReason);
                                }
                                else
                                {
                                    double peak = 0;
                                    int frames = 0, withEngines = 0;
                                    for (int f = 0; f < 150; f++)
                                    {
                                        fhost.Telemetry.SampleNow();
                                        gw.RequestRedraw();
                                        gw.RenderFrame(TestNow + f / 60.0, 1f / 60f);
                                        gw.CommitComposition();
                                        frames++;
                                        if (gpu.EngineSnapshot().Length > 0) withEngines++;
                                        peak = Math.Max(peak, gpu.LastUtilization);
                                        Thread.Sleep(4);
                                    }
                                    Check(withEngines > 0, "GPU engine rows exist while we render",
                                        $"{withEngines}/{frames} samples reported engine instances, " +
                                        $"peak {peak:0.00}%");
                                    Check(peak > 0, "GPU utilisation reflects our own rendering",
                                        $"peak {peak:0.00}% over {frames} presented frames " +
                                        $"(a rate counter sampled with dt = 0 stays at 0)");
                                }
                            }
                            catch (Exception ex)
                            {
                                failures++;
                                Log.Write("ERROR", "gpu load test threw: " + ex);
                            }
                            finally { try { gw?.Destroy(); } catch { } }
                        }

                        // --- 6i. header ink table (measured, every run) ---
                        // The header is judged on the pixels the painters produce, not on
                        // the arithmetic that produced them (docs/design.md rules 12-15).
                        {
                            WidgetWindow? hw = null;
                            try
                            {
                                hw = new WidgetWindow(fhost, new WidgetConfig
                                {
                                    Id = "selftest-header",
                                    Width = 460,
                                    Height = 200,
                                    // Byte axis, so a tick column exists and the X has to
                                    // anchor to it rather than to the corner inset.
                                    Graph = GraphKind.Memory,
                                    ClickThrough = ClickThroughMode.LeftClickOnly,
                                });
                                hw.Create(hwnd);
                                fhost.TrackWidgetForTest(hw);
                                for (int i = 0; i < 70; i++) fhost.Telemetry.SampleNow();
                                int cardW = hw.Surface!.Width, cardH = hw.Surface.Height;

                                void Frame(double at, float dt)
                                {
                                    hw!.RequestRedraw();
                                    hw.RenderFrame(at, dt);
                                    hw.CommitComposition();
                                }

                                // Locked row: the title and the value, nothing else.
                                Frame(TestNow, 1f / 60f);
                                Frame(TestNow + 1.0 / 60.0, 1f / 60f);
                                var lockPx = hw.Surface.CaptureToPixels(fhost.Device);
                                var locked = Elements(lockPx, cardW, cardH);
                                Check(locked.Count == 2, "locked row draws the title and the value, nothing else",
                                    $"{locked.Count} element(s): {Runs(locked)}");
                                if (locked.Count == 2)
                                {
                                    Check(Math.Abs(locked[0].Centre - cardW * 0.5f) <= 1.5f,
                                        "locked title is centred on the card",
                                        $"title ink {locked[0].Left}..{locked[0].Right}, centre {locked[0].Centre:0.0} vs {cardW * 0.5f:0.0}");
                                    Check(Math.Abs((cardW - HeaderLayout.Inset) - locked[1].Right) <= 1.5f,
                                        "locked value sits on the right inset",
                                        $"value ink ends {locked[1].Right}, inset edge {cardW - HeaderLayout.Inset:0.0}");
                                }

                                hw.BeginEdit();
                                // The edit fade runs on dt (8/s), so pump realistic frames
                                // until the badges are fully in.
                                for (int f = 0; f < 8; f++) Frame(TestNow + 0.2 + f * 0.05, 0.05f);
                                for (int f = 0; f < 2; f++) Frame(TestNow + 0.6 + f / 60.0, 1f / 60f);
                                var px = hw.Surface.CaptureToPixels(fhost.Device);
                                var e = Elements(px, cardW, cardH);
                                // X, chip, '<', title, '>', value, check.
                                Check(e.Count == 7, "edit row draws exactly its seven ink elements",
                                    $"{e.Count} element(s): {Runs(e)}");
                                if (e.Count == 7)
                                {
                                    Log.Info("header ink table: " + Runs(e));

                                    Check(Math.Abs(e[3].Centre - cardW * 0.5f) <= 1.5f,
                                        "title is centred on the card",
                                        $"title ink {e[3].Left}..{e[3].Right}, centre {e[3].Centre:0.0} vs {cardW * 0.5f:0.0}");

                                    // The tick column's own left ink, measured from the plot.
                                    // Scanned over the whole axis: the column's left edge
                                    // belongs to its widest tick, not to the short one that
                                    // happens to sit at the bottom.
                                    var (colInk, _) = GrayInkX(px, cardW, cardH, 0, cardW, 50);
                                    Check(Math.Abs(e[0].Left - colInk) <= 2,
                                        "X badge ink starts on the tick values' left edge",
                                        $"X ink starts {e[0].Left}, tick ink starts {colInk}");

                                    // Mirror with the corner clamp: the check may not come
                                    // closer to its edge than BadgeClear.
                                    double mirror = Math.Min(cardW - e[0].Left, cardW - HeaderLayout.BadgeClear);
                                    Check(Math.Abs(mirror - e[6].Right) <= 1.5f,
                                        "check badge mirrors the X, clamped at the corner",
                                        $"check ink ends {e[6].Right}, mirrored target {mirror:0.0}");

                                    // Every neighbouring pair keeps its gap, chrome pairs more.
                                    int[] pairs = { 0, 1, 2, 3, 4, 5 };
                                    bool gapsOk = true;
                                    var gapText = new System.Text.StringBuilder();
                                    foreach (int i in pairs)
                                    {
                                        int gap = e[i + 1].Left - e[i].Right - 1;
                                        float need = (i is 0 or 1 or 5) ? HeaderLayout.GapChrome : HeaderLayout.GapText;
                                        gapText.Append($"{gap}/{need:0} ");
                                        if (gap < need) gapsOk = false;
                                    }
                                    Check(gapsOk, "measured header gaps hold their design tokens",
                                        gapText.ToString().Trim());

                                    // One filled region: a glyph drawn as a union of strokes
                                    // composites twice where the strokes cross, so a few
                                    // pixels in the middle are brighter than the arms
                                    // (docs/design.md icon rule 24).
                                    foreach (int bi in new[] { 0, 6 })
                                    {
                                        var lum = InkLuminance(px, cardW, cardH, e[bi].Left, e[bi].Right, 4, 34);
                                        Check(lum.Length >= 8, $"{badgeName(bi)} has enough ink to judge",
                                            $"{lum.Length} ink pixel(s) in {e[bi].Left}..{e[bi].Right}");
                                        if (lum.Length >= 8)
                                            Check(PeakOverPlateau(lum) <= 0.10f,
                                                $"{badgeName(bi)} is one filled region, not overlapping strokes",
                                                $"peak {lum[0]} vs plateau {lum[(int)(lum.Length * 0.9f)]} " +
                                                $"(+{PeakOverPlateau(lum) * 100f:0}% over the plateau; a crossing is +100%)");
                                    }
                                }
                            }
                            catch (Exception ex) { failures++; Log.Write("ERROR", "header ink test threw: " + ex); }
                            finally { try { if (hw != null) fhost.ForgetWidgetForTest(hw); hw?.Destroy(); } catch { } }
                        }

                        // --- 6g. card contact sheet (--capture only) ---
                        // One capture per GraphKind on real telemetry, locked and edit,
                        // so every card can be eyeballed instead of assumed correct.
                        if (captureDir.Length > 0)
                        {
                            fw.Destroy();
                            fw = null;
                            for (int i = 0; i < 70; i++) fhost.Telemetry.SampleNow();
                            foreach (GraphKind kind in WidgetWindow.CardSheetKinds)
                            {
                                WidgetWindow? cw = null;
                                try
                                {
                                    cw = new WidgetWindow(fhost, new WidgetConfig
                                    {
                                        Id = "selftest-card-" + kind,
                                        Width = 460,
                                        Height = 200,
                                        Graph = kind,
                                        ClickThrough = ClickThroughMode.LeftClickOnly,
                                    });
                                    cw.Create(hwnd);
                                    fhost.TrackWidgetForTest(cw);
                                    // Flip model: the readback carries the previously
                                    // presented frame, so present twice before capturing.
                                    for (int f = 0; f < 2; f++)
                                    {
                                        cw.RequestRedraw();
                                        cw.RenderFrame(TestNow + f / 60.0, 1f / 60f);
                                        cw.CommitComposition();
                                    }
                                    cw.Surface!.CaptureToBmp(fhost.Device,
                                        System.IO.Path.Combine(captureDir, $"30-card-{kind}-locked.bmp"));
                                    cw.BeginEdit();
                                    // The edit fade runs on dt (8/s), so a couple of 1/60
                                    // frames would freeze the badges at their first
                                    // sliver. Pump realistic frames until it is full.
                                    for (int f = 0; f < 8; f++)
                                    {
                                        cw.RequestRedraw();
                                        cw.RenderFrame(TestNow + 0.2 + f * 0.05, 0.05f);
                                        cw.CommitComposition();
                                    }
                                    for (int f = 0; f < 2; f++)
                                    {
                                        cw.RequestRedraw();
                                        cw.RenderFrame(TestNow + 0.6 + f / 60.0, 1f / 60f);
                                        cw.CommitComposition();
                                    }
                                    cw.Surface.CaptureToBmp(fhost.Device,
                                        System.IO.Path.Combine(captureDir, $"30-card-{kind}-edit.bmp"));
                                    Log.Info($"captured card {kind} (locked + edit)");

                                    // A picked colour must reach the curve, not just the chip:
                                    // the same path the picker commits through.
                                    if (kind == GraphKind.Gpu)
                                    {
                                        const string Pick = "#FF3B30";
                                        cw.Config.SetLineColor(kind, Pick);
                                        fhost.OnWidgetConfigChanged(cw, livePreview: true);
                                        for (int f = 0; f < 2; f++)
                                        {
                                            cw.RequestRedraw();
                                            cw.RenderFrame(TestNow + 0.8 + f / 60.0, 1f / 60f);
                                            cw.CommitComposition();
                                        }
                                        var px = cw.Surface.CaptureToPixels(fhost.Device);
                                        // The stroke is a 1px line, so a flat 0% curve lands
                                        // around 54% of the picked colour. Match the hue, not
                                        // a brightness that depends on the data.
                                        int hit = CountPixels(px, cw.Surface.Width, cw.Surface.Height,
                                            x => true, y => y > 50,
                                            (r, g, b) => r > 100 && g < 90 && b < 90 && r > g * 2 && r > b * 2);
                                        Check(hit > 40, "a picked line colour reaches the curve",
                                            $"{hit} px tinted {Pick} in the plot (0 means the chip changed but the graph did not)");
                                        cw.Surface.CaptureToBmp(fhost.Device,
                                            System.IO.Path.Combine(captureDir, "30-card-Gpu-recolor.bmp"));
                                    }
                                }
                                catch (Exception ex) { Log.Write("ERROR", $"card {kind}: {ex}"); }
                                finally { try { cw?.Destroy(); } catch { } }
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        failures++;
                        Log.Write("ERROR", "freeze regression test threw: " + ex);
                    }
                    finally
                    {
                        try { fw?.Destroy(); } catch (Exception ex) { Log.Warn("freeze test widget destroy: " + ex.Message); }
                        try { fhost?.Dispose(); } catch (Exception ex) { Log.Warn("freeze test host dispose: " + ex.Message); }
                    }
                }
            }

            // ---------------------------------------------------------------- 7. DATA LAYER
            // Constructs the real samplers and checks counters return rows.
            // Zero is a legitimate idle value; where movement is required the test makes load.
            try
            {
                var tel = new Metrics.Telemetry();
                Log.Info("data layer: constructed Telemetry (cpu, memory, disk, network, gpu)");

                void Settle(int rounds, int gapMs = 220)
                {
                    for (int i = 0; i < rounds; i++) { tel.SampleNow(); Thread.Sleep(gapMs); }
                }

                Log.Info("data layer: priming 4 samples");
                // One source at a time so a native fault attributes to the current line.
                for (int round = 0; round < 4; round++)
                {
                    Log.Info($"  priming round {round}: cpu");
                    tel.Cpu.Update(tel.Time);
                    Log.Info($"  priming round {round}: memory");
                    tel.Memory.Update(tel.Time);
                    Log.Info($"  priming round {round}: disk");
                    tel.Disk.Update(tel.Time);
                    Log.Info($"  priming round {round}: network");
                    tel.Network.Update(tel.Time);
                    Log.Info($"  priming round {round}: gpu");
                    tel.Gpu.Update(tel.Time);
                    Thread.Sleep(220);
                }
                Log.Info("data layer: primed");

                // ---- CPU ---------------------------------------------------------------
                {
                    var before = tel.Cpu.Total.Latest;
                    var stop = new CancellationTokenSource();
                    var burners = new List<Thread>();
                    for (int i = 0; i < 2; i++)
                    {
                        var t = new Thread(() =>
                        {
                            double acc = 0;
                            while (!stop.IsCancellationRequested) { for (int k = 0; k < 20000; k++) acc += Math.Sqrt(k); }
                            GC.KeepAlive(acc);
                        })
                        { IsBackground = true, Name = "FlowMonitor.SelfTest.Burn" };
                        burners.Add(t);
                        t.Start();
                    }
                    Thread.Sleep(500);
                    Settle(2, 250);
                    stop.Cancel();
                    foreach (var t in burners) t.Join(700);

                    float after = tel.Cpu.Total.Latest;
                    Check(!float.IsNaN(before) && !float.IsNaN(after) && after > 0 && before >= 0 && after <= 100,
                        "CPU total responds to real load",
                        $"{before:0.0}% idle -> {after:0.0}% with two threads spinning (0 while spinning " +
                        "would mean the per-core differencing is not running)");
                    Log.Info($"cpu: idle {before:0.0}% -> loaded {after:0.0}% across {tel.Cpu.CoreCount} logical processors");
                }

                // ---- Memory -----------------------------------------------------------
                {
                    var m = tel.Memory;
                    long total = m.TotalPhysical, inUse = m.LastInUse;
                    Check(total > 0, "Memory reports a physical total",
                        $"{ChartRenderer.FormatBytes(total)} total, {ChartRenderer.FormatBytes(inUse)} in use");
                    Check(inUse > 0 && inUse < total && m.LastCommitted > 0 && m.LastCached > 0,
                        "Memory in use / cached / committed are all live and inside the physical total",
                        $"in use {ChartRenderer.FormatBytes(inUse)}, cached {ChartRenderer.FormatBytes(m.LastCached)}, " +
                        $"committed {ChartRenderer.FormatBytes(m.LastCommitted)} of {ChartRenderer.FormatBytes(total)} " +
                        "(0 or negative would mean GetPerformanceInfo is not being read)");
                    // Machine-scale guard: page/byte mixups and struct misalignments
                    // produce megabytes or terabytes on any realistic box.
                    const long GB = 1024L * 1024 * 1024;
                    Check(total >= 8 * GB && total <= 128 * GB,
                        "physical total is a real machine size",
                        $"{ChartRenderer.FormatBytes(total)} (phantom struct fields once gave terabytes here)");
                }

                // ---- Disk -------------------------------------------------------------
                // A zero here on an idle machine proves nothing, so the test creates the load.
                {
                    var d = tel.Disk;
                    if (d.Unavailable)
                    {
                        Check(false, "Disk counters are available", "unavailable: " + d.UnavailableReason);
                    }
                    else
                    {
                        // Temp file beside the executable (portable-safe location).
                        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "selftest-disk.tmp");

                        // Burst writes, then dropped; nothing left behind.
                        byte[] block = new byte[1 << 20];
                        new Random(1234).NextBytes(block);
                        Log.Info("data layer: writing 96 MB in 3 passes to force disk activity");
                        try
                        {
                            // PDH rates average BETWEEN two collects, so bracket the write with collects.
                            float peakWrite = 0, peakBusy = 0;
                            for (int pass = 0; pass < 3; pass++)
                            {
                                tel.Disk.Update(tel.Time);          // close the previous interval
                                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                               FileShare.None, 1 << 20, FileOptions.None))
                                {
                                    for (int i = 0; i < 32; i++) fs.Write(block, 0, block.Length);
                                    fs.Flush(true);
                                }
                                Thread.Sleep(120);
                                tel.Disk.Update(tel.Time);          // close the interval that contains the write
                                peakWrite = Math.Max(peakWrite, d.LastWrite);
                                peakBusy = Math.Max(peakBusy, d.LastBusy);
                                Thread.Sleep(200);
                            }

                            // Follow with a sustained in-flight sampled write; some stacks only report under load.
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                long written = 0;
                                const int chunk = 8 << 20;
                                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write,
                                                               FileShare.None, chunk, FileOptions.WriteThrough))
                                {
                                    var big = new byte[chunk];
                                    new Random(7).NextBytes(big);
                                    while (sw.Elapsed.TotalSeconds < 4.0 && written < (6L << 30))
                                    {
                                        fs.Write(big, 0, big.Length);
                                        fs.Flush(true);
                                        written += chunk;
                                        tel.Disk.Update(tel.Time);
                                        peakWrite = Math.Max(peakWrite, d.LastWrite);
                                        peakBusy = Math.Max(peakBusy, d.LastBusy);
                                    }
                                }
                                sw.Stop();
                                Log.Info($"disk: sustained write of {written / 1048576.0:N0} MiB in {sw.Elapsed.TotalSeconds:F2}s " +
                                         $"-> peaks {ChartRenderer.FormatBytes(peakWrite)}/s, {peakBusy:0.0}% active");
                            }

                            float w = peakWrite;
                            float busy = peakBusy;

                            // Requirement: live byte rates, or inert counters declared unavailable with a reason.
                            bool honest = d.ByteRatesUnavailable && !string.IsNullOrWhiteSpace(d.ByteRatesUnavailableReason);
                            // Three outcomes: live, honestly-unavailable, or neither (flag false with zero rates).
                            string detail = w > 0
                                ? $"{ChartRenderer.FormatBytes(w)}/s peak, {busy:0.0}% active time -- byte rates are live"
                                : honest
                                    ? $"byte rates stayed at 0 under load and the sampler declared them unavailable, " +
                                      $"with a reason: \"{d.ByteRatesUnavailableReason}\""
                                    : $"NEITHER: byte rates read 0 under a sustained write AND the sampler did NOT declare " +
                                      $"them unavailable (flag={d.ByteRatesUnavailable}, reason=\"{d.ByteRatesUnavailableReason ?? "<null>"}\", " +
                                      $"busy peaked at {busy:0.0}%). The % Disk Time the detector gates on does not move on this " +
                                      "storage stack even under load, so the detector can never fire here and the widget would " +
                                      "show two confident flat zero lines. That is the exact lie the detector exists to prevent.";
                            Check(w > 0 || honest,
                                "Disk byte-rate counters either respond to a real write, or are declared unavailable with a reason",
                                detail);
                            Log.Info($"disk: wrote 96 MB x3, counter peaks at {ChartRenderer.FormatBytes(w)}/s, {busy:0.0}% active, " +
                                     $"byteRatesUnavailable={d.ByteRatesUnavailable}");
                        }
                        finally
                        {
                            try { File.Delete(path); } catch { /* the OS will clean the output dir eventually */ }
                        }
                    }
                }

                // ---- Network ----------------------------------------------------------
                Log.Info("data layer: checking network");
                {
                    var n = tel.Network;
                    Check(n.Available, "Network picked a real up, non-loopback interface",
                        n.Available ? $"{n.InterfaceName} - {n.InterfaceDescription}"
                                    : "no up, non-loopback interface was found in GetIfTable2");
                    Check(!float.IsNaN(n.LastSend) && !float.IsNaN(n.LastRecv)
                          && n.SendBytes.Count > 0 && n.RecvBytes.Count > 0,
                        "Network send and receive series are being fed",
                        $"send {ChartRenderer.FormatBytes(n.LastSend)}/s, receive {ChartRenderer.FormatBytes(n.LastRecv)}/s, " +
                        $"{n.SendBytes.Count}/{n.RecvBytes.Count} samples");
                }

                // ---- GPU --------------------------------------------------------------
                Log.Info("data layer: checking gpu");
                {
                    var g = tel.Gpu;
                    if (g.Unsupported)
                    {
                        Check(false, "GPU counters are available", "unsupported: " + g.UnsupportedReason);
                    }
                    else
                    {
                        int engines = 0, adapters = 0;
                        string detail;
                        double util = 0;
                        _telemetryRead(tel, () =>
                        {
                            engines = g.EngineSnapshot().Length;
                            adapters = g.AdapterSnapshot().Length;
                            util = g.LastUtilization;
                        });
                        detail = $"{engines} engine type(s), {adapters} adapter(s), " +
                                 $"{util:0.0}% busy, {ChartRenderer.FormatBytes(g.LastDedicatedUsed)} dedicated VRAM";

                        // Zero engine types means the wildcard read is broken, not an idle GPU.
                        Check(engines > 0, "GPU engine counter returns instance rows",
                            detail + " -- 0 engine types means the wildcard read is broken, not that the GPU is idle");
                        Check(adapters > 0, "GPU adapter memory counter returns instance rows",
                            $"{ChartRenderer.FormatBytes(g.LastDedicatedUsed)} dedicated, " +
                            $"{ChartRenderer.FormatBytes(g.LastSharedUsed)} shared, " +
                            $"{ChartRenderer.FormatBytes(g.LastCommitted)} committed");
                        Check(!double.IsNaN(util) && util >= 0 && util <= 100,
                            "GPU headline utilisation is a finite 0-100 value", $"{util:0.0}%");
                        Log.Info("gpu: " + detail);

                        // Sampling rebuilds the engine/adapter lists every tick; the render
                        // thread reads them between ticks. Reading live lists used to throw
                        // "Collection was modified" mid-frame, which surfaced as a render
                        // fault. Hammer both sides to prove the reads are safe copies.
                        string? race = null;
                        int reads = 0;
                        using (var done = new ManualResetEventSlim(false))
                        {
                            var reader = new Thread(() =>
                            {
                                try
                                {
                                    while (!done.IsSet)
                                        foreach (var e in g.EngineSnapshot()) _ = e.Percent;
                                    foreach (var a in g.AdapterSnapshot()) _ = a.Luid;
                                }
                                catch (Exception ex) { race = "reader: " + ex.Message; }
                                finally { reads++; }
                            }) { IsBackground = true };
                            reader.Start();
                            for (int i = 0; i < 30; i++) { g.Update(TestNow + i); Thread.Sleep(1); }
                            done.Set();
                            reader.Join(2000);
                        }
                        Check(race is null && reads > 0,
                            "engine/adapter state is safe to read while sampling rebuilds it",
                            race ?? $"{reads} reader pass(es) over 30 rebuilds, no torn read");
                    }

                    // Temperature, power and fan RPM have no Windows counter at all. The sampler
                    // must say so rather than report a confident zero.
                    Log.Info("gpu: temperature / power / fan RPM have no Windows performance counter; " +
                             "these need NVML, ADL or Level Zero and are reported as unavailable by design");
                }

                tel.Dispose();
            }
            catch (Exception ex)
            {
                failures++;
                Log.Write("ERROR", "data layer test threw: " + ex);
            }

            Log.Info(failures == 0 ? "=== self test PASSED ===" : $"=== self test FAILED ({failures} assertion(s)) ===");
        }
        catch (Exception ex)
        {
            failures++;
            Log.Fatal(ex);
            Log.Info("=== self test FAILED ===");
        }
        finally
        {
            try { surface?.Dispose(); } catch (Exception ex) { Log.Warn("surface dispose: " + ex.Message); }
            try { resources?.Dispose(); } catch (Exception ex) { Log.Warn("resources dispose: " + ex.Message); }
            try { device?.Dispose(); } catch (Exception ex) { Log.Warn("device dispose: " + ex.Message); }
        }

        Console.WriteLine(failures == 0 ? "self test PASSED" : "self test FAILED");
        return failures;
    }

    // Seed range covers the snapshot; slot x never depends on these timestamps.
    const double Window = 60.0;
    const double TestNow = Window;          // draw clock, at the right edge of the window
    const double SeedStep = 0.25;            // 4 Hz source data
    const double Headroom = 2.0;             // lets the animation loop scroll past the edge

    // Enough samples to cover the window plus headroom: 70s at 0.25s = 281.
    const int SeedCount = 281;

    /// <summary>Reads the sampler's current snapshot under the telemetry lock.</summary>
    static void _telemetryRead(Metrics.Telemetry tel, Action reader) => tel.Read(reader);

    static void Seed(TimeSeries s, int n, double endT)
        => Seed(s, n, endT, 0);

    /// <summary>
    /// Seeds a series with its own waveform (per-series phase + harmonic).
    /// Shared waveforms would overlay all 32 per-core curves into one line.
    /// </summary>
    static void Seed(TimeSeries s, int n, double endT, int seriesIndex)
    {
        // Band-limited: 4 Hz sampling caps content well under 0.5 Hz to avoid aliasing.
        double phase = seriesIndex * 0.41;
        double rate = 0.30 + 0.010 * (seriesIndex % 9);   // <= 0.38 rad/s, ~0.06 Hz
        double amp = 26 + 3 * (seriesIndex % 6);
        double bias = (seriesIndex % 5) * 4.0 - 8.0;
        double t0 = endT - (n - 1) * SeedStep;
        for (int i = 0; i < n; i++)
        {
            double t = t0 + i * SeedStep;
            double v = 46 + bias
                     + amp * Math.Sin((t + phase) * rate)
                     + amp * 0.35 * Math.Cos((t + phase) * rate * 0.37);
            s.Add(t, (float)Math.Clamp(v, 0, 100));
        }
    }

    /// <summary>
    /// Fills a test model's snapshots from its seeded rings. Production does this in
    /// DesktopHost.BuildChart under the telemetry lock; tests own their data outright.
    /// </summary>
    static void Snap(ChartModel m)
    {
        m.SampleIntervalSec = 1.0;
        // MC default: 60 points spread across the plot (performance-page-data-points).
        m.VisiblePoints = 60;
        double newest = 0;
        foreach (var s in m.Series)
        {
            s.Snapshot = s.Data.SnapshotLatest(ChartModel.FixedSlots);
            if (s.Data.NewestTime > newest) newest = s.Data.NewestTime;
        }
        m.LastSampleTime = newest;
    }

    static bool IsAccent(byte[] px, int i)
    {
        byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
        return a > 200 && b > 190 && g > 150 && g < 235 && r > 30 && r < 130;
    }

    /// <summary>
    /// Column of the topmost accent pixel inside the plot (curve peak); -1 when absent.
    /// Starts below the header so text fringes are never mistaken for the curve.
    /// </summary>
    static int FindPeakColumn(byte[] px, int w, int h)
    {
        for (int y = 44; y < h; y++)
            for (int x = 0; x < w; x++)
                if (IsAccent(px, (y * w + x) * 4)) return x;
        return -1;
    }

    /// <summary>Accent pixels inside an x span and y band.</summary>
    static int CountAccentInX(byte[] px, int w, int h, int xLo, int xHi, int yLo, int yHi)
    {
        int n = 0;
        for (int y = Math.Max(0, yLo); y < Math.Min(h, yHi); y++)
            for (int x = Math.Max(0, xLo); x < Math.Min(w, xHi); x++)
                if (IsAccent(px, (y * w + x) * 4)) n++;
        return n;
    }

    /// <summary>Min/max column holding an accent pixel inside a y band; (-1,-1) when absent.</summary>
    static (int minX, int maxX) MinMaxAccentX(byte[] px, int w, int h, int yLo, int yHi)
    {
        int minX = int.MaxValue, maxX = -1;
        for (int y = Math.Max(0, yLo); y < Math.Min(h, yHi); y++)
            for (int x = 0; x < w; x++)
                if (IsAccent(px, (y * w + x) * 4)) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
        return maxX < 0 ? (-1, -1) : (minX, maxX);
    }

    /// <summary>
    /// Min/max column holding a dim gray (axis-label) pixel inside a box; (-1,-1) when
    /// absent. Axis text is white at 34% over the dark panel, so it reads as gray.
    /// </summary>
    static (int minX, int maxX) GrayInkX(byte[] px, int w, int h, int xLo, int xHi, int yLo)
    {
        int minX = int.MaxValue, maxX = -1;
        for (int y = Math.Max(0, yLo); y < h; y++)
            for (int x = Math.Max(0, xLo); x < Math.Min(w, xHi); x++)
            {
                int i = (y * w + x) * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                int lo = Math.Min(b, Math.Min(g, r)), hi = Math.Max(b, Math.Max(g, r));
                if (a > 100 && hi - lo <= 14 && hi >= 45) { if (x < minX) minX = x; if (x > maxX) maxX = x; }
            }
        return maxX < 0 ? (-1, -1) : (minX, maxX);
    }

    /// <summary>
    /// Min/max column holding a full-brightness white (badge glyph at hover) pixel inside
    /// a box; (-1,-1) when absent. Only the hover-brightened badge reaches 200+, so the
    /// header text and axis labels cannot be mistaken for it.
    /// </summary>
    static (int minX, int maxX) BrightInkX(byte[] px, int w, int h, int xLo, int xHi, int yLo, int yHi)
    {
        int minX = int.MaxValue, maxX = -1;
        for (int y = Math.Max(0, yLo); y < Math.Min(h, yHi); y++)
            for (int x = Math.Max(0, xLo); x < Math.Min(w, xHi); x++)
            {
                int i = (y * w + x) * 4;
                if (px[i + 3] > 200 && px[i + 2] > 200 && px[i + 1] > 200 && px[i] > 200)
                { if (x < minX) minX = x; if (x > maxX) maxX = x; }
            }
        return maxX < 0 ? (-1, -1) : (minX, maxX);
    }

    /// <summary>
    /// One horizontal run of header ink: (first column, last column). Measured out of
    /// the rendered pixels, never out of the layout's own arithmetic (design.md 12).
    /// </summary>
    readonly record struct InkRun(int Left, int Right)
    {
        public float Centre => (Left + Right) * 0.5f;
    }

    /// <summary>
    /// Ink predicate for the header strip: near-white glyphs and text, or a saturated
    /// chip. Gridlines (white at 5%) and the panel fill stay well under the threshold.
    /// </summary>
    static string badgeName(int element) => element == 0 ? "X badge" : "check badge";

    /// <summary>Luminance of every ink pixel in a box, brightest first.</summary>
    static int[] InkLuminance(byte[] px, int w, int h, int xLo, int xHi, int yLo, int yHi)
    {
        var list = new List<int>();
        for (int y = Math.Max(0, yLo); y <= Math.Min(h - 1, yHi); y++)
            for (int x = Math.Max(0, xLo); x <= Math.Min(w - 1, xHi); x++)
            {
                int i = (y * w + x) * 4;
                if (!HeaderInk(px, i)) continue;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                list.Add((r * 299 + g * 587 + b * 114) / 1000);
            }
        list.Sort();
        list.Reverse();
        return list.ToArray();
    }

    /// <summary>How far the brightest pixel runs past the glyph's plateau luminance.</summary>
    static float PeakOverPlateau(int[] lum)
    {
        if (lum.Length < 8) return 0f;
        int plateau = lum[(int)(lum.Length * 0.9f)];
        return plateau <= 0 ? 0f : (lum[0] - plateau) / (float)plateau;
    }

    static bool HeaderInk(byte[] px, int i)
    {
        byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
        if (a < 100) return false;
        int lo = Math.Min(b, Math.Min(g, r)), hi = Math.Max(b, Math.Max(g, r));
        return hi >= 110 && (lo >= 90 || hi - lo >= 40);
    }

    /// <summary>
    /// Column runs of header ink in a horizontal band, merging gaps under
    /// <paramref name="merge"/> px so anti-aliasing cannot split one glyph in two.
    /// </summary>
    static List<InkRun> InkRuns(byte[] px, int w, int h, int yLo, int yHi, int merge = 2)
    {
        var runs = new List<InkRun>();
        int start = -1, last = -1;
        for (int x = 0; x < w; x++)
        {
            bool hit = false;
            for (int y = Math.Max(0, yLo); y < Math.Min(h, yHi); y++)
                if (HeaderInk(px, (y * w + x) * 4)) { hit = true; break; }
            if (!hit) continue;
            if (start < 0) start = last = x;
            else if (x - last <= merge + 1) last = x;
            else { runs.Add(new InkRun(start, last)); start = last = x; }
        }
        if (start >= 0) runs.Add(new InkRun(start, last));
        return runs;
    }

    /// <summary>
    /// Ink runs joined into elements: a gap at least <c>minGap</c> starts a new element,
    /// so one word split by its own letter spacing stays one element (design.md 12).
    /// </summary>
    static List<InkRun> Elements(byte[] px, int w, int h, float minGap = HeaderLayout.GapText)
    {
        var runs = InkRuns(px, w, h, 6, 32);
        if (minGap <= 0 || runs.Count < 2) return runs;
        int gap = (int)minGap;
        var merged = new List<InkRun>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.Left - merged[^1].Right - 1 < gap)
                merged[^1] = new InkRun(merged[^1].Left, r.Right);
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>Compact "l..r" list of ink runs, for assertion messages.</summary>
    static string Runs(List<InkRun> runs)
    {
        var parts = new string[runs.Count];
        for (int i = 0; i < runs.Count; i++) parts[i] = $"[{runs[i].Left}..{runs[i].Right}]";
        return string.Join(" ", parts);
    }

    /// <summary>Whether any accent pixel sits within (dx, dy) of a point.</summary>
    static bool AccentNear(byte[] px, int w, int h, int x, int y, int dx, int dy)
    {
        for (int yy = Math.Max(0, y - dy); yy <= Math.Min(h - 1, y + dy); yy++)
            for (int xx = Math.Max(0, x - dx); xx <= Math.Min(w - 1, x + dx); xx++)
                if (IsAccent(px, (yy * w + xx) * 4)) return true;
        return false;
    }

    /// <summary>
    /// Counts distinct rows with accent pixels; flat lines touch 1-2 rows, curves sweep many.
    /// </summary>
    static int CountAccentRows(byte[] px, int w, int h)
    {
        var seen = new bool[h];
        int rows = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                // Back buffer is B8G8R8A8 premultiplied; accent (0.298, 0.761, 1.0) -> B=255 G=194 R=76.
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                if (a > 200 && b > 190 && g > 150 && g < 235 && r > 30 && r < 130)
                {
                    if (!seen[y]) { seen[y] = true; rows++; }
                    break;
                }
            }
        }
        return rows;
    }

    /// <summary>
    /// Counts near-white pixels in a region; proves the header value is on screen.
    /// </summary>
    static int CountPixels(byte[] px, int w, int h, Func<int, bool> inX, Func<int, bool> inY,
        Func<byte, byte, byte, bool> match)
    {
        int n = 0;
        for (int y = 0; y < h; y++)
        {
            if (!inY(y)) continue;
            for (int x = 0; x < w; x++)
            {
                if (!inX(x)) continue;
                int i = (y * w + x) * 4;
                if (px[i + 3] > 128 && match(px[i + 2], px[i + 1], px[i])) n++;
            }
        }
        return n;
    }

    /// <summary>
    /// Counts horizontal text bands in the left axis strip (one per label).
    /// Bands need 3+ blank rows of separation.
    /// </summary>
    // Scans only the plot area so the header cannot pad the count.
    static int CountTextBands(byte[] px, int w, int h, int yFrom)
    {
        int bands = 0;
        // Prime the gap so the first label at the scan top still counts.
        int gap = 3;
        for (int y = yFrom; y < h; y++)
        {
            int hits = 0;
            for (int x = 12; x < 60 && x < w; x++)
            {
                int i = (y * w + x) * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (a > 100 && mx > 70 && mx < 215 && (mx - mn) < 45) hits++;
            }
            if (hits >= 2) { if (gap >= 3) bands++; gap = 0; }
            else gap++;
        }
        return bands;
    }

    static ChartModel BuildSineModel(int w, int h, bool percent = true, double axisMax = 100,
        string valueUnit = "%") => new()
    {
        Title = "CPU",
        Subtitle = "self test",
        Series = [new ChartSeries { Name = "CPU", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.30f, 0.76f, 1f, 1f), Unit = "%" }],
        AxisMax = axisMax,
        PercentAxis = percent,
        ValueText = "42.3",
        ValueUnit = valueUnit,
        MinMaxText = "Min 3.1%    Avg 48.0%    Max 97.4%",
        WindowSeconds = 60,
    };

    static ChartModel BuildManySeriesModel(int w, int h, int count)
    {
        var list = new List<ChartSeries>();
        // Distinct hue per core (golden-ratio stepping maximises spread).
        for (int i = 0; i < count; i++)
        {
            float hue = (i * 0.6180339887f) % 1f;   // golden-ratio stepping, maximises spread
            var (r, g, b) = HsvToRgb(hue, 0.62f, 1f);
            list.Add(new ChartSeries
            {
                Name = "Core " + i,
                Data = new TimeSeries(),
                Color = new Vortice.Mathematics.Color4(r, g, b, 0.85f),
                Secondary = true,
            });
        }
        return new ChartModel
        {
            Title = "CPU",
            Subtitle = $"{count} logical processors",
            Series = list,
            AxisMax = 100,
            PercentAxis = true,
            ValueText = "18.7",
            ValueUnit = "%",
            WindowSeconds = 60,
        };
    }

    static (float r, float g, float b) HsvToRgb(float h, float s, float v)
    {
        int i = (int)(h * 6f);
        float f = h * 6f - i;
        float p = v * (1f - s), q = v * (1f - f * s), t = v * (1f - (1f - f) * s);
        return (i % 6) switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    /// <summary>Seeds a series in BYTES to match the 32 GB stacked-memory axis.</summary>
    static void SeedBytes(TimeSeries s, int n, double endT, int seriesIndex, double gib)
    {
        const double G = 1073741824.0;
        double phase = seriesIndex * 1.7;
        double t0 = endT - (n - 1) * SeedStep;
        for (int i = 0; i < n; i++)
        {
            double t = t0 + i * SeedStep;
            double v = gib * G * (0.90 + 0.10 * Math.Sin((t + phase) * 0.05));
            s.Add(t, (float)v);
        }
    }

    static ChartModel BuildStackedModel(int w, int h) => new()
    {
        Title = "Memory",
        Subtitle = "self test",
        Series =
        [
            new ChartSeries { Name = "In use", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.30f, 0.76f, 1f, 1f) },
            new ChartSeries { Name = "Cached", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.36f, 0.62f, 0.85f, 1f) },
            new ChartSeries { Name = "Available", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.20f, 0.30f, 0.38f, 1f) },
        ],
        Stacked = true,
        AxisMax = 32L * 1024 * 1024 * 1024,
        PercentAxis = true,
        ValueText = "14.2 GB",
        WindowSeconds = 60,
    };

    static ChartModel BuildBytesModel(int w, int h) => new()
    {
        Title = "Ethernet",
        Subtitle = "Intel I225-V",
        Series =
        [
            new ChartSeries { Name = "Send", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.30f, 0.74f, 0.80f, 1f) },
            new ChartSeries { Name = "Receive", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.24f, 0.55f, 0.86f, 1f) },
        ],
        AxisMax = 12_000_000,
        AutoRange = true,
        ValueText = "1.4 MB",
        ValueUnit = "/s",
        WindowSeconds = 60,
    };

    // ---------------------------------------------------------------- helpers

    /// <summary>Counts painted pixels (non-zero alpha); blank widgets produce 0.</summary>
    static int CountOpaquePixels(RenderDevice device, WidgetSurface? surface)
    {
        if (surface is null) return 0;
        byte[] px = surface.CaptureToPixels(device);
        int n = 0;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) n++;
        Log.Info($"CountOpaquePixels: {surface.Width}x{surface.Height}, {px.Length} bytes, {n} with alpha>0");
        return n;
    }

    /// <summary>
    /// No-op render host exercising a real WidgetWindow without telemetry or widget store.
    /// </summary>
    sealed class TestRenderHost : IRenderHost
    {
        public TestRenderHost(RenderDevice device, ResourceCache resources)
        {
            Device = device;
            Resources = resources;
        }

        public RenderDevice Device { get; }
        public ResourceCache Resources { get; }

        public ChartModel BuildChart(WidgetConfig cfg, double now)
        {
            var m = BuildSineModel(cfg.Width, cfg.Height);
            Seed(m.Series[0].Data, SeedCount, TestNow + Headroom);
            Snap(m);
            return m;
        }
        public ChartModel Retint(WidgetConfig cfg, ChartModel model) => model;
        public void OnWidgetGeometryChanged(WidgetWindow w, int x, int y, int width, int height) { }
        public void SetMenuOpen(bool open) { }
        public void ShowContextMenu(WidgetWindow widget, int x, int y) { }
        public void CloseWidget(WidgetWindow widget) { }
        public void CloseWidget(WidgetWindow widget, bool deleteSaved) { }
        public void OnWidgetConfigChanged(WidgetWindow widget, bool livePreview) { }
    }
}

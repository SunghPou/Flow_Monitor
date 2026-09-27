using System.Runtime.InteropServices;
using FlowMonitor.Graphs;
using FlowMonitor.Interop;
using FlowMonitor.Model;
using FlowMonitor.Rendering;
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
                    new WidgetConfig { CornerRadius = 8 }, surface.Width, surface.Height, 1f);
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

            // Scan from the plot top so title/subtitle are excluded; expect 5 labels.
            int plotTopScan = 44;
            int labelBands = CountTextBands(pxA, surface.Width, surface.Height, plotTopScan);
            Check(labelBands >= 5, "all five axis labels render inside the plot area",
                $"{labelBands} text bands from y={plotTopScan} down the left axis strip (expected 5; the header is excluded so it cannot pad the count)");

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
                            engines = g.Engines.Count;
                            adapters = g.Adapters.Count;
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

    static ChartModel BuildSineModel(int w, int h) => new()
    {
        Title = "CPU",
        Subtitle = "self test",
        Series = [new ChartSeries { Name = "CPU", Data = new TimeSeries(), Color = new Vortice.Mathematics.Color4(0.30f, 0.76f, 1f, 1f), Unit = "%" }],
        AxisMax = 100,
        PercentAxis = true,
        ValueText = "42.3",
        ValueUnit = "%",
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
        public void OnWidgetGeometryChanged(WidgetWindow w, int x, int y, int width, int height) { }
        public void SetMenuOpen(bool open) { }
        public void ShowContextMenu(WidgetWindow widget, int x, int y) { }
        public void CloseWidget(WidgetWindow widget) { }
        public void CloseWidget(WidgetWindow widget, bool deleteSaved) { }
        public void OnWidgetConfigChanged(WidgetWindow widget, bool livePreview) { }
    }
}

using FlowMonitor.Model;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Color4 = Vortice.Mathematics.Color4;
using RectF = System.Drawing.RectangleF;
using V2 = System.Numerics.Vector2;

namespace FlowMonitor.Rendering;

/// <summary>
/// Draws a Task-Manager-style performance chart onto a Direct2D device context.
/// Slot-indexed (MC parity): 600 fixed slots, index 0 newest, gliding in from one slot
/// past the right edge per sample interval; history shifts exactly one slot per tick,
/// never with the frame clock. NaN slots render as gaps. Smoothing is a monotone cubic
/// with horizontal tangents through every sample (no overshoot, apex stays on its slot).
/// The renderer also tracks whether the produced frame is pixel-identical to the previous
/// one, which is how the widget reaches near-zero GPU cost while a curve is flat.
/// </summary>
public sealed class ChartRenderer
{
    sealed class Slot
    {
        public V2[] Points = Array.Empty<V2>();
        public float[] Front = Array.Empty<float>();
        public float[] Back = Array.Empty<float>();
        public bool FrontIsA = true;
    }

    readonly RenderDevice _device;
    readonly ResourceCache _res;
    readonly List<Slot> _slots = new();

    bool _frameChanged = true;

    public bool FrameChanged => _frameChanged;

    static readonly Color4 GridLine = new(1f, 1f, 1f, 0.050f);
    static readonly Color4 GridLineStrong = new(1f, 1f, 1f, 0.095f);
    static readonly Color4 AxisText = new(1f, 1f, 1f, 0.34f);
    static readonly Color4 TitleText = new(1f, 1f, 1f, 0.90f);
    static readonly Color4 SubtitleText = new(1f, 1f, 1f, 0.46f);
    static readonly Color4 ValueText = new(1f, 1f, 1f, 1f);
    static readonly Color4 MinMaxText = new(1f, 1f, 1f, 0.42f);
    static readonly Color4 PeakLine = new(1f, 1f, 1f, 0.28f);

    /// <summary>RectF (System.Drawing) -> Vortice.Mathematics.Rect, which is what the draw calls take.</summary>
    static Rect R(RectF r) => new(r);

    /// <summary>Builds a path geometry from a polyline in one shot.</summary>
    ID2D1PathGeometry BuildPolyline(V2[] pts, FigureBegin begin)
    {
        var geom = _device.D2DFactory.CreatePathGeometry();
        using (var sink = geom.Open())
        {
            sink.BeginFigure(pts[0], begin);
            sink.AddLines(pts);
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        return geom;
    }

    public ChartRenderer(RenderDevice device, ResourceCache res)
    {
        _device = device;
        _res = res;
    }

    /// <summary>
    /// Draws the chart. Target size is passed explicitly; cfg holds the configured
    /// size, which may disagree with the actual render target.
    /// </summary>
    public void Draw(ID2D1DeviceContext dc, WidgetConfig cfg, ChartModel model, float dpi, double now,
        float width, float height)
    {
        _frameChanged = false;
        float s = dpi / 96f;
        var bounds = new RectF(0, 0, width, height);

        // ---------------------------------------------------------------- header
        // Title + value only, laid out by HeaderLayout (docs/design.md): the
        // < title > group is centred on the card, the value is right-anchored, and
        // the title yields width so the two can never touch.
        float pad = 12f * s;
        if (cfg.ShowLabels)
        {
            string text = !cfg.ShowUnits || string.IsNullOrEmpty(model.ValueUnit)
                ? model.ValueText
                : model.ValueText + (TightUnit(model.ValueUnit) ? "" : " ") + model.ValueUnit;

            float titleW = _res.Measure(model.Title, _res.Title, s).Width / s;
            float valueW = _res.Measure(text, _res.HeaderValue, s).Width / s;
            float logicalW = bounds.Width / s;
            var header = Widgets.HeaderLayout.Compute(logicalW, titleW, valueW, model.EditChrome);

            var titleRect = new RectF(header.Title.X * s, header.Title.Y * s,
                header.Title.Width * s, header.Title.Height * s);
            dc.DrawText(model.Title, _res.Title, R(titleRect), _res.Brush(TitleText));
            var valueRect = new RectF(header.Value.X * s, header.Value.Y * s,
                header.Value.Width * s, header.Value.Height * s);
            dc.DrawText(FitValue(text, header.ValueMax, s), _res.HeaderValue, R(valueRect),
                _res.Brush(ValueText));
            // No subtitle: the header is title + value only. model.Subtitle is kept
            // as data but never drawn.
        }

        // ---------------------------------------------------------------- plot rect
        // plotTop clears the title/value row; the subtitle slot stays empty air.
        float plotTop = cfg.ShowLabels ? 50f * s : pad;
        float plotLeft = pad;
        float plotRight = bounds.Width - pad;
        float plotBottom = bounds.Height - pad;
        if (cfg.ShowMinMax && bounds.Height > 120f * s)
            plotBottom -= 14f * s;
        double axisMax = model.AutoRange ? ComputeAutoMax(model) : model.AxisMax;
        if (axisMax <= 0) axisMax = 100;

        // Byte axes reserve a left gutter sized to their own widest label, so the labels
        // sit outside the plot, never clip, and the curve never runs under them; percent
        // axes draw no labels and span the full width.
        string[] axisLabels = ShowsAxisLabels(model, axisMax) ? AxisLabels(model, axisMax) : [];
        float gutter = 0f, labelInset = 0f;
        if (axisLabels.Length > 0)
        {
            // In edit mode the bottom-left corner bracket reaches CornerSpan into the card,
            // so the label column starts past it; otherwise the bracket arm crosses "0 B".
            labelInset = AxisLabelInset(model.EditChrome, s);
            foreach (string label in axisLabels)
                gutter = Math.Max(gutter, _res.Measure(label, _res.AxisRight, s).Width
                    + Widgets.HeaderLayout.GapText * s + labelInset);
            plotLeft += gutter;
        }

        // RectangleF is (x, y, width, height).
        var plot = new RectF(plotLeft, plotTop, plotRight - plotLeft, plotBottom - plotTop);
        if (plot.Width < 8f || plot.Height < 8f) return;

        // ---------------------------------------------------------------- gridlines
        // Labels are painted after the curves so the curve never strikes through them.
        if (cfg.ShowGrid)
            DrawGridLines(dc, plot, model, axisMax, s);

        // ---------------------------------------------------------------- curves
        // MC parity: the newest VisiblePoints samples spread across the plot width,
        // index 0 newest at the right edge (MC dataset plot(): x = width - x*spacing).
        // With sliding on, a fresh sample glides in from one slot past the right edge
        // as the interval elapses (enter = slotW * (1 - progress)), so each tick lands
        // exactly where the glide ended — no snap, no clock in x. Slide width uses
        // MC's subtract-2 (animating) vs subtract-1 (static) point_spacing_factor.
        int slots = Math.Clamp(model.VisiblePoints, 2, ChartModel.FixedSlots);
        bool glide = cfg.SlidingGraphs && model.SampleIntervalSec > 0 && model.LastSampleTime > 0;
        double slotW = SlotWidth(plot, slots, glide);
        float enterX = 0f;
        if (glide)
        {
            double progress = Math.Clamp((now - model.LastSampleTime) / model.SampleIntervalSec, 0, 1);
            enterX = (float)(slotW * (1 - progress));
            if (enterX != 0f) _frameChanged = true;
        }

        // Rounded-clip equivalent: the entering/exiting slots live past the plot edges.
        dc.PushAxisAlignedClip(R(plot), AntialiasMode.Aliased);
        try
        {
            if (model.Stacked)
                DrawStacked(dc, cfg, model, plot, axisMax, s, slots, slotW, enterX);
            else
                DrawLines(dc, cfg, model, plot, axisMax, s, slots, slotW, enterX);
        }
        finally { dc.PopAxisAlignedClip(); }

        // Axis labels outside the plot, painted after the curves.
        DrawAxisLabels(dc, plot, axisLabels, gutter, labelInset, s);

        if (_slots.Count > model.Series.Count) _frameChanged = true;

        // ---------------------------------------------------------------- min / max
        if (cfg.ShowMinMax && model.Series.Count > 0 && bounds.Height > 120f * s)
        {
            var mmRect = new RectF(pad, plotBottom + 1f * s, bounds.Width - pad * 2f, 13f * s);
            dc.DrawText(model.MinMaxText, _res.Micro, R(mmRect), _res.Brush(MinMaxText));
        }
    }

    // ------------------------------------------------------------------ frame diffing

    void DrawLines(ID2D1DeviceContext dc, WidgetConfig cfg, ChartModel model, RectF plot,
        double axisMax, float s, int slots, double slotW, float enterX)
    {
        EnsureSlotCapacity(model.Series.Count, slots);
        // Overlaid series divide the fill: MC sets the all-threads group's opacity to
        // 100/255/cpu_count (performance_page/cpu.rs, GRAPH_SELECTION_ALL_THREADS), so
        // 16 overlapping core fills read as tints instead of one saturated block.

        for (int si = 0; si < model.Series.Count; si++)
        {
            var series = model.Series[si];
            var slot = _slots[si];
            var prev = slot.FrontIsA ? slot.Front : slot.Back;
            var cur = slot.FrontIsA ? slot.Back : slot.Front;
            bool changed = false;

            int avail = Math.Min(slots, series.Snapshot.Length);
            for (int i = 0; i < slots; i++)
            {
                float v = i < avail ? series.Snapshot[i] : float.NaN;
                cur[i] = v;
                if (!SameSample(prev[i], v)) changed = true;
                slot.Points[i] = SlotPoint(plot, slotW, i, v, axisMax, enterX);
            }

            bool primary = !series.Secondary;
            float width = Math.Max(0.75f, cfg.LineThickness * s * (primary ? 1f : 0.75f));
            DrawRuns(dc, cfg, slot.Points, slots, plot, series.Color, width,
                series.Fill && primary ? cfg.FillOpacity / model.Series.Count : 0f);

            slot.FrontIsA = !slot.FrontIsA;
            if (changed) _frameChanged = true;
        }
    }

    /// <summary>Stacked bands: In use / Cached / Available, exactly like Task Manager's memory graph.</summary>
    void DrawStacked(ID2D1DeviceContext dc, WidgetConfig cfg, ChartModel model, RectF plot,
        double axisMax, float s, int slots, double slotW, float enterX)
    {
        EnsureSlotCapacity(model.Series.Count, slots);

        // Cumulative edge below the current band; a slot stays valid only while every
        // contributor is valid, so a gap in any series opens a gap in all bands above it.
        var below = new float[slots];
        var belowValid = new bool[slots];
        Array.Fill(belowValid, true);
        var bot = new V2[slots];

        for (int si = 0; si < model.Series.Count; si++)
        {
            var series = model.Series[si];
            var slot = _slots[si];
            var prev = slot.FrontIsA ? slot.Front : slot.Back;
            var cur = slot.FrontIsA ? slot.Back : slot.Front;
            bool changed = false;

            int avail = Math.Min(slots, series.Snapshot.Length);
            for (int i = 0; i < slots; i++)
            {
                float v = i < avail ? series.Snapshot[i] : float.NaN;
                cur[i] = v;
                if (!SameSample(prev[i], v)) changed = true;

                float bOld = below[i];
                bool bOk = belowValid[i];
                bool ok = !float.IsNaN(v) && bOk;
                float c = ok ? bOld + v : float.NaN;
                slot.Points[i] = SlotPoint(plot, slotW, i, c, axisMax, enterX);
                bot[i] = bOk ? SlotPoint(plot, slotW, i, bOld, axisMax, enterX)
                             : new V2((float)(plot.Right - i * slotW) + enterX, float.NaN);
                below[i] = c;
                belowValid[i] = ok;
            }

            bool primary = si == 0;
            float width = Math.Max(0.75f, cfg.LineThickness * s * (primary ? 1f : 0.7f));
            var color = primary ? series.Color
                : new Color4(series.Color.R, series.Color.G, series.Color.B, 0.55f);
            DrawRuns(dc, cfg, slot.Points, slots, plot, color, width,
                cfg.FillOpacity, si == 0 ? null : bot);

            slot.FrontIsA = !slot.FrontIsA;
            if (changed) _frameChanged = true;
        }
    }

    void EnsureSlotCapacity(int seriesCount, int cols)
    {
        while (_slots.Count < seriesCount) _slots.Add(new Slot());
        for (int i = 0; i < seriesCount; i++)
        {
            var slot = _slots[i];
            if (slot.Points.Length != cols)
            {
                slot.Points = new V2[cols];
                slot.Front = new float[cols];
                slot.Back = new float[cols];
                slot.FrontIsA = true;
                _frameChanged = true;
            }
        }
    }

    void DrawStackBand(ID2D1DeviceContext dc, V2[] top, V2[]? bottom, RectF plot, Color4 color, float opacity)
    {
        int n = top.Length;
        var poly = new V2[n * 2];
        Array.Copy(top, 0, poly, 0, n);
        if (bottom is null)
        {
            for (int i = 0; i < n; i++)
                poly[n + i] = new V2(top[n - 1 - i].X, plot.Bottom);
        }
        else
        {
            for (int i = 0; i < n; i++)
                poly[n + i] = bottom[n - 1 - i];
        }

        using var geom = BuildPolyline(poly, FigureBegin.Filled);

        float a = Math.Clamp(opacity, 0f, 1f);
        using var stops = dc.CreateGradientStopCollection(
        [
            new GradientStop(0f, new Color4(color.R, color.G, color.B, a)),
            new GradientStop(1f, new Color4(color.R, color.G, color.B, a * 0.75f)),
        ], Gamma.Linear, ExtendMode.Clamp);
        // Span the band's own vertical extent, not the whole plot.
        float bandTop = plot.Bottom, bandBottom = plot.Top;
        for (int i = 0; i < n; i++)
        {
            bandTop = Math.Min(bandTop, top[i].Y);
            bandBottom = Math.Max(bandBottom, top[i].Y);
        }
        if (bottom is not null)
            for (int i = 0; i < n; i++)
                bandBottom = Math.Max(bandBottom, bottom[i].Y);
        else
            bandBottom = plot.Bottom;
        if (bandBottom - bandTop < 1f) bandBottom = bandTop + 1f;
        var props = new LinearGradientBrushProperties(new V2(plot.Left, bandTop), new V2(plot.Left, bandBottom));
        using var brush = dc.CreateLinearGradientBrush(props, stops);
        dc.FillGeometry(geom, brush);
    }

    // ------------------------------------------------------------------ grid

    void DrawGridLines(ID2D1DeviceContext dc, RectF plot, ChartModel model, double axisMax, float s)
    {
        var soft = _res.Brush(GridLine);
        var strong = _res.Brush(GridLineStrong);
        var stroke = _res.Hairline;

        for (int i = 0; i <= 4; i++)
        {
            float y = plot.Bottom - (float)(plot.Height * (i / 4.0));
            bool edge = i == 0 || i == 4;
            dc.DrawLine(new V2(plot.Left, y), new V2(plot.Right, y), edge ? strong : soft, 1f, stroke);
        }
    }

    /// <summary>Percent axes draw no left-side labels; byte axes keep theirs.</summary>
    static bool ShowsAxisLabels(ChartModel model, double axisMax)
        => model.ShowAxisLabels && !model.PercentAxis && axisMax > 0;

    /// <summary>
    /// Byte-axis tick labels, right-aligned in the reserved gutter left of the plot, each
    /// vertically centred on its gridline. Only byte axes reach here; percent axes
    /// return early in <see cref="ShowsAxisLabels"/>.
    /// </summary>
    void DrawAxisLabels(ID2D1DeviceContext dc, RectF plot, string[] labels, float gutter,
        float labelInset, float s)
    {
        if (gutter <= 0f) return;
        var fmt = _res.AxisRight;
        for (int i = labels.Length - 1; i >= 0; i--)
        {
            float y = plot.Bottom - (float)(plot.Height * (i / 4.0));
            // GapText separates label ink from the plot, per docs/design.md.
            var r = new RectF(plot.Left - gutter + labelInset, y - 7f * s,
                gutter - Widgets.HeaderLayout.GapText * s - labelInset, 14f * s);
            dc.DrawText(labels[i], fmt, R(r), _res.Brush(AxisText));
        }
    }

    /// <summary>Five byte-axis tick labels, bottom to top.</summary>
    string[] AxisLabels(ChartModel model, double axisMax)
    {
        var labels = new string[5];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = AxisLabel(axisMax * i / 4.0, model);
        return labels;
    }

    /// <summary>
    /// One byte-axis label. Byte units are spelled out on every tick ("34.1 GB",
    /// "1 KB/s" on rate axes) so no gridline is ever read as a bare number.
    /// </summary>
    internal static string AxisLabel(double v, ChartModel model)
    {
        string text = FormatBytes(v);
        return model.ValueUnit.Contains('/') ? text + model.ValueUnit : text;
    }

    // ------------------------------------------------------------------ curves

    // ------------------------------------------------------------------ slot runs

    /// <summary>
    /// Slot width for N visible points. MC's point_spacing_factor subtracts 2 while
    /// sliding (entering/exiting slots live past the edges under clip) and 1 when
    /// static (index 0 at the right edge, index N-1 exactly at the left edge).
    /// </summary>
    static double SlotWidth(RectF plot, int visible, bool sliding)
        => plot.Width / (sliding ? visible - 2 : visible - 1);

    static bool SameSample(float a, float b)
        => (float.IsNaN(a) && float.IsNaN(b))
        || (!float.IsNaN(a) && !float.IsNaN(b) && Math.Abs(a - b) <= 0.0005f);

    /// <summary>Screen point for one slot plus the enter-glide offset. NaN value yields a NaN-Y point (gap).</summary>
    static V2 SlotPoint(RectF plot, double slotW, int index, float v, double axisMax, float enterX)
    {
        float x = (float)(plot.Right - index * slotW) + enterX;
        if (float.IsNaN(v)) return new V2(x, float.NaN);
        double n = axisMax <= 0 ? 0 : v / axisMax;
        n = n < 0 ? 0 : (n > 1.06 ? 1.06 : n);
        return new V2(x, (float)(plot.Bottom - plot.Height * n));
    }

    /// <summary>
    /// Strokes (and optionally fills) each maximal run of valid slot points. NaN slots
    /// break the run, so gaps stay empty instead of bridging fabricated lines.
    /// </summary>
    void DrawRuns(ID2D1DeviceContext dc, WidgetConfig cfg, V2[] pts, int count, RectF plot,
        Color4 color, float width, float fillOpacity, V2[]? bottom = null)
    {
        var brush = _res.Brush(color);
        int i = 0;
        while (i < count)
        {
            while (i < count && float.IsNaN(pts[i].Y)) i++;
            int start = i;
            while (i < count && !float.IsNaN(pts[i].Y)) i++;
            int m = i - start;
            if (m == 1)
            {
                // Isolated sample: a dot, not a bridge to nowhere.
                var c = pts[start];
                dc.FillEllipse(new Ellipse(c, width * 0.75f, width * 0.75f), brush);
                continue;
            }
            if (m < 2) continue;

            var line = cfg.Smoothing ? SmoothRun(pts, start, m) : SliceRun(pts, start, m);
            using (var geom = BuildPolyline(line, FigureBegin.Hollow))
                dc.DrawGeometry(geom, brush, width, _res.RoundStroke);

            if (fillOpacity > 0.001f)
            {
                if (bottom is null)
                    DrawAreaFill(dc, line, plot, color, fillOpacity);
                else
                {
                    var bed = cfg.Smoothing ? SmoothRun(bottom, start, m) : SliceRun(bottom, start, m);
                    DrawStackBand(dc, line, bed, plot, color, fillOpacity);
                }
            }
        }
    }

    /// <summary>
    /// Monotone cubic through every sample with horizontal tangents: control points sit at
    /// (x0+dx/2, y0) and (x1-dx/2, y1), so the curve passes through all samples with no
    /// overshoot and each apex stays on its slot.
    /// </summary>
    static V2[] SmoothRun(V2[] pts, int start, int m)
    {
        const int K = 8;
        var @out = new V2[(m - 1) * K + 1];
        int o = 0;
        for (int j = 0; j < m - 1; j++)
        {
            var p0 = pts[start + j];
            var p3 = pts[start + j + 1];
            float dx = p3.X - p0.X;
            var c1 = new V2(p0.X + dx / 2f, p0.Y);
            var c2 = new V2(p3.X - dx / 2f, p3.Y);
            for (int k = 0; k < K; k++)
                @out[o++] = Cubic(p0, c1, c2, p3, k / (float)K);
        }
        @out[o] = pts[start + m - 1];
        return @out;
    }

    static V2 Cubic(V2 p0, V2 c1, V2 c2, V2 p3, float t)
    {
        float u = 1f - t;
        float a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return new V2(a * p0.X + b * c1.X + c * c2.X + d * p3.X,
                      a * p0.Y + b * c1.Y + c * c2.Y + d * p3.Y);
    }

    static V2[] SliceRun(V2[] pts, int start, int m)
    {
        var slice = new V2[m];
        Array.Copy(pts, start, slice, 0, m);
        return slice;
    }

    /// <summary>
    /// Flat base-color fill under the run at FillOpacity. Mission Center fills with
    /// the dataset base color at settings.opacity (default 100./255.) and no gradient
    /// fade; matching that exactly instead of inventing our own shading.
    /// </summary>
    void DrawAreaFill(ID2D1DeviceContext dc, V2[] pts, RectF plot, Color4 color, float opacity)
    {
        V2[] line = AreaFillPolygon(pts, plot);
        using var geom = BuildPolyline(line, FigureBegin.Filled);
        float a = Math.Clamp(opacity, 0f, 1f);
        dc.FillGeometry(geom, _res.Brush(new Color4(color.R, color.G, color.B, a)));
    }

    /// <summary>
    /// Fill polygon for one run: down the run's own end x-values to the plot bottom.
    /// Runs are newest-first (pts[0] is the right edge), so both drop edges use the
    /// run's x — anchoring either one at plot.Left draws a cross-plot diagonal whose
    /// self-intersection paints fill lenses above the curve.
    /// </summary>
    internal static V2[] AreaFillPolygon(V2[] pts, RectF plot)
    {
        int n = pts.Length;
        var line = new V2[n + 2];
        line[0] = new V2(pts[0].X, plot.Bottom);
        Array.Copy(pts, 0, line, 1, n);
        line[n + 1] = new V2(pts[n - 1].X, plot.Bottom);
        return line;
    }

    // ------------------------------------------------------------------ helpers

    static double ComputeAutoMax(ChartModel model)
    {
        float max = 0;
        foreach (var s in model.Series)
            foreach (float v in s.Snapshot)
                if (!float.IsNaN(v) && v > max) max = v;

        if (max <= 0) return model.PercentAxis ? 1 : 4;
        // Byte axes round up like MC's RoundingSettings::Pow2 (graph-widget
        // core/src/scaling.rs): a power of two, which divides into clean quarters
        // for a 1024-based formatter (128/96/64/32 KB). MC's Pow2Base10 variant
        // assumes 1000-based labels, which FormatBytes is not. The floor of 4 keeps
        // quarters at a whole unit, so an idle 0.6 B/s still reads 1/2/3/4 B/s.
        if (!model.PercentAxis) return ByteAxisMax(max);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (double cand in new[] { 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 7.5, 10.0, 15.0, 20.0 })
        {
            double step = mag * cand;
            if (max * 1.15 <= step) return step;
        }
        return mag * 25.0;
    }

    /// <summary>
    /// Ceiling to the next power of two. Port of MC's
    /// RoundingSettings::round_up_to_next_power_of_two (core/src/scaling.rs).
    /// </summary>
    internal static double RoundUpPow2(double n)
    {
        if (n <= 0) return 0;
        double p = 1;
        while (p < n) p *= 2;
        return p;
    }

    /// <summary>
    /// Auto-scale maximum for byte axes: the next power of two, floored at 4 so the
    /// quarter ticks stay whole units.
    /// </summary>
    internal static double ByteAxisMax(double peak) => Math.Max(4, RoundUpPow2(peak));

    /// <summary>
    /// Left inset of the axis-label column. Edit-mode corner brackets reach
    /// <see cref="Widgets.WidgetPainter.CornerSpan"/> into the card, so labels start
    /// past them instead of being crossed by a bracket arm.
    /// </summary>
    internal static float AxisLabelInset(bool editChrome, float s)
        => editChrome ? (Widgets.WidgetPainter.CornerSpan + Widgets.HeaderLayout.GapText) * s : 0f;

    /// <summary>
    /// Fits the header value into maxW: the full text, then the same number with
    /// fewer decimals, then an ellipsized tail. The value yields space so the
    /// centred &lt; title &gt; group never moves (docs/design.md).
    /// </summary>
    string FitValue(string text, float maxW, float s)
    {
        if (maxW <= 0f || ValueWidth(text, s) <= maxW) return text;

        int sp = text.IndexOf(' ');
        if (sp > 0 && double.TryParse(text.AsSpan(0, sp), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v))
        {
            foreach (string n in new[] { v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture),
                                         v.ToString("0", System.Globalization.CultureInfo.InvariantCulture) })
            {
                string cand = n + text[sp..];
                if (ValueWidth(cand, s) <= maxW) return cand;
            }
        }

        for (int n = text.Length - 1; n > 0; n--)
        {
            string cand = text[..n].TrimEnd() + "\u2026";
            if (ValueWidth(cand, s) <= maxW) return cand;
        }
        return text;
    }

    float ValueWidth(string text, float s) => _res.Measure(text, _res.HeaderValue, s).Width / s;

    /// <summary>Units that bind to the number without a space: 12%, 0B/s.</summary>
    static bool TightUnit(string unit) => unit[0] is '%' or '/';

    public static string FormatShort(double v)
    {
        double a = Math.Abs(v);
        return a switch
        {
            >= 1_000_000_000_000 => (v / 1_000_000_000_000).ToString("0.#") + "T",
            >= 1_000_000_000 => (v / 1_000_000_000).ToString("0.#") + "G",
            >= 1_000_000 => (v / 1_000_000).ToString("0.#") + "M",
            >= 1000 => (v / 1000).ToString("0.#") + "K",
            >= 100 => v.ToString("0"),
            >= 10 => v.ToString("0.#"),
            >= 1 => v.ToString("0.##"),
            _ => v.ToString("0.###"),
        };
    }

    public static string FormatBytes(double bytes)
    {
        double a = Math.Abs(bytes);
        return a switch
        {
            >= 1L << 40 => (bytes / (double)(1L << 40)).ToString("0.##") + " TB",
            >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##") + " GB",
            >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#") + " MB",
            >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.#") + " KB",
            _ => bytes.ToString("0") + " B",
        };
    }
}

using Vortice.Direct2D1;
using V2 = System.Numerics.Vector2;
using Size = Vortice.Mathematics.Size;

namespace FlowMonitor.Widgets;

/// <summary>
/// Icon glyphs as filled outlines. Blender draws these as round-capped strokes
/// (release/datafiles/icons_svg/x.svg, checkmark.svg, eyedropper.svg). A round-capped
/// segment is a capsule (two offsets plus two half-circle arcs) and a round join is the
/// disc at the vertex, so the same three primitives give caps, joins and the dropper's
/// bulb.
///
/// Each primitive becomes its OWN geometry. D2D fills a path geometry with the
/// even-odd rule, so two crossing capsules in one geometry cancel their overlap and
/// punch a hole where the arms cross. Separate geometries are separate fills, and an
/// opaque fill over an opaque fill leaves no seam.
/// </summary>
public static class GlyphGeometry
{
    /// <summary>
    /// Bar half-thickness for a glyph of ink half-extent <paramref name="half"/>. A system
    /// close/tick icon is about 14% of its ink half-extent in bar width; heavier reads as
    /// a blob at badge sizes.
    /// </summary>
    public const float BarRatio = 0.137f;

    /// <summary>
    /// Fraction of a button's side that a glyph-only icon spans. A standard icon
    /// inside a button leaves roughly this much air on each side; filling the box
    /// reads as oversized, and inset by the text padding it reads as a sliver.
    /// </summary>
    public const float IconFill = 0.56f;

    /// <summary>Outlines collected by the primitives, one per primitive.</summary>
    static readonly List<Action<ID2D1GeometrySink>> Pending = new();

    /// <summary>A capsule: a bar of half-thickness t from a to b with round caps.</summary>
    public static void Capsule(V2 a, V2 b, float t, int seg = 10)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { Disc(a, t, seg); return; }
        double ux = dx / len, uy = dy / len;                 // along the bar
        double nx = -uy, ny = ux;                            // across the bar
        var pts = new V2[2 * (seg + 1)];
        int n = 0;
        // Far cap: n -> u -> -n. Near cap: -n -> -u -> n. Same sweep direction both
        // times, so the outline is a simple closed loop.
        for (int i = 0; i <= seg; i++) Arc(pts, ref n, b, nx, ny, ux, uy, t, Math.PI * i / seg);
        for (int i = 0; i <= seg; i++) Arc(pts, ref n, a, nx, ny, ux, uy, t, Math.PI + Math.PI * i / seg);
        Add(pts, n);
    }

    static void Arc(V2[] pts, ref int n, V2 c, double nx, double ny, double ux, double uy,
        double t, double th)
    {
        double co = Math.Cos(th) * t, si = Math.Sin(th) * t;
        pts[n++] = new V2((float)(c.X + nx * co + ux * si), (float)(c.Y + ny * co + uy * si));
    }

    /// <summary>A filled disc of radius r (a round join, a bulb, a dot).</summary>
    public static void Disc(V2 c, float r, int seg = 20)
    {
        var pts = new V2[seg];
        for (int i = 0; i < seg; i++)
        {
            double th = -Math.PI * 2.0 * i / seg;
            pts[i] = new V2(c.X + (float)(r * Math.Cos(th)), c.Y + (float)(r * Math.Sin(th)));
        }
        Add(pts, seg);
    }

    static void Add(V2[] pts, int n)
    {
        var exact = new V2[n];
        Array.Copy(pts, exact, n);
        Pending.Add(sink =>
        {
            sink.BeginFigure(exact[0], FigureBegin.Filled);
            for (int p = 1; p < exact.Length; p++) sink.AddLine(exact[p]);
            sink.EndFigure(FigureEnd.Closed);
        });
    }

    /// <summary>
    /// The close X: two equal arms at 45 degrees. `half` is the ink half-extent, so the
    /// cap centre sits at (half - t) along the diagonal.
    /// </summary>
    public static void X(V2 c, float half, float t)
    {
        // Cap centres at (half - t) along each diagonal, so the round cap's far edge
        // lands exactly on `half` in x and y and `half` is the true ink half-extent.
        float k = half - t;
        Capsule(new V2(c.X - k, c.Y - k), new V2(c.X + k, c.Y + k), t);
        Capsule(new V2(c.X - k, c.Y + k), new V2(c.X + k, c.Y - k), t);
    }

    /// <summary>
    /// The confirm tick: a short down-left arm and a longer up-right arm (Blender's
    /// checkmark), round caps, and the disc at the elbow as the round join.
    /// </summary>
    public static void Check(V2 c, float half, float t)
    {
        // Both arms rise from the elbow (the low point) on the diagonals: the short one
        // up-left, the long one up-right. A + B = 2*(half - t) makes the ink half-extent
        // exactly `half`, the same reserve the X uses, then the ink is centred on c.
        const float ShortArm = 0.46f;
        float a = 2f * (half - t) / (1f + ShortArm);
        float b = a * ShortArm;
        var elbow = new V2();
        var tip = new V2(-b, -b);
        var end = new V2(a, -a);
        float minX = -b - t, maxX = a + t;
        float minY = -a - t, maxY = t;
        var d = new V2(c.X - (minX + maxX) * 0.5f, c.Y - (minY + maxY) * 0.5f);
        Capsule(tip + d, elbow + d, t);
        Capsule(elbow + d, end + d, t);
        Disc(elbow + d, t, 16);
    }

    /// <summary>
    /// Blender's eyedropper, walked from release/datafiles/icons_svg/eyedropper.svg
    /// (GPL-2.0-or-later, reference only). The single path is already in Blender's 16-unit
    /// design box and is emitted as one closed contour, so the icon is the upstream shape
    /// rather than an approximation of it. `side` is the icon's box in pixels; the path's
    /// own bounding box is fitted into it, keeping the art's proportions.
    /// </summary>
    public static void Dropper(V2 c, float side)
    {
        // The art's own bounding box, fitted into the icon box, proportions kept.
        const float X0 = 195.146f, X1 = 208.990f, Y0 = 599.000f, Y1 = 612.894f;
        V2 P(float x, float y) => new(
            c.X + ((x - X0) / (X1 - X0) - 0.5f) * side,
            c.Y + ((y - Y0) / (Y1 - Y0) - 0.5f) * side);
        Pending.Add(sink =>
        {
            sink.BeginFigure(P(206.727f, 599.000f), FigureBegin.Filled);
            sink.AddBezier(new BezierSegment(P(206.533f, 598.996f), P(206.340f, 599.013f), P(206.152f, 599.051f)));
            sink.AddBezier(new BezierSegment(P(205.402f, 599.201f), P(204.705f, 599.629f), P(204.146f, 600.188f)));
            sink.AddLine(P(202.002f, 602.295f));
            sink.AddLine(P(200.854f, 601.146f));
            sink.AddLine(P(200.700f, 601.018f));
            sink.AddLine(P(200.562f, 601.088f));
            sink.AddLine(P(200.262f, 601.008f));
            sink.AddLine(P(200.075f, 600.999f));
            sink.AddLine(P(199.899f, 601.060f));
            sink.AddLine(P(199.757f, 601.180f));
            sink.AddLine(P(199.669f, 601.345f));
            sink.AddLine(P(199.648f, 601.530f));
            sink.AddLine(P(199.695f, 601.710f));
            sink.AddLine(P(199.806f, 601.860f));
            sink.AddLine(P(199.963f, 601.960f));
            sink.AddLine(P(200.147f, 601.994f));
            sink.AddLine(P(201.793f, 603.500f));
            sink.AddLine(P(197.145f, 608.189f));
            sink.AddLine(P(196.918f, 608.123f));
            sink.AddLine(P(196.745f, 608.051f));
            sink.AddLine(P(196.557f, 608.049f));
            sink.AddLine(P(196.381f, 608.116f));
            sink.AddLine(P(196.243f, 608.243f));
            sink.AddLine(P(196.161f, 608.412f));
            sink.AddLine(P(196.148f, 608.600f));
            sink.AddLine(P(196.205f, 608.779f));
            sink.AddLine(P(196.323f, 608.924f));
            sink.AddLine(P(196.488f, 609.016f));
            sink.AddLine(P(196.674f, 609.040f));
            sink.AddLine(P(196.856f, 608.994f));
            sink.AddLine(P(197.008f, 608.884f));
            sink.AddLine(P(197.109f, 608.726f));
            sink.AddLine(P(197.145f, 608.541f));
            sink.AddLine(P(197.000f, 609.334f));
            sink.AddLine(P(195.146f, 611.187f));
            sink.AddLine(P(195.054f, 611.337f));
            sink.AddLine(P(195.009f, 611.505f));
            sink.AddLine(P(195.024f, 611.678f));
            sink.AddLine(P(195.097f, 611.835f));
            sink.AddLine(P(195.220f, 611.958f));
            sink.AddLine(P(195.378f, 612.031f));
            sink.AddLine(P(195.551f, 612.046f));
            sink.AddLine(P(195.718f, 612.001f));
            sink.AddLine(P(195.861f, 611.902f));
            sink.AddLine(P(197.854f, 609.895f));
            sink.AddLine(P(198.081f, 609.960f));
            sink.AddLine(P(198.254f, 610.031f));
            sink.AddLine(P(198.442f, 610.033f));
            sink.AddLine(P(198.618f, 609.966f));
            sink.AddLine(P(198.756f, 609.838f));
            sink.AddLine(P(198.837f, 609.669f));
            sink.AddLine(P(198.850f, 609.482f));
            sink.AddLine(P(198.793f, 609.303f));
            sink.AddLine(P(198.674f, 609.157f));
            sink.AddLine(P(198.510f, 609.066f));
            sink.AddLine(P(198.324f, 609.042f));
            sink.AddLine(P(198.142f, 609.088f));
            sink.AddLine(P(197.990f, 609.198f));
            sink.AddLine(P(197.889f, 609.357f));
            sink.AddLine(P(197.854f, 609.541f));
            sink.AddLine(P(198.000f, 608.746f));
            sink.AddLine(P(202.502f, 604.209f));
            sink.AddLine(P(203.795f, 605.502f));
            sink.AddLine(P(199.291f, 610.041f));
            sink.AddLine(P(198.500f, 610.041f));
            sink.AddLine(P(198.298f, 610.065f));
            sink.AddLine(P(198.147f, 610.041f));
            sink.AddLine(P(196.146f, 612.187f));
            sink.AddLine(P(196.054f, 612.337f));
            sink.AddLine(P(196.009f, 612.505f));
            sink.AddLine(P(196.024f, 612.678f));
            sink.AddLine(P(196.097f, 612.835f));
            sink.AddLine(P(196.220f, 612.958f));
            sink.AddLine(P(196.378f, 613.031f));
            sink.AddLine(P(196.551f, 613.046f));
            sink.AddLine(P(196.718f, 613.001f));
            sink.AddLine(P(196.861f, 612.902f));
            sink.AddLine(P(198.707f, 611.041f));
            sink.AddLine(P(199.500f, 611.041f));
            sink.AddLine(P(199.703f, 611.017f));
            sink.AddLine(P(199.855f, 611.041f));
            sink.AddLine(P(204.502f, 606.209f));
            sink.AddLine(P(206.146f, 607.854f));
            sink.AddLine(P(206.302f, 607.969f));
            sink.AddLine(P(206.493f, 608.007f));
            sink.AddLine(P(206.684f, 607.969f));
            sink.AddLine(P(206.847f, 607.861f));
            sink.AddLine(P(206.955f, 607.698f));
            sink.AddLine(P(206.993f, 607.507f));
            sink.AddLine(P(206.955f, 607.316f));
            sink.AddLine(P(206.847f, 607.153f));
            sink.AddLine(P(205.711f, 606.004f));
            sink.AddLine(P(207.850f, 603.898f));
            sink.AddLine(P(207.854f, 603.894f));
            sink.AddBezier(new BezierSegment(P(208.413f, 603.336f), P(208.840f, 602.639f), P(208.990f, 601.889f)));
            sink.AddBezier(new BezierSegment(P(209.140f, 601.138f), P(208.971f, 600.305f), P(208.354f, 599.687f)));
            sink.AddBezier(new BezierSegment(P(207.890f, 599.224f), P(207.306f, 599.013f), P(206.727f, 599.000f)));
            sink.EndFigure(FigureEnd.Closed);
       });
    }

    /// <summary>
    /// Builds one geometry per primitive, for an opaque fill of each in turn.
    /// </summary>
    public static ID2D1PathGeometry[] Build(Rendering.ResourceCache res, Action draw)
    {
        Pending.Clear();
        draw();
        var result = new ID2D1PathGeometry[Pending.Count];
        for (int i = 0; i < Pending.Count; i++)
        {
            var geo = res.D2DFactory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                Pending[i](sink);
                sink.Close();
            }
            result[i] = geo;
        }
        Pending.Clear();
        return result;
    }
}

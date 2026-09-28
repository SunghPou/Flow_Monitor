using Vortice.Direct2D1;
using V2 = System.Numerics.Vector2;

namespace FlowMonitor.Widgets;

/// <summary>
/// Icon glyphs as single closed contours (X, check, dropper). One contour per
/// primitive, filled opaque over opaque, so overlaps never seam or cancel.
/// </summary>
public static class GlyphGeometry
{
    /// <summary>
    /// Fraction of a button's side that a glyph-only icon spans. A standard icon
    /// inside a button leaves roughly this much air on each side; filling the box
    /// reads as oversized, and inset by the text padding it reads as a sliver.
    /// </summary>
    public const float IconFill = 0.56f;

    /// <summary>Outlines collected by the primitives, one per primitive.</summary>
    static readonly List<Action<ID2D1GeometrySink>> Pending = new();

    /// <summary>
    /// The semicircle that caps a bar of half-thickness t at its free end: from c + n*t
    /// around the outside through c + u*t to c - n*t, as points on the SAME contour as the
    /// bar's edges. 11 chords per cap is under a tenth of a pixel of chord error at badge
    /// sizes, so the cap is a polygon of the outline, not a second filled region.
    /// </summary>
    static void Cap(List<V2> pts, V2 c, V2 n, V2 u, float t, int seg = 10)
    {
        for (int i = 0; i <= seg; i++)
        {
            double th = Math.PI * i / seg;
            double co = Math.Cos(th) * t, si = Math.Sin(th) * t;
            pts.Add(new V2((float)(c.X + n.X * co + u.X * si), (float)(c.Y + n.Y * co + u.Y * si)));
        }
    }

    /// <summary>
    /// Appends the shorter arc of radius r around c, from direction a to direction b.
    /// This is the round join between two bars, so the corner is part of the contour.
    /// </summary>
    static void Sweep(List<V2> pts, V2 c, V2 a, V2 b, float r, int seg = 10)
    {
        V2 ua = V2.Normalize(a), ub = V2.Normalize(b);
        double from = Math.Atan2(ua.Y, ua.X);
        double to = Math.Atan2(ub.Y, ub.X);
        double sweep = to - from;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;
        while (sweep < -Math.PI) sweep += 2 * Math.PI;
        for (int i = 0; i <= seg; i++)
        {
            double th = from + sweep * i / seg;
            pts.Add(new V2(c.X + (float)(r * Math.Cos(th)), c.Y + (float)(r * Math.Sin(th))));
        }
    }

    /// <summary>
    /// The close X: two equal arms at 45 degrees, each arm ending in a round cap. `half`
    /// is the ink half-extent, measured to the cap tips.
    /// </summary>
    public static void X(V2 c, float half)
    {
        // A plus rotated 45 degrees, as ONE closed contour: a 12-gon whose four end edges
        // are swapped for semicircular caps of radius W. Two capsules would be two regions,
        // so the crossing would composite twice and read brighter than the arms
        // (docs/design.md icon rule 24); the four notches between arms stay sharp.
        // W is the bar half-width against the cap centre's L: at 0.135 the bar is 0.552 of
        // the fitted ink half-extent, which is what the tick's Bar is tuned to match.
        const float L = 0.5f, W = 0.135f;
        V2[] arms = [new(0f, 1f), new(-1f, 0f), new(0f, -1f), new(1f, 0f)];
        var pts = new List<V2>();
        for (int i = 0; i < arms.Length; i++)
        {
            var a = arms[i];
            Cap(pts, a * L, new V2(a.Y, -a.X), a, W);
            // The concave corner, where this arm's inner edge meets the next arm's. Drop
            // it and the straight cap-to-cap edge fills the notch in as a blob.
            pts.Add((a + arms[(i + 1) % arms.Length]) * W);
        }
        Emit(c, half, MathF.PI / 4f, pts);
    }

    /// <summary>
    /// The confirm tick as ONE closed contour: a short arm up-left and a longer arm
    /// up-right, with the notch between them. Two capsules would composite twice at
    /// the elbow (docs/design.md icon rule 24), so the joint is part of the outline.
    /// </summary>
    public static void Check(V2 c, float half)
    {
        // Spine: short arm up-left, then a longer arm up-right, joined at the elbow. The
        // reference tick is wide and shallow (about 1.5:1), so the x span is stretched.
        V2[] spine = [new(0.02f, 0.50f), new(0.41f, 0.90f), new(1.12f, 0.16f)];
        // Bar half-width in the unit box. 0.183 is the X's 0.135 rescaled for this spine's
        // fitted ink width, so the two badges carry the same bar (0.552 of the ink).
        const float Bar = 0.183f;

        V2 Dir(int i) => V2.Normalize(spine[i + 1] - spine[i]);
        var d0 = Dir(0);
        var d1 = Dir(1);
        var n0 = new V2(-d0.Y, d0.X);      // across the bar
        var n1 = new V2(-d1.Y, d1.X);
        // Miter at the elbow so the two bar edges meet in a single corner, which keeps
        // the outline simple: an overlapping pair would cancel under the even-odd fill.
        var mid = V2.Normalize(n0 + n1);
        float scale = 1f / MathF.Max(0.4f, V2.Dot(mid, n1));

        var pts = new List<V2> { spine[0] + n0 * Bar };  // outer edge of the short arm
        Sweep(pts, spine[1], n0, n1, Bar);              // ROUND bottom corner (a join arc)
        pts.Add(spine[2] + n1 * Bar);
        Cap(pts, spine[2], n1, d1, Bar);                // cap on the long arm
        pts.Add(spine[1] - mid * Bar * scale);          // inner corner at the elbow, sharp
        pts.Add(spine[0] - n0 * Bar);
        // Cap on the short arm. n0 is flipped so the walk starts on the arm's INNER end
        // corner (the one the contour reaches from the inner elbow) and closes back on the
        // outer one; walking it the other way crosses the contour and drops a detached nub.
        Cap(pts, spine[0], -n0, -d0, Bar);
        Emit(c, half, 0f, pts);
    }

    /// <summary>
    /// Emits one closed figure from a point list in the unit box: rotated, re-centred on
    /// its own ink, then fitted so the ink half-extent is exactly `half` on both axes and
    /// lands on c. Centring the measured ink (not the point cloud's origin) is what puts
    /// an asymmetric glyph such as the tick under the centre the layout reserved.
    /// </summary>
    static void Emit(V2 c, float half, float rotation, List<V2> pts)
    {
        var sin = MathF.Sin(rotation);
        var cos = MathF.Cos(rotation);
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        var mapped = new V2[pts.Count];
        for (int i = 0; i < mapped.Length; i++)
        {
            var p = new V2(pts[i].X * cos - pts[i].Y * sin, pts[i].X * sin + pts[i].Y * cos);
            mapped[i] = p;
            minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
        }
        var mid = new V2((minX + maxX) / 2f, (minY + maxY) / 2f);
        // One scale for both axes, so the shape keeps its proportions.
        float k = half / MathF.Max(maxX - minX, maxY - minY) * 2f;
        Pending.Add(sink =>
        {
            sink.BeginFigure(c + (mapped[0] - mid) * k, FigureBegin.Filled);
            for (int i = 1; i < mapped.Length; i++) sink.AddLine(c + (mapped[i] - mid) * k);
            sink.EndFigure(FigureEnd.Closed);
        });
    }

    /// <summary>
    /// Blender's eyedropper, walked from release/datafiles/icons_svg/eyedropper.svg
    /// (GPL-2.0-or-later, reference only). First pair is the start point, then
    /// 0 is a line (x y) and 1 is a cubic (ax ay bx by x y). The card icon and
    /// the cursor both walk this, so the art has one definition.
    /// </summary>
    internal static readonly float[] DropperWalk =
    [
        206.727f, 599.000f, 1, 206.533f, 598.996f, 206.340f, 599.013f, 206.152f, 599.051f, 1, 205.402f, 599.201f, 204.705f, 599.629f,
        204.146f, 600.188f, 0, 202.002f, 602.295f, 0, 200.854f, 601.146f, 0, 200.700f, 601.018f, 0, 200.562f, 601.088f, 0, 200.262f,
        601.008f, 0, 200.075f, 600.999f, 0, 199.899f, 601.060f, 0, 199.757f, 601.180f, 0, 199.669f, 601.345f, 0, 199.648f, 601.530f, 0,
        199.695f, 601.710f, 0, 199.806f, 601.860f, 0, 199.963f, 601.960f, 0, 200.147f, 601.994f, 0, 201.793f, 603.500f, 0, 197.145f,
        608.189f, 0, 196.918f, 608.123f, 0, 196.745f, 608.051f, 0, 196.557f, 608.049f, 0, 196.381f, 608.116f, 0, 196.243f, 608.243f, 0,
        196.161f, 608.412f, 0, 196.148f, 608.600f, 0, 196.205f, 608.779f, 0, 196.323f, 608.924f, 0, 196.488f, 609.016f, 0, 196.674f,
        609.040f, 0, 196.856f, 608.994f, 0, 197.008f, 608.884f, 0, 197.109f, 608.726f, 0, 197.145f, 608.541f, 0, 197.000f, 609.334f, 0,
        195.146f, 611.187f, 0, 195.054f, 611.337f, 0, 195.009f, 611.505f, 0, 195.024f, 611.678f, 0, 195.097f, 611.835f, 0, 195.220f,
        611.958f, 0, 195.378f, 612.031f, 0, 195.551f, 612.046f, 0, 195.718f, 612.001f, 0, 195.861f, 611.902f, 0, 197.854f, 609.895f, 0,
        198.081f, 609.960f, 0, 198.254f, 610.031f, 0, 198.442f, 610.033f, 0, 198.618f, 609.966f, 0, 198.756f, 609.838f, 0, 198.837f,
        609.669f, 0, 198.850f, 609.482f, 0, 198.793f, 609.303f, 0, 198.674f, 609.157f, 0, 198.510f, 609.066f, 0, 198.324f, 609.042f, 0,
        198.142f, 609.088f, 0, 197.990f, 609.198f, 0, 197.889f, 609.357f, 0, 197.854f, 609.541f, 0, 198.000f, 608.746f, 0, 202.502f,
        604.209f, 0, 203.795f, 605.502f, 0, 199.291f, 610.041f, 0, 198.500f, 610.041f, 0, 198.298f, 610.065f, 0, 198.147f, 610.041f, 0,
        196.146f, 612.187f, 0, 196.054f, 612.337f, 0, 196.009f, 612.505f, 0, 196.024f, 612.678f, 0, 196.097f, 612.835f, 0, 196.220f,
        612.958f, 0, 196.378f, 613.031f, 0, 196.551f, 613.046f, 0, 196.718f, 613.001f, 0, 196.861f, 612.902f, 0, 198.707f, 611.041f, 0,
        199.500f, 611.041f, 0, 199.703f, 611.017f, 0, 199.855f, 611.041f, 0, 204.502f, 606.209f, 0, 206.146f, 607.854f, 0, 206.302f,
        607.969f, 0, 206.493f, 608.007f, 0, 206.684f, 607.969f, 0, 206.847f, 607.861f, 0, 206.955f, 607.698f, 0, 206.993f, 607.507f, 0,
        206.955f, 607.316f, 0, 206.847f, 607.153f, 0, 205.711f, 606.004f, 0, 207.850f, 603.898f, 0, 207.854f, 603.894f, 1, 208.413f,
        603.336f, 208.840f, 602.639f, 208.990f, 601.889f, 1, 209.140f, 601.138f, 208.971f, 600.305f, 208.354f, 599.687f, 1, 207.890f,
        599.224f, 207.306f, 599.013f, 206.727f, 599.000f,
    ];

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
            sink.BeginFigure(P(DropperWalk[0], DropperWalk[1]), FigureBegin.Filled);
            for (int i = 2; i < DropperWalk.Length;)
            {
                if (DropperWalk[i] == 0)
                {
                    sink.AddLine(P(DropperWalk[i + 1], DropperWalk[i + 2]));
                    i += 3;
                }
                else
                {
                    sink.AddBezier(new BezierSegment(
                        P(DropperWalk[i + 1], DropperWalk[i + 2]),
                        P(DropperWalk[i + 3], DropperWalk[i + 4]),
                        P(DropperWalk[i + 5], DropperWalk[i + 6])));
                    i += 7;
                }
            }
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

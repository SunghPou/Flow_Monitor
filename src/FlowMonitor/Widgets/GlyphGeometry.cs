using Vortice.Direct2D1;
using V2 = System.Numerics.Vector2;

namespace FlowMonitor.Widgets;

/// <summary>
/// Icon glyphs as single filled outlines. Blender draws these as round-capped strokes
/// (release/datafiles/icons_svg/x.svg, checkmark.svg, eyedropper.svg); a stroke rendered
/// as several primitives double-composites where they overlap and shows a seam, so every
/// glyph here is ONE geometry filled in ONE pass. A round-capped segment is a capsule
/// (two offsets plus two half-circle arcs) and a round join is the disc at the vertex, so
/// the same three primitives give caps, joins and the dropper's bulb.
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

    /// <summary>A capsule: a bar of half-thickness t from a to b with round caps.</summary>
    public static void Capsule(ID2D1GeometrySink sink, V2 a, V2 b, float t, int seg = 10)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) { Disc(sink, a, t, seg); return; }
        double ux = dx / len, uy = dy / len;                 // along the bar
        double nx = -uy, ny = ux;                            // across the bar
        var pts = new V2[2 * (seg + 1)];
        int n = 0;
        // Far cap: n -> u -> -n. Near cap: -n -> -u -> n. Same sweep direction both
        // times, so the outline is a simple closed loop.
        for (int i = 0; i <= seg; i++) Arc(pts, ref n, b, nx, ny, ux, uy, t, Math.PI * i / seg);
        for (int i = 0; i <= seg; i++) Arc(pts, ref n, a, nx, ny, ux, uy, t, Math.PI + Math.PI * i / seg);
        Figure(sink, pts, n);
    }

    static void Arc(V2[] pts, ref int n, V2 c, double nx, double ny, double ux, double uy,
        double t, double th)
    {
        double co = Math.Cos(th) * t, si = Math.Sin(th) * t;
        pts[n++] = new V2((float)(c.X + nx * co + ux * si), (float)(c.Y + ny * co + uy * si));
    }

    /// <summary>A filled disc of radius r (a round join, a bulb, a dot).</summary>
    public static void Disc(ID2D1GeometrySink sink, V2 c, float r, int seg = 20)
    {
        var pts = new V2[seg];
        for (int i = 0; i < seg; i++)
        {
            double th = -Math.PI * 2.0 * i / seg;
            pts[i] = new V2(c.X + (float)(r * Math.Cos(th)), c.Y + (float)(r * Math.Sin(th)));
        }
        Figure(sink, pts, seg);
    }

    /// <summary>
    /// The close X: two equal arms at 45 degrees. `half` is the ink half-extent, so the
    /// cap centre sits at (half - t) along the diagonal.
    /// </summary>
    public static void X(ID2D1GeometrySink sink, V2 c, float half, float t)
    {
        // Cap centres at (half - t) along each diagonal, so the round cap's far edge
        // lands exactly on `half` in x and y and `half` is the true ink half-extent.
        float k = half - t;
        Capsule(sink, new V2(c.X - k, c.Y - k), new V2(c.X + k, c.Y + k), t);
        Capsule(sink, new V2(c.X - k, c.Y + k), new V2(c.X + k, c.Y - k), t);
    }

    /// <summary>
    /// The confirm tick: a short down-left arm and a longer up-right arm (Blender's
    /// checkmark), round caps, and the disc at the elbow as the round join.
    /// </summary>
    public static void Check(ID2D1GeometrySink sink, V2 c, float half, float t)
    {
        // Two arms on the diagonals with a round join, the long arm reaching exactly
        // `half`, then the whole ink is centred on c: an offset tick reads as lopsided.
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
        Capsule(sink, tip + d, elbow + d, t);
        Capsule(sink, elbow + d, end + d, t);
        Disc(sink, elbow + d, t, 16);
    }

    /// <summary>
    /// The eyedropper: a bulb disc at the top right, a shaft capsule to the tip at the
    /// bottom left, and a collar disc partway along (Blender's eyedropper.svg, which is
    /// itself the outline of a round-capped stroke plus its bulb).
    /// </summary>
    public static void Dropper(ID2D1GeometrySink sink, V2 c, float half, float t)
    {
        float bx = half * 0.62f, by = -half * 0.62f;          // bulb centre
        var bulb = new V2(c.X + bx, c.Y + by);
        var tip = new V2(c.X - half * 0.86f, c.Y + half * 0.86f);
        float collarD = (float)(half * 0.30);
        var collar = new V2(c.X + bx - collarD * 0.7071f, c.Y + by + collarD * 0.7071f);
        Capsule(sink, collar, tip, t);
        Disc(sink, bulb, half * 0.40f, 20);
        Disc(sink, collar, t * 1.35f, 16);
    }

    static void Figure(ID2D1GeometrySink sink, V2[] pts, int n)
    {
        sink.BeginFigure(pts[0], FigureBegin.Filled);
        for (int i = 1; i < n; i++) sink.AddLine(pts[i]);
        sink.EndFigure(FigureEnd.Closed);
    }

    /// <summary>Builds one geometry from the primitives above, for a single fill.</summary>
    public static ID2D1PathGeometry? Build(Rendering.ResourceCache res, Action<ID2D1GeometrySink> draw)
    {
        var geo = res.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open()) { draw(sink); sink.Close(); }
        return geo;
    }
}

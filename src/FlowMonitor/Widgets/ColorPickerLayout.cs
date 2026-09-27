using System.Drawing;

namespace FlowMonitor.Widgets;

/// <summary>Which picker control a point belongs to.</summary>
public enum PickerPart
{
    None,
    Ring,
    Disc,
    Alpha,
    Eyedropper,
}

/// <summary>
/// Colour picker geometry in logical pixels, plus every point/value conversion.
/// Paint and hit-test both read this file, so the drawn control and the clickable
/// control cannot drift apart. Proportions follow the reference card: the ring is
/// 70% of the card width, the disc floats inside its hole with a white gap, and the
/// field row is inset one step further than the title.
/// </summary>
public static class ColorPickerLayout
{
    public const float CardW = 320f;
    public const float CardH = 447f;
    public const float Pad = 28f;
    public const float Radius = 30f;

    public static readonly PointF Center = new(CardW / 2f, 185f);
    public const float RingOuter = 110f;
    public const float RingInner = 81f;
    /// <summary>The disc is smaller than the ring hole, leaving a white gap.</summary>
    public const float DiscR = 66f;
    /// <summary>Marker rides halfway through the ring band.</summary>
    public const float RingMarkR = (RingOuter + RingInner) / 2f;
    /// <summary>Ring marker is the filled one, disc marker the small hollow one.</summary>
    public const float RingMarkerR = 18f;
    public const float MarkerR = 10f;
    public const float MarkerStroke = 2f;

    public const float AlphaY = 317f;
    public const float AlphaH = 17f;
    public const float DividerY = 351f;
    public const float PillY = 366f;
    public const float PillH = 26f;
    public const float HexW = 68f;
    public const float Cxw = 39f;
    public const float PillGap = 5f;
    public const float LabelY = 398f;
    public const float LabelH = 18f;
    public const float DropW = 20f;

    public static RectangleF Title => new(Pad + 6f, 30f, CardW - Pad * 2f, 36f);
    public static RectangleF Card => new(0f, 0f, CardW, CardH);

    public static RectangleF AlphaTrack => new(Pad, AlphaY, CardW - Pad * 2f, AlphaH);

    /// <summary>Field 0 is the wide hex field, 1..3 the R/G/B fields.</summary>
    public static RectangleF Pill(int i)
        => i == 0
            ? new RectangleF(Pad, PillY, HexW, PillH)
            : new RectangleF(Pad + HexW + PillGap * 2f + (i - 1) * (Cxw + PillGap), PillY, Cxw, PillH);

    public static RectangleF Eyedropper =>
        new(CardW - Pad - DropW, PillY, DropW, PillH);

    public static RectangleF PillLabel(int i) => new(Pill(i).Left, LabelY, Pill(i).Width, LabelH);

    public static readonly string[] PillLabels = ["#", "R", "G", "B"];

    /// <summary>Control under a logical point; the ring band wins over the disc it surrounds.</summary>
    public static PickerPart HitTest(float x, float y)
    {
        if (Eyedropper.Contains(x, y)) return PickerPart.Eyedropper;
        if (AlphaTrack.Contains(x, y)) return PickerPart.Alpha;
        float dx = x - Center.X, dy = y - Center.Y;
        float r = MathF.Sqrt(dx * dx + dy * dy);
        if (r <= RingOuter) return r >= RingInner ? PickerPart.Ring : PickerPart.Disc;
        return PickerPart.None;
    }

    /// <summary>
    /// Hue under a point: 0 (red) at 3 o'clock, falling clockwise, so magenta,
    /// blue, cyan, green and yellow follow it — the reference ring's order.
    /// </summary>
    public static float HueFromPoint(float x, float y)
    {
        double deg = Math.Atan2(y - Center.Y, x - Center.X) * 180.0 / Math.PI;
        return (float)(((-deg) % 360.0 + 360.0) % 360.0);
    }

    public static PointF PointFromHue(float hue)
    {
        double rad = -hue * Math.PI / 180.0;
        return new PointF(
            Center.X + RingMarkR * (float)Math.Cos(rad),
            Center.Y + RingMarkR * (float)Math.Sin(rad));
    }

    /// <summary>Saturation grows from the centre out; value falls from the top down.</summary>
    public static (double S, double V) SvFromPoint(float x, float y)
    {
        float dx = x - Center.X, dy = y - Center.Y;
        double s = Math.Clamp(MathF.Sqrt(dx * dx + dy * dy) / DiscR, 0.0, 1.0);
        double v = Math.Clamp((dy + DiscR) / (DiscR * 2.0), 0.0, 1.0);
        return (s, v);
    }

    public static PointF PointFromSv(double s, double v)
    {
        double r = Math.Clamp(s, 0.0, 1.0) * DiscR;
        double yy = (1.0 - Math.Clamp(v, 0.0, 1.0)) * DiscR * 2.0;
        return new PointF(Center.X + (float)r, Center.Y - DiscR + (float)yy);
    }

    public static double AlphaFromX(float x)
        => Math.Clamp((x - AlphaTrack.Left) / AlphaTrack.Width, 0.0, 1.0);

    public static float XFromAlpha(double a)
        => AlphaTrack.Left + (float)Math.Clamp(a, 0.0, 1.0) * AlphaTrack.Width;
}

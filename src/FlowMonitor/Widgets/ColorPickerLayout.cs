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
/// control cannot drift apart.
/// </summary>
public static class ColorPickerLayout
{
    public const float CardW = 320f;
    public const float CardH = 428f;
    public const float Pad = 20f;
    public const float Radius = 14f;

    public static readonly PointF Center = new(CardW / 2f, 168f);
    public const float RingOuter = 104f;
    public const float RingInner = 74f;
    /// <summary>Marker rides halfway through the ring band.</summary>
    public const float RingMarkR = (RingOuter + RingInner) / 2f;
    public const float MarkerR = 8f;
    public const float MarkerStroke = 2f;

    public const float AlphaY = 306f;
    public const float AlphaH = 18f;
    public const float DividerY = 344f;
    public const float PillY = 360f;
    public const float PillH = 28f;
    public const float PillW = 58f;
    public const float PillGap = 6f;
    public const float LabelY = 392f;
    public const float DropW = 24f;

    public static RectangleF Title => new(Pad, 16f, CardW - Pad * 2f, 24f);
    public static RectangleF Card => new(0f, 0f, CardW, CardH);

    public static RectangleF AlphaTrack =>
        new(Pad, AlphaY, CardW - Pad * 2f, AlphaH);

    public static RectangleF Pill(int i) =>
        new(Pad + i * (PillW + PillGap), PillY, PillW, PillH);

    public static RectangleF Eyedropper => new(CardW - Pad - DropW, PillY, DropW, PillH);

    public static RectangleF PillLabel(int i) =>
        new(Pad + i * (PillW + PillGap), LabelY, PillW, 14f);

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
        double s = Math.Clamp(MathF.Sqrt((x - Center.X) * (x - Center.X) + (y - Center.Y) * (y - Center.Y))
                              / RingInner, 0.0, 1.0);
        double v = Math.Clamp((y - (Center.Y - RingInner)) / (RingInner * 2.0), 0.0, 1.0);
        return (s, v);
    }

    public static PointF PointFromSv(double s, double v)
    {
        double r = Math.Clamp(s, 0.0, 1.0) * RingInner;
        double yy = (1.0 - Math.Clamp(v, 0.0, 1.0)) * RingInner * 2.0;
        return new PointF(Center.X + (float)r, Center.Y - RingInner + (float)yy);
    }

    public static double AlphaFromX(float x)
        => Math.Clamp((x - AlphaTrack.Left) / AlphaTrack.Width, 0.0, 1.0);

    public static float XFromAlpha(double a)
        => AlphaTrack.Left + (float)Math.Clamp(a, 0.0, 1.0) * AlphaTrack.Width;
}

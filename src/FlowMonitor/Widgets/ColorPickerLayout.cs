using System.Drawing;

namespace FlowMonitor.Widgets;

/// <summary>Which picker control a point belongs to.</summary>
public enum PickerPart
{
    None,
    Wheel,
    Value,
    Eyedropper,
}

/// <summary>
/// Colour picker geometry in logical pixels, plus every point/value conversion.
/// Paint and hit-test both read this file, so the drawn control and the clickable
/// control cannot drift apart. The control set is Blender's colour picker: one
/// full-bleed wheel where the angle is hue and the radius is saturation, a value
/// slider under it, then the hex and RGB fields.
/// </summary>
public static class ColorPickerLayout
{
    public const float CardW = 320f;
    public const float CardH = 456f;
    public const float Pad = 28f;
    public const float Radius = 30f;

    /// <summary>Wheel centre; the wheel is a disc that fills its own box.</summary>
    public static readonly PointF Center = new(CardW / 2f, 192f);
    public const float WheelR = 112f;
    public const float MarkerR = 9f;
    public const float MarkerStroke = 2f;

    // Vertical rhythm: 24 of air at the top and at the bottom, 20 between the major
    // blocks, 16 above the fields and 10 between a field and its label.
    public const float ValueY = 324f;
    public const float ValueH = 16f;
    public const float DividerY = 360f;
    public const float PillY = 376f;
    public const float PillH = 28f;
    public const float HexW = 68f;
    public const float Cxw = 39f;
    public const float PillGap = 5f;
    public const float LabelY = 414f;
    public const float LabelH = 18f;
    public const float DropW = 20f;
    /// <summary>Inner left padding so a field value never touches its box edge.</summary>
    public const float FieldPad = 8f;

    public static RectangleF Title => new(Pad + 6f, 24f, CardW - Pad * 2f, 36f);
    public static RectangleF Card => new(0f, 0f, CardW, CardH);

    /// <summary>The value track: black at the left, the full hue at the right.</summary>
    public static RectangleF ValueTrack => new(Pad, ValueY, CardW - Pad * 2f, ValueH);

    /// <summary>Field 0 is the wide hex field, 1..3 the R/G/B fields.</summary>
    public static RectangleF Pill(int i)
        => i == 0
            ? new RectangleF(Pad, PillY, HexW, PillH)
            : new RectangleF(Pad + HexW + PillGap * 2f + (i - 1) * (Cxw + PillGap), PillY, Cxw, PillH);

    public static RectangleF Eyedropper =>
        new(CardW - Pad - DropW, PillY, DropW, PillH);

    public static RectangleF PillLabel(int i) => new(Pill(i).Left, LabelY, Pill(i).Width, LabelH);

    public static readonly string[] PillLabels = ["#", "R", "G", "B"];

    /// <summary>Control under a logical point; a click past the wheel rim is a commit.</summary>
    public static PickerPart HitTest(float x, float y)
    {
        if (Eyedropper.Contains(x, y)) return PickerPart.Eyedropper;
        if (ValueTrack.Contains(x, y)) return PickerPart.Value;
        float dx = x - Center.X, dy = y - Center.Y;
        return MathF.Sqrt(dx * dx + dy * dy) <= WheelR ? PickerPart.Wheel : PickerPart.None;
    }

    /// <summary>
    /// Hue and saturation under a point: hue 0 (red) sits at 12 o'clock and falls
    /// clockwise, saturation is the distance from the centre as a fraction of the
    /// radius and clamps to 1 outside the rim (Blender's hsvcircle_vals_from_pos).
    /// </summary>
    public static (double H, double S) HsFromPoint(float x, float y)
    {
        float dx = x - Center.X, dy = y - Center.Y;
        double dist = MathF.Sqrt(dx * dx + dy * dy);
        double s = dist < WheelR ? dist / WheelR : 1.0;
        double ang = Math.Atan2(dx, -dy);            // 0 at the top, growing clockwise
        double h = (ang / (2.0 * Math.PI)) * 360.0;
        return ((h % 360.0 + 360.0) % 360.0, s);
    }

    public static PointF PointFromHs(double hue, double sat)
    {
        double r = Math.Clamp(sat, 0.0, 1.0) * WheelR;
        double ang = hue * Math.PI / 180.0;
        return new PointF(
            Center.X + (float)(r * Math.Sin(ang)),
            Center.Y - (float)(r * Math.Cos(ang)));
    }

    public static double ValueFromX(float x)
        => Math.Clamp((x - ValueTrack.Left) / ValueTrack.Width, 0.0, 1.0);

    public static float XFromValue(double v)
        => ValueTrack.Left + (float)Math.Clamp(v, 0.0, 1.0) * ValueTrack.Width;
}

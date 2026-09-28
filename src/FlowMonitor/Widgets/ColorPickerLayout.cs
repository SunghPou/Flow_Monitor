using System.Drawing;

namespace FlowMonitor.Widgets;

/// <summary>Which picker control a point belongs to.</summary>
public enum PickerPart
{
    None,
    Wheel,
    Value,
    /// <summary>Left segment of the colour-space row (Linear / Perceptual).</summary>
    Space,
    /// <summary>Right segment of the colour-model row (RGB / HSV).</summary>
    Model,
    SliderA,
    SliderB,
    SliderC,
    SliderD,
    Hex,
    Eyedropper,
}

/// <summary>Which working space the number sliders and the wheel are shown in.</summary>
public enum PickerSpace
{
    Linear,
    Perceptual,
}

/// <summary>Which components the four number sliders show.</summary>
public enum PickerModel
{
    Rgb,
    Hsv,
}

/// <summary>
/// Colour picker geometry in logical pixels, plus every point/value conversion.
/// Paint and hit-test both read this file, so the drawn control and the clickable
/// control cannot drift apart.
/// The card is Blender's colour picker, circle-HSV variant
/// (editors/interface/regions/interface_region_color_picker.cc): the wheel with a thin
/// vertical value bar beside it, the two mode rows, four number sliders, then the hex
/// field and the eyedropper. Proportions follow the 194x309 reference card.
/// </summary>
public static class ColorPickerLayout
{
    public const float CardW = 320f;
    public const float CardH = 542f;
    public const float Pad = 20f;
    public const float Radius = 30f;

    // Reference card: the wheel takes the row, the bar is a thin strip at the far edge
    // with a hair of gap (Blender: PICKER_BAR, PICKER_SPACE).
    public const float BarW = 14f;
    public const float Space = 6f;
    public const float WheelD = CardW - Pad * 2f - BarW - Space;
    public const float WheelR = WheelD / 2f;
    /// <summary>Wheel centre; the wheel is a disc that fills its own box.</summary>
    public static readonly PointF Center = new(Pad + WheelR, Pad + WheelR);
    public const float MarkerR = 9f;
    public const float MarkerStroke = 2f;

    public const float RowH = 28f;
    public const float RowGap = 4f;
    public const float SpaceRowY = Pad + WheelD + 10f;
    public const float ModelRowY = SpaceRowY + RowH + RowGap;
    public const float SliderY0 = ModelRowY + RowH + 10f;
    public const float HexY = SliderY0 + 4 * (RowH + RowGap) + 6f;
    /// <summary>Room for the mode-row label text and for the number in a slider row.</summary>
    public const float ChanLabelW = 82f;
    public const float ChanValueW = 46f;
    public const float ChanGroove = 5f;
    public const float HexLabelW = 38f;
    public const float DropW = 32f;
    /// <summary>
    /// Inset for anything drawn inside a container: a segment's label, a slider's label
    /// and number, a text field's value. Nothing sits flush to a container edge
    /// (docs/design.md rule 21), so this is the only padding number in the picker.
    /// </summary>
    public const float InnerPad = 8f;

    public static readonly string[] SpaceLabels = ["Linear", "Perceptual"];
    public static readonly string[] ModelLabels = ["RGB", "HSV"];

    public static RectangleF Card => new(0f, 0f, CardW, CardH);

    /// <summary>The value bar: black at the top, the full hue at the bottom.</summary>
    public static RectangleF ValueTrack => new(CardW - Pad - BarW, Pad, BarW, WheelD);

    /// <summary>Half of a mode row: 0 = left segment, 1 = right segment.</summary>
    public static RectangleF Segment(int row, int half)
        => new(Pad + half * ((CardW - Pad * 2f) / 2f + RowGap), row == 0 ? SpaceRowY : ModelRowY,
            (CardW - Pad * 2f) / 2f - RowGap, RowH);

    /// <summary>Number-slider row 0..3 (RGB: R G B Alpha; HSV: Hue Saturation Value Alpha).</summary>
    public static RectangleF Slider(int i) => new(Pad, SliderY0 + i * (RowH + RowGap), CardW - Pad * 2f, RowH);

    /// <summary>The draggable groove inside a slider row.</summary>
    public static RectangleF Groove(int i)
    {
        var row = Slider(i);
        return new RectangleF(row.Left + ChanLabelW, row.Top + (row.Height - ChanGroove) / 2f,
            row.Width - ChanLabelW - ChanValueW, ChanGroove);
    }

    public static RectangleF HexLabel => new(Pad, HexY, HexLabelW, RowH);

    public static RectangleF Hex => new(Pad + HexLabelW + RowGap, HexY,
        CardW - Pad * 2f - HexLabelW - RowGap * 2f - DropW, RowH);

    public static RectangleF Eyedropper => new(CardW - Pad - DropW, HexY, DropW, RowH);

    /// <summary>Control under a logical point; a click on empty card space is a commit.</summary>
    public static PickerPart HitTest(float x, float y)
    {
        if (Eyedropper.Contains(x, y)) return PickerPart.Eyedropper;
        if (Hex.Contains(x, y)) return PickerPart.Hex;
        if (ValueTrack.Contains(x, y)) return PickerPart.Value;
        for (int i = 0; i < 4; i++)
            if (Slider(i).Contains(x, y)) return (PickerPart)((int)PickerPart.SliderA + i);
        if (Segment(0, 1).Contains(x, y) || Segment(0, 0).Contains(x, y)) return PickerPart.Space;
        if (Segment(1, 0).Contains(x, y) || Segment(1, 1).Contains(x, y)) return PickerPart.Model;
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

    /// <summary>Value under a point on the vertical bar: full at the top, black at the bottom.</summary>
    public static double ValueFromY(float y)
        => Math.Clamp(1.0 - (y - ValueTrack.Top) / ValueTrack.Height, 0.0, 1.0);

    public static float YFromValue(double v)
        => ValueTrack.Bottom - (float)Math.Clamp(v, 0.0, 1.0) * ValueTrack.Height;

    /// <summary>Slider value under a point, normalised to 0..1 along the groove.</summary>
    public static double SliderFromX(int i, float x)
    {
        var g = Groove(i);
        return Math.Clamp((x - g.Left) / g.Width, 0.0, 1.0);
    }

    public static float XFromSlider(int i, double t)
    {
        var g = Groove(i);
        return g.Left + (float)Math.Clamp(t, 0.0, 1.0) * g.Width;
    }
}

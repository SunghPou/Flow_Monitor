using System.Drawing;

namespace FlowMonitor.Widgets;

/// <summary>Which picker control a point belongs to.</summary>
public enum PickerPart
{
    None,
    Wheel,
    Value,
    ChannelR,
    ChannelG,
    ChannelB,
    Hex,
    Eyedropper,
}

/// <summary>
/// Colour picker geometry in logical pixels, plus every point/value conversion.
/// Paint and hit-test both read this file, so the drawn control and the clickable
/// control cannot drift apart.
/// The control set is Blender's colour picker
/// (editors/interface/regions/interface_region_color_picker.cc): a hue/saturation
/// wheel with a thin VERTICAL value bar right beside it, one number-slider row per RGB
/// channel, then the hex field and the eyedropper button. The top row's proportions are
/// Blender's own PICKER_TOTAL_W / PICKER_BAR / PICKER_SPACE.
/// </summary>
public static class ColorPickerLayout
{
    public const float CardW = 320f;
    public const float CardH = 447f;
    public const float Pad = 24f;
    public const float Radius = 30f;

    // Blender: PICKER_BAR = 8 UI units + 6 px and PICKER_SPACE = 8 units inside a
    // PICKER_TOTAL_W of 180 units; the wheel takes whatever the row has left.
    public const float BarW = 21f;
    public const float Space = 12f;
    public const float WheelD = CardW - Pad * 2f - BarW - Space;
    public const float WheelR = WheelD / 2f;
    /// <summary>Wheel centre; the wheel is a disc that fills its own box.</summary>
    public static readonly PointF Center = new(Pad + WheelR, Pad + WheelR);
    public const float MarkerR = 9f;
    public const float MarkerStroke = 2f;

    // Vertical rhythm: 24 of air at the top and the bottom, 20 between the major blocks.
    public const float RowH = 28f;
    public const float RowGap = 4f;
    public const float RowY0 = Pad + WheelD + 20f;
    public const float FieldY = RowY0 + 3 * (RowH + RowGap) + 20f;
    public const float FieldH = 28f;
    /// <summary>Room for the "R:" label and the number, so the groove has a clean run.</summary>
    public const float ChanLabelW = 26f;
    public const float ChanValueW = 36f;
    public const float ChanGroove = 5f;
    /// <summary>As tall as the hex field, wide enough for Blender's 32px dropper art.</summary>
    public const float DropW = 36f;
    public const float FieldGap = 8f;

    public static readonly string[] ChannelLabels = ["R:", "G:", "B:"];

    public static RectangleF Card => new(0f, 0f, CardW, CardH);

    /// <summary>The value bar: black at the top, the full hue at the bottom.</summary>
    public static RectangleF ValueTrack => new(Pad + WheelD + Space, Pad, BarW, WheelD);

    /// <summary>Number-slider row for one RGB channel (0 = R, 1 = G, 2 = B).</summary>
    public static RectangleF Channel(int i) => new(Pad, RowY0 + i * (RowH + RowGap), CardW - Pad * 2f, RowH);

    /// <summary>The draggable groove inside a channel row.</summary>
    public static RectangleF Groove(int i)
    {
        var row = Channel(i);
        float x = row.Left + ChanLabelW;
        return new RectangleF(x, row.Top + (row.Height - ChanGroove) / 2f, row.Width - ChanLabelW - ChanValueW, ChanGroove);
    }

    public static RectangleF Hex => new(Pad, FieldY, CardW - Pad * 2f - DropW - FieldGap, FieldH);

    public static RectangleF Eyedropper => new(CardW - Pad - DropW, FieldY, DropW, FieldH);

    /// <summary>Control under a logical point; a click on empty card space is a commit.</summary>
    public static PickerPart HitTest(float x, float y)
    {
        if (Eyedropper.Contains(x, y)) return PickerPart.Eyedropper;
        if (Hex.Contains(x, y)) return PickerPart.Hex;
        if (ValueTrack.Contains(x, y)) return PickerPart.Value;
        for (int i = 0; i < 3; i++)
            if (Channel(i).Contains(x, y)) return (PickerPart)((int)PickerPart.ChannelR + i);
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

    /// <summary>Channel byte under a point on a number slider's groove.</summary>
    public static byte ChannelFromX(int channel, float x)
    {
        var g = Groove(channel);
        double t = Math.Clamp((x - g.Left) / g.Width, 0.0, 1.0);
        return (byte)Math.Round(t * 255.0);
    }

    public static float XFromChannel(int channel, byte value)
    {
        var g = Groove(channel);
        return g.Left + (float)(value / 255.0) * g.Width;
    }
}

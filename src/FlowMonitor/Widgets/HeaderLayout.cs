using RectF = System.Drawing.RectangleF;

namespace FlowMonitor.Widgets;

/// <summary>
/// Header geometry in logical px. Single definition for paint and hit-test
/// (docs/design.md). The &lt; title &gt; group is centred on the card, the value is
/// right-anchored, and the visual gaps are measured glyph edge to glyph edge:
/// a hit box is allowed to overhang into its neighbour's gap, because only ink
/// is seen.
/// </summary>
public readonly record struct HeaderLayout(RectF Value, RectF Title, RectF Prev, RectF Next,
    RectF Close, RectF Check, float ValueMax)
{
    public const float Inset = 12f;       // card edge to ink (locked row)
    public const float GapText = 8f;      // min ink gap, text to text
    public const float GapChrome = 12f;   // min ink gap, text to chrome glyph
    public const float BadgeBox = 30f;    // X / check hit box (square, WidgetWindow.CheckMarkSize)
    public const float BadgeSpacing = 8f; // between the two badge hit boxes
    // Card edge to the check hit box: the corner bracket owns the outermost strip,
    // so the badge block starts one text gap inside it.
    public const float BadgeClear = WidgetPainter.CornerSpan + GapText;
    public const float ChevBox = 26f;     // metric chevron hit box (square)
    public const float ChevGap = 8f;      // chevron hit box to title ink
    public const float RowCenter = 19f;   // the one optical center of the row
    public const float BadgeTop = 4f;     // badge hit box top; 4 + 30/2 == RowCenter
    const float TitleH = 15f;
    const float ValueH = 26f;

    /// <summary>
    /// The X / check hit boxes, right-anchored. Single definition for paint, hit-test
    /// and the value's right edge (which is measured off the X glyph ink).
    /// </summary>
    public static (RectF Close, RectF Check) BadgeRects(float logicalW)
    {
        float checkR = logicalW - BadgeClear;
        var check = new RectF(checkR - BadgeBox, RowCenter - BadgeBox / 2f, BadgeBox, BadgeBox);
        var close = new RectF(check.Left - BadgeSpacing - BadgeBox, RowCenter - BadgeBox / 2f, BadgeBox, BadgeBox);
        return (close, check);
    }

    /// <summary>Left edge of a badge glyph's ink: the box pad plus the glyph half.</summary>
    public static float BadgeInkLeft(RectF box) => box.Left + BadgeBox / 2f - WidgetPainter.BadgeGlyphHalf;

    /// <summary>
    /// Lays out the header. titleW/valueW are measured text widths. The &lt; title &gt;
    /// group is centred on the card and never yields; the value is right-anchored and
    /// fitted into <see cref="ValueMax"/> (the renderer shortens or ellipsizes it), so a
    /// wide value can no longer drag the group off centre. Only a card too narrow to
    /// centre the group at all clips the title.
    /// </summary>
    public static HeaderLayout Compute(float logicalW, float titleW, float valueW, bool editing)
    {
        var (close, check) = editing ? BadgeRects(logicalW) : (RectF.Empty, RectF.Empty);

        // The group = [<] gap title gap [>]; each chevron reserves ChevGap plus its
        // glyph half beyond the title, so the ink group is symmetric about the title.
        float inkPad = editing ? WidgetPainter.ChevronGlyphHalf * 2f + ChevGap : 0f;
        float cx = logicalW / 2f;
        float titleW2 = titleW;
        if (cx - titleW / 2f - inkPad < Inset) titleW2 = Math.Max(0f, (cx - Inset - inkPad) * 2f);
        float groupInkRight = cx + titleW2 / 2f + inkPad;

        // Value right edge: clear of the X glyph ink (edit) or the card edge (locked).
        float valueRight = editing ? BadgeInkLeft(close) - GapChrome : logicalW - Inset;
        float valueMax = Math.Max(0f, valueRight - groupInkRight - GapText);
        float valueW2 = Math.Min(valueW, valueMax);
        float valueLeft = valueRight - valueW2;

        float tx = cx - titleW2 / 2f;
        var title = new RectF(tx, RowCenter - TitleH / 2f, titleW2 + 1f, TitleH);
        var value = new RectF(valueLeft, RowCenter - ValueH / 2f, valueW2, ValueH);

        RectF prev = RectF.Empty, next = RectF.Empty;
        if (editing)
        {
            // Hit boxes are wider than the ink: each is centred on its glyph and may
            // overhang into the gap, but never past the value or the card edge.
            float prevC = tx - ChevGap - WidgetPainter.ChevronGlyphHalf;
            float nextC = tx + titleW2 + ChevGap + WidgetPainter.ChevronGlyphHalf;
            prev = new RectF(Math.Max(Inset, prevC - ChevBox / 2f), RowCenter - ChevBox / 2f, ChevBox, ChevBox);
            next = new RectF(Math.Min(nextC - ChevBox / 2f, valueLeft - ChevBox),
                RowCenter - ChevBox / 2f, ChevBox, ChevBox);
        }
        return new HeaderLayout(value, title, prev, next, close, check, valueMax);
    }
}

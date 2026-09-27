using RectF = System.Drawing.RectangleF;

namespace FlowMonitor.Widgets;

/// <summary>
/// Header geometry in logical px. Single definition for paint and hit-test
/// (docs/design.md). The X sits in the top-left corner and the check in the
/// top-right, mirrored about the card centre; the &lt; title &gt; group is centred,
/// the value is right-anchored, and the gaps are measured glyph edge to glyph
/// edge: a hit box may overhang into its neighbour's gap, because only ink is seen.
/// </summary>
public readonly record struct HeaderLayout(RectF Value, RectF Title, RectF Prev, RectF Next,
    RectF Close, RectF Check, float ValueMax)
{
    public const float Inset = 12f;       // card edge to ink (locked row)
    public const float GapText = 8f;      // min ink gap, text to text
    public const float GapChrome = 12f;   // min ink gap, text to chrome glyph
    public const float BadgeBox = 30f;    // X / check hit box (square, WidgetWindow.CheckMarkSize)
    // Card edge to each badge's glyph ink: the corner bracket owns the outermost
    // strip, so the badges start one text gap inside it. Both edges use this same
    // inset, which is what makes the pair mirror-symmetric.
    public const float BadgeClear = WidgetPainter.CornerSpan + GapText;
    public const float ChevBox = 26f;     // metric chevron hit box (square)
    public const float ChevGap = 8f;      // chevron hit box to title ink
    public const float RowCenter = 19f;   // the one optical center of the row
    const float TitleH = 15f;
    const float ValueH = 26f;

    /// <summary>
    /// The X (left corner) and check (right corner) hit boxes, mirrored about the
    /// card's vertical centre line. Single definition for paint, hit-test and the
    /// reserves the value and the centred group keep off them.
    /// </summary>
    public static (RectF Close, RectF Check) BadgeRects(float logicalW)
    {
        float center = BadgeClear + WidgetPainter.BadgeGlyphHalf;
        return (new RectF(center - BadgeBox / 2f, RowCenter - BadgeBox / 2f, BadgeBox, BadgeBox),
                new RectF(logicalW - center - BadgeBox / 2f, RowCenter - BadgeBox / 2f, BadgeBox, BadgeBox));
    }

    /// <summary>Left edge of a badge glyph's ink: the box pad plus the glyph half.</summary>
    public static float BadgeInkLeft(RectF box) => box.Left + BadgeBox / 2f - WidgetPainter.BadgeGlyphHalf;
    /// <summary>Right edge of a badge glyph's ink.</summary>
    public static float BadgeInkRight(RectF box) => box.Left + BadgeBox / 2f + WidgetPainter.BadgeGlyphHalf;

    /// <summary>
    /// Chevron hit box of at most ChevBox centred on its glyph, held inside
    /// [minL, maxR]: a clamp shortens the box instead of sliding it off the glyph.
    /// </summary>
    static RectF ChevHitBox(float glyphCenter, float minL, float maxR)
    {
        float l = glyphCenter - ChevBox / 2f, r = glyphCenter + ChevBox / 2f;
        if (l < minL) { r = Math.Min(r + (minL - l), maxR); l = minL; }
        if (r > maxR) { l = Math.Max(l - (r - maxR), minL); r = maxR; }
        return new RectF(l, RowCenter - ChevBox / 2f, Math.Max(0f, r - l), ChevBox);
    }

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
        // Left reserve: the X glyph ink in edit mode, the card edge otherwise.
        float leftReserve = editing ? BadgeInkRight(close) + GapChrome : Inset;
        float titleW2 = titleW;
        if (cx - titleW / 2f - inkPad < leftReserve)
            titleW2 = Math.Max(0f, (cx - leftReserve - inkPad) * 2f);
        float groupInkRight = cx + titleW2 / 2f + inkPad;

        // Value right edge: clear of the check glyph ink (edit) or the card edge (locked).
        float valueRight = editing ? BadgeInkLeft(check) - GapChrome : logicalW - Inset;
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
            // overhang into the gap, but never past the value, the X badge or the edge.
            prev = ChevHitBox(tx - ChevGap - WidgetPainter.ChevronGlyphHalf, leftReserve, valueLeft);
            next = ChevHitBox(tx + titleW2 + ChevGap + WidgetPainter.ChevronGlyphHalf, leftReserve, valueLeft);
        }
        return new HeaderLayout(value, title, prev, next, close, check, valueMax);
    }
}

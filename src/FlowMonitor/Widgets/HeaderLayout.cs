using RectF = System.Drawing.RectangleF;

namespace FlowMonitor.Widgets;

/// <summary>
/// Header geometry in logical px. Single definition for paint and hit-test
/// (docs/design.md). The X's ink ends on the tick column, the check mirrors it about
/// the card centre; the &lt; title &gt; group is centred, the value is right-anchored, and
/// the gaps are measured glyph edge to glyph edge: a hit box may overhang into its
/// neighbour's gap, because only ink is seen.
/// </summary>
public readonly record struct HeaderLayout(RectF Value, RectF Title, RectF Prev, RectF Next,
    RectF Close, RectF Check, RectF Chip, float ValueMax)
{
    public const float Inset = 12f;       // card edge to ink (locked row)
    public const float GapText = 8f;      // min ink gap, text to text
    public const float GapChrome = 12f;   // min ink gap, text to chrome glyph
    public const float BadgeBox = 30f;    // X / check hit box (square, WidgetWindow.CheckMarkSize)
    // Smallest card edge to badge ink: the corner bracket owns the outermost strip, so
    // the badges start one text gap inside it.
    public const float BadgeClear = WidgetPainter.CornerSpan + GapText;
    // Hard floor for a left-aligned X badge's ink: the card inset, the same left edge
    // the tick column starts on. The corner bracket's arms live in the card's top strip
    // and are asserted not to collide with the glyph, so the floor needs no bracket
    // arithmetic (docs/design.md header rule 5).
    public const float MinInkLeft = Inset;
    public const float ChipDia = 16f;      // colour chip swatch (edit mode only)
    public const float ChevBox = 26f;     // metric chevron hit box (square)
    public const float ChevGap = 8f;      // chevron hit box to title ink
    /// <summary>
    /// The header row is EVERY element in the widget's upper part except the corner
    /// brackets, and they move as one: the badges, the chip, the chevrons, the title and
    /// the value all take their y from this one centre. It is the middle of the band
    /// between the widget's top edge and the first horizontal line (the plot's top edge),
    /// so the row always sits centred in the space above the chart. <see cref="RowCenter"/>
    /// is only the fallback for callers with no plot geometry.
    /// </summary>
    public const float RowCenter = 19f;
    /// <summary>Smallest band that still holds a badge box plus a little air.</summary>
    public const float MinRowBand = 38f;
    /// <summary>Row centre for a band of <paramref name="bandLogical"/> logical px under the top edge.</summary>
    public static float RowCenterFor(float bandLogical) => Math.Max(bandLogical, MinRowBand) / 2f;

    const float TitleH = 15f;
    const float ValueH = 26f;

    /// <summary>
    /// The X (left) and check (right) hit boxes. The X glyph's ink starts on anchorX — the
    /// tick column's left edge — and the check mirrors that distance about the card's
    /// centre line, never coming closer to its edge than <see cref="BadgeClear"/> so it
    /// keeps clear of the top-right bracket. anchorX 0 means no tick column.
    /// </summary>
    public static (RectF Close, RectF Check) BadgeRects(float logicalW, float anchorX,
        float rowCenter = RowCenter)
    {
        // anchorX > 0: the tick column's left ink edge, so the X heads the values.
        // anchorX == 0: no column, sit at the preferred BadgeClear inset.
        float inkL = anchorX > 0f ? Math.Max(anchorX, MinInkLeft) : BadgeClear;
        float xCenter = inkL + WidgetPainter.BadgeGlyphHalf;
        // Mirror the X, but never closer to the right edge than the bracket it clears.
        float checkInkR = Math.Min(logicalW - inkL, logicalW - BadgeClear);
        float checkCenter = checkInkR - WidgetPainter.BadgeGlyphHalf;
        return (new RectF(xCenter - BadgeBox / 2f, rowCenter - BadgeBox / 2f, BadgeBox, BadgeBox),
                new RectF(checkCenter - BadgeBox / 2f, rowCenter - BadgeBox / 2f, BadgeBox, BadgeBox));
    }

    /// <summary>Left edge of a badge glyph's ink: the box pad plus the glyph half.</summary>
    public static float BadgeInkLeft(RectF box) => box.Left + BadgeBox / 2f - WidgetPainter.BadgeGlyphHalf;
    /// <summary>Right edge of a badge glyph's ink.</summary>
    public static float BadgeInkRight(RectF box) => box.Left + BadgeBox / 2f + WidgetPainter.BadgeGlyphHalf;

    /// <summary>
    /// Chevron hit box of at most ChevBox centred on its glyph, held inside
    /// [minL, maxR]: a clamp shortens the box instead of sliding it off the glyph.
    /// </summary>
    static RectF ChevHitBox(float glyphCenter, float minL, float maxR, float rowCenter)
    {
        float l = glyphCenter - ChevBox / 2f, r = glyphCenter + ChevBox / 2f;
        if (l < minL) { r = Math.Min(r + (minL - l), maxR); l = minL; }
        if (r > maxR) { l = Math.Max(l - (r - maxR), minL); r = maxR; }
        return new RectF(l, rowCenter - ChevBox / 2f, Math.Max(0f, r - l), ChevBox);
    }

    /// <summary>
    /// Lays out the header. titleW/valueW are measured text widths, anchorX the tick
    /// column's right edge (0 when the card has no labels). The &lt; title &gt; group is
    /// centred on the card and never yields; the value is right-anchored and fitted into
    /// <see cref="ValueMax"/> (the renderer shortens or ellipsizes it), so a wide value can
    /// no longer drag the group off centre. Only a card too narrow to centre the group at
    /// all clips the title.
    /// </summary>
    public static HeaderLayout Compute(float logicalW, float titleW, float valueW, bool editing,
        float anchorX = 0f, float rowCenter = RowCenter)
    {
        var (close, check) = editing ? BadgeRects(logicalW, anchorX, rowCenter) : (RectF.Empty, RectF.Empty);

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
        var title = new RectF(tx, rowCenter - TitleH / 2f, titleW2 + 1f, TitleH);
        var value = new RectF(valueLeft, rowCenter - ValueH / 2f, valueW2, ValueH);

        RectF prev = RectF.Empty, next = RectF.Empty;
        if (editing)
        {
            // Hit boxes are wider than the ink: each is centred on its glyph and may
            // overhang into the gap, but never past the value, the X badge or the edge.
            prev = ChevHitBox(tx - ChevGap - WidgetPainter.ChevronGlyphHalf, leftReserve, valueLeft, rowCenter);
            next = ChevHitBox(tx + titleW2 + ChevGap + WidgetPainter.ChevronGlyphHalf, leftReserve, valueLeft, rowCenter);
        }
        // The colour chip opens the picker. It is pushed against the centred group
        // (GapChrome clear of its left ink edge) so it sits in the gap between the
        // X and the metric arrows; it is dropped when that gap cannot hold it.
        RectF chip = RectF.Empty;
        if (editing)
        {
            float groupInkLeft = tx - inkPad;
            float chipRight = groupInkLeft - GapChrome;
            if (leftReserve + ChipDia + GapText <= chipRight)
                chip = new RectF(chipRight - ChipDia, rowCenter - ChipDia / 2f, ChipDia, ChipDia);
        }
        return new HeaderLayout(value, title, prev, next, close, check, chip, valueMax);
    }
}

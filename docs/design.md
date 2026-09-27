# FlowMonitor design rules

Researched from the 8pt grid system (all spacing in multiples of 8/4) and
Apple HIG toolbar guidance (~8–10px gaps between buttons, 12–16px container
insets). Anyone touching widget chrome follows this file.

## Tokens (logical px)

| Token | Value | Use |
|---|---|---|
| `Inset` | 12 | outer pad: card edge to any chrome |
| `GapText` | 8 | min gap between two text elements |
| `GapChrome` | 12 | min gap between text and any badge/glyph |
| `BadgeBox` | 30 | X / checkmark hit box (square) |
| `BadgeGlyphHalf` | 6.14 | badge ink half-width, stroke included; what every reserve measures |
| `BadgeClear` | 24.5 | preferred card edge to badge ink on cards with no tick column |
| `MinAnchor` | 28.8 | hard floor for the X anchor: `CornerSpan` + one full glyph |
| `ChevBox` | 26 | metric chevron hit box (square) |
| `ChevGap` | 8 | gap between chevron and title |
| `RowCenter` | 19 | the single optical center of the header row |

A reserve is always a real ink edge (`BadgeGlyphHalf`, not the hit box): anchoring
to a box would leave the glyph visibly short of the line it is meant to sit on.

## Header rules

1. One row, one optical center. Title, value, chevrons, badges all share
   `RowCenter`; nothing is eyeballed per element.
2. The value is right-anchored and always wins. It is drawn last so it can
   never be struck through or pushed.
3. The `< title >` group is glued together (`ChevGap`) and centered on the
   card: the title is centered, and the group's ink is symmetric about the
   title. The value yields width (`FitValue`) instead of moving the centre.
4. Minimum gaps are structural: `GapText` text-to-text, `GapChrome`
   text-to-chrome. Ink that is *reserved* can never be overlapped; a hit box may
   overhang into a gap (invisible), and only a card too narrow to centre the
   group clips the title.
5. The X heads the axis tick column: its ink ends flush on the column's right
   ink edge (not the gutter box, which sits one `GapText` further right), never
   left of `MinAnchor` so it keeps clear of the corner bracket, and never past
   the card's centre line. With no tick column it sits at the `BadgeClear`
   inset instead. The check mirrors whichever distance was used, so the pair is
   symmetrical on any card. The value clears the check ink by `GapChrome` and
   the centered group clears the X ink by `GapChrome`.
6. Paint and hit-test share one definition (`HeaderLayout`): the rects used to
   paint are the rects used to hit-test, taken from the last painted frame. A
   hit box may be shortened by a clamp, never slid off its glyph.
7. Layout is binary (edit vs locked). Only opacity and glyph scale animate:
   every centre, anchor and gap is fixed for the whole fade, and a glyph grows
   about its own centre.

## Axis rules

8. Percent axes print no labels and take no gutter; byte axes keep five.
9. Byte ticks always carry their unit ("8 GB", "1 KB/s"), so no gridline can
   be misread as a bare number.
10. Byte scales are powers of two (Mission Center's `RoundingSettings::Pow2`),
    floored at 4 units, so the quarters print as whole numbers. Fixed-scale
    byte axes with a fixed scale (RAM) use the real total instead of rounding.
11. The label column is sized from the labels themselves and never moves
    between locked and edit mode; the bottom-left corner bracket yields to it
    instead, so a bracket arm can never cross a tick.

## Colour

17. The edit-mode colour chip is a filled circle in the free zone between the X badge and
    the centred group, GapChrome clear of the X ink. It is dropped when the card is too
    narrow to hold it, so the group never moves.
18. The picker follows the system theme, never a hardcoded white: card #424242 in dark mode,
    #CCCCCC in light mode (`SystemTheme.Card`, read from AppsUseLightTheme). Ink, hairlines
    and pills come from the same palette.
19. Paint and hit-test share `ColorPickerLayout`; the ring runs red at 3 o'clock
    clockwise (red, magenta, blue, cyan, green, yellow) and the disc is white at the top,
    hue at the rim, black at the bottom.
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
| `BadgeGap` | 8 | gap between the two right badges |
| `ChevBox` | 26 | metric chevron hit box (square) |
| `ChevGap` | 8 | gap between chevron and title |
| `RowCenter` | 19 | the single optical center of the header row |

## Header rules

1. One row, one optical center. Title, value, chevrons, badges all share
   `RowCenter`; nothing is eyeballed per element.
2. The value is right-anchored and always wins. It is drawn last so it can
   never be struck through or pushed.
3. The `< title >` group is glued together (`ChevGap`) and centered on the
   card: the title is centered, and the group's ink is symmetric about the
   title. The value yields width (`FitValue`) instead of moving the centre.
4. Minimum gaps are structural: `GapText` text-to-text, `GapChrome`
   text-to-chrome. Layout clamps by construction; no overlap is possible.
5. Badges sit at the right inset; the value clears their ink by `GapChrome`,
   and the check glyph clears the corner bracket by `GapText`.
6. Paint and hit-test share one definition (`HeaderLayout`); a glyph is
   clickable exactly where it is drawn.
7. Layout is binary (edit vs locked); only opacity animates. Positions never
   drift mid-fade.

## Axis rules

8. Percent axes print no labels and take no gutter; byte axes keep five.
9. Byte ticks always carry their unit ("8 GB", "1 KB/s"), so no gridline can
   be misread as a bare number.
10. Byte scales are powers of two (Mission Center's `RoundingSettings::Pow2`),
    floored at 4 units, so the quarters print as whole numbers. Fixed-scale
    byte axes (RAM, VRAM) use the real total instead.
11. The label column is sized from the labels themselves and is inset past the
    edit-mode corner bracket, so a bracket arm can never cross a tick.

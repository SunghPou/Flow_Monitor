# Eyedropper: what was tried, what is true

Goal (user's spec): click the eyedropper icon -> Windows cursor becomes the pipette ->
user moves it anywhere -> click sets the colour. No drag-averaging, no magnifier.

## Blender (source-read, GPL ref-only): what the real one does
- Gesture is press-hold-release; bare release/`RET` degrades to a single-point pick
  (`accum_tot == 0` path). Live write-back on every sample; cancel restores `init_col`.
- The picker popup CLOSES when the eyedropper starts (`popup_close_cb`); the
  eyedropper runs as its own blocking modal. (We keep our card up instead: hiding it
  tore down the composition target and closed the popup — commit bd92448.)
- The cursor is per-window (`WM_cursor_modal_set`): over other apps' windows the OS
  cursor shows. Desktop pixels are still sampled (`GetCursorPos` + `GetDC(NULL)` +
  `GetPixel` — exactly our `ScreenPick.SampleAt`).

## Tried paths (do NOT repeat)
1. `PostMessage` synthetic clicks: never entered edit mode / opened nothing. Dead.
2. `WH_MOUSE_LL` hook: runs BEFORE the target's `WM_SETCURSOR`, so re-applying the
   cursor there loses by ordering. Deleted (`GlobalMouseHook.cs`).
3. `SetCursor` once on icon click: the next window's `WM_SETCURSOR` replaces it on the
   next mouse move. Never sticks alone.
4. 16ms `WM_TIMER` coalescer for the live push: the timer never fired (zero
   `card paint` lines ever) — fixed by pushing synchronously in `NotifyChanged`.
5. Same 16ms timer for cursor re-assert: same starvation, same silence.
6. `SetCapture` + 10ms timer + `GetAsyncKeyState` edge detect: pick loop exited in
   ~1ms three times (`pick start` -> `pick end`, no tick, no pick). Capture also
   floods our queue with moves, starving the very timer the design depends on.
7. `SetCursor(NULL)` to "restore": hides the pointer. Non-dropper `WM_SETCURSOR`
   over the card made the cursor vanish. Now sets `IDC_ARROW`.
8. `BITMAPINFOHEADER` with `uint biPlanes/biBitCount`: 44 bytes, `CreateDIBSection`
   refuses it. They are `WORD`s (40 bytes total).
9. Freeing the DIB right after `CreateCursor`: keep it alive in `_keptBmp`.
10. In-process `SetCursor` -> `GetCursorInfo` readback: races any live mouse move
    (each move re-asserts that window's cursor). Stable signal is the swap itself
    (`SetCursor` returns the previous handle); sightings need a still mouse.

## Verified facts (selftest asserts, run every time)
- `EyedropperCursor.Handle != 0` (pipette mask builds from Blender's icon).
- Rendered wheel pixels: cyan top, red bottom (31-picker sheet, `2b`).
- Wheel mapping: red at bottom, clockwise (`3d`); shader comment records the
  clip-y-up vs bitmap-row-down sign.

## Verification recipe (the self-serve loop)
- Drive the live app with `SendInput` (real input pipeline; `PostMessage` does not
  work): right-click widget center -> edit, left-click chip -> `picker: open` in log,
  left-click eyedropper -> `pick start`, `GetCursorInfo` must read the pipette
  handle, move -> still pipette, click desktop -> `picked #HEX at (x,y)`.
- Cursor handles are session-global: the AW value from `GetCursorInfo` in PowerShell
  compares directly with the handle the app logs.

## Current design
Fullscreen transparent overlay (own hwnd, same proven `WidgetSurface`/DComp path as
widgets — transparent regions provably receive clicks) shown during the pick: its
`WM_SETCURSOR` always answers the pipette, `WM_LBUTTONDOWN` samples + closes,
`Esc`/right-click cancels. No capture, no timer, no hook, no polling — the click
arrives as a real message, so nothing can starve.

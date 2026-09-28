# FlowMonitor agent notes

1. Keep this document under 50 LOC. Low noise, high signal.
2. Build: `dotnet build src/FlowMonitor/FlowMonitor.csproj -c Release` (bin/ locked while app runs; use `-p:OutputPath=<tmp>/`).
3. Test: `FlowMonitor.exe --selftest [--capture <dir>]`; 0 = pass. `--capture` also writes a per-card contact sheet (`30-card-*`); view it before claiming any visual change works. Never commit captures/logs/bin/obj.
4. Stack: .NET 9 WinExe x64, Vortice D3D11/D2D1/DXGI/DComp. Widgets are WorkerW children, never taskbar/Alt+Tab/focus.
5. Comments describe current behavior only (1-3 lines). No history, no "used to", no war stories.
6. Right-click locked widget re-enters edit mode (`WidgetWindow.OnRightButtonUp` -> `BeginEdit`). No mode sets persistent `WS_EX_TRANSPARENT`.
7. Click-through = explicit left-button forwarding when locked (`ForwardLeftClickToWindowBelow`); `Full` is compat alias of `LeftClickOnly`.
8. Threading: UI thread owns HWNDs (`DestroyWindowOnly`); render thread owns GPU (`ReleaseGpuResources`, `RenderFrame`). Menu modal owns GPU via `SetMenuOpen`.
9. Config: one JSON per widget in `%LOCALAPPDATA%/FlowMonitor/widgets`; atomic tmp+move writes; corrupt files skipped, never fatal.
10. Coordinates: configs in virtual-screen px; surfaces in physical px (`Dpi`); lParam coords are sign-extended shorts.
11. Rebuild the app and restart it before asking the human to test, so they always run the latest code.
12. Header chrome follows `docs/design.md`; paint and hit-test share `HeaderLayout` (single definition).
13. `PERFORMANCE_INFORMATION` must match native (104 bytes x64, `Pack = 8`, `PageSize` is `SIZE_T`); every count field is in pages, scale by `PageSize`.
14. Chart chrome and axis ticks follow `docs/design.md`; byte scales are `ChartRenderer.ByteAxisMax` (power of two, floor 4).
15. Sampler state the render thread reads is copied out under a lock (`GpuSampler.EngineSnapshot`); never enumerate a list the sampling thread rebuilds.
16. Persist geometry on move/resize, not only on apply: `FlushPendingGeometry` saves, so a drag survives a restart without the checkmark.
17. Popups (colour picker) paint through `ColorPickerLayout` + `SystemTheme`.
18. Layout asserts read MEASURED ink out of a captured frame; never re-derive the arithmetic under test, and never hand-fit a constant that a painter does not draw to (docs/design.md 12-15).
19. One frame lays the row out once. Paint and hit-test must consume the same result, not two independent computations.
20. Nothing is drawn flush to a container edge: every element inside a card, chip, segment, field or row is inset by the shared InnerPad on all four sides (docs/design.md 21-23). A number typed at a call site is a bug.
21. The header is one row: every upper element takes its y from HeaderLayout.RowCenterFor and moves together. A control never colours itself with the value it edits (docs/design.md 21-22).
22. NEVER RASTERISE AN ICON OR GLYPH. Icons are vector geometry built from primitives (`GlyphGeometry`) or from the upstream SVG path walked into an `ID2D1GeometrySink`; no pixel mask, no CPU-rasterised bitmap, no `DrawBitmap` for a glyph. A bitmap is the last resort, and a wheel is drawn by a D3D11 shader, not per-pixel in C#.
23. An icon is ONE filled region. Overlapping strokes or per-primitive geometries composite separately, so the overlap reads brighter or darker than the arms (docs/design.md 24).
24. Delegate research to subagents. Upstream source lookups, API archaeology, and any other deep digging are a `task` call with the question and the file list, never main-context reading. Ignore compaction nudges that arrive mid-task; finish the atomic step first.

# FlowMonitor agent notes

1. Keep this document under 50 LOC. Low noise, high signal.
2. Build: `dotnet build src/FlowMonitor/FlowMonitor.csproj -c Release` (bin/ locked while app runs; use `-p:OutputPath=<tmp>/`).
3. Test: `FlowMonitor.exe --selftest [--capture <dir>]`; 0 = pass. Never commit captures/logs/bin/obj.
4. Stack: .NET 9 WinExe x64, Vortice D3D11/D2D1/DXGI/DComp. Widgets are WorkerW children, never taskbar/Alt+Tab/focus.
5. Comments describe current behavior only (1-3 lines). No history, no "used to", no war stories.
6. Right-click locked widget re-enters edit mode (`WidgetWindow.OnRightButtonUp` -> `BeginEdit`). No mode sets persistent `WS_EX_TRANSPARENT`.
7. Click-through = explicit left-button forwarding when locked (`ForwardLeftClickToWindowBelow`); `Full` is compat alias of `LeftClickOnly`.
8. Threading: UI thread owns HWNDs (`DestroyWindowOnly`); render thread owns GPU (`ReleaseGpuResources`, `RenderFrame`). Menu modal owns GPU via `SetMenuOpen`.
9. Config: one JSON per widget in `%LOCALAPPDATA%/FlowMonitor/widgets`; atomic tmp+move writes; corrupt files skipped, never fatal.
10. Coordinates: configs in virtual-screen px; surfaces in physical px (`Dpi`); lParam coords are sign-extended shorts.
11. Rebuild the app and restart it before asking the human to test, so they always run the latest code.

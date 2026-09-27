# Vortice 3.8.3 API cheat-sheet (verified by reflection)

Verified against the actual 3.8.3 assemblies on this machine. Names below are exact.

## D2D1 factory / device
```csharp
ID2D1Factory8  f = D2D1.D2D1CreateFactory<ID2D1Factory8>();          // or <ID2D1Factory1>
ID2D1Device    d = f.CreateDevice(dxgiDevice);                     // ID2D1Factory1.CreateDevice(IDXGIDevice)
ID2D1DeviceContext dc = d.CreateDeviceContext(DeviceContextOptions.None);
ID2D1Bitmap1   bmp = dc.CreateBitmapFromDxgiSurface(dxgiSurface, null);
dc.Target = bmp;        // ID2D1RenderTarget.Target { get; set; } (ID2D1Image)
```
## DXGI
```csharp
IDXGIFactory2        fx = D3D11.CreateDXGIFactory2<IDXGIFactory2>();  // check exact helper name
IDXGISwapChain1      sc = fx.CreateSwapChainForComposition(d3dDevice, desc);
ID3D11Texture2D      tex = sc.GetBuffer<ID3D11Texture2D>(0);
IDXGISurface         surf = D3D11.GetDXGISurface(tex);
Result               r = sc.Present(1, PresentFlags.None);
```
## D3D11
```csharp
D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                        new[]{ FeatureLevel.Level_11_1 }, out ID3D11Device dev,
                        out FeatureLevel fl, out ID3D11DeviceContext imm);
```
## DirectComposition
```csharp
IDCompositionDevice  cd  = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
IDCompositionTarget  tgt = cd.CreateTargetForHwnd(hwnd, false);
IDCompositionVisual  vis = cd.CreateVisual();
vis.SetContent(swapChain);
tgt.SetRoot(vis);
cd.Commit();
```
Useful visual members: `SetOffsetX/Y(float|IDCompositionAnimation)`, `SetTransform(Matrix3x2)`,
`SetClip(RawRectF)`, `SetOpacity(float)`, `SetVisible(bool)`, `SetEffect(IDCompositionEffect)`.
Device: `CreateTranslateTransform()`, `CreateScaleTransform()`, `CreateMatrixTransform()`,
`CreateGaussianBlurEffect()`, `CreateRectangleClip()`, `CreateEffectGroup()`, `CreateTargetForHwnd`.
Visual3: `SetOpacity(float)` — so create visuals as `IDCompositionVisual3` for opacity/visible.
Device3: `CreateAffineTransform2DEffect()`, `CreateGaussianBlurEffect()`, `CreateColorMatrixEffect()`.

## Draw calls (ID2D1RenderTarget)
`BeginDraw()`, `Clear(Color4?)`, `EndDraw()`, `SetDpi(f,f)`,
`DrawGeometry(geom, brush, strokeWidth, strokeStyle)`, `FillGeometry(geom, brush)`,
`DrawLine(Vector2,Vector2,brush,width,style)`, `FillRoundedRectangle(RoundedRectangle, brush)`,
`DrawText(string, IDWriteTextFormat, Rect, ID2D1Brush)`, `DrawTextLayout(...)`,
`CreateSolidColorBrush(Color4, BrushProperties?)`, `CreateLinearGradientBrush(props, stops)`,
`CreateGradientStopCollection(GradientStop[], Gamma, ExtendMode)`,
`CreateStrokeStyle(StrokeStyleProperties1, float[] dashes)`, `CreateRoundedRectangleGeometry(...)`,
`CreatePathGeometry()` (ID2D1Factory1) -> `OpenSink()`.

Structs: `GradientStop(float position, Color4 color)`, `BrushProperties(float opacity)`,
`LinearGradientBrushProperties(Vector2 start, Vector2 end)`,
`StrokeStyleProperties1 { DashStyle, DashCap, StartCap, EndCap, LineJoin, MiterLimit, DashOffset }`,
`RenderTargetProperties`, `HwndRenderTargetProperties`, `RoundedRectangle(Rect,float,float)`,
`PixelFormat`, `BitmapProperties1`, `LayerParameters1`.

## Gotchas
- `Vortice.Direct2D1.D2D1` is the static entry class (not `Factory`).
- `ID2D1DeviceContext` has **no** `CreateSwapChain1` -> use `dc.Target = bitmapFromDxgiSurface`.
- Assemblies to reference: Vortice.Direct2D1, .Direct3D11, .DXGI, .DirectComposition
  (pulls DirectWrite, WIC, DXCore, D3D12, Mathematics automatically).
- No C++ toolchain on this box: VS 18 Community shell only, no MSBuild/vcvars. `dotnet` 9.0.202 only.

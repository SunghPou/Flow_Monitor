using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;

namespace FlowMonitor.Rendering;

/// <summary>
/// Owns the D3D11 / Direct2D / DirectComposition / DirectWrite object graph shared by every
/// widget in the process. Created once, disposed on shutdown.
/// </summary>
public sealed class RenderDevice : IDisposable
{
    public ID3D11Device D3DDevice { get; }

    /// <summary>
    /// Serialises every user of this device: the render thread frames and the
    /// modal popup (colour picker) that paints from the UI thread.
    /// </summary>
    public object GpuLock { get; } = new();

    /// <summary>
    /// Why the device went away, or S_OK if healthy. Device loss is process-wide
    /// (single shared D3D/D2D/DComp device), so any failure here is not widget-local.
    /// </summary>
    // NOTE: ID3D11Device::GetDeviceRemovedReason is not surfaced by Vortice 3.8.3;
    // classify device-wide vs widget-local by how many widgets faulted in the same frame.
    public const int DeviceReasonUnavailable = unchecked((int)0x80004005); // E_FAIL: cannot ask

    public ID3D11DeviceContext D3DContext { get; }
    public IDXGIDevice DxgiDevice { get; }
    public IDXGIFactory4 DxgiFactory { get; }
    public ID2D1Factory1 D2DFactory { get; }
    public ID2D1Device D2DDevice { get; }
    public IDCompositionDevice CompositionDevice { get; }
    public IDCompositionDevice3? CompositionDevice3 { get; }
    public IDWriteFactory DWriteFactory { get; }

    /// <summary>
    /// Factory for device-independent resources (brushes). Valid on every target
    /// created from the same ID2D1Device, so one shared context serves all widgets.
    /// </summary>
    public ID2D1DeviceContext ResourceContext { get; }

    public RenderDevice()
    {
        Vortice.Direct3D11.D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null!,
            out ID3D11Device d3dDevice,
            out _,
            out ID3D11DeviceContext d3dContext);
        D3DDevice = d3dDevice;
        D3DContext = d3dContext;

        DxgiDevice = D3DDevice.QueryInterfaceOrNull<IDXGIDevice>()
            ?? throw new InvalidOperationException("D3D11 device does not expose IDXGIDevice.");
        DxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();

        D2DFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded, DebugLevel.None);
        D2DDevice = D2DFactory.CreateDevice(DxgiDevice);

        // Composition device is created from the DXGI device, not via QI off ID2D1Device.
        CompositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(DxgiDevice)
            ?? throw new InvalidOperationException("DCompositionCreateDevice returned null.");
        CompositionDevice3 = CompositionDevice.QueryInterfaceOrNull<IDCompositionDevice3>();

        DWriteFactory = DWrite.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);

        ResourceContext = D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
    }

    public IDWriteTextFormat CreateTextFormat(
        string family, float size, FontWeight weight, FontStyle style = FontStyle.Normal,
        TextAlignment hAlign = TextAlignment.Leading, ParagraphAlignment vAlign = ParagraphAlignment.Center,
        WordWrapping wrap = WordWrapping.NoWrap)
    {
        var fmt = DWriteFactory.CreateTextFormat(family, weight, style, FontStretch.Normal, size);
        fmt.TextAlignment = hAlign;
        fmt.ParagraphAlignment = vAlign;
        fmt.WordWrapping = wrap;
        // Flow and reading direction must be perpendicular or GetMetrics fails (0x8898500B).
        fmt.ReadingDirection = Vortice.DirectWrite.ReadingDirection.LeftToRight;
        fmt.FlowDirection = Vortice.DirectWrite.FlowDirection.TopToBottom;
        return fmt;
    }

    public void Dispose()
    {
        CompositionDevice3?.Dispose();
        ResourceContext.Dispose();
        DWriteFactory.Dispose();
        CompositionDevice.Dispose();
        D2DDevice.Dispose();
        D2DFactory.Dispose();
        DxgiFactory.Dispose();
        DxgiDevice.Dispose();
        D3DContext.Dispose();
        D3DDevice.Dispose();
    }
}

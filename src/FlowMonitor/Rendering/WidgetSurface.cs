using FlowMonitor.Interop;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Color4 = Vortice.Mathematics.Color4;
using SizeI = Vortice.Mathematics.SizeI;

namespace FlowMonitor.Rendering;

/// <summary>
/// Per-widget composition + swap chain + Direct2D device context.
/// The swap chain is a composition swap chain bound to the widget HWND through
/// DirectComposition, so the widget composites as a real desktop child (no taskbar,
/// no alt-tab, no flicker).
/// </summary>
public sealed class WidgetSurface : IDisposable
{
    public IntPtr Hwnd { get; }
    public ID2D1DeviceContext Context { get; }
    public IDCompositionVisual RootVisual { get; }
    public IDCompositionVisual ContentVisual { get; }

    IDXGISwapChain1? _swapChain;
    ID2D1Bitmap1? _target;
    IDCompositionTarget? _compTarget;
    IDCompositionVisual3? _rootVisual3;
    int _width;
    int _height;

    const Format BackBufferFormat = Format.B8G8R8A8_UNorm;

    public WidgetSurface(RenderDevice device, IntPtr hwnd, int width, int height)
    {
        Hwnd = hwnd;
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);

        var desc = new SwapChainDescription1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = BackBufferFormat,
            Stereo = false,
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            // SampleDescription must be (1,0); zero fails CreateSwapChainForComposition.
            SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipSequential,
            AlphaMode = AlphaMode.Premultiplied,
            Flags = SwapChainFlags.None,
        };

        _swapChain = device.DxgiFactory.CreateSwapChainForComposition(device.D3DDevice, desc, null);

        Context = device.D2DDevice.CreateDeviceContext(DeviceContextOptions.None);
        // Flip-model swap chains expose no IDXGISurface; use GetBuffer(0).
        using (var surface = _swapChain.GetBuffer<IDXGISurface>(0))
            _target = Context.CreateBitmapFromDxgiSurface(surface, null);
        Context.Target = _target;
        Context.AntialiasMode = AntialiasMode.PerPrimitive;
        Context.TextAntialiasMode = TextAntialiasMode.Cleartype;

        _rootVisual3 = null;

        RootVisual = device.CompositionDevice.CreateVisual();
        ContentVisual = device.CompositionDevice.CreateVisual();
        ContentVisual.SetContent(_swapChain);
        RootVisual.AddVisual(ContentVisual, true, null);
        device.CompositionDevice.CreateTargetForHwnd(hwnd, true, out _compTarget);
        _compTarget!.SetRoot(RootVisual);
        _rootVisual3 = RootVisual.QueryInterfaceOrNull<IDCompositionVisual3>();
        device.CompositionDevice.Commit();
    }

    public int Width => _width;
    public int Height => _height;

    /// <summary>
    /// Re-bind the composition target to the widget's current HWND.
    /// IDCompositionTarget is bound to its creation HWND; only the target is rebuilt.
    /// </summary>
    public void RenewTargetForHwnd(RenderDevice device, IntPtr hwnd)
    {
        _compTarget?.Dispose();
        _compTarget = null;

        device.CompositionDevice.CreateTargetForHwnd(hwnd, true, out _compTarget);
        _compTarget!.SetRoot(RootVisual);
        device.CompositionDevice.Commit();
    }

    // ---- deferred resize -------------------------------------------------------------
    // ResizeBuffers requires all back-buffer references released and nothing bound.
    // UI thread only records the wanted size; render thread applies at most one pending
    // resize per frame before BeginDraw, where release-unbind-resize-rebind is atomic
    // with respect to Present. Scaling.Stretch covers the one-frame size mismatch.
    int _pendingW, _pendingH;

    /// <summary>Thread-safe. Records the size the window now wants. Consumed on the render thread.</summary>
    public void RequestResize(int width, int height)
    {
        if (width < 1 || height < 1) return;
        _pendingW = width;
        _pendingH = height;
    }

    /// <summary>
    /// RENDER THREAD ONLY. Applies at most one pending resize, before BeginDraw. Must not be
    /// called while a BeginDraw..EndDraw window is open on this surface.
    /// </summary>
    public void ApplyPendingResize()
    {
        int w = Interlocked.Exchange(ref _pendingW, 0);
        int h = Interlocked.Exchange(ref _pendingH, 0);
        if (w <= 0 || h <= 0) return;
        Resize(w, h);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == _width && height == _height) return;
        _width = width;
        _height = height;

        Context.Target = null;
        _target?.Dispose();
        _target = null;

        _swapChain!.ResizeBuffers(2, (uint)width, (uint)height, BackBufferFormat, SwapChainFlags.None);
        using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
        _target = Context.CreateBitmapFromDxgiSurface(surface, null);
        Context.Target = _target;
    }

    public void SetOpacity(float value) => _rootVisual3?.SetOpacity(value);

    public void SetVisible(bool value) => _rootVisual3?.SetVisible(value);

    public void SetOffset(float x, float y)
    {
        ContentVisual.SetOffsetX(x);
        ContentVisual.SetOffsetY(y);
    }

    public void SetTransform(System.Numerics.Matrix3x2 m) => ContentVisual.SetTransform(m);

    public void Commit(RenderDevice device) => device.CompositionDevice.Commit();

    public void BeginDraw(float dpi)
    {
        Context.SetDpi(dpi, dpi);
        Context.BeginDraw();
    }

    public void EndDrawAndPresent()
    {
        Context.EndDraw();
        _swapChain!.Present(0, PresentFlags.None);
    }

    /// <summary>
    /// Split form of <see cref="EndDrawAndPresent"/>, for capture before Present.
    /// Present advances the back-buffer index, so read the buffer first.
    /// </summary>
    public void EndDrawOnly() => Context.EndDraw();

    public void Present() => _swapChain!.Present(0, PresentFlags.None);

    /// <summary>
    /// Reads the back buffer back to the CPU and writes it as a 24-bit BMP.
    /// Diagnostics only; not on the widget render path.
    /// </summary>
    public unsafe void CaptureToBmp(RenderDevice device, string path)
    {
        // D2D context does not expose ID3D11Device; use the RenderDevice's.
        var d3dDevice = device.D3DDevice;
        var immediate = device.D3DContext;

        var backBuffer = _swapChain!.GetBuffer<IDXGISurface>(0).QueryInterfaceOrNull<ID3D11Texture2D>()
            ?? throw new InvalidOperationException("capture: swap chain buffer is not a D3D11 texture");

        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = BackBufferFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = d3dDevice.CreateTexture2D(in desc);
        immediate.CopyResource(staging, backBuffer);

        var mapped = immediate.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            WriteBmp24(path, (byte*)mapped.DataPointer, mapped.RowPitch, _width, _height);
        }
        finally
        {
            immediate.Unmap(staging, 0);
        }
    }

    /// <summary>
    /// Same readback as <see cref="CaptureToBmp"/>, but returns tightly packed top-down BGRA pixels.
    /// </summary>
    public unsafe byte[] CaptureToPixels(RenderDevice device)
    {
        var d3dDevice = device.D3DDevice;
        var immediate = device.D3DContext;

        var backBuffer = _swapChain!.GetBuffer<IDXGISurface>(0).QueryInterfaceOrNull<ID3D11Texture2D>()
            ?? throw new InvalidOperationException("capture: swap chain buffer is not a D3D11 texture");

        var desc = new Texture2DDescription
        {
            Width = (uint)_width,
            Height = (uint)_height,
            MipLevels = 1,
            ArraySize = 1,
            Format = BackBufferFormat,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        using var staging = d3dDevice.CreateTexture2D(in desc);
        immediate.CopyResource(staging, backBuffer);

        var mapped = immediate.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int stride = _width * 4;
            var packed = new byte[stride * _height];
            for (int y = 0; y < _height; y++)
            {
                fixed (byte* dst = &packed[y * stride])
                {
                    Buffer.MemoryCopy((byte*)mapped.DataPointer + y * (int)mapped.RowPitch, dst, stride, stride);
                }
            }
            return packed;
        }
        finally
        {
            immediate.Unmap(staging, 0);
        }
    }

    /// <summary>Diagnostics write; retries briefly on file sharing conflicts.</summary>
    static void WriteAllBytesWithRetry(string path, byte[] bytes)
    {
        const int Attempts = 12;
        for (int i = 0; i < Attempts; i++)
        {
            try { System.IO.File.WriteAllBytes(path, bytes); return; }
            catch (IOException) when (i < Attempts - 1) { System.Threading.Thread.Sleep(150); }
            catch (UnauthorizedAccessException) when (i < Attempts - 1) { System.Threading.Thread.Sleep(150); }
        }
        System.IO.File.WriteAllBytes(path, bytes);
    }

    /// <summary>Writes a bottom-up 24-bit BMP, converting the BGRA back buffer as it goes.</summary>
    static unsafe void WriteBmp24(string path, byte* src, uint srcPitch, int width, int height)
    {
        int rowSize = (width * 3 + 3) & ~3;      // rows are padded to a 4-byte boundary
        int pixelBytes = rowSize * height;
        int offset = 14 + 40;
        int fileSize = offset + pixelBytes;

        var bytes = new byte[fileSize];
        int p = 0;

        void U16(int v) { bytes[p++] = (byte)v; bytes[p++] = (byte)(v >> 8); }
        void U32(int v) { bytes[p++] = (byte)v; bytes[p++] = (byte)(v >> 8); bytes[p++] = (byte)(v >> 16); bytes[p++] = (byte)(v >> 24); }

        bytes[p++] = (byte)'B'; bytes[p++] = (byte)'M';
        U32(fileSize); U32(0); U32(offset);
        U32(40); U32(width); U32(height);
        U16(1); U16(24); U32(0); U32(pixelBytes); U32(2835); U32(2835); U32(0); U32(0);

        // BMP scanlines run bottom-up; D3D hands back the surface top-down.
        for (int y = height - 1; y >= 0; y--)
        {
            byte* row = src + (long)y * srcPitch;
            for (int x = 0; x < width; x++)
            {
                bytes[p++] = row[x * 4 + 0];   // B
                bytes[p++] = row[x * 4 + 1];   // G
                bytes[p++] = row[x * 4 + 2];   // R
            }
            for (int pad = rowSize - width * 3; pad > 0; pad--) bytes[p++] = 0;
        }

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        WriteAllBytesWithRetry(path, bytes);
    }

    public void Dispose()
    {
        // Teardown order: unbind target, release back-buffer bitmap, then context,
        // swap chain, composition target, visuals. Each step is individually guarded.
        try { Context.Target = null; } catch { /* device may already be gone */ }
        try { _target?.Dispose(); } catch { }
        _target = null;
        try { Context.Dispose(); } catch { }
        try { _compTarget?.Dispose(); } catch { }
        _compTarget = null;
        try { _swapChain?.Dispose(); } catch { }
        _swapChain = null;
        try { _rootVisual3?.Dispose(); } catch { }
        try { ContentVisual.Dispose(); } catch { }
        try { RootVisual.Dispose(); } catch { }
    }
}

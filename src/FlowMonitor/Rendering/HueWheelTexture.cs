using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3DCompiler = Vortice.D3DCompiler;

namespace FlowMonitor.Rendering;

/// <summary>
/// The colour wheel, drawn by a pixel shader into a texture and handed to Direct2D as
/// an <see cref="ID2D1Bitmap1"/>. D2D has no polar gradient and a per-pixel C# loop
/// gives the rim a hard staircase, so the shader computes the hue from the angle and
/// antialiases the rim with the screen-space derivative of the radius. The vertex stage
/// comes from SV_VertexID, so there is no vertex buffer and no input layout.
/// </summary>
public static class HueWheelTexture
{
    const string Common = @"
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

// HLSL will not index a constant array with a runtime value, so the two triangles
// are chosen with arithmetic from the vertex id: a shared corner plus its two
// neighbours, mirrored for the second triangle.
float2 Corner(uint id)
{
    float2 p = id < 3u ? float2(-1, -1) : float2(1, 1);
    uint i = id % 3u;
    if (i == 0u) return p;
    if (i == 1u) return float2(-p.x, p.y);
    return float2(-p.y, -p.x);
}

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 c = Corner(id);
    o.pos = float4(c, 0, 1);
    o.uv = c * 0.5 + 0.5;
    return o;
}

float3 Hsv2Rgb(float h, float s, float v)
{
    float3 k = float3(1.0, 2.0 / 3.0, 1.0 / 3.0);
    float3 p = abs(frac(float3(h, h, h) + k) * 6.0 - 3.0);
    return v * lerp(float3(1.0, 1.0, 1.0), clamp(p - 1.0, 0.0, 1.0), s);
}

float3 ToLinear(float3 c)
{
    // HLSL has no `select`; step gives the per-channel branch as a 0/1 weight.
    return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, 0.04045));
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * 2.0;
    float r = length(p);
    // Red at the bottom, hue running clockwise, matching ColorPickerLayout.HsFromPoint.
    // Bitmap rows run DOWN from the centre while clip-space y runs UP, so p.y is
    // already the negation the CPU loop spells as -dy: atan2(p.x, p.y) here equals
    // atan2(dx, -dy) there, and the +0.5 is HsFromPoint's +180 degrees.
    float h = atan2(p.x, p.y) / 6.2831853 + 0.5;
    h = h - floor(h);
    float3 rgb = Hsv2Rgb(h, clamp(r, 0.0, 1.0), 1.0);
#ifdef LINEAR_SPACE
    rgb = ToLinear(rgb);
#endif
    // Premultiplied, so the antialiased rim blends with whatever is behind the wheel.
    float a = 1.0 - smoothstep(1.0 - fwidth(r), 1.0, r);
    return float4(rgb * a, a);
}
";
    /// <summary>True when the last successful Build came from the shader, not the fallback.</summary>
    public static bool LastUsedShader { get; private set; }

    /// <summary>
    /// Renders an n-by-n wheel. Returns null when the shader will not build, so the
    /// caller keeps a CPU fallback rather than losing the control.
    /// </summary>
    public static ID2D1Bitmap1? Build(RenderDevice dev, ID2D1DeviceContext dc, int n, bool linear)
    {
        LastUsedShader = false;
        var d3d = dev.D3DDevice;
        var ctx = dev.D3DContext;
        string source = (linear ? "#define LINEAR_SPACE 1\n" : "") + Common;
        byte[]? vsCode = Compile(source, "VSMain", "vs_4_0");
        byte[]? psCode = Compile(source, "PSMain", "ps_4_0");
        if (vsCode is null || psCode is null) return null;

        try
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)n,
                Height = (uint)n,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            using var tex = d3d.CreateTexture2D(desc);
            using var vs = d3d.CreateVertexShader(vsCode, null);
            using var ps = d3d.CreatePixelShader(psCode, null);
            using var rtv = d3d.CreateRenderTargetView(tex, null);
            if (rtv is null) { Log.Warn("hue wheel: no render target view"); return null; }
            // The default rasteriser culls back faces, and both full-screen triangles
            // are counter-clockwise, so the default state discards the whole draw.
            using var rs = d3d.CreateRasterizerState(new RasterizerDescription
            {
                FillMode = Vortice.Direct3D11.FillMode.Solid,
                CullMode = CullMode.None,
            });

            ctx.OMSetRenderTargets(1, [rtv], null);
            ctx.RSSetViewports([new Vortice.Mathematics.Viewport(0f, 0f, n, n, 0f, 1f)]);
            // D2D leaves its own rasteriser, blend and depth state behind; the default
            // state is what a full-screen triangle needs, and culling it is what made
            // the wheel render black.
            ctx.RSSetState(rs);
            ctx.OMSetBlendState(null);
            ctx.OMSetDepthStencilState(null, 0);
            ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
            ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            ctx.IASetInputLayout(null);
            ctx.VSSetShader(vs);
            ctx.PSSetShader(ps);
            ctx.Draw(6, 0);
            // The wheel is drawn by the GPU; only the hand-off to D2D is a staged
            // readback (CopyResource plus a CPU map into a D2D bitmap), which happens
            // once per (size, working space). D2D rejects a surface rendered on a
            // foreign device, so sharing the texture directly is not an option.
            using var staging = d3d.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)n,
                Height = (uint)n,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            ctx.CopyResource(staging, tex);
            ctx.Flush();
            ctx.VSSetShader(null);
            ctx.PSSetShader(null);
            ctx.RSSetState(null);
            var box = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                ID2D1Bitmap1? bmp = dc.CreateBitmap(
                    new Vortice.Mathematics.SizeI(n, n), box.DataPointer, (uint)box.RowPitch,
                    new Vortice.Direct2D1.BitmapProperties1
                    {
                        PixelFormat = new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Premultiplied),
                        DpiX = 96,
                        DpiY = 96,
                    });
                LastUsedShader = bmp is not null;
                return bmp;
            }
            finally { ctx.Unmap(staging, 0); }
        }
        catch (Exception ex)
        {
            Log.Warn($"hue wheel: render failed ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>Compiled bytecode for one shader, or null when the source will not build.</summary>
    static byte[]? Compile(string src, string entry, string target)
    {
        try
        {
            var code = D3DCompiler.Compiler.Compile(src, entry, "wheel.hlsl", target,
                D3DCompiler.ShaderFlags.None, D3DCompiler.EffectFlags.None);
            var bytes = new byte[code.Length];
            code.Span.CopyTo(bytes);
            return bytes.Length == 0 ? null : bytes;
        }
        catch (Exception ex)
        {
            Log.Warn($"hue wheel: {entry} ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}

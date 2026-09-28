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

static const float2 Verts[6] = {
    float2(-1, -1), float2(1, -1), float2(1, 1),
    float2(-1, -1), float2(1, 1), float2(-1, 1) };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.pos = float4(Verts[id], 0, 1);
    o.uv = Verts[id] * 0.5 + 0.5;
    return o;
}

float3 Hsv2Rgb(float h, float s, float v)
{
    float3 k = float3(1.0, 2.0 / 3.0, 1.0 / 3.0);
    float3 p = abs(frac(float3(h) + k) * 6.0 - 3.0);
    return v * mix(float3(1.0), clamp(p - 1.0, 0.0, 1.0), s);
}

float3 ToLinear(float3 c)
{
    return select(c / 12.92, pow((c + 0.055) / 1.055, 2.4), c > 0.04045);
}

float4 PSMain(VSOut i) : SV_Target
{
    float2 p = (i.uv - 0.5) * 2.0;
    float r = length(p);
    float h = atan2(p.x, -p.y) / 6.2831853;
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

    /// <summary>
    /// Renders an n-by-n wheel. Returns null when the shader will not build, so the
    /// caller keeps a CPU fallback rather than losing the control.
    /// </summary>
    public static ID2D1Bitmap1? Build(RenderDevice dev, ID2D1DeviceContext dc, int n, bool linear)
    {
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
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            };
            using var tex = dev.D3DDevice.CreateTexture2D(desc);
            using var vs = dev.D3DDevice.CreateVertexShader(vsCode, null);
            using var ps = dev.D3DDevice.CreatePixelShader(psCode, null);
            using var rtv = tex.QueryInterface<ID3D11RenderTargetView>();
            if (rtv is null) return null;

            dev.D3DContext.OMSetRenderTargets(1, [rtv], null);
            dev.D3DContext.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
            dev.D3DContext.VSSetShader(vs);
            dev.D3DContext.PSSetShader(ps);
            dev.D3DContext.Draw(6, 0);

            using var surface = tex.QueryInterface<IDXGISurface>();
            if (surface is null) return null;
            return dc.CreateBitmapFromDxgiSurface(surface, null);
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

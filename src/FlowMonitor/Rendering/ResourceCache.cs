using System.Collections.Concurrent;
using SizeF = System.Drawing.SizeF;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Color4 = Vortice.Mathematics.Color4;

namespace FlowMonitor.Rendering;

/// <summary>Process-wide cache of Direct2D brushes, stroke styles and DirectWrite formats.</summary>
public sealed class ResourceCache : IDisposable
{
    readonly RenderDevice _device;
    readonly ConcurrentDictionary<uint, ID2D1SolidColorBrush> _brushes = new();
    readonly ConcurrentDictionary<string, IDWriteTextFormat> _formats = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, SizeF> _measureCache = new(StringComparer.Ordinal);

    public IDWriteTextFormat Title { get; }
    public IDWriteTextFormat HeaderValue { get; }
    public IDWriteTextFormat Axis { get; }
    public IDWriteTextFormat AxisRight { get; }
    public IDWriteTextFormat Micro { get; }
    public IDWriteTextFormat Body { get; }
    public IDWriteTextFormat Bold { get; }

    public ID2D1StrokeStyle RoundStroke { get; }

    /// <summary>Factory for geometries callers build outside the cached set.</summary>
    public ID2D1Factory D2DFactory => _device.D2DFactory;
    public ID2D1StrokeStyle Hairline { get; }

    /// <summary>Dashed companion lines, e.g. a committed-bytes line over a filled area.</summary>
    public ID2D1StrokeStyle DashStroke { get; }

    public ResourceCache(RenderDevice device)
    {
        _device = device;

        Title = device.CreateTextFormat("Segoe UI Variable Text", 12.5f, FontWeight.SemiBold);
        HeaderValue = device.CreateTextFormat("Segoe UI Variable Display", 21f, FontWeight.SemiBold,
            hAlign: TextAlignment.Trailing);
        Axis = device.CreateTextFormat("Segoe UI Variable Text", 9.5f, FontWeight.Normal);
        // Axis labels sit in the left gutter, flush to the plot edge.
        AxisRight = device.CreateTextFormat("Segoe UI Variable Text", 9.5f, FontWeight.Normal,
            hAlign: TextAlignment.Trailing);
        Micro = device.CreateTextFormat("Segoe UI Variable Text", 9.5f, FontWeight.Normal);
        Body = device.CreateTextFormat("Segoe UI Variable Text", 12.5f, FontWeight.Normal);
        Bold = device.CreateTextFormat("Segoe UI Variable Text", 12.5f, FontWeight.Bold);

        // D2D only accepts a dashes array when DashStyle is Custom; otherwise throws (0x80070057).
        RoundStroke = device.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
            MiterLimit = 2f,
            DashStyle = DashStyle.Solid,
            TransformType = StrokeTransformType.Fixed,
        });

        Hairline = device.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            LineJoin = LineJoin.Miter,
            TransformType = StrokeTransformType.Fixed,
        });

        DashStroke = device.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties1
        {
            StartCap = CapStyle.Flat,
            EndCap = CapStyle.Flat,
            DashCap = CapStyle.Square,
            LineJoin = LineJoin.Round,
            MiterLimit = 2f,
            DashStyle = DashStyle.Dash,
            TransformType = StrokeTransformType.Fixed,
        });
    }

    public ID2D1SolidColorBrush Brush(Color4 c)
    {
        uint key = (uint)(c.R * 255) << 24 | (uint)(c.G * 255) << 16 | (uint)(c.B * 255) << 8 | (uint)(c.A * 255);
        return _brushes.GetOrAdd(key, _ => _device.ResourceContext.CreateSolidColorBrush(c));
    }

    public ID2D1SolidColorBrush Brush(uint argb) => Brush(new Color4(
        ((argb >> 16) & 0xFF) / 255f,
        ((argb >> 8) & 0xFF) / 255f,
        (argb & 0xFF) / 255f,
        ((argb >> 24) & 0xFF) / 255f));

    public IDWriteTextFormat Format(string family, float size, FontWeight weight)
    {
        string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)weight;
        return _formats.GetOrAdd(key, _ => _device.CreateTextFormat(family, size, weight));
    }

    public SizeF Measure(string text, IDWriteTextFormat fmt, float scale)
    {
        string key = text + "|" + (uint)fmt.NativePointer + "|" + scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return _measureCache.GetOrAdd(key, _ =>
        {
            if (text.Length == 0) return SizeF.Empty;
            using var layout = _device.DWriteFactory.CreateTextLayout(text, fmt, 8192f, 8192f);
            var m = layout.Metrics;
            return new SizeF(m.WidthIncludingTrailingWhitespace * scale, m.Height * scale);
        });
    }

    public void Dispose()
    {
        foreach (var b in _brushes.Values) b.Dispose();
        _brushes.Clear();
        foreach (var f in _formats.Values) f.Dispose();
        _formats.Clear();
        _measureCache.Clear();
        RoundStroke.Dispose();
        DashStroke.Dispose();
        Hairline.Dispose();
    }
}

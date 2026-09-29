using System.Text.Json.Serialization;

namespace FlowMonitor.Model;

public enum GraphKind
{
    Cpu,
    CpuCores,
    Memory,
    Disk,
    Network,
    Gpu,
    GpuCores,
    Fans,
    Vram,
}

/// <summary>
/// Mouse behaviour of a locked widget. Window stays hit-testable so right-click
/// re-enters edit mode; left button is forwarded to the window underneath.
/// </summary>
public enum ClickThroughMode
{
    /// <summary>Swallow everything. Only sensible while dragging.</summary>
    Off = 0,

    /// <summary>Left button and drag fall through; right button still reaches the widget.</summary>
    LeftClickOnly = 1,
}

/// <summary>Per-widget persistent state. Everything the context menu can change lives here.</summary>
public sealed class WidgetConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    // geometry (virtual-screen coordinates)
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 420;
    public int Height { get; set; } = 180;

    // content
    public GraphKind Graph { get; set; } = GraphKind.Cpu;

    // appearance
    public int Transparency { get; set; } = 12;      // 0 = opaque, 100 = invisible
    public string AccentHex { get; set; } = "#4CC2FF";

    // Per-kind line colours (spec: accent + individual graph line colours).
    // The Cores variants fall back to their base kind inside LineColorFor.
    public string LineColorCpu { get; set; } = "#4CC2FF";
    public string LineColorMemory { get; set; } = "#6CCB5F";
    public string LineColorDisk { get; set; } = "#FFB44C";
    public string LineColorNetwork { get; set; } = "#4CDAC2";
    public string LineColorGpu { get; set; } = "#B07CFF";
    public string LineColorFans { get; set; } = "#FF7A9E";

    // Curve stroke width in logical px. 1.0 matches Mission Center's Stroke::new(1.)
    // (vendor/src/render.rs:49); thicker re-sharpens the sub-pixel slot zigzag.
    public float LineThickness { get; set; } = 1.0f;
    // Fill is flat base color at this alpha. 0.39 matches Mission Center's default
    // fill opacity 100./255. (graph-widget core/src/dataset.rs); MC has no gradient fade.
    public float FillOpacity { get; set; } = 0.39f;
    public bool ShowGrid { get; set; } = true;
    // Smoothing: false = straight segments, true = monotone cubic through every sample.
    // Default true: Mission Center ships performance-smooth-graphs default FALSE as a
    // low-end/performance caution, but the flowing MC look is the toggles-ON look, which
    // is what this widget sells. Honoured by the renderer.
    public bool Smoothing { get; set; } = true;
    // Sliding: the curve drifts left continuously between sample ticks instead of
    // stepping once per tick. Default true for the same reason as Smoothing: MC's
    // performance-sliding-graphs default FALSE is caution, the ON look is the product.
    // Honoured by the renderer.
    public bool SlidingGraphs { get; set; } = true;
    public bool ShowLabels { get; set; } = true;
    public bool ShowUnits { get; set; } = true;
    public bool ShowMinMax { get; set; } = false;
    public int CornerRadius { get; set; } = 8;

    // behaviour
    public int UpdateIntervalMs { get; set; } = 1000;
    public float IdleOpacity { get; set; } = 1.0f;
    public float HoverOpacity { get; set; } = 1.0f;
    // How many points are spread across the plot width. 60 matches Mission Center's
    // performance-page-data-points default (range 10-600: "How many points should be
    // displayed on each chart?"). The ring always buffers 600 (MC MAX_DATA_POINTS).
    public int DataPoints { get; set; } = 60;
    // Stats window (min/avg/max) in seconds. The plot shows the newest DataPoints
    // samples; this no longer controls how many slots are drawn.
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// How the widget behaves toward mouse input while it is locked (not in edit mode).
    /// See <see cref="ClickThroughMode"/> for why this exists at all.
    /// </summary>
    public ClickThroughMode ClickThrough { get; set; } = ClickThroughMode.LeftClickOnly;

    /// <summary>Set by the widget at runtime; never persisted.</summary>
    [JsonIgnore] public float Dpi { get; set; } = 1f;

    public WidgetConfig Clone() => (WidgetConfig)MemberwiseClone();
}

/// <summary>Colour resolution for a widget's graph kind.</summary>
public static class WidgetConfigColors
{
    /// <summary>
    /// Per-kind line colour with cores-fallback. Never throws; falls back to <see cref="AccentHex"/>.
    /// </summary>
    public static string LineColorFor(this WidgetConfig c, GraphKind kind) => kind switch
    {
        GraphKind.Cpu or GraphKind.CpuCores => c.LineColorCpu,
        GraphKind.Memory => c.LineColorMemory,
        GraphKind.Disk => c.LineColorDisk,
        GraphKind.Network => c.LineColorNetwork,
        GraphKind.Gpu or GraphKind.GpuCores or GraphKind.Vram => c.LineColorGpu,
        GraphKind.Fans => c.LineColorFans,
        _ => c.AccentHex,
    };

    /// <summary>
    /// Writes the picker hex back onto the per-kind slot, normalised to #RRGGBB so
    /// LineColorFor and LineColorFor(kind) can never disagree.
    /// </summary>
    public static void SetLineColor(this WidgetConfig c, GraphKind kind, string hex)
    {
        var rgb = Widgets.Rgba.FromHex(hex).ToHex();
        switch (kind)
        {
            case GraphKind.Cpu:
            case GraphKind.CpuCores: c.LineColorCpu = rgb; break;
            case GraphKind.Memory: c.LineColorMemory = rgb; break;
            case GraphKind.Disk: c.LineColorDisk = rgb; break;
            case GraphKind.Network: c.LineColorNetwork = rgb; break;
            case GraphKind.Gpu:
            case GraphKind.GpuCores:
            case GraphKind.Vram: c.LineColorGpu = rgb; break;
            case GraphKind.Fans: c.LineColorFans = rgb; break;
            default: c.AccentHex = rgb; break;
        }
    }
}

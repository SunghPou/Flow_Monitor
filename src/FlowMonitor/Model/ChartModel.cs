using FlowMonitor.Graphs;
using Vortice.Mathematics;
using Color4 = Vortice.Mathematics.Color4;

namespace FlowMonitor.Model;

/// <summary>One plotted line inside a chart.</summary>
public sealed class ChartSeries
{
    public required string Name { get; init; }
    public required TimeSeries Data { get; init; }
    /// <summary>
    /// Immutable newest-first values taken under the telemetry lock (NaN = gap).
    /// The renderer reads only this, never the live ring.
    /// </summary>
    public float[] Snapshot { get; set; } = Array.Empty<float>();
    public required Color4 Color { get; set; }
    public string Unit { get; init; } = "";
    /// <summary>Draw the gradient area under the line.</summary>
    public bool Fill { get; init; } = true;
    /// <summary>Secondary series (no fill, thinner) such as 'cached' or 'send'.</summary>
    public bool Secondary { get; init; }
    /// <summary>
    /// Dashed stroke, so a companion line never reads as a second border of the filled
    /// area it runs along (MC draws its committed dataset dashed).
    /// </summary>
    public bool Dashed { get; init; }
}

/// <summary>Everything the renderer needs to draw one chart for one frame.</summary>
public sealed class ChartModel
{
    public required string Title { get; init; }
    public required List<ChartSeries> Series { get; init; }

    /// <summary>Upper bound of the value axis. Use <see cref="AutoRange"/> for dynamic scaling.</summary>
    public double AxisMax { get; init; } = 100.0;
    public bool AutoRange { get; init; }
    public bool ShowAxisLabels { get; init; } = true;
    public bool PercentAxis { get; init; } = true;

    /// <summary>Formatted value shown in the header.</summary>
    public string ValueText { get; init; } = "";
    public string ValueUnit { get; init; } = "";
    public string MinMaxText { get; init; } = "";
    /// <summary>Reason shown dim in the plot centre when there is nothing to plot.</summary>
    public string EmptyText { get; init; } = "";

    public double WindowSeconds { get; init; } = 60.0;

    /// <summary>
    /// Ring capacity (MC MAX_DATA_POINTS parity). The renderer draws the newest
    /// <see cref="VisiblePoints"/> of these.
    /// </summary>
    public const int FixedSlots = 600;

    /// <summary>
    /// How many points are spread across the plot width. 60 matches Mission Center's
    /// performance-page-data-points default (range 10-600).
    /// </summary>
    public int VisiblePoints { get; set; } = 60;

    /// <summary>
    /// Newest sample's clock time and the nominal seconds between samples. The sliding
    /// offset is (now - LastSampleTime) / SampleIntervalSec, clamped to 0..1.
    /// </summary>
    public double LastSampleTime { get; set; }
    public double SampleIntervalSec { get; set; } = 1.0;

    /// <summary>
    /// Edit chrome (badges, metric chevrons) is visible, so the header follows the
    /// edit branch of HeaderLayout. Binary with the mode; only opacity animates.
    /// </summary>
    public bool EditChrome { get; set; }

    /// <summary>Series are stacked bands (Task Manager's memory graph) rather than
    /// independent curves sharing one axis.</summary>
    public bool Stacked { get; init; }
}

using FileWatcherApp.Services.DXFAnalysis.Geometry;
using FileWatcherApp.Services.DXFAnalysis.Models;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Holds data derived from a DXF document that downstream steps reuse.
/// </summary>
public sealed class DXFAnalysisGeometrySnapshot
{
    /// <summary>Gets or sets the metrics extracted by the analyzer.</summary>
    public required DXFMetrics Metrics { get; init; }

    /// <summary>Gets or sets a defensive copy of the line segments for rendering and intersections.</summary>
    public required IReadOnlyList<Segment2D> Segments { get; init; }

    /// <summary>Gets or sets the mapping from layer name to semantic type.</summary>
    public required IReadOnlyDictionary<string, string> LayerSemanticTypes { get; init; }

    /// <summary>Gets or sets the unit scaling factor (document units to millimeters).</summary>
    public required double UnitToMillimeter { get; init; }
}

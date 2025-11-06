using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using netDxf.Units;
using FileWatcherApp.Services.DXFAnalysis.Geometry;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Vector3 = netDxf.Vector3;
using DxfVector2 = netDxf.Vector2;
using NumericsVector2 = System.Numerics.Vector2;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Extracts deterministic geometric metrics from DXF entities.
/// </summary>
public sealed class DXFAnalyzer
{
    private readonly ILogger<DXFAnalyzer> _logger;
    private readonly DXFAnalysisOptions _options;
    private readonly Lazy<Dictionary<string, List<System.Text.RegularExpressions.Regex>>> _layerRegexLookup;
    private readonly Lazy<Dictionary<string, List<DXFAnalysisOptions.SerrilhaSymbolMatcher>>> _serrilhaSymbolLookup;
    private readonly Lazy<Dictionary<string, List<DXFAnalysisOptions.SerrilhaTextSymbolMatcher>>> _serrilhaTextLookup;
    private readonly Lazy<Dictionary<string, List<Regex>>> _specialMaterialRegexLookup;

    private static readonly string[] SerrilhaMistaKeywords = { "mista", "mixta" };
    private static readonly string[] SerrilhaZipperKeywords = { "zip", "ziper", "zipper" };
    private static readonly string[] SerrilhaTravadaKeywords =
    {
        "trav", "trava", "travada",
        "ranh", "ranhura", "ranhuras",
        "selcola", "sel cola", "sel-cola", "sel_col", "selagem", "selado"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DXFAnalyzer"/> class.
    /// </summary>
    public DXFAnalyzer(IOptions<DXFAnalysisOptions> options, ILogger<DXFAnalyzer> logger)
    {
        _logger = logger;
        _options = options.Value;
        _layerRegexLookup = new Lazy<Dictionary<string, List<System.Text.RegularExpressions.Regex>>>(_options.BuildLayerRegexLookup);
        _serrilhaSymbolLookup = new Lazy<Dictionary<string, List<DXFAnalysisOptions.SerrilhaSymbolMatcher>>>(_options.BuildSerrilhaSymbolLookup);
        _serrilhaTextLookup = new Lazy<Dictionary<string, List<DXFAnalysisOptions.SerrilhaTextSymbolMatcher>>>(_options.BuildSerrilhaTextLookup);
        _specialMaterialRegexLookup = new Lazy<Dictionary<string, List<Regex>>>(_options.BuildSpecialMaterialRegexLookup);
    }

    /// <summary>
    /// Computes metrics and auxiliary geometry from the supplied DXF document.
    /// </summary>
    public DXFAnalysisGeometrySnapshot Analyze(
        DxfDocument document,
        DXFQualityMetrics quality,
        CancellationToken cancellationToken = default)
    {
        var unitInfo = ResolveUnits(document);
        var metrics = new DXFMetrics
        {
            Unit = unitInfo.UnitName,
            Quality = quality
        };

        var segments = new List<Segment2D>();
        var layerAccumulators = new Dictionary<string, LayerAccumulator>(StringComparer.OrdinalIgnoreCase);

        EvaluateLines(document, metrics, segments, layerAccumulators, unitInfo, cancellationToken);
        EvaluateArcs(document, metrics, segments, layerAccumulators, unitInfo, cancellationToken);
        EvaluateCircles(document, metrics, segments, layerAccumulators, unitInfo, cancellationToken);
        EvaluatePolylines(document, metrics, segments, layerAccumulators, unitInfo, cancellationToken);
        EvaluateSplines(document, metrics, segments, layerAccumulators, unitInfo, cancellationToken);

        var serrilhaSummary = AnalyzeSerrilhaSymbols(document, unitInfo.ToMillimeters, cancellationToken);
        if (serrilhaSummary is not null)
        {
            ApplyCorteSecoHeuristic(serrilhaSummary, segments, layerAccumulators, cancellationToken);
            EnrichSerrilhaSummary(serrilhaSummary);
            metrics.Serrilha = serrilhaSummary;
        }

        metrics.LayerStats = layerAccumulators.Values
            .OrderByDescending(l => l.TotalLength)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(acc => new DXFLayerStats
            {
                Name = acc.Name,
                Type = acc.Type,
                EntityCount = acc.Count,
                TotalLength = acc.TotalLength
            })
            .ToList();

        metrics.ThreePtCutRatio = metrics.TotalCutLength > 0
            ? metrics.TotalThreePtLength / metrics.TotalCutLength
            : 0.0;
        metrics.RequiresManualThreePtHandling = metrics.TotalThreePtLength > 0.0;
        if (metrics.RequiresManualThreePtHandling)
        {
            metrics.Quality.Notes ??= new List<string>();
            if (!metrics.Quality.Notes.Any(n => n.Contains("Vinco 3pt", StringComparison.OrdinalIgnoreCase)))
            {
                metrics.Quality.Notes.Add("Vinco 3pt identificado: exige dobra manual.");
            }
        }

        var totalLinearLength = layerAccumulators.Values.Sum(static acc => acc.TotalLength);
        metrics.Quality.DelicateArcDensity = totalLinearLength > 0.0
            ? metrics.Quality.DelicateArcLength / totalLinearLength
            : 0.0;

        ComputeExtents(document, segments, metrics, unitInfo.ToMillimeters);

        EstimateClosedLoopsFromSegments(metrics, segments, layerAccumulators);
        AnnotateSpecialMaterials(metrics, layerAccumulators);

        metrics.BboxArea = (metrics.Extents.MaxX - metrics.Extents.MinX) * (metrics.Extents.MaxY - metrics.Extents.MinY);
        var width = metrics.Extents.MaxX - metrics.Extents.MinX;
        var height = metrics.Extents.MaxY - metrics.Extents.MinY;
        metrics.BboxPerimeter = 2 * (width + height);

        if (metrics.BboxArea > 0)
        {
            metrics.Quality.ClosedLoopDensity = metrics.Quality.ClosedLoops / metrics.BboxArea;
        }

        metrics.NumIntersections = DetectIntersections(segments, cancellationToken);

        return new DXFAnalysisGeometrySnapshot
        {
            Metrics = metrics,
            Segments = segments,
            LayerSemanticTypes = layerAccumulators.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Type, StringComparer.OrdinalIgnoreCase),
            UnitToMillimeter = unitInfo.ToMillimeters
        };
    }

    private void EstimateClosedLoopsFromSegments(
        DXFMetrics metrics,
        IReadOnlyList<Segment2D> segments,
        IReadOnlyDictionary<string, LayerAccumulator> layerAccumulators)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var snapTolerance = Math.Max(0.2, Math.Max(_options.GapTolerance, 1e-3));
        var nodeLookup = new Dictionary<(long X, long Y), int>(segments.Count * 2);
        var nodes = new List<LoopNode>(segments.Count * 2);
        var edges = new List<LoopEdge>(segments.Count);

        int GetOrAddNode(Point2D point)
        {
            var key = Quantize(point, snapTolerance);
            if (!nodeLookup.TryGetValue(key, out var index))
            {
                index = nodes.Count;
                nodeLookup[key] = index;
                nodes.Add(new LoopNode(point));
            }
            return index;
        }

        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            var a = GetOrAddNode(seg.Start);
            var b = GetOrAddNode(seg.End);
            if (a == b)
            {
                continue;
            }

            var edgeIndex = edges.Count;
            edges.Add(new LoopEdge(a, b, seg.Layer ?? "default"));
            nodes[a].Edges.Add(edgeIndex);
            nodes[b].Edges.Add(edgeIndex);
        }

        if (edges.Count == 0)
        {
            return;
        }

        var visitedEdges = new bool[edges.Count];
        var componentNodes = new HashSet<int>();
        var queue = new Queue<int>();
        var loopsByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < edges.Count; i++)
        {
            if (visitedEdges[i])
            {
                continue;
            }

            componentNodes.Clear();
            queue.Clear();
            queue.Enqueue(i);

            var componentEdges = new List<int>();

            while (queue.Count > 0)
            {
                var edgeIdx = queue.Dequeue();
                if (visitedEdges[edgeIdx])
                {
                    continue;
                }

                visitedEdges[edgeIdx] = true;
                componentEdges.Add(edgeIdx);

                var edge = edges[edgeIdx];
                componentNodes.Add(edge.NodeA);
                componentNodes.Add(edge.NodeB);

                foreach (var neighborEdge in nodes[edge.NodeA].Edges)
                {
                    if (!visitedEdges[neighborEdge])
                    {
                        queue.Enqueue(neighborEdge);
                    }
                }

                foreach (var neighborEdge in nodes[edge.NodeB].Edges)
                {
                    if (!visitedEdges[neighborEdge])
                    {
                        queue.Enqueue(neighborEdge);
                    }
                }
            }

            if (componentEdges.Count < 3 || componentNodes.Count < 3)
            {
                continue;
            }

            var isLoop = true;
            foreach (var nodeIdx in componentNodes)
            {
                var degree = nodes[nodeIdx].Edges.Count;
                if (degree != 2)
                {
                    isLoop = false;
                    break;
                }
            }

            if (!isLoop)
            {
                continue;
            }

            var layer = edges[componentEdges[0]].Layer ?? "default";
            loopsByLayer.TryGetValue(layer, out var count);
            loopsByLayer[layer] = count + 1;
        }

        if (loopsByLayer.Count == 0)
        {
            return;
        }

        var totalLoops = loopsByLayer.Values.Sum();
        if (totalLoops <= 0)
        {
            return;
        }

        if (totalLoops > metrics.Quality.ClosedLoops)
        {
            metrics.Quality.ClosedLoops = totalLoops;
            metrics.Quality.ClosedLoopsByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in loopsByLayer)
            {
                var layerName = kvp.Key;
                var type = layerAccumulators.TryGetValue(layerName, out var layer)
                    ? (string.IsNullOrWhiteSpace(layer.Type) ? "unknown" : layer.Type)
                    : (string.IsNullOrWhiteSpace(layerName) ? "unknown" : ResolveLayerType(layerName));
                metrics.Quality.ClosedLoopsByType.TryGetValue(type, out var existing);
                metrics.Quality.ClosedLoopsByType[type] = existing + kvp.Value;
            }
        }

        metrics.Quality.Notes ??= new List<string>();
        var summaryNote = $"Loops estimados: {totalLoops}";
        if (!metrics.Quality.Notes.Any(n => string.Equals(n, summaryNote, StringComparison.OrdinalIgnoreCase)))
        {
            metrics.Quality.Notes.Add(summaryNote);
        }
    }

    private static (long X, long Y) Quantize(in Point2D point, double tolerance)
    {
        var inv = 1.0 / tolerance;
        var qx = (long)Math.Round(point.X * inv);
        var qy = (long)Math.Round(point.Y * inv);
        return (qx, qy);
    }

    private sealed class LoopNode
    {
        public LoopNode(Point2D position)
        {
            Position = position;
        }

        public Point2D Position { get; }
        public List<int> Edges { get; } = new();
    }

    private readonly struct LoopEdge
    {
        public LoopEdge(int nodeA, int nodeB, string layer)
        {
            NodeA = nodeA;
            NodeB = nodeB;
            Layer = layer;
        }

        public int NodeA { get; }
        public int NodeB { get; }
        public string Layer { get; }
    }

    private (double ToMillimeters, string UnitName) ResolveUnits(DxfDocument document)
    {
        var insUnits = document.DrawingVariables?.InsUnits ?? DrawingUnits.Unitless;
        if (insUnits == DrawingUnits.Unitless)
        {
            return (ParseDefaultUnitToMillimeters(_options.DefaultUnit), NormalizeUnitName(_options.DefaultUnit));
        }

        if (TryGetUnitFactor(insUnits, out var factor, out var name))
        {
            return (factor, name);
        }

        _logger.LogWarning("Unidade DXF desconhecida {Units}; usando default {Default}", insUnits, _options.DefaultUnit);
        return (ParseDefaultUnitToMillimeters(_options.DefaultUnit), NormalizeUnitName(_options.DefaultUnit));
    }

    private static string NormalizeUnitName(string unit)
    {
        return unit.ToLowerInvariant() switch
        {
            "mm" or "millimeter" or "millimeters" => "mm",
            "cm" or "centimeter" or "centimeters" => "cm",
            "m" or "meter" or "meters" => "m",
            "in" or "inch" or "inches" => "in",
            "ft" or "feet" or "foot" => "ft",
            _ => unit.ToLowerInvariant()
        };
    }

    private static bool TryGetUnitFactor(DrawingUnits units, out double factor, out string name)
    {
        factor = units switch
        {
            DrawingUnits.Millimeters => 1.0,
            DrawingUnits.Centimeters => 10.0,
            DrawingUnits.Meters => 1000.0,
            DrawingUnits.Inches => 25.4,
            DrawingUnits.Feet => 304.8,
            DrawingUnits.Microinches => 25.4 / 1000.0,
            DrawingUnits.Mils => 25.4 / 1000.0,
            DrawingUnits.Decimeters => 100.0,
            DrawingUnits.Decameters => 10000.0,
            DrawingUnits.Hectometers => 100000.0,
            DrawingUnits.Kilometers => 1000000.0,
            DrawingUnits.Microns => 0.001,
            DrawingUnits.Nanometers => 1e-6,
            DrawingUnits.Yards => 914.4,
            DrawingUnits.Miles => 1609344.0,
            _ => double.NaN
        };

        name = units switch
        {
            DrawingUnits.Millimeters => "mm",
            DrawingUnits.Centimeters => "cm",
            DrawingUnits.Meters => "m",
            DrawingUnits.Inches => "in",
            DrawingUnits.Feet => "ft",
            DrawingUnits.Yards => "yd",
            DrawingUnits.Miles => "mi",
            DrawingUnits.Decimeters => "dm",
            DrawingUnits.Decameters => "dam",
            DrawingUnits.Hectometers => "hm",
            DrawingUnits.Kilometers => "km",
            DrawingUnits.Microns => "µm",
            DrawingUnits.Nanometers => "nm",
            DrawingUnits.Microinches => "µin",
            DrawingUnits.Mils => "mil",
            _ => "unitless"
        };

        return !double.IsNaN(factor);
    }

    private double ParseDefaultUnitToMillimeters(string unit)
    {
        return unit.ToLowerInvariant() switch
        {
            "mm" or "millimeter" or "millimeters" => 1.0,
            "cm" or "centimeter" or "centimeters" => 10.0,
            "m" or "meter" or "meters" => 1000.0,
            "in" or "inch" or "inches" => 25.4,
            "ft" or "feet" or "foot" => 304.8,
            _ => 1.0
        };
    }

    private void EvaluateLines(
        DxfDocument document,
        DXFMetrics metrics,
        List<Segment2D> segments,
        Dictionary<string, LayerAccumulator> layerAccumulators,
        (double ToMillimeters, string UnitName) unitInfo,
        CancellationToken token)
    {
        foreach (var line in document.Entities.Lines)
        {
            token.ThrowIfCancellationRequested();

            var start = line.StartPoint;
            var end = line.EndPoint;
            var length = Vector3.Distance(start, end) * unitInfo.ToMillimeters;

            metrics.LineCount++;
            metrics.NumNodes += 2;

            AddLengthToLayer(line.Layer?.Name, length, layerAccumulators);
            AddSegment(segments, line.Layer?.Name ?? "default", start, end, unitInfo.ToMillimeters, isCurve: false, radius: null);
            AccumulateBySemanticType(layerAccumulators, line.Layer?.Name, length, metrics);
        }
    }

    private void EvaluateArcs(
        DxfDocument document,
        DXFMetrics metrics,
        List<Segment2D> segments,
        Dictionary<string, LayerAccumulator> layerAccumulators,
        (double ToMillimeters, string UnitName) unitInfo,
        CancellationToken token)
    {
        foreach (var arc in document.Entities.Arcs)
        {
            token.ThrowIfCancellationRequested();

            var angle = GetArcAngleRadians(arc.StartAngle, arc.EndAngle);
            var radiusMillimeters = arc.Radius * unitInfo.ToMillimeters;
            var length = radiusMillimeters * angle;
            metrics.ArcCount++;
            metrics.NumCurves++;
            metrics.NumNodes += 2;

            UpdateMinArcRadius(metrics, radiusMillimeters);
            RegisterDelicateArc(radiusMillimeters, Math.Abs(length), metrics.Quality);

            AddLengthToLayer(arc.Layer?.Name, length, layerAccumulators);
            AccumulateBySemanticType(layerAccumulators, arc.Layer?.Name, length, metrics);

            var segmentCount = Math.Max(4, (int)Math.Ceiling(angle / (Math.PI / 16)));
            var points = SampleArcPoints(arc, segmentCount);
            for (int i = 0; i < points.Count - 1; i++)
            {
                AddSegment(segments, arc.Layer?.Name ?? "default", points[i], points[i + 1], unitInfo.ToMillimeters, isCurve: true, radius: radiusMillimeters);
            }
        }
    }

    private void EvaluateCircles(
        DxfDocument document,
        DXFMetrics metrics,
        List<Segment2D> segments,
        Dictionary<string, LayerAccumulator> layerAccumulators,
        (double ToMillimeters, string UnitName) unitInfo,
        CancellationToken token)
    {
        foreach (var circle in document.Entities.Circles)
        {
            token.ThrowIfCancellationRequested();

            var radiusMillimeters = circle.Radius * unitInfo.ToMillimeters;
            var length = 2 * Math.PI * radiusMillimeters;
            metrics.ArcCount++;
            metrics.NumCurves++;
            metrics.NumNodes += 1;

            UpdateMinArcRadius(metrics, radiusMillimeters);
            RegisterDelicateArc(radiusMillimeters, length, metrics.Quality);

            AddLengthToLayer(circle.Layer?.Name, length, layerAccumulators);
            AccumulateBySemanticType(layerAccumulators, circle.Layer?.Name, length, metrics);
            RegisterClosedLoop(circle.Layer?.Name, layerAccumulators, metrics);

            var sampleCount = 32;
            var points = SampleCirclePoints(circle, sampleCount);
            for (int i = 0; i < points.Count - 1; i++)
            {
                AddSegment(segments, circle.Layer?.Name ?? "default", points[i], points[i + 1], unitInfo.ToMillimeters, isCurve: true, radius: radiusMillimeters);
            }
        }
    }

    private void EvaluatePolylines(
        DxfDocument document,
        DXFMetrics metrics,
        List<Segment2D> segments,
        Dictionary<string, LayerAccumulator> layerAccumulators,
        (double ToMillimeters, string UnitName) unitInfo,
        CancellationToken token)
    {
        foreach (var pl in document.Entities.Polylines2D)
        {
            token.ThrowIfCancellationRequested();

            metrics.PolylineCount++;
            metrics.NumNodes += pl.Vertexes.Count;

            if (pl.IsClosed)
            {
                RegisterClosedLoop(pl.Layer?.Name, layerAccumulators, metrics);
            }

            for (int i = 0; i < pl.Vertexes.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var current = pl.Vertexes[i];
                var next = pl.Vertexes[(i + 1) % pl.Vertexes.Count];
                bool isLast = i == pl.Vertexes.Count - 1;
                if (!pl.IsClosed && isLast) break;

                var start = current.Position;
                var end = next.Position;
                var bulge = current.Bulge;

                if (Math.Abs(bulge) < 1e-8)
                {
                    var length = DxfVector2.Distance(start, end) * unitInfo.ToMillimeters;
                    AddLengthToLayer(pl.Layer?.Name, length, layerAccumulators);
                    AccumulateBySemanticType(layerAccumulators, pl.Layer?.Name, length, metrics);
                    AddSegment(
                        segments,
                        pl.Layer?.Name ?? "default",
                        new Vector3(start.X, start.Y, 0),
                        new Vector3(end.X, end.Y, 0),
                        unitInfo.ToMillimeters,
                        isCurve: false,
                        radius: null);
                }
                else
                {
                    var chord = DxfVector2.Distance(start, end);
                    var angle = 4 * Math.Atan(bulge);
                    var radius = Math.Abs(chord / (2 * Math.Sin(angle / 2)));
                    var length = radius * Math.Abs(angle) * unitInfo.ToMillimeters;

                    metrics.NumCurves++;
                    UpdateMinArcRadius(metrics, radius * unitInfo.ToMillimeters);
                    RegisterDelicateArc(radius * unitInfo.ToMillimeters, length, metrics.Quality);

                    AddLengthToLayer(pl.Layer?.Name, length, layerAccumulators);
                    AccumulateBySemanticType(layerAccumulators, pl.Layer?.Name, length, metrics);

                    var points = SampleBulgedSegment(start, end, bulge, _options.ChordTolerance);
                    for (int s = 0; s < points.Count - 1; s++)
                    {
                        AddSegment(
                            segments,
                            pl.Layer?.Name ?? "default",
                            new Vector3(points[s].X, points[s].Y, 0),
                            new Vector3(points[s + 1].X, points[s + 1].Y, 0),
                            unitInfo.ToMillimeters,
                            isCurve: true,
                            radius: radius * unitInfo.ToMillimeters);
                    }
                }
            }
        }

        foreach (var pl in document.Entities.Polylines3D)
        {
            token.ThrowIfCancellationRequested();

            var vertices = pl.Vertexes.ToList();
            if (vertices.Count < 2) continue;

            metrics.PolylineCount++;
            metrics.NumNodes += vertices.Count;

            if (pl.IsClosed)
            {
                RegisterClosedLoop(pl.Layer?.Name, layerAccumulators, metrics);
            }

            for (int i = 0; i < vertices.Count; i++)
            {
                var current = vertices[i];
                var next = vertices[(i + 1) % vertices.Count];
                bool isLast = i == vertices.Count - 1;
                if (!pl.IsClosed && isLast) break;

                var length = Vector3.Distance(current, next) * unitInfo.ToMillimeters;
                AddLengthToLayer(pl.Layer?.Name, length, layerAccumulators);
                AccumulateBySemanticType(layerAccumulators, pl.Layer?.Name, length, metrics);
                AddSegment(segments, pl.Layer?.Name ?? "default", current, next, unitInfo.ToMillimeters, isCurve: false, radius: null);
            }
        }
    }

    private void EvaluateSplines(
        DxfDocument document,
        DXFMetrics metrics,
        List<Segment2D> segments,
        Dictionary<string, LayerAccumulator> layerAccumulators,
        (double ToMillimeters, string UnitName) unitInfo,
        CancellationToken token)
    {
        foreach (var spline in document.Entities.Splines)
        {
            token.ThrowIfCancellationRequested();

            metrics.SplineCount++;
            metrics.NumCurves++;
            var controlPoints = spline.ControlPoints ?? Array.Empty<Vector3>();
            metrics.NumNodes += controlPoints.Length;

            try
            {
                var tessellation = Math.Max(16, controlPoints.Length * 4);
                var poly = spline.ToPolyline2D(tessellation);
                double length = 0.0;

                for (int i = 0; i < poly.Vertexes.Count - 1; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var a = poly.Vertexes[i].Position;
                    var b = poly.Vertexes[i + 1].Position;
                    length += DxfVector2.Distance(a, b);
                    AddSegment(
                        segments,
                        spline.Layer?.Name ?? "default",
                        new Vector3(a.X, a.Y, 0),
                        new Vector3(b.X, b.Y, 0),
                        unitInfo.ToMillimeters,
                        isCurve: true,
                        radius: null);
                }

                length *= unitInfo.ToMillimeters;
                AddLengthToLayer(spline.Layer?.Name, length, layerAccumulators);
                AccumulateBySemanticType(layerAccumulators, spline.Layer?.Name, length, metrics);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao discretizar spline");
            }
        }
    }

    private DXFSerrilhaSummary? AnalyzeSerrilhaSymbols(
        DxfDocument document,
        double unitToMillimeters,
        CancellationToken token)
    {
        var summary = new DXFSerrilhaSummary();
        var entryLookup = new Dictionary<string, Dictionary<string, DXFSerrilhaEntry>>(StringComparer.OrdinalIgnoreCase);

        ProcessInsertSymbols(document, unitToMillimeters, token, summary, entryLookup);
        ProcessTextSymbols(document, summary, entryLookup);

        if (summary.TotalCount == 0 && summary.UnknownCount == 0)
        {
            return null;
        }

        if (summary.UnknownSymbols is { Count: 0 })
        {
            summary.UnknownSymbols = null;
        }

        return summary;
    }

    private void EnrichSerrilhaSummary(DXFSerrilhaSummary summary)
    {
        if (summary.Entries is null || summary.Entries.Count == 0)
        {
            summary.DistinctSemanticTypes = 0;
            summary.DistinctBladeCodes = 0;
            summary.Classification = null;
            return;
        }

        var semanticTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bladeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classification = new DXFSerrilhaClassificationMetrics();
        double totalEstimatedLength = 0.0;
        bool hasEstimatedLength = false;

        foreach (var entry in summary.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SemanticType))
            {
                semanticTypes.Add(entry.SemanticType.Trim());
            }

            if (!string.IsNullOrWhiteSpace(entry.BladeCode))
            {
                bladeCodes.Add(entry.BladeCode.Trim());
            }

            AccumulateSerrilhaClassification(entry, classification);

            if (entry.EstimatedLength.HasValue && entry.EstimatedLength.Value > 0)
            {
                totalEstimatedLength += entry.EstimatedLength.Value;
                hasEstimatedLength = true;
            }
        }

        classification.DistinctCategories = new[]
        {
            classification.Simple > 0,
            classification.Travada > 0,
            classification.Zipper > 0,
            classification.Mista > 0
        }.Count(static present => present);

        summary.DistinctSemanticTypes = semanticTypes.Count;
        summary.DistinctBladeCodes = bladeCodes.Count;
        summary.Classification = classification;
        summary.TotalEstimatedLength = hasEstimatedLength ? totalEstimatedLength : null;
        summary.AverageEstimatedLength = hasEstimatedLength && summary.TotalCount > 0
            ? totalEstimatedLength / summary.TotalCount
            : null;
    }

    private void AccumulateSerrilhaClassification(DXFSerrilhaEntry entry, DXFSerrilhaClassificationMetrics classification)
    {
        if (entry is null)
        {
            return;
        }

        var occurrences = Math.Max(entry.Count, 0);
        if (occurrences == 0)
        {
            return;
        }

        var hasMista = EntryContainsKeyword(entry, SerrilhaMistaKeywords);
        var hasZipper = EntryContainsKeyword(entry, SerrilhaZipperKeywords);
        var hasTravada = EntryContainsKeyword(entry, SerrilhaTravadaKeywords);

        if (!hasMista && !hasZipper && !hasTravada)
        {
            classification.Simple += occurrences;
            return;
        }

        if (hasMista)
        {
            classification.Mista += occurrences;
        }

        if (hasZipper)
        {
            classification.Zipper += occurrences;
        }

        if (hasTravada)
        {
            classification.Travada += occurrences;
        }
    }

    private static bool EntryContainsKeyword(DXFSerrilhaEntry entry, IEnumerable<string> keywords)
    {
        if (entry is null)
        {
            return false;
        }

        var rawFields = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.SemanticType))
        {
            rawFields.Add(entry.SemanticType);
        }

        if (!string.IsNullOrWhiteSpace(entry.BladeCode))
        {
            rawFields.Add(entry.BladeCode);
        }

        if (entry.SymbolNames is not null)
        {
            foreach (var symbol in entry.SymbolNames)
            {
                if (!string.IsNullOrWhiteSpace(symbol))
                {
                    rawFields.Add(symbol);
                }
            }
        }

        if (rawFields.Count == 0)
        {
            return false;
        }

        List<string>? normalizedFields = null;

        foreach (var keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                continue;
            }

            foreach (var field in rawFields)
            {
                if (field.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var normalizedKeyword = NormalizeComparisonText(keyword);
            if (string.IsNullOrEmpty(normalizedKeyword))
            {
                continue;
            }

            normalizedFields ??= rawFields
                .Select(NormalizeComparisonText)
                .Where(static value => !string.IsNullOrEmpty(value))
                .ToList();

            foreach (var normalizedField in normalizedFields)
            {
                if (normalizedField.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizeComparisonText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormD);
        var buffer = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch) || ch == '_' || ch == '-' || ch == '/' || ch == '\\')
            {
                continue;
            }

            buffer.Append(char.ToUpperInvariant(ch));
        }

        return buffer.ToString();
    }

    private void ApplyCorteSecoHeuristic(
        DXFSerrilhaSummary summary,
        IReadOnlyList<Segment2D> segments,
        IReadOnlyDictionary<string, LayerAccumulator> layerAccumulators,
        CancellationToken token)
    {
        var options = _options.CorteSeco ?? new DXFAnalysisOptions.CorteSecoOptions();
        if (!options.Enabled)
        {
            return;
        }

        if (summary.Entries.Count == 0)
        {
            return;
        }

        var bladeCodes = FindComplementaryBladeCodes(summary);
        if (bladeCodes.Count == 0)
        {
            return;
        }

        var layerTypeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in layerAccumulators)
        {
            layerTypeLookup[kvp.Key] = kvp.Value.Type;
        }

        var configuredTargets = options.TargetLayerTypes is { Count: > 0 }
            ? options.TargetLayerTypes
            : null;

        var targetTypes = configuredTargets is null
            ? new HashSet<string>(new[] { "serrilha", "serrilha_mista", "serrilhamista" }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(configuredTargets, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<CorteSecoCandidate>();
        for (int i = 0; i < segments.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var segment = segments[i];
            if (segment.IsCurve)
            {
                continue;
            }

            var layerName = string.IsNullOrWhiteSpace(segment.Layer) ? "default" : segment.Layer;
            if (!layerTypeLookup.TryGetValue(layerName, out var layerType))
            {
                layerType = ResolveLayerType(layerName);
            }

            if (!targetTypes.Contains(layerType))
            {
                continue;
            }

            var length = ComputeSegmentLength(segment);
            if (length < options.MinLengthMillimeters)
            {
                continue;
            }

            candidates.Add(new CorteSecoCandidate(i, segment, layerName, layerType, length));
        }

        if (candidates.Count < Math.Max(options.MinPairCount * 2, 2))
        {
            return;
        }

        var pairs = DetectCorteSecoPairs(candidates, options, token);
        if (pairs.Count < options.MinPairCount)
        {
            return;
        }

        summary.IsCorteSeco = true;
        summary.CorteSecoPairs = pairs
            .OrderByDescending(p => p.OverlapMillimeters)
            .Take(10)
            .ToList();
        summary.CorteSecoBladeCodes = bladeCodes;
    }

    private List<string> FindComplementaryBladeCodes(DXFSerrilhaSummary summary)
    {
        var groups = summary.Entries
            .Where(e => !string.IsNullOrWhiteSpace(e.BladeCode))
            .GroupBy(e => NormalizeBladeCode(e.BladeCode!), StringComparer.OrdinalIgnoreCase);

        var list = new List<string>();
        foreach (var group in groups)
        {
            if (group.Count() < 2)
            {
                continue;
            }

            var pretty = group
                .Select(e => e.BladeCode)
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));

            if (string.IsNullOrWhiteSpace(pretty))
            {
                pretty = group.Key;
            }

            if (string.IsNullOrWhiteSpace(pretty))
            {
                continue;
            }

            if (!list.Any(existing => existing.Equals(pretty, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(pretty);
            }
        }

        return list;
    }

    private static string NormalizeBladeCode(string bladeCode)
    {
        if (string.IsNullOrWhiteSpace(bladeCode))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(bladeCode.Length);
        foreach (var ch in bladeCode)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToUpperInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private List<DXFCorteSecoPair> DetectCorteSecoPairs(
        List<CorteSecoCandidate> candidates,
        DXFAnalysisOptions.CorteSecoOptions options,
        CancellationToken token)
    {
        var results = new List<DXFCorteSecoPair>();
        if (candidates.Count < 2)
        {
            return results;
        }

        var cellSize = Math.Max(Math.Max(options.MinLengthMillimeters, options.MaxOffsetMillimeters * 6), _options.GapTolerance * 4);
        var grid = new Dictionary<(int, int), List<int>>();

        for (int idx = 0; idx < candidates.Count; idx++)
        {
            token.ThrowIfCancellationRequested();
            var bounds = candidates[idx].Segment.Bounds;
            var inflate = options.MaxOffsetMillimeters;
            var minX = (int)Math.Floor((bounds.MinX - inflate) / cellSize);
            var maxX = (int)Math.Floor((bounds.MaxX + inflate) / cellSize);
            var minY = (int)Math.Floor((bounds.MinY - inflate) / cellSize);
            var maxY = (int)Math.Floor((bounds.MaxY + inflate) / cellSize);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    var cell = (x, y);
                    if (!grid.TryGetValue(cell, out var list))
                    {
                        list = new List<int>();
                        grid[cell] = list;
                    }
                    list.Add(idx);
                }
            }
        }

        var considered = new HashSet<(int, int)>();
        var cosineThreshold = Math.Cos(Math.Clamp(options.MaxParallelAngleDegrees, 0, 90) * Math.PI / 180.0);

        foreach (var entry in grid)
        {
            var indexes = entry.Value;
            for (int i = 0; i < indexes.Count; i++)
            {
                for (int j = i + 1; j < indexes.Count; j++)
                {
                    token.ThrowIfCancellationRequested();
                    var idxA = indexes[i];
                    var idxB = indexes[j];
                    if (idxA == idxB) continue;

                    var key = idxA < idxB ? (idxA, idxB) : (idxB, idxA);
                    if (!considered.Add(key))
                    {
                        continue;
                    }

                    if (TryBuildCorteSecoPair(
                            candidates[idxA],
                            candidates[idxB],
                            cosineThreshold,
                            options,
                            out var pair))
                    {
                        results.Add(pair);
                    }
                }
            }
        }

        return results;
    }

    private bool TryBuildCorteSecoPair(
        in CorteSecoCandidate a,
        in CorteSecoCandidate b,
        double cosineThreshold,
        DXFAnalysisOptions.CorteSecoOptions options,
        out DXFCorteSecoPair pair)
    {
        pair = default!;

        var dirA = ComputeUnitVector(a.Segment.Start, a.Segment.End);
        var dirB = ComputeUnitVector(b.Segment.Start, b.Segment.End);
        if ((Math.Abs(dirA.X) < 1e-6 && Math.Abs(dirA.Y) < 1e-6) ||
            (Math.Abs(dirB.X) < 1e-6 && Math.Abs(dirB.Y) < 1e-6))
        {
            return false;
        }

        var dot = Math.Abs(dirA.X * dirB.X + dirA.Y * dirB.Y);
        if (dot < cosineThreshold)
        {
            return false;
        }

        var angle = Math.Acos(Math.Min(1.0, dot)) * 180.0 / Math.PI;

        var overlap = ComputeOverlapAlongAxis(a, b, dirA);
        if (overlap <= 0)
        {
            return false;
        }

        var minLength = Math.Min(a.Length, b.Length);
        if (overlap < minLength * options.MinOverlapRatio)
        {
            return false;
        }

        var offsetCandidate = ComputeAverageOffset(a.Segment, b.Segment, dirA, dirB);
        if (!offsetCandidate.HasValue)
        {
            return false;
        }

        var offset = offsetCandidate.Value;
        if (offset <= Math.Max(_options.GapTolerance, 1e-3) || offset > options.MaxOffsetMillimeters)
        {
            return false;
        }

        pair = new DXFCorteSecoPair
        {
            LayerA = a.Layer,
            LayerB = b.Layer,
            TypeA = a.Type,
            TypeB = b.Type,
            OverlapMillimeters = overlap,
            OffsetMillimeters = offset,
            AngleDifferenceDegrees = angle
        };

        return true;
    }

    private static double ComputeOverlapAlongAxis(in CorteSecoCandidate a, in CorteSecoCandidate b, (double X, double Y) axis)
    {
        var aStart = 0.0;
        var aEnd = a.Length;

        var projBStart = DotAlongAxis(b.Segment.Start, a.Segment.Start, axis);
        var projBEnd = DotAlongAxis(b.Segment.End, a.Segment.Start, axis);

        var bMin = Math.Min(projBStart, projBEnd);
        var bMax = Math.Max(projBStart, projBEnd);

        return Math.Min(aEnd, bMax) - Math.Max(aStart, bMin);
    }

    private double? ComputeAverageOffset(
        Segment2D a,
        Segment2D b,
        (double X, double Y) dirA,
        (double X, double Y) dirB)
    {
        var offsetB1 = SignedDistanceToLine(b.Start, a.Start, dirA);
        var offsetB2 = SignedDistanceToLine(b.End, a.Start, dirA);
        if (offsetB1 * offsetB2 <= 0)
        {
            return null;
        }

        var offsetA1 = SignedDistanceToLine(a.Start, b.Start, dirB);
        var offsetA2 = SignedDistanceToLine(a.End, b.Start, dirB);
        if (offsetA1 * offsetA2 <= 0)
        {
            return null;
        }

        var avgA = (Math.Abs(offsetA1) + Math.Abs(offsetA2)) / 2.0;
        var avgB = (Math.Abs(offsetB1) + Math.Abs(offsetB2)) / 2.0;

        if (Math.Abs(Math.Abs(offsetB1) - Math.Abs(offsetB2)) > Math.Max(_options.GapTolerance * 2, 0.1))
        {
            return null;
        }

        if (Math.Abs(Math.Abs(offsetA1) - Math.Abs(offsetA2)) > Math.Max(_options.GapTolerance * 2, 0.1))
        {
            return null;
        }

        return Math.Max(avgA, avgB);
    }

    private static double ComputeSegmentLength(Segment2D segment)
    {
        return Math.Sqrt(segment.Start.DistanceSquared(segment.End));
    }

    private static (double X, double Y) ComputeUnitVector(Point2D start, Point2D end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6)
        {
            return (0.0, 0.0);
        }

        return (dx / length, dy / length);
    }

    private static double DotAlongAxis(Point2D point, Point2D origin, (double X, double Y) axis)
    {
        var vx = point.X - origin.X;
        var vy = point.Y - origin.Y;
        return vx * axis.X + vy * axis.Y;
    }

    private static double SignedDistanceToLine(Point2D point, Point2D origin, (double X, double Y) axis)
    {
        var vx = point.X - origin.X;
        var vy = point.Y - origin.Y;
        return vx * axis.Y - vy * axis.X;
    }

    private void ProcessInsertSymbols(
        DxfDocument document,
        double unitToMillimeters,
        CancellationToken token,
        DXFSerrilhaSummary summary,
        Dictionary<string, Dictionary<string, DXFSerrilhaEntry>> entryLookup)
    {
        var lookup = _serrilhaSymbolLookup.Value;
        if (lookup.Count == 0)
        {
            return;
        }

        var inserts = document.Entities.Inserts?.ToList() ?? new List<Insert>();
        if (inserts.Count == 0)
        {
            return;
        }

        foreach (var insert in inserts)
        {
            token.ThrowIfCancellationRequested();

            var blockName = insert.Block?.Name ?? string.Empty;
            var matcher = FindInsertMatcher(blockName, insert, lookup);
            if (matcher is null)
            {
                summary.UnknownCount++;
                if (!string.IsNullOrWhiteSpace(blockName))
                {
                    summary.UnknownSymbols ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    summary.UnknownSymbols.Add(blockName);
                }

                _logger.LogWarning("serrilha_unknown_symbol name={Symbol} layer={Layer}", blockName, insert.Layer?.Name ?? string.Empty);
                continue;
            }

            summary.TotalCount++;
            var entry = GetOrCreateSerrilhaEntry(summary, entryLookup, matcher.SemanticType, matcher.BladeCode);
            entry.Count++;
            if (!string.IsNullOrWhiteSpace(blockName))
            {
                entry.SymbolNames.Add(blockName);
            }

            var estimatedLength = EstimateInsertLength(insert, unitToMillimeters);
            if (estimatedLength.HasValue)
            {
                entry.EstimatedLength = (entry.EstimatedLength ?? 0.0) + estimatedLength.Value;
            }
            else if (matcher.DefaultLength.HasValue)
            {
                entry.EstimatedLength = (entry.EstimatedLength ?? 0.0) + matcher.DefaultLength.Value;
            }

            if (matcher.DefaultToothCount.HasValue)
            {
                entry.EstimatedToothCount = (entry.EstimatedToothCount ?? 0.0) + matcher.DefaultToothCount.Value;
            }
        }
    }

    private void ProcessTextSymbols(
        DxfDocument document,
        DXFSerrilhaSummary summary,
        Dictionary<string, Dictionary<string, DXFSerrilhaEntry>> entryLookup)
    {
        var lookup = _serrilhaTextLookup.Value;
        if (lookup.Count == 0)
        {
            return;
        }

        foreach (var text in document.Entities.Texts)
        {
            MatchTextSymbol(text?.Value, summary, entryLookup, lookup);
        }

        foreach (var mtext in document.Entities.MTexts)
        {
            MatchTextSymbol(mtext?.Value, summary, entryLookup, lookup);
        }
    }

    private void MatchTextSymbol(
        string? rawValue,
        DXFSerrilhaSummary summary,
        Dictionary<string, Dictionary<string, DXFSerrilhaEntry>> entryLookup,
        IReadOnlyDictionary<string, List<DXFAnalysisOptions.SerrilhaTextSymbolMatcher>> lookup)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return;
        }

        var value = rawValue.Trim();
        foreach (var kvp in lookup)
        {
            foreach (var matcher in kvp.Value)
            {
                var matches = matcher.AllowMultipleMatches
                    ? matcher.TextRegex.Matches(value).Cast<Match>()
                    : Enumerable.Repeat(matcher.TextRegex.Match(value), 1);

                foreach (var match in matches)
                {
                    if (!match.Success)
                    {
                        continue;
                    }

                    var semanticType = ResolveSemanticType(match, matcher);
                    if (string.IsNullOrWhiteSpace(semanticType))
                    {
                        continue;
                    }

                    summary.TotalCount++;
                    var bladeCode = ResolveBladeCode(match, matcher);
                    var entry = GetOrCreateSerrilhaEntry(summary, entryLookup, semanticType, bladeCode);
                    entry.Count++;
                    entry.SymbolNames.Add(match.Value.Trim());

                    var length = TryParseGroup(match, matcher.LengthGroup, matcher.LengthScale);
                    if (length.HasValue)
                    {
                        entry.EstimatedLength = (entry.EstimatedLength ?? 0.0) + length.Value;
                    }
                    else if (matcher.DefaultLength.HasValue)
                    {
                        entry.EstimatedLength = (entry.EstimatedLength ?? 0.0) + matcher.DefaultLength.Value;
                    }

                    var tooth = TryParseGroup(match, matcher.ToothCountGroup, matcher.ToothCountScale);
                    if (tooth.HasValue)
                    {
                        entry.EstimatedToothCount = (entry.EstimatedToothCount ?? 0.0) + tooth.Value;
                    }
                    else if (matcher.DefaultToothCount.HasValue)
                    {
                        entry.EstimatedToothCount = (entry.EstimatedToothCount ?? 0.0) + matcher.DefaultToothCount.Value;
                    }
                }
            }
        }
    }

    private static string? ResolveBladeCode(
        Match match,
        DXFAnalysisOptions.SerrilhaTextSymbolMatcher matcher)
    {
        if (!string.IsNullOrWhiteSpace(matcher.BladeCodeGroup))
        {
            var group = match.Groups[matcher.BladeCodeGroup];
            if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
            {
                var value = group.Value.Trim();
                return matcher.UppercaseBladeCode ? value.ToUpperInvariant() : value;
            }
        }

        if (string.IsNullOrWhiteSpace(matcher.BladeCode))
        {
            return null;
        }

        return matcher.UppercaseBladeCode ? matcher.BladeCode.ToUpperInvariant() : matcher.BladeCode;
    }

    private static string ResolveSemanticType(
        Match match,
        DXFAnalysisOptions.SerrilhaTextSymbolMatcher matcher)
    {
        var semanticType = matcher.SemanticType;
        if (!string.IsNullOrWhiteSpace(matcher.SemanticTypeGroup))
        {
            var group = match.Groups[matcher.SemanticTypeGroup];
            if (group.Success && !string.IsNullOrWhiteSpace(group.Value))
            {
                var token = group.Value.Trim();
                semanticType = matcher.SemanticTypeFormat.Replace("{value}", token, StringComparison.OrdinalIgnoreCase);
            }
        }

        if (string.IsNullOrWhiteSpace(semanticType))
        {
            return string.Empty;
        }

        return matcher.UppercaseSemanticType
            ? semanticType.ToUpperInvariant()
            : semanticType.Trim();
    }

    private static double? TryParseGroup(
        Match match,
        string? groupName,
        double scale)
    {
        if (string.IsNullOrWhiteSpace(groupName))
        {
            return null;
        }

        var group = match.Groups[groupName];
        if (!group.Success)
        {
            return null;
        }

        var token = group.Value?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalized = token.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value * scale
            : null;
    }

    private DXFAnalysisOptions.SerrilhaSymbolMatcher? FindInsertMatcher(
        string blockName,
        Insert insert,
        IReadOnlyDictionary<string, List<DXFAnalysisOptions.SerrilhaSymbolMatcher>> lookup)
    {
        foreach (var kvp in lookup)
        {
            foreach (var matcher in kvp.Value)
            {
                if (!matcher.SymbolNameRegex.IsMatch(blockName))
                {
                    continue;
                }

                if (matcher.AttributeRegex == null || MatchesAttribute(insert, matcher.AttributeRegex))
                {
                    return matcher;
                }
            }
        }

        return null;
    }


    private static bool MatchesAttribute(Insert insert, System.Text.RegularExpressions.Regex regex)
    {
        if (insert.Attributes == null)
        {
            return false;
        }

        foreach (var attribute in insert.Attributes)
        {
            if (!string.IsNullOrEmpty(attribute.Value) && regex.IsMatch(attribute.Value))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(attribute.Tag) && regex.IsMatch(attribute.Tag))
            {
                return true;
            }
        }

        return false;
    }

    private DXFSerrilhaEntry GetOrCreateSerrilhaEntry(
        DXFSerrilhaSummary summary,
        Dictionary<string, Dictionary<string, DXFSerrilhaEntry>> lookup,
        string semanticType,
        string? bladeCode)
    {
        if (!lookup.TryGetValue(semanticType, out var byBlade))
        {
            byBlade = new Dictionary<string, DXFSerrilhaEntry>(StringComparer.OrdinalIgnoreCase);
            lookup[semanticType] = byBlade;
        }

        var key = string.IsNullOrWhiteSpace(bladeCode) ? string.Empty : bladeCode;
        if (!byBlade.TryGetValue(key, out var entry))
        {
            entry = new DXFSerrilhaEntry
            {
                SemanticType = semanticType,
                BladeCode = string.IsNullOrWhiteSpace(bladeCode) ? null : bladeCode
            };
            byBlade[key] = entry;
            summary.Entries.Add(entry);
        }
        else if (entry.BladeCode is null && !string.IsNullOrWhiteSpace(bladeCode))
        {
            entry.BladeCode = bladeCode;
        }

        return entry;
    }

    private double? EstimateInsertLength(Insert insert, double unitToMillimeters)
    {
        try
        {
            var length = TryComputeInsertedGeometryLength(insert, depth: 0);
            if (!length.HasValue || length.Value <= 0)
            {
                return null;
            }

            return length.Value * unitToMillimeters;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao estimar comprimento para símbolo serrilha {Symbol}", insert.Block?.Name ?? string.Empty);
            return null;
        }
    }

    private double? TryComputeInsertedGeometryLength(Insert insert, int depth)
    {
        if (depth > 8)
        {
            return null;
        }

        var exploded = insert.Explode();
        double total = 0.0;
        var hasGeometry = false;

        foreach (var entity in exploded)
        {
            var length = ComputeEntityLength(entity, depth + 1);
            if (length > 1e-9)
            {
                hasGeometry = true;
                total += length;
            }
        }

        return hasGeometry ? total : (double?)null;
    }

    private double ComputeEntityLength(EntityObject entity, int depth)
    {
        if (depth > 8)
        {
            return 0.0;
        }

        switch (entity)
        {
            case Line line:
                return Vector3.Distance(line.StartPoint, line.EndPoint);
            case Polyline2D poly2:
                return ComputePolyline2DLength(poly2);
            case Polyline3D poly3:
                return ComputePolyline3DLength(poly3);
            case Arc arc:
                return ComputeArcLength(arc);
            case Circle circle:
                return 2 * Math.PI * Math.Abs(circle.Radius);
            case Ellipse ellipse:
                return ComputeEllipseLength(ellipse);
            case Spline spline:
                return ComputeSplineLength(spline);
            case Insert nested:
                var nestedLength = TryComputeInsertedGeometryLength(nested, depth + 1);
                return nestedLength ?? 0.0;
            default:
                return ComputeExplodedChildren(entity, depth + 1);
        }
    }

    private double ComputeArcLength(Arc arc)
    {
        var start = arc.StartAngle * MathHelper.DegToRad;
        var end = arc.EndAngle * MathHelper.DegToRad;
        var sweep = end - start;
        if (sweep < 0)
        {
            sweep += 2 * Math.PI;
        }

        if (Math.Abs(sweep) < 1e-9)
        {
            sweep = 2 * Math.PI;
        }

        return Math.Abs(arc.Radius) * Math.Abs(sweep);
    }

    private double ComputePolyline2DLength(Polyline2D poly)
    {
        if (poly.Vertexes.Count < 2)
        {
            return 0.0;
        }

        double total = 0.0;
        for (int i = 0; i < poly.Vertexes.Count; i++)
        {
            var current = poly.Vertexes[i];
            var next = poly.Vertexes[(i + 1) % poly.Vertexes.Count];
            bool isLast = i == poly.Vertexes.Count - 1;
            if (!poly.IsClosed && isLast)
            {
                break;
            }

            var start = current.Position;
            var end = next.Position;
            var bulge = current.Bulge;

            if (Math.Abs(bulge) < 1e-8)
            {
                total += DxfVector2.Distance(start, end);
            }
            else
            {
                var chord = DxfVector2.Distance(start, end);
                var angle = 4 * Math.Atan(bulge);
                var radius = Math.Abs(chord / (2 * Math.Sin(angle / 2)));
                total += radius * Math.Abs(angle);
            }
        }

        return total;
    }

    private double ComputePolyline3DLength(Polyline3D poly)
    {
        var vertices = poly.Vertexes.ToList();
        if (vertices.Count < 2)
        {
            return 0.0;
        }

        double total = 0.0;
        for (int i = 0; i < vertices.Count; i++)
        {
            var current = vertices[i];
            var next = vertices[(i + 1) % vertices.Count];
            bool isLast = i == vertices.Count - 1;
            if (!poly.IsClosed && isLast)
            {
                break;
            }

            total += Vector3.Distance(current, next);
        }

        return total;
    }

    private double ComputeEllipseLength(Ellipse ellipse)
    {
        try
        {
            var tessellation = 64;
            var poly = ellipse.ToPolyline2D(tessellation);
            return ComputePolyline2DLength(poly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao converter ellipse para polilinha");
            return 0.0;
        }
    }

    private double ComputeSplineLength(Spline spline)
    {
        try
        {
            var tessellation = Math.Max(16, (spline.ControlPoints?.Length ?? 0) * 4);
            var polyline = spline.ToPolyline2D(tessellation);
            return ComputePolyline2DLength(polyline);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao converter spline para polilinha");
            return 0.0;
        }
    }

    private double ComputeExplodedChildren(EntityObject entity, int depth)
    {
        if (depth > 8)
        {
            return 0.0;
        }

        try
        {
            var method = entity.GetType().GetMethod("Explode", Type.EmptyTypes);
            if (method == null)
            {
                return 0.0;
            }

            if (method.Invoke(entity, Array.Empty<object>()) is not IList<EntityObject> exploded || exploded.Count == 0)
            {
                return 0.0;
            }

            double total = 0.0;
            foreach (var child in exploded)
            {
                total += ComputeEntityLength(child, depth + 1);
            }

            return total;
        }
        catch
        {
            return 0.0;
        }
    }

    private void UpdateMinArcRadius(DXFMetrics metrics, double radiusMm)
    {
        if (radiusMm <= _options.MinCurveRadiusTolerance) return;
        if (metrics.MinArcRadius <= 0 || radiusMm < metrics.MinArcRadius)
        {
            metrics.MinArcRadius = radiusMm;
        }
    }

    private void ComputeExtents(DxfDocument document, List<Segment2D> segments, DXFMetrics metrics, double scale)
    {
        if (segments.Count == 0)
        {
            metrics.Extents = new DXFExtents();
            return;
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var segment in segments)
        {
            minX = Math.Min(minX, Math.Min(segment.Start.X, segment.End.X));
            minY = Math.Min(minY, Math.Min(segment.Start.Y, segment.End.Y));
            maxX = Math.Max(maxX, Math.Max(segment.Start.X, segment.End.X));
            maxY = Math.Max(maxY, Math.Max(segment.Start.Y, segment.End.Y));
        }

        metrics.Extents = new DXFExtents
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }

    private void AddLengthToLayer(string? layerName, double length, Dictionary<string, LayerAccumulator> acc)
    {
        var name = string.IsNullOrWhiteSpace(layerName) ? "default" : layerName;
        if (!acc.TryGetValue(name, out var layer))
        {
            layer = new LayerAccumulator(name, ResolveLayerType(name));
            acc[name] = layer;
        }

        layer.TotalLength += length;
        layer.Count++;
    }

    private void RegisterClosedLoop(string? layerName, Dictionary<string, LayerAccumulator> acc, DXFMetrics metrics)
    {
        var name = string.IsNullOrWhiteSpace(layerName) ? "default" : layerName;
        if (!acc.TryGetValue(name, out var layer))
        {
            layer = new LayerAccumulator(name, ResolveLayerType(name));
            acc[name] = layer;
        }

        layer.ClosedLoopCount++;

        metrics.Quality.ClosedLoops++;
        metrics.Quality.ClosedLoopsByType ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var typeKey = string.IsNullOrWhiteSpace(layer.Type) ? "unknown" : layer.Type;
        metrics.Quality.ClosedLoopsByType.TryGetValue(typeKey, out var existing);
        metrics.Quality.ClosedLoopsByType[typeKey] = existing + 1;
    }

    private void RegisterDelicateArc(double radiusMillimeters, double lengthMillimeters, DXFQualityMetrics quality)
    {
        if (_options.DelicateArcRadiusThreshold <= 0)
        {
            return;
        }

        if (radiusMillimeters <= _options.DelicateArcRadiusThreshold + 1e-6)
        {
            quality.DelicateArcCount++;
            quality.DelicateArcLength += Math.Max(lengthMillimeters, 0.0);
        }
    }

    private void AnnotateSpecialMaterials(DXFMetrics metrics, IReadOnlyDictionary<string, LayerAccumulator> layerAccumulators)
    {
        var lookup = _specialMaterialRegexLookup.Value;
        if (lookup.Count == 0)
        {
            return;
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in lookup)
        {
            var material = kvp.Key;
            var regexes = kvp.Value;
            if (regexes is null || regexes.Count == 0)
            {
                continue;
            }

            foreach (var layer in layerAccumulators.Values)
            {
                if (regexes.Any(regex => regex.IsMatch(layer.Name)))
                {
                    matches.Add(material);
                    break;
                }
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        var ordered = matches.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        metrics.Quality.SpecialMaterials = ordered;

        metrics.Quality.Notes ??= new List<string>();
        var notePrefix = "Material especial detectado";
        var existing = metrics.Quality.Notes.FirstOrDefault(n => n.StartsWith(notePrefix, StringComparison.OrdinalIgnoreCase));
        var summary = string.Join(", ", ordered);
        var note = $"{notePrefix}: {summary}.";
        if (existing is null)
        {
            metrics.Quality.Notes.Add(note);
        }
        else
        {
            var index = metrics.Quality.Notes.IndexOf(existing);
            if (index >= 0)
            {
                metrics.Quality.Notes[index] = note;
            }
        }
    }

    private void AccumulateBySemanticType(Dictionary<string, LayerAccumulator> acc, string? layerName, double length, DXFMetrics metrics)
    {
        var name = string.IsNullOrWhiteSpace(layerName) ? "default" : layerName;
        if (!acc.TryGetValue(name, out var layer))
        {
            layer = new LayerAccumulator(name, ResolveLayerType(name));
            acc[name] = layer;
        }

        switch (layer.Type.ToLowerInvariant())
        {
            case "corte":
                metrics.TotalCutLength += length;
                break;
            case "vinco":
                metrics.TotalFoldLength += length;
                break;
            case "serrilha":
                metrics.TotalPerfLength += length;
                break;
            case "serrilhamista":
            case "serrilha_mista":
            case "serrilhamista2":
                metrics.TotalPerfLength += length;
                break;
            case "trespt":
            case "3pt":
                metrics.TotalThreePtLength += length;
                metrics.ThreePtSegmentCount++;
                break;
        }
    }

    private string ResolveLayerType(string layerName)
    {
        var sanitized = (layerName ?? string.Empty).Trim();

        foreach (var kvp in _layerRegexLookup.Value)
        {
            foreach (var regex in kvp.Value)
            {
                if (regex.IsMatch(sanitized))
                {
                    return kvp.Key;
                }
            }
        }

        if (sanitized.Contains("VINCO", StringComparison.OrdinalIgnoreCase))
        {
            return "vinco";
        }

        if (sanitized.Contains("SERR", StringComparison.OrdinalIgnoreCase))
        {
            return "serrilha";
        }

        return "outro";
    }

    private void AddSegment(List<Segment2D> segments, string layer, Vector3 start, Vector3 end, double scale, bool isCurve, double? radius)
    {
        segments.Add(new Segment2D(
            new Point2D(start.X * scale, start.Y * scale),
            new Point2D(end.X * scale, end.Y * scale),
            layer,
            isCurve,
            radius));
    }

    private List<Vector3> SampleArcPoints(Arc arc, int count)
    {
        var points = new List<Vector3>(count + 1);
        var start = DegreesToRadians(arc.StartAngle);
        var end = DegreesToRadians(arc.EndAngle);
        var sweep = NormalizeAngle(end - start);
        var step = sweep / count;

        for (int i = 0; i <= count; i++)
        {
            var angle = start + step * i;
            var x = arc.Center.X + arc.Radius * Math.Cos(angle);
            var y = arc.Center.Y + arc.Radius * Math.Sin(angle);
            points.Add(new Vector3(x, y, arc.Center.Z));
        }

        return points;
    }

    private List<Vector3> SampleCirclePoints(Circle circle, int count)
    {
        var points = new List<Vector3>(count + 1);
        for (int i = 0; i <= count; i++)
        {
            var angle = 2 * Math.PI * i / count;
            var x = circle.Center.X + circle.Radius * Math.Cos(angle);
            var y = circle.Center.Y + circle.Radius * Math.Sin(angle);
            points.Add(new Vector3(x, y, circle.Center.Z));
        }

        return points;
    }

    private List<DxfVector2> SampleBulgedSegment(DxfVector2 start, DxfVector2 end, double bulge, double chordTolerance)
    {
        var points = new List<DxfVector2> { start };

        var chord = DxfVector2.Distance(start, end);
        var angle = 4 * Math.Atan(bulge);
        var radius = Math.Abs(chord / (2 * Math.Sin(angle / 2)));

        int segments = Math.Clamp((int)Math.Ceiling(Math.Abs(angle) / Math.Acos(1 - chordTolerance / radius)), 4, 64);
        var center = CalculateBulgeCenter(start, end, bulge, radius);
        var startAngle = Math.Atan2(start.Y - center.Y, start.X - center.X);
        var sweep = angle;
        var step = sweep / segments;

        for (int i = 1; i < segments; i++)
        {
            var angleCurrent = startAngle + step * i;
            points.Add(new DxfVector2(
                center.X + radius * Math.Cos(angleCurrent),
                center.Y + radius * Math.Sin(angleCurrent)));
        }

        points.Add(end);
        return points;
    }

    private DxfVector2 CalculateBulgeCenter(DxfVector2 start, DxfVector2 end, double bulge, double radius)
    {
        var chordMid = new DxfVector2((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
        var chordVecX = end.X - start.X;
        var chordVecY = end.Y - start.Y;
        var chordLength = Math.Sqrt(chordVecX * chordVecX + chordVecY * chordVecY);
        var sagitta = (bulge * chordLength) / 2.0;
        var normalX = -chordVecY;
        var normalY = chordVecX;
        var normalLengthSq = normalX * normalX + normalY * normalY;
        if (normalLengthSq < 1e-12)
        {
            return chordMid;
        }

        var invLen = 1.0 / Math.Sqrt(normalLengthSq);
        normalX *= invLen;
        normalY *= invLen;
        return new DxfVector2(
            chordMid.X + normalX * (radius - sagitta),
            chordMid.Y + normalY * (radius - sagitta));
    }

    private double GetArcAngleRadians(double startAngleDegrees, double endAngleDegrees)
    {
        var start = DegreesToRadians(startAngleDegrees);
        var end = DegreesToRadians(endAngleDegrees);
        return NormalizeAngle(end - start);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private double NormalizeAngle(double angle)
    {
        while (angle < 0) angle += 2 * Math.PI;
        while (angle > 2 * Math.PI) angle -= 2 * Math.PI;
        return angle;
    }

    private int DetectIntersections(IReadOnlyList<Segment2D> segments, CancellationToken token)
    {
        if (segments.Count < 2)
        {
            return 0;
        }

        var bbox = ComputeBoundingBox(segments);
        var diag = bbox.DiagonalLength();
        var cellSize = Math.Max(diag / 100.0, _options.GapTolerance * 4);

        var grid = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var bounds = segments[i].Bounds;
            int minX = (int)Math.Floor(bounds.MinX / cellSize);
            int maxX = (int)Math.Floor(bounds.MaxX / cellSize);
            int minY = (int)Math.Floor(bounds.MinY / cellSize);
            int maxY = (int)Math.Floor(bounds.MaxY / cellSize);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    var cell = (x, y);
                    if (!grid.TryGetValue(cell, out var list))
                    {
                        list = new List<int>();
                        grid[cell] = list;
                    }
                    list.Add(i);
                }
            }
        }

        var considered = new HashSet<(int, int)>();
        int intersections = 0;

        foreach (var entry in grid)
        {
            var list = entry.Value;
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    token.ThrowIfCancellationRequested();

                    var idx1 = list[i];
                    var idx2 = list[j];

                    if (idx1 == idx2) continue;
                    var key = idx1 < idx2 ? (idx1, idx2) : (idx2, idx1);
                    if (!considered.Add(key)) continue;

                    if (SegmentsIntersect(segments[idx1], segments[idx2], _options.GapTolerance))
                    {
                        intersections++;
                    }
                }
            }
        }

        return intersections;
    }

    private BoundingBox2D ComputeBoundingBox(IReadOnlyList<Segment2D> segments)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var segment in segments)
        {
            minX = Math.Min(minX, Math.Min(segment.Start.X, segment.End.X));
            minY = Math.Min(minY, Math.Min(segment.Start.Y, segment.End.Y));
            maxX = Math.Max(maxX, Math.Max(segment.Start.X, segment.End.X));
            maxY = Math.Max(maxY, Math.Max(segment.Start.Y, segment.End.Y));
        }
        return new BoundingBox2D(minX, minY, maxX, maxY);
    }

    private bool SegmentsIntersect(Segment2D a, Segment2D b, double tolerance)
    {
        var p = a.Start;
        var r = new NumericsVector2((float)(a.End.X - a.Start.X), (float)(a.End.Y - a.Start.Y));
        var q = b.Start;
        var s = new NumericsVector2((float)(b.End.X - b.Start.X), (float)(b.End.Y - b.Start.Y));

        var rxs = Cross(r, s);
        var q_p = new NumericsVector2((float)(q.X - p.X), (float)(q.Y - p.Y));
        var qpxr = Cross(q_p, r);

        if (Math.Abs(rxs) < 1e-8 && Math.Abs(qpxr) < 1e-8)
        {
            // Colinear; treat as intersection if projections overlap more than tolerance.
            var dotR = Dot(r, r);
            var t0 = Dot(q_p, r) / dotR;
            var t1 = t0 + Dot(s, r) / dotR;

            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }

            return (t0 <= 1 && t1 >= 0) &&
                   (Math.Min(1, t1) - Math.Max(0, t0) >= tolerance / Math.Max(Math.Sqrt(dotR), 1e-6f));
        }

        if (Math.Abs(rxs) < 1e-8)
        {
            return false;
        }

        var t = Cross(q_p, s) / rxs;
        var u = Cross(q_p, r) / rxs;

        if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
        {
            var ix = p.X + t * (a.End.X - a.Start.X);
            var iy = p.Y + t * (a.End.Y - a.Start.Y);
            // Exclude shared endpoints counted twice
            var isEndpoint = (DistanceSquared(ix, iy, p.X, p.Y) < tolerance * tolerance) ||
                             (DistanceSquared(ix, iy, a.End.X, a.End.Y) < tolerance * tolerance) ||
                             (DistanceSquared(ix, iy, b.Start.X, b.Start.Y) < tolerance * tolerance) ||
                             (DistanceSquared(ix, iy, b.End.X, b.End.Y) < tolerance * tolerance);
            return !isEndpoint;
        }

        return false;
    }

    private static float Cross(NumericsVector2 a, NumericsVector2 b) => a.X * b.Y - a.Y * b.X;

    private static float Dot(NumericsVector2 a, NumericsVector2 b) => a.X * b.X + a.Y * b.Y;

    private static double DistanceSquared(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return dx * dx + dy * dy;
    }

    private readonly record struct CorteSecoCandidate(
        int Index,
        Segment2D Segment,
        string Layer,
        string Type,
        double Length);

    private sealed class LayerAccumulator
    {
        public LayerAccumulator(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }
        public string Type { get; }
        public double TotalLength { get; set; }
        public int Count { get; set; }
        public int ClosedLoopCount { get; set; }
    }
}

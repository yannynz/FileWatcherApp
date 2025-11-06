using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Entities;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Vector2 = netDxf.Vector2;
using Vector3 = netDxf.Vector3;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Performs lightweight sanitation and quality analysis before the DXF metrics are extracted.
/// </summary>
public sealed class DXFPreprocessor
{
    private readonly ILogger<DXFPreprocessor> _logger;
    private readonly DXFAnalysisOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="DXFPreprocessor"/> class.
    /// </summary>
    public DXFPreprocessor(IOptions<DXFAnalysisOptions> options, ILogger<DXFPreprocessor> logger)
    {
        _logger = logger;
        _options = options.Value;
    }

    /// <summary>
    /// Cleans degeneracies and computes quality hints that downstream steps can use.
    /// </summary>
    /// <param name="document">The DXF document to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A quality summary.</returns>
    public DXFQualityMetrics Preprocess(DxfDocument document, CancellationToken cancellationToken = default)
    {
        var quality = new DXFQualityMetrics();
        var endpoints = new List<Vector2>();
        var segmentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        RemoveDegenerateLines(document, quality, endpoints, segmentKeys, cancellationToken);
        NormalizePolylines(document, quality, endpoints, segmentKeys, cancellationToken);
        NormalizeSplines(document, quality, endpoints, segmentKeys, cancellationToken);

        quality.DanglingEnds = CountDanglingEnds(endpoints);

        return quality;
    }

    private void RemoveDegenerateLines(
        DxfDocument document,
        DXFQualityMetrics quality,
        List<Vector2> endpoints,
        HashSet<string> segmentKeys,
        CancellationToken token)
    {
        foreach (var line in document.Entities.Lines.ToList())
        {
            token.ThrowIfCancellationRequested();

            var start = new Vector2(line.StartPoint.X, line.StartPoint.Y);
            var end = new Vector2(line.EndPoint.X, line.EndPoint.Y);
            var length = Distance(start, end);

            if (length < _options.GapTolerance)
            {
                document.Entities.Remove(line);
                quality.TinyGaps++;
                continue;
            }

            RegisterSegment(start, end, endpoints, segmentKeys, quality);
        }
    }

    private void NormalizePolylines(
        DxfDocument document,
        DXFQualityMetrics quality,
        List<Vector2> endpoints,
        HashSet<string> segmentKeys,
        CancellationToken token)
    {
        foreach (var pl in document.Entities.Polylines2D.ToList())
        {
            token.ThrowIfCancellationRequested();

            var vertices = pl.Vertexes;
            if (vertices.Count < 2) continue;

            for (int i = 0; i < vertices.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var current = vertices[i];
                var next = vertices[(i + 1) % vertices.Count];

                var start = current.Position;
                var end = next.Position;

                if (!pl.IsClosed && i == vertices.Count - 1) break;

                if (Distance(start, end) < _options.GapTolerance)
                {
                    var average = new Vector2((start.X + end.X) / 2.0, (start.Y + end.Y) / 2.0);
                    vertices[i] = new Polyline2DVertex(average, current.Bulge);
                    vertices[(i + 1) % vertices.Count] = new Polyline2DVertex(average, next.Bulge);
                    quality.TinyGaps++;
                    start = average;
                    end = average;
                }

                RegisterSegment(start, end, endpoints, segmentKeys, quality);
            }
        }

        foreach (var pl in document.Entities.Polylines3D.ToList())
        {
            token.ThrowIfCancellationRequested();

            var vertices = pl.Vertexes;
            if (vertices.Count < 2) continue;

            for (int i = 0; i < vertices.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var current = vertices[i];
                var next = vertices[(i + 1) % vertices.Count];

                if (!pl.IsClosed && i == vertices.Count - 1) break;

                var start2 = new Vector2(current.X, current.Y);
                var end2 = new Vector2(next.X, next.Y);
                RegisterSegment(start2, end2, endpoints, segmentKeys, quality);
            }
        }
    }

    private void NormalizeSplines(
        DxfDocument document,
        DXFQualityMetrics quality,
        List<Vector2> endpoints,
        HashSet<string> segmentKeys,
        CancellationToken token)
    {
        foreach (var spline in document.Entities.Splines.ToList())
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var poly = spline.ToPolyline2D(16);
                for (int i = 0; i < poly.Vertexes.Count - 1; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var a = poly.Vertexes[i].Position;
                    var b = poly.Vertexes[i + 1].Position;
                    RegisterSegment(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), endpoints, segmentKeys, quality);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha ao discretizar spline para qualidade.");
            }
        }
    }

    private static double Distance(Vector2 a, Vector2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void RegisterSegment(
        Vector2 start,
        Vector2 end,
        List<Vector2> endpoints,
        HashSet<string> segmentKeys,
        DXFQualityMetrics quality)
    {
        endpoints.Add(start);
        endpoints.Add(end);

        var key = MakeSegmentKey(start, end);
        if (!segmentKeys.Add(key))
        {
            quality.Overlaps++;
        }
    }

    private string MakeSegmentKey(Vector2 start, Vector2 end)
    {
        static string FormatVector(Vector2 v) => $"{Math.Round(v.X, 3):F3}:{Math.Round(v.Y, 3):F3}";
        var a = FormatVector(start);
        var b = FormatVector(end);
        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    private int CountDanglingEnds(List<Vector2> endpoints)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var point in endpoints)
        {
            var key = $"{Math.Round(point.X, 2):F2}:{Math.Round(point.Y, 2):F2}";
            map.TryGetValue(key, out int count);
            map[key] = count + 1;
        }

        var dangling = map.Values.Count(v => v == 1);
        return dangling;
    }
}

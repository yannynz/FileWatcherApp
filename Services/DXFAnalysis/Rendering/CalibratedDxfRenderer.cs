using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using netDxf;
using netDxf.Collections;
using netDxf.Entities;
using SkiaSharp;
using Vector2 = netDxf.Vector2;
using Vector3 = netDxf.Vector3;

namespace FileWatcherApp.Services.DXFAnalysis.Rendering;

/// <summary>
/// Reuses the standalone DxfRender calibration logic to produce PNG snapshots in-memory.
/// </summary>
public sealed class CalibratedDxfRenderer
{
    private const int TargetDimension = 2000;
    private const double FramePaddingFraction = 0.0;
    private const double KnifeMarginRatio = 0.0;
    private const int CurvePrecision = 256;
    private const double MinExtent = 1e-3;
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly ILogger<CalibratedDxfRenderer> _logger;

    public CalibratedDxfRenderer(ILogger<CalibratedDxfRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Renders the supplied DXF document into an in-memory PNG using the calibrated heuristics.
    /// </summary>
    /// <param name="filePath">Absolute path to the DXF file (used only for logging).</param>
    /// <param name="document">Pre-loaded DXF document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="overlay">Optional callback executed after the calibrated drawing, before snapshotting.</param>
    /// <returns>The calibrated render result.</returns>
    public CalibratedRenderResult Render(
        string filePath,
        DxfDocument document,
        CancellationToken cancellationToken,
        Action<SKCanvas, CalibratedRenderInfo>? overlay = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entities = FlattenEntities(document.Entities.All).ToList();
        if (entities.Count == 0)
        {
            throw new InvalidOperationException($"Nenhuma entidade visível encontrada no DXF: {filePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var primitives = ConvertEntitiesToPrimitives(entities).ToList();
        if (primitives.Count == 0)
        {
            throw new InvalidOperationException($"Nenhuma geometria renderizável encontrada no DXF: {filePath}");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var globalBounds = CalculateBounds(primitives);
        if (globalBounds is null)
        {
            throw new InvalidOperationException($"Não foi possível determinar o bounding box global do DXF: {filePath}");
        }

        Bounds primaryBounds;
        bool knifeDetected = false;
        int knifeCandidateCount = 0;
        bool combinedKnife = false;
        bool skippedFrame = false;

        var knifeDetection = DetectKnifeBounds(entities);
        if (knifeDetection is { } detection)
        {
            knifeDetected = true;
            knifeCandidateCount = detection.TotalCandidates;
            combinedKnife = detection.CombinedMultiple;
            skippedFrame = detection.SkippedDominantFrame;
            primaryBounds = detection.Bounds;
        }
        else
        {
            primaryBounds = globalBounds.Value;
        }

        var baseBounds = primaryBounds.EnsureMinimumExtent(MinExtent);
        var targetBounds = baseBounds.Expand(KnifeMarginRatio);

        var (imageWidth, imageHeight) = CalculateImageDimensions(targetBounds);
        var scaleSettings = CalculateScale(targetBounds, imageWidth, imageHeight);

        LogKnifeBounds(filePath, primaryBounds, knifeDetected, knifeCandidateCount, combinedKnife, skippedFrame);
        LogScale(filePath, targetBounds, scaleSettings);

        cancellationToken.ThrowIfCancellationRequested();

        using var surface = CreateSurface(imageWidth, imageHeight);
        var canvas = surface.Canvas ?? throw new InvalidOperationException("Falha ao inicializar superfície de desenho.");

        ConfigureCanvas(canvas);
        using var paint = CreatePaint(scaleSettings.Scale);
        var clipBounds = targetBounds;
        var clipRect = CalculateClipRect(scaleSettings, paint.StrokeWidth);

        var primitivesToDraw = FilterPrimitives(primitives, clipBounds, scaleSettings.Scale, out var fallbackToOriginal);
        LogPrimitiveFilter(filePath, primitives.Count, primitivesToDraw.Count, fallbackToOriginal);

        canvas.Save();
        canvas.ClipRect(clipRect, SKClipOperation.Intersect, antialias: true);
        try
        {
            foreach (var primitive in primitivesToDraw)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var points = BuildPointArray(primitive.Points, scaleSettings, primitive.IsClosed);
                if (points.Length < 2)
                {
                    continue;
                }

                using var path = new SKPath();
                path.MoveTo(points[0]);
                for (var i = 1; i < points.Length; i++)
                {
                    path.LineTo(points[i]);
                }

                if (primitive.IsClosed)
                {
                    path.Close();
                }

                canvas.DrawPath(path, paint);
            }
        }
        finally
        {
            canvas.Restore();
        }

        var renderInfo = new CalibratedRenderInfo(imageWidth, imageHeight, scaleSettings.Scale);
        overlay?.Invoke(canvas, renderInfo);

        canvas.Flush();

        using var image = surface.Snapshot();
        using var subset = TryCreateSubset(image, scaleSettings, paint.StrokeWidth);
        var imageToEncode = subset ?? image;
        using var data = imageToEncode.Encode(SKEncodedImageFormat.Png, 100);
        if (data is null)
        {
            throw new InvalidOperationException("Falha ao codificar imagem renderizada.");
        }

        var bytes = data.ToArray();
        var finalWidth = imageToEncode.Width;
        var finalHeight = imageToEncode.Height;
        _logger.LogInformation(
            "Renderização concluída para {File}. Dimensões={Width}x{Height} px | DPI efetivo≈{Dpi:0.##} | faixas combinadas={Combined} | moldura ignorada={Skipped} | fallback filtro={Fallback}",
            filePath,
            finalWidth,
            finalHeight,
            renderInfo.EffectiveDpi,
            combinedKnife,
            skippedFrame,
            fallbackToOriginal);

        return new CalibratedRenderResult(
            bytes,
            finalWidth,
            finalHeight,
            scaleSettings.Scale,
            knifeDetected,
            knifeCandidateCount,
            combinedKnife,
            skippedFrame,
            fallbackToOriginal);
    }

    private static void ConfigureCanvas(SKCanvas canvas) => canvas.Clear(SKColors.Transparent);

    private static float CalculateStrokeWidth(double scale) =>
        (float)Math.Clamp(scale / 150.0, 1.0, 4.5);

    private static SKPaint CreatePaint(double scale)
    {
        var width = CalculateStrokeWidth(scale);
        return new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = SKColors.Black,
            StrokeWidth = width,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
    }

    private static SKSurface CreateSurface(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var surface = SKSurface.Create(info);
        return surface ?? throw new InvalidOperationException("Falha ao criar superfície SkiaSharp.");
    }

    private IEnumerable<EntityObject> FlattenEntities(IEnumerable<EntityObject> entities)
    {
        foreach (var entity in entities)
        {
            foreach (var flattened in FlattenEntity(entity))
            {
                yield return flattened;
            }
        }
    }

    private IEnumerable<EntityObject> FlattenEntity(EntityObject entity)
    {
        if (entity is null)
        {
            yield break;
        }

        if (!entity.IsVisible || (entity.Layer != null && entity.Layer.IsFrozen))
        {
            yield break;
        }

        if (entity is Insert insert)
        {
            foreach (var nested in insert.Explode())
            {
                foreach (var flattened in FlattenEntity(nested))
                {
                    yield return flattened;
                }
            }
            yield break;
        }

        yield return entity;
    }

    private static IEnumerable<RenderPrimitive> ConvertEntitiesToPrimitives(IEnumerable<EntityObject> entities)
    {
        foreach (var entity in entities)
        {
            foreach (var primitive in ConvertEntityToPrimitives(entity))
            {
                if (primitive.Points.Length == 0)
                {
                    continue;
                }
                yield return primitive;
            }
        }
    }

    private static IEnumerable<RenderPrimitive> ConvertEntityToPrimitives(EntityObject entity)
    {
        switch (entity)
        {
            case Line line:
                return new[]
                {
                    new RenderPrimitive(
                        new[]
                        {
                            ToVector2(line.StartPoint),
                            ToVector2(line.EndPoint)
                        },
                        false,
                        PrimitiveKind.Line)
                };

            case Polyline2D poly2D:
                return new[]
                {
                    new RenderPrimitive(
                        poly2D.PolygonalVertexes(CurvePrecision).ToArray(),
                        poly2D.IsClosed,
                        PrimitiveKind.Polyline)
                };

            case Polyline3D poly3D:
                return new[]
                {
                    new RenderPrimitive(
                        poly3D.PolygonalVertexes(CurvePrecision).Select(ToVector2).ToArray(),
                        poly3D.IsClosed,
                        PrimitiveKind.Polyline)
                };

            case Arc arc:
                return new[]
                {
                    new RenderPrimitive(
                        arc.PolygonalVertexes(CurvePrecision).ToArray(),
                        false,
                        PrimitiveKind.Arc)
                };

            case Circle circle:
                return new[]
                {
                    new RenderPrimitive(
                        circle.PolygonalVertexes(CurvePrecision).ToArray(),
                        true,
                        PrimitiveKind.Circle)
                };

            case Ellipse ellipse:
                return new[]
                {
                    new RenderPrimitive(
                        ellipse.PolygonalVertexes(CurvePrecision).ToArray(),
                        true,
                        PrimitiveKind.Ellipse)
                };

            case Spline spline:
                var splinePolyline = spline.ToPolyline2D(CurvePrecision);
                return new[]
                {
                    new RenderPrimitive(
                        splinePolyline.PolygonalVertexes(CurvePrecision).ToArray(),
                        splinePolyline.IsClosed,
                        PrimitiveKind.Polyline)
                };

            case Solid solid:
                return new[]
                {
                    new RenderPrimitive(
                        new[]
                        {
                            solid.FirstVertex,
                            solid.SecondVertex,
                            solid.ThirdVertex,
                            solid.FourthVertex
                        },
                        true,
                        PrimitiveKind.Polyline)
                };

            case Face3D face:
                return new[]
                {
                    new RenderPrimitive(
                        new[]
                        {
                            ToVector2(face.FirstVertex),
                            ToVector2(face.SecondVertex),
                            ToVector2(face.ThirdVertex),
                            ToVector2(face.FourthVertex)
                        },
                        true,
                        PrimitiveKind.Polyline)
                };

            default:
                return Array.Empty<RenderPrimitive>();
        }
    }

    private static KnifeDetectionResult? DetectKnifeBounds(IEnumerable<EntityObject> entities)
    {
        var candidates = new List<KnifeCandidate>();

        foreach (var entity in entities)
        {
            if (TryCreateKnifeCandidate(entity, out var candidate))
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var ordered = candidates
            .OrderByDescending(c => c.Area)
            .ToList();

        var skipDominantFrame = ShouldSkipDominantFrame(ordered);
        var combinationCandidates = new List<KnifeCandidate>();

        for (var i = 0; i < ordered.Count; i++)
        {
            if (skipDominantFrame && i == 0)
            {
                continue;
            }

            combinationCandidates.Add(ordered[i]);
        }

        if (combinationCandidates.Count == 0)
        {
            combinationCandidates.Add(ordered[skipDominantFrame ? 1 : 0]);
        }

        var combined = combinationCandidates.Count > 1;

        var minX = combinationCandidates.Min(c => c.Bounds.MinX);
        var minY = combinationCandidates.Min(c => c.Bounds.MinY);
        var maxX = combinationCandidates.Max(c => c.Bounds.MaxX);
        var maxY = combinationCandidates.Max(c => c.Bounds.MaxY);

        var bounds = new Bounds(minX, minY, maxX, maxY);

        return new KnifeDetectionResult(bounds, ordered.Count, combined, skipDominantFrame);
    }

    private static bool ShouldSkipDominantFrame(IReadOnlyList<KnifeCandidate> ordered)
    {
        if (ordered.Count <= 1)
        {
            return false;
        }

        var largest = ordered[0];
        var second = ordered[1];

        var areaDominates = largest.Area > second.Area * 3.0;
        var tolerance = Math.Max(largest.Bounds.Width, largest.Bounds.Height) * 0.01;
        var containsAllOthers = ordered.Skip(1).All(candidate => BoundsContained(candidate.Bounds, largest.Bounds, tolerance));

        return areaDominates || containsAllOthers;
    }

    private static bool TryCreateKnifeCandidate(EntityObject entity, out KnifeCandidate candidate)
    {
        candidate = default;

        Vector2[]? points = entity switch
        {
            Polyline2D poly2d when poly2d.IsClosed => poly2d.PolygonalVertexes(CurvePrecision).ToArray(),
            Polyline3D poly3d when poly3d.IsClosed => poly3d.PolygonalVertexes(CurvePrecision).Select(ToVector2).ToArray(),
            _ => null
        };

        if (points == null || points.Length < 3)
        {
            return false;
        }

        var bounds = Bounds.FromPoints(points);
        var width = bounds.Width;
        var height = bounds.Height;

        if (width <= double.Epsilon || height <= double.Epsilon)
        {
            return false;
        }

        var area = width * height;
        if (area <= 1000.0)
        {
            return false;
        }

        var ratio = width / height;
        if (ratio >= 6.0 || ratio <= 0.15)
        {
            return false;
        }

        candidate = new KnifeCandidate(area, ratio, bounds);
        return true;
    }

    private static Bounds? CalculateBounds(IEnumerable<RenderPrimitive> primitives)
    {
        var accumulator = new BoundsAccumulator();

        foreach (var primitive in primitives)
        {
            foreach (var point in primitive.Points)
            {
                accumulator.Add(point);
            }
        }

        return accumulator.HasValue ? accumulator.ToBounds() : null;
    }

    private static (int Width, int Height) CalculateImageDimensions(Bounds bounds)
    {
        var width = bounds.Width;
        var height = bounds.Height;

        if (width <= 0 || height <= 0)
        {
            return (TargetDimension, TargetDimension);
        }

        var aspect = width / height;
        if (aspect >= 1.0)
        {
            var heightPixels = Math.Max(1, (int)Math.Ceiling(TargetDimension / aspect));
            return (TargetDimension, heightPixels);
        }
        else
        {
            var widthPixels = Math.Max(1, (int)Math.Ceiling(TargetDimension * aspect));
            return (widthPixels, TargetDimension);
        }
    }

    private static RenderSettings CalculateScale(Bounds bounds, int imageWidth, int imageHeight)
    {
        var width = bounds.Width;
        var height = bounds.Height;

        var usableWidth = imageWidth * Math.Max(0.0, 1.0 - FramePaddingFraction);
        var usableHeight = imageHeight * Math.Max(0.0, 1.0 - FramePaddingFraction);

        var scale = Math.Min(usableWidth / width, usableHeight / height);
        scale = Math.Max(scale, 0.0001);

        var scaledWidth = width * scale;
        var scaledHeight = height * scale;

        var paddingX = (imageWidth - scaledWidth) / 2.0;
        var paddingY = (imageHeight - scaledHeight) / 2.0;

        var translationX = -bounds.MinX;
        var translationY = -bounds.MinY;

        return new RenderSettings(bounds, scale, paddingX, paddingY, imageWidth, imageHeight, translationX, translationY);
    }

    private static IReadOnlyList<RenderPrimitive> FilterPrimitives(IReadOnlyList<RenderPrimitive> primitives, Bounds clipBounds, double scale, out bool fallbackToOriginal)
    {
        fallbackToOriginal = false;
        var filtered = new List<RenderPrimitive>(primitives.Count);
        var tolerance = Math.Max(clipBounds.Width, clipBounds.Height) * 0.001;
        if (scale > 0)
        {
            tolerance = Math.Max(tolerance, 1.5 / scale);
        }

        foreach (var primitive in primitives)
        {
            if (!TryComputeBounds(primitive.Points, out var primitiveBounds))
            {
                continue;
            }

            if (BoundsContained(primitiveBounds, clipBounds, tolerance))
            {
                filtered.Add(primitive);
            }
        }

        if (filtered.Count == 0)
        {
            fallbackToOriginal = true;
            return primitives;
        }

        return filtered;
    }

    private static bool TryComputeBounds(Vector2[] points, out Bounds bounds)
    {
        if (points.Length == 0)
        {
            bounds = default;
            return false;
        }

        bounds = Bounds.FromPoints(points);
        return true;
    }

    private static bool BoundsContained(Bounds inner, Bounds outer, double tolerance) =>
        inner.MinX >= outer.MinX - tolerance &&
        inner.MinY >= outer.MinY - tolerance &&
        inner.MaxX <= outer.MaxX + tolerance &&
        inner.MaxY <= outer.MaxY + tolerance;

    private static SKPoint[] BuildPointArray(Vector2[] points, RenderSettings settings, bool isClosed)
    {
        if (points.Length == 0)
        {
            return Array.Empty<SKPoint>();
        }

        var result = new List<SKPoint>(points.Length);
        Vector2? previous = null;

        foreach (var point in points)
        {
            if (previous.HasValue && AreClose(previous.Value, point))
            {
                continue;
            }

            result.Add(ToPoint(point, settings));
            previous = point;
        }

        if (isClosed && result.Count >= 3 && AreClose(result[0], result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        return result.ToArray();
    }

    private static SKPoint ToPoint(Vector2 value, RenderSettings settings)
    {
        var xUnits = value.X + settings.TranslationX;
        var yUnits = settings.Bounds.Height - (value.Y + settings.TranslationY);
        var x = (float)(xUnits * settings.Scale + settings.PaddingX);
        var y = (float)(yUnits * settings.Scale + settings.PaddingY);
        return new SKPoint(x, y);
    }

    private static SKRect CalculateClipRect(RenderSettings settings, float strokeWidth)
    {
        var topLeft = ToPoint(new Vector2(settings.Bounds.MinX, settings.Bounds.MaxY), settings);
        var bottomRight = ToPoint(new Vector2(settings.Bounds.MaxX, settings.Bounds.MinY), settings);

        var left = Math.Min(topLeft.X, bottomRight.X);
        var top = Math.Min(topLeft.Y, bottomRight.Y);
        var right = Math.Max(topLeft.X, bottomRight.X);
        var bottom = Math.Max(topLeft.Y, bottomRight.Y);

        var margin = Math.Max(strokeWidth / 2f, 0.5f);
        left = Math.Max(0f, left - margin);
        top = Math.Max(0f, top - margin);
        right = Math.Min(settings.ImageWidth, right + margin);
        bottom = Math.Min(settings.ImageHeight, bottom + margin);

        return new SKRect(left, top, right, bottom);
    }

    private static SKRectI CalculateClipRectInt(RenderSettings settings, float strokeWidth)
    {
        var rect = CalculateClipRect(settings, strokeWidth);

        var left = Math.Max(0, (int)Math.Floor(rect.Left));
        var top = Math.Max(0, (int)Math.Floor(rect.Top));
        var right = Math.Min(settings.ImageWidth, (int)Math.Ceiling(rect.Right));
        var bottom = Math.Min(settings.ImageHeight, (int)Math.Ceiling(rect.Bottom));

        if (right <= left)
        {
            right = Math.Min(settings.ImageWidth, left + 1);
        }
        if (bottom <= top)
        {
            bottom = Math.Min(settings.ImageHeight, top + 1);
        }

        return new SKRectI(left, top, right, bottom);
    }

    private static SKImage? TryCreateSubset(SKImage image, RenderSettings settings, float strokeWidth)
    {
        var clipRect = CalculateClipRectInt(settings, strokeWidth);
        if (clipRect.Width <= 0 || clipRect.Height <= 0)
        {
            return null;
        }

        if (clipRect.Left == 0 && clipRect.Top == 0 &&
            clipRect.Width == image.Width && clipRect.Height == image.Height)
        {
            return null;
        }

        return image.Subset(clipRect);
    }

    private static bool AreClose(Vector2 a, Vector2 b)
    {
        const double epsilon = 1e-8;
        return Math.Abs(a.X - b.X) < epsilon && Math.Abs(a.Y - b.Y) < epsilon;
    }

    private static bool AreClose(SKPoint a, SKPoint b)
    {
        const double epsilon = 1e-3;
        return Math.Abs(a.X - b.X) < epsilon && Math.Abs(a.Y - b.Y) < epsilon;
    }

    private static Vector2 ToVector2(Vector3 point) => new(point.X, point.Y);

    private void LogKnifeBounds(string filePath, Bounds bounds, bool knifeDetected, int candidateCount, bool combinedMultiple, bool skippedFrame)
    {
        var label = knifeDetected
            ? combinedMultiple
                ? "Bounding box combinado das facas"
                : "Bounding box da faca principal"
            : "Bounding box global utilizado (faca não encontrada)";

        var message =
            $"{label}: xMin={bounds.MinX.ToString("F3", Culture)}, xMax={bounds.MaxX.ToString("F3", Culture)}, " +
            $"yMin={bounds.MinY.ToString("F3", Culture)}, yMax={bounds.MaxY.ToString("F3", Culture)}, " +
            $"largura={bounds.Width.ToString("F3", Culture)}, altura={bounds.Height.ToString("F3", Culture)}";

        if (knifeDetected)
        {
            message += $", candidatos={candidateCount}";
            if (combinedMultiple)
            {
                message += ", múltiplas facas combinadas";
            }
            if (skippedFrame)
            {
                message += ", moldura descartada";
            }
        }

        _logger.LogInformation("Render DXF {File}: {Message}", filePath, message);
    }

    private void LogScale(string filePath, Bounds bounds, RenderSettings settings)
    {
        var marginX = settings.PaddingX / settings.ImageWidth;
        var marginY = settings.PaddingY / settings.ImageHeight;

        _logger.LogInformation(
            "Render DXF {File}: escala={Scale:F6} px/unid | offset=({OffsetX:F3}, {OffsetY:F3}) | padding≈({PaddingX:F1}%, {PaddingY:F1}%) | área renderizada=({Width:F3} x {Height:F3})",
            filePath,
            settings.Scale,
            settings.TranslationX,
            settings.TranslationY,
            marginX * 100,
            marginY * 100,
            bounds.Width,
            bounds.Height);
    }

    private void LogPrimitiveFilter(string filePath, int originalCount, int filteredCount, bool fallbackToOriginal)
    {
        if (fallbackToOriginal)
        {
            _logger.LogInformation("Render DXF {File}: {Count} primitivas renderizadas (filtro vazio - fallback aplicado).", filePath, originalCount);
            return;
        }

        if (filteredCount == originalCount)
        {
            _logger.LogInformation("Render DXF {File}: {Count} primitivas renderizadas (sem filtro).", filePath, filteredCount);
        }
        else
        {
            _logger.LogInformation("Render DXF {File}: {Filtered} de {Original} primitivas renderizadas (conteúdo fora da faca ignorado).", filePath, filteredCount, originalCount);
        }
    }

    private enum PrimitiveKind
    {
        Line,
        Polyline,
        Arc,
        Circle,
        Ellipse
    }

    private sealed record RenderPrimitive(Vector2[] Points, bool IsClosed, PrimitiveKind Kind);

    private readonly struct KnifeCandidate
    {
        public KnifeCandidate(double area, double ratio, Bounds bounds)
        {
            Area = area;
            Ratio = ratio;
            Bounds = bounds;
        }

        public double Area { get; }
        public double Ratio { get; }
        public Bounds Bounds { get; }
    }

    private readonly struct KnifeDetectionResult
    {
        public KnifeDetectionResult(Bounds bounds, int totalCandidates, bool combinedMultiple, bool skippedDominantFrame)
        {
            Bounds = bounds;
            TotalCandidates = totalCandidates;
            CombinedMultiple = combinedMultiple;
            SkippedDominantFrame = skippedDominantFrame;
        }

        public Bounds Bounds { get; }
        public int TotalCandidates { get; }
        public bool CombinedMultiple { get; }
        public bool SkippedDominantFrame { get; }
    }

    private readonly struct RenderSettings
    {
        public RenderSettings(Bounds bounds, double scale, double paddingX, double paddingY, int imageWidth, int imageHeight, double translationX, double translationY)
        {
            Bounds = bounds;
            Scale = scale;
            PaddingX = paddingX;
            PaddingY = paddingY;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
            TranslationX = translationX;
            TranslationY = translationY;
        }

        public Bounds Bounds { get; }
        public double Scale { get; }
        public double PaddingX { get; }
        public double PaddingY { get; }
        public int ImageWidth { get; }
        public int ImageHeight { get; }
        public double TranslationX { get; }
        public double TranslationY { get; }
    }

    private readonly struct Bounds
    {
        public Bounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        public Bounds Expand(double marginFraction)
        {
            var marginX = Width * marginFraction;
            var marginY = Height * marginFraction;
            return new Bounds(MinX - marginX, MinY - marginY, MaxX + marginX, MaxY + marginY);
        }

        public static Bounds FromPoints(IEnumerable<Vector2> points)
        {
            var accumulator = new BoundsAccumulator();
            foreach (var point in points)
            {
                accumulator.Add(point);
            }

            if (!accumulator.HasValue)
            {
                throw new ArgumentException("A coleção de pontos está vazia.", nameof(points));
            }

            return accumulator.ToBounds();
        }

        public Bounds EnsureMinimumExtent(double minExtent)
        {
            var width = Width;
            var height = Height;

            var minX = MinX;
            var maxX = MaxX;
            if (width < minExtent)
            {
                var centerX = (MinX + MaxX) / 2.0;
                minX = centerX - minExtent / 2.0;
                maxX = centerX + minExtent / 2.0;
            }

            var minY = MinY;
            var maxY = MaxY;
            if (height < minExtent)
            {
                var centerY = (MinY + MaxY) / 2.0;
                minY = centerY - minExtent / 2.0;
                maxY = centerY + minExtent / 2.0;
            }

            return new Bounds(minX, minY, maxX, maxY);
        }
    }

    private sealed class BoundsAccumulator
    {
        private bool _initialized;

        public double MinX { get; private set; }
        public double MaxX { get; private set; }
        public double MinY { get; private set; }
        public double MaxY { get; private set; }

        public bool HasValue => _initialized;

        public void Add(Vector2 point)
        {
            if (!_initialized)
            {
                MinX = MaxX = point.X;
                MinY = MaxY = point.Y;
                _initialized = true;
                return;
            }

            MinX = Math.Min(MinX, point.X);
            MaxX = Math.Max(MaxX, point.X);
            MinY = Math.Min(MinY, point.Y);
            MaxY = Math.Max(MaxY, point.Y);
        }

        public Bounds ToBounds() => new(MinX, MinY, MaxX, MaxY);
    }
}

public sealed class CalibratedRenderResult
{
    public CalibratedRenderResult(
        byte[] data,
        int widthPx,
        int heightPx,
        double scale,
        bool knifeDetected,
        int knifeCandidateCount,
        bool combinedKnife,
        bool skippedDominantFrame,
        bool filterFallbackApplied)
    {
        Data = data;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Scale = scale;
        KnifeDetected = knifeDetected;
        KnifeCandidateCount = knifeCandidateCount;
        CombinedKnife = combinedKnife;
        SkippedDominantFrame = skippedDominantFrame;
        FilterFallbackApplied = filterFallbackApplied;
    }

    public byte[] Data { get; }
    public int WidthPx { get; }
    public int HeightPx { get; }
    public double Scale { get; }
    public bool KnifeDetected { get; }
    public int KnifeCandidateCount { get; }
    public bool CombinedKnife { get; }
    public bool SkippedDominantFrame { get; }
    public bool FilterFallbackApplied { get; }
    public double EffectiveDpi => Scale * 25.4;
}

public readonly struct CalibratedRenderInfo
{
    public CalibratedRenderInfo(int widthPx, int heightPx, double scale)
    {
        WidthPx = widthPx;
        HeightPx = heightPx;
        Scale = scale;
    }

    public int WidthPx { get; }
    public int HeightPx { get; }
    public double Scale { get; }
    public double EffectiveDpi => Scale * 25.4;
}

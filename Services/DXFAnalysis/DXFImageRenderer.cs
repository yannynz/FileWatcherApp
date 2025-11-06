using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using FileWatcherApp.Services.DXFAnalysis.Geometry;
using FileWatcherApp.Services.DXFAnalysis.Models;
using FileWatcherApp.Services.DXFAnalysis.Rendering;
using netDxf;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Renders analyzed DXF geometry into a normalized PNG representation.
/// </summary>
public sealed class DXFImageRenderer
{
    private readonly DXFAnalysisOptions _options;
    private readonly ILogger<DXFImageRenderer> _logger;
    private readonly CalibratedDxfRenderer _calibratedRenderer;

    /// <summary>
    /// Initializes a new instance of the <see cref="DXFImageRenderer"/> class.
    /// </summary>
    public DXFImageRenderer(
        IOptions<DXFAnalysisOptions> options,
        ILogger<DXFImageRenderer> logger,
        CalibratedDxfRenderer calibratedRenderer)
    {
        _options = options.Value;
        _logger = logger;
        _calibratedRenderer = calibratedRenderer;
    }

    /// <summary>
    /// Produces a PNG render of the DXF geometry snapshot.
    /// </summary>
    /// <param name="analysisId">The analysis identifier.</param>
    /// <param name="fileName">The original DXF file name.</param>
    /// <param name="snapshot">Geometry snapshot returned by the analyzer.</param>
    /// <param name="score">Optional score to display in the watermark.</param>
    /// <param name="document">DXF document used for calibrated rendering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered image payload and metadata.</returns>
    public Task<DXFRenderedImage> RenderAsync(
        string analysisId,
        string fileName,
        DXFAnalysisGeometrySnapshot snapshot,
        double? score,
        DxfDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(document);

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var safeName = SanitizeFilename(baseName);

        var renderResult = _calibratedRenderer.Render(
            fileName,
            document,
            cancellationToken,
            (canvas, info) => DrawWatermark(canvas, info.WidthPx, info.HeightPx, safeName, score));

        var bytes = renderResult.Data;
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var renderedImage = new DXFRenderedImage
        {
            SafeName = safeName,
            OriginalFileName = Path.GetFileName(fileName),
            LocalPath = null,
            WidthPx = renderResult.WidthPx,
            HeightPx = renderResult.HeightPx,
            Dpi = renderResult.EffectiveDpi,
            Data = bytes,
            Sha256 = sha256
        };

        return Task.FromResult(renderedImage);
    }

    private static string SanitizeFilename(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (invalid.Contains(ch) || char.IsWhiteSpace(ch))
            {
                sb.Append('_');
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private void DrawWatermark(SKCanvas canvas, int widthPx, int heightPx, string name, double? score)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(0x77, 0x77, 0x77),
            TextSize = Math.Min(32, Math.Max(14, widthPx * 0.02f)),
            IsAntialias = true
        };

        var text = score.HasValue ? $"{name} | score={score:0.##}" : name;
        var metrics = paint.FontMetrics;
        var x = 16f;
        var y = heightPx - metrics.Bottom - 16f;
        canvas.DrawText(text, x, y, paint);
    }
}

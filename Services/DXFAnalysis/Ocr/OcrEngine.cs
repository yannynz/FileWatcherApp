/*
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileWatcherApp.Services.DXFAnalysis.Ocr;

/// <summary>
/// Provides OCR capabilities over rendered DXF images using Tesseract CLI.
/// </summary>
public sealed class OcrEngine
{
    private readonly DXFAnalysisOptions _options;
    private readonly ILogger<OcrEngine> _logger;

    public OcrEngine(IOptions<DXFAnalysisOptions> options, ILogger<OcrEngine> logger)
    {
        _options = options.Value ?? new DXFAnalysisOptions();
        _logger = logger;
    }

    /// <summary>
    /// Runs OCR over the rendered DXF image. Returns null on failure or empty output.
    /// </summary>
    public async Task<string?> RecognizeAsync(DXFRenderedImage rendered, CancellationToken cancellationToken)
    {
        var ocrOptions = _options.Ocr ?? new DXFAnalysisOptions.OcrOptions();
        if (!ocrOptions.Enabled)
        {
            return null;
        }

        if (rendered?.Data is null || rendered.Data.Length == 0)
        {
            _logger.LogWarning("OCR skipped: rendered image is empty");
            return null;
        }

        var tesseract = string.IsNullOrWhiteSpace(ocrOptions.TesseractPath) ? "tesseract" : ocrOptions.TesseractPath;
        var tempPng = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

        try
        {
            await File.WriteAllBytesAsync(tempPng, rendered.Data, cancellationToken);

            var args = new List<string> { Quote(tempPng), "stdout" };
            if (!string.IsNullOrWhiteSpace(ocrOptions.Languages))
            {
                args.Add("-l");
                args.Add(ocrOptions.Languages);
            }

            if (ocrOptions.PageSegMode > 0)
            {
                args.Add("--psm");
                args.Add(ocrOptions.PageSegMode.ToString());
            }

            if (!string.IsNullOrWhiteSpace(ocrOptions.AdditionalArgs))
            {
                args.Add(ocrOptions.AdditionalArgs);
            }

            var psi = new ProcessStartInfo
            {
                FileName = tesseract,
                Arguments = string.Join(" ", args),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                _logger.LogWarning("OCR skipped: failed to start tesseract (path={Path})", tesseract);
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);
            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("OCR failed for {File}: exitCode={Code} stderr={Stderr}", rendered.OriginalFileName, process.ExitCode, stderr);
                return null;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("OCR stderr for {File}: {Stderr}", rendered.OriginalFileName, stderr);
            }

            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
        {
            _logger.LogWarning(ex, "OCR failed for {File}", rendered?.OriginalFileName ?? "n/d");
            return null;
        }
        finally
        {
            TryDelete(tempPng);
        }
    }

    private static string Quote(string value) => $"\"{value}\"";

    private void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to delete temp file {Path}", path);
        }
    }
}
*/
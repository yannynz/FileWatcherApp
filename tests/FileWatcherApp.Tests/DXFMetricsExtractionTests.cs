using System;
using System.IO;
using FileWatcherApp.Services.DXFAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using netDxf;
using Xunit;

namespace FileWatcherApp.Tests;

public sealed class DXFMetricsExtractionTests
{
    private static string FixturesFolder => Path.Combine(AppContext.BaseDirectory, "resources", "dxf");

    [Fact]
    public void ThreePtFixture_ComputesThreePtMetrics()
    {
        var options = Options.Create(ComplexityCalibrationTests.CreateOptions());
        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);

        var path = Path.Combine(FixturesFolder, "calibration_threept_complexity.dxf");
        var doc = DxfDocument.Load(path);
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.True(snapshot.Metrics.TotalThreePtLength > 0);
        Assert.Equal(18, snapshot.Metrics.ThreePtSegmentCount);
        Assert.True(snapshot.Metrics.RequiresManualThreePtHandling);
        Assert.True(snapshot.Metrics.ThreePtCutRatio > 1.0);
    }

    [Fact]
    public void ZipperFixture_ExtractsClassificationAndMaterials()
    {
        var options = Options.Create(ComplexityCalibrationTests.CreateOptions());
        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);

        var path = Path.Combine(FixturesFolder, "calibration_zipper_complexity.dxf");
        var doc = DxfDocument.Load(path);
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.NotNull(snapshot.Metrics.Serrilha);
        var serrilha = snapshot.Metrics.Serrilha!;
        Assert.True(serrilha.Classification?.Zipper > 0, "Expected zipper classification");
        Assert.Contains("adesivo", snapshot.Metrics.Quality.SpecialMaterials?.ToArray() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        Assert.True(snapshot.Metrics.Quality.ClosedLoops >= 10);
    }

    [Fact]
    public void LowComplexityFixture_TracksLoopsAndCutLength()
    {
        var options = Options.Create(ComplexityCalibrationTests.CreateOptions());
        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);

        var path = Path.Combine(FixturesFolder, "calibration_low_complexity.dxf");
        var doc = DxfDocument.Load(path);
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.True(snapshot.Metrics.TotalCutLength >= 2500);
        Assert.Equal(2, snapshot.Metrics.Quality.ClosedLoops);
        Assert.Contains("adesivo", snapshot.Metrics.Quality.SpecialMaterials?.ToArray() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }
}

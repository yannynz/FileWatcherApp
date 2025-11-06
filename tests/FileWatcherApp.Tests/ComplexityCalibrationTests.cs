using System;
using System.Collections.Generic;
using System.IO;
using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using netDxf;
using Xunit;
using Xunit.Abstractions;

namespace FileWatcherApp.Tests;

public sealed class ComplexityCalibrationTests
{
    private readonly ITestOutputHelper _output;

    public ComplexityCalibrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static IEnumerable<object[]> CalibrationTargets()
    {
        yield return new object[] { "NR 120184.dxf", 5.0 };
        yield return new object[] { "NR119812.dxf", 3.0 };
        yield return new object[] { Path.Combine("tests", "resources", "dxf", "calibration_low_complexity.dxf"), 1.6 };
        yield return new object[] { Path.Combine("tests", "resources", "dxf", "calibration_zipper_complexity.dxf"), 3.6 };
        yield return new object[] { Path.Combine("tests", "resources", "dxf", "calibration_threept_complexity.dxf"), 5.0 };
    }

    [Theory]
    [MemberData(nameof(CalibrationTargets))]
    public void PrintScoreBreakdown(string relativePath, double expectedScore)
    {
        var filePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
        if (!File.Exists(filePath))
        {
            _output.WriteLine($"Skipping calibration target (missing fixture): {filePath}");
            return;
        }

        var options = Options.Create(CreateOptions());
        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);
        var scorer = new ComplexityScorer(options, NullLogger<ComplexityScorer>.Instance);

        var doc = LoadDocumentWithFallback(filePath);
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);
        var result = scorer.Compute(snapshot.Metrics);

        _output.WriteLine($"Target: {expectedScore:0.##} | Actual: {result.Score:0.##}");
        _output.WriteLine($"  TotalCutLength={snapshot.Metrics.TotalCutLength:0.##} mm");
        _output.WriteLine($"  BBoxArea={snapshot.Metrics.BboxArea:0.##} mm² | BBoxPerimeter={snapshot.Metrics.BboxPerimeter:0.##} mm");
        _output.WriteLine($"  Curves={snapshot.Metrics.NumCurves} | MinArcRadius={snapshot.Metrics.MinArcRadius:0.###} mm");
        _output.WriteLine($"  ClosedLoops={snapshot.Metrics.Quality.ClosedLoops} | Variety={snapshot.Metrics.Quality.ClosedLoopsByType?.Count ?? 0}");
        _output.WriteLine($"  DelicateArcCount={snapshot.Metrics.Quality.DelicateArcCount} | Density={snapshot.Metrics.Quality.DelicateArcDensity:G6}");
        _output.WriteLine($"  SpecialMaterials={string.Join(", ", snapshot.Metrics.Quality.SpecialMaterials?.ToArray() ?? Array.Empty<string>())}");
        _output.WriteLine($"  3pt: length={snapshot.Metrics.TotalThreePtLength:0.##} mm | segments={snapshot.Metrics.ThreePtSegmentCount} | ratio={snapshot.Metrics.ThreePtCutRatio:0.###} | manual={snapshot.Metrics.RequiresManualThreePtHandling}");
        if (snapshot.Metrics.Serrilha is { } serrilha)
        {
            _output.WriteLine($"  Serrilha total={serrilha.TotalCount} | categorias={serrilha.Classification?.DistinctCategories ?? 0} | tipos={serrilha.DistinctSemanticTypes} | lâminas={serrilha.DistinctBladeCodes}");
        }
        foreach (var explanation in result.Explanations)
        {
            _output.WriteLine($" - {explanation}");
        }

        Assert.InRange(result.Score, expectedScore - 0.25, expectedScore + 0.25);
    }

    internal static DXFAnalysisOptions CreateOptions()
    {
        return new DXFAnalysisOptions
        {
            LayerMapping = new Dictionary<string, string[]>
            {
                ["corte"] = new[] { "^CUT$", "^CORTE$", "CUTTER", "^FACA_PONTES$", "^LAYOUT_PONTES$" },
                ["vinco"] = new[] { "VINCO", "FOLD", "SCORE" },
                ["serrilha"] = new[] { "SERRILHA", "PERF" },
                ["serrilha_mista"] = new[] { "MISTA" },
                ["trespt"] = new[] { "3PT", "THREE_PT", "THREE-POINT", "VINCO3PT", "VINCO_3PT" }
            },
            SpecialMaterialLayerMapping = new Dictionary<string, string[]>
            {
                ["adesivo"] = new[] { "ADES", "ADESIVO", "LAM_ADESIVO" },
                ["borracha"] = new[] { "BORRACHA", "GOMA" }
            },
            SerrilhaSymbols = new List<DXFAnalysisOptions.SerrilhaSymbol>
            {
                new()
                {
                    SymbolNamePattern = "^SERRILHA_FINA$",
                    SemanticType = "serrilha_fina",
                    BladeCode = "FINA",
                    DefaultLength = 120,
                    DefaultToothCount = 40
                },
                new()
                {
                    SymbolNamePattern = "^SERRILHA_MISTA_.*$",
                    AttributePattern = "LAMINA\\s*MISTA",
                    SemanticType = "serrilha_mista",
                    BladeCode = "MISTA",
                    DefaultToothCount = 28
                },
                new()
                {
                    SymbolNamePattern = "^SERRILHA_ZIPPER$",
                    SemanticType = "serrilha_zipper",
                    BladeCode = "ZIPPER"
                }
            },
            SerrilhaTextSymbols = new List<DXFAnalysisOptions.SerrilhaTextSymbol>
            {
                new()
                {
                    TextPattern = "(?<code>[A-Z])\\s*[-=]\\s*(?<descriptor>[0-9]+(?:x[0-9\\.,]+)?)\\s+(?<length>[0-9]+[\\.,]?[0-9]*)(?:\\s+(?<teeth>[0-9]+)\\s*(?:d|dentes?)?)?",
                    SemanticType = "serrilha",
                    SemanticTypeGroup = "code",
                    SemanticTypeFormat = "serrilha_{value}",
                    BladeCodeGroup = "descriptor",
                    UppercaseBladeCode = false,
                    UppercaseSemanticType = true,
                    AllowMultipleMatches = true,
                    LengthGroup = "length",
                    ToothCountGroup = "teeth"
                }
            },
            Scoring = new DXFAnalysisOptions.ScoringThresholds
            {
                TotalCutLength = 2000,
                TotalCutLengthWeight = 0.8,
                NumCurves = 60,
                NumCurvesWeight = 0.5,
                BonusIntersections = 30,
                BonusIntersectionsWeight = 0.25,
                MinRadius = new DXFAnalysisOptions.MinRadiusScoringOptions
                {
                    DangerThreshold = 0.35,
                    NeutralThreshold = 1.0,
                    PenaltyWeight = 0.37,
                    CorteSecoAdjustment = 0.3
                },
                Serrilha = new DXFAnalysisOptions.SerrilhaScoringOptions
                {
                    PresenceWeight = 0.6,
                    MistaWeight = 0.7,
                    MultiTypeWeight = 0.35,
                    MultiTypeThreshold = 2,
                    ManualBladeWeight = 0.4,
                    ManualBladeCodes = new List<string> { "MANUAL" },
                    TravadaWeight = 0.7,
                    ZipperWeight = 0.6,
                    DiversityWeight = 0.4,
                    DiversityThreshold = 2,
                    DistinctBladeWeight = 0.25,
                    DistinctBladeThreshold = 2,
                    CorteSecoMultiTypeWeight = 0.35
                },
                ClosedLoops = new DXFAnalysisOptions.ClosedLoopScoringOptions
                {
                    CountThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 2, Weight = 0.15 },
                        new() { Threshold = 4, Weight = 0.2 },
                        new() { Threshold = 8, Weight = 0.2 },
                        new() { Threshold = 20, Weight = 0.25 },
                        new() { Threshold = 40, Weight = 0.8 }
                    },
                    VarietyThreshold = 2,
                    VarietyWeight = 0.3,
                    DensityThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 4.5e-5, Weight = 0.25 },
                        new() { Threshold = 5.5e-5, Weight = 0.8 }
                    }
                },
                ThreePt = new DXFAnalysisOptions.ThreePtScoringOptions
                {
                    LengthThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 150, Weight = 0.35 },
                        new() { Threshold = 300, Weight = 0.45 }
                    },
                    SegmentThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 6, Weight = 0.3 },
                        new() { Threshold = 12, Weight = 0.3 }
                    },
                    RatioThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 0.08, Weight = 0.3 },
                        new() { Threshold = 0.15, Weight = 0.35 }
                    },
                    ManualHandlingWeight = 0.45
                },
                CurveDensity = new DXFAnalysisOptions.CurveDensityScoringOptions
                {
                    DensityThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 0.0005, Weight = 0.2 },
                        new() { Threshold = 0.00055, Weight = 0.5 }
                    },
                    DelicateArcCountThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                    {
                        new() { Threshold = 12, Weight = 0.25 },
                        new() { Threshold = 28, Weight = 0.35 }
                    }
                },
                Materials = new DXFAnalysisOptions.MaterialScoringOptions
                {
                    DefaultWeight = 0.5,
                    Overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["adesivo"] = 0.5,
                        ["borracha"] = 0.4
                    }
                }
            }
        };
    }

    private static DxfDocument LoadDocumentWithFallback(string path)
    {
        try
        {
            return DxfDocument.Load(path);
        }
        catch (netDxf.IO.DxfVersionNotSupportedException) when (TryLoadAfterHeaderUpgrade(path, out var upgraded))
        {
            return upgraded!;
        }
    }

    private static bool TryLoadAfterHeaderUpgrade(string path, out DxfDocument? document)
    {
        document = null;

        var bytes = File.ReadAllBytes(path);
        var marker = System.Text.Encoding.ASCII.GetBytes("AC1014");
        var replacement = System.Text.Encoding.ASCII.GetBytes("AC1015");
        var index = IndexOf(bytes, marker);
        if (index < 0)
        {
            return false;
        }

        Array.Copy(replacement, 0, bytes, index, replacement.Length);
        try
        {
            using var stream = new MemoryStream(bytes);
            document = DxfDocument.Load(stream);
            return true;
        }
        catch
        {
            document = null;
            return false;
        }
    }

    private static int IndexOf(byte[] source, byte[] pattern)
    {
        for (var i = 0; i <= source.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}

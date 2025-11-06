using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using Xunit;

namespace FileWatcherApp.Tests;

public sealed class SerrilhaAnalysisTests
{
    private static string GetFixture(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "resources", "dxf", name);
    }

    [Fact]
    public void Analyzer_RecognizesSerrilhaFromTextAnnotations()
    {
        var options = new DXFAnalysisOptions
        {
            SerrilhaTextSymbols = new List<DXFAnalysisOptions.SerrilhaTextSymbol>
            {
                new()
                {
                    TextPattern = @"(?<code>[A-Z])\s*[-=]\s*(?<descriptor>[0-9]+(?:x[0-9\.,]+)?)\s+(?<length>[0-9]+[\.,]?[0-9]*)(?:\s+(?<teeth>[0-9]+)\s*(?:d|dentes?)?)?",
                    SemanticType = "serrilha",
                    SemanticTypeGroup = "code",
                    SemanticTypeFormat = "serrilha_{value}",
                    BladeCodeGroup = "descriptor",
                    UppercaseBladeCode = false,
                    LengthGroup = "length",
                    ToothCountGroup = "teeth",
                    AllowMultipleMatches = true
                }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var doc = new DxfDocument();
        doc.Entities.Add(new netDxf.Entities.Text("X=2x1 23,8 12d Y-10x0.4 11,5 24 dentes", new netDxf.Vector3(0, 0, 0), 12));

        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.NotNull(snapshot.Metrics.Serrilha);
        var summary = snapshot.Metrics.Serrilha!;
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.NotNull(summary.TotalEstimatedLength);
        Assert.InRange(summary.TotalEstimatedLength!.Value, 34.0, 36.0);

        Assert.Collection(
            summary.Entries.OrderBy(e => e.SemanticType),
            entry =>
            {
                Assert.Equal("serrilha_X", entry.SemanticType);
                Assert.Equal("2x1", entry.BladeCode);
                Assert.Equal(1, entry.Count);
                Assert.Contains("X=2x1 23,8 12d", entry.SymbolNames);
                Assert.InRange(entry.EstimatedLength!.Value, 23.7, 23.9);
                Assert.InRange(entry.EstimatedToothCount!.Value, 11.5, 12.5);
            },
            entry =>
            {
                Assert.Equal("serrilha_Y", entry.SemanticType);
                Assert.Equal("10x0.4", entry.BladeCode);
                Assert.Equal(1, entry.Count);
                Assert.Contains(entry.SymbolNames, s => s.Contains("Y-10x0.4 11,5 24", StringComparison.OrdinalIgnoreCase));
                Assert.InRange(entry.EstimatedLength!.Value, 11.4, 11.6);
                Assert.InRange(entry.EstimatedToothCount!.Value, 23.5, 24.5);
            });
    }
    [Fact]
    public void Analyzer_MapsConfiguredSerrilhaSymbols()
    {
        var options = new DXFAnalysisOptions
        {
            SerrilhaSymbols = new List<DXFAnalysisOptions.SerrilhaSymbol>
            {
                new()
                {
                    SymbolNamePattern = "^SERRILHA_FINA$",
                    SemanticType = "serrilha_fina",
                    BladeCode = "FINA",
                    DefaultLength = 120,
                    DefaultToothCount = 40
                }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var doc = DxfDocument.Load(GetFixture("serrilha_fina.dxf"));
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.NotNull(snapshot.Metrics.Serrilha);
        var summary = snapshot.Metrics.Serrilha!;
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.Single(summary.Entries);

        var entry = summary.Entries[0];
        Assert.Equal("serrilha_fina", entry.SemanticType);
        Assert.Equal("FINA", entry.BladeCode);
        Assert.Equal(1, entry.Count);
        Assert.True(entry.EstimatedLength.HasValue);
        Assert.True(entry.EstimatedToothCount.HasValue);
    }

    [Fact]
    public void Analyzer_UsesAttributePatternForMatching()
    {
        var options = new DXFAnalysisOptions
        {
            SerrilhaSymbols = new List<DXFAnalysisOptions.SerrilhaSymbol>
            {
                new()
                {
                    SymbolNamePattern = "^SERRILHA_MISTA_.*$",
                    AttributePattern = "LAMINA\\s*MISTA",
                    SemanticType = "serrilha_mista",
                    BladeCode = "MISTA",
                    DefaultToothCount = 28
                }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var doc = DxfDocument.Load(GetFixture("serrilha_mista.dxf"));
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.NotNull(snapshot.Metrics.Serrilha);
        var summary = snapshot.Metrics.Serrilha!;
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(0, summary.UnknownCount);
        var entry = Assert.Single(summary.Entries);
        Assert.Equal("serrilha_mista", entry.SemanticType);
        Assert.Equal(2, entry.Count);
        Assert.Equal("MISTA", entry.BladeCode);
        Assert.True(entry.SymbolNames.Count >= 2);
    }

    [Fact]
    public void Analyzer_DetectsCorteSecoPairs_FromParallelSegments()
    {
        var options = new DXFAnalysisOptions
        {
            LayerMapping = new Dictionary<string, string[]>
            {
                ["serrilha"] = new[] { "^SERR$" }
            },
            SerrilhaTextSymbols = new List<DXFAnalysisOptions.SerrilhaTextSymbol>
            {
                new()
                {
                    TextPattern = @"(?<code>[A-Z])\s*[-=]\s*(?<descriptor>[0-9]+(?:x[0-9\.,]+)?)\s+(?<length>[0-9]+[\.,]?[0-9]*)",
                    SemanticType = "serrilha",
                    SemanticTypeGroup = "code",
                    SemanticTypeFormat = "serrilha_{value}",
                    BladeCodeGroup = "descriptor",
                    AllowMultipleMatches = true
                }
            },
            CorteSeco = new DXFAnalysisOptions.CorteSecoOptions
            {
                Enabled = true,
                MaxOffsetMillimeters = 0.6,
                MinOverlapRatio = 0.6,
                MinLengthMillimeters = 10,
                MinPairCount = 1,
                TargetLayerTypes = new List<string> { "serrilha" }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var serrLayer = new Layer("SERR");
        var doc = new DxfDocument();
        doc.Layers.Add(serrLayer);

        doc.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(60, 0, 0)) { Layer = serrLayer });
        doc.Entities.Add(new Line(new netDxf.Vector3(0, 0.3, 0), new netDxf.Vector3(60, 0.3, 0)) { Layer = serrLayer });

        doc.Entities.Add(new netDxf.Entities.Text("X=2x1 25", new netDxf.Vector3(5, 5, 0), 12));
        doc.Entities.Add(new netDxf.Entities.Text("Y-2x1 25", new netDxf.Vector3(5, -5, 0), 12));

        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        var serrilha = snapshot.Metrics.Serrilha;
        Assert.NotNull(serrilha);
        Assert.True(serrilha!.IsCorteSeco);
        Assert.NotNull(serrilha.CorteSecoPairs);
        Assert.NotEmpty(serrilha.CorteSecoPairs!);
        Assert.Contains(serrilha.CorteSecoBladeCodes!, code => string.Equals(code, "2x1", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyzer_ReportsUnknownSymbols()
    {
        var options = new DXFAnalysisOptions
        {
            SerrilhaSymbols = new List<DXFAnalysisOptions.SerrilhaSymbol>
            {
                new()
                {
                    SymbolNamePattern = "^SERRILHA_FINA$",
                    SemanticType = "serrilha_fina"
                }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var doc = DxfDocument.Load(GetFixture("serrilha_nao_map.dxf"));
        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.NotNull(snapshot.Metrics.Serrilha);
        var summary = snapshot.Metrics.Serrilha!;
        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(1, summary.UnknownCount);
        Assert.NotNull(summary.UnknownSymbols);
        Assert.Contains("SYM_DESCONHECIDO", summary.UnknownSymbols!);
    }

    [Fact]
    public void EnrichSerrilhaSummary_ClassifiesTravadaSynonyms()
    {
        var analyzer = new DXFAnalyzer(Options.Create(new DXFAnalysisOptions()), NullLogger<DXFAnalyzer>.Instance);
        var summary = new DXFSerrilhaSummary
        {
            Entries = new List<DXFSerrilhaEntry>
            {
                new()
                {
                    SemanticType = "serrilha_ranhura",
                    BladeCode = "RANHURA",
                    Count = 1,
                    SymbolNames = new HashSet<string> { "RANHURA" }
                },
                new()
                {
                    SemanticType = "serrilha_sel_cola",
                    BladeCode = "SEL-COLA",
                    Count = 2,
                    SymbolNames = new HashSet<string> { "SEL COLA" }
                }
            },
            TotalCount = 3
        };

        var method = typeof(DXFAnalyzer).GetMethod("EnrichSerrilhaSummary", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(analyzer, new object[] { summary });

        Assert.NotNull(summary.Classification);
        Assert.Equal(3, summary.Classification!.Travada);
        Assert.Equal(2, summary.DistinctSemanticTypes);
        Assert.Equal(2, summary.DistinctBladeCodes);
        Assert.True(summary.Classification!.DistinctCategories >= 1);
    }

    [Fact]
    public void ComplexityScorer_UsesSerrilhaSummaryWeights()
    {
        var scorer = new ComplexityScorer(
            Options.Create(new DXFAnalysisOptions
            {
                Scoring = new DXFAnalysisOptions.ScoringThresholds
                {
                    Serrilha = new DXFAnalysisOptions.SerrilhaScoringOptions
                    {
                        PresenceWeight = 0.75,
                        MistaWeight = 1.25,
                        MultiTypeWeight = 0.5,
                        ManualBladeWeight = 0.5,
                        ManualBladeCodes = new List<string> { "MANUAL" }
                    }
                }
            }),
            NullLogger<ComplexityScorer>.Instance);

        var metrics = new DXFMetrics
        {
            Serrilha = new DXFSerrilhaSummary
            {
                TotalCount = 3,
                Entries = new List<DXFSerrilhaEntry>
                {
                    new()
                    {
                        SemanticType = "serrilha_mista",
                        BladeCode = "MANUAL",
                        Count = 2,
                        SymbolNames = new HashSet<string> { "SERRILHA_MISTA_A", "SERRILHA_MISTA_B" }
                    },
                    new()
                    {
                        SemanticType = "serrilha_fina",
                        BladeCode = "FINA",
                        Count = 1,
                        SymbolNames = new HashSet<string> { "SERRILHA_FINA" }
                    }
                }
            }
        };

        var result = scorer.Compute(metrics);
        Assert.Equal(1.25, result.Score);
        Assert.Contains(result.Explanations, e => e.Contains("Serrilha detectada", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Explanations, e => e.Contains("manual", StringComparison.OrdinalIgnoreCase));
    }
}

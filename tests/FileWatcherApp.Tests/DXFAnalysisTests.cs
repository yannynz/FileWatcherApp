using System;
using System.Collections.Generic;
using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.DXFAnalysis.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using netDxf;
using netDxf.Entities;
using netDxf.Tables;
using Xunit;

namespace FileWatcherApp.Tests;

public class DXFAnalysisTests
{
    [Fact]
    public void Analyzer_ComputesBasicMetrics_ForSimpleLine()
    {
        var options = Options.Create(new DXFAnalysisOptions
        {
            LayerMapping = new Dictionary<string, string[]>
            {
                ["corte"] = new[] { "^CUT$" }
            }
        });

        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);

        var doc = new DxfDocument();
        var layer = new Layer("CUT");
        doc.Layers.Add(layer);
        doc.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(10, 0, 0))
        {
            Layer = layer
        });

        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.Equal("mm", snapshot.Metrics.Unit);
        Assert.Equal(10, snapshot.Metrics.TotalCutLength, 3);
        Assert.Equal(1, snapshot.Metrics.LineCount);
        Assert.Equal(0, snapshot.Metrics.NumCurves);
        Assert.Equal(2, snapshot.Metrics.NumNodes);
        Assert.Equal(0, snapshot.Metrics.NumIntersections);
        Assert.Single(snapshot.Metrics.LayerStats);
        Assert.Equal("corte", snapshot.Metrics.LayerStats[0].Type);
        Assert.True(snapshot.Metrics.Extents.MaxX > snapshot.Metrics.Extents.MinX);
    }

    [Fact]
    public void ComplexityScorer_AwardsScorePerThresholds()
    {
        var options = Options.Create(new DXFAnalysisOptions
        {
            Scoring = new DXFAnalysisOptions.ScoringThresholds
            {
                TotalCutLength = 2000,
                NumCurves = 60,
                MinArcRadiusMax = 1.0,
                BonusIntersections = 30
            }
        });

        var scorer = new ComplexityScorer(options, NullLogger<ComplexityScorer>.Instance);

        var metrics = new DXFMetrics
        {
            TotalCutLength = 2500,
            NumCurves = 80,
            MinArcRadius = 0.5,
            NumIntersections = 42,
            LayerStats = new List<DXFLayerStats>
            {
                new() { Name = "SERRILHA", Type = "serrilha", EntityCount = 2, TotalLength = 40 },
                new() { Name = "3PT", Type = "trespt", EntityCount = 1, TotalLength = 10 }
            }
        };

        var score = scorer.Compute(metrics);

        Assert.Equal(4, score.Score);
        Assert.Contains(score.Explanations, e => e.Contains("serrilha", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(score.Explanations, e => e.Contains("3pt", StringComparison.OrdinalIgnoreCase) || e.Contains("layer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComplexityScorer_HandlesMinRadiusNeutralZoneAndCorteSeco()
    {
        var options = Options.Create(new DXFAnalysisOptions
        {
            Scoring = new DXFAnalysisOptions.ScoringThresholds
            {
                TotalCutLength = double.MaxValue,
                NumCurves = int.MaxValue,
                MinArcRadiusMax = 0.0,
                BonusIntersections = int.MaxValue,
                MinRadius = new DXFAnalysisOptions.MinRadiusScoringOptions
                {
                    DangerThreshold = 0.3,
                    NeutralThreshold = 1.0,
                    PenaltyWeight = 1.0,
                    CorteSecoAdjustment = -0.5
                },
                Serrilha = new DXFAnalysisOptions.SerrilhaScoringOptions
                {
                    PresenceWeight = 0.0,
                    MistaWeight = 0.0,
                    MultiTypeWeight = 0.0,
                    ManualBladeWeight = 0.0
                }
            }
        });

        var scorer = new ComplexityScorer(options, NullLogger<ComplexityScorer>.Instance);

        var baseMetrics = new DXFMetrics
        {
            MinArcRadius = 0.25
        };

        var penaltyResult = scorer.Compute(baseMetrics);
        Assert.Equal(1.0, penaltyResult.Score);
        Assert.Contains(penaltyResult.Explanations, e => e.Contains("Raio mínimo delicado", StringComparison.OrdinalIgnoreCase));

        var corteSecoMetrics = new DXFMetrics
        {
            MinArcRadius = 0.25,
            Serrilha = new DXFSerrilhaSummary
            {
                TotalCount = 2,
                IsCorteSeco = true,
                Entries = new List<DXFSerrilhaEntry>
                {
                    new()
                    {
                        SemanticType = "serrilha_A",
                        BladeCode = "A",
                        Count = 1,
                        SymbolNames = new HashSet<string> { "A" }
                    },
                    new()
                    {
                        SemanticType = "serrilha_B",
                        BladeCode = "B",
                        Count = 1,
                        SymbolNames = new HashSet<string> { "B" }
                    }
                },
                CorteSecoPairs = new List<DXFCorteSecoPair>
                {
                    new()
                    {
                        LayerA = "L1",
                        LayerB = "L2",
                        TypeA = "serrilha",
                        TypeB = "serrilha",
                        OverlapMillimeters = 20,
                        OffsetMillimeters = 0.3,
                        AngleDifferenceDegrees = 0
                    }
                }
            }
        };

        var corteSecoResult = scorer.Compute(corteSecoMetrics);
        Assert.Equal(0.0, corteSecoResult.Score);
        Assert.Contains(corteSecoResult.Explanations, e => e.Contains("ajuste tratado separadamente", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(corteSecoResult.Explanations, e => e.Contains("redução", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyzer_DetectsAdesivoLayerWithSelColaPattern()
    {
        var options = new DXFAnalysisOptions
        {
            SpecialMaterialLayerMapping = new Dictionary<string, string[]>
            {
                ["adesivo"] = new[] { "SEL[_\\s-]*COLA" }
            }
        };

        var opts = Options.Create(options);
        var analyzer = new DXFAnalyzer(opts, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(opts, NullLogger<DXFPreprocessor>.Instance);

        var doc = new DxfDocument();
        var layer = new Layer("SEL-COLA");
        doc.Layers.Add(layer);
        doc.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(25, 0, 0)) { Layer = layer });

        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        var materials = snapshot.Metrics.Quality.SpecialMaterials;
        Assert.NotNull(materials);
        Assert.Contains("adesivo", materials!);
    }

    [Fact]
    public void Analyzer_EstimatesClosedLoops_FromDisconnectedSegments()
    {
        var options = Options.Create(new DXFAnalysisOptions());
        var analyzer = new DXFAnalyzer(options, NullLogger<DXFAnalyzer>.Instance);
        var preprocessor = new DXFPreprocessor(options, NullLogger<DXFPreprocessor>.Instance);

        var doc = new DxfDocument();
        var layer = new Layer("FACA_PONTES");
        doc.Layers.Add(layer);

        doc.Entities.Add(new Line(new netDxf.Vector3(0, 0, 0), new netDxf.Vector3(50, 0, 0)) { Layer = layer });
        doc.Entities.Add(new Line(new netDxf.Vector3(50, 0, 0), new netDxf.Vector3(50, 30, 0)) { Layer = layer });
        doc.Entities.Add(new Line(new netDxf.Vector3(50, 30, 0), new netDxf.Vector3(0, 30, 0)) { Layer = layer });
        doc.Entities.Add(new Line(new netDxf.Vector3(0, 30, 0), new netDxf.Vector3(0, 0, 0)) { Layer = layer });

        var quality = preprocessor.Preprocess(doc);
        var snapshot = analyzer.Analyze(doc, quality);

        Assert.True(snapshot.Metrics.Quality.ClosedLoops >= 1);
        Assert.NotNull(snapshot.Metrics.Quality.ClosedLoopsByType);
    }

    [Fact]
    public void ComplexityScorer_AppliesDanglingEndsThresholds()
    {
        var options = Options.Create(new DXFAnalysisOptions
        {
            Scoring = new DXFAnalysisOptions.ScoringThresholds
            {
                DanglingEndThresholds = new List<DXFAnalysisOptions.ThresholdWeight>
                {
                    new() { Threshold = 100, Weight = 0.5 },
                    new() { Threshold = 300, Weight = 0.5 }
                }
            }
        });

        var scorer = new ComplexityScorer(options, NullLogger<ComplexityScorer>.Instance);
        var metrics = new DXFMetrics
        {
            Quality = new DXFQualityMetrics
            {
                DanglingEnds = 350
            }
        };

        var result = scorer.Compute(metrics);
        Assert.Equal(1.0, result.Score);
        Assert.Contains(result.Explanations, e => e.Contains("Muitos cortes isolados", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComplexityScorer_AppliesSmallPieceAdjustment()
    {
        var options = Options.Create(new DXFAnalysisOptions
        {
            Scoring = new DXFAnalysisOptions.ScoringThresholds
            {
                Serrilha = new DXFAnalysisOptions.SerrilhaScoringOptions
                {
                    PresenceWeight = 0,
                    SmallPieceMaxCount = 2,
                    SmallPieceMaxTotalLength = 80,
                    SmallPieceAdjustment = -0.4
                }
            }
        });

        var scorer = new ComplexityScorer(options, NullLogger<ComplexityScorer>.Instance);

        var summary = new DXFSerrilhaSummary
        {
            TotalCount = 2,
            TotalEstimatedLength = 60,
            Entries = new List<DXFSerrilhaEntry>
            {
                new()
                {
                    SemanticType = "serrilha_fina",
                    Count = 1,
                    EstimatedLength = 30,
                    SymbolNames = new HashSet<string> { "FINA" }
                },
                new()
                {
                    SemanticType = "serrilha_travada",
                    Count = 1,
                    EstimatedLength = 30,
                    SymbolNames = new HashSet<string> { "TRAV" }
                }
            },
            Classification = new DXFSerrilhaClassificationMetrics()
        };

        var metrics = new DXFMetrics { Serrilha = summary };
        var result = scorer.Compute(metrics);

        Assert.Equal(-0.4, result.Score);
        Assert.Contains(result.Explanations, e => e.Contains("Serrilha curta", StringComparison.OrdinalIgnoreCase));
    }
}

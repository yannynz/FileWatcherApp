using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FileWatcherApp.Services.DXFAnalysis.Models;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Applies deterministic heuristics to transform DXF metrics into a 0-5 complexity score.
/// </summary>
public sealed class ComplexityScorer
{
    private readonly DXFAnalysisOptions _options;
    private readonly ILogger<ComplexityScorer> _logger;
    private static readonly string[] ColaHintDefaults = new[] { "COLA", "SER_COL", "SER-COL", "SER COL" };

    /// <summary>
    /// Initializes a new instance of the <see cref="ComplexityScorer"/> class.
    /// </summary>
    public ComplexityScorer(IOptions<DXFAnalysisOptions> options, ILogger<ComplexityScorer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Computes the deterministic score and explanations for the provided metrics.
    /// </summary>
    public ComplexityScoreResult Compute(DXFMetrics metrics)
    {
        var thresholds = _options.Scoring ?? new DXFAnalysisOptions.ScoringThresholds();
        var explanations = new List<string>();
        double score = 0.0;

        LogConfigOnce(thresholds);

        score += ApplyCutLengthScore(metrics, thresholds, explanations);
        score += ApplyCurveCountScore(metrics, thresholds, explanations);
        score += ApplyMinRadiusScore(metrics, thresholds, explanations);
        score += ApplySerrilhaScore(metrics, thresholds.Serrilha ?? new DXFAnalysisOptions.SerrilhaScoringOptions(), explanations);
        score += ApplyClosedLoopScore(metrics, thresholds.ClosedLoops ?? new DXFAnalysisOptions.ClosedLoopScoringOptions(), explanations);
        score += ApplyCurveDensityScore(metrics, thresholds.CurveDensity ?? new DXFAnalysisOptions.CurveDensityScoringOptions(), explanations);
        score += ApplyMaterialScore(metrics, thresholds.Materials ?? new DXFAnalysisOptions.MaterialScoringOptions(), explanations);
        score += ApplyThreePtScore(metrics, thresholds.ThreePt ?? new DXFAnalysisOptions.ThreePtScoringOptions(), explanations);
        score += ApplyDanglingEndsScore(metrics, thresholds, explanations);
        score += ApplyIntersectionsScore(metrics, thresholds, explanations);

        score = Math.Clamp(score, 0.0, 5.0);
        return new ComplexityScoreResult(Math.Round(score, 2), explanations);
    }

    private double ApplyCutLengthScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ScoringThresholds thresholds,
        List<string> explanations)
    {
        if (thresholds.TotalCutLengthWeight <= 0)
        {
            return 0;
        }

        if (MeetsThreshold(metrics.TotalCutLength, thresholds.TotalCutLength))
        {
            explanations.Add($"Comprimento de corte alto ({metrics.TotalCutLength:0.##} mm >= {thresholds.TotalCutLength:0.##} mm): +{thresholds.TotalCutLengthWeight:0.##}");
            return thresholds.TotalCutLengthWeight;
        }

        return 0;
    }

    private double ApplyCurveCountScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ScoringThresholds thresholds,
        List<string> explanations)
    {
        double contribution = 0;

        if (thresholds.NumCurvesWeight > 0 && MeetsThreshold(metrics.NumCurves, thresholds.NumCurves))
        {
            contribution += thresholds.NumCurvesWeight;
            explanations.Add($"Densidade de curvas elevada ({metrics.NumCurves} >= {thresholds.NumCurves}): +{thresholds.NumCurvesWeight:0.##}");
        }
        else
        {
            Console.WriteLine($"[CURVES] Sem peso base: curves={metrics.NumCurves} threshold={thresholds.NumCurves} weight={thresholds.NumCurvesWeight}");
        }

        foreach (var extra in OrderThresholds(thresholds.NumCurvesExtraThresholds))
        {
            if (Math.Abs(extra.Weight) < 1e-6)
            {
                continue;
            }

            if (metrics.NumCurves >= extra.Threshold - 1e-6)
            {
                contribution += extra.Weight;
                explanations.Add($"Curvas abundantes ({metrics.NumCurves} >= {FormatNumber(extra.Threshold, preferInteger: true)}): +{extra.Weight:0.##}");
                Console.WriteLine($"[CURVES] Extra threshold hit: curves={metrics.NumCurves} >= {extra.Threshold} weight={extra.Weight}");
            }
        }

        if (thresholds.NumCurvesStep > 0 &&
            thresholds.NumCurvesStepWeight > 0 &&
            thresholds.NumCurvesStepMaxContribution > 0 &&
            metrics.NumCurves > thresholds.NumCurves)
        {
            var extraCurves = Math.Max(0.0, metrics.NumCurves - thresholds.NumCurves);
            var steps = extraCurves / Math.Max(thresholds.NumCurvesStep, 1);
            var fractionalWeight = steps * thresholds.NumCurvesStepWeight;
            fractionalWeight = Math.Min(fractionalWeight, thresholds.NumCurvesStepMaxContribution);
            Console.WriteLine($"[CURVES] Step contribution: curves={metrics.NumCurves} extra={extraCurves:0.##} steps={steps:0.##} weight={fractionalWeight:0.##}");

            if (fractionalWeight > 1e-6)
            {
                contribution += fractionalWeight;
                explanations.Add($"Excesso de curvas ({metrics.NumCurves} > {thresholds.NumCurves}) adiciona +{fractionalWeight:0.##}");
            }
        }

        return contribution;
    }

    private double ApplyDanglingEndsScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ScoringThresholds thresholds,
        List<string> explanations)
    {
        if (metrics.Quality is null)
        {
            return 0;
        }

        double contribution = 0;
        var dangling = metrics.Quality.DanglingEnds;

        foreach (var threshold in OrderThresholds(thresholds.DanglingEndThresholds))
        {
            if (Math.Abs(threshold.Weight) < 1e-6)
            {
                continue;
            }

            if (dangling >= threshold.Threshold - 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Muitos cortes isolados ({dangling} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
            }
        }

        return contribution;
    }

    private double ApplyMinRadiusScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ScoringThresholds thresholds,
        List<string> explanations)
    {
        var options = thresholds.MinRadius ?? new DXFAnalysisOptions.MinRadiusScoringOptions();
        double contribution = 0;
        var serrilha = metrics.Serrilha;
        bool corteSeco = serrilha?.IsCorteSeco == true;
        var pairCount = serrilha?.CorteSecoPairs?.Count ?? 0;

        if (metrics.MinArcRadius > 0 && metrics.MinArcRadius <= options.DangerThreshold + 1e-6)
        {
            if (!corteSeco)
            {
                if (options.PenaltyWeight > 0)
                {
                    contribution += options.PenaltyWeight;
                    explanations.Add($"Raio mínimo delicado ({metrics.MinArcRadius:0.##} mm <= {options.DangerThreshold:0.##} mm): +{options.PenaltyWeight:0.##}");
                }
            }
            else
            {
                explanations.Add($"Raio mínimo delicado ({metrics.MinArcRadius:0.##} mm) com corte seco - ajuste tratado separadamente");
            }
        }

        if (corteSeco && Math.Abs(options.CorteSecoAdjustment) > 1e-6)
        {
            contribution += options.CorteSecoAdjustment;
            var verb = options.CorteSecoAdjustment > 0 ? "bônus" : "redução";
            var suffix = pairCount > 0 ? $" ({pairCount} pares)" : string.Empty;
            explanations.Add($"Corte seco detectado{suffix}: {verb} de {Math.Abs(options.CorteSecoAdjustment):0.##} ponto(s)");
        }

        if (corteSeco)
        {
            foreach (var threshold in OrderThresholds(options.CorteSecoPairThresholds))
            {
                if (Math.Abs(threshold.Weight) < 1e-6)
                {
                    continue;
                }

                if (pairCount >= threshold.Threshold - 1e-6)
                {
                    contribution += threshold.Weight;
                    explanations.Add($"Corte seco intenso ({pairCount} pares >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                }
            }
        }

        return contribution;
    }

    private double ApplySerrilhaScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.SerrilhaScoringOptions options,
        List<string> explanations)
    {
        double contribution = 0;
        var summary = metrics.Serrilha;

        if (summary is not null && summary.TotalCount > 0)
        {
            if (options.PresenceWeight > 0)
            {
                contribution += options.PresenceWeight;
                explanations.Add($"Serrilha detectada ({summary.TotalCount} símbolo(s)): +{options.PresenceWeight:0.##}");
            }

            foreach (var threshold in OrderThresholds(options.TotalCountThresholds))
            {
                if (Math.Abs(threshold.Weight) < 1e-6)
                {
                    continue;
                }

                if (summary.TotalCount >= threshold.Threshold - 1e-6)
                {
                    contribution += threshold.Weight;
                    explanations.Add($"Serrilha volumosa ({summary.TotalCount} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                }
            }

            var classification = summary.Classification;
            if (classification is not null)
            {
                if (classification.Mista > 0 && options.MistaWeight > 0)
                {
                    contribution += options.MistaWeight;
                    explanations.Add($"Serrilha mista ({classification.Mista} ocorrência(s)): +{options.MistaWeight:0.##}");
                }

                foreach (var threshold in OrderThresholds(options.MistaCountThresholds))
                {
                    if (Math.Abs(threshold.Weight) < 1e-6)
                    {
                        continue;
                    }

                    if (classification.Mista >= threshold.Threshold - 1e-6)
                    {
                        contribution += threshold.Weight;
                        explanations.Add($"Serrilha mista intensa ({classification.Mista} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                    }
                }

                if (classification.Travada > 0 && options.TravadaWeight > 0)
                {
                    contribution += options.TravadaWeight;
                    explanations.Add($"Serrilha travada ({classification.Travada} ocorrência(s)): +{options.TravadaWeight:0.##}");
                }

                foreach (var threshold in OrderThresholds(options.TravadaCountThresholds))
                {
                    if (Math.Abs(threshold.Weight) < 1e-6)
                    {
                        continue;
                    }

                    if (classification.Travada >= threshold.Threshold - 1e-6)
                    {
                        contribution += threshold.Weight;
                        explanations.Add($"Serrilha travada intensa ({classification.Travada} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                    }
                }

                if (classification.Zipper > 0 && options.ZipperWeight > 0)
                {
                    contribution += options.ZipperWeight;
                    explanations.Add($"Serrilha zipper ({classification.Zipper} ocorrência(s)): +{options.ZipperWeight:0.##}");
                }

                if (classification.DistinctCategories >= options.DiversityThreshold && options.DiversityWeight > 0)
                {
                    contribution += options.DiversityWeight;
                    explanations.Add($"Diversidade de serrilhas ({classification.DistinctCategories} categorias): +{options.DiversityWeight:0.##}");
                }
            }

            if (summary.DistinctSemanticTypes >= options.MultiTypeThreshold && options.MultiTypeWeight > 0)
            {
                var types = summary.Entries
                    .Select(e => e.SemanticType)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6);
                contribution += options.MultiTypeWeight;
                explanations.Add($"Múltiplos tipos de serrilha ({string.Join(", ", types)}): +{options.MultiTypeWeight:0.##}");
            }

            if (summary.DistinctBladeCodes >= options.DistinctBladeThreshold && options.DistinctBladeWeight > 0)
            {
                contribution += options.DistinctBladeWeight;
                explanations.Add($"Várias lâminas ({summary.DistinctBladeCodes} códigos): +{options.DistinctBladeWeight:0.##}");
            }

            if (options.ManualBladeWeight > 0)
            {
                var manualBlades = DetectManualBlades(summary, options)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (manualBlades.Count > 0)
                {
                    contribution += options.ManualBladeWeight;
                    explanations.Add($"Serrilha manual identificada (códigos: {string.Join(", ", manualBlades)}): +{options.ManualBladeWeight:0.##}");
                }
            }

            int colaCount = 0;
            var colaHintsSource = options.ColaSemanticHints?.Count > 0
                ? (IEnumerable<string>)options.ColaSemanticHints
                : ColaHintDefaults;
            var colaHints = colaHintsSource
                .Where(static hint => !string.IsNullOrWhiteSpace(hint))
                .ToArray();
            if (colaHints.Length > 0 && summary.Entries is not null)
            {
                foreach (var entry in summary.Entries)
                {
                    if (ContainsHint(entry.SemanticType, colaHints) ||
                        ContainsHint(entry.BladeCode, colaHints))
                    {
                        colaCount += entry.Count > 0 ? entry.Count : 1;
                    }
                }
            }

            if (colaCount > 0 && Math.Abs(options.ColaWeight) > 1e-6)
            {
                contribution += options.ColaWeight;
                explanations.Add($"Serrilha cola ({colaCount} ocorrência(s)): +{options.ColaWeight:0.##}");
            }

            foreach (var threshold in OrderThresholds(options.ColaCountThresholds))
            {
                if (Math.Abs(threshold.Weight) < 1e-6)
                {
                    continue;
                }

                if (colaCount >= threshold.Threshold - 1e-6)
                {
                    contribution += threshold.Weight;
                    explanations.Add($"Serrilha cola intensa ({colaCount} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                }
            }

            if (summary.IsCorteSeco == true &&
                summary.DistinctSemanticTypes >= options.MultiTypeThreshold &&
                options.CorteSecoMultiTypeWeight > 0)
            {
                contribution += options.CorteSecoMultiTypeWeight;
                var pairs = summary.CorteSecoPairs?.Count ?? 0;
                var pairLabel = pairs > 0 ? $"{pairs} pares" : "sobreposição";
                explanations.Add($"Corte seco entre serrilhas ({pairLabel}): +{options.CorteSecoMultiTypeWeight:0.##}");
            }

            var totalEstimatedLength = summary.TotalEstimatedLength
                ?? summary.Entries?
                    .Where(static e => e.EstimatedLength.HasValue && e.EstimatedLength.Value > 0)
                    .Sum(static e => e.EstimatedLength!.Value) ?? 0.0;

            if (options.SmallPieceAdjustment != 0 &&
                options.SmallPieceMaxCount > 0 &&
                options.SmallPieceMaxTotalLength > 0 &&
                summary.TotalCount > 0 &&
                summary.TotalCount <= options.SmallPieceMaxCount &&
                totalEstimatedLength > 0 &&
                totalEstimatedLength <= options.SmallPieceMaxTotalLength + 1e-6)
            {
                contribution += options.SmallPieceAdjustment;
                var verb = options.SmallPieceAdjustment > 0 ? "bônus" : "redução";
                explanations.Add($"Serrilha curta ({summary.TotalCount} peça(s), {totalEstimatedLength:0.##} mm): {verb} de {Math.Abs(options.SmallPieceAdjustment):0.##}");
            }

            return contribution;
        }

        bool serrilhaLayer = HasLayer(metrics.LayerStats, "serrilha");
        bool serrilhaMistaLayer = HasLayer(metrics.LayerStats, "serrilha_mista") || HasLayer(metrics.LayerStats, "serrilhamista");

        if (serrilhaLayer && options.PresenceWeight > 0)
        {
            contribution += options.PresenceWeight;
            explanations.Add($"Serrilha identificada via layer: +{options.PresenceWeight:0.##}");
        }

        if (serrilhaMistaLayer && options.MistaWeight > 0)
        {
            contribution += options.MistaWeight;
            explanations.Add($"Serrilha mista identificada via layer: +{options.MistaWeight:0.##}");
        }

        return contribution;
    }

    private double ApplyClosedLoopScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ClosedLoopScoringOptions options,
        List<string> explanations)
    {
        if (metrics.Quality is null || metrics.Quality.ClosedLoops <= 0)
        {
            return 0;
        }

        double contribution = 0;
        var loops = metrics.Quality.ClosedLoops;

        foreach (var threshold in OrderThresholds(options.CountThresholds))
        {
            if (MeetsThreshold(loops, threshold.Threshold) && Math.Abs(threshold.Weight) > 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Bocas abundantes ({loops} loops >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
            }
        }

        var area = Math.Max(metrics.BboxArea, 1e-6);
        var density = loops / area;
        foreach (var threshold in OrderThresholds(options.DensityThresholds))
        {
            if (density >= threshold.Threshold - 1e-9 && Math.Abs(threshold.Weight) > 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Densidade de bocas {density:0.###E0} >= {threshold.Threshold:0.###E0}: +{threshold.Weight:0.##}");
            }
        }

        var variety = metrics.Quality.ClosedLoopsByType?.Count ?? 0;
        if (variety >= options.VarietyThreshold && options.VarietyWeight > 0)
        {
            contribution += options.VarietyWeight;
            var topTypes = metrics.Quality.ClosedLoopsByType!
                .OrderByDescending(pair => pair.Value)
                .Select(pair => $"{pair.Key}:{pair.Value}")
                .Take(4);
            explanations.Add($"Variedade de bocas ({variety} tipos - {string.Join(", ", topTypes)}): +{options.VarietyWeight:0.##}");
        }

        return contribution;
    }

    private double ApplyCurveDensityScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.CurveDensityScoringOptions options,
        List<string> explanations)
    {
        if (metrics.Quality is null)
        {
            return 0;
        }

        double contribution = 0;
        var density = metrics.Quality.DelicateArcDensity;

        if (density > 0)
        {
            foreach (var threshold in OrderThresholds(options.DensityThresholds))
            {
                if (density >= threshold.Threshold - 1e-6 && Math.Abs(threshold.Weight) > 1e-6)
                {
                    contribution += threshold.Weight;
                    explanations.Add($"Densidade de curvas delicadas {FormatPercent(density)} >= {FormatPercent(threshold.Threshold)}: +{threshold.Weight:0.##}");
                }
            }
        }

        var delicateCount = metrics.Quality.DelicateArcCount;
        if (delicateCount > 0)
        {
            foreach (var threshold in OrderThresholds(options.DelicateArcCountThresholds))
            {
                if (delicateCount >= threshold.Threshold - 1e-6 && Math.Abs(threshold.Weight) > 1e-6)
                {
                    contribution += threshold.Weight;
                    explanations.Add($"Muitas curvas delicadas ({delicateCount} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
                }
            }
        }

        return contribution;
    }

    private double ApplyMaterialScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.MaterialScoringOptions options,
        List<string> explanations)
    {
        var materials = metrics.Quality?.SpecialMaterials;
        if (materials is null || materials.Count == 0)
        {
            return 0;
        }

        double contribution = 0;
        foreach (var material in materials)
        {
            if (string.IsNullOrWhiteSpace(material))
            {
                continue;
            }

            var normalized = material.Trim();
            if (normalized.Length == 0)
            {
                continue;
            }

            var weight = options.DefaultWeight;
            if (options.Overrides != null && options.Overrides.TryGetValue(normalized, out var overrideWeight))
            {
                weight = overrideWeight;
            }
            else if (options.KeywordOverrides is not null && options.KeywordOverrides.Count > 0)
            {
                foreach (var kvp in options.KeywordOverrides)
                {
                    if (string.IsNullOrWhiteSpace(kvp.Key))
                    {
                        continue;
                    }

                    if (normalized.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        weight = kvp.Value;
                        break;
                    }
                }
            }

            if (Math.Abs(weight) < 1e-6)
            {
                continue;
            }

            contribution += weight;
            explanations.Add($"Material especial ({normalized}) demanda cuidado: +{weight:0.##}");
        }

        return contribution;
    }

    private double ApplyThreePtScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ThreePtScoringOptions options,
        List<string> explanations)
    {
        if (metrics.TotalThreePtLength <= 0 && metrics.ThreePtSegmentCount <= 0)
        {
            return 0;
        }

        double contribution = 0;
        var length = metrics.TotalThreePtLength;

        foreach (var threshold in OrderThresholds(options.LengthThresholds))
        {
            if (MeetsThreshold(length, threshold.Threshold) && Math.Abs(threshold.Weight) > 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Vinco 3pt extenso ({length:0.##} mm >= {threshold.Threshold:0.##} mm): +{threshold.Weight:0.##}");
            }
        }

        var segments = metrics.ThreePtSegmentCount;
        foreach (var threshold in OrderThresholds(options.SegmentThresholds))
        {
            if (segments >= threshold.Threshold - 1e-6 && Math.Abs(threshold.Weight) > 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Muitos segmentos 3pt ({segments} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
            }
        }

        var ratio = metrics.ThreePtCutRatio;
        foreach (var threshold in OrderThresholds(options.RatioThresholds))
        {
            if (ratio >= threshold.Threshold - 1e-6 && Math.Abs(threshold.Weight) > 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Vinco 3pt dominante ({FormatPercent(ratio)} >= {FormatPercent(threshold.Threshold)}): +{threshold.Weight:0.##}");
            }
        }

        if (metrics.RequiresManualThreePtHandling && options.ManualHandlingWeight > 0)
        {
            contribution += options.ManualHandlingWeight;
            explanations.Add($"Vinco 3pt exige dobra manual: +{options.ManualHandlingWeight:0.##}");
        }

        return contribution;
    }

    private double ApplyIntersectionsScore(
        DXFMetrics metrics,
        DXFAnalysisOptions.ScoringThresholds thresholds,
        List<string> explanations)
    {
        double contribution = 0;

        if (thresholds.BonusIntersectionsWeight > 0 &&
            MeetsThreshold(metrics.NumIntersections, thresholds.BonusIntersections))
        {
            contribution += thresholds.BonusIntersectionsWeight;
            explanations.Add($"Muitas interseções ({metrics.NumIntersections} >= {thresholds.BonusIntersections}): +{thresholds.BonusIntersectionsWeight:0.##}");
        }

        foreach (var threshold in OrderThresholds(thresholds.IntersectionThresholds))
        {
            if (Math.Abs(threshold.Weight) < 1e-6)
            {
                continue;
            }

            if (metrics.NumIntersections >= threshold.Threshold - 1e-6)
            {
                contribution += threshold.Weight;
                explanations.Add($"Interseções complexas ({metrics.NumIntersections} >= {FormatNumber(threshold.Threshold, preferInteger: true)}): +{threshold.Weight:0.##}");
            }
        }

        return contribution;
    }

    private static bool HasLayer(IEnumerable<DXFLayerStats> stats, string expected)
    {
        return stats.Any(ls => string.Equals(ls.Type, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<DXFAnalysisOptions.ThresholdWeight> OrderThresholds(IEnumerable<DXFAnalysisOptions.ThresholdWeight> thresholds)
    {
        return thresholds?.Where(t => t is not null).OrderBy(t => t!.Threshold) ?? Enumerable.Empty<DXFAnalysisOptions.ThresholdWeight>();
    }

    private static bool MeetsThreshold(double value, double threshold) => value >= threshold - 1e-6;

    private static string FormatNumber(double value, bool preferInteger = false)
    {
        if (preferInteger || Math.Abs(value - Math.Round(value)) < 1e-6)
        {
            return ((int)Math.Round(value)).ToString();
        }

        return value.ToString("0.##");
    }

    private static string FormatPercent(double value)
    {
        return $"{value * 100:0.0}%";
    }

    private void LogConfigOnce(DXFAnalysisOptions.ScoringThresholds thresholds)
    {
        if (_configLogged || !_logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        _configLogged = true;

        var serrilha = thresholds.Serrilha ?? new DXFAnalysisOptions.SerrilhaScoringOptions();
        var minRadius = thresholds.MinRadius ?? new DXFAnalysisOptions.MinRadiusScoringOptions();

        _logger.LogInformation(
            "[SCORER] Pesos carregados | NumCurvesWeight={NumCurvesWeight} ExtraThresholds={ExtraThresholds} Step=({NumCurvesStep},{NumCurvesStepWeight},{NumCurvesStepMaxContribution}) " +
            "DanglingThresholds={DanglingThresholds} MinRadiusDanger={MinRadiusDanger} SerrilhaPresence={SerrilhaPresence} ColaWeight={ColaWeight}",
            thresholds.NumCurvesWeight,
            thresholds.NumCurvesExtraThresholds?.Count ?? 0,
            thresholds.NumCurvesStep,
            thresholds.NumCurvesStepWeight,
            thresholds.NumCurvesStepMaxContribution,
            thresholds.DanglingEndThresholds?.Count ?? 0,
            minRadius.DangerThreshold,
            serrilha.PresenceWeight,
            serrilha.ColaWeight);

        Console.WriteLine(
            $"[SCORER-CONFIG] NumCurvesWeight={thresholds.NumCurvesWeight} ExtraThresholds={thresholds.NumCurvesExtraThresholds?.Count ?? 0} " +
            $"Step=({thresholds.NumCurvesStep},{thresholds.NumCurvesStepWeight},{thresholds.NumCurvesStepMaxContribution}) " +
            $"Dangling={thresholds.DanglingEndThresholds?.Count ?? 0} MinRadiusDanger={minRadius.DangerThreshold} SerrilhaPresence={serrilha.PresenceWeight} ColaWeight={serrilha.ColaWeight}");
    }

    private static bool _configLogged;

    private static bool ContainsHint(string? value, IReadOnlyList<string> hints)
    {
        if (string.IsNullOrWhiteSpace(value) || hints.Count == 0)
        {
            return false;
        }

        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint))
            {
                continue;
            }

            if (value.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private IEnumerable<string> DetectManualBlades(
        DXFSerrilhaSummary summary,
        DXFAnalysisOptions.SerrilhaScoringOptions options)
    {
        foreach (var entry in summary.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.BladeCode))
            {
                continue;
            }

            if (options.ManualBladeCodes.Count > 0)
            {
                if (options.ManualBladeCodes.Any(code => string.Equals(code, entry.BladeCode, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return entry.BladeCode;
                }
            }
            else if (entry.BladeCode.Contains("MANUAL", StringComparison.OrdinalIgnoreCase) ||
                     entry.BladeCode.Contains("MANUA", StringComparison.OrdinalIgnoreCase))
            {
                yield return entry.BladeCode;
            }
        }
    }
}

/// <summary>
/// Holds the resulting score payload.
/// </summary>
public sealed class ComplexityScoreResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComplexityScoreResult"/> class.
    /// </summary>
    public ComplexityScoreResult(double score, IReadOnlyList<string> explanations)
    {
        Score = score;
        Explanations = explanations;
    }

    /// <summary>Gets the computed score.</summary>
    public double Score { get; }

    /// <summary>Gets the activated explanations.</summary>
    public IReadOnlyList<string> Explanations { get; }
}

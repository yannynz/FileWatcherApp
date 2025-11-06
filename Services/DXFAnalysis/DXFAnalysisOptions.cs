using System.Text.RegularExpressions;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Represents immutable configuration for the DXF deterministic complexity engine.
/// </summary>
public sealed class DXFAnalysisOptions
{
    /// <summary>
    /// Gets or sets the folder that the watcher monitors for DXF files.
    /// </summary>
    public string WatchFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the output folder where rendered PNG images will be stored.
    /// </summary>
    public string OutputImageFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the request queue name used to consume work.
    /// </summary>
    public string RabbitQueueRequest { get; set; } = "facas.analysis.request";

    /// <summary>
    /// Gets or sets the queue name used to publish analysis results.
    /// </summary>
    public string RabbitQueueResult { get; set; } = "facas.analysis.result";

    /// <summary>
    /// Gets or sets the units assumed when the DXF document omits INSUNITS.
    /// </summary>
    public string DefaultUnit { get; set; } = "mm";

    /// <summary>
    /// Gets or sets the dots per inch for rendered PNG images.
    /// </summary>
    public int ImageDpi { get; set; } = 300;

    /// <summary>
    /// Gets or sets the padding in document units used when rendering images.
    /// </summary>
    public double ImagePadding { get; set; } = 20.0;

    /// <summary>
    /// Gets or sets the degree of parallelism for the worker.
    /// </summary>
    public int Parallelism { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Gets or sets a value indicating whether files with identical hash should be reprocessed.
    /// </summary>
    public bool ReprocessSameHash { get; set; }

    /// <summary>
    /// Gets or sets the tolerance used to treat min curve radius as zero.
    /// </summary>
    public double MinCurveRadiusTolerance { get; set; } = 0.01;

    /// <summary>
    /// Gets or sets the maximum gap tolerated when snapping line endpoints during preprocessing.
    /// </summary>
    public double GapTolerance { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets the tolerance for considering two segments overlapping.
    /// </summary>
    public double OverlapTolerance { get; set; } = 0.05;

    /// <summary>
    /// Gets or sets the radius threshold (in mm) used to marcar arcos delicados.
    /// </summary>
    public double DelicateArcRadiusThreshold { get; set; } = 0.5;

    /// <summary>
    /// Gets or sets the maximum error allowed when tessellating curves.
    /// </summary>
    public double ChordTolerance { get; set; } = 0.2;

    /// <summary>
    /// Gets or sets the timeout for DXF parsing.
    /// </summary>
    public TimeSpan ParseTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the timeout for PNG rendering.
    /// </summary>
    public TimeSpan RenderTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the optional folder used to persist analysis cache entries.
    /// </summary>
    public string CacheFolder { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the renderer should persist a PNG copy locally.
    /// </summary>
    public bool PersistLocalImageCopy { get; set; }

    /// <summary>
    /// Gets or sets cache-related toggles (ex.: bypass).
    /// </summary>
    public CacheOptions Cache { get; set; } = new();

    /// <summary>
    /// Gets or sets the semantic version string emitted in result payloads.
    /// </summary>
    public string Version { get; set; } = "complexity-engine/1.1.0";

    /// <summary>
    /// Gets or sets a value indicating whether produced scores should be ignored by downstream systems.
    /// </summary>
    public bool ShadowMode { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ connection information.
    /// </summary>
    public RabbitMqConnectionOptions RabbitMq { get; set; } = new();

    /// <summary>
    /// Gets or sets the storage configuration used to persist rendered images.
    /// </summary>
    public DXFImageStorageOptions ImageStorage { get; set; } = new();

    /// <summary>
    /// Gets or sets the mapping that associates DXF layers to semantic types.
    /// </summary>
    public Dictionary<string, string[]> LayerMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the mapping used to identificar materiais especiais (ex.: adesivo).
    /// </summary>
    public Dictionary<string, string[]> SpecialMaterialLayerMapping { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the collection of serrilha symbol definitions used to detect blade types via block inserts.
    /// </summary>
    public List<SerrilhaSymbol> SerrilhaSymbols { get; set; } = new();

    /// <summary>
    /// Gets or sets the scoring thresholds.
    /// </summary>
    public ScoringThresholds Scoring { get; set; } = new();

    /// <summary>
    /// Gets or sets the instrumentation options.
    /// </summary>
    public TelemetryOptions Telemetry { get; set; } = new();

    /// <summary>
    /// Gets or sets the corte seco detection options.
    /// </summary>
    public CorteSecoOptions CorteSeco { get; set; } = new();

    /// <summary>
    /// Builds a layer mapping lookup that uses compiled regular expressions.
    /// </summary>
    /// <returns>A dictionary of semantic type to regex list.</returns>
    public Dictionary<string, List<Regex>> BuildLayerRegexLookup()
    {
        var lookup = new Dictionary<string, List<Regex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in LayerMapping)
        {
            var list = new List<Regex>();
            foreach (var pattern in kvp.Value)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                list.Add(regex);
            }
            lookup[kvp.Key] = list;
        }

        return lookup;
    }

    /// <summary>
    /// Builds a lookup mapping materiais especiais para regex de layer.
    /// </summary>
    /// <returns>A dictionary of material name to regex list.</returns>
    public Dictionary<string, List<Regex>> BuildSpecialMaterialRegexLookup()
    {
        var lookup = new Dictionary<string, List<Regex>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in SpecialMaterialLayerMapping)
        {
            if (kvp.Value is null || kvp.Value.Length == 0)
            {
                continue;
            }

            var list = new List<Regex>();
            foreach (var pattern in kvp.Value)
            {
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                list.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));
            }

            if (list.Count > 0)
            {
                lookup[kvp.Key] = list;
            }
        }

        return lookup;
    }

    /// <summary>
    /// Builds a lookup of serrilha symbol matchers keyed by semantic type.
    /// </summary>
    /// <returns>A dictionary of semantic type to matcher list.</returns>
    public Dictionary<string, List<SerrilhaSymbolMatcher>> BuildSerrilhaSymbolLookup()
    {
        var lookup = new Dictionary<string, List<SerrilhaSymbolMatcher>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in SerrilhaSymbols)
        {
            if (symbol is null) continue;
            if (string.IsNullOrWhiteSpace(symbol.SemanticType)) continue;
            if (string.IsNullOrWhiteSpace(symbol.SymbolNamePattern)) continue;

            var matcher = symbol.ToMatcher();
            if (!lookup.TryGetValue(symbol.SemanticType, out var list))
            {
                list = new List<SerrilhaSymbolMatcher>();
                lookup[symbol.SemanticType] = list;
            }

            list.Add(matcher);
        }

        return lookup;
    }

    /// <summary>
    /// Builds a lookup of serrilha text matchers keyed by semantic type.
    /// </summary>
    /// <returns>A dictionary of semantic type to matcher list.</returns>
    public Dictionary<string, List<SerrilhaTextSymbolMatcher>> BuildSerrilhaTextLookup()
    {
        var lookup = new Dictionary<string, List<SerrilhaTextSymbolMatcher>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in SerrilhaTextSymbols)
        {
            if (symbol is null) continue;
            if (string.IsNullOrWhiteSpace(symbol.SemanticType)) continue;
            if (string.IsNullOrWhiteSpace(symbol.TextPattern)) continue;

            var matcher = symbol.ToMatcher();
            if (!lookup.TryGetValue(symbol.SemanticType, out var list))
            {
                list = new List<SerrilhaTextSymbolMatcher>();
                lookup[symbol.SemanticType] = list;
            }

            list.Add(matcher);
        }

        return lookup;
    }

    /// <summary>
    /// RabbitMQ connection configuration.
    /// </summary>
    public sealed class RabbitMqConnectionOptions
    {
        /// <summary>Gets or sets the host name.</summary>
        public string HostName { get; set; } = "localhost";

        /// <summary>Gets or sets the connection port.</summary>
        public int Port { get; set; } = 5672;

        /// <summary>Gets or sets the user name.</summary>
        public string UserName { get; set; } = "guest";

        /// <summary>Gets or sets the password.</summary>
        public string Password { get; set; } = "guest";

        /// <summary>Gets or sets the virtual host.</summary>
        public string VirtualHost { get; set; } = "/";

        /// <summary>Gets or sets whether automatic recovery is enabled.</summary>
        public bool AutomaticRecoveryEnabled { get; set; } = true;

        /// <summary>Gets or sets the requested heartbeat interval in seconds.</summary>
        public int RequestedHeartbeatSeconds { get; set; } = 30;

        /// <summary>Gets or sets the prefetch count for consumers.</summary>
        public ushort PrefetchCount { get; set; } = 4;
    }

    /// <summary>
    /// Represents scoring configuration.
    /// </summary>
    public sealed class ScoringThresholds
    {
        /// <summary>Gets or sets the minimum total cut length (in mm) considered complex.</summary>
        public double TotalCutLength { get; set; } = 2000.0;

        /// <summary>Gets or sets the weight added when the total cut length excede o limiar.</summary>
        public double TotalCutLengthWeight { get; set; } = 1.0;

        /// <summary>Gets or sets the minimum number of curves considered complex.</summary>
        public int NumCurves { get; set; } = 60;

        /// <summary>Gets or sets the weight added quando a contagem de curvas excede o limiar.</summary>
        public double NumCurvesWeight { get; set; } = 1.0;

        /// <summary>Gets or sets os thresholds extras para contagem de curvas.</summary>
        public List<ThresholdWeight> NumCurvesExtraThresholds { get; set; } = new();

        /// <summary>Gets or sets the incremental step (in curves) used for fractional curve scoring beyond <see cref="NumCurves"/>.</summary>
        public int NumCurvesStep { get; set; }

        /// <summary>Gets or sets the weight added per <see cref="NumCurvesStep"/> curves above <see cref="NumCurves"/>.</summary>
        public double NumCurvesStepWeight { get; set; }

        /// <summary>Gets or sets the maximum cumulative contribution produced by curve steps.</summary>
        public double NumCurvesStepMaxContribution { get; set; }

        /// <summary>Gets or sets the maximum rounded radius to account as delicate.</summary>
        public double MinArcRadiusMax { get; set; } = 1.0;

        /// <summary>Gets or sets the minimum number of intersections for bonus score.</summary>
        public int BonusIntersections { get; set; } = 30;

        /// <summary>Gets or sets the weight added when the número de interseções excede o limiar.</summary>
        public double BonusIntersectionsWeight { get; set; } = 1.0;

        /// <summary>Gets or sets thresholds extras para número de interseções.</summary>
        public List<ThresholdWeight> IntersectionThresholds { get; set; } = new();

        /// <summary>Gets or sets thresholds para pontuar dangling ends.</summary>
        public List<ThresholdWeight> DanglingEndThresholds { get; set; } = new();

        /// <summary>Gets or sets the min-radius scoring tunables.</summary>
        public MinRadiusScoringOptions MinRadius { get; set; } = new();

        /// <summary>Gets or sets serrilha-related scoring weights.</summary>
        public SerrilhaScoringOptions Serrilha { get; set; } = new();

        /// <summary>Gets or sets closed-loop (bocas) scoring weights.</summary>
        public ClosedLoopScoringOptions ClosedLoops { get; set; } = new();

        /// <summary>Gets or sets vinco 3pt scoring weights.</summary>
        public ThreePtScoringOptions ThreePt { get; set; } = new();

        /// <summary>Gets or sets scoring para densidade de curvas delicadas.</summary>
        public CurveDensityScoringOptions CurveDensity { get; set; } = new();

        /// <summary>Gets or sets special material scoring weights.</summary>
        public MaterialScoringOptions Materials { get; set; } = new();
    }

    /// <summary>
    /// Captures scoring weights tied to serrilha symbol detection.
    /// </summary>
    public sealed class SerrilhaScoringOptions
    {
        /// <summary>Gets or sets the weight added when any serrilha symbol is detected.</summary>
        public double PresenceWeight { get; set; } = 1.0;

        /// <summary>Gets or sets the extra weight when serrilha mista is present.</summary>
        public double MistaWeight { get; set; } = 1.0;

        /// <summary>Gets or sets the extra weight when more than one serrilha type is present.</summary>
        public double MultiTypeWeight { get; set; } = 0.5;

        /// <summary>Gets or sets the minimum quantidade de tipos distintos para aplicar <see cref="MultiTypeWeight"/>.</summary>
        public int MultiTypeThreshold { get; set; } = 2;

        /// <summary>Gets or sets the extra weight when manual blade codes are found.</summary>
        public double ManualBladeWeight { get; set; } = 0.5;

        /// <summary>Gets or sets the list of blade codes considered manuais (case-insensitive).</summary>
        public List<string> ManualBladeCodes { get; set; } = new();

        /// <summary>Gets or sets the extra weight when serrilha travada/travada manual é detectada.</summary>
        public double TravadaWeight { get; set; } = 0.6;

        /// <summary>Gets or sets the extra weight when serrilha zipper é detectada.</summary>
        public double ZipperWeight { get; set; } = 0.6;

        /// <summary>Gets or sets the thresholds that add contribution based on o total de serrilhas.</summary>
        public List<ThresholdWeight> TotalCountThresholds { get; set; } = new();

        /// <summary>Gets or sets the thresholds that add contribuição adicional quando serrilha mista é abundante.</summary>
        public List<ThresholdWeight> MistaCountThresholds { get; set; } = new();

        /// <summary>Gets or sets the thresholds that add contribuição adicional quando serrilha travada é abundante.</summary>
        public List<ThresholdWeight> TravadaCountThresholds { get; set; } = new();

        /// <summary>Gets or sets the hints (semantic types ou códigos) usados para identificar serrilha “cola”.</summary>
        public List<string> ColaSemanticHints { get; set; } = new();

        /// <summary>Gets or sets the base weight applied when serrilha “cola” é detectada.</summary>
        public double ColaWeight { get; set; }

        /// <summary>Gets or sets thresholds extras para serrilha “cola”.</summary>
        public List<ThresholdWeight> ColaCountThresholds { get; set; } = new();

        /// <summary>Gets or sets the extra weight when o conjunto de categorias de serrilha é diverso.</summary>
        public double DiversityWeight { get; set; } = 0.4;

        /// <summary>Gets or sets the minimum de categorias distintas para aplicar <see cref="DiversityWeight"/>.</summary>
        public int DiversityThreshold { get; set; } = 2;

        /// <summary>Gets or sets the extra weight when múltiplos códigos de lâmina são identificados.</summary>
        public double DistinctBladeWeight { get; set; } = 0.25;

        /// <summary>Gets or sets the minimum quantidade de códigos distintos para aplicar <see cref="DistinctBladeWeight"/>.</summary>
        public int DistinctBladeThreshold { get; set; } = 2;

        /// <summary>Gets or sets the bônus quando corte seco coincide com múltiplos tipos de serrilha.</summary>
        public double CorteSecoMultiTypeWeight { get; set; } = 0.5;

        /// <summary>Gets or sets the maximum total length (mm) considered “pequena” para ajuste de facilidade.</summary>
        public double SmallPieceMaxTotalLength { get; set; } = 0.0;

        /// <summary>Gets or sets the maximum quantidade de peças para aplicar o ajuste de facilidade.</summary>
        public int SmallPieceMaxCount { get; set; } = 0;

        /// <summary>Gets or sets the ajuste aplicado quando a serrilha é curta/linear (valores negativos reduzem o score).</summary>
        public double SmallPieceAdjustment { get; set; } = 0.0;
    }

    /// <summary>
    /// Captures penalties applied to minimum radius findings.
    /// </summary>
    public sealed class MinRadiusScoringOptions
    {
        /// <summary>Gets or sets the threshold (in mm) that is considered delicado (penaliza).</summary>
        public double DangerThreshold { get; set; } = 0.3;

        /// <summary>Gets or sets the upper bound (in mm) for the neutral zone. Values above this are ignored.</summary>
        public double NeutralThreshold { get; set; } = 1.0;

        /// <summary>Gets or sets the weight to add when the minimum radius falls below <see cref="DangerThreshold"/>.</summary>
        public double PenaltyWeight { get; set; } = 1.0;

        /// <summary>Gets or sets the score adjustment applied whenever corte seco is detected.</summary>
        public double CorteSecoAdjustment { get; set; } = -0.5;

        /// <summary>Gets or sets extra thresholds baseados na quantidade de pares de corte seco detectados.</summary>
        public List<ThresholdWeight> CorteSecoPairThresholds { get; set; } = new();
    }

    /// <summary>
    /// Captures scoring weights for closed loops (bocas).
    /// </summary>
    public sealed class ClosedLoopScoringOptions
    {
        /// <summary>Gets or sets the thresholds that increment score based on quantidade de bocas.</summary>
        public List<ThresholdWeight> CountThresholds { get; set; } = new();

        /// <summary>Gets or sets the minimum quantidade de tipos distintos para bônus adicional.</summary>
        public int VarietyThreshold { get; set; } = 2;

        /// <summary>Gets or sets the weight aplicado quando variedade de bocas excede <see cref="VarietyThreshold"/>.</summary>
        public double VarietyWeight { get; set; } = 0.4;

        /// <summary>Gets or sets the thresholds baseados na densidade de bocas por área.</summary>
        public List<ThresholdWeight> DensityThresholds { get; set; } = new();
    }

    /// <summary>
    /// Captures scoring weights for vinco 3pt.
    /// </summary>
    public sealed class ThreePtScoringOptions
    {
        /// <summary>Gets or sets the thresholds/weights baseados em comprimento total.</summary>
        public List<ThresholdWeight> LengthThresholds { get; set; } = new();

        /// <summary>Gets or sets the thresholds/weights baseados na quantidade de segmentos.</summary>
        public List<ThresholdWeight> SegmentThresholds { get; set; } = new();

        /// <summary>Gets or sets the thresholds/weights baseados na razão vs corte.</summary>
        public List<ThresholdWeight> RatioThresholds { get; set; } = new();

        /// <summary>Gets or sets the bônus aplicado sempre que o vinco 3pt exigir dobra manual.</summary>
        public double ManualHandlingWeight { get; set; } = 0.4;
    }

    /// <summary>
    /// Captures scoring weights for curve density.
    /// </summary>
    public sealed class CurveDensityScoringOptions
    {
        /// <summary>Gets or sets the thresholds/weights baseados na densidade relativa.</summary>
        public List<ThresholdWeight> DensityThresholds { get; set; } = new();

        /// <summary>Gets or sets the thresholds/weights baseados na contagem absoluta de arcos delicados.</summary>
        public List<ThresholdWeight> DelicateArcCountThresholds { get; set; } = new();
    }

    /// <summary>
    /// Captures scoring weights for materiais especiais (ex.: adesivo).
    /// </summary>
    public sealed class MaterialScoringOptions
    {
        /// <summary>Gets or sets the weight padrão aplicado quando um material especial é encontrado.</summary>
        public double DefaultWeight { get; set; } = 0.5;

        /// <summary>Gets or sets o mapa de pesos por material (case-insensitive).</summary>
        public Dictionary<string, double> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets or sets overrides aplicados quando o nome contém determinadas palavras-chave.</summary>
        public Dictionary<string, double> KeywordOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Encapsula ajustes relacionados ao cache de resultados.
    /// </summary>
    public sealed class CacheOptions
    {
        /// <summary>Gets or sets a value indicating whether cached results must be ignored.</summary>
        public bool Bypass { get; set; }
    }

    /// <summary>
    /// Represents a numeric threshold associated with a weight contribution.
    /// </summary>
    public sealed class ThresholdWeight
    {
        /// <summary>Gets or sets the threshold value.</summary>
        public double Threshold { get; set; }

        /// <summary>Gets or sets the weight contributed when the threshold is satisfied.</summary>
        public double Weight { get; set; }
    }

    /// <summary>
    /// Captures heuristics for identifying corte seco (batida seca) geometrically.
    /// </summary>
    public sealed class CorteSecoOptions
    {
        /// <summary>Gets or sets a value indicating whether the heuristic is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Gets or sets the maximum angular difference (in degrees) considered parallel.</summary>
        public double MaxParallelAngleDegrees { get; set; } = 5.0;

        /// <summary>Gets or sets the maximum offset between complementary segments (in mm).</summary>
        public double MaxOffsetMillimeters { get; set; } = 0.45;

        /// <summary>Gets or sets the minimum overlap ratio relative to the shorter segment.</summary>
        public double MinOverlapRatio { get; set; } = 0.65;

        /// <summary>Gets or sets the minimum segment length (in mm) to consider.</summary>
        public double MinLengthMillimeters { get; set; } = 8.0;

        /// <summary>Gets or sets the minimum number of qualifying pairs required to mark corte seco.</summary>
        public int MinPairCount { get; set; } = 1;

        /// <summary>Gets or sets the semantic layer types inspected by the heuristic.</summary>
        public List<string> TargetLayerTypes { get; set; } = new()
        {
            "serrilha",
            "serrilha_mista",
            "serrilhamista",
            "corte"
        };
    }

    /// <summary>
    /// Represents a serrilha symbol configuration entry.
    /// </summary>
    public sealed class SerrilhaSymbol
    {
        /// <summary>Gets or sets the regex pattern matched against <see cref="netDxf.Entities.Insert.Block"/> names.</summary>
        public string SymbolNamePattern { get; set; } = string.Empty;

        /// <summary>Gets or sets the optional regex pattern matched against attribute text or tags.</summary>
        public string? AttributePattern { get; set; }

        /// <summary>Gets or sets the semantic type (for example, serrilha_fina, serrilha_mista).</summary>
        public string SemanticType { get; set; } = string.Empty;

        /// <summary>Gets or sets an optional blade code or descriptive identifier.</summary>
        public string? BladeCode { get; set; }

        /// <summary>Gets or sets the optional default tooth count to use when no attribute provides it.</summary>
        public double? DefaultToothCount { get; set; }

        /// <summary>Gets or sets the optional default length in millimeters to use when geometry cannot be measured.</summary>
        public double? DefaultLength { get; set; }

        internal SerrilhaSymbolMatcher ToMatcher()
        {
            var symbolRegex = new Regex(SymbolNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            if (!string.IsNullOrWhiteSpace(AttributePattern))
            {
                var attributeRegex = new Regex(AttributePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
                return new SerrilhaSymbolMatcher(symbolRegex, attributeRegex, SemanticType, BladeCode, DefaultToothCount, DefaultLength);
            }

            return new SerrilhaSymbolMatcher(symbolRegex, null, SemanticType, BladeCode, DefaultToothCount, DefaultLength);
        }
    }

    /// <summary>
    /// Represents a serrilha text configuration entry.
    /// </summary>
    public sealed class SerrilhaTextSymbol
    {
        /// <summary>Gets or sets the regex pattern matched against text or mtext values.</summary>
        public string TextPattern { get; set; } = string.Empty;

        /// <summary>Gets or sets the semantic type (for example, serrilha_fina, serrilha_mista).</summary>
        public string SemanticType { get; set; } = string.Empty;

        /// <summary>Gets or sets an optional blade code or descriptive identifier.</summary>
        public string? BladeCode { get; set; }

        /// <summary>Gets or sets the optional capture group name used to override the semantic type.</summary>
        public string? SemanticTypeGroup { get; set; }

        /// <summary>Gets or sets the composite format applied when <see cref="SemanticTypeGroup"/> is used (default "{value}").</summary>
        public string SemanticTypeFormat { get; set; } = "{value}";

        /// <summary>Gets or sets the optional capture group name used as blade code.</summary>
        public string? BladeCodeGroup { get; set; }

        /// <summary>Gets or sets a value indicating whether extracted blade codes should be upper-cased.</summary>
        public bool UppercaseBladeCode { get; set; } = true;

        /// <summary>Gets or sets a value indicating whether semantic types derived from groups should be upper-cased.</summary>
        public bool UppercaseSemanticType { get; set; }

        /// <summary>Gets or sets a value indicating whether multiple matches inside the same text entity should be considered.</summary>
        public bool AllowMultipleMatches { get; set; } = true;

        /// <summary>Gets or sets the optional capture group name used to derive the length.</summary>
        public string? LengthGroup { get; set; }

        /// <summary>Gets or sets the scale applied to captured length values.</summary>
        public double LengthScale { get; set; } = 1.0;

        /// <summary>Gets or sets the optional capture group name used to derive tooth count.</summary>
        public string? ToothCountGroup { get; set; }

        /// <summary>Gets or sets the scale applied to captured tooth count values.</summary>
        public double ToothCountScale { get; set; } = 1.0;

        /// <summary>Gets or sets the optional default tooth count when the pattern does not provide one.</summary>
        public double? DefaultToothCount { get; set; }

        /// <summary>Gets or sets the optional default length when the pattern does not provide one.</summary>
        public double? DefaultLength { get; set; }

        internal SerrilhaTextSymbolMatcher ToMatcher()
        {
            var regex = new Regex(TextPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            return new SerrilhaTextSymbolMatcher(
                regex,
                SemanticType,
                SemanticTypeGroup,
                SemanticTypeFormat,
                BladeCode,
                BladeCodeGroup,
                UppercaseBladeCode,
                UppercaseSemanticType,
                AllowMultipleMatches,
                LengthGroup,
                LengthScale,
                ToothCountGroup,
                ToothCountScale,
                DefaultToothCount,
                DefaultLength);
        }
    }

    /// <summary>
    /// Represents a compiled serrilha symbol matcher.
    /// </summary>
    public sealed record SerrilhaSymbolMatcher(
        Regex SymbolNameRegex,
        Regex? AttributeRegex,
        string SemanticType,
        string? BladeCode,
        double? DefaultToothCount,
        double? DefaultLength);

    /// <summary>
    /// Represents a compiled serrilha text matcher.
    /// </summary>
    public sealed record SerrilhaTextSymbolMatcher(
        Regex TextRegex,
        string SemanticType,
        string? SemanticTypeGroup,
        string SemanticTypeFormat,
        string? BladeCode,
        string? BladeCodeGroup,
        bool UppercaseBladeCode,
        bool UppercaseSemanticType,
        bool AllowMultipleMatches,
        string? LengthGroup,
        double LengthScale,
        string? ToothCountGroup,
        double ToothCountScale,
        double? DefaultToothCount,
        double? DefaultLength);

    /// <summary>
    /// Represents telemetry configuration knobs.
    /// </summary>
    public sealed class TelemetryOptions
    {
        /// <summary>Gets or sets a value indicating whether Prometheus counters are enabled.</summary>
        public bool EnableMetrics { get; set; } = true;

        /// <summary>Gets or sets the meter name used when creating <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
        public string MeterName { get; set; } = "FileWatcherApp.DXFAnalysis";
    }

    /// <summary>
    /// Gets or sets the collection of serrilha text definitions used to detect blade types via textual annotations.
    /// </summary>
    public List<SerrilhaTextSymbol> SerrilhaTextSymbols { get; set; } = new();
}

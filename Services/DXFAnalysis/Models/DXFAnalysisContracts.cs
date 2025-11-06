using Newtonsoft.Json;

namespace FileWatcherApp.Services.DXFAnalysis.Models;

/// <summary>
/// Represents a request message to analyze a DXF file.
/// </summary>
public sealed class DXFAnalysisRequest
{
    /// <summary>Gets or sets the optional OP identifier.</summary>
    [JsonProperty("opId", NullValueHandling = NullValueHandling.Ignore)]
    public string? OpId { get; set; }

    /// <summary>Gets or sets the path to the DXF file.</summary>
    [JsonProperty("filePath", Required = Required.Always)]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional file hash if already computed.</summary>
    [JsonProperty("fileHash", NullValueHandling = NullValueHandling.Ignore)]
    public string? FileHash { get; set; }

    /// <summary>Gets or sets arbitrary boolean or string flags.</summary>
    [JsonProperty("flags", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object>? Flags { get; set; }

    /// <summary>Gets or sets optional metadata forwarded downstream.</summary>
    [JsonProperty("meta", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object>? Meta { get; set; }
}

/// <summary>
/// Represents the result message published after finishing a DXF analysis.
/// </summary>
public sealed class DXFAnalysisResult
{
    /// <summary>Gets or sets the analysis identifier.</summary>
    [JsonProperty("analysisId")]
    public string AnalysisId { get; set; } = string.Empty;

    /// <summary>Gets or sets the ISO 8601 timestamp when the analysis completed.</summary>
    [JsonProperty("timestampUtc")]
    public string TimestampUtc { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional OP identifier originally provided.</summary>
    [JsonProperty("opId", NullValueHandling = NullValueHandling.Ignore)]
    public string? OpId { get; set; }

    /// <summary>Gets or sets the analyzed file name.</summary>
    [JsonProperty("fileName")]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the file hash used for deduplication.</summary>
    [JsonProperty("fileHash", NullValueHandling = NullValueHandling.Ignore)]
    public string? FileHash { get; set; }

    /// <summary>Gets or sets the metrics that were extracted from the DXF.</summary>
    [JsonProperty("metrics", NullValueHandling = NullValueHandling.Include)]
    public DXFMetrics Metrics { get; set; } = new();

    /// <summary>Gets or sets the optional flags forwarded from the request.</summary>
    [JsonProperty("flags", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, object>? Flags { get; set; }

    /// <summary>Gets or sets the optional image metadata when rendering succeeds.</summary>
    [JsonProperty("image", NullValueHandling = NullValueHandling.Ignore)]
    public DXFImageInfo? Image { get; set; }

    /// <summary>Gets or sets the score computed by the deterministic engine.</summary>
    [JsonProperty("score", NullValueHandling = NullValueHandling.Include)]
    public double? Score { get; set; }

    /// <summary>Gets or sets the explanations for the score awarded.</summary>
    [JsonProperty("explanations", NullValueHandling = NullValueHandling.Include)]
    public List<string> Explanations { get; set; } = new();

    /// <summary>Gets or sets the semantic version string emitted by the engine.</summary>
    [JsonProperty("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the processing duration in milliseconds.</summary>
    [JsonProperty("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>Gets or sets a value indicating whether the result is read-only / shadow.</summary>
    [JsonProperty("shadowMode", NullValueHandling = NullValueHandling.Ignore)]
    public bool? ShadowMode { get; set; }
}

/// <summary>
/// Collects numeric and categorical metrics derived from a DXF file.
/// </summary>
public sealed class DXFMetrics
{
    /// <summary>Gets or sets the unit used across all numeric measurements.</summary>
    [JsonProperty("unit")]
    public string Unit { get; set; } = "mm";

    /// <summary>Gets or sets the bounding extents.</summary>
    [JsonProperty("extents")]
    public DXFExtents Extents { get; set; } = new();

    /// <summary>Gets or sets the bounding box area.</summary>
    [JsonProperty("bboxArea")]
    public double BboxArea { get; set; }

    /// <summary>Gets or sets the bounding box perimeter.</summary>
    [JsonProperty("bboxPerimeter")]
    public double BboxPerimeter { get; set; }

    /// <summary>Gets or sets the total cut length.</summary>
    [JsonProperty("totalCutLength")]
    public double TotalCutLength { get; set; }

    /// <summary>Gets or sets the total fold length.</summary>
    [JsonProperty("totalFoldLength")]
    public double TotalFoldLength { get; set; }

    /// <summary>Gets or sets the total perf length.</summary>
    [JsonProperty("totalPerfLength")]
    public double TotalPerfLength { get; set; }

    /// <summary>Gets or sets the total 3pt length.</summary>
    [JsonProperty("total3PtLength")]
    public double TotalThreePtLength { get; set; }

    /// <summary>Gets or sets the number of segments classificados como 3pt.</summary>
    [JsonProperty("threePtSegmentCount")]
    public int ThreePtSegmentCount { get; set; }

    /// <summary>Gets or sets the razão entre 3pt e cortes normais.</summary>
    [JsonProperty("threePtCutRatio")]
    public double ThreePtCutRatio { get; set; }

    /// <summary>Gets or sets a value indicating whether os vincos 3pt exigem dobra manual.</summary>
    [JsonProperty("requiresManualThreePtHandling")]
    public bool RequiresManualThreePtHandling { get; set; }

    /// <summary>Gets or sets the number of curved entities.</summary>
    [JsonProperty("numCurves")]
    public int NumCurves { get; set; }

    /// <summary>Gets or sets the number of nodes.</summary>
    [JsonProperty("numNodes")]
    public int NumNodes { get; set; }

    /// <summary>Gets or sets the number of intersections detected.</summary>
    [JsonProperty("numIntersections")]
    public int NumIntersections { get; set; }

    /// <summary>Gets or sets the minimum arc radius detected.</summary>
    [JsonProperty("minArcRadius")]
    public double MinArcRadius { get; set; } = 0.0;

    /// <summary>Gets or sets the number of polylines.</summary>
    [JsonProperty("polylineCount")]
    public int PolylineCount { get; set; }

    /// <summary>Gets or sets the number of splines.</summary>
    [JsonProperty("splineCount")]
    public int SplineCount { get; set; }

    /// <summary>Gets or sets the number of lines.</summary>
    [JsonProperty("lineCount")]
    public int LineCount { get; set; }

    /// <summary>Gets or sets the number of arcs.</summary>
    [JsonProperty("arcCount")]
    public int ArcCount { get; set; }

    /// <summary>Gets or sets per-layer statistics.</summary>
    [JsonProperty("layerStats")]
   public List<DXFLayerStats> LayerStats { get; set; } = new();

    /// <summary>Gets or sets the serrilha symbol summary extracted from block inserts.</summary>
    [JsonProperty("serrilha", NullValueHandling = NullValueHandling.Ignore)]
    public DXFSerrilhaSummary? Serrilha { get; set; }

    /// <summary>Gets or sets quality metrics.</summary>
    [JsonProperty("quality")]
    public DXFQualityMetrics Quality { get; set; } = new();
}

/// <summary>Represents the spatial extents of the analyzed entities.</summary>
public sealed class DXFExtents
{
    /// <summary>Gets or sets the minimum X coordinate.</summary>
    [JsonProperty("minX")]
    public double MinX { get; set; }

    /// <summary>Gets or sets the minimum Y coordinate.</summary>
    [JsonProperty("minY")]
    public double MinY { get; set; }

    /// <summary>Gets or sets the maximum X coordinate.</summary>
    [JsonProperty("maxX")]
    public double MaxX { get; set; }

    /// <summary>Gets or sets the maximum Y coordinate.</summary>
    [JsonProperty("maxY")]
    public double MaxY { get; set; }
}

/// <summary>Holds aggregate information about a DXF layer.</summary>
public sealed class DXFLayerStats
{
    /// <summary>Gets or sets the raw layer name.</summary>
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the semantic type derived from configuration.</summary>
    [JsonProperty("type")]
    public string Type { get; set; } = "unknown";

    /// <summary>Gets or sets the number of entities contained in the layer.</summary>
    [JsonProperty("entityCount")]
    public int EntityCount { get; set; }

    /// <summary>Gets or sets the total linear length of the entities in millimeters.</summary>
    [JsonProperty("totalLength")]
    public double TotalLength { get; set; }
}

/// <summary>Summarizes all serrilha symbols detected in the document.</summary>
public sealed class DXFSerrilhaSummary
{
    /// <summary>Gets or sets the total number of recognized serrilha symbols.</summary>
    [JsonProperty("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the total number of occurrences that could not be mapped to known symbols.</summary>
    [JsonProperty("unknownCount")]
    public int UnknownCount { get; set; }

    /// <summary>Gets or sets the list of canonical entries grouped by semantic type and blade code.</summary>
    [JsonProperty("entries")]
    public List<DXFSerrilhaEntry> Entries { get; set; } = new();

    /// <summary>Gets or sets the set of symbol names that failed to match any configured pattern.</summary>
    [JsonProperty("unknownSymbols", NullValueHandling = NullValueHandling.Ignore)]
    public HashSet<string>? UnknownSymbols { get; set; }

    /// <summary>Gets or sets the total estimated length (mm) somada das serrilhas.</summary>
    [JsonProperty("totalEstimatedLength", NullValueHandling = NullValueHandling.Ignore)]
    public double? TotalEstimatedLength { get; set; }

    /// <summary>Gets or sets the average estimated length (mm) por peça.</summary>
    [JsonProperty("averageEstimatedLength", NullValueHandling = NullValueHandling.Ignore)]
    public double? AverageEstimatedLength { get; set; }

    /// <summary>Gets or sets a value indicating whether corte seco was detected.</summary>
    [JsonProperty("isCorteSeco", NullValueHandling = NullValueHandling.Ignore)]
    public bool? IsCorteSeco { get; set; }

    /// <summary>Gets or sets the detected corte seco pairs.</summary>
    [JsonProperty("corteSecoPairs", NullValueHandling = NullValueHandling.Ignore)]
    public List<DXFCorteSecoPair>? CorteSecoPairs { get; set; }

    /// <summary>Gets or sets the normalized blade codes that contributed to corte seco detection.</summary>
    [JsonProperty("corteSecoBladeCodes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? CorteSecoBladeCodes { get; set; }

    /// <summary>Gets or sets aggregate classification counters.</summary>
    [JsonProperty("classification", NullValueHandling = NullValueHandling.Ignore)]
    public DXFSerrilhaClassificationMetrics? Classification { get; set; }

    /// <summary>Gets or sets the number of distinct semantic types.</summary>
    [JsonProperty("distinctSemanticTypes")]
    public int DistinctSemanticTypes { get; set; }

    /// <summary>Gets or sets the number of distinct blade codes.</summary>
    [JsonProperty("distinctBladeCodes")]
    public int DistinctBladeCodes { get; set; }
}

/// <summary>Represents an aggregated serrilha symbol group.</summary>
public sealed class DXFSerrilhaEntry
{
    /// <summary>Gets or sets the semantic type (serrilha_fina, serrilha_mista, etc.).</summary>
    [JsonProperty("semanticType")]
    public string SemanticType { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional blade code.</summary>
    [JsonProperty("bladeCode", NullValueHandling = NullValueHandling.Ignore)]
    public string? BladeCode { get; set; }

    /// <summary>Gets or sets the canonical block names that contributed to this entry.</summary>
    [JsonProperty("symbolNames")]
    public HashSet<string> SymbolNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the number of occurrences detected.</summary>
    [JsonProperty("count")]
    public int Count { get; set; }

    /// <summary>Gets or sets the aggregated estimated length in millimeters.</summary>
    [JsonProperty("estimatedLength", NullValueHandling = NullValueHandling.Ignore)]
    public double? EstimatedLength { get; set; }

    /// <summary>Gets or sets the aggregated estimated tooth count.</summary>
    [JsonProperty("estimatedToothCount", NullValueHandling = NullValueHandling.Ignore)]
    public double? EstimatedToothCount { get; set; }
}

/// <summary>Aggregated counters grouped by serrilha classification.</summary>
public sealed class DXFSerrilhaClassificationMetrics
{
    /// <summary>Gets or sets the total occurrences de serrilha simples.</summary>
    [JsonProperty("simple")]
    public int Simple { get; set; }

    /// <summary>Gets or sets the total occurrences de serrilha travada.</summary>
    [JsonProperty("travada")]
    public int Travada { get; set; }

    /// <summary>Gets or sets the total occurrences de serrilha zipper.</summary>
    [JsonProperty("zipper")]
    public int Zipper { get; set; }

    /// <summary>Gets or sets the total occurrences de serrilha mista.</summary>
    [JsonProperty("mista")]
    public int Mista { get; set; }

    /// <summary>Gets or sets o número de categorias com ocorrência.</summary>
    [JsonProperty("distinctCategories")]
    public int DistinctCategories { get; set; }
}

/// <summary>Represents a pair of segments identified as corte seco.</summary>
public sealed class DXFCorteSecoPair
{
    /// <summary>Gets or sets the first layer name.</summary>
    [JsonProperty("layerA")]
    public string LayerA { get; set; } = string.Empty;

    /// <summary>Gets or sets the second layer name.</summary>
    [JsonProperty("layerB")]
    public string LayerB { get; set; } = string.Empty;

    /// <summary>Gets or sets the semantic type of the first layer.</summary>
    [JsonProperty("typeA")]
    public string TypeA { get; set; } = string.Empty;

    /// <summary>Gets or sets the semantic type of the second layer.</summary>
    [JsonProperty("typeB")]
    public string TypeB { get; set; } = string.Empty;

    /// <summary>Gets or sets the overlapping length in millimeters.</summary>
    [JsonProperty("overlapMm")]
    public double OverlapMillimeters { get; set; }

    /// <summary>Gets or sets the average offset between the segments in millimeters.</summary>
    [JsonProperty("offsetMm")]
    public double OffsetMillimeters { get; set; }

    /// <summary>Gets or sets the angular difference in degrees.</summary>
    [JsonProperty("angleDeg")]
    public double AngleDifferenceDegrees { get; set; }
}

/// <summary>Captures quality tolerances discovered during preprocessing.</summary>
public sealed class DXFQualityMetrics
{
    /// <summary>Gets or sets the number of tiny gaps detected.</summary>
    [JsonProperty("tinyGaps")]
    public int TinyGaps { get; set; }

    /// <summary>Gets or sets the number of overlaps heuristically detected.</summary>
    [JsonProperty("overlaps")]
    public int Overlaps { get; set; }

    /// <summary>Gets or sets the number of dangling ends.</summary>
    [JsonProperty("danglingEnds")]
    public int DanglingEnds { get; set; }

    /// <summary>Gets or sets the quantidade de loops fechados (bocas).</summary>
    [JsonProperty("closedLoops")]
    public int ClosedLoops { get; set; }

    /// <summary>Gets or sets o mapa de loops por tipo de camada.</summary>
    [JsonProperty("closedLoopsByType", NullValueHandling = NullValueHandling.Ignore)]
    public Dictionary<string, int>? ClosedLoopsByType { get; set; }

    /// <summary>Gets or sets the loop density (loops por mm²).</summary>
    [JsonProperty("closedLoopDensity")]
    public double ClosedLoopDensity { get; set; }

    /// <summary>Gets or sets a lista de materiais especiais detectados.</summary>
    [JsonProperty("specialMaterials", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? SpecialMaterials { get; set; }

    /// <summary>Gets or sets the quantidade de arcos delicados.</summary>
    [JsonProperty("delicateArcCount")]
    public int DelicateArcCount { get; set; }

    /// <summary>Gets or sets o comprimento acumulado de arcos delicados.</summary>
    [JsonProperty("delicateArcLength")]
    public double DelicateArcLength { get; set; }

    /// <summary>Gets or sets a densidade relativa de arcos delicados.</summary>
    [JsonProperty("delicateArcDensity")]
    public double DelicateArcDensity { get; set; }

    /// <summary>Gets or sets notas complementares.</summary>
    [JsonProperty("notes", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Notes { get; set; }
}

/// <summary>Metadata for the rendered DXF image.</summary>
public sealed class DXFImageInfo
{
    /// <summary>Gets or sets the PNG path (vazio quando a cópia local é desativada).</summary>
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the rendered width in pixels.</summary>
    [JsonProperty("widthPx")]
    public int WidthPx { get; set; }

    /// <summary>Gets or sets the rendered height in pixels.</summary>
    [JsonProperty("heightPx")]
    public int HeightPx { get; set; }

    /// <summary>Gets or sets the DPI used for the image.</summary>
    [JsonProperty("dpi")]
    public double Dpi { get; set; }

    /// <summary>Gets or sets the image MIME type.</summary>
    [JsonProperty("contentType", NullValueHandling = NullValueHandling.Ignore)]
    public string? ContentType { get; set; }

    /// <summary>Gets or sets the payload size in bytes.</summary>
    [JsonProperty("sizeBytes", NullValueHandling = NullValueHandling.Ignore)]
    public long? SizeBytes { get; set; }

    /// <summary>Gets or sets the optional checksum (ex.: SHA-256).</summary>
    [JsonProperty("checksum", NullValueHandling = NullValueHandling.Ignore)]
    public string? Checksum { get; set; }

    /// <summary>Gets or sets the storage bucket/container.</summary>
    [JsonProperty("storageBucket", NullValueHandling = NullValueHandling.Ignore)]
    public string? StorageBucket { get; set; }

    /// <summary>Gets or sets the storage object key.</summary>
    [JsonProperty("storageKey", NullValueHandling = NullValueHandling.Ignore)]
    public string? StorageKey { get; set; }

    /// <summary>Gets or sets the public URI (when available).</summary>
    [JsonProperty("storageUri", NullValueHandling = NullValueHandling.Ignore)]
    public string? StorageUri { get; set; }

    /// <summary>Gets or sets the upload status label (uploaded, skipped, failed...).</summary>
    [JsonProperty("uploadStatus", NullValueHandling = NullValueHandling.Ignore)]
    public string? UploadStatus { get; set; }

    /// <summary>Gets or sets the upload timestamp.</summary>
    [JsonProperty("uploadedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
    public string? UploadedAtUtc { get; set; }

    /// <summary>Gets or sets the storage ETag when provided.</summary>
    [JsonProperty("etag", NullValueHandling = NullValueHandling.Ignore)]
    public string? ETag { get; set; }

    /// <summary>Gets or sets an optional backend message (error or skipped reason).</summary>
    [JsonProperty("uploadMessage", NullValueHandling = NullValueHandling.Ignore)]
    public string? UploadMessage { get; set; }
}

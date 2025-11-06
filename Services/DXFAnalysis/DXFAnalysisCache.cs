using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using FileWatcherApp.Services.DXFAnalysis.Models;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Persists and retrieves deterministic DXF analysis results by file hash.
/// </summary>
public sealed class DXFAnalysisCache
{
    private readonly DXFAnalysisOptions _options;
    private readonly ILogger<DXFAnalysisCache> _logger;
    private readonly JsonSerializerSettings _serializerSettings = new()
    {
        Formatting = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="DXFAnalysisCache"/> class.
    /// </summary>
    public DXFAnalysisCache(IOptions<DXFAnalysisOptions> options, ILogger<DXFAnalysisCache> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Attempts to retrieve a cached result by hash.
    /// </summary>
    public bool TryGet(string? fileHash, out DXFAnalysisResult? result)
    {
        result = null;
        if (_options.Cache?.Bypass == true)
        {
            _logger.LogDebug("Cache bypass ativado - ignorando leitura para hash {Hash}", fileHash);
            return false;
        }

        if (string.IsNullOrWhiteSpace(fileHash))
        {
            return false;
        }

        var path = GetCacheFilePath(fileHash);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var payload = File.ReadAllText(path);
            result = JsonConvert.DeserializeObject<DXFAnalysisResult>(payload);
            if (result != null)
            {
                _logger.LogDebug("Cache hit para hash {Hash}", fileHash);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao ler cache {Path}", path);
        }

        return false;
    }

    /// <summary>
    /// Stores the result for future reuse.
    /// </summary>
    public void Save(DXFAnalysisResult result)
    {
        if (_options.Cache?.Bypass == true)
        {
            _logger.LogDebug("Cache bypass ativado - não persistindo hash {Hash}", result.FileHash);
            return;
        }

        if (result.FileHash is null)
        {
            return;
        }

        var path = GetCacheFilePath(result.FileHash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            var payload = JsonConvert.SerializeObject(result, _serializerSettings);
            File.WriteAllText(path, payload);
            _logger.LogDebug("Cache salvo em {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao salvar cache {Path}", path);
        }
    }

    private string GetCacheFilePath(string fileHash)
    {
        var folder = string.IsNullOrWhiteSpace(_options.CacheFolder)
            ? Path.Combine(_options.OutputImageFolder, "_analysis-cache")
            : _options.CacheFolder;

        var safeHash = fileHash.Replace(":", "_");
        return Path.Combine(folder, safeHash + ".analysis.json");
    }
}

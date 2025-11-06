using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using FileWatcherApp.Services.DXFAnalysis.Models;
using System.Linq;
using FileWatcherApp.Services.DXFAnalysis.Storage;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Consumes DXF analysis requests and orchestrates the deterministic pipeline.
/// </summary>
public sealed class DXFAnalysisWorker : BackgroundService
{
    private readonly DXFAnalysisOptions _options;
    private readonly ILogger<DXFAnalysisWorker> _logger;
    private readonly DXFPreprocessor _preprocessor;
    private readonly DXFAnalyzer _analyzer;
    private readonly DXFImageRenderer _renderer;
    private readonly ComplexityScorer _scorer;
    private readonly DXFAnalysisCache _cache;
    private readonly IImageStorageClient _imageStorageClient;

    private readonly Meter? _meter;
    private readonly Counter<long>? _analysisOk;
    private readonly Counter<long>? _analysisFailed;
    private readonly Counter<long>? _renderFailed;
    private readonly Counter<long>? _cacheHit;
    private readonly Counter<long>? _cacheMiss;
    private readonly Histogram<double>? _analysisDuration;
    private readonly Counter<long>? _serrilhaUnknown;

    private IConnection? _connection;
    private IModel? _consumerChannel;
    private IModel? _publisherChannel;
    private SemaphoreSlim? _concurrency;
    private CancellationToken _stoppingToken;
    private readonly object _publishLock = new();
    private static readonly byte[] HeaderAc1014 = Encoding.ASCII.GetBytes("AC1014");
    private static readonly byte[] HeaderAc1015 = Encoding.ASCII.GetBytes("AC1015");

    /// <summary>
    /// Initializes a new instance of the <see cref="DXFAnalysisWorker"/> class.
    /// </summary>
    public DXFAnalysisWorker(
        IOptions<DXFAnalysisOptions> options,
        ILogger<DXFAnalysisWorker> logger,
        DXFPreprocessor preprocessor,
        DXFAnalyzer analyzer,
        DXFImageRenderer renderer,
        ComplexityScorer scorer,
        DXFAnalysisCache cache,
        IImageStorageClient imageStorageClient)
    {
        _options = options.Value;
        _logger = logger;
        _preprocessor = preprocessor;
        _analyzer = analyzer;
        _renderer = renderer;
        _scorer = scorer;
        _cache = cache;
        _imageStorageClient = imageStorageClient;

        if (_options.Telemetry.EnableMetrics)
        {
            _meter = new Meter(_options.Telemetry.MeterName, _options.Version);
            _analysisOk = _meter.CreateCounter<long>("analysis_ok");
            _analysisFailed = _meter.CreateCounter<long>("analysis_failed");
            _renderFailed = _meter.CreateCounter<long>("render_failed");
            _cacheHit = _meter.CreateCounter<long>("cache_hit");
            _cacheMiss = _meter.CreateCounter<long>("cache_miss");
            _analysisDuration = _meter.CreateHistogram<double>("analysis_duration_ms");
            _serrilhaUnknown = _meter.CreateCounter<long>("serrilha_unknown_symbol");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Iniciando DXFAnalysisWorker... fila={RequestQueue} paralelismo={Parallelism}", _options.RabbitQueueRequest, _options.Parallelism);
        _logger.LogInformation(
            "Config imagem: persistLocal={PersistLocal} storageEnabled={Enabled} provider={Provider} endpoint={Endpoint} bucket={Bucket} keyPrefix={KeyPrefix}",
            _options.PersistLocalImageCopy,
            _options.ImageStorage?.Enabled,
            _options.ImageStorage?.Provider,
            _options.ImageStorage?.Endpoint,
            _options.ImageStorage?.Bucket,
            _options.ImageStorage?.KeyPrefix);

        _concurrency = new SemaphoreSlim(Math.Max(1, _options.Parallelism));
        _stoppingToken = stoppingToken;

        var factory = new ConnectionFactory
        {
            HostName = _options.RabbitMq.HostName,
            Port = _options.RabbitMq.Port,
            UserName = _options.RabbitMq.UserName,
            Password = _options.RabbitMq.Password,
            VirtualHost = _options.RabbitMq.VirtualHost,
            AutomaticRecoveryEnabled = _options.RabbitMq.AutomaticRecoveryEnabled,
            RequestedHeartbeat = TimeSpan.FromSeconds(_options.RabbitMq.RequestedHeartbeatSeconds),
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _consumerChannel = _connection.CreateModel();
        _publisherChannel = _connection.CreateModel();

        _consumerChannel.BasicQos(0, _options.RabbitMq.PrefetchCount, false);
        _consumerChannel.QueueDeclare(_options.RabbitQueueRequest, durable: true, exclusive: false, autoDelete: false);
        _publisherChannel.QueueDeclare(_options.RabbitQueueResult, durable: true, exclusive: false, autoDelete: false);

        var consumer = new AsyncEventingBasicConsumer(_consumerChannel);
        consumer.Received += HandleMessageAsync;
        _consumerChannel.BasicConsume(queue: _options.RabbitQueueRequest, autoAck: false, consumer: consumer);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // expected on shutdown
        }
    }

    private Task HandleMessageAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        if (_concurrency is null || _consumerChannel is null)
        {
            return Task.CompletedTask;
        }

        return HandleAsync();

        async Task HandleAsync()
        {
            await _concurrency.WaitAsync(_stoppingToken);
            try
            {
                await ProcessMessageAsync(eventArgs);
                if (_consumerChannel.IsOpen)
                {
                    _consumerChannel.BasicAck(eventArgs.DeliveryTag, multiple: false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar mensagem DXF deliveryTag={Tag}", eventArgs.DeliveryTag);
                if (_consumerChannel.IsOpen)
                {
                    _consumerChannel.BasicNack(eventArgs.DeliveryTag, multiple: false, requeue: false);
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs eventArgs)
    {
        var stopwatch = Stopwatch.StartNew();
        DXFAnalysisRequest? request = null;
        string? fileHash = null;
        var analysisId = Guid.NewGuid().ToString();

        try
        {
            var payload = Encoding.UTF8.GetString(eventArgs.Body.Span);
            request = JsonConvert.DeserializeObject<DXFAnalysisRequest>(payload);
            if (request is null)
            {
                throw new InvalidOperationException("Payload inválido (null)");
            }

            if (string.IsNullOrWhiteSpace(request.FilePath))
            {
                throw new InvalidOperationException("Request sem filePath");
            }

            if (!File.Exists(request.FilePath))
            {
                await PublishFailureAsync(request, analysisId, "file_not_found", stopwatch.ElapsedMilliseconds, null, null, null);
                _analysisFailed?.Add(1);
                return;
            }

            fileHash = !string.IsNullOrWhiteSpace(request.FileHash)
                ? request.FileHash
                : await ComputeFileHashAsync(request.FilePath, _stoppingToken);

            if (!_options.ReprocessSameHash && _cache.TryGet(fileHash, out var cached))
            {
                if (cached is null || !ShouldReuseCachedResult(cached))
                {
                    _logger.LogInformation("Cache inválido ou desatualizado para hash={Hash} - reprocessando", fileHash);
                }
                else if (cached.Image is not null && HasRemoteImage(cached.Image))
                {
                    if (await RemoteImageAvailableAsync(cached.Image, _stoppingToken))
                    {
                        _cacheHit?.Add(1);
                        _logger.LogInformation("Cache hit com imagem remota (hash={Hash})", fileHash);
                        var cloned = CloneFromCache(cached, request, analysisId, stopwatch.ElapsedMilliseconds, fileHash);
                        PublishResult(cloned);
                        _analysisOk?.Add(1);
                        return;
                    }

                    _logger.LogInformation("Imagem remota não encontrada para cache hash={Hash} - reprocessando", fileHash);
                }
                else
                {
                    _logger.LogInformation("Cache hit sem imagem remota (hash={Hash}) - reprocessando", fileHash);
                }
            }

            _cacheMiss?.Add(1);

            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
            loadCts.CancelAfter(_options.ParseTimeout);

            var document = await Task.Run(() => LoadDocumentWithFallback(request.FilePath), loadCts.Token);
            var renderDocument = await Task.Run(() => LoadDocumentForRender(request.FilePath), loadCts.Token);
            var quality = _preprocessor.Preprocess(document, loadCts.Token);
            var geometry = _analyzer.Analyze(document, quality, loadCts.Token);

            var serrilhaSummary = geometry.Metrics.Serrilha;
            if (serrilhaSummary is not null && serrilhaSummary.UnknownCount > 0)
            {
                _serrilhaUnknown?.Add(serrilhaSummary.UnknownCount);
            }

            var scoreResult = _scorer.Compute(geometry.Metrics);

            DXFImageInfo? image = null;
            DXFRenderedImage? renderedImage = null;
            try
            {
                using var renderCts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
                renderCts.CancelAfter(_options.RenderTimeout);
                renderedImage = await _renderer.RenderAsync(analysisId, request.FilePath, geometry, scoreResult.Score, renderDocument, renderCts.Token);
                image = CreateImageInfo(renderedImage);
                NormalizeImageInfo(image);

                if (_options.ImageStorage?.Enabled == true)
                {
                    _logger.LogInformation("Iniciando upload da imagem renderizada (hash={Hash})", fileHash);
                    var uploadResult = await UploadRenderedImageAsync(
                        renderedImage,
                        fileHash,
                        request,
                        analysisId,
                        scoreResult.Score,
                        renderCts.Token);

                    ApplyUploadResult(image, uploadResult);
                    if (uploadResult.Uploaded)
                    {
                        _logger.LogInformation("Upload concluído: {Bucket}/{Key} ({Status})", uploadResult.Bucket, uploadResult.ObjectKey, uploadResult.Status);
                    }
                    else
                    {
                        _logger.LogWarning("Upload não realizado ({Status}). Motivo: {Reason}", uploadResult.Status, uploadResult.ErrorMessage ?? uploadResult.UploadMessage ?? "n/d");
                    }
                }
            }
            catch (Exception ex)
            {
                _renderFailed?.Add(1);
                _logger.LogWarning(ex, "Falha ao renderizar imagem do DXF {File}", request.FilePath);
            }

            var result = BuildResult(request, analysisId, fileHash, geometry, scoreResult, image, stopwatch.ElapsedMilliseconds);
            PublishResult(result);
            _cache.Save(result);
            _analysisOk?.Add(1);
            _analysisDuration?.Record(stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            _analysisFailed?.Add(1);
            _logger.LogError(ex, "Falha ao processar análise DXF");
            if (request != null)
            {
                await PublishFailureAsync(request, analysisId, ex.GetType().Name, stopwatch.ElapsedMilliseconds, fileHash, ex.Message, ex.StackTrace);
            }
        }
    }

    private netDxf.DxfDocument LoadDocumentWithFallback(string path)
    {
        try
        {
            return netDxf.DxfDocument.Load(path);
        }
        catch (netDxf.IO.DxfVersionNotSupportedException) when (TryLoadWithHeaderUpgrade(path, out var upgraded))
        {
            _logger.LogInformation("DXF {File} reloaded with header upgrade (AutoCAD 2000 fallback)", path);
            return upgraded!;
        }
    }

    private netDxf.DxfDocument LoadDocumentForRender(string filePath) => LoadDocumentWithFallback(filePath);

    private bool TryLoadWithHeaderUpgrade(string path, out netDxf.DxfDocument? document)
    {
        document = null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var index = IndexOf(bytes, HeaderAc1014);
            if (index < 0)
            {
                return false;
            }

            Buffer.BlockCopy(HeaderAc1015, 0, bytes, index, HeaderAc1015.Length);

            using var stream = new MemoryStream(bytes, writable: false);
            document = netDxf.DxfDocument.Load(stream);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback AutoCAD R14 falhou para {File}", path);
            document = null;
            return false;
        }
    }

    private static int IndexOf(byte[] source, byte[] pattern)
    {
        for (int i = 0; i <= source.Length - pattern.Length; i++)
        {
            var match = true;
            for (int j = 0; j < pattern.Length; j++)
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

    private DXFImageInfo CreateImageInfo(DXFRenderedImage rendered)
    {
        return new DXFImageInfo
        {
            Path = rendered.LocalPath ?? string.Empty,
            WidthPx = rendered.WidthPx,
            HeightPx = rendered.HeightPx,
            Dpi = rendered.Dpi,
            ContentType = rendered.ContentType,
            SizeBytes = rendered.Length,
            Checksum = rendered.Sha256
        };
    }

    private async Task<ImageStorageUploadResult> UploadRenderedImageAsync(
        DXFRenderedImage rendered,
        string? fileHash,
        DXFAnalysisRequest request,
        string analysisId,
        double? score,
        CancellationToken cancellationToken)
    {
        var objectKey = BuildStorageObjectKey(rendered, fileHash, analysisId);
        try
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["analysis-id"] = analysisId,
                ["file-name"] = rendered.OriginalFileName,
                ["safe-name"] = rendered.SafeName
            };

            if (!string.IsNullOrWhiteSpace(request.OpId))
            {
                metadata["op-id"] = request.OpId!;
            }

            if (score.HasValue)
            {
                metadata["score"] = score.Value.ToString("0.###", CultureInfo.InvariantCulture);
            }

            var uploadRequest = new ImageStorageUploadRequest(
                objectKey,
                rendered.ContentType,
                rendered.Length,
                () => new ValueTask<Stream>(new MemoryStream(rendered.Data, writable: false)),
                rendered.Sha256,
                metadata);

            return await _imageStorageClient.UploadAsync(uploadRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao enviar imagem renderizada para storage externo key={Key}", objectKey);
            return new ImageStorageUploadResult
            {
                Uploaded = false,
                Bucket = _options.ImageStorage?.Bucket,
                ObjectKey = objectKey,
                SizeBytes = rendered.Length,
                Status = "error",
                ErrorMessage = ex.Message
            };
        }
    }

    private string BuildStorageObjectKey(DXFRenderedImage rendered, string? fileHash, string analysisId)
    {
        var baseKey = !string.IsNullOrWhiteSpace(fileHash)
            ? fileHash.Replace(":", "_").ToLowerInvariant()
            : analysisId;

        return $"{baseKey}/{rendered.SafeName}.png";
    }

    private void ApplyUploadResult(DXFImageInfo image, ImageStorageUploadResult result)
    {
        image.StorageKey = result.ObjectKey ?? image.StorageKey;
        image.StorageBucket = result.Bucket ?? image.StorageBucket;
        image.StorageUri = result.PublicUri?.ToString() ?? image.StorageUri;
        image.UploadStatus = result.Status ?? (result.Uploaded ? "uploaded" : "skipped");
        image.UploadedAtUtc ??= DateTimeOffset.UtcNow.ToString("O");
        image.ETag = result.ETag ?? image.ETag;
        if (!string.IsNullOrWhiteSpace(result.Checksum))
        {
            image.Checksum ??= result.Checksum;
        }

        if (result.SizeBytes > 0)
        {
            image.SizeBytes = result.SizeBytes;
        }

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            image.UploadMessage = result.ErrorMessage;
        }
    }

    private DXFAnalysisResult BuildResult(
        DXFAnalysisRequest request,
        string analysisId,
        string? fileHash,
        DXFAnalysisGeometrySnapshot geometry,
        ComplexityScoreResult scoreResult,
        DXFImageInfo? image,
        long durationMs)
    {
        var metrics = geometry.Metrics;
        metrics.Quality ??= new DXFQualityMetrics();

        return new DXFAnalysisResult
        {
            AnalysisId = analysisId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            OpId = request.OpId,
            FileName = Path.GetFileName(request.FilePath),
            FileHash = fileHash,
            Metrics = metrics,
            Flags = request.Flags != null ? new Dictionary<string, object>(request.Flags) : null,
            Image = image,
            Score = scoreResult.Score,
            Explanations = scoreResult.Explanations.ToList(),
            Version = _options.Version,
            DurationMs = durationMs,
            ShadowMode = _options.ShadowMode
        };
    }

    private Task PublishFailureAsync(
        DXFAnalysisRequest request,
        string analysisId,
        string reason,
        long durationMs,
        string? fileHash,
        string? errorMessage,
        string? stackTrace)
    {
        var result = new DXFAnalysisResult
        {
            AnalysisId = analysisId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            OpId = request.OpId,
            FileName = Path.GetFileName(request.FilePath),
            FileHash = fileHash,
            Metrics = new DXFMetrics { Quality = new DXFQualityMetrics() },
            Flags = request.Flags != null ? new Dictionary<string, object>(request.Flags) : null,
            Score = null,
            Explanations = new List<string> { $"{reason}: {errorMessage ?? string.Empty}" },
            Version = _options.Version,
            DurationMs = durationMs,
            ShadowMode = _options.ShadowMode
        };

        PublishResult(result);
        return Task.CompletedTask;
    }

    private void PublishResult(DXFAnalysisResult result)
    {
        if (_publisherChannel is null)
        {
            throw new InvalidOperationException("Publisher channel não inicializado");
        }

        var payload = JsonConvert.SerializeObject(result, Formatting.None);
        var body = Encoding.UTF8.GetBytes(payload);

        var properties = _publisherChannel.CreateBasicProperties();
        properties.ContentType = "application/json";
        properties.DeliveryMode = 2;

        lock (_publishLock)
        {
            _publisherChannel.BasicPublish(
                exchange: string.Empty,
                routingKey: _options.RabbitQueueResult,
                basicProperties: properties,
                body: body);
        }

        _logger.LogInformation(
            "Resultado publicado analysisId={AnalysisId} file={File} score={Score} duration={Duration}ms",
            result.AnalysisId,
            result.FileName,
            result.Score,
            result.DurationMs);
    }

    private DXFAnalysisResult CloneFromCache(
        DXFAnalysisResult cached,
        DXFAnalysisRequest request,
        string analysisId,
        long durationMs,
        string fileHash)
    {
        var metricsCopy = CloneMetrics(cached.Metrics);
        var imageCopy = cached.Image != null ? CloneImage(cached.Image) : null;
        var flags = request.Flags != null
            ? new Dictionary<string, object>(request.Flags)
            : cached.Flags != null ? new Dictionary<string, object>(cached.Flags) : null;

        return new DXFAnalysisResult
        {
            AnalysisId = analysisId,
            TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
            OpId = request.OpId,
            FileName = Path.GetFileName(request.FilePath),
            FileHash = fileHash,
            Metrics = metricsCopy,
            Flags = flags,
            Image = imageCopy,
            Score = cached.Score,
            Explanations = cached.Explanations.ToList(),
            Version = _options.Version,
            DurationMs = durationMs,
            ShadowMode = _options.ShadowMode
        };
    }

    private static DXFMetrics CloneMetrics(DXFMetrics metrics)
    {
        var payload = JsonConvert.SerializeObject(metrics, Formatting.None);
        return JsonConvert.DeserializeObject<DXFMetrics>(payload)!;
    }

    private DXFImageInfo CloneImage(DXFImageInfo image)
    {
        var payload = JsonConvert.SerializeObject(image, Formatting.None);
        var clone = JsonConvert.DeserializeObject<DXFImageInfo>(payload)!;
        NormalizeImageInfo(clone);
        return clone;
    }

    private void NormalizeImageInfo(DXFImageInfo image)
    {
        if (_options.PersistLocalImageCopy == false)
        {
            image.Path = string.Empty;
        }
    }

    private static bool HasRemoteImage(DXFImageInfo? image)
    {
        if (image == null)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(image.StorageUri)
            || (!string.IsNullOrWhiteSpace(image.StorageBucket) && !string.IsNullOrWhiteSpace(image.StorageKey));
    }

    private async Task<bool> RemoteImageAvailableAsync(DXFImageInfo image, CancellationToken cancellationToken)
    {
        if (_options.ImageStorage?.Enabled != true)
        {
            return true;
        }

        var bucket = string.IsNullOrWhiteSpace(image.StorageBucket)
            ? _options.ImageStorage.Bucket
            : image.StorageBucket;

        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(image.StorageKey))
        {
            return false;
        }

        try
        {
            var exists = await _imageStorageClient.ObjectExistsAsync(bucket, image.StorageKey, cancellationToken);
            if (!exists)
            {
                _logger.LogWarning("Imagem remota ausente: {Bucket}/{Key}", bucket, image.StorageKey);
            }

            return exists;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao verificar existência da imagem remota {Bucket}/{Key}", bucket, image.StorageKey);
            return false;
        }
    }

    private bool ShouldReuseCachedResult(DXFAnalysisResult cached)
    {
        if (!string.Equals(cached.Version, _options.Version, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Versão da engine mudou (cache={CacheVersion}, atual={CurrentVersion}) - reprocessando", cached.Version, _options.Version);
            return false;
        }

        var image = cached.Image;
        if (image == null)
        {
            return true;
        }

        if (image.SizeBytes <= 0)
        {
            _logger.LogInformation("Imagem em cache com tamanho inválido (hash={Hash})", cached.FileHash);
            return false;
        }

        if (string.IsNullOrWhiteSpace(image.Checksum))
        {
            _logger.LogInformation("Imagem em cache sem checksum (hash={Hash}) - reprocessando", cached.FileHash);
            return false;
        }

        return true;
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            sha.TransformBlock(buffer, 0, bytesRead, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return "sha256:" + Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Parando DXFAnalysisWorker...");

        if (_consumerChannel != null)
        {
            try { _consumerChannel.Close(); } catch { /* ignore */ }
            _consumerChannel.Dispose();
        }
        if (_publisherChannel != null)
        {
            try { _publisherChannel.Close(); } catch { /* ignore */ }
            _publisherChannel.Dispose();
        }
        if (_connection != null)
        {
            try { _connection.Close(); } catch { /* ignore */ }
            _connection.Dispose();
        }

        if (_meter != null)
        {
            _meter.Dispose();
        }

        await base.StopAsync(cancellationToken);
    }
}

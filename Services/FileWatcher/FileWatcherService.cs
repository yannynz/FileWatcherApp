using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using FileWatcherApp.Messaging;
using FileWatcherApp.Services.DXFAnalysis;
using FileWatcherApp.Services.DXFAnalysis.Models;
using FileWatcherApp.Util;

namespace FileWatcherApp.Services.FileWatcher;

/// <summary>
/// Coordinates filesystem monitoring and publishing of RabbitMQ notifications plus DXF analysis requests.
/// </summary>
public sealed class FileWatcherService : BackgroundService, IDisposable
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly FileWatcherOptions _watcherOptions;
    private readonly RabbitMqOptions _rabbitOptions;
    private readonly DXFAnalysisOptions _analysisOptions;

    private readonly JsonSerializerOptions _jsonOpts = new(JsonSerializerDefaults.Web);
    private readonly TimeZoneInfo _saoPauloTimeZone;

    private readonly ConcurrentDictionary<string, System.Timers.Timer> _fileDebouncers = new();
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _opDebouncers = new();
    private readonly ConcurrentDictionary<string, DateTime> _dobrasSeen = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _dobrasDedupWindow = TimeSpan.FromMinutes(2);

    private readonly object _publishLock = new();

    private IConnection? _connection;
    private IModel? _channel;

    private FileSystemWatcher? _laserWatcher;
    private FileSystemWatcher? _facasWatcher;
    private FileSystemWatcher? _dobrarWatcher;
    private FileSystemWatcher? _opWatcher;

    private string _laserDir = string.Empty;
    private string _facasDir = string.Empty;
    private string _dobrasDir = string.Empty;
    private string _opsDir = string.Empty;
    private string _destacadorDir = string.Empty;

    private readonly string _laserQueue;
    private readonly string _facasQueue;
    private readonly string _dobraQueue;
    private readonly string _opsQueue;

    private readonly bool _analysisEnabled;
    private readonly string _analysisQueue;
    private readonly string _analysisExchange;

    private readonly Regex _toolingRegex = new(
        @"^NR(?<nr>\d+)(?<cliente>[A-Z0-9]+)_(?<sexo>MACHO|FEMEA)_(?<cor>[A-Z0-9]+)\.CNC$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ConcurrentQueue<AnalysisWorkItem> _analysisWorkQueue = new();
    private readonly SemaphoreSlim _analysisSignal = new(0);
    private readonly ConcurrentDictionary<string, byte> _analysisDedup = new(StringComparer.OrdinalIgnoreCase);
    private Task? _analysisWorker;

    public FileWatcherService(
        ILogger<FileWatcherService> logger,
        IOptions<FileWatcherOptions> watcherOptions,
        IOptions<RabbitMqOptions> rabbitOptions,
        IOptions<DXFAnalysisOptions> analysisOptions)
    {
        _logger = logger;
        _watcherOptions = watcherOptions.Value;
        _rabbitOptions = rabbitOptions.Value;
        _analysisOptions = analysisOptions.Value;

        _saoPauloTimeZone = ResolveSaoPauloTimeZone();

        _laserQueue = ResolveQueueName("Laser", "laser_notifications");
        _facasQueue = ResolveQueueName("Facas", "facas_notifications");
        _dobraQueue = ResolveQueueName("Dobra", "dobra_notifications");
        _opsQueue = ResolveQueueName("Ops", "op.imported");

        _analysisQueue = string.IsNullOrWhiteSpace(_watcherOptions.AnalysisRequestQueue)
            ? _analysisOptions.RabbitQueueRequest
            : _watcherOptions.AnalysisRequestQueue;

        _analysisExchange = _watcherOptions.AnalysisRequestExchange ?? string.Empty;
        _analysisEnabled = _watcherOptions.EnableAnalysisPublishing &&
            !string.IsNullOrWhiteSpace(_analysisQueue);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ConsoleHelper.DisableQuickEdit();
        }

        var osDescription = RuntimeInformation.OSDescription.Trim();
        _logger.LogInformation("[BOOT] FileWatcherService em {OS} ({Arch}) Machine='{Machine}'",
            osDescription, RuntimeInformation.OSArchitecture, Environment.MachineName);

        InitializeDirectories();
        InitializeRabbitMq();
        InitializeWatchers();
        StartAnalysisWorker(stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // expected on shutdown
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Encerrando FileWatcherService...");
        DisposeWatchers();
        DisposeRabbitMq();
        return base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        base.Dispose();
        DisposeWatchers();
        DisposeRabbitMq();
    }

    private void InitializeDirectories()
    {
        _laserDir = ResolveDirectory("LASER_DIR", _watcherOptions.LaserDirectory, @"D:\Laser", "/home/laser");
        _facasDir = ResolveDirectory("FACAS_DIR", _watcherOptions.FacasDirectory, @"D:\Laser\FACAS OK", "/home/laser/FACASOK");
        _dobrasDir = ResolveDirectory("DOBRAS_DIR", _watcherOptions.DobrasDirectory, @"D:\Dobradeira\Facas para Dobrar", "/home/dobras");
        _opsDir = ResolveDirectory("OPS_DIR", _watcherOptions.OpsDirectory, @"D:\Laser\NR", "/home/nr");
        _destacadorDir = Path.Combine(_laserDir, _watcherOptions.DestacadorSubfolderName ?? "DESTACADOR");

        TryPrepareDirectory(_laserDir, "Laser watcher");
        TryPrepareDirectory(_facasDir, "Facas watcher");
        TryPrepareDirectory(_dobrasDir, "Dobras watcher");
        TryPrepareDirectory(_opsDir, "OP watcher");

        _logger.LogInformation("[CFG] Pastas monitoradas: Laser='{Laser}' Facas='{Facas}' Dobras='{Dobras}' Ops='{Ops}' Destacador='{Destacador}'",
            _laserDir, _facasDir, _dobrasDir, _opsDir, _destacadorDir);
    }

    private void InitializeRabbitMq()
    {
        var factory = new ConnectionFactory
        {
            HostName = _rabbitOptions.HostName,
            Port = _rabbitOptions.Port,
            UserName = _rabbitOptions.UserName,
            Password = _rabbitOptions.Password,
            VirtualHost = _rabbitOptions.VirtualHost,
            AutomaticRecoveryEnabled = _rabbitOptions.AutomaticRecoveryEnabled,
            TopologyRecoveryEnabled = _rabbitOptions.TopologyRecoveryEnabled,
            RequestedHeartbeat = TimeSpan.FromSeconds(_rabbitOptions.RequestedHeartbeatSeconds),
            DispatchConsumersAsync = true
        };

        _connection = factory.CreateConnection();
        _channel = _connection.CreateModel();

        DeclareQueue(_laserQueue);
        DeclareQueue(_facasQueue);
        DeclareQueue(_dobraQueue);
        DeclareQueue(_opsQueue);

        if (_analysisEnabled)
        {
            DeclareQueue(_analysisQueue);
        }

        _logger.LogDebug("RabbitMQ conectado em {Host}:{Port}", _rabbitOptions.HostName, _rabbitOptions.Port);
    }

    private void DeclareQueue(string queue)
    {
        if (string.IsNullOrWhiteSpace(queue) || _channel is null)
        {
            return;
        }

        _channel.QueueDeclare(queue: queue, durable: true, exclusive: false, autoDelete: false, arguments: null);
    }

    private void InitializeWatchers()
    {
        _laserWatcher = CreateFileWatcher(_laserDir, _laserQueue, "LASER");
        _facasWatcher = CreateFileWatcher(_facasDir, _facasQueue, "FACAS");
        _dobrarWatcher = CreateDobrasWatcher(_dobrasDir, _dobraQueue, "DOBRAS");
        _opWatcher = CreateOpWatcher(_opsDir, _opsQueue, "OPS");
    }

    private FileSystemWatcher? CreateFileWatcher(string path, string queueName, string watcherLabel)
    {
        if (!TryPrepareDirectory(path, $"watcher {queueName}"))
        {
            return null;
        }

        var watcher = new FileSystemWatcher
        {
            Path = path,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*",
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024
        };

        void EnqueueEvent(FileSystemEventArgs e, bool logEvent = true)
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            if (logEvent)
            {
                LogWatcherEvent(watcherLabel, e.ChangeType, e.FullPath);
            }

            var key = e.FullPath;
            _fileDebouncers.AddOrUpdate(
                key,
                _ => NewTimer(key, queueName, watcherLabel),
                (_, existing) =>
                {
                    existing.Stop();
                    existing.Start();
                    return existing;
                });
        }

        watcher.Created += (_, e) => EnqueueEvent(e);
        watcher.Changed += (_, e) => EnqueueEvent(e);
        watcher.Renamed += (_, e) =>
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            LogWatcherRename(watcherLabel, e.OldFullPath, e.FullPath);

            var normalized = new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                Path.GetDirectoryName(e.FullPath) ?? string.Empty,
                Path.GetFileName(e.FullPath) ?? string.Empty);

            EnqueueEvent(normalized, logEvent: false);
        };

        watcher.Error += (s, e) =>
        {
            _logger.LogError(e.GetException(), "Erro no FileSystemWatcher em '{Path}'", path);
        };

        watcher.EnableRaisingEvents = true;
        _logger.LogInformation("[WATCHER-{Label}] Ativo em '{Path}' (Queue={Queue})", watcherLabel, path, queueName);
        return watcher;

        System.Timers.Timer NewTimer(string fullPath, string q, string label)
        {
            var t = new System.Timers.Timer(_watcherOptions.DebounceIntervalMilliseconds)
            {
                AutoReset = false
            };

            t.Elapsed += async (_, __) =>
            {
                try
                {
                    if (string.Equals(q, _laserQueue, StringComparison.OrdinalIgnoreCase) &&
                        IsUnder(fullPath, _facasDir))
                    {
                        return;
                    }

                    if (string.Equals(q, _facasQueue, StringComparison.OrdinalIgnoreCase) &&
                        !IsUnder(fullPath, _facasDir))
                    {
                        return;
                    }

                    var fileName = Path.GetFileName(fullPath);
                    var args = new FileSystemEventArgs(WatcherChangeTypes.Changed,
                        Path.GetDirectoryName(fullPath) ?? string.Empty,
                        fileName ?? string.Empty);

                    if (_toolingRegex.IsMatch(fileName ?? string.Empty))
                    {
                        await HandleToolingFileAsync(args, q, label);
                    }
                    else
                    {
                        await HandleNewFileAsync(args, q, label);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[LASER/FACAS] Erro ao processar '{Path}'", fullPath);
                }
                finally
                {
                    _fileDebouncers.TryRemove(fullPath, out System.Timers.Timer? _);
                    t.Dispose();
                }
            };

            t.Start();
            return t;
        }
    }

    private FileSystemWatcher? CreateDobrasWatcher(string path, string queueName, string watcherLabel)
    {
        if (!TryPrepareDirectory(path, "dobras watcher"))
        {
            return null;
        }

        var watcher = new FileSystemWatcher
        {
            Path = path,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
            Filter = "*",
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024
        };

        async Task ProcessEvent(FileSystemEventArgs e, bool logEvent = true)
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            if (logEvent)
            {
                LogWatcherEvent(watcherLabel, e.ChangeType, e.FullPath);
            }

            await HandleDobrasFileAsync(e, queueName, watcherLabel);
        }

        watcher.Created += async (_, e) => await ProcessEvent(e);
        watcher.Changed += async (_, e) => await ProcessEvent(e);
        watcher.Renamed += async (_, e) =>
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            LogWatcherRename(watcherLabel, e.OldFullPath, e.FullPath);

            var normalized = new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                Path.GetDirectoryName(e.FullPath) ?? string.Empty,
                Path.GetFileName(e.FullPath) ?? string.Empty);

            await ProcessEvent(normalized, logEvent: false);
        };

        watcher.Error += (s, e) =>
        {
            _logger.LogError(e.GetException(), "Erro no FileSystemWatcher em '{Path}'", path);
        };

        watcher.EnableRaisingEvents = true;
        _logger.LogInformation("[WATCHER-{Label}] Ativo em '{Path}' (Queue={Queue})", watcherLabel, path, queueName);
        return watcher;
    }

    private FileSystemWatcher? CreateOpWatcher(string path, string queueName, string watcherLabel)
    {
        if (!TryPrepareDirectory(path, "OP watcher"))
        {
            return null;
        }

        var watcher = new FileSystemWatcher
        {
            Path = path,
            Filter = "*",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };

        void EnqueueEvent(FileSystemEventArgs e, bool logEvent = true)
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            var extension = Path.GetExtension(e.FullPath) ?? string.Empty;
            if (!extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (logEvent)
            {
                LogWatcherEvent(watcherLabel, e.ChangeType, e.FullPath);
            }

            var key = e.FullPath;
            _opDebouncers.AddOrUpdate(
                key,
                _ => NewTimer(key, queueName, watcherLabel),
                (_, existing) =>
                {
                    existing.Stop();
                    existing.Start();
                    return existing;
                });
        }

        watcher.Created += (_, e) => EnqueueEvent(e);
        watcher.Changed += (_, e) => EnqueueEvent(e);
        watcher.Renamed += (_, e) =>
        {
            if (Directory.Exists(e.FullPath))
            {
                return;
            }

            LogWatcherRename(watcherLabel, e.OldFullPath, e.FullPath);

            var normalized = new FileSystemEventArgs(
                WatcherChangeTypes.Changed,
                Path.GetDirectoryName(e.FullPath) ?? string.Empty,
                Path.GetFileName(e.FullPath) ?? string.Empty);

            EnqueueEvent(normalized, logEvent: false);
        };

        watcher.Error += (s, e) =>
        {
            _logger.LogError(e.GetException(), "Erro no FileSystemWatcher(OP) em '{Path}'", path);
        };

        watcher.EnableRaisingEvents = true;
        _logger.LogInformation("[WATCHER-{Label}] Ativo em '{Path}' (Queue={Queue})", watcherLabel, path, queueName);
        return watcher;

        System.Timers.Timer NewTimer(string fullPath, string q, string label)
        {
            var t = new System.Timers.Timer(_watcherOptions.DebounceIntervalMilliseconds)
            {
                AutoReset = false
            };

            t.Elapsed += async (_, __) =>
            {
                try
                {
                    var ext = Path.GetExtension(fullPath) ?? string.Empty;
                    if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    await HandleOpFileAsync(fullPath, q, label);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[OP] Erro ao processar '{Path}'", fullPath);
                }
                finally
                {
                    _opDebouncers.TryRemove(fullPath, out System.Timers.Timer? _);
                    t.Dispose();
                }
            };

            t.Start();
            return t;
        }
    }

    private async Task HandleToolingFileAsync(FileSystemEventArgs e, string queueName, string watcherLabel)
    {
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        _logger.LogInformation("[PROCESS-{Label}] TOOLING queue={Queue} file='{File}' path='{Path}'",
            watcherLabel, queueName, e.Name, e.FullPath);

        if (!await WaitFileReadyAsync(e.FullPath, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200), 2))
        {
            _logger.LogWarning("[TOOLING] Arquivo não ficou pronto a tempo: '{File}'", e.Name);
            return;
        }

        var message = new
        {
            file_name = e.Name,
            path = e.FullPath,
            timestamp = GetSaoPauloTimestamp()
        };

        var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));
        try
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                await Task.Delay(50);
                PublishLegacyNotification(queueName, message);
            });

            var fase = string.Equals(queueName, _facasQueue, StringComparison.OrdinalIgnoreCase) ? "CUT" : "NEW";
            _logger.LogInformation("[TOOLING-{Fase}] publicado em {Queue}: {File}", fase, queueName, e.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TOOLING] Erro após {Attempts} tentativas", retryPolicy.MaxRetries);
        }
    }

    private async Task HandleNewFileAsync(FileSystemEventArgs e, string queueName, string watcherLabel)
    {
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        var originalName = e.Name ?? string.Empty;
        var cleanName = FileWatcherNaming.CleanFileName(originalName);
        var isDxf = string.Equals(Path.GetExtension(originalName), ".dxf", StringComparison.OrdinalIgnoreCase);
        var skipAnalysis = isDxf && ShouldSkipDxf(originalName);

        if (string.IsNullOrEmpty(cleanName))
        {
            if (!isDxf)
            {
                _logger.LogDebug("Arquivo '{File}' ignorado por formato inválido.", originalName);
                return;
            }

            _logger.LogInformation("[PROCESS-{Label}] Arquivo DXF '{Original}' fora do padrão; enviando para análise (queue={Queue})",
                watcherLabel, originalName, queueName);
        }
        else
        {
            _logger.LogInformation("[PROCESS-{Label}] Arquivo '{Clean}' (orig='{Original}') path='{Path}' queue={Queue}",
                watcherLabel, cleanName, originalName, e.FullPath, queueName);

            var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));

            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await Task.Delay(100);

                    var message = new
                    {
                        file_name = cleanName,
                        path = e.FullPath,
                        timestamp = GetSaoPauloTimestamp()
                    };

                    PublishLegacyNotification(queueName, message);
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao publicar notificação após {Attempts} tentativas", retryPolicy.MaxRetries);
            }
        }

        if (_analysisEnabled && isDxf && !skipAnalysis)
        {
            string? opId = null;
            var normalizedForOp = cleanName ?? originalName;

            if (FileWatcherNaming.TryExtractOpId(normalizedForOp, out var extracted))
            {
                opId = extracted;
            }

            var resolvedPath = ResolveAnalysisFilePath(e.FullPath, opId);
            EnqueueAnalysisRequest(opId, e.FullPath, resolvedPath, normalizedForOp, queueName);
        }
    }

    private async Task HandleDobrasFileAsync(FileSystemEventArgs e, string queueName, string watcherLabel)
    {
        if (Directory.Exists(e.FullPath))
        {
            return;
        }

        var originalName = e.Name ?? string.Empty;
        var upperName = originalName.Trim().ToUpperInvariant();

        foreach (var reserved in FileWatcherNamingReservedWords)
        {
            if (upperName.Contains(reserved, StringComparison.Ordinal))
            {
                _logger.LogDebug("[DOBRAS] Ignorado por palavra reservada: '{File}'", originalName);
                return;
            }
        }

        if (!FileWatcherNaming.TrySanitizeDobrasName(originalName, out var nr, out var sanitizedName))
        {
            _logger.LogDebug("[DOBRAS] Ignorado por padrão não correspondente: '{File}'", originalName);
            return;
        }

        _logger.LogInformation("[PROCESS-{Label}] DOBRAS arquivo='{Sanitized}' (orig='{Original}') path='{Path}' queue={Queue}",
            watcherLabel, sanitizedName, originalName, e.FullPath, queueName);

        var hasSavedSuffix = FileWatcherNaming.HasDobrasSavedSuffix(originalName);
        var skipAnalysis = ShouldSkipDxf(originalName);

        if (!await WaitFileReadyAsync(e.FullPath, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200), 2))
        {
            _logger.LogWarning("[DOBRAS] Arquivo não ficou pronto a tempo: '{File}'", originalName);
            return;
        }

        if (!hasSavedSuffix)
        {
            if (!_analysisEnabled)
            {
                _logger.LogDebug("[DOBRAS] Análise ignorada (analysis desabilitado) para '{File}'", originalName);
                return;
            }

            string? opId = null;
            if (FileWatcherNaming.TryExtractOpId(sanitizedName, out var extracted))
            {
                opId = extracted;
            }

            if (skipAnalysis)
            {
                _logger.LogInformation("[DOBRAS] Análise suprimida (sufixo não permitido) para '{File}'", originalName);
            }
            else
            {
                var resolvedPath = ResolveAnalysisFilePath(e.FullPath, opId);
                EnqueueAnalysisRequest(opId, e.FullPath, resolvedPath, sanitizedName, queueName);
                _logger.LogInformation("[DOBRAS] Análise solicitada (NR={Nr}) a partir de '{Original}'", nr, originalName);
            }
            _logger.LogDebug("[DOBRAS] Publicação na fila '{Queue}' suprimida para '{Original}' sem sufixo salvo", queueName, originalName);
            return;
        }

        var now = DateTime.UtcNow;
        PruneDobrasSeen(now);
        if (_dobrasSeen.TryGetValue(nr, out var last) && now - last < _dobrasDedupWindow)
        {
            _logger.LogDebug("[DOBRAS] Evento duplicado suprimido (NR={Nr}) para '{File}'", nr, originalName);
            return;
        }

        _dobrasSeen.AddOrUpdate(nr, now, (_, __) => now);

        var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));

        try
        {
            await retryPolicy.ExecuteAsync(async () =>
            {
                await Task.Delay(50);
                var message = new
                {
                    file_name = sanitizedName,
                    original_file_name = originalName,
                    path = e.FullPath,
                    timestamp = GetSaoPauloTimestamp()
                };

                PublishLegacyNotification(queueName, message);
            });

            _logger.LogInformation("[DOBRAS] Mensagem publicada (NR={Nr}) a partir de '{Original}' como '{Sanitized}'",
                nr, originalName, sanitizedName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DOBRAS] Erro após {Attempts} tentativas", retryPolicy.MaxRetries);
        }
    }

    private async Task HandleOpFileAsync(string fullPath, string queueName, string watcherLabel)
    {
        if (Directory.Exists(fullPath))
        {
            return;
        }

        _logger.LogInformation("[PROCESS-{Label}] OP detectada file='{File}' path='{Path}' queue={Queue}",
            watcherLabel, Path.GetFileName(fullPath), fullPath, queueName);

        if (!await WaitFileReadyAsync(fullPath, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250), 3))
        {
            _logger.LogWarning("[OP] Arquivo não ficou pronto a tempo: '{File}'", Path.GetFileName(fullPath));
            return;
        }

        var parsed = PdfParser.Parse(fullPath);

        var materialTokens = parsed.Materiais?.ToList() ?? new List<string>();

        var emborrachada = DetectEmbossing(parsed);
        var vaiVinco = DetectVinco(parsed);

        string? dataOp = NormalizeDate(parsed.DataOpIso);
        string? dataReq = NormalizeDateTime(parsed.DataEntregaIso, parsed.HoraEntrega);

        var message = new
        {
            numeroOp = parsed.NumeroOp,
            codigoProduto = parsed.CodigoProduto,
            descricaoProduto = parsed.DescricaoProduto,
            cliente = parsed.Cliente,
            dataOp,
            materiais = materialTokens,
            emborrachada,
            vaiVinco,
            sharePath = fullPath,
            destacador = parsed.Destacador,
            modalidadeEntrega = string.IsNullOrWhiteSpace(parsed.ModalidadeEntrega) ? null : parsed.ModalidadeEntrega,
            dataRequeridaEntrega = dataReq,
            usuarioImportacao = parsed.Usuario,
            pertinax = parsed.Pertinax,
            poliester = parsed.Poliester,
            papelCalibrado = parsed.PapelCalibrado,
            clienteNomeOficial = parsed.ClienteNomeOficial,
            apelidosSugeridos = parsed.ApelidosSugeridos,
            enderecosSugeridos = parsed.EnderecosSugeridos,
            padraoEntregaSugerido = parsed.PadraoEntregaSugerido,
            dataUltimoServicoSugerida = parsed.DataUltimoServicoSugerida,
            cnpjCpf = parsed.CnpjCpf,
            inscricaoEstadual = parsed.InscricaoEstadual,
            telefone = parsed.Telefone,
            email = parsed.Email
        };

        PublishLegacyNotification(queueName, message);

        _logger.LogInformation("[OP] Import publicada: {NumeroOp} (emborrachada={Emb}, vaiVinco={Vinco})",
            parsed.NumeroOp, emborrachada, vaiVinco);

        // DXF correspondente será tratado quando o arquivo for disponibilizado nas pastas monitoradas.
    }

    private void PublishLegacyNotification(string queueName, object payload)
    {
        if (_channel is null)
        {
            return;
        }

        var body = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload, _jsonOpts));
        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.ContentType = "application/json";
        props.ContentEncoding = "utf-8";

        lock (_publishLock)
        {
            _channel.BasicPublish(exchange: string.Empty, routingKey: queueName, basicProperties: props, body: body);
        }
    }

    private void PublishAnalysisRequest(string? opId, string sourcePath, string resolvedPath, string? normalizedName, string sourceQueue)
    {
        if (!_analysisEnabled || _channel is null)
        {
            return;
        }

        var request = new DXFAnalysisRequest
        {
            OpId = opId,
            FilePath = resolvedPath,
            Flags = new Dictionary<string, object?>
            {
                ["sourceQueue"] = sourceQueue,
                ["normalizedName"] = normalizedName,
                ["sourcePath"] = sourcePath
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!),
            Meta = new Dictionary<string, object?>
            {
                ["requestedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["originalPath"] = sourcePath,
                ["resolvedPath"] = resolvedPath
            }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!)
        };

        var payload = JsonConvert.SerializeObject(request);
        var body = Encoding.UTF8.GetBytes(payload);

        var props = _channel.CreateBasicProperties();
        props.Persistent = true;
        props.ContentType = "application/json";
        props.ContentEncoding = "utf-8";

        lock (_publishLock)
        {
            _channel.BasicPublish(exchange: _analysisExchange ?? string.Empty, routingKey: _analysisQueue, basicProperties: props, body: body);
        }

        _logger.LogInformation("[DXF] Request publicado opId={OpId} file='{File}' resolved='{Resolved}'", opId, sourcePath, resolvedPath);
    }

    private void EnqueueAnalysisRequest(string? opId, string sourcePath, string resolvedPath, string? normalizedName, string sourceQueue)
    {
        if (!_analysisEnabled)
        {
            return;
        }

        if (!_analysisDedup.TryAdd(resolvedPath, 0))
        {
            _logger.LogDebug("[DXF] Requisição já enfileirada para '{File}'", resolvedPath);
            return;
        }

        _analysisWorkQueue.Enqueue(new AnalysisWorkItem
        {
            OpId = opId,
            SourcePath = sourcePath,
            ResolvedPath = resolvedPath,
            NormalizedName = normalizedName,
            SourceQueue = sourceQueue
        });
        _analysisSignal.Release();
        _logger.LogDebug("[DXF] Requisição enfileirada file='{File}'", resolvedPath);
    }

    private void StartAnalysisWorker(CancellationToken stoppingToken)
    {
        if (!_analysisEnabled)
        {
            return;
        }

        _analysisWorker = Task.Run(() => RunAnalysisWorkerAsync(stoppingToken), stoppingToken);
    }

    private async Task RunAnalysisWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _analysisSignal.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_analysisWorkQueue.TryDequeue(out var item))
            {
                await ProcessAnalysisWorkItemAsync(item, stoppingToken);
            }
        }
    }

    private async Task ProcessAnalysisWorkItemAsync(AnalysisWorkItem item, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (!File.Exists(item.ResolvedPath))
        {
            _analysisDedup.TryRemove(item.ResolvedPath, out _);
            _logger.LogWarning("[DXF] Arquivo não encontrado para análise: '{File}'", item.ResolvedPath);
            return;
        }

        const int maxAttempts = 6;
        var ready = await WaitForStableFileAsync(
            item.ResolvedPath,
            maxWait: TimeSpan.FromSeconds(60),
            sampleInterval: TimeSpan.FromMilliseconds(500),
            stableSamples: 3,
            stoppingToken);

        if (!ready)
        {
            if (item.Attempts < maxAttempts)
            {
                item.Attempts++;
                var delaySeconds = Math.Min(60, 5 * item.Attempts);
                _logger.LogWarning("[DXF] Arquivo ainda em uso, refileirando ({Attempt}/{Max}) '{File}'",
                    item.Attempts, maxAttempts, item.ResolvedPath);
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                _analysisWorkQueue.Enqueue(item);
                _analysisSignal.Release();
                return;
            }

            _analysisDedup.TryRemove(item.ResolvedPath, out _);
            _logger.LogWarning("[DXF] Arquivo não ficou pronto a tempo após {Max} tentativas: '{File}'",
                maxAttempts, item.ResolvedPath);
            return;
        }

        try
        {
            PublishAnalysisRequest(item.OpId, item.SourcePath, item.ResolvedPath, item.NormalizedName, item.SourceQueue);
        }
        finally
        {
            _analysisDedup.TryRemove(item.ResolvedPath, out _);
        }
    }

    private static async Task<bool> WaitForStableFileAsync(
        string path,
        TimeSpan maxWait,
        TimeSpan sampleInterval,
        int stableSamples,
        CancellationToken stoppingToken)
    {
        var deadline = DateTime.UtcNow + maxWait;
        long? lastSize = null;
        DateTime? lastWrite = null;
        var stableCount = 0;

        while (DateTime.UtcNow < deadline && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    return false;
                }

                var size = info.Length;
                var write = info.LastWriteTimeUtc;
                if (lastSize.HasValue && lastWrite.HasValue && size == lastSize && write == lastWrite)
                {
                    stableCount++;
                }
                else
                {
                    stableCount = 0;
                    lastSize = size;
                    lastWrite = write;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stableCount >= stableSamples)
                {
                    return true;
                }
            }
            catch (IOException)
            {
                stableCount = 0;
            }
            catch (UnauthorizedAccessException)
            {
                stableCount = 0;
            }

            await Task.Delay(sampleInterval, stoppingToken);
        }

        return false;
    }

    private static bool ShouldSkipDxf(string fileName)
    {
        var upper = (fileName ?? string.Empty).Trim().ToUpperInvariant();
        return upper.EndsWith(".M.DXF", StringComparison.Ordinal) || upper.EndsWith(".FCD.DXF", StringComparison.Ordinal);
    }

    private sealed class AnalysisWorkItem
    {
        public string? OpId { get; init; }
        public string SourcePath { get; init; } = string.Empty;
        public string ResolvedPath { get; init; } = string.Empty;
        public string? NormalizedName { get; init; }
        public string SourceQueue { get; init; } = string.Empty;
        public int Attempts { get; set; }
    }

    private string ResolveAnalysisFilePath(string sourcePath, string? opId)
    {
        if (string.IsNullOrWhiteSpace(opId))
        {
            return sourcePath;
        }

        if (!Directory.Exists(_dobrasDir))
        {
            return sourcePath;
        }

        try
        {
            var rawOp = opId;
            var formattedOp = opId.StartsWith("NR", StringComparison.OrdinalIgnoreCase)
                ? opId.Insert(2, " ")
                : opId;

            var matches = Directory.EnumerateFiles(_dobrasDir, "*", SearchOption.TopDirectoryOnly)
                .Where(File.Exists)
                .Where(path =>
                {
                    var fileName = Path.GetFileName(path) ?? string.Empty;
                    if (!fileName.StartsWith(formattedOp, StringComparison.OrdinalIgnoreCase) &&
                        !fileName.StartsWith(rawOp, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    return fileName.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (matches.Count == 0)
            {
                return sourcePath;
            }

            matches.Sort(CompareDobrasCandidates);
            var resolved = matches[0];
            if (!string.Equals(resolved, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("[DXF] opId={OpId} arquivo resolvido '{Source}' -> '{Resolved}'", opId, sourcePath, resolved);
            }

            return resolved;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falha ao resolver arquivo de Dobras para opId={OpId}", opId);
            return sourcePath;
        }
    }

    private static int CompareDobrasCandidates(string left, string right)
    {
        static int Score(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            var savedWeight = FileWatcherNaming.HasDobrasSavedSuffix(name) ? 0 : 1;

            var statusWeight = 3;
            if (name.Contains("FINAL", StringComparison.OrdinalIgnoreCase)) statusWeight = 0;
            else if (name.Contains("OK", StringComparison.OrdinalIgnoreCase)) statusWeight = 1;
            else if (name.Contains("PRODUCAO", StringComparison.OrdinalIgnoreCase)) statusWeight = 2;

            return savedWeight * 10 + statusWeight;
        }

        var scoreDiff = Score(left) - Score(right);
        if (scoreDiff != 0)
        {
            return scoreDiff;
        }

        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private void LogWatcherEvent(string watcherLabel, WatcherChangeTypes changeType, string fullPath)
    {
        var directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
        var fileName = Path.GetFileName(fullPath) ?? string.Empty;
        _logger.LogInformation("[WATCHER-{Label}] {Change} file='{File}' dir='{Dir}' full='{Full}'",
            watcherLabel, changeType, fileName, directory, fullPath);
    }

    private void LogWatcherRename(string watcherLabel, string? oldFullPath, string? newFullPath)
    {
        var oldDisplay = oldFullPath ?? "<unknown>";
        var newDisplay = newFullPath ?? "<unknown>";
        _logger.LogInformation("[WATCHER-{Label}] MOVE '{Old}' -> '{New}'", watcherLabel, oldDisplay, newDisplay);
    }

    private double GetSaoPauloTimestamp()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _saoPauloTimeZone);
        var dto = new DateTimeOffset(now);
        long ticks = dto.ToUnixTimeMilliseconds() * 10_000;
        return ticks / (double)TimeSpan.TicksPerSecond;
    }

    private static bool DetectEmbossing(PdfParser.ParsedOp parsed)
    {
        if (parsed.Emborrachada)
        {
            return true;
        }

        if (parsed.Pertinax == true || parsed.Poliester == true || parsed.PapelCalibrado == true)
        {
            return true;
        }

        var materials = parsed.Materiais?.AsEnumerable() ?? Array.Empty<string>();
        var joined = string.Join(" ", materials).ToUpperInvariant();
        return joined.Contains("BOR ") ||
               joined.Contains("BORRACHA") ||
               joined.Contains("SHORE") ||
               joined.Contains("EMBORRACH");
    }

    private static bool DetectVinco(PdfParser.ParsedOp parsed)
    {
        if (parsed.VaiVinco)
        {
            return true;
        }

        var materials = parsed.Materiais?.AsEnumerable() ?? Array.Empty<string>();
        var joined = string.Join(" ", materials).ToUpperInvariant();
        return joined.Contains("VINCO", StringComparison.Ordinal);
    }

    private string? NormalizeDate(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return null;
        }

        if (DateTime.TryParse(isoDate, out var date))
        {
            var local = new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
            var offset = _saoPauloTimeZone.GetUtcOffset(local);
            var dto = new DateTimeOffset(local, offset);
            return dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "[America/Sao_Paulo]";
        }

        return null;
    }

    private string? NormalizeDateTime(string? isoDate, string? isoTime)
    {
        if (string.IsNullOrWhiteSpace(isoDate) || string.IsNullOrWhiteSpace(isoTime))
        {
            return null;
        }

        try
        {
            var parts = isoDate.Split('-');
            var hm = isoTime.Split(':');

            int y = int.Parse(parts[0]);
            int m = int.Parse(parts[1]);
            int d = int.Parse(parts[2]);

            int hh = int.Parse(hm[0]);
            int mm = int.Parse(hm[1]);

            var local = new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Unspecified);
            var offset = _saoPauloTimeZone.GetUtcOffset(local);
            var dto = new DateTimeOffset(local, offset);
            return dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "[America/Sao_Paulo]";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string fullPath, string directory)
    {
        if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var dirWithSep = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullPath.StartsWith(dirWithSep, comparison) ||
               string.Equals(fullPath, directory, comparison);
    }

    private async Task<bool> WaitFileReadyAsync(string fullPath, TimeSpan timeout, TimeSpan pollInterval, int stableReads)
    {
        var sw = Stopwatch.StartNew();
        long? lastLen = null;
        int stable = 0;

        while (sw.Elapsed < timeout)
        {
            long len = -1;
            try
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                len = stream.Length;
            }
            catch
            {
                // file not ready yet
            }

            if (len >= 0)
            {
                if (lastLen.HasValue && len == lastLen.Value)
                {
                    stable++;
                }
                else
                {
                    stable = 1;
                    lastLen = len;
                }

                if (stable >= stableReads)
                {
                    try
                    {
                        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                        if (stream.Length >= 0)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // still locked
                    }
                }
            }

            await Task.Delay(pollInterval);
        }

        return false;
    }

    private void PruneDobrasSeen(DateTime now)
    {
        foreach (var kvp in _dobrasSeen)
        {
            if (now - kvp.Value > _dobrasDedupWindow)
            {
                _dobrasSeen.TryRemove(kvp.Key, out _);
            }
        }
    }

    private void DisposeWatchers()
    {
        _laserWatcher?.Dispose();
        _laserWatcher = null;
        _facasWatcher?.Dispose();
        _facasWatcher = null;
        _dobrarWatcher?.Dispose();
        _dobrarWatcher = null;
        _opWatcher?.Dispose();
        _opWatcher = null;
    }

    private void DisposeRabbitMq()
    {
        try { _channel?.Close(); } catch { }
        try { _channel?.Dispose(); } catch { }
        try { _connection?.Close(); } catch { }
        try { _connection?.Dispose(); } catch { }
        _channel = null;
        _connection = null;
    }

    private static TimeZoneInfo ResolveSaoPauloTimeZone()
    {
        var preferred = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[] { "E. South America Standard Time", "America/Sao_Paulo" }
            : new[] { "America/Sao_Paulo", "E. South America Standard Time" };

        foreach (var id in preferred)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
                // try next
            }
            catch (InvalidTimeZoneException)
            {
                // try next
            }
        }

        return TimeZoneInfo.Local;
    }

    private bool TryPrepareDirectory(string path, string description)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                _logger.LogInformation("[CFG] Created missing directory '{Path}' for {Description}.", path, description);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CFG] Unable to initialize directory '{Path}' for {Description}", path, description);
            return false;
        }
    }

    private string ResolveDirectory(string envVarName, string? configuredValue, string windowsDefault, string linuxDefault)
    {
        var env = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        if (!string.IsNullOrWhiteSpace(configuredValue))
        {
            return configuredValue!;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return windowsDefault;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return linuxDefault;
        }

        return windowsDefault;
    }

    private string ResolveQueueName(string key, string fallback)
    {
        if (_watcherOptions.QueueNames.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback;
    }

    private static IReadOnlyList<string> FileWatcherNamingReservedWords => new[]
    {
        "MODELO", "BORRACHA", "REGUA", "MACHO", "FEMEA", "BLOCO", "RELEVO"
    };

    private sealed class RetryPolicy
    {
        private readonly int _maxRetries;
        private readonly TimeSpan _initialDelay;

        public RetryPolicy(int maxRetries, TimeSpan initialDelay)
        {
            _maxRetries = maxRetries;
            _initialDelay = initialDelay;
        }

        public int MaxRetries => _maxRetries;

        public async Task ExecuteAsync(Func<Task> action)
        {
            var retryCount = 0;
            while (true)
            {
                try
                {
                    await action();
                    return;
                }
                catch (BrokerUnreachableException)
                {
                    if (retryCount >= _maxRetries)
                    {
                        throw;
                    }

                    var delay = TimeSpan.FromTicks((long)(_initialDelay.Ticks * Math.Pow(2, retryCount)));
                    await Task.Delay(delay);
                    retryCount++;
                }
            }
        }
    }
}

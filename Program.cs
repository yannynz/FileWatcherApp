using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Text.RegularExpressions;
using TimeZoneInfo = System.TimeZoneInfo;
using System.Collections.Concurrent;
using System.Diagnostics;
using RabbitMQ.Client.Events;
using System.Runtime.InteropServices;


namespace FileMonitor
{
    class Program
    {
        // RPC isolado
        private static IConnection rpcConnection;
        private static IModel rpcChannel;
        private const string RpcQueueName = "filewatcher.rpc.ping";
        private static string rpcConsumerTag;
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

        private static readonly ConcurrentDictionary<string, System.Timers.Timer> OpDebouncers = new();
        private static readonly ConcurrentDictionary<string, System.Timers.Timer> FileDebouncers = new();

        private static FileSystemWatcher laserWatcher;
        private static FileSystemWatcher facasWatcher;
        private static FileSystemWatcher dobrarWatcher;
        private static FileSystemWatcher opWatcher;

        private static IConnection persistentConnection;
        private static IModel persistentChannel;

        private static readonly string LaserDir = @"D:\Laser";
        private static readonly string FacasDir = @"D:\Laser\FACAS OK";
        private static readonly string DobrasDir = @"D:\Dobradeira\Facas para Dobrar";
        private static readonly string OpsDir = @"D:\Laser\NR";

    //     private static readonly string LaserDir =
    // RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"D:\Laser" : "/home/laser";
    //     private static readonly string FacasDir =
    //         RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"D:\Laser\FACAS OK" : "/home/laser/FACASOK";
    //     private static readonly string DobrasDir =
    //         RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"D:\Dobradeira\Facas para Dobrar" : "/home/dobras";
    //     private static readonly string OpsDir =
    // RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"D:\NR" : "/home/nr";

        private static readonly RabbitMQConfig MqConfig = new RabbitMQConfig
        {
            Host = "192.168.10.13",
            Port = 5672,
            VirtualHost = "/",
            UserName = "guest",
            Password = "guest",
            QueueNames = new Dictionary<string, string>
            {
                { "Laser", "laser_notifications" },
                { "Facas", "facas_notifications" },
                { "Dobra", "dobra_notifications" },
                { "Ops", "op.imported" }
            }
        };

        private static readonly TimeZoneInfo SaoPauloTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");

    //     private static readonly TimeZoneInfo SaoPauloTimeZone =
    // RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    // ? TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time")
    // : TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

        private static readonly ConnectionFactory RabbitMqFactory = new ConnectionFactory
        {
            HostName = MqConfig.Host,
            Port = MqConfig.Port,
            VirtualHost = MqConfig.VirtualHost,
            UserName = MqConfig.UserName,
            Password = MqConfig.Password,
            AutomaticRecoveryEnabled = true,
            DispatchConsumersAsync = true
        };

        private static readonly Regex NumeroOpFromNameRegex = new Regex(
            @"(?i)\bOrdem\s*de\s*Produ[cç][aã]o\s*n[ºo\.]?\s*(\d{4,})\b",
            RegexOptions.Compiled
        );

        private static readonly Regex UnifiedRegex = new Regex(
            @"^(NR|CL)(\d+)([A-ZÀ-Ú]+).*?(VERMELHO|LARANJA|AMARELO|AZUL|VERDE).*?(?:\.CNC)?$",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex DobrasRegex = new Regex(
            @"^NR\s*(\d+)\.(M\.DXF|DXF\.FCD)$",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex ToolingRegex = new Regex(
            @"^NR(?<nr>\d+)(?<cliente>[A-Z0-9]+)_(?<sexo>MACHO|FEMEA)_(?<cor>[A-Z0-9]+)\.CNC$",
            RegexOptions.IgnoreCase
        );

        private static readonly string[] ReservedWords = { "modelo", "borracha", "regua", "macho", "femea", "bloco", "relevo"};
        private static readonly string DestacadorDir = Path.Combine(LaserDir, "DESTACADOR");

        private static readonly ConcurrentDictionary<string, DateTime> DobrasSeen = new();
        private static readonly TimeSpan DobrasDedupWindow = TimeSpan.FromMinutes(2);

        static void Main(string[] args)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                ConsoleHelper.DisableQuickEdit();
            }
            SetupRabbitMQ();

            // Verificação rápida dos diretórios monitorados
            try
            {
                Console.WriteLine($"[CFG] OS='{RuntimeInformation.OSDescription}'");
                Console.WriteLine($"[CFG] LaserDir='{LaserDir}' exists={Directory.Exists(LaserDir)}");
                Console.WriteLine($"[CFG] FacasDir='{FacasDir}' exists={Directory.Exists(FacasDir)}");
                Console.WriteLine($"[CFG] DobrasDir='{DobrasDir}' exists={Directory.Exists(DobrasDir)}");
                Console.WriteLine($"[CFG] OpsDir='{OpsDir}' exists={Directory.Exists(OpsDir)}");
            }
            catch { /* ignore logging issues */ }

            laserWatcher = CreateFileWatcher(LaserDir, MqConfig.QueueNames["Laser"]);
            facasWatcher = CreateFileWatcher(FacasDir, MqConfig.QueueNames["Facas"]);
            dobrarWatcher = CreateDobrasWatcher(DobrasDir, MqConfig.QueueNames["Dobra"]);
            opWatcher = CreateOpWatcher(OpsDir, MqConfig.QueueNames["Ops"]);
            StartRpcResponder();


            Console.WriteLine("Monitoramento iniciado. Pressione CTRL+C para sair.");
            using (var resetEvent = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    resetEvent.Set();

                    laserWatcher?.Dispose();
                    facasWatcher?.Dispose();
                    dobrarWatcher?.Dispose();
                    opWatcher?.Dispose();

                    persistentChannel?.Close();
                    persistentConnection?.Close();
                };
                resetEvent.WaitOne();
            }
        }

        private static void SetupRabbitMQ()
        {
            try
            {
                persistentConnection = RabbitMqFactory.CreateConnection();
                persistentChannel = persistentConnection.CreateModel();
                rpcConnection = RabbitMqFactory.CreateConnection();
                rpcChannel = rpcConnection.CreateModel();


                foreach (var queue in MqConfig.QueueNames.Values)
                {
                    persistentChannel.QueueDeclare(
                        queue: queue,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);

                    // fila de RPC (request)
                    rpcChannel.QueueDeclare(
                        queue: RpcQueueName,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);

                    rpcChannel.BasicQos(prefetchSize: 0, prefetchCount: 1, global: false);

                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao estabelecer conexão persistente com RabbitMQ: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private static FileSystemWatcher CreateFileWatcher(string path, string queueName)
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*",
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024
            };

            FileSystemEventHandler handler = (sender, e) =>
            {
                var key = e.FullPath;
                var timer = FileDebouncers.AddOrUpdate(key,
                    _ => NewTimer(key, queueName),
                    (_, t) => { t.Stop(); t.Start(); return t; });
            };

            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.Renamed += (s, e) => handler(s, new FileSystemEventArgs(
                WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)!));

            watcher.Error += (s, e) =>
            {
                Console.WriteLine($"Erro no FileSystemWatcher em '{path}': {e.GetException().Message}");
            };

            watcher.EnableRaisingEvents = true;
            return watcher;

            System.Timers.Timer NewTimer(string fullPath, string q)
            {
                var t = new System.Timers.Timer(1200) { AutoReset = false };
                t.Elapsed += async (_, __) =>
                {
                    try
                    {
                        // Evita duplicidade: se o watcher é o de Laser, ignore arquivos que estejam sob a pasta FACAS
                        if (string.Equals(q, MqConfig.QueueNames["Laser"], StringComparison.OrdinalIgnoreCase)
                            && IsUnder(fullPath, FacasDir))
                        {
                            return;
                        }
                        // Se o watcher é o de Facas, ignore qualquer coisa fora da pasta de Facas
                        if (string.Equals(q, MqConfig.QueueNames["Facas"], StringComparison.OrdinalIgnoreCase)
                            && !IsUnder(fullPath, FacasDir))
                        {
                            return;
                        }

                        var nameUpper = Path.GetFileName(fullPath).Trim().ToUpperInvariant();
                        var args = new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath)!);
                        if (ToolingRegex.IsMatch(nameUpper))
                            await HandleToolingFile(args, q);
                        else
                            await HandleNewFile(args, q);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LASER/FACAS] Erro ao processar '{fullPath}': {ex.Message}");
                    }
                    finally
                    {
                        FileDebouncers.TryRemove(fullPath, out System.Timers.Timer _removed);
                        t.Dispose();
                    }
                };
                t.Start();
                return t;
            }
        }

        private static FileSystemWatcher CreateDobrasWatcher(string path, string queueName)
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*",
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024
            };

            FileSystemEventHandler handler = async (sender, e) => await HandleDobrasFile(e, queueName);
            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.Renamed += (s, e) => handler(s, new FileSystemEventArgs(
                WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)!));

            watcher.Error += (s, e) =>
            {
                Console.WriteLine($"Erro no FileSystemWatcher em '{path}': {e.GetException().Message}");
            };

            watcher.EnableRaisingEvents = true;
            return watcher;
        }

        private static FileSystemWatcher CreateOpWatcher(string path, string queueName)
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                // Usa filtro amplo e filtra por extensão no timer (debounce)
                Filter = "*",
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };

            FileSystemEventHandler handler = (sender, e) =>
            {
                var key = e.FullPath;
                try { Console.WriteLine($"[OP] FS event: {e.ChangeType} -> {key}"); } catch { }
                var timer = OpDebouncers.AddOrUpdate(key,
                    _ => NewTimer(key, queueName),
                    (_, t) => { t.Stop(); t.Start(); return t; });
            };

            watcher.Created += handler;
            watcher.Changed += handler;
            watcher.Renamed += (s, e) => handler(s, new FileSystemEventArgs(
                WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)!));

            watcher.Error += (s, e) =>
                Console.WriteLine($"Erro no FileSystemWatcher(OP) em '{path}': {e.GetException().Message}");

            watcher.EnableRaisingEvents = true;
            return watcher;

            System.Timers.Timer NewTimer(string fullPath, string q)
            {
                var t = new System.Timers.Timer(1200) { AutoReset = false };
                t.Elapsed += async (_, __) =>
                {
                    try
                    {
                        var ext = Path.GetExtension(fullPath) ?? string.Empty;
                        if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return;
                        await HandleOpFile(fullPath, q);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[OP] Erro ao processar '{fullPath}': {ex.Message}");
                    }
                    finally
                    {
                        OpDebouncers.TryRemove(fullPath, out System.Timers.Timer _removed);
                        t.Dispose();
                    }
                };
                t.Start();
                return t;
            }
        }

        private static async Task HandleOpFile(string fullPath, string queueName)
        {
            if (Directory.Exists(fullPath)) return;

            // aguarda o arquivo “assentar” com tamanho estável + teste de lock
            if (!await WaitFileReady(fullPath, TimeSpan.FromSeconds(20), TimeSpan.FromMilliseconds(250), stableReads: 3))
            {
                Console.WriteLine($"[OP] Arquivo não ficou pronto a tempo: '{fullPath}'");
                return;
            }

            var parsed = PdfParser.Parse(fullPath);

            // Ajusta data da OP para ZonedDateTime ISO (inclui offset e região SP)
            string? dataOpZoned = null;
            if (!string.IsNullOrWhiteSpace(parsed.DataOpIso))
            {
                try
                {
                    var parts = parsed.DataOpIso!.Split('-');
                    int y = int.Parse(parts[0]);
                    int m = int.Parse(parts[1]);
                    int d = int.Parse(parts[2]);
                    var local = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
                    var offset = SaoPauloTimeZone.GetUtcOffset(local);
                    var dto = new DateTimeOffset(local, offset);
                    dataOpZoned = dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "[America/Sao_Paulo]";
                }
                catch { /* deixa nulo se der erro */ }
            }

            // Heurística adicional de emborrachada (bor, shore)
            bool emborrachada = parsed.Emborrachada;
            if (!emborrachada)
            {
                var joined = string.Join(" ", parsed.Materiais).ToUpperInvariant();
                if (joined.Contains("BOR ") || joined.Contains("SHORE") || joined.Contains("EMBORRACH"))
                    emborrachada = true;
            }

            // Combina data/hora de entrega requerida (se houver) para ZonedDateTime SP
            string? dataReqZoned = null;
            if (!string.IsNullOrWhiteSpace(parsed.DataEntregaIso))
            {
                var hora = string.IsNullOrWhiteSpace(parsed.HoraEntrega) ? "18:00" : parsed.HoraEntrega;
                try
                {
                    var parts = parsed.DataEntregaIso!.Split('-');
                    int y = int.Parse(parts[0]);
                    int m = int.Parse(parts[1]);
                    int d = int.Parse(parts[2]);
                    var hm = hora.Split(':');
                    int hh = int.Parse(hm[0]);
                    int mm = int.Parse(hm[1]);
                    var local = new DateTime(y, m, d, hh, mm, 0, DateTimeKind.Unspecified);
                    var offset = SaoPauloTimeZone.GetUtcOffset(local);
                    var dto = new DateTimeOffset(local, offset);
                    dataReqZoned = dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz") + "[America/Sao_Paulo]";
                }
                catch { /* deixa nulo se der erro */ }
            }

            var message = new
            {
                numeroOp = parsed.NumeroOp,
                codigoProduto = parsed.CodigoProduto,
                descricaoProduto = parsed.DescricaoProduto,
                cliente = parsed.Cliente,
                dataOp = dataOpZoned,
                materiais = parsed.Materiais,
                emborrachada = emborrachada,
                sharePath = fullPath,
                // Extras estruturados (não escrevemos em observação do pedido)
                destacador = parsed.Destacador,
                modalidadeEntrega = string.IsNullOrWhiteSpace(parsed.ModalidadeEntrega) ? null : parsed.ModalidadeEntrega,
                dataRequeridaEntrega = dataReqZoned,
                usuarioImportacao = parsed.Usuario,
                pertinax = parsed.Pertinax,
                poliester = parsed.Poliester,
                papelCalibrado = parsed.PapelCalibrado
            };

            SendToRabbitMQ(queueName, message);
            Console.WriteLine($"[OP] Import publicada: {parsed.NumeroOp} (emborrachada={parsed.Emborrachada})");
        }

        private static bool IsUnder(string fullPath, string directory)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(directory)) return false;
            var dirWithSep = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
            var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return fullPath.StartsWith(dirWithSep, comparison) || string.Equals(fullPath, directory, comparison);
        }

        private static async Task HandleToolingFile(FileSystemEventArgs e, string queueName)
        {
            if (Directory.Exists(e.FullPath)) return;

            var fileInfo = new FileInfo(e.FullPath);
            var original = fileInfo.Name;

            if (!await WaitFileReady(fileInfo.FullName, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200), 2))
            {
                Console.WriteLine($"[TOOLING] Arquivo não ficou pronto a tempo: '{original}'");
                return;
            }

            var message = new
            {
                file_name = fileInfo.Name,
                path = fileInfo.FullName,
                timestamp = GetSaoPauloTimestamp()
            };

            var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));
            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await Task.Delay(50);
                    SendToRabbitMQ(queueName, message);
                });

                var fase = string.Equals(queueName, MqConfig.QueueNames["Facas"], StringComparison.OrdinalIgnoreCase)
                    ? "CUT" : "NEW";
                Console.WriteLine($"[TOOLING-{fase}] publicado em {queueName}: {original}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TOOLING] Erro após {retryPolicy.MaxRetries} tentativas: {ex.Message}");
            }
        }

        private static async Task HandleNewFile(FileSystemEventArgs e, string queueName)
        {
            if (Directory.Exists(e.FullPath)) return;

            var fileInfo = new FileInfo(e.FullPath);
            var original = fileInfo.Name;
            var cleanName = CleanFileName(original);

            if (string.IsNullOrEmpty(cleanName))
            {
                Console.WriteLine($"Arquivo '{original}' ignorado por conter palavra reservada ou formato inválido.");
                return;
            }

            var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));

            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await Task.Delay(100);

                    var message = new
                    {
                        file_name = cleanName,
                        path = fileInfo.FullName,
                        timestamp = GetSaoPauloTimestamp()
                    };

                    SendToRabbitMQ(queueName, message);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro após {retryPolicy.MaxRetries} tentativas: {ex.Message}");
            }
        }

        private static async Task HandleDobrasFile(FileSystemEventArgs e, string queueName)
        {
            if (Directory.Exists(e.FullPath)) return;

            var fileInfo = new FileInfo(e.FullPath);
            var originalUpper = fileInfo.Name.Trim().ToUpperInvariant();

            foreach (var rw in ReservedWords)
            {
                if (originalUpper.Contains(rw.ToUpperInvariant()))
                {
                    Console.WriteLine($"[DOBRAS] Ignorado por palavra reservada: '{fileInfo.Name}'");
                    return;
                }
            }

            var m = DobrasRegex.Match(originalUpper);
            if (!m.Success)
            {
                Console.WriteLine($"[DOBRAS] Ignorado por padrão não correspondente: '{fileInfo.Name}'");
                return;
            }

            var nr = m.Groups[1].Value;

            if (!await WaitFileReady(fileInfo.FullName, TimeSpan.FromSeconds(8), TimeSpan.FromMilliseconds(200), 2))
            {
                Console.WriteLine($"[DOBRAS] Arquivo não ficou pronto a tempo: '{fileInfo.Name}'");
                return;
            }

            var now = DateTime.UtcNow;
            PruneDobrasSeen(now);
            if (DobrasSeen.TryGetValue(nr, out var last) && now - last < DobrasDedupWindow)
            {
                Console.WriteLine($"[DOBRAS] Evento duplicado suprimido (NR={nr}) para '{fileInfo.Name}'");
                return;
            }
            DobrasSeen.AddOrUpdate(nr, now, (_, __) => now);

            var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));

            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await Task.Delay(50);
                    var message = new
                    {
                        file_name = fileInfo.Name,
                        path = fileInfo.FullName,
                        timestamp = GetSaoPauloTimestamp()
                    };

                    SendToRabbitMQ(queueName, message);
                    Console.WriteLine($"[DOBRAS] Mensagem publicada (NR={nr}) a partir de '{fileInfo.Name}'");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DOBRAS] Erro após {retryPolicy.MaxRetries} tentativas: {ex.Message}");
            }
        }

        /// <summary>
        /// Aguarda o arquivo "assentar" usando dois critérios:
        ///  1) tamanho estável por N leituras consecutivas (usando FileShare.Read para conseguir inspecionar);
        ///  2) ao final, tenta abrir com FileShare.None (sem lock) para garantir que ninguém mais está escrevendo.
        /// </summary>
        private static async Task<bool> WaitFileReady(
            string fullPath,
            TimeSpan timeout,
            TimeSpan pollInterval,
            int stableReads = 3)
        {
            var sw = Stopwatch.StartNew();
            long? lastLen = null;
            int stable = 0;

            while (sw.Elapsed < timeout)
            {
                long len = -1;
                try
                {
                    // Usa Read/Write share apenas para olhar o tamanho enquanto o arquivo pode estar em escrita
                    using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        len = stream.Length;
                    }
                }
                catch
                {
                    // arquivo ainda não disponível para leitura básica
                }

                if (len >= 0)
                {
                    if (lastLen.HasValue && len == lastLen.Value) stable++;
                    else { stable = 1; lastLen = len; }

                    if (stable >= stableReads)
                    {
                        // teste final: abrir sem compartilhamento (garante que não está em uso)
                        try
                        {
                            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None);
                            if (stream.Length >= 0) return true;
                        }
                        catch
                        {
                            // ainda travado por outra app — continua aguardando
                        }
                    }
                }

                await Task.Delay(pollInterval);
            }

            return false;
        }

        // Limpa entradas antigas de de-duplicação
        private static void PruneDobrasSeen(DateTime utcNow)
        {
            foreach (var kvp in DobrasSeen)
            {
                if (utcNow - kvp.Value > DobrasDedupWindow)
                {
                    DobrasSeen.TryRemove(kvp.Key, out _);
                }
            }
        }

        private static string CleanFileName(string name)
        {
            var upper = name.Trim().ToUpperInvariant();

            foreach (var rw in ReservedWords)
            {
                if (upper.Contains(rw.ToUpperInvariant()))
                    return null;
            }

            var m = UnifiedRegex.Match(upper);
            if (!m.Success)
                return null;

            var tipo = m.Groups[1].Value.ToUpperInvariant();
            var numero = m.Groups[2].Value;
            var client = m.Groups[3].Value.Replace(".", string.Empty).Replace(",", string.Empty);
            var priority = m.Groups[4].Value.ToUpperInvariant();

            return $"{tipo}{numero}{client}_{priority}.CNC";
        }

        private static double GetSaoPauloTimestamp()
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SaoPauloTimeZone);
            var dateTimeOffset = new DateTimeOffset(now);

            long ticks = dateTimeOffset.ToUnixTimeMilliseconds() * 10_000;
            return ticks / (double)TimeSpan.TicksPerSecond;
        }

        private static void SendToRabbitMQ(string queueName, object message)
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = persistentChannel.CreateBasicProperties();
                properties.Persistent = true;
                properties.ContentType = "application/json";
                properties.ContentEncoding = "utf-8";
                properties.Headers ??= new Dictionary<string, object>();
                // Ajuda o Spring a bindar diretamente no DTO quando aplicável
                properties.Headers["__TypeId__"] = "git.yannynz.organizadorproducao.model.dto.OpImportRequestDTO";

                persistentChannel.BasicPublish(
                    exchange: "",
                    routingKey: queueName,
                    basicProperties: properties,
                    body: body);

                Console.WriteLine($"Mensagem enviada para {queueName}: {JsonSerializer.Serialize(message)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao enviar mensagem para RabbitMQ: {ex.Message}");
            }
        }

        public class RetryPolicy
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
                int retryCount = 0;
                while (true)
                {
                    try
                    {
                        await action();
                        return;
                    }
                    catch (BrokerUnreachableException)
                    {
                        if (retryCount >= _maxRetries) throw;

                        var delay = TimeSpan.FromTicks((long)(_initialDelay.Ticks * Math.Pow(2, retryCount)));
                        Console.WriteLine($"Tentativa {retryCount + 1} falhou. Nova tentativa em {delay.TotalSeconds} segundos.");
                        await Task.Delay(delay);
                        retryCount++;
                    }
                }
            }
        }

        public class RabbitMQConfig
        {
            public string Host { get; set; }
            public int Port { get; set; }
            public string VirtualHost { get; set; }
            public string UserName { get; set; }
            public string Password { get; set; }
            public Dictionary<string, string> QueueNames { get; set; }
        }

        private static void StartRpcResponder()
        {
            var consumer = new AsyncEventingBasicConsumer(rpcChannel);

            consumer.Received += async (sender, ea) =>
            {
                try
                {
                    var replyTo = ea.BasicProperties?.ReplyTo;
                    var corrId = ea.BasicProperties?.CorrelationId;

                    if (string.IsNullOrWhiteSpace(replyTo))
                    {
                        rpcChannel.BasicAck(ea.DeliveryTag, false);
                        return;
                    }

                    var payload = new
                    {
                        ok = true,
                        instanceId = Environment.MachineName,
                        ts = DateTimeOffset.UtcNow.ToString("O"),
                        version = "1.0.0"
                    };
                    var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));

                    var replyProps = rpcChannel.CreateBasicProperties();
                    replyProps.ContentType = "application/json";
                    replyProps.CorrelationId = corrId;

                    // publica a resposta NO CANAL RPC
                    rpcChannel.BasicPublish(exchange: "", routingKey: replyTo, basicProperties: replyProps, body: body);

                    // ack do request após publicar
                    rpcChannel.BasicAck(ea.DeliveryTag, false);
                }
                catch (Exception)
                {
                    try { rpcChannel.BasicNack(ea.DeliveryTag, false, requeue: true); } catch { }
                }
                await Task.Yield();
            };

            rpcConsumerTag = rpcChannel.BasicConsume(queue: RpcQueueName, autoAck: false, consumer: consumer);
            Console.WriteLine($"[RPC] responder ligado em '{RpcQueueName}'");
        }
    }
}


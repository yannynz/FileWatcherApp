using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Text.RegularExpressions;
using TimeZoneInfo = System.TimeZoneInfo;
using System.Collections.Concurrent;

namespace FileMonitor
{
    class Program
    {
        private static FileSystemWatcher laserWatcher;
        private static FileSystemWatcher facasWatcher;
        private static FileSystemWatcher dobrarWatcher;


        private static IConnection persistentConnection;
        private static IModel persistentChannel;

        private static readonly string LaserDir = @"D:\Laser";
        private static readonly string FacasDir = @"D:\Laser\FACAS OK";
        private static readonly string DobrasDir = @"D:\Dobradeira\Facas para Dobrar";

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
                { "Dobra", "dobra_notifications" }
            }
        };

        private static readonly TimeZoneInfo SaoPauloTimeZone = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");

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

        private static readonly Regex UnifiedRegex = new Regex(
            @"^(NR|CL)(\d+)([A-ZÀ-Ú]+).*?(VERMELHO|LARANJA|AMARELO|AZUL|VERDE).*?(?:\.CNC)?$",
            RegexOptions.IgnoreCase
        );

        private static readonly Regex DobrasRegex = new Regex(
            @"^NR\s*(\d+)\.(M\.DXF|DXF\.FCD)$",
            RegexOptions.IgnoreCase
        );

        private static readonly string[] ReservedWords = { "modelo", "femea", "macho", "borracha" };

        private static readonly ConcurrentDictionary<string, DateTime> DobrasSeen = new ConcurrentDictionary<string, DateTime>();
        private static readonly TimeSpan DobrasDedupWindow = TimeSpan.FromMinutes(2);

        static void Main(string[] args)
        {
            SetupRabbitMQ();

            laserWatcher = CreateFileWatcher(LaserDir, MqConfig.QueueNames["Laser"]);
            facasWatcher = CreateFileWatcher(FacasDir, MqConfig.QueueNames["Facas"]);
            dobrarWatcher = CreateDobrasWatcher(DobrasDir, MqConfig.QueueNames["Dobra"]);


            Console.WriteLine("Monitoramento iniciado. Pressione CTRL+C para sair.");
            using (var resetEvent = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    resetEvent.Set();

                    laserWatcher.Dispose();
                    facasWatcher.Dispose();
                    dobrarWatcher?.Dispose();
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

                foreach (var queue in MqConfig.QueueNames.Values)
                {
                    persistentChannel.QueueDeclare(
                        queue: queue,
                        durable: true,
                        exclusive: false,
                        autoDelete: false,
                        arguments: null);
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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                Filter = "*.*",
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };

            watcher.Created += async (sender, e) => await HandleNewFile(e, queueName);

            watcher.Error += (s, e) =>
            {
                Console.WriteLine($"Erro no FileSystemWatcher em '{path}': {e.GetException().Message}");
            };

            return watcher;
        }

        private static FileSystemWatcher CreateDobrasWatcher(string path, string queueName)
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                Filter = "*.*",
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };

            watcher.Created += async (sender, e) => await HandleDobrasFile(e, queueName);

            watcher.Error += (s, e) =>
            {
                Console.WriteLine($"Erro no FileSystemWatcher em '{path}': {e.GetException().Message}");
            };

            return watcher;
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

            // Ignora palavras reservadas, se aparecerem por algum motivo
            foreach (var rw in ReservedWords)
            {
                if (originalUpper.Contains(rw.ToUpperInvariant()))
                {
                    Console.WriteLine($"[DOBRAS] Ignorado por palavra reservada: '{fileInfo.Name}'");
                    return;
                }
            }

            // Aceita apenas os padrões finais das máquinas de dobra
            var m = DobrasRegex.Match(originalUpper);
            if (!m.Success)
            {
                // Ignora DXF "simples" e CF2 (não são fim de dobra)
                Console.WriteLine($"[DOBRAS] Ignorado por padrão não correspondente: '{fileInfo.Name}'");
                return;
            }

            var nr = m.Groups[1].Value; // número do pedido

            // Espera curta para garantir que o arquivo terminou de ser gravado
            if (!await WaitFileReady(fileInfo.FullName, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)))
            {
                Console.WriteLine($"[DOBRAS] Arquivo não ficou pronto a tempo: '{fileInfo.Name}'");
                return;
            }

            // De-duplicação por janela (se vier .m.DXF e depois .DXF.FCD do mesmo NR)
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

                    // Envia no MESMO formato de mensagem (mesmas chaves),
                    // usando o nome real do arquivo salvo pela máquina de dobra.
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

        private static async Task<bool> WaitFileReady(string fullPath, TimeSpan timeout, TimeSpan pollInterval)
        {
            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < timeout)
            {
                try
                {
                    using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                        if (stream.Length >= 0) return true;
                    }
                }
                catch
                {
                    // ainda bloqueado
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
                    catch (BrokerUnreachableException ex)
                    {
                        if (retryCount >= _maxRetries)
                            throw;

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
    }
}

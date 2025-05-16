using System;
using System.IO;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using TimeZoneInfo = System.TimeZoneInfo;

namespace FileMonitor
{
    class Program
    {
        private static FileSystemWatcher laserWatcher;
        private static FileSystemWatcher facasWatcher;

        private static IConnection persistentConnection;
        private static IModel persistentChannel;

        private static readonly string LaserDir = @"D:\Laser";
        private static readonly string FacasDir = @"D:\Laser\FACAS OK";

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
                { "Facas", "facas_notifications" }
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
            @"^(NR|CL)(\d+)([A-ZÀ-Ú]+).*?(VERMELHO|AMARELO|AZUL|VERDE).*?(?:\.CNC)?$",
            RegexOptions.IgnoreCase
        );

        private static readonly string[] ReservedWords = { "modelo", "femea", "macho", "borracha" };

        static void Main(string[] args)
        {
            SetupRabbitMQ();

            laserWatcher = CreateFileWatcher(LaserDir, MqConfig.QueueNames["Laser"]);
            facasWatcher = CreateFileWatcher(FacasDir, MqConfig.QueueNames["Facas"]);

            Console.WriteLine("Monitoramento iniciado. Pressione CTRL+C para sair.");
            using (var resetEvent = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    resetEvent.Set();

                    laserWatcher.Dispose();
                    facasWatcher.Dispose();
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

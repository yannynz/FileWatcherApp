using System;
using System.IO;
using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using TimeZoneInfo = System.TimeZoneInfo;

namespace FileMonitor
{
    class Program
    {
        // Configurações
        private static readonly string LaserDir = @"/home/ynz/Laser";
        private static readonly string FacasDir = @"/home/ynz/Laser/FacasOk/";

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
            AutomaticRecoveryEnabled = true
        };

        static void Main(string[] args)
        {
            var laserWatcher = CreateFileWatcher(LaserDir, MqConfig.QueueNames["Laser"]);
            var facasWatcher = CreateFileWatcher(FacasDir, MqConfig.QueueNames["Facas"]);

            Console.WriteLine("Monitoramento iniciado. Pressione CTRL+C para sair.");
            using (var resetEvent = new ManualResetEvent(false))
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    resetEvent.Set();
                    laserWatcher.Dispose();
                    facasWatcher.Dispose();
                };
                resetEvent.WaitOne();
            }
        }

        private static FileSystemWatcher CreateFileWatcher(string path, string queueName)
        {
            var watcher = new FileSystemWatcher
            {
                Path = path,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            watcher.Created += async (sender, e) => await HandleNewFile(e, queueName);
            return watcher;
        }

        private static async Task HandleNewFile(FileSystemEventArgs e, string queueName)
        {
            if (Directory.Exists(e.FullPath)) return;

            var fileInfo = new FileInfo(e.FullPath);
            var retryPolicy = new RetryPolicy(maxRetries: 3, initialDelay: TimeSpan.FromSeconds(2));

            try
            {
                await retryPolicy.ExecuteAsync(async () =>
                {
                    await Task.Delay(100); // Pequeno delay para garantir disponibilidade do arquivo

                    var message = new
                    {
                        file_name = fileInfo.Name,
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

        private static double GetSaoPauloTimestamp()
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SaoPauloTimeZone);
            var dateTimeOffset = new DateTimeOffset(now);

            // Obter ticks desde a época Unix
            long ticks = dateTimeOffset.ToUnixTimeMilliseconds() * 10_000; // Converter para ticks de 100-ns
            double secondsSinceEpoch = ticks / (double)TimeSpan.TicksPerSecond;

            return secondsSinceEpoch;
        }

        private static void SendToRabbitMQ(string queueName, object message)
        {
            using (var connection = RabbitMqFactory.CreateConnection())
            using (var channel = connection.CreateModel())
            {
                channel.QueueDeclare(
                    queue: queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: null);

                var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));

                var properties = channel.CreateBasicProperties();
                properties.Persistent = true;

                channel.BasicPublish(
                    exchange: "",
                    routingKey: queueName,
                    basicProperties: properties,
                    body: body);

                Console.WriteLine($"Mensagem enviada para {queueName}: {JsonSerializer.Serialize(message)}");
            }
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

                    var delay = _initialDelay * Math.Pow(2, retryCount);
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

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace FileWatcherApp.Messaging;

public sealed class RabbitMqConnection : IDisposable
{
    private readonly IConnection _connection;
    private readonly ILogger<RabbitMqConnection> _logger;
    public IModel Channel { get; }

    public RabbitMqConnection(IOptions<RabbitMqOptions> options, ILogger<RabbitMqConnection> logger)
    {
        _logger = logger;
        var o = options.Value;

        var factory = new ConnectionFactory
        {
            HostName = o.HostName,
            Port = o.Port,
            UserName = o.UserName,
            Password = o.Password,
            VirtualHost = o.VirtualHost,
            AutomaticRecoveryEnabled = o.AutomaticRecoveryEnabled,
            TopologyRecoveryEnabled = o.TopologyRecoveryEnabled,
            RequestedHeartbeat = TimeSpan.FromSeconds(o.RequestedHeartbeatSeconds),
            DispatchConsumersAsync = true 
        };

        _connection = factory.CreateConnection();
        Channel = _connection.CreateModel();

        Channel.QueueDeclare(queue: o.Queue, durable: true, exclusive: false, autoDelete: false, arguments: null);

        Channel.BasicQos(0, o.Prefetch, global: false);

        _logger.LogInformation("RabbitMQ conectado em {Host}:{Port} vhost={Vhost}", o.HostName, o.Port, o.VirtualHost);
    }

    public void Dispose()
    {
        try { Channel?.Close(); } catch { /* ignore */ }
        try { Channel?.Dispose(); } catch { /* ignore */ }
        try { _connection?.Close(); } catch { /* ignore */ }
        try { _connection?.Dispose(); } catch { /* ignore */ }
    }
}


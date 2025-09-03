using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using FileWatcherApp.Util;

namespace FileWatcherApp.Messaging;

public sealed class RpcPingResponderService : BackgroundService
{
    private readonly ILogger<RpcPingResponderService> _logger;
    private readonly RabbitMqConnection _rmq;
    private readonly RabbitMqOptions _opts;
    private readonly IConfiguration _config;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public RpcPingResponderService(
        ILogger<RpcPingResponderService> logger,
        RabbitMqConnection rmqConn,
        IOptions<RabbitMqOptions> opts,
        IConfiguration config)
    {
        _logger = logger;
        _rmq = rmqConn;
        _opts = opts.Value;
        _config = config;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = new AsyncEventingBasicConsumer(_rmq.Channel);
        consumer.Received += OnRequestAsync;

        _rmq.Channel.BasicConsume(queue: _opts.Queue, autoAck: false, consumer: consumer);
        _logger.LogInformation("RPC responder ouvindo fila {Queue}", _opts.Queue);

        return Task.CompletedTask;
    }

    private async Task OnRequestAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var props = ea.BasicProperties;
            var replyTo = props.ReplyTo;
            var corrId  = props.CorrelationId;

            if (string.IsNullOrWhiteSpace(replyTo))
            {
                _logger.LogWarning("Mensagem sem ReplyTo; descartando. corrId={Corr}", corrId);
                _rmq.Channel.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            var payload = new
            {
                ok = true,
                instanceId = SystemInfo.GetHostName(),
                ts = DateTimeOffset.UtcNow.ToString("O"),
                version = SystemInfo.GetVersion(_config)
            };
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));

            var replyProps = _rmq.Channel.CreateBasicProperties();
            replyProps.ContentType = "application/json";
            replyProps.CorrelationId = corrId;

            _rmq.Channel.BasicPublish(
                exchange: string.Empty,
                routingKey: replyTo,
                basicProperties: replyProps,
                body: body
            );

            _rmq.Channel.BasicAck(ea.DeliveryTag, multiple: false);

            _logger.LogDebug("Pong enviado. corrId={Corr} replyTo={Reply}", corrId, replyTo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao processar ping RPC");
            try { _rmq.Channel.BasicNack(deliveryTag: ea.DeliveryTag, multiple: false, requeue: true); } catch { /* ignore */ }
        }

        await Task.Yield();
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Encerrando RpcPingResponderService...");
        return base.StopAsync(cancellationToken);
    }
}


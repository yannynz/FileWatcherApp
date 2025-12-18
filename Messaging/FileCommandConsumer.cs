using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using FileWatcherApp.Services.FileWatcher;

namespace FileWatcherApp.Messaging;

public sealed class FileCommandConsumer : BackgroundService
{
    private readonly ILogger<FileCommandConsumer> _logger;
    private readonly RabbitMqConnection _rmq;
    private readonly FileWatcherOptions _options;
    private readonly FileCommandHandler _handler;
    
    private const string CommandQueue = "file_commands";

    public FileCommandConsumer(
        ILogger<FileCommandConsumer> logger,
        RabbitMqConnection rmq,
        IOptions<FileWatcherOptions> options)
    {
        _logger = logger;
        _rmq = rmq;
        _options = options.Value;
        _handler = new FileCommandHandler(logger, _options);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _rmq.Channel.QueueDeclare(queue: CommandQueue, durable: true, exclusive: false, autoDelete: false, arguments: null);

        var consumer = new AsyncEventingBasicConsumer(_rmq.Channel);
        consumer.Received += HandleCommandAsync;

        _rmq.Channel.BasicConsume(queue: CommandQueue, autoAck: false, consumer: consumer);
        _logger.LogInformation("FileCommandConsumer ouvindo fila {Queue}", CommandQueue);

        return Task.CompletedTask;
    }

    private async Task HandleCommandAsync(object sender, BasicDeliverEventArgs ea)
    {
        try
        {
            var body = Encoding.UTF8.GetString(ea.Body.Span);
            Console.WriteLine($"[RABBIT-CONSOLE] BodyLen={body.Length} Content={body}");
            var command = _handler.TryParse(body);

            if (command is null)
            {
                _logger.LogWarning("Comando inválido recebido: {Body}", Truncate(body, 400));
                _rmq.Channel.BasicAck(ea.DeliveryTag, multiple: false);
                return;
            }

            await _handler.HandleAsync(command);
            _rmq.Channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar comando de arquivo");
            _rmq.Channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value.Substring(0, maxLength) + "...";
    }
}

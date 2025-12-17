using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    
    private const string CommandQueue = "file_commands";

    public FileCommandConsumer(
        ILogger<FileCommandConsumer> logger,
        RabbitMqConnection rmq,
        IOptions<FileWatcherOptions> options)
    {
        _logger = logger;
        _rmq = rmq;
        _options = options.Value;
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
            var command = JsonSerializer.Deserialize<FileCommandDto>(body);

            if (command != null && command.Action == "RENAME_PRIORITY")
            {
                await RenamePriorityAsync(command);
            }

            _rmq.Channel.BasicAck(ea.DeliveryTag, multiple: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar comando de arquivo");
            _rmq.Channel.BasicNack(ea.DeliveryTag, multiple: false, requeue: false);
        }
    }

    private Task RenamePriorityAsync(FileCommandDto command)
    {
        string? targetDir = command.Directory?.ToUpperInvariant() switch 
        {
            "LASER" => _options.LaserDirectory,
            "FACAS" => _options.FacasDirectory,
            _ => null
        };

        // Fallback defaults if options are null (similar to FileWatcherService logic)
        if (string.IsNullOrEmpty(targetDir)) 
        {
             if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                targetDir = "/home/laser";
             else
                targetDir = @"D:\Laser";
        }

        if (!Directory.Exists(targetDir))
        {
            _logger.LogWarning("Diretório alvo inexistente: {Dir}", targetDir);
            return Task.CompletedTask;
        }

        var searchPattern = $"*{command.Nr}*";
        var files = Directory.GetFiles(targetDir, searchPattern);

        foreach (var oldPath in files)
        {
            var filename = Path.GetFileName(oldPath);
            
            // Check if file truly corresponds to the number (avoid partial matches like matching 123 in 1234)
            if (!Regex.IsMatch(filename, $@"(?:^|[^0-9]){Regex.Escape(command.Nr)}(?:[^0-9]|$)", RegexOptions.IgnoreCase))
            {
                continue;
            }

            string folder = Path.GetDirectoryName(oldPath) ?? string.Empty;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(oldPath);
            string ext = Path.GetExtension(oldPath);

            string newPrioritySuffix = $"_{command.NewPriority.ToUpperInvariant()}";
            string newNameWithoutExt = nameWithoutExt;

            var priorityRegex = new Regex(@"_(VERMELHO|AMARELO|AZUL|VERDE)$", RegexOptions.IgnoreCase);
            
            if (priorityRegex.IsMatch(nameWithoutExt))
            {
                // Replace existing priority
                newNameWithoutExt = priorityRegex.Replace(nameWithoutExt, newPrioritySuffix);
            }
            else
            {
                // Append new priority
                newNameWithoutExt += newPrioritySuffix;
            }

            if (string.Equals(newNameWithoutExt, nameWithoutExt, StringComparison.OrdinalIgnoreCase)) continue; 

            string newPath = Path.Combine(folder, newNameWithoutExt + ext);

            try
            {
                File.Move(oldPath, newPath);
                _logger.LogInformation("[RENAME] {Old} -> {New}", filename, Path.GetFileName(newPath));
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Falha ao renomear {File}", filename);
            }
        }

        return Task.CompletedTask;
    }
}

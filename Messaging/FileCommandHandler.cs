using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FileWatcherApp.Services.FileWatcher;
using Microsoft.Extensions.Logging;

namespace FileWatcherApp.Messaging;

/// <summary>
/// Parses and executes file commands coming from RabbitMQ.
/// Separated from the consumer for easier testing and tolerance to publisher variations.
/// </summary>
internal sealed class FileCommandHandler
{
    private readonly ILogger _logger;
    private readonly FileWatcherOptions _options;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
    };

    private static readonly Regex PriorityRegex = new(@"_(VERMELHO|AMARELO|AZUL|VERDE)$", RegexOptions.IgnoreCase);

    public FileCommandHandler(ILogger logger, FileWatcherOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public FileCommandDto? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileCommandDto>(body, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Falha ao desserializar comando; tentando fallback tolerante.");
            return TryParseLoosely(body);
        }
    }

    public async Task HandleAsync(FileCommandDto command)
    {
        if (string.Equals(command.Action, "RENAME_PRIORITY", StringComparison.OrdinalIgnoreCase))
        {
            await RenamePriorityAsync(command);
            return;
        }

        _logger.LogWarning("Ação de comando desconhecida: {Action}", command.Action);
    }

    internal Task RenamePriorityAsync(FileCommandDto command)
    {
        if (string.IsNullOrWhiteSpace(command.Nr))
        {
            _logger.LogWarning("Comando de rename ignorado: NR vazio.");
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(command.NewPriority))
        {
            _logger.LogWarning("Comando de rename ignorado: newPriority vazio (NR={Nr})", command.Nr);
            return Task.CompletedTask;
        }

        var targetDir = ResolveTargetDirectory(command.Directory);
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            _logger.LogWarning("Diretório alvo inexistente ou não configurado: {Dir}", targetDir);
            return Task.CompletedTask;
        }

        var searchPattern = $"*{command.Nr}*";
        var files = Directory.GetFiles(targetDir, searchPattern);

        foreach (var oldPath in files)
        {
            var filename = Path.GetFileName(oldPath);
            if (string.IsNullOrWhiteSpace(filename))
            {
                continue;
            }

            if (!Regex.IsMatch(filename, $@"(?:^|[^0-9]){Regex.Escape(command.Nr)}(?:[^0-9]|$)", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var nameWithoutExt = Path.GetFileNameWithoutExtension(oldPath) ?? string.Empty;
            var ext = Path.GetExtension(oldPath) ?? string.Empty;

            var newPrioritySuffix = $"_{command.NewPriority.ToUpperInvariant()}";
            var newNameWithoutExt = PriorityRegex.IsMatch(nameWithoutExt)
                ? PriorityRegex.Replace(nameWithoutExt, newPrioritySuffix)
                : nameWithoutExt + newPrioritySuffix;

            if (string.Equals(newNameWithoutExt, nameWithoutExt, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var newPath = Path.Combine(Path.GetDirectoryName(oldPath) ?? string.Empty, newNameWithoutExt + ext);

            try
            {
                File.Move(oldPath, newPath, overwrite: false);
                _logger.LogInformation("[RENAME] {Old} -> {New}", filename, Path.GetFileName(newPath));
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "Falha ao renomear {File}", filename);
            }
        }

        return Task.CompletedTask;
    }

    private FileCommandDto? TryParseLoosely(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var action = GetFirstString(root, "action");
            var nr = GetFirstString(root, "nr", "orderNr", "opId");
            var newPriority = GetFirstString(root, "newPriority", "new_priority", "priority");
            var directory = GetFirstString(root, "directory", "dir");

            if (string.IsNullOrWhiteSpace(action))
            {
                return null;
            }

            return new FileCommandDto
            {
                Action = action,
                Nr = nr ?? string.Empty,
                NewPriority = newPriority ?? string.Empty,
                Directory = directory ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback de desserialização falhou para comando.");
            return null;
        }
    }

    private static string? GetFirstString(JsonElement root, params string[] propertyNames)
    {
        foreach (var desired in propertyNames)
        {
            if (TryGetPropertyIgnoreCase(root, desired, out var value))
            {
                var str = ConvertToString(value);
                if (!string.IsNullOrWhiteSpace(str))
                {
                    return str;
                }
            }
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.NameEquals(propertyName) || prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ConvertToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private string ResolveTargetDirectory(string? directoryField)
    {
        var dir = directoryField?.Trim().ToUpperInvariant() switch
        {
            "LASER" => _options.LaserDirectory,
            "FACAS" => _options.FacasDirectory,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(dir))
        {
            return dir;
        }

        // Fallback defaults if options are missing (mirrors previous behavior).
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "/home/laser";
        }

        return @"D:\Laser";
    }
}

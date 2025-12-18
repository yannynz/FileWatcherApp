using FileWatcherApp.Messaging;
using FileWatcherApp.Services.FileWatcher;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileWatcherApp.Tests;

public class FileCommandHandlerTests
{
    [Fact]
    public void TryParse_AcceptsSnakeCaseAndNumericNr()
    {
        var handler = new FileCommandHandler(NullLogger.Instance, new FileWatcherOptions());
        var payload = "{\"action\":\"rename_priority\",\"nr\":514820,\"new_priority\":\"AZUL\",\"directory\":\"Laser\"}";

        var command = handler.TryParse(payload);

        Assert.NotNull(command);
        Assert.Equal("RENAME_PRIORITY", command!.Action, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("514820", command.Nr);
        Assert.Equal("AZUL", command.NewPriority);
        Assert.Equal("Laser", command.Directory);
    }

    [Fact]
    public async Task RenamePriorityAsync_RenamesMatchingFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var originalPath = Path.Combine(tempDir, "NR514820.dxf");
            await File.WriteAllTextAsync(originalPath, "test");

            var handler = new FileCommandHandler(NullLogger.Instance, new FileWatcherOptions
            {
                LaserDirectory = tempDir
            });

            await handler.RenamePriorityAsync(new FileCommandDto
            {
                Action = "RENAME_PRIORITY",
                Nr = "514820",
                NewPriority = "AZUL",
                Directory = "LASER"
            });

            Assert.False(File.Exists(originalPath));
            Assert.True(File.Exists(Path.Combine(tempDir, "NR514820_AZUL.dxf")));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}

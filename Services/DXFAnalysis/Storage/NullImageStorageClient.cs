using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace FileWatcherApp.Services.DXFAnalysis.Storage;

/// <summary>
/// No-op storage client used when uploads are disabled.
/// </summary>
public sealed class NullImageStorageClient : IImageStorageClient
{
    private readonly ILogger<NullImageStorageClient> _logger;

    public NullImageStorageClient(ILogger<NullImageStorageClient> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<ImageStorageUploadResult> UploadAsync(ImageStorageUploadRequest request, CancellationToken cancellationToken = default)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Image storage disabled - skipping upload for key {Key} ({Size} bytes)", request.ObjectKey, request.ContentLength);
        }

        return Task.FromResult(new ImageStorageUploadResult
        {
            Uploaded = false,
            ObjectKey = request.ObjectKey,
            SizeBytes = request.ContentLength,
            Status = "disabled"
        });
    }

    /// <inheritdoc />
    public Task<bool> ObjectExistsAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}

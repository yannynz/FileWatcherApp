using System.Threading;
using System.Threading.Tasks;

namespace FileWatcherApp.Services.DXFAnalysis.Storage;

/// <summary>
/// Abstraction for uploading rendered DXF images to an external storage backend.
/// </summary>
public interface IImageStorageClient
{
    /// <summary>
    /// Uploads the provided image stream to the configured backend.
    /// </summary>
    /// <param name="request">Upload parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A descriptor containing the stored object metadata.</returns>
    Task<ImageStorageUploadResult> UploadAsync(ImageStorageUploadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether the provided object key exists in the configured backend.
    /// </summary>
    /// <param name="bucket">Bucket/container name.</param>
    /// <param name="objectKey">Object key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when the object exists; otherwise, false.</returns>
    Task<bool> ObjectExistsAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

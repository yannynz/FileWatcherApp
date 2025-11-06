using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace FileWatcherApp.Services.DXFAnalysis.Storage;

/// <summary>
/// Represents the data required to upload an image to external storage.
/// </summary>
public sealed class ImageStorageUploadRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImageStorageUploadRequest"/> class.
    /// </summary>
    public ImageStorageUploadRequest(
        string objectKey,
        string contentType,
        long contentLength,
        Func<ValueTask<Stream>> openStream,
        string? checksum = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ObjectKey = objectKey ?? throw new ArgumentNullException(nameof(objectKey));
        ContentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
        ContentLength = contentLength;
        OpenStream = openStream ?? throw new ArgumentNullException(nameof(openStream));
        Checksum = checksum;
        Metadata = metadata ?? new Dictionary<string, string>();
    }

    /// <summary>
    /// Gets the storage object key.
    /// </summary>
    public string ObjectKey { get; }

    /// <summary>
    /// Gets the MIME type of the payload.
    /// </summary>
    public string ContentType { get; }

    /// <summary>
    /// Gets the payload length in bytes.
    /// </summary>
    public long ContentLength { get; }

    /// <summary>
    /// Gets an optional checksum (ex.: SHA-256 in hex).
    /// </summary>
    public string? Checksum { get; }

    /// <summary>
    /// Gets optional metadata to persist alongside the object.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>
    /// Gets the factory used to produce the upload stream.
    /// </summary>
    public Func<ValueTask<Stream>> OpenStream { get; }
}

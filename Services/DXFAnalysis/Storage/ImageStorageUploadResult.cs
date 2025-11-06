using System;

namespace FileWatcherApp.Services.DXFAnalysis.Storage;

/// <summary>
/// Represents the result produced after attempting to upload an image.
/// </summary>
public sealed class ImageStorageUploadResult
{
    /// <summary>
    /// Gets or sets a value indicating whether the upload was executed (false means skipped or disabled).
    /// </summary>
    public bool Uploaded { get; set; }

    /// <summary>
    /// Gets or sets the bucket/container name.
    /// </summary>
    public string? Bucket { get; set; }

    /// <summary>
    /// Gets or sets the storage object key.
    /// </summary>
    public string? ObjectKey { get; set; }

    /// <summary>
    /// Gets or sets the optionally resolvable URI.
    /// </summary>
    public Uri? PublicUri { get; set; }

    /// <summary>
    /// Gets or sets the server ETag (when available).
    /// </summary>
    public string? ETag { get; set; }

    /// <summary>
    /// Gets or sets the checksum reported by the backend (if any).
    /// </summary>
    public string? Checksum { get; set; }

    /// <summary>
    /// Gets or sets the payload size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Gets or sets an optional status label (ex.: skipped, failed).
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Gets or sets an optional failure message.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets an optional informational message returned alongside the upload status.
    /// </summary>
    public string? UploadMessage { get; set; }
}

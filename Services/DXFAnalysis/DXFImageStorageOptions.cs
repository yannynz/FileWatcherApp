namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Configuration for persisting rendered DXF images in an external storage backend.
/// </summary>
public sealed class DXFImageStorageOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether uploads are enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the storage provider identifier (ex.: s3).
    /// </summary>
    public string Provider { get; set; } = "s3";

    /// <summary>
    /// Gets or sets an optional prefix added ahead of the generated object key.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Gets or sets the bucket/container where images are stored.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the region (AWS-style) used when contacting the provider.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Gets or sets the optional custom endpoint (for MinIO or self-hosted S3-compatible services).
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the access key (when applicable).
    /// </summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret key (when applicable).
    /// </summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether the client should force path-style URLs.
    /// </summary>
    public bool UsePathStyle { get; set; } = true;

    /// <summary>
    /// Gets or sets an optional public base URL used to compose shareable links (ex.: CDN).
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether uploads should be skipped when the key already exists.
    /// </summary>
    public bool SkipIfExists { get; set; } = true;

    /// <summary>
    /// Gets or sets the upload timeout.
    /// </summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets or sets the maximum number of upload retries.
    /// </summary>
    public int MaxRetries { get; set; } = 3;
}

using System;

namespace FileWatcherApp.Services.DXFAnalysis;

/// <summary>
/// Represents the in-memory PNG produced from a DXF geometry snapshot.
/// </summary>
public sealed class DXFRenderedImage
{
    /// <summary>
    /// Gets or sets the friendly base name derived from the original file.
    /// </summary>
    public string SafeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the original file name (without path).
    /// </summary>
    public string OriginalFileName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the on-disk path where the PNG was written (when enabled).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Gets or sets the rendered width in pixels.
    /// </summary>
    public int WidthPx { get; set; }

    /// <summary>
    /// Gets or sets the rendered height in pixels.
    /// </summary>
    public int HeightPx { get; set; }

    /// <summary>
    /// Gets or sets the DPI used during rendering.
    /// </summary>
    public double Dpi { get; set; }

    /// <summary>
    /// Gets or sets the PNG payload.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Gets or sets the SHA-256 checksum of the payload (hex encoded).
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Gets the content type for the rendered image.
    /// </summary>
    public string ContentType => "image/png";

    /// <summary>
    /// Gets the size in bytes of the payload.
    /// </summary>
    public long Length => Data?.LongLength ?? 0;
}

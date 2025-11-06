using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace FileWatcherApp.Services.DXFAnalysis.Storage;

/// <summary>
/// S3-compatible implementation of <see cref="IImageStorageClient"/>.
/// </summary>
public sealed class S3ImageStorageClient : IImageStorageClient, IDisposable
{
    private readonly DXFImageStorageOptions _options;
    private readonly ILogger<S3ImageStorageClient> _logger;
    private readonly Lazy<IAmazonS3> _client;

    public S3ImageStorageClient(DXFImageStorageOptions options, ILogger<S3ImageStorageClient> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    /// <inheritdoc />
    public async Task<ImageStorageUploadResult> UploadAsync(ImageStorageUploadRequest request, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new ImageStorageUploadResult
            {
                Uploaded = false,
                ObjectKey = request.ObjectKey,
                SizeBytes = request.ContentLength,
                Status = "disabled"
            };
        }

        var key = NormalizeKey(request.ObjectKey);
        var client = _client.Value;

        if (_options.SkipIfExists)
        {
            try
            {
                _logger.LogDebug("Verificando existência de {Bucket}/{Key} antes do upload", _options.Bucket, key);
                var headResponse = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = _options.Bucket,
                    Key = key
                }, cancellationToken);

                string? existingChecksum = null;
                if (headResponse.Metadata is not null)
                {
                    foreach (var metaKey in headResponse.Metadata.Keys)
                    {
                        if (string.Equals(metaKey, "sha256", StringComparison.OrdinalIgnoreCase))
                        {
                            existingChecksum = headResponse.Metadata[metaKey];
                            break;
                        }
                    }
                }
                var checksumMatches = !string.IsNullOrWhiteSpace(request.Checksum)
                    && !string.IsNullOrWhiteSpace(existingChecksum)
                    && string.Equals(existingChecksum, request.Checksum, StringComparison.OrdinalIgnoreCase);
                var sizeMatches = headResponse.ContentLength == request.ContentLength;

                if (checksumMatches && sizeMatches)
                {
                    _logger.LogInformation("Imagem já existe no bucket {Bucket} com key {Key} - upload ignorado (checksum idêntico)", _options.Bucket, key);
                    var existsResult = new ImageStorageUploadResult
                    {
                        Uploaded = false,
                        Bucket = _options.Bucket,
                        ObjectKey = key,
                        SizeBytes = headResponse.ContentLength,
                        Status = "exists",
                        Checksum = existingChecksum ?? request.Checksum,
                        PublicUri = BuildPublicUri(key),
                        ETag = headResponse.ETag?.Trim('"')
                    };
                    return existsResult;
                }

                _logger.LogInformation(
                    "Imagem existente em {Bucket}/{Key} será sobrescrita (checksum atual={ExistingChecksum}, novo={NewChecksum}, tamanhoAtual={ExistingSize}, novo={NewSize})",
                    _options.Bucket,
                    key,
                    existingChecksum ?? "<null>",
                    request.Checksum ?? "<null>",
                    headResponse.ContentLength,
                    request.ContentLength);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // continue with upload
            }
        }

        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxRetries); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation("Enviando imagem para {Bucket}/{Key} (tentativa {Attempt}/{Max})", _options.Bucket, key, attempt, _options.MaxRetries);
                using var stream = await request.OpenStream();
                var putRequest = new PutObjectRequest
                {
                    BucketName = _options.Bucket,
                    Key = key,
                    InputStream = stream,
                    AutoCloseStream = true,
                    ContentType = request.ContentType
                };

                if (!string.IsNullOrWhiteSpace(request.Checksum))
                {
                    putRequest.Metadata["sha256"] = request.Checksum;
                }

                foreach (var (metaKey, metaValue) in request.Metadata)
                {
                    putRequest.Metadata[metaKey] = metaValue;
                }

                var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(_options.UploadTimeout);
                var response = await client.PutObjectAsync(putRequest, timeoutCts.Token);

                _logger.LogInformation("Upload concluído para {Bucket}/{Key} (ETag={Etag})", _options.Bucket, key, response.ETag);

                return new ImageStorageUploadResult
                {
                    Uploaded = true,
                    Bucket = _options.Bucket,
                    ObjectKey = key,
                    ETag = response.ETag?.Trim('"'),
                    SizeBytes = request.ContentLength,
                    Checksum = request.Checksum,
                    PublicUri = BuildPublicUri(key),
                    Status = "uploaded"
                };
            }
            catch (Exception ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Falha ao enviar imagem para {Bucket}/{Key} (tentativa {Attempt}/{Max})", _options.Bucket, key, attempt, _options.MaxRetries);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken);
            }
        }

        _logger.LogWarning("Falha ao enviar imagem após {Attempts} tentativas ({Bucket}/{Key})", _options.MaxRetries, _options.Bucket, key);
        return new ImageStorageUploadResult
        {
            Uploaded = false,
            Bucket = _options.Bucket,
            ObjectKey = key,
            SizeBytes = request.ContentLength,
            Status = "failed",
            ErrorMessage = $"Falha após {_options.MaxRetries} tentativas"
        };
    }

    private IAmazonS3 CreateClient()
    {
        AWSCredentials credentials;
        if (!string.IsNullOrWhiteSpace(_options.AccessKey) && !string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            credentials = new BasicAWSCredentials(_options.AccessKey, _options.SecretKey);
        }
        else
        {
            credentials = new AnonymousAWSCredentials();
        }

        var config = new AmazonS3Config
        {
            RegionEndpoint = RegionEndpoint.GetBySystemName(_options.Region),
            ForcePathStyle = _options.UsePathStyle
        };

        if (!string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            config.ServiceURL = _options.Endpoint;
            config.AuthenticationRegion = _options.Region;
        }

        return new AmazonS3Client(credentials, config);
    }

    /// <inheritdoc />
    public async Task<bool> ObjectExistsAsync(string bucket, string objectKey, CancellationToken cancellationToken = default)
    {
        bucket = string.IsNullOrWhiteSpace(bucket) ? _options.Bucket : bucket;
        if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(objectKey))
        {
            return false;
        }

        var normalizedKey = objectKey.Trim().TrimStart('/');
        var client = _client.Value;
        try
        {
            await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = normalizedKey
            }, cancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private string NormalizeKey(string key)
    {
        key = key.Trim().TrimStart('/');
        var prefix = string.IsNullOrWhiteSpace(_options.KeyPrefix)
            ? string.Empty
            : _options.KeyPrefix.Trim('/').Replace('\\', '/');

        return string.IsNullOrWhiteSpace(prefix) ? key : $"{prefix}/{key}";
    }

    private Uri? BuildPublicUri(string key)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
        {
            return null;
        }

        var baseUrl = _options.PublicBaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{key}");
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }
}

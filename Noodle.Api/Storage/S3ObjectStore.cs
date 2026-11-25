using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Noodle.Api.Configuration;
using Polly;
using Polly.Retry;

namespace Noodle.Api.Storage;

/// <summary>
///   S3 compatible object storage (MinIO, RustFS, Ceph, AWS, ...).
/// </summary>
/// <remarks>
///   TLS certificates are validated against the OS trust store, so an
///   internal CA has to be installed there (see the Dockerfile).
/// </remarks>
public sealed partial class S3ObjectStore : IObjectStore, IDisposable {
  // Files up to this size are sent in one PutObject request; larger ones
  // in parts of this size (several in parallel).
  private const long C_PART_SIZE_ = 32L * 1024 * 1024;
  private const int C_PART_PARALLELISM_ = 4;
  private const int C_DELETE_BATCH_ = 1000;
  private const string C_META_PREFIX_ = "x-amz-meta-";

  private readonly AmazonS3Client client_;
  private readonly ILogger<S3ObjectStore> logger_;
  private readonly StorageOptions options_;
  private readonly string prefix_;

  // Requests that failed for reasons other than the request itself (the
  // store was busy, a connection broke): sent again, from the start.
  private readonly ResiliencePipeline transient_;

  public S3ObjectStore(StorageOptions options, ILogger<S3ObjectStore> logger) {
    if (!BucketNameRegex().IsMatch(options.Bucket)) {
      // A configuration mistake: fail at startup rather than retry forever.
      throw new InvalidOperationException(
        $"Storage:Bucket '{options.Bucket}' is not a valid bucket name " +
        "(3-63 lower case letters, digits, dots and dashes)."
      );
    }

    options_ = options;
    logger_ = logger;
    string prefix = options.Prefix.Trim('/');
    prefix_ = prefix.Length == 0 ? "" : prefix + "/";

    AmazonS3Config config = new() {
      ForcePathStyle = options.ForcePathStyle,
      // The SDK's default flexible checksums (trailing CRC headers) are not
      // understood by older MinIO releases and other S3 compatible stores.
      RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
      ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
      // The SDK's own retries (with backoff) for transient errors; busy
      // stores answer 503 under load.
      RetryMode = RequestRetryMode.Standard,
      MaxErrorRetry = Math.Max(0, options.MaxErrorRetry),
      Timeout = TimeSpan.FromMinutes(5)
    };
    if (string.IsNullOrWhiteSpace(options.ServiceUrl)) {
      config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
    } else {
      config.ServiceURL = options.ServiceUrl;
      config.AuthenticationRegion = options.Region;
    }

    transient_ = new ResiliencePipelineBuilder()
                 .AddRetry(
                   new RetryStrategyOptions {
                     ShouldHandle =
                       new PredicateBuilder().Handle<Exception>(IsTransient),
                     MaxRetryAttempts = 4,
                     BackoffType = DelayBackoffType.Exponential,
                     UseJitter = true,
                     Delay = TimeSpan.FromMilliseconds(250),
                     MaxDelay = TimeSpan.FromSeconds(5),
                     OnRetry = args => {
                       logger.LogWarning(
                         "Storage request failed ({Reason}); retry {Attempt}",
                         args.Outcome.Exception?.Message,
                         args.AttemptNumber + 1
                       );
                       return ValueTask.CompletedTask;
                     }
                   }
                 )
                 .Build();
    client_ = string.IsNullOrEmpty(options.AccessKey)
                ? new AmazonS3Client(config)
                : new AmazonS3Client(
                  new BasicAWSCredentials(
                    options.AccessKey,
                    options.SecretKey ?? ""
                  ),
                  config
                );
  }

  public StoreCounters Counters { get; } = new();

  public string Description =>
    $"s3 ({options_.ServiceUrl ?? "aws:" + options_.Region}/" +
    $"{options_.Bucket}/{prefix_})";

  public void Dispose() {
    client_.Dispose();
  }

  public async Task InitializeAsync(CancellationToken ct) {
    if (await AmazonS3Util.DoesS3BucketExistV2Async(client_, options_.Bucket)) {
      return;
    }

    if (!options_.CreateBucket) {
      throw new InvalidOperationException(
        $"Bucket '{options_.Bucket}' does not exist " +
        "(and Storage:CreateBucket is false)."
      );
    }

    logger_.LogInformation("Creating bucket {Bucket}", options_.Bucket);
    PutBucketRequest request = new() {
      BucketName = options_.Bucket,
      // The SDK would otherwise send the client's region as location
      // constraint, which servers in another (or no) region reject.
      UseClientRegion = false
    };
    // us-east-1 is the default and must not be sent as a location
    // constraint; strict implementations (AWS, RustFS) reject it.
    if (!string.Equals(
          options_.Region,
          "us-east-1",
          StringComparison.OrdinalIgnoreCase
        )) {
      request.BucketRegionName = options_.Region;
    }

    await client_.PutBucketAsync(request, ct);
  }

  public async Task<StoredObject?> GetAsync(string key, CancellationToken ct) {
    ConditionalRead read = await GetIfChangedAsync(key, null, ct);
    return read.Object;
  }

  public async Task<ConditionalRead> GetIfChangedAsync(string key,
    string? if_none_match, CancellationToken ct) {
    GetObjectRequest request = new() {
      BucketName = options_.Bucket,
      Key = ToObjectKey(key)
    };
    if (if_none_match != null) {
      request.EtagToNotMatch = if_none_match;
    }

    Counters.Get();
    try {
      using GetObjectResponse response = await transient_.ExecuteAsync(
                                           async token =>
                                             await client_.GetObjectAsync(
                                               request,
                                               token
                                             ),
                                           ct
                                         );
      ObjectInfo info = ToInfo(key, response);
      using MemoryStream buffer = new(
        (int)Math.Clamp(info.Size, 0, int.MaxValue)
      );
      await response.ResponseStream.CopyToAsync(buffer, ct);
      return new ConditionalRead {
        Object = new StoredObject {
          Info = info,
          Content = buffer.ToArray()
        }
      };
    } catch (AmazonS3Exception ex) when (IsNotFound(ex)) {
      return ConditionalRead.S_MISSING;
    } catch (AmazonS3Exception ex) when (
      ex.StatusCode == HttpStatusCode.NotModified) {
      Counters.Unchanged();
      return ConditionalRead.S_NOT_MODIFIED;
    }
  }

  public async Task<ObjectReadStream?> OpenReadAsync(string key,
    CancellationToken ct) {
    Counters.Get();
    string object_key = ToObjectKey(key);
    try {
      GetObjectResponse response = await transient_.ExecuteAsync(
                                     async token =>
                                       await client_.GetObjectAsync(
                                         new GetObjectRequest {
                                           BucketName = options_.Bucket,
                                           Key = object_key
                                         },
                                         token
                                       ),
                                     ct
                                   );
      return new ObjectReadStream(
        ToInfo(key, response),
        response.ResponseStream,
        response
      );
    } catch (AmazonS3Exception ex) when (IsNotFound(ex)) {
      return null;
    }
  }

  public async Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) {
    Counters.Head();
    string object_key = ToObjectKey(key);
    try {
      GetObjectMetadataResponse response = await transient_.ExecuteAsync(
                                             async token =>
                                               await client_
                                                 .GetObjectMetadataAsync(
                                                   new
                                                     GetObjectMetadataRequest {
                                                       BucketName =
                                                         options_.Bucket,
                                                       Key = object_key
                                                     },
                                                   token
                                                 ),
                                             ct
                                           );
      return new ObjectInfo {
        Key = key,
        Size = response.ContentLength,
        ETag = response.ETag,
        LastModified = ToOffset(response.LastModified),
        ContentType = response.Headers.ContentType,
        Metadata = ReadMetadata(response.Metadata)
      };
    } catch (AmazonS3Exception ex) when (IsNotFound(ex)) {
      return null;
    }
  }

  public async Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                                      PutOptions options,
                                      CancellationToken ct) {
    using MemoryStream stream = MemoryMarshal.TryGetArray(
                                  content,
                                  out ArraySegment<byte> segment
                                )
                                  ? new MemoryStream(
                                    segment.Array!,
                                    segment.Offset,
                                    segment.Count,
                                    false
                                  )
                                  : new MemoryStream(content.ToArray(), false);
    return await PutStreamAsync(key, stream, options, ct);
  }

  public async Task<string?> PutFileAsync(string key, string path,
                                          PutOptions options,
                                          CancellationToken ct) {
    long size = new FileInfo(path).Length;
    if (size <= C_PART_SIZE_) {
      await using FileStream file = new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        128 * 1024,
        FileOptions.Asynchronous
      );
      return await PutStreamAsync(key, file, options, ct);
    }

    return await PutMultipartAsync(key, path, size, options, ct);
  }

  public async Task DeleteAsync(string key, CancellationToken ct) {
    Counters.Delete();
    string object_key = ToObjectKey(key);
    await transient_.ExecuteAsync(
      async token => await client_.DeleteObjectAsync(
                       new DeleteObjectRequest {
                         BucketName = options_.Bucket,
                         Key = object_key
                       },
                       token
                     ),
      ct
    );
  }

  public async Task DeleteManyAsync(IReadOnlyCollection<string> keys,
                                    CancellationToken ct) {
    foreach (string[] batch in keys.Chunk(C_DELETE_BATCH_)) {
      Counters.Delete();
      List<KeyVersion> objects = batch.Select(
                                        k => new KeyVersion {
                                          Key = ToObjectKey(k)
                                        }
                                      )
                                      .ToList();
      try {
        await transient_.ExecuteAsync(
          async token => await client_.DeleteObjectsAsync(
                           new DeleteObjectsRequest {
                             BucketName = options_.Bucket,
                             Quiet = true,
                             Objects = objects
                           },
                           token
                         ),
          ct
        );
      } catch (AmazonS3Exception ex) when (
        ex.StatusCode == HttpStatusCode.NotImplemented) {
        // Stores without multi-object delete.
        foreach (string key in batch) {
          await DeleteAsync(key, ct);
        }
      }
    }
  }

  public async IAsyncEnumerable<ObjectInfo> ListAsync(string prefix,
    string? start_after,
    [EnumeratorCancellation] CancellationToken ct) {
    ListObjectsV2Request request = new() {
      BucketName = options_.Bucket,
      Prefix = prefix_ + prefix,
      StartAfter = start_after == null ? null : prefix_ + start_after
    };
    while (true) {
      Counters.List();
      ListObjectsV2Response response = await transient_.ExecuteAsync(
                                         async token =>
                                           await client_.ListObjectsV2Async(
                                             request,
                                             token
                                           ),
                                         ct
                                       );
      foreach (S3Object item in response.S3Objects ?? []) {
        if (!item.Key.StartsWith(prefix_, StringComparison.Ordinal)) {
          continue;
        }

        yield return new ObjectInfo {
          Key = item.Key[prefix_.Length..],
          Size = item.Size ?? 0,
          ETag = item.ETag,
          LastModified = ToOffset(item.LastModified)
        };
      }

      if (response.IsTruncated != true ||
          string.IsNullOrEmpty(response.NextContinuationToken)) {
        yield break;
      }

      request.ContinuationToken = response.NextContinuationToken;
      request.StartAfter = null;
    }
  }

  private async Task<string?> PutStreamAsync(string key, Stream stream,
                                             PutOptions options,
                                             CancellationToken ct) {
    PutObjectRequest request = new() {
      BucketName = options_.Bucket,
      Key = ToObjectKey(key),
      InputStream = stream,
      AutoCloseStream = false,
      ContentType = options.ContentType,
      UseChunkEncoding = options_.UseChunkEncoding
    };
    if (options_.ConditionalWrites) {
      if (options.IfNoneMatch) {
        request.IfNoneMatch = "*";
      } else if (options.IfMatch != null) {
        request.IfMatch = options.IfMatch;
      }
    }

    AddMetadata(request.Metadata, options.Metadata);
    Counters.Put();
    try {
      PutObjectResponse response = await transient_.ExecuteAsync(
                                     async token => {
                                       // Every attempt sends the whole body.
                                       stream.Position = 0;
                                       return await client_.PutObjectAsync(
                                                request,
                                                token
                                              );
                                     },
                                     ct
                                   );
      return response.ETag;
    } catch (AmazonS3Exception ex) when (IsConditionFailure(ex)) {
      throw new PreconditionFailedException(key, ex);
    }
  }

  /// <summary>
  ///   Uploads in parts; the condition is checked when the upload is
  ///   completed, so a concurrent writer can never be overwritten.
  /// </summary>
  private async Task<string?> PutMultipartAsync(string key, string path,
                                                long size, PutOptions options,
                                                CancellationToken ct) {
    string object_key = ToObjectKey(key);
    InitiateMultipartUploadRequest initiate = new() {
      BucketName = options_.Bucket,
      Key = object_key,
      ContentType = options.ContentType
    };
    AddMetadata(initiate.Metadata, options.Metadata);

    Task<InitiateMultipartUploadResponse> Initiate(CancellationToken token) {
      return client_.InitiateMultipartUploadAsync(initiate, token);
    }

    Counters.Put();
    InitiateMultipartUploadResponse started = await SendAsync(Initiate, ct);
    try {
      int parts = (int)((size + C_PART_SIZE_ - 1) / C_PART_SIZE_);
      PartETag[] etags = new PartETag[parts];
      await Parallel.ForAsync(
        0,
        parts,
        new ParallelOptions {
          MaxDegreeOfParallelism = C_PART_PARALLELISM_,
          CancellationToken = ct
        },
        async (index, token) => {
          long position = index * C_PART_SIZE_;
          UploadPartResponse part = await transient_.ExecuteAsync(
                                      async attempt =>
                                        await client_.UploadPartAsync(
                                          new UploadPartRequest {
                                            BucketName = options_.Bucket,
                                            Key = object_key,
                                            UploadId = started.UploadId,
                                            PartNumber = index + 1,
                                            FilePath = path,
                                            FilePosition = position,
                                            PartSize = Math.Min(
                                              C_PART_SIZE_,
                                              size - position
                                            ),
                                            UseChunkEncoding =
                                              options_.UseChunkEncoding
                                          },
                                          attempt
                                        ),
                                      token
                                    );
          etags[index] = new PartETag(index + 1, part.ETag);
        }
      );
      CompleteMultipartUploadRequest complete = new() {
        BucketName = options_.Bucket,
        Key = object_key,
        UploadId = started.UploadId,
        PartETags = etags.ToList()
      };

      Task<CompleteMultipartUploadResponse> Complete(CancellationToken token) {
        return client_.CompleteMultipartUploadAsync(complete, token);
      }

      if (options_.ConditionalWrites) {
        if (options.IfNoneMatch) {
          complete.IfNoneMatch = "*";
        } else if (options.IfMatch != null) {
          complete.IfMatch = options.IfMatch;
        }
      }

      CompleteMultipartUploadResponse response = await SendAsync(Complete, ct);
      return response.ETag;
    } catch (Exception ex) {
      await AbortAsync(object_key, started.UploadId);
      if (ex is AmazonS3Exception s3 && IsConditionFailure(s3)) {
        throw new PreconditionFailedException(key, ex);
      }

      throw;
    }
  }

  private async Task AbortAsync(string object_key, string upload_id) {
    try {
      await client_.AbortMultipartUploadAsync(
        new AbortMultipartUploadRequest {
          BucketName = options_.Bucket,
          Key = object_key,
          UploadId = upload_id
        },
        CancellationToken.None
      );
    } catch (Exception ex) {
      logger_.LogWarning(
        ex,
        "Could not abort the multipart upload of {Key}",
        object_key
      );
    }
  }

  /// <summary>
  ///   Checks a key before it reaches S3. Package names may contain
  ///   characters such as <c>!</c> or <c>'</c>, but never empty, <c>.</c>
  ///   or <c>..</c> segments.
  /// </summary>
  private string ToObjectKey(string key) {
    if (!StorageKeys.IsSafeKey(key)) {
      throw new ArgumentException($"Invalid object key '{key}'", nameof(key));
    }

    return prefix_ + key;
  }

  /// <summary>Sends a request, again when it fails transiently.</summary>
  private async Task<T> SendAsync<T>(Func<CancellationToken, Task<T>> request,
                                     CancellationToken ct) {
    return await transient_.ExecuteAsync(
             async token => await request(token),
             ct
           );
  }

  /// <summary>Failures worth sending the same request again for.</summary>
  private static bool IsTransient(Exception ex) {
    return ex switch {
      AmazonS3Exception s3 => (int)s3.StatusCode >= 500 ||
                              s3.ErrorCode is "IncompleteBody" or
                                "RequestTimeout" or "SlowDown"
                                or "InternalError" or "ServiceUnavailable",
      AmazonServiceException service => (int)service.StatusCode >= 500,
      HttpRequestException or IOException => true,
      // HttpClient timeouts; caller cancellation is not retried by Polly.
      TaskCanceledException { InnerException: TimeoutException } => true,
      _ => false
    };
  }

  private static bool IsNotFound(AmazonS3Exception ex) {
    return ex.StatusCode == HttpStatusCode.NotFound;
  }

  private static bool IsConditionFailure(AmazonS3Exception ex) {
    // 409 is what S3 answers when two conditional writes race.
    return ex.StatusCode is HttpStatusCode.PreconditionFailed
             or HttpStatusCode.Conflict;
  }

  private static ObjectInfo ToInfo(string key, GetObjectResponse response) {
    return new ObjectInfo {
      Key = key,
      Size = response.ContentLength,
      ETag = response.ETag,
      LastModified = ToOffset(response.LastModified),
      ContentType = response.Headers.ContentType,
      Metadata = ReadMetadata(response.Metadata)
    };
  }

  private static DateTimeOffset ToOffset(DateTime? value) {
    return value == null
             ? DateTimeOffset.UnixEpoch
             : new DateTimeOffset(value.Value.ToUniversalTime(), TimeSpan.Zero);
  }

  private static void AddMetadata(MetadataCollection target,
                                  IReadOnlyDictionary<string, string>? source) {
    if (source == null) {
      return;
    }

    foreach (KeyValuePair<string, string> pair in source) {
      target.Add(pair.Key, pair.Value);
    }
  }

  private static Dictionary<string, string> ReadMetadata(
    MetadataCollection metadata) {
    Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
    foreach (string key in metadata.Keys) {
      // The SDK reports keys with their "x-amz-meta-" prefix.
      string name = key.StartsWith(
                      C_META_PREFIX_,
                      StringComparison.OrdinalIgnoreCase
                    )
                      ? key[C_META_PREFIX_.Length..]
                      : key;
      result[name] = metadata[key];
    }

    return result;
  }

  [System.Text.RegularExpressions.GeneratedRegex(
    "^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$"
  )]
  private static partial System.Text.RegularExpressions.Regex BucketNameRegex();
}
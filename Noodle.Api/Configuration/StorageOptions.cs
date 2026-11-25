namespace Noodle.Api.Configuration;

/// <summary>S3 settings (configuration section <c>Storage</c>).</summary>
public sealed class StorageOptions {
  public const string C_SECTION = "Storage";

  /// <summary>
  ///   Endpoint of an S3 compatible service, e.g. <c>https://minio:9000</c>.
  ///   Leave empty for AWS S3 (then <see cref="Region" /> selects the
  ///   endpoint).
  /// </summary>
  public string? ServiceUrl { get; set; }

  public string Bucket { get; set; } = "npm";

  /// <summary>Optional key prefix, to share a bucket with other data.</summary>
  public string Prefix { get; set; } = "";

  public string Region { get; set; } = "us-east-1";

  /// <summary>
  ///   Static credentials. When empty, the AWS default credential chain is
  ///   used (environment variables, profile, instance or pod identity).
  /// </summary>
  public string? AccessKey { get; set; }

  public string? SecretKey { get; set; }

  /// <summary>
  ///   Path style (<c>host/bucket/key</c>) URLs; needed by most non-AWS stores.
  /// </summary>
  public bool ForcePathStyle { get; set; } = true;

  /// <summary>
  ///   Uses <c>If-Match</c> / <c>If-None-Match</c> on writes so several
  ///   instances can update the same package safely. Disable only for stores
  ///   that reject conditional writes, and then run a single instance.
  /// </summary>
  public bool ConditionalWrites { get; set; } = true;

  /// <summary>Creates the bucket on startup when it does not exist.</summary>
  public bool CreateBucket { get; set; } = true;

  /// <summary>
  ///   Use <c>aws-chunked</c> payload signing for uploads. Some older S3
  ///   compatible stores need this switched off.
  /// </summary>
  public bool UseChunkEncoding { get; set; } = true;

  /// <summary>
  ///   How often the S3 client itself retries a request that failed with a
  ///   transient error (timeouts, 500, 503 "slow down"). Noodle retries
  ///   failed requests a few more times on top of that.
  /// </summary>
  public int MaxErrorRetry { get; set; } = 3;
}
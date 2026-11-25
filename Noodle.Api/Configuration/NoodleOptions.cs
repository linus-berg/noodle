namespace Noodle.Api.Configuration;

/// <summary>
///   General server settings (configuration section <c>Noodle</c>).
/// </summary>
public sealed class NoodleOptions {
  public const string C_SECTION = "Noodle";

  /// <summary>
  ///   Single key accepted (via <c>Authorization: Bearer</c> or
  ///   <c>X-Api-Key</c>) for uploads and other write operations.
  /// </summary>
  public string? ApiKey { get; set; }

  /// <summary>Additional accepted API keys, e.g. one per uploader.</summary>
  public List<string> ApiKeys { get; set; } = new();

  /// <summary>
  ///   Externally visible base URL (e.g. <c>https://registry.npmjs.org</c>).
  ///   When empty, tarball URLs are built from the scheme and host of each
  ///   request, which is what you want when several names point at the same
  ///   server.
  /// </summary>
  public string? PublicBaseUrl { get; set; }

  /// <summary>Maximum size of a single uploaded tarball.</summary>
  public long MaxTarballBytes { get; set; } = 2L * 1024 * 1024 * 1024;

  /// <summary>
  ///   Maximum number of bytes a tarball may unpack to (all entries
  ///   together), so gzip bombs are rejected.
  /// </summary>
  public long MaxUnpackedBytes { get; set; } = 8L * 1024 * 1024 * 1024;

  /// <summary>Logs every request (method, path, status, client).</summary>
  public bool LogRequests { get; set; } = true;

  /// <summary>
  ///   Memory for cached packuments (the gzipped documents clients fetch on
  ///   every install), in megabytes.
  /// </summary>
  public int PackumentCacheMegabytes { get; set; } = 512;

  /// <summary>
  ///   Memory for recently downloaded small tarballs (in megabytes), so
  ///   installs from many build agents do not each read every tarball from
  ///   S3. <c>0</c> disables it.
  /// </summary>
  public int TarballCacheMegabytes { get; set; } = 256;

  /// <summary>
  ///   Tarballs up to this size (kilobytes) are cached; larger ones are
  ///   streamed.
  /// </summary>
  public int TarballCacheMaxKilobytes { get; set; } = 1024;

  public SyncOptions Sync { get; set; } = new();

  public IngestOptions Ingest { get; set; } = new();

  public IReadOnlyList<string> GetApiKeys() {
    List<string> keys = ApiKeys.Where(k => !string.IsNullOrWhiteSpace(k))
                               .Select(k => k.Trim())
                               .ToList();
    if (!string.IsNullOrWhiteSpace(ApiKey)) {
      keys.Add(ApiKey.Trim());
    }

    return keys;
  }
}

/// <summary>
///   How instances share the in-memory catalog through S3 (section
///   <c>Noodle:Sync</c>).
/// </summary>
public sealed class SyncOptions {
  /// <summary>
  ///   How often (seconds) the change log is polled for packages uploaded
  ///   through other instances. <c>0</c> disables polling (tests only).
  /// </summary>
  public double PollIntervalSeconds { get; set; } = 5;

  /// <summary>How often (minutes) a catalog snapshot is written.</summary>
  public double SnapshotIntervalMinutes { get; set; } = 15;

  /// <summary>How often (seconds) download counts are persisted.</summary>
  public double DownloadFlushSeconds { get; set; } = 60;

  /// <summary>
  ///   How long change log entries are kept after they are included in a
  ///   snapshot. Instances that fall further behind reload the snapshot.
  /// </summary>
  public double ChangeRetentionHours { get; set; } = 24;

  /// <summary>
  ///   Parallel S3 reads when the catalog is rebuilt from scratch.
  /// </summary>
  public int RebuildParallelism { get; set; } = 32;
}

/// <summary>How uploads are committed (section <c>Noodle:Ingest</c>).</summary>
public sealed class IngestOptions {
  /// <summary>
  ///   When versions of one package keep arriving (bulk loads), uploads are
  ///   acknowledged as soon as the tarball is stored and the package is
  ///   rewritten at most this often (milliseconds), with every version that
  ///   arrived meanwhile. Single uploads are committed before the response.
  /// </summary>
  public int BatchDelayMilliseconds { get; set; } = 500;

  /// <summary>
  ///   A package counts as busy (batched) while its previous commit ended
  ///   less than this long ago (milliseconds).
  /// </summary>
  public int BusyWindowMilliseconds { get; set; } = 2000;

  /// <summary>
  ///   Acknowledged versions that are still not committed after this many
  ///   seconds (the instance stopped meanwhile) are committed by whichever
  ///   instance finds them first.
  /// </summary>
  public double RecoverAfterSeconds { get; set; } = 60;
}
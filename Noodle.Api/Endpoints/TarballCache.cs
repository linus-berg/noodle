using Microsoft.Extensions.Options;
using Noodle.Api.Configuration;
using Noodle.Api.Storage;

namespace Noodle.Api.Endpoints;

/// <summary>A small tarball held in memory.</summary>
public sealed class CachedTarball {
  public required byte[] Content { get; init; }
  public required ObjectInfo Info { get; init; }
}

/// <summary>
///   Recently downloaded small tarballs (most npm tarballs are a few kB),
///   so that many build agents installing the same lockfile do not each
///   read every tarball from S3. Keyed by the package's state as well, so a
///   version that is purged and uploaded again with other bytes is never
///   served stale.
/// </summary>
public sealed class TarballCache(IOptions<NoodleOptions> options) {
  private readonly LruCache<string, CachedTarball> cache_ = new(
    Math.Max(0, options.Value.TarballCacheMegabytes) * 1024L * 1024
  );

  private long hits_;
  private long misses_;

  public long Hits => Interlocked.Read(ref hits_);
  public long Misses => Interlocked.Read(ref misses_);
  public long Size => cache_.Size;

  /// <summary>Largest tarball kept, or 0 when caching is off.</summary>
  public long MaxBytes =>
    options.Value.TarballCacheMegabytes <= 0
      ? 0
      : Math.Max(0, options.Value.TarballCacheMaxKilobytes) * 1024L;

  public CachedTarball? Find(string key, string stamp) {
    CachedTarball? cached = cache_.Get(key + "|" + stamp);
    if (cached == null) {
      Interlocked.Increment(ref misses_);
    } else {
      Interlocked.Increment(ref hits_);
    }

    return cached;
  }

  public void Add(string key, string stamp, CachedTarball tarball) {
    cache_.Set(key + "|" + stamp, tarball, tarball.Content.Length + 256);
  }
}
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Noodle.Api.Configuration;
using Noodle.Api.Storage;

namespace Noodle.Api.Endpoints;

/// <summary>
///   Packuments (gzipped) recently served, by object key and base URL. The
///   objects are content addressed and never change, so cached copies need
///   no revalidation; an install of thousands of packages from many clients
///   costs one S3 read per package and instance.
/// </summary>
public sealed class PackumentCache(
  IObjectStore store,
  IOptions<NoodleOptions> options
) {
  private readonly LruCache<string, byte[]> cache_ = new(
    Math.Max(1, options.Value.PackumentCacheMegabytes) * 1024L * 1024
  );

  private readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> loading_ =
    new(StringComparer.Ordinal);

  private long hits_;
  private long misses_;

  public long Hits => Interlocked.Read(ref hits_);
  public long Misses => Interlocked.Read(ref misses_);
  public long Size => cache_.Size;

  /// <summary>
  ///   The stored object (rewritten by <paramref name="transform" /> when
  ///   given), or <c>null</c> when it does not exist.
  /// </summary>
  public async Task<byte[]?> GetAsync(string key, string variant,
                                      Func<byte[], byte[]>? transform,
                                      CancellationToken ct) {
    string cache_key = key + "|" + variant;
    byte[]? cached = cache_.Get(cache_key);
    if (cached != null) {
      Interlocked.Increment(ref hits_);
      return cached;
    }

    Interlocked.Increment(ref misses_);
    // Many clients asking for the same package at once share one read.
    Lazy<Task<byte[]?>> load = loading_.GetOrAdd(
      cache_key,
      _ => new Lazy<Task<byte[]?>>(() => LoadAsync(key, cache_key, transform))
    );
    try {
      return await load.Value.WaitAsync(ct);
    } finally {
      if (load.Value.IsCompleted) {
        loading_.TryRemove(
          new KeyValuePair<string, Lazy<Task<byte[]?>>>(cache_key, load)
        );
      }
    }
  }

  private async Task<byte[]?> LoadAsync(string key, string cache_key,
                                        Func<byte[], byte[]>? transform) {
    StoredObject? stored = await store.GetAsync(key, CancellationToken.None);
    if (stored == null) {
      return null;
    }

    byte[] content =
      transform == null ? stored.Content : transform(stored.Content);
    cache_.Set(cache_key, content, content.Length);
    return content;
  }
}
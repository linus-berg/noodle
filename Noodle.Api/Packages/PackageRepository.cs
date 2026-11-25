using System.Runtime.CompilerServices;
using Noodle.Api.Json;
using Noodle.Api.Storage;

namespace Noodle.Api.Packages;

/// <summary>A package document with the ETag it was read with.</summary>
public sealed class StoredDocument {
  public required PackageDocument Document { get; init; }
  public required string? ETag { get; init; }
}

/// <summary>
///   Reads and writes package documents (<c>packages/{name}.json.gz</c>).
///   Recently used documents are kept in memory and revalidated with
///   conditional GETs, so re-reading an unchanged document costs one small
///   request instead of downloading megabytes.
/// </summary>
public sealed class PackageRepository(IObjectStore store) {
  // Memory for cached documents (estimated).
  private const long C_CACHE_BYTES_ = 384L * 1024 * 1024;

  private readonly LruCache<string, CachedDocument>
    cache_ = new(C_CACHE_BYTES_);

  /// <summary>
  ///   The current document from S3 (revalidating a cached copy).
  /// </summary>
  public async Task<StoredDocument?> LoadAsync(string name,
                                               CancellationToken ct) {
    string key = StorageKeys.Package(name);
    CachedDocument? cached = cache_.Get(name);
    ConditionalRead read = await store.GetIfChangedAsync(
                             key,
                             cached?.ETag,
                             ct
                           );
    if (read.NotModified && cached != null) {
      return cached.ToStored();
    }

    if (read.Object == null) {
      cache_.Remove(name);
      return null;
    }

    PackageDocument document = NoodleJson.DeserializeGzip(
                                 read.Object.Content,
                                 NoodleJson.S_CONTEXT.PackageDocument
                               ) ??
                               throw new InvalidDataException(
                                 $"Empty package document for '{name}'"
                               );
    CachedDocument entry = new() {
      Document = document,
      ETag = read.Object.Info.ETag
    };
    cache_.Set(name, entry, EstimateSize(read.Object.Content.Length));
    return entry.ToStored();
  }

  /// <summary>
  ///   Writes a document unless another writer changed it since <paramref
  ///   name="if_match" />.
  /// </summary>
  /// <exception cref="PreconditionFailedException">
  ///   Lost against another writer.
  /// </exception>
  public async Task<string?> SaveAsync(PackageDocument document,
                                       string? if_match,
                                       CancellationToken ct) {
    byte[] content = NoodleJson.SerializeGzip(
      document,
      NoodleJson.S_CONTEXT.PackageDocument
    );
    try {
      string? etag = await store.PutAsync(
                       StorageKeys.Package(document.Name),
                       content,
                       new PutOptions {
                         ContentType = "application/gzip",
                         IfMatch = if_match,
                         IfNoneMatch = if_match == null,
                         Metadata = new Dictionary<string, string> {
                           ["revision"] = document.Revision.ToString(),
                           ["corgi"] = document.Corgi?.Hash ?? "",
                           ["full"] = document.Full?.Hash ?? ""
                         }
                       },
                       ct
                     );
      cache_.Set(
        document.Name,
        new CachedDocument {
          Document = document,
          ETag = etag
        },
        EstimateSize(content.Length)
      );
      return etag;
    } catch (PreconditionFailedException) {
      cache_.Remove(document.Name);
      throw;
    }
  }

  public async Task DeleteAsync(string name, CancellationToken ct) {
    cache_.Remove(name);
    await store.DeleteAsync(StorageKeys.Package(name), ct);
  }

  /// <summary>
  ///   The packument hashes of a document without downloading it (they are
  ///   in its S3 metadata), or <c>null</c> when the package does not exist.
  /// </summary>
  public async Task<(string Corgi, string Full)?> HeadPackumentsAsync(
    string name, CancellationToken ct) {
    CachedDocument? cached = cache_.Get(name);
    ObjectInfo? info = await store.HeadAsync(StorageKeys.Package(name), ct);
    if (info == null) {
      return null;
    }

    if (info.Metadata.TryGetValue("corgi", out string? corgi) &&
        info.Metadata.TryGetValue("full", out string? full) &&
        corgi.Length > 0 &&
        full.Length > 0) {
      return (corgi, full);
    }

    if (cached != null && cached.ETag == info.ETag) {
      return (cached.Document.Corgi?.Hash ?? "",
              cached.Document.Full?.Hash ?? "");
    }

    StoredDocument? loaded = await LoadAsync(name, ct);
    return loaded == null
             ? null
             : (loaded.Document.Corgi?.Hash ?? "",
                loaded.Document.Full?.Hash ?? "");
  }

  /// <summary>Names of all packages with a document, in key order.</summary>
  public async IAsyncEnumerable<string> ListNamesAsync(
    [EnumeratorCancellation] CancellationToken ct) {
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_PACKAGES_PREFIX,
                     null,
                     ct
                   )) {
      string? name = StorageKeys.NameFromPackageKey(item.Key);
      if (name != null) {
        yield return name;
      }
    }
  }

  /// <summary>
  ///   Documents take several times their compressed size in memory.
  /// </summary>
  private static long EstimateSize(long compressed) {
    return compressed * 8;
  }

  private sealed class CachedDocument {
    public required PackageDocument Document { get; init; }
    public required string? ETag { get; init; }

    public StoredDocument ToStored() {
      return new StoredDocument {
        Document = Document,
        ETag = ETag
      };
    }
  }
}
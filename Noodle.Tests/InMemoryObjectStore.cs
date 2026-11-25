using System.Runtime.CompilerServices;
using Noodle.Api.Storage;

namespace Noodle.Tests;

/// <summary>
///   An S3 stand-in with the semantics Noodle relies on: ETags, conditional
///   writes and reads, and key ordered listings. Share one between several
///   factories to simulate several instances.
/// </summary>
public sealed class InMemoryObjectStore : IObjectStore {
  private readonly SortedDictionary<string, Entry> objects_ =
    new(StringComparer.Ordinal);

  private int puts_;
  private int gets_;
  private int not_modified_;
  private int conflicts_;

  public string Description => "memory";

  public StoreCounters Counters { get; } = new();

  public int GetCount => gets_;

  /// <summary>Conditional writes that were refused.</summary>
  public int ConflictCount => conflicts_;

  /// <summary>Awaited before a stream is opened (to hold up loading).</summary>
  public Func<string, Task>? BeforeOpen { get; set; }

  /// <summary>Delay before each write, to make races likely.</summary>
  public TimeSpan WriteDelay { get; set; }

  public IReadOnlyList<string> Keys {
    get {
      lock (objects_) {
        return objects_.Keys.ToList();
      }
    }
  }

  public Task InitializeAsync(CancellationToken ct) {
    return Task.CompletedTask;
  }

  public Task<StoredObject?> GetAsync(string key, CancellationToken ct) {
    Interlocked.Increment(ref gets_);
    lock (objects_) {
      return Task.FromResult(
        objects_.TryGetValue(key, out Entry? entry)
          ? new StoredObject {
            Info = entry.Info,
            Content = entry.Content
          }
          : null
      );
    }
  }

  public async Task<ConditionalRead> GetIfChangedAsync(string key,
    string? if_none_match, CancellationToken ct) {
    StoredObject? stored = await GetAsync(key, ct);
    if (stored == null) {
      return ConditionalRead.S_MISSING;
    }

    if (if_none_match != null && stored.Info.ETag == if_none_match) {
      Interlocked.Increment(ref not_modified_);
      return ConditionalRead.S_NOT_MODIFIED;
    }

    return new ConditionalRead {
      Object = stored
    };
  }

  public async Task<ObjectReadStream?> OpenReadAsync(string key,
    CancellationToken ct) {
    if (BeforeOpen != null) {
      await BeforeOpen(key);
    }

    StoredObject? stored = await GetAsync(key, ct);
    return stored == null
             ? null
             : new ObjectReadStream(
               stored.Info,
               new MemoryStream(stored.Content, false),
               null
             );
  }

  public Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct) {
    lock (objects_) {
      return Task.FromResult(
        objects_.TryGetValue(key, out Entry? entry) ? entry.Info : null
      );
    }
  }

  public async Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                                      PutOptions options,
                                      CancellationToken ct) {
    if (!StorageKeys.IsSafeKey(key)) {
      throw new ArgumentException($"Invalid object key '{key}'", nameof(key));
    }

    if (WriteDelay > TimeSpan.Zero) {
      await Task.Delay(WriteDelay, ct);
    }

    lock (objects_) {
      bool exists = objects_.TryGetValue(key, out Entry? current);
      if ((options.IfNoneMatch && exists) ||
          (options.IfMatch != null && current?.Info.ETag != options.IfMatch)) {
        Interlocked.Increment(ref conflicts_);
        throw new PreconditionFailedException(key, null);
      }

      string etag = $"\"{Guid.NewGuid():N}\"";
      objects_[key] = new Entry {
        Content = content.ToArray(),
        Info = new ObjectInfo {
          Key = key,
          Size = content.Length,
          ETag = etag,
          LastModified = DateTimeOffset.UtcNow,
          ContentType = options.ContentType,
          Metadata = new Dictionary<string, string>(
            options.Metadata ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase
          )
        }
      };
      Interlocked.Increment(ref puts_);
      return etag;
    }
  }

  public async Task<string?> PutFileAsync(string key, string path,
                                          PutOptions options,
                                          CancellationToken ct) {
    byte[] content = await File.ReadAllBytesAsync(path, ct);
    return await PutAsync(key, content, options, ct);
  }

  public Task DeleteAsync(string key, CancellationToken ct) {
    lock (objects_) {
      objects_.Remove(key);
    }

    return Task.CompletedTask;
  }

  public Task DeleteManyAsync(IReadOnlyCollection<string> keys,
                              CancellationToken ct) {
    lock (objects_) {
      foreach (string key in keys) {
        objects_.Remove(key);
      }
    }

    return Task.CompletedTask;
  }

  public async IAsyncEnumerable<ObjectInfo> ListAsync(string prefix,
    string? start_after,
    [EnumeratorCancellation] CancellationToken ct) {
    List<ObjectInfo> items;
    lock (objects_) {
      items = objects_.Where(
                        pair => pair.Key.StartsWith(
                                  prefix,
                                  StringComparison.Ordinal
                                ) &&
                                (start_after == null ||
                                 string.CompareOrdinal(pair.Key, start_after) >
                                 0)
                      )
                      .Select(pair => pair.Value.Info)
                      .ToList();
    }

    foreach (ObjectInfo item in items) {
      await Task.Yield();
      yield return item;
    }
  }

  /// <summary>
  ///   Overwrites an object's content (to simulate outside changes).
  /// </summary>
  public void Replace(string key, byte[] content) {
    lock (objects_) {
      Entry entry = objects_[key];
      objects_[key] = new Entry {
        Content = content,
        Info = new ObjectInfo {
          Key = key,
          Size = content.Length,
          ETag = $"\"{Guid.NewGuid():N}\"",
          LastModified = DateTimeOffset.UtcNow,
          ContentType = entry.Info.ContentType,
          Metadata = entry.Info.Metadata
        }
      };
    }
  }

  /// <summary>
  ///   Moves an object's modification time back (for age based logic).
  /// </summary>
  public void Age(string key, TimeSpan by) {
    lock (objects_) {
      Entry entry = objects_[key];
      objects_[key] = new Entry {
        Content = entry.Content,
        Info = new ObjectInfo {
          Key = key,
          Size = entry.Info.Size,
          ETag = entry.Info.ETag,
          LastModified = entry.Info.LastModified - by,
          ContentType = entry.Info.ContentType,
          Metadata = entry.Info.Metadata
        }
      };
    }
  }

  public byte[]? Read(string key) {
    lock (objects_) {
      return objects_.TryGetValue(key, out Entry? entry) ? entry.Content : null;
    }
  }

  private sealed class Entry {
    public required byte[] Content { get; init; }
    public required ObjectInfo Info { get; init; }
  }
}
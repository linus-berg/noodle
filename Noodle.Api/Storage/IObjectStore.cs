namespace Noodle.Api.Storage;

/// <summary>
///   The object storage everything lives in. Keys are relative to the
///   configured prefix and always use <c>/</c> as separator.
/// </summary>
public interface IObjectStore {
  string Description { get; }

  /// <summary>
  ///   Requests sent to the store since start (for monitoring and benchmarks).
  /// </summary>
  StoreCounters Counters { get; }

  Task InitializeAsync(CancellationToken ct);

  /// <summary>
  ///   Reads a whole (small) object, or <c>null</c> when it does not exist.
  /// </summary>
  Task<StoredObject?> GetAsync(string key, CancellationToken ct);

  /// <summary>
  ///   Reads a whole object unless its ETag is still
  ///   <paramref name="if_none_match" /> (then
  ///   <see cref="ConditionalRead.NotModified" />).
  /// </summary>
  Task<ConditionalRead> GetIfChangedAsync(string key, string? if_none_match,
                                          CancellationToken ct);

  /// <summary>
  ///   Opens an object for streaming, or <c>null</c> when it does not exist.
  /// </summary>
  Task<ObjectReadStream?> OpenReadAsync(string key, CancellationToken ct);

  Task<ObjectInfo?> HeadAsync(string key, CancellationToken ct);

  /// <summary>Writes an object and returns its new ETag.</summary>
  /// <exception cref="PreconditionFailedException">
  ///   <see cref="PutOptions.IfMatch" /> or
  ///   <see cref="PutOptions.IfNoneMatch" /> did not hold.
  /// </exception>
  Task<string?> PutAsync(string key, ReadOnlyMemory<byte> content,
                         PutOptions options, CancellationToken ct);

  /// <summary>
  ///   Uploads a (possibly large) file, multipart where useful.
  /// </summary>
  /// <exception cref="PreconditionFailedException">
  ///   A condition in <paramref name="options" /> did not hold.
  /// </exception>
  Task<string?> PutFileAsync(string key, string path, PutOptions options,
                             CancellationToken ct);

  Task DeleteAsync(string key, CancellationToken ct);

  Task DeleteManyAsync(IReadOnlyCollection<string> keys, CancellationToken ct);

  /// <summary>
  ///   Lists objects in key order, starting after <paramref name="start_after"
  ///   />.
  /// </summary>
  IAsyncEnumerable<ObjectInfo> ListAsync(string prefix, string? start_after,
                                         CancellationToken ct);
}

/// <summary>Counts requests by kind.</summary>
public sealed class StoreCounters {
  private long deletes_;
  private long gets_;
  private long heads_;
  private long lists_;
  private long not_modified_;
  private long puts_;

  public long Gets => Interlocked.Read(ref gets_);
  public long NotModified => Interlocked.Read(ref not_modified_);
  public long Heads => Interlocked.Read(ref heads_);
  public long Puts => Interlocked.Read(ref puts_);
  public long Deletes => Interlocked.Read(ref deletes_);
  public long Lists => Interlocked.Read(ref lists_);

  public void Get() {
    Interlocked.Increment(ref gets_);
  }

  public void Unchanged() {
    Interlocked.Increment(ref not_modified_);
  }

  public void Head() {
    Interlocked.Increment(ref heads_);
  }

  public void Put() {
    Interlocked.Increment(ref puts_);
  }

  public void Delete() {
    Interlocked.Increment(ref deletes_);
  }

  public void List() {
    Interlocked.Increment(ref lists_);
  }
}

public sealed class PutOptions {
  public string ContentType { get; init; } = "application/octet-stream";

  /// <summary>Only write when the current object has this ETag.</summary>
  public string? IfMatch { get; init; }

  /// <summary>Only write when the object does not exist yet.</summary>
  public bool IfNoneMatch { get; init; }

  /// <summary>User metadata (<c>x-amz-meta-*</c>).</summary>
  public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed class ObjectInfo {
  public required string Key { get; init; }
  public long Size { get; init; }
  public string? ETag { get; init; }
  public DateTimeOffset LastModified { get; init; }
  public string? ContentType { get; init; }

  public IReadOnlyDictionary<string, string> Metadata { get; init; } =
    new Dictionary<string, string>();
}

public sealed class StoredObject {
  public required ObjectInfo Info { get; init; }
  public required byte[] Content { get; init; }
}

/// <summary>Result of <see cref="IObjectStore.GetIfChangedAsync" />.</summary>
public sealed class ConditionalRead {
  public static readonly ConditionalRead S_MISSING = new();

  public static readonly ConditionalRead S_NOT_MODIFIED = new() {
    NotModified = true
  };

  /// <summary>The object, when it exists and changed.</summary>
  public StoredObject? Object { get; init; }

  /// <summary>The object still has the ETag the caller has.</summary>
  public bool NotModified { get; init; }
}

/// <summary>
///   An open object body; dispose it to release the connection.
/// </summary>
public sealed class ObjectReadStream : IAsyncDisposable {
  private readonly IDisposable? owner_;

  public ObjectReadStream(ObjectInfo info, Stream stream, IDisposable? owner) {
    Info = info;
    Stream = stream;
    owner_ = owner;
  }

  public ObjectInfo Info { get; }
  public Stream Stream { get; }

  public async ValueTask DisposeAsync() {
    await Stream.DisposeAsync();
    owner_?.Dispose();
  }
}

/// <summary>A conditional write lost against a concurrent writer.</summary>
public sealed class PreconditionFailedException(string key, Exception? inner)
  : Exception($"Conditional write of '{key}' failed", inner);
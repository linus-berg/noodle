using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Noodle.Api.Catalog;
using Noodle.Api.Configuration;
using Noodle.Api.Json;
using Noodle.Api.Storage;
using Polly;
using Polly.Retry;

namespace Noodle.Api.Packages;

public enum ChangeStatus {
  /// <summary>The version was added.</summary>
  CREATED,

  /// <summary>The identical version was already there.</summary>
  EXISTS,

  /// <summary>A different tarball with this name and version exists.</summary>
  CONFLICT,

  /// <summary>
  ///   Stored durably; it appears in the package within seconds.
  /// </summary>
  ACCEPTED,

  REMOVED,
  UPDATED,
  NOT_FOUND
}

public sealed class ChangeResult {
  public required ChangeStatus Status { get; init; }
  public VersionRecord? Version { get; init; }
}

/// <summary>One requested change to a package.</summary>
public abstract class PackageChange {
  public TaskCompletionSource<ChangeResult> Completion { get; } =
    new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class AddVersion(VersionDocument version) : PackageChange {
  public VersionDocument Version { get; } = version;

  /// <summary>
  ///   A <c>pending/</c> marker was written for it (it is acknowledged
  ///   already).
  /// </summary>
  public bool HasMarker { get; init; }
}

public sealed class RemoveVersion(string version) : PackageChange {
  public string Version { get; } = version;
}

public sealed class SetTag(string tag, string version) : PackageChange {
  public string Tag { get; } = tag;
  public string Version { get; } = version;
}

public sealed class RemoveTag(string tag) : PackageChange {
  public string Tag { get; } = tag;
}

/// <summary>
///   Regenerates the packuments (e.g. after an upgrade or a base URL change).
/// </summary>
public sealed class Regenerate : PackageChange;

/// <summary>
///   Applies changes to package documents. Changes to one package are
///   queued and committed together ("group commit"): while versions of a
///   package keep arriving, each commit takes everything that arrived since
///   the previous one, so a bulk load of a package with thousands of
///   versions rewrites its multi-megabyte document a few dozen times, not
///   thousands. Commits are conditional writes, retried when another
///   instance got there first.
/// </summary>
public sealed class PackageCommitter : IAsyncDisposable {
  private readonly PackageCatalog catalog_;
  private readonly ChangeFeed feed_;
  private readonly ILogger<PackageCommitter> logger_;
  private readonly IOptions<NoodleOptions> options_;

  private readonly ConcurrentDictionary<string, PackageQueue> queues_ =
    new(StringComparer.Ordinal);

  private readonly PackageRepository repository_;
  private readonly ResiliencePipeline retry_;
  private readonly IObjectStore store_;
  private long commits_;
  private long conflicts_;
  private long enqueued_;
  private long written_bytes_;

  public PackageCommitter(IObjectStore store, PackageRepository repository,
                          PackageCatalog catalog, ChangeFeed feed,
                          IOptions<NoodleOptions> options,
                          ILogger<PackageCommitter> logger) {
    store_ = store;
    repository_ = repository;
    catalog_ = catalog;
    feed_ = feed;
    options_ = options;
    logger_ = logger;
    retry_ = new ResiliencePipelineBuilder()
             .AddRetry(
               new RetryStrategyOptions {
                 ShouldHandle = new PredicateBuilder()
                   .Handle<PreconditionFailedException>(),
                 MaxRetryAttempts = 30,
                 BackoffType = DelayBackoffType.Exponential,
                 UseJitter = true,
                 Delay = TimeSpan.FromMilliseconds(25),
                 MaxDelay = TimeSpan.FromSeconds(2),
                 OnRetry = args => {
                   Interlocked.Increment(ref conflicts_);
                   logger.LogDebug(
                     "Concurrent update ({Reason}), retry {Attempt}",
                     args.Outcome.Exception?.Message,
                     args.AttemptNumber + 1
                   );
                   return ValueTask.CompletedTask;
                 }
               }
             )
             .Build();
  }

  /// <summary>Commits written (package documents and packuments).</summary>
  public long Commits => Interlocked.Read(ref commits_);

  /// <summary>Lost write races that were retried.</summary>
  public long Conflicts => Interlocked.Read(ref conflicts_);

  /// <summary>Bytes of documents and packuments written.</summary>
  public long WrittenBytes => Interlocked.Read(ref written_bytes_);

  /// <summary>Base URL in stored packuments' tarball URLs.</summary>
  public string StoredBaseUrl =>
    string.IsNullOrWhiteSpace(options_.Value.PublicBaseUrl)
      ? PackumentWriter.C_DEFAULT_BASE_URL
      : options_.Value.PublicBaseUrl.TrimEnd('/');

  public async ValueTask DisposeAsync() {
    await DrainAsync(TimeSpan.FromSeconds(30));
  }

  /// <summary>
  ///   Adds a version whose tarball and version document are stored. Waits
  ///   for the commit unless the package is busy: then the version is
  ///   acknowledged right away (after writing a recovery marker) and
  ///   committed with the next batch.
  /// </summary>
  public async Task<ChangeResult> AddVersionAsync(VersionDocument version,
                                                  bool allow_deferred,
                                                  CancellationToken ct) {
    PackageQueue queue = GetQueue(version.Name);
    if (allow_deferred && queue.IsBusy(BusyWindow)) {
      await store_.PutAsync(
        StorageKeys.Pending(version.Name, version.Record.Version),
        Array.Empty<byte>(),
        new PutOptions {
          ContentType = "text/plain"
        },
        ct
      );
      AddVersion deferred = new(version) {
        HasMarker = true
      };
      // Observe failures: recovery commits the version later.
      _ = deferred.Completion.Task.ContinueWith(
        t => t.Exception,
        TaskContinuationOptions.OnlyOnFaulted
      );
      Enqueue(version.Name, deferred);
      return new ChangeResult {
        Status = ChangeStatus.ACCEPTED,
        Version = version.Record
      };
    }

    return await SubmitAsync(version.Name, new AddVersion(version), ct);
  }

  public async Task<ChangeResult> SubmitAsync(string name,
                                              PackageChange change,
                                              CancellationToken ct) {
    Enqueue(name, change);
    return await change.Completion.Task.WaitAsync(ct);
  }

  /// <summary>Forgets packages that have been idle for a while.</summary>
  public void PruneIdleQueues() {
    TimeSpan window = BusyWindow;
    foreach (KeyValuePair<string, PackageQueue> pair in queues_) {
      if (pair.Value.TryRetire(window)) {
        queues_.TryRemove(pair);
      }
    }
  }

  /// <summary>Whether a version is queued here and not committed yet.</summary>
  public bool IsQueued(string name, string version) {
    return queues_.TryGetValue(name, out PackageQueue? queue) &&
           queue.Contains(version);
  }

  /// <summary>
  ///   Waits until all queued changes are committed (on shutdown).
  /// </summary>
  public async Task DrainAsync(TimeSpan timeout) {
    Task[] running = queues_.Values.Select(q => q.Runner)
                            .Where(t => t != null)
                            .Select(t => t!)
                            .ToArray();
    if (running.Length == 0) {
      return;
    }

    try {
      await Task.WhenAll(running).WaitAsync(timeout);
    } catch (Exception ex) when (ex is TimeoutException or
                                   OperationCanceledException) {
      logger_.LogWarning(
        "Pending package commits did not finish; they are recovered later"
      );
    } catch (Exception ex) {
      logger_.LogWarning(ex, "Package commits failed while shutting down");
    }
  }

  private TimeSpan BusyWindow =>
    TimeSpan.FromMilliseconds(
      Math.Max(0, options_.Value.Ingest.BusyWindowMilliseconds)
    );

  private PackageQueue GetQueue(string name) {
    return queues_.GetOrAdd(name, n => new PackageQueue(n));
  }

  private void Enqueue(string name, PackageChange change) {
    // (ConcurrentDictionary.Count takes every lock: count enqueues instead.)
    if (Interlocked.Increment(ref enqueued_) % 10_000 == 0) {
      PruneIdleQueues();
    }

    while (true) {
      PackageQueue queue = GetQueue(name);
      switch (queue.Add(change, BusyWindow)) {
        case QueueAdd.RETIRED:
          continue;
        case QueueAdd.START:
          queue.Runner = Task.Run(() => RunAsync(queue));
          break;
      }

      return;
    }
  }

  private async Task RunAsync(PackageQueue queue) {
    TimeSpan delay = TimeSpan.FromMilliseconds(
      Math.Max(0, options_.Value.Ingest.BatchDelayMilliseconds)
    );
    bool gather = queue.StartedBusy;
    while (true) {
      if (gather && delay > TimeSpan.Zero && queue.OnlyDeferred) {
        // More versions of this package are on their way; take them along.
        // (Not when someone waits for the commit.)
        await Task.Delay(delay);
      }

      List<PackageChange>? batch = queue.TakeAll();
      if (batch == null) {
        break;
      }

      try {
        await CommitAsync(queue.Name, batch);
      } catch (Exception ex) {
        logger_.LogError(
          ex,
          "Could not commit {Count} change(s) to {Package}",
          batch.Count,
          queue.Name
        );
        foreach (PackageChange change in batch) {
          change.Completion.TrySetException(ex);
        }
      } finally {
        queue.Committed(batch);
      }

      gather = true;
    }
  }

  private async Task CommitAsync(string name, List<PackageChange> batch) {
    long started = System.Diagnostics.Stopwatch.GetTimestamp();
    Outcome outcome = await retry_.ExecuteAsync(
                        async token => await TryCommitAsync(name, batch, token),
                        CancellationToken.None
                      );
    List<string> marked = batch.OfType<AddVersion>()
                               .Where(a => a.HasMarker)
                               .Select(a => a.Version.Record.Version)
                               .ToList();
    if (outcome.Changed) {
      await AfterCommitAsync(name, outcome, marked);
    } else if (marked.Count > 0) {
      // Already committed elsewhere: the markers are done with.
      await store_.DeleteManyAsync(
        marked.Select(v => StorageKeys.Pending(name, v)).ToList(),
        CancellationToken.None
      );
    }

    logger_.LogDebug(
      "Committed {Count} change(s) to {Package} (revision {Revision}) " +
      "in {Elapsed:0} ms",
      batch.Count,
      name,
      outcome.Document?.Revision,
      System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
    );

    for (int i = 0; i < batch.Count; i++) {
      batch[i].Completion.TrySetResult(outcome.Results[i]);
    }
  }

  /// <exception cref="PreconditionFailedException">
  ///   Lost against another writer.
  /// </exception>
  private async Task<Outcome> TryCommitAsync(string name,
                                             List<PackageChange> batch,
                                             CancellationToken ct) {
    StoredDocument? stored = await repository_.LoadAsync(name, ct);
    PackageDocument? current = stored?.Document;
    List<VersionRecord> versions = current?.Versions.ToList() ?? [];
    Dictionary<string, string> tags = new(
      current?.PinnedTags ?? new Dictionary<string, string>(),
      StringComparer.Ordinal
    );
    Dictionary<string, VersionDocument> added = new(StringComparer.Ordinal);
    List<string> removed = [];
    ChangeResult[] results = new ChangeResult[batch.Count];
    bool changed = false;
    for (int i = 0; i < batch.Count; i++) {
      results[i] = Apply(
        batch[i],
        versions,
        tags,
        added,
        removed,
        ref changed
      );
    }

    if (!changed) {
      return new Outcome {
        Results = results,
        Document = current
      };
    }

    PackageDocument.SortVersions(versions);
    foreach (KeyValuePair<string, string> tag in tags.ToList()) {
      if (!versions.Any(v => v.Version == tag.Value)) {
        tags.Remove(tag.Key);
      }
    }

    if (versions.Count == 0) {
      if (stored != null) {
        await repository_.DeleteAsync(name, ct);
      }

      return new Outcome {
        Results = results,
        Changed = true,
        Previous = current,
        Removed = removed
      };
    }

    PackageDocument draft = new() {
      Name = name,
      Revision = (current?.Revision ?? 0) + 1,
      Modified = DateTimeOffset.UtcNow,
      Versions = versions,
      PinnedTags = tags
    };
    string? latest = draft.Latest;
    (string? readme, string? readme_file) = await GetReadmeAsync(
                                              name,
                                              latest,
                                              current,
                                              added,
                                              ct
                                            );
    string base_url = StoredBaseUrl;
    draft = With(draft, readme, readme_file, latest, base_url);
    GeneratedDocument corgi = PackumentWriter.Pack(
      PackumentWriter.WriteCorgi(draft, base_url)
    );
    GeneratedDocument full = PackumentWriter.Pack(
      PackumentWriter.WriteFull(draft, base_url)
    );
    // Content addressed, so writing them before the commit is harmless:
    // nothing refers to them until the package document does.
    await Task.WhenAll(
      WritePackumentAsync(name, corgi, true, ct),
      WritePackumentAsync(name, full, false, ct)
    );
    List<string> previous = [];
    if (current?.Corgi != null) {
      previous.Add(StorageKeys.Packument(name, current.Corgi.Hash, true));
    }

    if (current?.Full != null) {
      previous.Add(StorageKeys.Packument(name, current.Full.Hash, false));
    }

    PackageDocument document = new() {
      Name = draft.Name,
      Revision = draft.Revision,
      Modified = draft.Modified,
      Versions = draft.Versions,
      PinnedTags = draft.PinnedTags,
      Readme = draft.Readme,
      ReadmeFilename = draft.ReadmeFilename,
      ReadmeVersion = draft.ReadmeVersion,
      BaseUrl = base_url,
      Corgi = new PackumentRef {
        Hash = corgi.Hash,
        Size = corgi.Gzip.Length
      },
      Full = new PackumentRef {
        Hash = full.Hash,
        Size = full.Gzip.Length
      },
      PreviousPackuments = previous
    };
    await repository_.SaveAsync(document, stored?.ETag, ct);
    Interlocked.Increment(ref commits_);
    Interlocked.Add(
      ref written_bytes_,
      corgi.Gzip.Length + full.Gzip.Length
    );
    return new Outcome {
      Results = results,
      Changed = true,
      Document = document,
      Previous = current,
      Removed = removed
    };
  }

  private static ChangeResult Apply(PackageChange change,
                                    List<VersionRecord> versions,
                                    Dictionary<string, string> tags,
                                    Dictionary<string, VersionDocument> added,
                                    List<string> removed, ref bool changed) {
    switch (change) {
      case AddVersion add: {
        VersionRecord record = add.Version.Record;
        VersionRecord? existing =
          versions.Find(v => v.Version == record.Version);
        if (existing != null) {
          return new ChangeResult {
            Status = existing.Integrity == record.Integrity
                       ? ChangeStatus.EXISTS
                       : ChangeStatus.CONFLICT,
            Version = existing
          };
        }

        versions.Add(record);
        added[record.Version] = add.Version;
        changed = true;
        return new ChangeResult {
          Status = ChangeStatus.CREATED,
          Version = record
        };
      }
      case RemoveVersion remove: {
        VersionRecord? existing =
          versions.Find(v => v.Version == remove.Version);
        if (existing == null) {
          return new ChangeResult {
            Status = ChangeStatus.NOT_FOUND
          };
        }

        versions.Remove(existing);
        added.Remove(existing.Version);
        removed.Add(existing.Version);
        changed = true;
        return new ChangeResult {
          Status = ChangeStatus.REMOVED,
          Version = existing
        };
      }
      case SetTag set: {
        VersionRecord? target = versions.Find(v => v.Version == set.Version);
        if (target == null) {
          return new ChangeResult {
            Status = ChangeStatus.NOT_FOUND
          };
        }

        if (!tags.TryGetValue(set.Tag, out string? old) || old != set.Version) {
          tags[set.Tag] = set.Version;
          changed = true;
        }

        return new ChangeResult {
          Status = ChangeStatus.UPDATED,
          Version = target
        };
      }
      case RemoveTag remove_tag: {
        if (!tags.Remove(remove_tag.Tag)) {
          return new ChangeResult {
            Status = ChangeStatus.NOT_FOUND
          };
        }

        changed = true;
        return new ChangeResult {
          Status = ChangeStatus.UPDATED
        };
      }
      case Regenerate:
        changed = versions.Count > 0;
        return new ChangeResult {
          Status = versions.Count > 0
                     ? ChangeStatus.UPDATED
                     : ChangeStatus.NOT_FOUND
        };
      default:
        throw new ArgumentOutOfRangeException(nameof(change));
    }
  }

  private static PackageDocument With(PackageDocument draft, string? readme,
                                      string? readme_file, string? latest,
                                      string base_url) {
    return new PackageDocument {
      Name = draft.Name,
      Revision = draft.Revision,
      CommitId = draft.CommitId,
      Modified = draft.Modified,
      Versions = draft.Versions,
      PinnedTags = draft.PinnedTags,
      Readme = readme,
      ReadmeFilename = readme_file,
      ReadmeVersion = latest,
      BaseUrl = base_url
    };
  }

  /// <summary>
  ///   The readme of the latest version, which the full packument shows.
  /// </summary>
  private async Task<(string?, string?)> GetReadmeAsync(string name,
    string? latest, PackageDocument? current,
    Dictionary<string, VersionDocument> added, CancellationToken ct) {
    if (latest == null) {
      return (null, null);
    }

    if (added.TryGetValue(latest, out VersionDocument? version)) {
      return (version.Readme, version.ReadmeFilename);
    }

    if (current != null && current.ReadmeVersion == latest) {
      return (current.Readme, current.ReadmeFilename);
    }

    StoredObject? stored = await store_.GetAsync(
                             StorageKeys.Version(name, latest),
                             ct
                           );
    if (stored == null) {
      return (null, null);
    }

    VersionDocument? document = NoodleJson.DeserializeGzip(
      stored.Content,
      NoodleJson.S_CONTEXT.VersionDocument
    );
    return (document?.Readme, document?.ReadmeFilename);
  }

  private async Task WritePackumentAsync(string name,
                                         GeneratedDocument document,
                                         bool corgi, CancellationToken ct) {
    await store_.PutAsync(
      StorageKeys.Packument(name, document.Hash, corgi),
      document.Gzip,
      new PutOptions {
        ContentType = "application/gzip",
        Metadata = new Dictionary<string, string> {
          ["length"] = document.Length.ToString()
        }
      },
      ct
    );
  }

  /// <summary>
  ///   Announces the change, updates this instance and cleans up.
  /// </summary>
  private async Task AfterCommitAsync(string name, Outcome outcome,
                                      List<string> marked) {
    CancellationToken ct = CancellationToken.None;
    await feed_.AppendAsync(ChangeKind.PACKAGE, name, null, ct);
    catalog_.Apply(name, outcome.Document);

    List<string> garbage = [];
    HashSet<string> keep = new(StringComparer.Ordinal);
    if (outcome.Document != null) {
      keep.UnionWith(outcome.Document.PreviousPackuments);
      if (outcome.Document.Corgi != null) {
        keep.Add(
          StorageKeys.Packument(name, outcome.Document.Corgi.Hash, true)
        );
      }

      if (outcome.Document.Full != null) {
        keep.Add(
          StorageKeys.Packument(name, outcome.Document.Full.Hash, false)
        );
      }
    }

    if (outcome.Previous != null) {
      // Packuments two revisions old: other instances that have not polled
      // yet may still serve the previous ones.
      garbage.AddRange(outcome.Previous.PreviousPackuments);
      if (outcome.Document == null) {
        garbage.AddRange(keep);
        if (outcome.Previous.Corgi != null) {
          garbage.Add(
            StorageKeys.Packument(name, outcome.Previous.Corgi.Hash, true)
          );
        }

        if (outcome.Previous.Full != null) {
          garbage.Add(
            StorageKeys.Packument(name, outcome.Previous.Full.Hash, false)
          );
        }
      }
    }

    foreach (string version in outcome.Removed) {
      garbage.Add(StorageKeys.Tarball(name, version));
      garbage.Add(StorageKeys.Version(name, version));
      garbage.Add(StorageKeys.Pending(name, version));
    }

    foreach (string version in marked) {
      garbage.Add(StorageKeys.Pending(name, version));
    }

    List<string> delete = garbage
                          .Where(
                            k => outcome.Document == null || !keep.Contains(k)
                          )
                          .Distinct(StringComparer.Ordinal)
                          .ToList();
    if (delete.Count > 0) {
      try {
        await store_.DeleteManyAsync(delete, ct);
      } catch (Exception ex) {
        logger_.LogWarning(
          ex,
          "Could not clean up after a commit of {Package}",
          name
        );
      }
    }
  }

  private sealed class Outcome {
    public required ChangeResult[] Results { get; init; }
    public bool Changed { get; init; }
    public PackageDocument? Document { get; init; }
    public PackageDocument? Previous { get; init; }
    public List<string> Removed { get; init; } = [];
  }

  private enum QueueAdd {
    QUEUED,
    START,
    RETIRED
  }

  /// <summary>
  ///   Changes waiting for one package, and its committer task.
  /// </summary>
  private sealed class PackageQueue(string name) {
    private readonly Lock lock_ = new();
    private List<PackageChange> pending_ = [];
    private readonly HashSet<string> versions_ = new(StringComparer.Ordinal);
    private bool running_;
    private bool retired_;
    private DateTimeOffset last_commit_ = DateTimeOffset.MinValue;

    public string Name { get; } = name;
    public Task? Runner { get; set; }

    /// <summary>The runner was started while the package was busy.</summary>
    public bool StartedBusy { get; private set; }

    /// <summary>
    ///   Changes are queued, and nobody waits for them to be committed.
    /// </summary>
    public bool OnlyDeferred {
      get {
        lock (lock_) {
          return pending_.Count > 0 &&
                 pending_.All(c => c is AddVersion { HasMarker: true });
        }
      }
    }

    public bool IsBusy(TimeSpan window) {
      lock (lock_) {
        return running_ || DateTimeOffset.UtcNow - last_commit_ < window;
      }
    }

    public bool Contains(string version) {
      lock (lock_) {
        return versions_.Contains(version);
      }
    }

    public QueueAdd Add(PackageChange change, TimeSpan busy_window) {
      lock (lock_) {
        if (retired_) {
          return QueueAdd.RETIRED;
        }

        pending_.Add(change);
        if (change is AddVersion add) {
          versions_.Add(add.Version.Record.Version);
        }

        if (running_) {
          return QueueAdd.QUEUED;
        }

        StartedBusy = DateTimeOffset.UtcNow - last_commit_ < busy_window;
        running_ = true;
        return QueueAdd.START;
      }
    }

    /// <summary>Retires an idle queue so it can be forgotten.</summary>
    public bool TryRetire(TimeSpan busy_window) {
      lock (lock_) {
        if (running_ ||
            pending_.Count > 0 ||
            DateTimeOffset.UtcNow - last_commit_ < busy_window) {
          return false;
        }

        retired_ = true;
        return true;
      }
    }

    /// <summary>
    ///   Takes everything queued, or stops the runner when nothing is.
    /// </summary>
    public List<PackageChange>? TakeAll() {
      lock (lock_) {
        if (pending_.Count == 0) {
          running_ = false;
          return null;
        }

        List<PackageChange> batch = pending_;
        pending_ = [];
        return batch;
      }
    }

    public void Committed(List<PackageChange> batch) {
      lock (lock_) {
        last_commit_ = DateTimeOffset.UtcNow;
        foreach (PackageChange change in batch) {
          if (change is AddVersion add) {
            versions_.Remove(add.Version.Record.Version);
          }
        }
      }
    }
  }
}
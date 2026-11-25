using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Noodle.Api.Advisories;
using Noodle.Api.Configuration;
using Noodle.Api.Json;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Api.Catalog;

/// <summary>What a rebuild re-derives.</summary>
public enum RebuildScope {
  /// <summary>The catalog, from the package documents.</summary>
  CATALOG,

  /// <summary>Also the package documents, from the version documents.</summary>
  VERSIONS,

  /// <summary>
  ///   Also every packument (after an upgrade or base URL change).
  /// </summary>
  PACKUMENTS
}

/// <summary>
///   Keeps this instance's catalog in step with S3: loads the snapshot (or
///   rebuilds it from the package documents), applies the change log,
///   publishes download counts, writes snapshots, and commits versions that
///   were acknowledged by an instance that stopped before committing them.
/// </summary>
public sealed class CatalogSynchronizer(
  IObjectStore store,
  PackageRepository repository,
  PackageCommitter committer,
  PackageCatalog catalog,
  DownloadStatistics downloads,
  AdvisoryStore advisories,
  ChangeFeed feed,
  IOptions<NoodleOptions> options,
  ILogger<CatalogSynchronizer> logger
) {
  private const string C_WATERMARK_METADATA_ = "watermark";
  private const int C_POLL_PARALLELISM_ = 16;

  private readonly SemaphoreSlim gate_ = new(1, 1);
  private DateTimeOffset last_snapshot_ = DateTimeOffset.MinValue;
  private long snapshot_generation_ = -1;
  private long snapshot_downloads_ = -1;

  // After a rebuild the package documents already contain every change
  // older than this, so only download counts need replaying.
  private long rebuilt_at_ticks_;
  private bool snapshot_due_;
  private volatile string? rebuild_status_;

  private SyncOptions Sync => options.Value.Sync;

  /// <summary>Progress of a running (or the last) rebuild.</summary>
  public string? RebuildStatus => rebuild_status_;

  public async Task InitializeAsync(CancellationToken ct) {
    await gate_.WaitAsync(ct);
    try {
      logger.LogInformation("Storage: {Storage}", store.Description);
      await store.InitializeAsync(ct);
      await advisories.LoadAsync(ct);
      await LoadCoreAsync(ct);
      catalog.MarkReady();
      logger.LogInformation(
        "Catalog ready with {Count:N0} package(s)",
        catalog.Count
      );
    } finally {
      gate_.Release();
    }

    if (snapshot_due_) {
      await WriteSnapshotAsync(ct);
    }
  }

  /// <summary>Applies changes made by other instances.</summary>
  public async Task PollAsync(CancellationToken ct) {
    await gate_.WaitAsync(ct);
    try {
      await ApplyChangesAsync(ct);
    } finally {
      gate_.Release();
    }
  }

  /// <summary>
  ///   Publishes download counts recorded since the last flush.
  /// </summary>
  public async Task FlushDownloadsAsync(CancellationToken ct) {
    Dictionary<string, Dictionary<string, long>> pending =
      downloads.TakePending();
    if (pending.Count == 0) {
      return;
    }

    try {
      await feed.AppendAsync(
        ChangeKind.DOWNLOADS,
        Guid.NewGuid().ToString("N"),
        NoodleJson.SerializeGzip(
          pending,
          NoodleJson.S_CONTEXT.DictionaryStringDictionaryStringInt64
        ),
        ct
      );
    } catch {
      downloads.RestorePending(pending);
      throw;
    }
  }

  /// <summary>
  ///   Commits versions whose upload was acknowledged but never committed
  ///   (the instance stopped first). Any instance may do it; committing a
  ///   version twice changes nothing.
  /// </summary>
  public async Task<int> RecoverPendingAsync(CancellationToken ct) {
    TimeSpan age = TimeSpan.FromSeconds(
      Math.Max(1, options.Value.Ingest.RecoverAfterSeconds)
    );
    List<(string Name, string Version, string Key)> stale = [];
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_PENDING_PREFIX,
                     null,
                     ct
                   )) {
      if (DateTimeOffset.UtcNow - item.LastModified < age) {
        continue;
      }

      (string Name, string Version)? parsed = StorageKeys.SplitVersionKey(
        item.Key,
        StorageKeys.C_PENDING_PREFIX,
        ""
      );
      if (parsed != null &&
          !committer.IsQueued(parsed.Value.Name, parsed.Value.Version)) {
        stale.Add((parsed.Value.Name, parsed.Value.Version, item.Key));
      }
    }

    int recovered = 0;
    foreach ((string name, string version, string key) in stale) {
      StoredObject? stored = await store.GetAsync(
                               StorageKeys.Version(name, version),
                               ct
                             );
      VersionDocument? document = stored == null
                                    ? null
                                    : NoodleJson.DeserializeGzip(
                                      stored.Content,
                                      NoodleJson.S_CONTEXT.VersionDocument
                                    );
      if (document == null) {
        // Purged meanwhile.
        await store.DeleteAsync(key, ct);
        continue;
      }

      ChangeResult result =
        await committer.AddVersionAsync(document, false, ct);
      if (result.Status == ChangeStatus.CREATED) {
        recovered++;
      }

      await store.DeleteAsync(key, ct);
    }

    if (recovered > 0) {
      logger.LogInformation(
        "Committed {Count} acknowledged version(s) another instance " +
        "left behind",
        recovered
      );
    }

    return recovered;
  }

  /// <summary>
  ///   Writes a snapshot when one is due and no other instance wrote one
  ///   recently. Also notices when this instance fell so far behind that
  ///   the change log no longer covers the gap, and reloads.
  /// </summary>
  public async Task<bool> MaybeWriteSnapshotAsync(CancellationToken ct) {
    TimeSpan interval = TimeSpan.FromMinutes(Sync.SnapshotIntervalMinutes);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    if (!snapshot_due_ && now - last_snapshot_ < interval) {
      return false;
    }

    if (!snapshot_due_ &&
        catalog.Generation == snapshot_generation_ &&
        downloads.Changes == snapshot_downloads_) {
      // Nothing to add to the last snapshot this instance wrote.
      last_snapshot_ = now;
      return false;
    }

    ObjectInfo? current = await store.HeadAsync(StorageKeys.C_SNAPSHOT, ct);
    if (current != null && await ReloadIfBehindAsync(current, ct)) {
      return false;
    }

    if (!snapshot_due_ &&
        current != null &&
        now - current.LastModified < interval) {
      last_snapshot_ = current.LastModified;
      return false;
    }

    await WriteSnapshotAsync(ct);
    return true;
  }

  public async Task WriteSnapshotAsync(CancellationToken ct) {
    await FlushDownloadsAsync(ct);
    await gate_.WaitAsync(ct);
    string path = Path.Combine(
      Path.GetTempPath(),
      $"noodle-snapshot-{Guid.NewGuid():N}.bin.gz"
    );
    try {
      long generation = catalog.Generation;
      long download_changes = downloads.Changes;
      (long watermark, List<string> recent) = feed.GetCheckpoint();
      CatalogSnapshot snapshot = new() {
        Created = DateTimeOffset.UtcNow,
        WatermarkTicks = watermark,
        RecentChanges = recent,
        Packages = catalog.GetAll().ToList(),
        Downloads = downloads.Export().ToList()
      };
      await using (FileStream file = File.Create(path)) {
        snapshot.Write(file);
      }

      long size = new FileInfo(path).Length;
      await store.PutFileAsync(
        StorageKeys.C_SNAPSHOT,
        path,
        new PutOptions {
          ContentType = "application/gzip",
          Metadata = new Dictionary<string, string> {
            [C_WATERMARK_METADATA_] =
              watermark.ToString(CultureInfo.InvariantCulture)
          }
        },
        ct
      );
      last_snapshot_ = snapshot.Created;
      snapshot_generation_ = generation;
      snapshot_downloads_ = download_changes;
      snapshot_due_ = false;

      long cutoff = watermark -
                    TimeSpan.FromHours(Sync.ChangeRetentionHours).Ticks;
      int removed = await feed.DeleteOlderThanAsync(cutoff, ct);
      logger.LogInformation(
        "Wrote catalog snapshot: {Packages:N0} package(s), {Size:N0} bytes; " +
        "removed {Removed} old change(s)",
        snapshot.Packages.Count,
        size,
        removed
      );
    } finally {
      gate_.Release();
      File.Delete(path);
    }
  }

  /// <summary>
  ///   Rebuilds from the source of truth and writes a new snapshot
  ///   (recovery after a lost or damaged snapshot, documents or packuments).
  /// </summary>
  public async Task RebuildAsync(RebuildScope scope, CancellationToken ct) {
    rebuild_status_ = $"running ({scope.ToString().ToLowerInvariant()})";
    try {
      if (scope != RebuildScope.CATALOG) {
        await RepairDocumentsAsync(scope == RebuildScope.PACKUMENTS, ct);
      }

      await gate_.WaitAsync(ct);
      try {
        long started = DateTime.UtcNow.Ticks;
        // Download counts and the change log position stay as they are.
        await RebuildCoreAsync(false, ct);
        await ApplyChangesAsync(ct);
        await ReapplyPackageChangesAsync(started, ct);
      } finally {
        gate_.Release();
      }

      await WriteSnapshotAsync(ct);
      rebuild_status_ = $"done at {DateTimeOffset.UtcNow:u} " +
                        $"({catalog.Count:N0} packages)";
    } catch (Exception ex) {
      rebuild_status_ = $"failed: {ex.Message}";
      throw;
    }
  }

  /// <summary>
  ///   Commits every version document that its package document lacks (and
  ///   recreates missing package documents), optionally regenerating every
  ///   packument.
  /// </summary>
  private async Task RepairDocumentsAsync(bool regenerate,
                                          CancellationToken ct) {
    logger.LogInformation("Repairing package documents from version documents");
    int packages = 0;
    int repaired = 0;
    ConcurrentBag<Exception> failures = [];
    await Parallel.ForEachAsync(
      ListVersionGroupsAsync(ct),
      new ParallelOptions {
        MaxDegreeOfParallelism = Math.Max(1, Sync.RebuildParallelism / 4),
        CancellationToken = ct
      },
      async (group, token) => {
        try {
          StoredDocument?
            stored = await repository.LoadAsync(group.Name, token);
          List<string> missing = group.Versions
                                      .Where(
                                        v => stored?.Document.Find(v) == null
                                      )
                                      .ToList();
          foreach (string version in missing) {
            StoredObject? item = await store.GetAsync(
                                   StorageKeys.Version(group.Name, version),
                                   token
                                 );
            VersionDocument? document = item == null
                                          ? null
                                          : NoodleJson.DeserializeGzip(
                                            item.Content,
                                            NoodleJson.S_CONTEXT.VersionDocument
                                          );
            if (document != null) {
              await committer.AddVersionAsync(document, true, token);
            }
          }

          if (regenerate) {
            await committer.SubmitAsync(group.Name, new Regenerate(), token);
          }

          if (missing.Count > 0) {
            Interlocked.Increment(ref repaired);
          }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
          failures.Add(ex);
          logger.LogWarning(ex, "Could not repair {Package}", group.Name);
        }

        int done = Interlocked.Increment(ref packages);
        if (done % 10_000 == 0) {
          rebuild_status_ = $"running: {done:N0} packages checked";
          logger.LogInformation("Checked {Count:N0} packages", done);
        }
      }
    );
    await committer.DrainAsync(TimeSpan.FromMinutes(10));
    logger.LogInformation(
      "Checked {Packages:N0} packages, repaired {Repaired:N0}, {Failed} failed",
      packages,
      repaired,
      failures.Count
    );
  }

  /// <summary>
  ///   Version documents grouped by package (keys list sorted by name).
  /// </summary>
  private async IAsyncEnumerable<(string Name, List<string> Versions)>
    ListVersionGroupsAsync(
      [System.Runtime.CompilerServices.EnumeratorCancellation]
      CancellationToken ct) {
    string? name = null;
    List<string> versions = [];
    await foreach (ObjectInfo item in store.ListAsync(
                     StorageKeys.C_VERSIONS_PREFIX,
                     null,
                     ct
                   )) {
      (string Name, string Version)? parsed = StorageKeys.SplitVersionKey(
        item.Key,
        StorageKeys.C_VERSIONS_PREFIX,
        ".json.gz"
      );
      if (parsed == null) {
        continue;
      }

      if (parsed.Value.Name != name) {
        if (name != null) {
          yield return (name, versions);
        }

        name = parsed.Value.Name;
        versions = [];
      }

      versions.Add(parsed.Value.Version);
    }

    if (name != null) {
      yield return (name, versions);
    }
  }

  /// <summary>
  ///   Replaces the catalog with the snapshot (or a rebuild) and applies the
  ///   changes made since.
  /// </summary>
  private async Task LoadCoreAsync(CancellationToken ct) {
    long started = DateTime.UtcNow.Ticks;
    if (!await TryLoadSnapshotAsync(ct)) {
      await RebuildCoreAsync(true, ct);
    }

    await ApplyChangesAsync(ct);
    await ReapplyPackageChangesAsync(started, ct);
  }

  /// <param name="fresh">
  ///   There is no snapshot: start the change log from the beginning to
  ///   recover the download counts recorded there.
  /// </param>
  /// <param name="ct">Cancellation.</param>
  private async Task RebuildCoreAsync(bool fresh, CancellationToken ct) {
    logger.LogInformation("Rebuilding the catalog from package documents");
    DateTime started = DateTime.UtcNow;
    if (fresh) {
      rebuilt_at_ticks_ = started.Ticks - ChangeFeed.S_WINDOW.Ticks;
      feed.Reset(0, []);
      downloads.Load([]);
    }

    List<string> names = [];
    await foreach (string name in repository.ListNamesAsync(ct)) {
      names.Add(name);
    }

    ConcurrentBag<CatalogEntry> entries = [];
    int loaded = 0;
    await Parallel.ForEachAsync(
      names,
      new ParallelOptions {
        MaxDegreeOfParallelism = Math.Max(1, Sync.RebuildParallelism),
        CancellationToken = ct
      },
      async (name, token) => {
        PackageDocument? document = await ReadDocumentAsync(name, token);
        CatalogEntry? entry = document == null
                                ? null
                                : CatalogEntry.FromDocument(document);
        if (entry != null) {
          entries.Add(entry);
        }

        int count = Interlocked.Increment(ref loaded);
        if (count % 10_000 == 0) {
          rebuild_status_ =
            $"running: {count:N0} of {names.Count:N0} packages loaded";
          logger.LogInformation(
            "Loaded {Count:N0} of {Total:N0} packages",
            count,
            names.Count
          );
        }
      }
    );
    catalog.Load(entries);
    snapshot_due_ = true;
    logger.LogInformation(
      "Rebuilt the catalog with {Count:N0} package(s) in {Elapsed:0.0} s",
      entries.Count,
      (DateTime.UtcNow - started).TotalSeconds
    );
  }

  /// <summary>
  ///   Reads a package document without filling the document cache.
  /// </summary>
  private async Task<PackageDocument?> ReadDocumentAsync(string name,
    CancellationToken ct) {
    StoredObject? stored = await store.GetAsync(StorageKeys.Package(name), ct);
    if (stored == null) {
      return null;
    }

    try {
      return NoodleJson.DeserializeGzip(
        stored.Content,
        NoodleJson.S_CONTEXT.PackageDocument
      );
    } catch (Exception ex) when (ex is JsonException or InvalidDataException) {
      logger.LogWarning(
        ex,
        "Ignoring unreadable package document of {Name}",
        name
      );
      return null;
    }
  }

  private async Task<bool> TryLoadSnapshotAsync(CancellationToken ct) {
    string path = Path.Combine(
      Path.GetTempPath(),
      $"noodle-snapshot-{Guid.NewGuid():N}.bin.gz"
    );
    try {
      DateTimeOffset modified;
      await using (ObjectReadStream? stream = await store.OpenReadAsync(
                                                StorageKeys.C_SNAPSHOT,
                                                ct
                                              )) {
        if (stream == null) {
          return false;
        }

        modified = stream.Info.LastModified;
        await using FileStream file = File.Create(path);
        await stream.Stream.CopyToAsync(file, ct);
      }

      DateTime started = DateTime.UtcNow;
      CatalogSnapshot snapshot;
      try {
        await using FileStream file = File.OpenRead(path);
        snapshot = CatalogSnapshot.Read(file);
      } catch (InvalidDataException ex) {
        logger.LogWarning(ex, "The catalog snapshot is unreadable; rebuilding");
        return false;
      }

      catalog.Load(snapshot.Packages);
      downloads.Load(snapshot.Downloads);
      feed.Reset(snapshot.WatermarkTicks, snapshot.RecentChanges);
      rebuilt_at_ticks_ = 0;
      last_snapshot_ = modified;
      logger.LogInformation(
        "Loaded catalog snapshot from {Created:u} with {Count:N0} package(s) " +
        "in {Elapsed:0.0} s",
        snapshot.Created,
        snapshot.Packages.Count,
        (DateTime.UtcNow - started).TotalSeconds
      );
      return true;
    } finally {
      File.Delete(path);
    }
  }

  private async Task<bool> ReloadIfBehindAsync(ObjectInfo snapshot,
                                               CancellationToken ct) {
    if (!snapshot.Metadata.TryGetValue(
          C_WATERMARK_METADATA_,
          out string? value
        ) ||
        !long.TryParse(
          value,
          NumberStyles.None,
          CultureInfo.InvariantCulture,
          out long watermark
        )) {
      return false;
    }

    long retention = TimeSpan.FromHours(Sync.ChangeRetentionHours).Ticks;
    if (feed.CursorTicks >= watermark - retention / 2) {
      return false;
    }

    logger.LogWarning(
      "This instance is too far behind the change log; reloading the snapshot"
    );
    await gate_.WaitAsync(ct);
    try {
      await LoadCoreAsync(ct);
    } finally {
      gate_.Release();
    }

    return true;
  }

  /// <summary>
  ///   Loading replaces the whole catalog, which can undo packages this
  ///   instance changed while it was loading (their changes are already
  ///   marked as seen). Re-reading every package changed since the load
  ///   started puts them back; applying a change twice is harmless.
  /// </summary>
  private async Task ReapplyPackageChangesAsync(long since_ticks,
                                                CancellationToken ct) {
    IReadOnlyList<ChangeEntry> changes = await feed.ListSinceAsync(
                                           since_ticks -
                                           ChangeFeed.S_WINDOW.Ticks,
                                           ct
                                         );
    await RefreshPackagesAsync(
      changes.Where(c => c.Kind == ChangeKind.PACKAGE)
             .Select(c => c.Subject)
             .ToHashSet(StringComparer.Ordinal),
      ct
    );
  }

  private async Task RefreshPackagesAsync(IReadOnlyCollection<string> names,
                                          CancellationToken ct) {
    await Parallel.ForEachAsync(
      names,
      new ParallelOptions {
        MaxDegreeOfParallelism = C_POLL_PARALLELISM_,
        CancellationToken = ct
      },
      async (name, token) => {
        StoredDocument? stored = await repository.LoadAsync(name, token);
        catalog.Apply(name, stored?.Document);
      }
    );
  }

  private async Task ApplyChangesAsync(CancellationToken ct) {
    IReadOnlyList<ChangeEntry> changes = await feed.ReadNewAsync(ct);
    if (changes.Count == 0) {
      return;
    }

    HashSet<string> packages = new(StringComparer.Ordinal);
    bool advisories_changed = false;
    foreach (ChangeEntry change in changes) {
      switch (change.Kind) {
        case ChangeKind.PACKAGE:
          if (change.Ticks >= rebuilt_at_ticks_) {
            packages.Add(change.Subject);
          }

          break;
        case ChangeKind.DOWNLOADS:
          await ApplyDownloadsAsync(change, ct);
          break;
        case ChangeKind.ADVISORIES:
          advisories_changed = true;
          break;
      }
    }

    if (advisories_changed) {
      await advisories.LoadAsync(ct);
    }

    await RefreshPackagesAsync(packages, ct);
    if (packages.Count > 0) {
      logger.LogDebug("Applied {Count} package change(s)", packages.Count);
    }
  }

  private async Task ApplyDownloadsAsync(ChangeEntry change,
                                         CancellationToken ct) {
    StoredObject? stored = await store.GetAsync(change.Key, ct);
    if (stored == null) {
      return;
    }

    try {
      Dictionary<string, Dictionary<string, long>>? delta =
        NoodleJson.DeserializeGzip(
          stored.Content,
          NoodleJson.S_CONTEXT.DictionaryStringDictionaryStringInt64
        );
      if (delta != null) {
        downloads.Apply(delta);
      }
    } catch (Exception ex) when (ex is JsonException or InvalidDataException) {
      logger.LogWarning(
        ex,
        "Ignoring unreadable download counts {Key}",
        change.Key
      );
    }
  }
}
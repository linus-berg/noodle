using Microsoft.Extensions.Options;
using Noodle.Api.Catalog;
using Noodle.Api.Configuration;
using Noodle.Api.Json;
using Noodle.Api.Npm;
using Noodle.Api.Storage;

namespace Noodle.Api.Packages;

public sealed class IngestResult {
  public required ChangeStatus Status { get; init; }
  public required string Name { get; init; }
  public required string Version { get; init; }
  public required string Integrity { get; init; }
}

/// <summary>
///   Turns an uploaded tarball into a package version: checks it, derives
///   the manifest, stores the tarball (first one wins) and the version
///   document, and commits. Safe to repeat and to run in parallel, also
///   across instances.
/// </summary>
public sealed class PackageIngestor(
  IObjectStore store,
  PackageCommitter committer,
  PackageCatalog catalog,
  IOptions<NoodleOptions> options,
  ILogger<PackageIngestor> logger
) {
  // A tarball without a version document that is older than this was left
  // behind by an upload that failed halfway; it may be replaced.
  private static readonly TimeSpan S_ORPHAN_AGE_ = TimeSpan.FromMinutes(10);

  private const string C_SHA512_METADATA_ = "sha512";

  /// <exception cref="InvalidTarballException">
  ///   Damaged or not a tarball.
  /// </exception>
  /// <exception cref="InvalidManifestException">
  ///   Unusable package.json.
  /// </exception>
  public async Task<IngestResult> IngestAsync(string path, long size,
                                              string sha512_base64,
                                              string sha1_hex, bool wait,
                                              CancellationToken ct) {
    TarballContents contents = TarballInspector.Inspect(
      path,
      options.Value.MaxUnpackedBytes
    );
    NormalizedManifest manifest = ManifestNormalizer.Normalize(contents);
    string name = manifest.Name;
    string version = manifest.Version;
    string integrity = "sha512-" + sha512_base64;

    TarballState state = await StoreTarballAsync(
                           name,
                           version,
                           path,
                           sha512_base64,
                           ct
                         );
    if (state.Conflict) {
      logger.LogWarning(
        "Rejected {Name}@{Version}: a different tarball is stored already",
        name,
        version
      );
      return Result(
        ChangeStatus.CONFLICT,
        name,
        version,
        state.Integrity ?? ""
      );
    }

    if (state.Existed && catalog.IsReady && catalog.HasVersion(name, version)) {
      // Duplicates are normal: the stored tarball of this committed version
      // has the same bytes, so nothing else needs to be read or written.
      return Result(ChangeStatus.EXISTS, name, version, integrity);
    }

    VersionDocument document = await StoreVersionDocumentAsync(
                                 new VersionDocument {
                                   Name = name,
                                   Record = new VersionRecord {
                                     Version = version,
                                     Arrived = DateTimeOffset.UtcNow,
                                     Integrity = integrity,
                                     Shasum = sha1_hex,
                                     Size = size,
                                     FileCount = contents.FileCount,
                                     UnpackedSize = contents.UnpackedSize,
                                     Manifest = new RawJson(
                                       JsJson.Serialize(manifest.Manifest)
                                     )
                                   },
                                   Readme = manifest.Readme,
                                   ReadmeFilename = manifest.ReadmeFilename
                                 },
                                 ct
                               );
    ChangeResult result = await committer.AddVersionAsync(document, !wait, ct);
    if (result.Status is ChangeStatus.CREATED or ChangeStatus.ACCEPTED) {
      logger.LogInformation(
        "Added {Name}@{Version} ({Size:N0} bytes){Deferred}",
        name,
        version,
        size,
        result.Status == ChangeStatus.ACCEPTED ? " (batched)" : ""
      );
    }

    return Result(
      result.Status,
      name,
      version,
      result.Version?.Integrity ?? integrity
    );
  }

  /// <summary>
  ///   Stores the tarball unless one is there already. The tarball is the
  ///   arbiter between uploads with different bytes: the first one stored
  ///   wins, even when the version is not committed yet.
  /// </summary>
  private async Task<TarballState> StoreTarballAsync(string name,
    string version,
    string path,
    string sha512,
    CancellationToken ct) {
    string key = StorageKeys.Tarball(name, version);
    ObjectInfo? existing = await store.HeadAsync(key, ct);
    if (existing != null) {
      TarballState? known = await CheckExistingAsync(
                              name,
                              version,
                              existing,
                              sha512,
                              ct
                            );
      if (known != null) {
        return known;
      }
    }

    try {
      await store.PutFileAsync(
        key,
        path,
        new PutOptions {
          ContentType = "application/octet-stream",
          IfNoneMatch = existing == null,
          IfMatch = existing?.ETag,
          Metadata = new Dictionary<string, string> {
            [C_SHA512_METADATA_] = sha512
          }
        },
        ct
      );
      return new TarballState();
    } catch (PreconditionFailedException) {
      // Another upload of this version won the race; same bytes or not?
      ObjectInfo? winner = await store.HeadAsync(key, ct);
      string? stored = winner?.Metadata.GetValueOrDefault(C_SHA512_METADATA_);
      return stored == sha512
               ? new TarballState {
                 Existed = true
               }
               : new TarballState {
                 Conflict = true,
                 Integrity = stored == null ? null : "sha512-" + stored
               };
    }
  }

  /// <returns>
  ///   <c>null</c> when the stored tarball is an orphan to replace.
  /// </returns>
  private async Task<TarballState?> CheckExistingAsync(string name,
    string version, ObjectInfo existing, string sha512, CancellationToken ct) {
    string? stored = existing.Metadata.GetValueOrDefault(C_SHA512_METADATA_);
    if (stored == sha512) {
      return new TarballState {
        Existed = true
      };
    }

    TarballState conflict = new() {
      Conflict = true,
      Integrity = stored == null ? null : "sha512-" + stored
    };
    if (catalog.HasVersion(name, version) ||
        DateTimeOffset.UtcNow - existing.LastModified < S_ORPHAN_AGE_) {
      return conflict;
    }

    ObjectInfo? document = await store.HeadAsync(
                             StorageKeys.Version(name, version),
                             ct
                           );
    if (document != null) {
      return conflict;
    }

    logger.LogWarning(
      "Replacing the tarball of {Name}@{Version} left by an incomplete upload",
      name,
      version
    );
    return null;
  }

  /// <summary>
  ///   Writes the version document, or returns the stored one (which keeps
  ///   the original arrival time) when this tarball was uploaded before.
  ///   New versions cost one conditional write; only repeats read.
  /// </summary>
  private async Task<VersionDocument> StoreVersionDocumentAsync(
    VersionDocument document, CancellationToken ct) {
    string key = StorageKeys.Version(document.Name, document.Record.Version);
    byte[] content = NoodleJson.SerializeGzip(
      document,
      NoodleJson.S_CONTEXT.VersionDocument
    );
    try {
      await store.PutAsync(
        key,
        content,
        new PutOptions {
          ContentType = "application/gzip",
          IfNoneMatch = true
        },
        ct
      );
      return document;
    } catch (PreconditionFailedException) {
      StoredObject? existing = await store.GetAsync(key, ct);
      VersionDocument? stored = existing == null ? null : TryRead(existing);
      if (stored != null &&
          stored.Record.Integrity == document.Record.Integrity) {
        return stored;
      }

      // Left by an incomplete upload of other bytes, whose tarball was
      // replaced above.
      await store.PutAsync(
        key,
        content,
        new PutOptions {
          ContentType = "application/gzip",
          IfMatch = existing?.Info.ETag,
          IfNoneMatch = existing == null
        },
        ct
      );
      return document;
    }
  }

  private VersionDocument? TryRead(StoredObject stored) {
    try {
      return NoodleJson.DeserializeGzip(
        stored.Content,
        NoodleJson.S_CONTEXT.VersionDocument
      );
    } catch (Exception ex) when (ex is System.Text.Json.JsonException or
                                   InvalidDataException) {
      logger.LogWarning(ex, "Ignoring unreadable {Key}", stored.Info.Key);
      return null;
    }
  }

  private static IngestResult Result(ChangeStatus status, string name,
                                     string version, string integrity) {
    return new IngestResult {
      Status = status,
      Name = name,
      Version = version,
      Integrity = integrity
    };
  }

  private sealed class TarballState {
    /// <summary>The identical tarball was stored before.</summary>
    public bool Existed { get; init; }

    public bool Conflict { get; init; }
    public string? Integrity { get; init; }
  }
}
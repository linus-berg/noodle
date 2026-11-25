using System.Collections.Concurrent;
using Noodle.Api.Catalog;
using Noodle.Api.Json;
using Noodle.Api.Storage;
using Semver;

namespace Noodle.Api.Advisories;

/// <summary>
///   Imported security advisories, matched against package versions for
///   <c>npm audit</c> and search's <c>insecure</c> flag. Each import replaces
///   the whole data set.
/// </summary>
public sealed class AdvisoryStore(
  IObjectStore store,
  ILogger<AdvisoryStore> logger
) : IVulnerabilityLookup {
  private volatile State state_ = State.S_EMPTY;

  public bool HasData => state_.Data != null;

  public AdvisoryData? Data => state_.Data;

  public int PackageCount => state_.ByPackage.Count;

  public async Task LoadAsync(CancellationToken ct) {
    StoredObject? stored = await store.GetAsync(StorageKeys.C_ADVISORIES, ct);
    if (stored == null) {
      state_ = State.S_EMPTY;
      return;
    }

    AdvisoryData? data;
    try {
      data = NoodleJson.DeserializeGzip(
        stored.Content,
        NoodleJson.S_CONTEXT.AdvisoryData
      );
    } catch (Exception ex) when (ex is System.Text.Json.JsonException or
                                   InvalidDataException) {
      logger.LogWarning(ex, "Ignoring unreadable advisory data");
      return;
    }

    if (data != null) {
      state_ = Build(data);
      logger.LogInformation(
        "Loaded {Count:N0} advisories for {Packages:N0} packages " +
        "(imported {When:u})",
        data.Advisories.Count,
        state_.ByPackage.Count,
        data.ImportedAt
      );
    }
  }

  /// <summary>
  ///   Replaces the data set with the advisories in an OSV file.
  /// </summary>
  /// <exception cref="InvalidAdvisoryDataException">
  ///   The file is not OSV data.
  /// </exception>
  public async Task<AdvisoryData> ImportAsync(string path, string? source,
                                              CancellationToken ct) {
    List<AdvisoryRecord> records = OsvImporter.Read(path);
    AdvisoryData data = new() {
      ImportedAt = DateTimeOffset.UtcNow,
      Source = source,
      Advisories = records
    };
    await store.PutAsync(
      StorageKeys.C_ADVISORIES,
      NoodleJson.SerializeGzip(data, NoodleJson.S_CONTEXT.AdvisoryData),
      new PutOptions {
        ContentType = "application/gzip"
      },
      ct
    );
    state_ = Build(data);
    logger.LogInformation(
      "Imported {Count:N0} advisories for {Packages:N0} packages",
      records.Count,
      state_.ByPackage.Count
    );
    return data;
  }

  public async Task<bool> DeleteAsync(CancellationToken ct) {
    if (!HasData) {
      return false;
    }

    await store.DeleteAsync(StorageKeys.C_ADVISORIES, ct);
    state_ = State.S_EMPTY;
    return true;
  }

  /// <summary>Advisories of a package affecting any of the versions.</summary>
  public List<AdvisoryRecord> Find(string name, IEnumerable<string> versions) {
    if (!state_.ByPackage.TryGetValue(name, out List<Entry>? entries)) {
      return [];
    }

    List<SemVersion> parsed = versions.Select(Parse)
                                      .Where(v => v != null)
                                      .Select(v => v!)
                                      .ToList();
    return entries.Where(e => parsed.Any(v => e.Range.Contains(v)))
                  .Select(e => e.Record)
                  .ToList();
  }

  public bool IsVulnerable(string name, string version) {
    State state = state_;
    if (!state.ByPackage.ContainsKey(name)) {
      return false;
    }

    return state.Cache.GetOrAdd(
      (name, version),
      key => Find(key.Item1, [key.Item2]).Count > 0
    );
  }

  private static SemVersion? Parse(string version) {
    return SemVersion.TryParse(
             version,
             SemVersionStyles.Any,
             out SemVersion? parsed
           )
             ? parsed
             : null;
  }

  private static State Build(AdvisoryData data) {
    Dictionary<string, List<Entry>> by_package = new(StringComparer.Ordinal);
    foreach (AdvisoryRecord record in data.Advisories) {
      // Like npm's audit: prereleases inside a range are affected too.
      if (!SemVersionRange.TryParseNpm(
            record.VulnerableVersions,
            true,
            out SemVersionRange? range
          )) {
        continue;
      }

      if (!by_package.TryGetValue(record.Package, out List<Entry>? list)) {
        list = [];
        by_package[record.Package] = list;
      }

      list.Add(
        new Entry {
          Record = record,
          Range = range
        }
      );
    }

    return new State {
      Data = data,
      ByPackage = by_package
    };
  }

  private sealed class Entry {
    public required AdvisoryRecord Record { get; init; }
    public required SemVersionRange Range { get; init; }
  }

  private sealed class State {
    public static readonly State S_EMPTY = new() {
      Data = null,
      ByPackage = new Dictionary<string, List<Entry>>()
    };

    public required AdvisoryData? Data { get; init; }
    public required Dictionary<string, List<Entry>> ByPackage { get; init; }

    public ConcurrentDictionary<(string, string), bool> Cache { get; } = new();
  }
}
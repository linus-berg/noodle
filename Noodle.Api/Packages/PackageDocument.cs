using System.Text.Json.Serialization;
using Noodle.Api.Npm;

namespace Noodle.Api.Packages;

/// <summary>A generated packument object in S3.</summary>
public sealed class PackumentRef {
  /// <summary>
  ///   Content hash (hex); also the object name and the HTTP ETag.
  /// </summary>
  public required string Hash { get; init; }

  /// <summary>Size of the stored (gzipped) object.</summary>
  public long Size { get; init; }
}

/// <summary>
///   <c>packages/{name}.json.gz</c>: every version of one package, the
///   administrator's dist-tags and the packuments generated from them. This
///   is the commit point: a version exists once it is listed here.
/// </summary>
public sealed class PackageDocument {
  public required string Name { get; init; }

  /// <summary>
  ///   Incremented by every write. Each write is conditional on the previous
  ///   document, so a higher revision is always newer.
  /// </summary>
  public long Revision { get; init; }

  public string CommitId { get; init; } = Guid.NewGuid().ToString("N");

  /// <summary>
  ///   Last change of the package (npm's <c>time.modified</c>).
  /// </summary>
  public DateTimeOffset Modified { get; init; }

  /// <summary>All versions, ordered by npm precedence (oldest first).</summary>
  public required List<VersionRecord> Versions { get; init; }

  /// <summary>
  ///   dist-tags set by an administrator. They stay until removed; a
  ///   <c>latest</c> here overrides the automatic one.
  /// </summary>
  public Dictionary<string, string> PinnedTags { get; init; } =
    new(StringComparer.Ordinal);

  /// <summary>
  ///   Readme of <see cref="ReadmeVersion" /> (the latest version).
  /// </summary>
  public string? Readme { get; init; }

  public string? ReadmeFilename { get; init; }
  public string? ReadmeVersion { get; init; }

  /// <summary>
  ///   The abbreviated packument (<c>application/vnd.npm.install-v1+json</c>).
  /// </summary>
  public PackumentRef? Corgi { get; init; }

  /// <summary>The full packument (<c>application/json</c>).</summary>
  public PackumentRef? Full { get; init; }

  /// <summary>Base URL the stored packuments' tarball URLs use.</summary>
  public string? BaseUrl { get; init; }

  /// <summary>
  ///   Packuments of the previous revision, deleted after the next commit.
  /// </summary>
  public List<string> PreviousPackuments { get; init; } = [];

  public VersionRecord? Find(string version) {
    foreach (VersionRecord record in Versions) {
      if (record.Version == version) {
        return record;
      }
    }

    return null;
  }

  /// <summary>
  ///   The automatic <c>latest</c>: the highest version without prerelease,
  ///   or the highest prerelease when there is no other. Unlike
  ///   <c>npm publish</c>, independent of the order versions arrived in.
  /// </summary>
  [JsonIgnore]
  public string? AutomaticLatest {
    get {
      for (int i = Versions.Count - 1; i >= 0; i--) {
        if (!Versions[i].IsPrerelease) {
          return Versions[i].Version;
        }
      }

      return Versions.Count == 0 ? null : Versions[^1].Version;
    }
  }

  [JsonIgnore]
  public string? Latest =>
    PinnedTags.TryGetValue("latest", out string? pinned) &&
    Find(pinned) != null
      ? pinned
      : AutomaticLatest;

  /// <summary>
  ///   The dist-tags clients see: <c>latest</c> plus the pinned ones.
  /// </summary>
  public SortedDictionary<string, string> GetDistTags() {
    SortedDictionary<string, string> tags = new(StringComparer.Ordinal);
    foreach (KeyValuePair<string, string> tag in PinnedTags) {
      if (Find(tag.Value) != null) {
        tags[tag.Key] = tag.Value;
      }
    }

    if (Latest is { } latest) {
      tags["latest"] = latest;
    }

    return tags;
  }

  [JsonIgnore]
  public DateTimeOffset Created =>
    Versions.Count == 0 ? Modified : Versions.Min(v => v.Arrived);

  /// <summary>Sorts versions by precedence (in place).</summary>
  public static void SortVersions(List<VersionRecord> versions) {
    versions.Sort((a, b) => NpmVersion.Compare(a.Version, b.Version));
  }
}
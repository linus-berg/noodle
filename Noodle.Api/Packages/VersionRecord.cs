using Noodle.Api.Json;

namespace Noodle.Api.Packages;

/// <summary>
///   One version of a package: its normalized manifest and what was
///   computed from the tarball. Order independent: nothing here depends on
///   which other versions exist, except <see cref="Arrived" />.
/// </summary>
public sealed class VersionRecord {
  public required string Version { get; init; }

  /// <summary>
  ///   When the tarball arrived (npm's publish time, which is unknown).
  /// </summary>
  public required DateTimeOffset Arrived { get; init; }

  /// <summary>
  ///   <c>sha512-...</c> of the tarball (<c>dist.integrity</c>).
  /// </summary>
  public required string Integrity { get; init; }

  /// <summary>SHA-1 hex of the tarball (<c>dist.shasum</c>).</summary>
  public required string Shasum { get; init; }

  public long Size { get; init; }
  public int FileCount { get; init; }
  public long UnpackedSize { get; init; }

  /// <summary>The manifest without <c>dist</c> and <c>readme</c>.</summary>
  public required RawJson Manifest { get; init; }

  public bool IsPrerelease => Version.Contains('-');
}

/// <summary>
///   <c>versions/{name}/{version}.json.gz</c>: everything about one version,
///   written before the package document. Together with the tarballs these
///   are the source of truth the package documents can be rebuilt from.
/// </summary>
public sealed class VersionDocument {
  public required string Name { get; init; }
  public required VersionRecord Record { get; init; }
  public string? Readme { get; init; }
  public string? ReadmeFilename { get; init; }
}
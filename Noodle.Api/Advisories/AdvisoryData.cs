namespace Noodle.Api.Advisories;

/// <summary>
///   One advisory for one package, in the shape npm's audit endpoints use.
/// </summary>
public sealed class AdvisoryRecord {
  /// <summary>Stable number derived from the GHSA identifier.</summary>
  public required long Id { get; init; }

  public required string GhsaId { get; init; }
  public required string Package { get; init; }
  public required string Title { get; init; }

  /// <summary>
  ///   <c>low</c>, <c>moderate</c>, <c>high</c> or <c>critical</c>.
  /// </summary>
  public required string Severity { get; init; }

  /// <summary>
  ///   An npm semver range, e.g. <c>&gt;=3.7.0 &lt;4.17.19</c>.
  /// </summary>
  public required string VulnerableVersions { get; init; }

  public string? PatchedVersions { get; init; }
  public List<string> Cwe { get; init; } = [];
  public List<string> Cves { get; init; } = [];
  public double CvssScore { get; init; }
  public string? CvssVector { get; init; }
  public string? Overview { get; init; }
  public string? References { get; init; }
  public DateTimeOffset? Created { get; init; }
  public DateTimeOffset? Updated { get; init; }

  public string Url => $"https://github.com/advisories/{GhsaId}";
}

/// <summary>
///   The imported data set (<c>advisories/advisories.json.gz</c>).
/// </summary>
public sealed class AdvisoryData {
  public required DateTimeOffset ImportedAt { get; init; }

  /// <summary>
  ///   Where the data came from (file name or description given on import).
  /// </summary>
  public string? Source { get; init; }

  public required List<AdvisoryRecord> Advisories { get; init; }
}
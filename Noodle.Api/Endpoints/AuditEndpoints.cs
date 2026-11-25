using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using Noodle.Api.Advisories;
using Noodle.Api.Packages;

namespace Noodle.Api.Endpoints;

/// <summary>
///   npm's audit endpoints: the bulk advisory endpoint of npm 7+, Yarn berry
///   and bun, and the older "quick audit" of npm 6, Yarn classic and pnpm.
///   Until advisory data is imported they fail on purpose, so that clients
///   report the audit as unavailable instead of "0 vulnerabilities".
/// </summary>
/// <remarks>
///   The failure is a 424 (Failed Dependency): every client shows it with
///   the message. A 5xx would be retried by pnpm for over a minute, and a
///   404 makes pnpm claim the endpoint does not exist.
/// </remarks>
public static class AuditEndpoints {
  public const string C_NO_DATA =
    "No security advisory data has been imported into this registry yet, " +
    "so packages cannot be audited.";

  private const int C_MAX_DEPTH_ = 1024;

  private static readonly JsonWriterOptions S_WRITER_OPTIONS_ = new() {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  private static readonly string[] S_SEVERITIES_ = [
    "info",
    "low",
    "moderate",
    "high",
    "critical"
  ];

  public static void MapAuditEndpoints(this IEndpointRouteBuilder app) {
    app.MapPost("/-/npm/v1/security/advisories/bulk", BulkAsync);
    app.MapPost("/-/npm/v1/security/audits/quick", QuickAsync);
    app.MapPost("/-/npm/v1/security/audits", QuickAsync);
  }

  private static async Task<IResult> BulkAsync(HttpContext context,
                                               AdvisoryStore advisories,
                                               CancellationToken ct) {
    if (!advisories.HasData) {
      return HttpHelpers.Error(
        StatusCodes.Status424FailedDependency,
        C_NO_DATA
      );
    }

    using JsonDocument? body = await ReadBodyAsync(context, ct);
    if (body == null || body.RootElement.ValueKind != JsonValueKind.Object) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        "Expected an object of package names and version lists."
      );
    }

    ArrayBufferWriter<byte> buffer = new(4096);
    await using (Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_)) {
      writer.WriteStartObject();
      foreach (JsonProperty package in body.RootElement.EnumerateObject()) {
        List<string> versions = package.Value.ValueKind == JsonValueKind.Array
                                  ? package.Value.EnumerateArray()
                                           .Where(
                                             v => v.ValueKind ==
                                                  JsonValueKind.String
                                           )
                                           .Select(v => v.GetString()!)
                                           .ToList()
                                  : [];
        List<AdvisoryRecord> found = advisories.Find(package.Name, versions);
        if (found.Count == 0) {
          continue;
        }

        writer.WriteStartArray(package.Name);
        foreach (AdvisoryRecord advisory in found) {
          writer.WriteStartObject();
          writer.WriteNumber("id", advisory.Id);
          writer.WriteString("url", advisory.Url);
          writer.WriteString("title", advisory.Title);
          writer.WriteString("severity", advisory.Severity);
          writer.WriteString(
            "vulnerable_versions",
            advisory.VulnerableVersions
          );
          WriteStrings(writer, "cwe", advisory.Cwe);
          WriteCvss(writer, advisory);
          writer.WriteEndObject();
        }

        writer.WriteEndArray();
      }

      writer.WriteEndObject();
    }

    return Results.Bytes(buffer.WrittenSpan.ToArray(), "application/json");
  }

  /// <summary>
  ///   The quick audit: the body is the dependency tree (npm 6 lockfile
  ///   shape), the answer lists advisories with the paths they were found at.
  /// </summary>
  private static async Task<IResult> QuickAsync(HttpContext context,
                                                AdvisoryStore advisories,
                                                CancellationToken ct) {
    if (!advisories.HasData) {
      return HttpHelpers.Error(
        StatusCodes.Status424FailedDependency,
        C_NO_DATA
      );
    }

    using JsonDocument? body = await ReadBodyAsync(context, ct);
    if (body == null || body.RootElement.ValueKind != JsonValueKind.Object) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        "Expected a dependency tree."
      );
    }

    List<Finding> findings = [];
    Walk(body.RootElement, "", findings, 0);
    Dictionary<string, List<Finding>> by_name = findings
                                                .GroupBy(f => f.Name)
                                                .ToDictionary(
                                                  g => g.Key,
                                                  g => g.ToList(),
                                                  StringComparer.Ordinal
                                                );
    Dictionary<string, long> counts =
      S_SEVERITIES_.ToDictionary(s => s, _ => 0L);
    ArrayBufferWriter<byte> buffer = new(4096);
    await using (Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_)) {
      writer.WriteStartObject();
      writer.WriteStartArray("actions");
      writer.WriteEndArray();
      writer.WriteStartObject("advisories");
      foreach ((string name, List<Finding> found) in by_name) {
        foreach (AdvisoryRecord advisory in advisories.Find(
                   name,
                   found.Select(f => f.Version).Distinct()
                 )) {
          List<Finding> affected = found.Where(
                                          f => advisories.Find(
                                              name,
                                              [f.Version]
                                            )
                                            .Any(a => a.Id == advisory.Id)
                                        )
                                        .ToList();
          counts[advisory.Severity] =
            counts.GetValueOrDefault(advisory.Severity) +
            affected.Count;
          WriteQuickAdvisory(writer, advisory, affected);
        }
      }

      writer.WriteEndObject();
      writer.WriteStartArray("muted");
      writer.WriteEndArray();
      writer.WriteStartObject("metadata");
      writer.WriteStartObject("vulnerabilities");
      foreach (string severity in S_SEVERITIES_) {
        writer.WriteNumber(severity, counts[severity]);
      }

      writer.WriteEndObject();
      int dev = findings.Count(f => f.Dev);
      writer.WriteNumber("dependencies", findings.Count - dev);
      writer.WriteNumber("devDependencies", dev);
      writer.WriteNumber(
        "optionalDependencies",
        findings.Count(f => f.Optional)
      );
      writer.WriteNumber("totalDependencies", findings.Count);
      writer.WriteEndObject();
      writer.WriteEndObject();
    }

    return Results.Bytes(buffer.WrittenSpan.ToArray(), "application/json");
  }

  private static void WriteQuickAdvisory(Utf8JsonWriter writer,
                                         AdvisoryRecord advisory,
                                         List<Finding> affected) {
    writer.WriteStartObject(advisory.Id.ToString());
    writer.WriteStartArray("findings");
    foreach (IGrouping<string, Finding> version in affected.GroupBy(
               f => f.Version
             )) {
      writer.WriteStartObject();
      writer.WriteString("version", version.Key);
      writer.WriteStartArray("paths");
      foreach (Finding finding in version) {
        writer.WriteStringValue(finding.Path);
      }

      writer.WriteEndArray();
      writer.WriteBoolean("dev", version.All(f => f.Dev));
      writer.WriteBoolean("optional", version.All(f => f.Optional));
      writer.WriteBoolean("bundled", false);
      writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteNumber("id", advisory.Id);
    writer.WriteString("title", advisory.Title);
    writer.WriteString("module_name", advisory.Package);
    writer.WriteString("severity", advisory.Severity);
    writer.WriteString("url", advisory.Url);
    writer.WriteString("github_advisory_id", advisory.GhsaId);
    writer.WriteString("vulnerable_versions", advisory.VulnerableVersions);
    writer.WriteString(
      "patched_versions",
      advisory.PatchedVersions ?? "<0.0.0"
    );
    writer.WriteString("overview", advisory.Overview ?? advisory.Title);
    writer.WriteString(
      "recommendation",
      advisory.PatchedVersions is { } patched && patched != "<0.0.0"
        ? $"Upgrade to version {patched.TrimStart('>', '=')} or later"
        : "None"
    );
    writer.WriteString("references", advisory.References ?? "");
    WriteStrings(writer, "cwe", advisory.Cwe);
    WriteStrings(writer, "cves", advisory.Cves);
    WriteCvss(writer, advisory);
    writer.WriteString(
      "created",
      advisory.Created == null
        ? null
        : PackumentWriter.FormatTime(advisory.Created.Value)
    );
    writer.WriteString(
      "updated",
      advisory.Updated == null
        ? null
        : PackumentWriter.FormatTime(advisory.Updated.Value)
    );
    writer.WriteNull("deleted");
    writer.WriteString("access", "public");
    writer.WriteNull("found_by");
    writer.WriteNull("reported_by");
    writer.WriteNull("metadata");
    writer.WriteNull("npm_advisory_id");
    writer.WriteEndObject();
  }

  private static void
    WriteCvss(Utf8JsonWriter writer, AdvisoryRecord advisory) {
    writer.WriteStartObject("cvss");
    writer.WriteNumber("score", advisory.CvssScore);
    writer.WriteString("vectorString", advisory.CvssVector);
    writer.WriteEndObject();
  }

  private static void WriteStrings(Utf8JsonWriter writer, string name,
                                   IEnumerable<string> values) {
    writer.WriteStartArray(name);
    foreach (string value in values) {
      writer.WriteStringValue(value);
    }

    writer.WriteEndArray();
  }

  /// <summary>
  ///   Collects every package of an npm 6 style tree with its path.
  /// </summary>
  private static void Walk(JsonElement node, string path,
                           List<Finding> findings,
                           int depth) {
    if (depth > 200 ||
        !node.TryGetProperty("dependencies", out JsonElement dependencies) ||
        dependencies.ValueKind != JsonValueKind.Object) {
      return;
    }

    foreach (JsonProperty dependency in dependencies.EnumerateObject()) {
      if (dependency.Value.ValueKind != JsonValueKind.Object) {
        continue;
      }

      string child = path.Length == 0
                       ? dependency.Name
                       : path + ">" + dependency.Name;
      if (dependency.Value.TryGetProperty("version", out JsonElement version) &&
          version.ValueKind == JsonValueKind.String) {
        findings.Add(
          new Finding {
            Name = dependency.Name,
            Version = version.GetString()!,
            Path = child,
            Dev = IsTrue(dependency.Value, "dev"),
            Optional = IsTrue(dependency.Value, "optional")
          }
        );
      }

      Walk(dependency.Value, child, findings, depth + 1);
    }
  }

  private static bool IsTrue(JsonElement element, string property) {
    return element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.True;
  }

  private static async Task<JsonDocument?> ReadBodyAsync(HttpContext context,
    CancellationToken ct) {
    try {
      return await JsonDocument.ParseAsync(
               context.Request.Body,
               new JsonDocumentOptions {
                 MaxDepth = C_MAX_DEPTH_
               },
               ct
             );
    } catch (JsonException) {
      return null;
    }
  }

  private sealed class Finding {
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Path { get; init; }
    public bool Dev { get; init; }
    public bool Optional { get; init; }
  }
}
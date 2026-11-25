using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Noodle.Api.Npm;
using Semver;

namespace Noodle.Api.Advisories;

/// <summary>The uploaded file is not advisory data.</summary>
public sealed class InvalidAdvisoryDataException(string message)
  : Exception(message);

/// <summary>
///   Reads security advisories in OSV format, as published by the GitHub
///   Advisory Database and OSV (<c>npm/all.zip</c>): a zip or tar.gz of
///   OSV files, or JSON (one object, an array, or one object per line),
///   optionally gzipped. Only npm entries of GHSA advisories are kept.
/// </summary>
public static class OsvImporter {
  private const int C_MAX_DOCUMENT_BYTES_ = 16 * 1024 * 1024;

  // GHSA identifiers use 4 groups of 4 characters from this alphabet.
  private const string C_GHSA_ALPHABET_ = "23456789cfghjmpqrvwx";

  public static List<AdvisoryRecord> Read(string path) {
    List<AdvisoryRecord> records = [];
    int documents = 0;
    foreach (byte[] json in ReadDocuments(path)) {
      documents++;
      try {
        using JsonDocument document = JsonDocument.Parse(
          json,
          new JsonDocumentOptions {
            MaxDepth = 64
          }
        );
        AddAll(document.RootElement, records);
      } catch (JsonException) {
        // NDJSON: one object per line.
        foreach (string line in Encoding.UTF8.GetString(json)
                                        .Split(
                                          '\n',
                                          StringSplitOptions.RemoveEmptyEntries
                                        )) {
          try {
            using JsonDocument document = JsonDocument.Parse(line);
            AddAll(document.RootElement, records);
          } catch (JsonException ex) {
            throw new InvalidAdvisoryDataException(
              $"Not OSV JSON: {ex.Message}"
            );
          }
        }
      }
    }

    if (documents == 0) {
      throw new InvalidAdvisoryDataException(
        "The file contains no OSV documents."
      );
    }

    // Like npm's registry: one record per affected range ("vulnerability"
    // in GitHub's terms), each with its own number.
    return records.OrderBy(r => r.Package, StringComparer.Ordinal)
                  .ThenBy(r => r.Id)
                  .ToList();
  }

  /// <summary>
  ///   The number of a further range of an advisory: a stable hash, above
  ///   every GHSA number (2^52 and up) and below JavaScript's 2^53.
  /// </summary>
  public static long RangeId(string ghsa, string package, string range) {
    byte[] hash = System.Security.Cryptography.SHA256.HashData(
      Encoding.UTF8.GetBytes($"{ghsa.ToUpperInvariant()}|{package}|{range}")
    );
    long value = BitConverter.ToInt64(hash, 0) & ((1L << 51) - 1);
    return (1L << 52) + value;
  }

  /// <summary>
  ///   The number npm shows for an advisory, derived from its GHSA identifier
  ///   (12 base-20 digits, so it always fits in a JavaScript number).
  /// </summary>
  public static long? IdFromGhsa(string ghsa) {
    if (!ghsa.StartsWith("GHSA-", StringComparison.OrdinalIgnoreCase)) {
      return null;
    }

    string digits = ghsa[5..].Replace("-", "").ToLowerInvariant();
    if (digits.Length != 12) {
      return null;
    }

    long value = 0;
    foreach (char c in digits) {
      int digit = C_GHSA_ALPHABET_.IndexOf(c);
      if (digit < 0) {
        return null;
      }

      value = value * 20 + digit;
    }

    return value;
  }

  private static IEnumerable<byte[]> ReadDocuments(string path) {
    using FileStream file = File.OpenRead(path);
    Span<byte> magic = stackalloc byte[4];
    int read = file.Read(magic);
    file.Seek(0, SeekOrigin.Begin);
    if (read >= 4 &&
        magic[0] == 'P' &&
        magic[1] == 'K' &&
        magic[2] == 3 &&
        magic[3] == 4) {
      return ReadZip(path);
    }

    if (read >= 2 && magic[0] == 0x1F && magic[1] == 0x8B) {
      return IsTarGz(path) ? ReadTarGz(path) : [ReadGzip(path)];
    }

    if (file.Length > 1024L * 1024 * 1024) {
      throw new InvalidAdvisoryDataException("The file is too large.");
    }

    return [File.ReadAllBytes(path)];
  }

  private static IEnumerable<byte[]> ReadZip(string path) {
    using ZipArchive zip = ZipFile.OpenRead(path);
    foreach (ZipArchiveEntry entry in zip.Entries) {
      if (!entry.FullName.EndsWith(
            ".json",
            StringComparison.OrdinalIgnoreCase
          ) ||
          entry.Length > C_MAX_DOCUMENT_BYTES_) {
        continue;
      }

      using Stream stream = entry.Open();
      yield return ReadLimited(stream);
    }
  }

  private static bool IsTarGz(string path) {
    using GZipStream gzip = new(
      File.OpenRead(path),
      CompressionMode.Decompress
    );
    byte[] header = new byte[512];
    int total = 0;
    while (total < header.Length) {
      int read = gzip.Read(header, total, header.Length - total);
      if (read == 0) {
        return false;
      }

      total += read;
    }

    return header.AsSpan(257, 5).SequenceEqual("ustar"u8);
  }

  private static IEnumerable<byte[]> ReadTarGz(string path) {
    using GZipStream gzip = new(
      File.OpenRead(path),
      CompressionMode.Decompress
    );
    TarArchive archive = new(gzip);
    while (archive.Next() is { } entry) {
      if (entry.Kind == TarEntryKind.FILE &&
          entry.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
          entry.Size <= C_MAX_DOCUMENT_BYTES_) {
        yield return archive.ReadAll(C_MAX_DOCUMENT_BYTES_)!;
      }
    }
  }

  private static byte[] ReadGzip(string path) {
    using GZipStream gzip = new(
      File.OpenRead(path),
      CompressionMode.Decompress
    );
    using MemoryStream buffer = new();
    byte[] chunk = new byte[81920];
    int read;
    while ((read = gzip.Read(chunk)) > 0) {
      if (buffer.Length + read > 1024L * 1024 * 1024) {
        throw new InvalidAdvisoryDataException(
          "The file unpacks to more than 1 GiB."
        );
      }

      buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
  }

  private static byte[] ReadLimited(Stream stream) {
    using MemoryStream buffer = new();
    byte[] chunk = new byte[81920];
    int read;
    while ((read = stream.Read(chunk)) > 0) {
      if (buffer.Length + read > C_MAX_DOCUMENT_BYTES_) {
        throw new InvalidAdvisoryDataException("An OSV document is too large.");
      }

      buffer.Write(chunk, 0, read);
    }

    return buffer.ToArray();
  }

  private static void AddAll(JsonElement root, List<AdvisoryRecord> records) {
    if (root.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement item in root.EnumerateArray()) {
        AddAdvisory(item, records);
      }
    } else {
      AddAdvisory(root, records);
    }
  }

  private static void
    AddAdvisory(JsonElement osv, List<AdvisoryRecord> records) {
    if (osv.ValueKind != JsonValueKind.Object ||
        Text(osv, "id") is not { } id ||
        IdFromGhsa(id) is not { } number ||
        osv.TryGetProperty("withdrawn", out _) ||
        !osv.TryGetProperty("affected", out JsonElement affected) ||
        affected.ValueKind != JsonValueKind.Array) {
      return;
    }

    string? vector = null;
    if (osv.TryGetProperty("severity", out JsonElement severities) &&
        severities.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement severity in severities.EnumerateArray()) {
        if (Text(severity, "type") == "CVSS_V3") {
          vector = Text(severity, "score");
        }
      }
    }

    double score = CvssV3.BaseScore(vector) ?? 0;
    JsonElement database =
      osv.TryGetProperty("database_specific", out JsonElement d)
        ? d
        : default;
    string severity_name =
      Text(database, "severity")?.ToLowerInvariant() switch {
        "low" => "low",
        "moderate" or "medium" => "moderate",
        "high" => "high",
        "critical" => "critical",
        _ => score > 0 ? CvssV3.Severity(score) : "moderate"
      };
    // npm lists CWEs by number (CWE-400 before CWE-1333).
    List<string> cwe = Strings(database, "cwe_ids")
                       .OrderBy(
                         c => int.TryParse(
                                c.AsSpan(c.IndexOf('-') + 1),
                                out int n
                              )
                                ? n
                                : int.MaxValue
                       )
                       .ThenBy(c => c, StringComparer.Ordinal)
                       .ToList();
    List<string> cves = Strings(osv, "aliases")
                        .Where(
                          a => a.StartsWith("CVE-", StringComparison.Ordinal)
                        )
                        .ToList();
    List<string> references = [];
    if (osv.TryGetProperty("references", out JsonElement refs) &&
        refs.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement reference in refs.EnumerateArray()) {
        if (Text(reference, "url") is { } url) {
          references.Add("- " + url);
        }
      }
    }

    string? details = Text(osv, "details");
    string title = Text(osv, "summary") ??
                   details?.Split('\n')[0] ??
                   id;
    HashSet<string> numbered = new(StringComparer.Ordinal);
    foreach (JsonElement item in affected.EnumerateArray()) {
      if (!item.TryGetProperty("package", out JsonElement package) ||
          Text(package, "ecosystem") != "npm" ||
          Text(package, "name") is not { } name ||
          !PackageName.IsValid(name)) {
        continue;
      }

      (string? vulnerable, string? patched) = Ranges(item);
      if (vulnerable == null) {
        continue;
      }

      records.Add(
        new AdvisoryRecord {
          // The first range of each package carries the GHSA number itself.
          Id = numbered.Add(name) ? number : RangeId(id, name, vulnerable),
          GhsaId = id,
          Package = name,
          Title = title,
          Severity = severity_name,
          VulnerableVersions = vulnerable,
          PatchedVersions = patched,
          Cwe = cwe,
          Cves = cves,
          CvssScore = score,
          CvssVector = score > 0 ? vector : null,
          Overview = details,
          References =
            references.Count == 0 ? null : string.Join('\n', references),
          Created = Time(osv, "published"),
          Updated = Time(osv, "modified")
        }
      );
    }
  }

  /// <summary>
  ///   OSV events turned into an npm range (and the patched range).
  /// </summary>
  private static (string? Vulnerable, string? Patched) Ranges(
    JsonElement affected) {
    List<string> parts = [];
    List<string> fixes = [];
    string? last_known = Text(
      affected.TryGetProperty("database_specific", out JsonElement d)
        ? d
        : default,
      "last_known_affected_version_range"
    );
    if (affected.TryGetProperty("ranges", out JsonElement ranges) &&
        ranges.ValueKind == JsonValueKind.Array) {
      foreach (JsonElement range in ranges.EnumerateArray()) {
        string? type = Text(range, "type");
        if (type is not ("SEMVER" or "ECOSYSTEM") ||
            !range.TryGetProperty("events", out JsonElement events) ||
            events.ValueKind != JsonValueKind.Array) {
          continue;
        }

        string? start = null;
        bool open = false;
        foreach (JsonElement e in events.EnumerateArray()) {
          if (Text(e, "introduced") is { } introduced) {
            start = introduced;
            open = true;
          } else if (Text(e, "fixed") is { } fixed_version) {
            // GitHub's own range ("<= 4.17.23") is what npm shows, when given.
            parts.Add(
              Interval(start, LastKnown(last_known) ?? "<" + fixed_version)
            );
            fixes.Add(">=" + fixed_version);
            open = false;
          } else if (Text(e, "last_affected") is { } last) {
            parts.Add(Interval(start, "<=" + last));
            open = false;
          } else if (Text(e, "limit") is { } limit) {
            parts.Add(Interval(start, "<" + limit));
            open = false;
          }
        }

        if (open) {
          parts.Add(Interval(start, LastKnown(last_known)));
        }
      }
    }

    if (parts.Count == 0 &&
        affected.TryGetProperty("versions", out JsonElement versions) &&
        versions.ValueKind == JsonValueKind.Array) {
      parts.AddRange(
        versions.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
      );
    }

    if (parts.Count == 0) {
      return (null, null);
    }

    string vulnerable = string.Join(" || ", parts.Distinct());
    if (!SemVersionRange.TryParseNpm(vulnerable, true, out _)) {
      return (null, null);
    }

    return (vulnerable,
            fixes.Count == 0 ? "<0.0.0" : string.Join(" || ", fixes));
  }

  /// <summary>
  ///   <c>"&lt;= 1.2.3"</c> as an npm comparator (<c>&lt;=1.2.3</c>), or
  ///   <c>null</c>.
  /// </summary>
  private static string? LastKnown(string? range) {
    if (range == null) {
      return null;
    }

    string compact = range.Replace(" ", "", StringComparison.Ordinal);
    return compact.StartsWith('<') ? compact : null;
  }

  private static string Interval(string? start, string? end) {
    string lower = start == null || start == "0" ? "" : ">=" + start;
    string result = $"{lower} {end}".Trim();
    return result.Length == 0 ? ">=0.0.0" : result;
  }

  private static string? Text(JsonElement element, string property) {
    return element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
             ? value.GetString()
             : null;
  }

  private static List<string> Strings(JsonElement element, string property) {
    if (element.ValueKind != JsonValueKind.Object ||
        !element.TryGetProperty(property, out JsonElement value) ||
        value.ValueKind != JsonValueKind.Array) {
      return [];
    }

    return value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .ToList();
  }

  private static DateTimeOffset? Time(JsonElement element, string property) {
    return DateTimeOffset.TryParse(
             Text(element, property),
             CultureInfo.InvariantCulture,
             DateTimeStyles.AssumeUniversal,
             out DateTimeOffset time
           )
             ? time
             : null;
  }
}
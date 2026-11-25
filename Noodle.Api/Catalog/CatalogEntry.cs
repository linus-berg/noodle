using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Noodle.Api.Packages;

namespace Noodle.Api.Catalog;

/// <summary>
///   What every instance keeps in memory per package: enough to answer 404s,
///   dist-tags, tarball checks and search without S3, and to find the
///   current packuments. Kept compact (one million of them must fit): the
///   versions are one UTF-8 string, the search fields one byte array.
/// </summary>
public sealed class CatalogEntry {
  public required string Name { get; init; }

  /// <summary>
  ///   <see cref="PackageDocument.Revision" /> this entry was built from.
  /// </summary>
  public required long Revision { get; init; }

  public required long ModifiedTicks { get; init; }

  /// <summary>
  ///   Content hashes of the packuments (16 bytes each rather than hex
  ///   strings).
  /// </summary>
  public required UInt128 CorgiHash { get; init; }

  public required UInt128 FullHash { get; init; }
  public required long CorgiSize { get; init; }
  public required long FullSize { get; init; }

  /// <summary>Base URL the stored packuments use.</summary>
  public required string BaseUrl { get; init; }

  public required string Latest { get; init; }

  /// <summary>
  ///   Other dist-tags as alternating tag and version, or <c>null</c>.
  /// </summary>
  public string[]? Tags { get; init; }

  /// <summary>
  ///   All versions in precedence order, separated by <c>\n</c> (UTF-8).
  /// </summary>
  public required byte[] Versions { get; init; }

  public required SearchFields Search { get; init; }

  /// <summary>
  ///   Arrival time of the latest version (search's <c>date</c>).
  /// </summary>
  public required long LatestTicks { get; init; }

  /// <summary>
  ///   Identifies this state of the package for caches: the revision alone
  ///   starts over when a package is deleted and uploaded again.
  /// </summary>
  public string CacheStamp => $"{Revision}.{ModifiedTicks}";

  public int VersionCount {
    get {
      if (Versions.Length == 0) {
        return 0;
      }

      return Versions.AsSpan().Count((byte)'\n') + 1;
    }
  }

  public bool HasVersion(string version) {
    Span<byte> needle =
      stackalloc byte[Encoding.UTF8.GetMaxByteCount(version.Length)];
    int length = Encoding.UTF8.GetBytes(version, needle);
    needle = needle[..length];
    ReadOnlySpan<byte> rest = Versions;
    while (rest.Length > 0) {
      int end = rest.IndexOf((byte)'\n');
      ReadOnlySpan<byte> item = end < 0 ? rest : rest[..end];
      if (item.SequenceEqual(needle)) {
        return true;
      }

      if (end < 0) {
        break;
      }

      rest = rest[(end + 1)..];
    }

    return false;
  }

  public SortedDictionary<string, string> GetDistTags() {
    SortedDictionary<string, string> tags = new(StringComparer.Ordinal);
    if (Tags != null) {
      for (int i = 0; i + 1 < Tags.Length; i += 2) {
        tags[Tags[i]] = Tags[i + 1];
      }
    }

    tags["latest"] = Latest;
    return tags;
  }

  public string? ResolveTag(string tag) {
    if (tag == "latest") {
      return Latest;
    }

    if (Tags == null) {
      return null;
    }

    for (int i = 0; i + 1 < Tags.Length; i += 2) {
      if (Tags[i] == tag) {
        return Tags[i + 1];
      }
    }

    return null;
  }

  public static UInt128 ParseHash(string hex) {
    return UInt128.Parse(
      hex,
      NumberStyles.AllowHexSpecifier,
      CultureInfo.InvariantCulture
    );
  }

  public static string FormatHash(UInt128 hash) {
    return hash.ToString("x32", CultureInfo.InvariantCulture);
  }

  public static CatalogEntry? FromDocument(PackageDocument document) {
    string? latest = document.Latest;
    if (latest == null || document.Corgi == null || document.Full == null) {
      return null;
    }

    List<string> tags = [];
    foreach (KeyValuePair<string, string> tag in document.GetDistTags()) {
      if (tag.Key != "latest") {
        tags.Add(tag.Key);
        tags.Add(VersionPool.Intern(tag.Value));
      }
    }

    VersionRecord record = document.Find(latest)!;
    using JsonDocument manifest = record.Manifest.Parse();
    return new CatalogEntry {
      Name = document.Name,
      Revision = document.Revision,
      ModifiedTicks = document.Modified.UtcTicks,
      CorgiHash = ParseHash(document.Corgi.Hash),
      FullHash = ParseHash(document.Full.Hash),
      CorgiSize = document.Corgi.Size,
      FullSize = document.Full.Size,
      BaseUrl = VersionPool.Intern(
        document.BaseUrl ?? PackumentWriter.C_DEFAULT_BASE_URL
      ),
      Latest = VersionPool.Intern(latest),
      Tags = tags.Count == 0 ? null : tags.ToArray(),
      Versions = Encoding.UTF8.GetBytes(
        string.Join('\n', document.Versions.Select(v => v.Version))
      ),
      Search = SearchFields.FromManifest(manifest.RootElement),
      LatestTicks = record.Arrived.UtcTicks
    };
  }
}

/// <summary>
///   The search fields of a package's latest version packed into one byte
///   array: description, keywords, author, maintainers, license, homepage,
///   repository and bugs, each as a length-prefixed UTF-8 segment.
/// </summary>
public readonly struct SearchFields {
  public const int C_DESCRIPTION = 0;
  public const int C_KEYWORDS = 1;
  public const int C_AUTHOR = 2;
  public const int C_MAINTAINERS = 3;
  public const int C_LICENSE = 4;
  public const int C_HOMEPAGE = 5;
  public const int C_REPOSITORY = 6;
  public const int C_BUGS = 7;
  private const int C_SEGMENTS_ = 8;

  // Long descriptions are cut; search results show a line or two anyway.
  private const int C_MAX_SEGMENT_ = 4000;

  public SearchFields(byte[] data) {
    Data = data;
  }

  public byte[] Data { get; }

  public ReadOnlySpan<byte> Get(int segment) {
    ReadOnlySpan<byte> data = Data;
    int offset = 0;
    for (int i = 0; i < C_SEGMENTS_; i++) {
      if (offset + 2 > data.Length) {
        return [];
      }

      int length = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
      offset += 2;
      if (i == segment) {
        return data.Slice(offset, Math.Min(length, data.Length - offset));
      }

      offset += length;
    }

    return [];
  }

  public string GetString(int segment) {
    return Encoding.UTF8.GetString(Get(segment));
  }

  /// <summary>
  ///   Lines of a multi-value segment (keywords; people as <c>name\temail</c>).
  /// </summary>
  public List<string> GetLines(int segment) {
    string text = GetString(segment);
    return text.Length == 0 ? [] : text.Split('\n').ToList();
  }

  public static SearchFields FromManifest(JsonElement manifest) {
    string[] segments = new string[C_SEGMENTS_];
    segments[C_DESCRIPTION] = Text(manifest, "description");
    segments[C_KEYWORDS] = string.Join(
      '\n',
      Strings(manifest, "keywords").Select(k => k.Replace('\n', ' '))
    );
    segments[C_AUTHOR] = Person(
      manifest.TryGetProperty("author", out JsonElement author)
        ? author
        : default
    );
    segments[C_MAINTAINERS] = manifest.TryGetProperty(
                                "maintainers",
                                out JsonElement maintainers
                              ) &&
                              maintainers.ValueKind == JsonValueKind.Array
                                ? string.Join(
                                  '\n',
                                  maintainers.EnumerateArray()
                                             .Select(Person)
                                             .Where(p => p.Length > 0)
                                )
                                : "";
    segments[C_LICENSE] = License(manifest);
    segments[C_HOMEPAGE] = Text(manifest, "homepage");
    segments[C_REPOSITORY] = manifest.TryGetProperty(
                               "repository",
                               out JsonElement repository
                             )
                               ? repository.ValueKind switch {
                                 JsonValueKind.Object => Text(
                                   repository,
                                   "url"
                                 ),
                                 JsonValueKind.String =>
                                   repository.GetString()!,
                                 _ => ""
                               }
                               : "";
    segments[C_BUGS] = manifest.TryGetProperty("bugs", out JsonElement bugs)
                         ? bugs.ValueKind switch {
                           JsonValueKind.Object => Text(bugs, "url"),
                           JsonValueKind.String => bugs.GetString()!,
                           _ => ""
                         }
                         : "";
    using MemoryStream buffer = new();
    Span<byte> length = stackalloc byte[2];
    foreach (string segment in segments) {
      byte[] bytes = Encoding.UTF8.GetBytes(segment);
      int size = Math.Min(bytes.Length, C_MAX_SEGMENT_);
      // Never cut a character in half.
      while (size < bytes.Length && size > 0 && (bytes[size] & 0xC0) == 0x80) {
        size--;
      }

      BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)size);
      buffer.Write(length);
      buffer.Write(bytes, 0, size);
    }

    return new SearchFields(buffer.ToArray());
  }

  private static string Text(JsonElement element, string property) {
    return element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out JsonElement value) &&
           value.ValueKind == JsonValueKind.String
             ? value.GetString()!.Replace('\n', ' ')
             : "";
  }

  private static IEnumerable<string> Strings(JsonElement element,
                                             string property) {
    if (!element.TryGetProperty(property, out JsonElement value) ||
        value.ValueKind != JsonValueKind.Array) {
      return [];
    }

    return value.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .Where(v => v.Length > 0);
  }

  private static string Person(JsonElement person) {
    string name;
    string email;
    switch (person.ValueKind) {
      case JsonValueKind.Object:
        name = Text(person, "name");
        email = Text(person, "email");
        break;
      case JsonValueKind.String:
        name = person.GetString()!;
        email = "";
        break;
      default:
        return "";
    }

    name = name.Replace('\t', ' ').Replace('\n', ' ');
    email = email.Replace('\t', ' ').Replace('\n', ' ');
    return name.Length == 0 && email.Length == 0 ? "" : $"{name}\t{email}";
  }

  private static string License(JsonElement manifest) {
    if (!manifest.TryGetProperty("license", out JsonElement license)) {
      return "";
    }

    return license.ValueKind switch {
      JsonValueKind.String => license.GetString()!,
      JsonValueKind.Object => Text(license, "type"),
      _ => ""
    };
  }
}

/// <summary>
///   Shares one copy of each version string: "1.0.0" and friends occur
///   millions of times across packages. Unlike <see cref="string.Intern" />
///   the pool can be dropped.
/// </summary>
public static class VersionPool {
  private const int C_MAX_ENTRIES_ = 2_000_000;

  private static ConcurrentDictionary<string, string> s_pool_ =
    new(StringComparer.Ordinal);

  // ConcurrentDictionary.Count takes every lock; count additions instead.
  private static int s_count_;

  public static string Intern(string value) {
    ConcurrentDictionary<string, string> pool = s_pool_;
    if (pool.TryGetValue(value, out string? shared)) {
      return shared;
    }

    if (pool.TryAdd(value, value) &&
        Interlocked.Increment(ref s_count_) > C_MAX_ENTRIES_) {
      // Mostly one-off prerelease versions by now: start over.
      s_pool_ =
        new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
      s_count_ = 0;
    }

    return pool.GetValueOrDefault(value, value);
  }
}
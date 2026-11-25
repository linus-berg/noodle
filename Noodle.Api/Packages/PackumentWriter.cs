using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Noodle.Api.Npm;

namespace Noodle.Api.Packages;

/// <summary>A generated document, ready to store and serve.</summary>
public sealed class GeneratedDocument {
  public required byte[] Gzip { get; init; }

  /// <summary>Hex content hash of the uncompressed JSON.</summary>
  public required string Hash { get; init; }

  public required long Length { get; init; }
}

/// <summary>
///   Builds packuments from a package document, field for field like
///   registry.npmjs.org: the full document (<c>npm view</c>) and the
///   abbreviated "corgi" one that installs use.
/// </summary>
public static class PackumentWriter {
  public const string C_DEFAULT_BASE_URL = "https://registry.npmjs.org";

  /// <summary>
  ///   Fields of the latest version repeated at the top of the full packument.
  /// </summary>
  private static readonly string[] S_TOP_LEVEL_FIELDS_ = [
    "description",
    "homepage",
    "keywords",
    "repository",
    "author",
    "bugs",
    "license",
    "maintainers",
    "contributors"
  ];

  /// <summary>
  ///   What the abbreviated packument keeps of each version (found by
  ///   comparing registry.npmjs.org's two flavours). Empty and false values
  ///   are left out there, and so here.
  /// </summary>
  private static readonly string[] S_CORGI_FIELDS_ = [
    "name",
    "version",
    "deprecated",
    "dependencies",
    "optionalDependencies",
    "devDependencies",
    "bundleDependencies",
    "peerDependencies",
    "peerDependenciesMeta",
    "bin",
    "directories",
    "dist",
    "engines",
    "_hasShrinkwrap",
    "hasInstallScript",
    "cpu",
    "os",
    "funding"
  ];

  private static readonly string[] S_INSTALL_SCRIPTS_ = [
    "preinstall",
    "install",
    "postinstall"
  ];

  private static readonly JsonWriterOptions S_WRITER_OPTIONS_ = new() {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    SkipValidation = true
  };

  public static byte[] WriteFull(PackageDocument document, string base_url) {
    ArrayBufferWriter<byte> buffer = new(64 * 1024);
    using Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_);
    writer.WriteStartObject();
    writer.WriteString("_id", document.Name);
    writer.WriteString(
      "_rev",
      $"{document.Revision}-{document.CommitId}"
    );
    writer.WriteString("name", document.Name);
    WriteDistTags(writer, document);
    writer.WriteStartObject("versions");
    foreach (VersionRecord version in document.Versions) {
      writer.WritePropertyName(version.Version);
      writer.WriteRawValue(
        WithDist(version, document.Name, base_url),
        true
      );
    }

    writer.WriteEndObject();
    writer.WriteStartObject("time");
    writer.WriteString("created", FormatTime(document.Created));
    writer.WriteString("modified", FormatTime(document.Modified));
    foreach (VersionRecord version in document.Versions) {
      writer.WriteString(version.Version, FormatTime(version.Arrived));
    }

    writer.WriteEndObject();
    string? latest = document.Latest;
    VersionRecord? latest_record =
      latest == null ? null : document.Find(latest);
    if (latest_record != null) {
      using JsonDocument manifest = latest_record.Manifest.Parse();
      foreach (string field in S_TOP_LEVEL_FIELDS_) {
        if (manifest.RootElement.TryGetProperty(field, out JsonElement value)) {
          writer.WritePropertyName(field);
          value.WriteTo(writer);
        }
      }
    }

    writer.WriteString("readme", document.Readme ?? "");
    writer.WriteString("readmeFilename", document.ReadmeFilename ?? "");
    writer.WriteEndObject();
    writer.Flush();
    return buffer.WrittenSpan.ToArray();
  }

  public static byte[] WriteCorgi(PackageDocument document, string base_url) {
    ArrayBufferWriter<byte> buffer = new(64 * 1024);
    using Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_);
    writer.WriteStartObject();
    writer.WriteString("name", document.Name);
    WriteDistTags(writer, document);
    writer.WriteString("modified", FormatTime(document.Modified));
    writer.WriteStartObject("versions");
    foreach (VersionRecord version in document.Versions) {
      writer.WritePropertyName(version.Version);
      WriteCorgiVersion(writer, version, document.Name, base_url);
    }

    writer.WriteEndObject();
    writer.WriteEndObject();
    writer.Flush();
    return buffer.WrittenSpan.ToArray();
  }

  /// <summary>
  ///   One version manifest, as <c>GET /{name}/{version}</c> returns it.
  /// </summary>
  public static byte[] WriteVersion(VersionRecord version, string name,
                                    string base_url) {
    return WithDist(version, name, base_url);
  }

  public static GeneratedDocument Pack(byte[] json) {
    using MemoryStream compressed = new(json.Length / 6 + 64);
    using (GZipStream gzip = new(
             compressed,
             CompressionLevel.Optimal,
             true
           )) {
      gzip.Write(json);
    }

    return new GeneratedDocument {
      Gzip = compressed.ToArray(),
      Hash = Hash(json),
      Length = json.Length
    };
  }

  public static string Hash(ReadOnlySpan<byte> json) {
    Span<byte> hash = stackalloc byte[32];
    SHA256.HashData(json, hash);
    return Convert.ToHexStringLower(hash[..16]);
  }

  /// <summary>
  ///   Replaces the base URL of every tarball URL in a stored (gzipped)
  ///   packument, for requests that reach this server under another name.
  /// </summary>
  public static byte[] RebaseGzip(byte[] gzip, string from_base,
                                  string to_base) {
    byte[] json = Gunzip(gzip);
    byte[] needle = Encoding.UTF8.GetBytes($"\"tarball\":\"{from_base}/");
    byte[] replacement = Encoding.UTF8.GetBytes($"\"tarball\":\"{to_base}/");
    using MemoryStream output = new(gzip.Length + 1024);
    using (GZipStream compressor = new(
             output,
             CompressionLevel.Fastest,
             true
           )) {
      ReadOnlySpan<byte> rest = json;
      while (true) {
        int index = rest.IndexOf(needle);
        if (index < 0) {
          compressor.Write(rest);
          break;
        }

        compressor.Write(rest[..index]);
        compressor.Write(replacement);
        rest = rest[(index + needle.Length)..];
      }
    }

    return output.ToArray();
  }

  public static byte[] Gunzip(byte[] gzip) {
    using GZipStream decompressor = new(
      new MemoryStream(gzip, false),
      CompressionMode.Decompress
    );
    using MemoryStream output = new(gzip.Length * 6);
    decompressor.CopyTo(output);
    return output.ToArray();
  }

  public static string TarballUrl(string base_url, string name,
                                  string version) {
    return $"{base_url}/{PackageName.TarballPath(name, version)}";
  }

  public static string FormatTime(DateTimeOffset time) {
    return time.UtcDateTime.ToString(
      "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
      CultureInfo.InvariantCulture
    );
  }

  public static bool HasInstallScript(JsonElement manifest) {
    if (!manifest.TryGetProperty("scripts", out JsonElement scripts) ||
        scripts.ValueKind != JsonValueKind.Object) {
      return false;
    }

    foreach (string name in S_INSTALL_SCRIPTS_) {
      if (scripts.TryGetProperty(name, out JsonElement script) &&
          script.ValueKind == JsonValueKind.String &&
          script.GetString()!.Length > 0) {
        return true;
      }
    }

    return false;
  }

  private static void WriteDistTags(Utf8JsonWriter writer,
                                    PackageDocument document) {
    writer.WriteStartObject("dist-tags");
    foreach (KeyValuePair<string, string> tag in document.GetDistTags()) {
      writer.WriteString(tag.Key, tag.Value);
    }

    writer.WriteEndObject();
  }

  private static void WriteCorgiVersion(Utf8JsonWriter writer,
                                        VersionRecord version, string name,
                                        string base_url) {
    using JsonDocument manifest = version.Manifest.Parse();
    JsonElement root = manifest.RootElement;
    writer.WriteStartObject();
    foreach (string field in S_CORGI_FIELDS_) {
      switch (field) {
        case "dist":
          WriteDist(writer, version, name, base_url);
          continue;
        case "hasInstallScript":
          if (HasInstallScript(root)) {
            writer.WriteBoolean("hasInstallScript", true);
          }

          continue;
      }

      if (root.TryGetProperty(field, out JsonElement value) &&
          !IsEmpty(value)) {
        writer.WritePropertyName(field);
        value.WriteTo(writer);
      }
    }

    writer.WriteEndObject();
  }

  private static bool IsEmpty(JsonElement value) {
    return value.ValueKind switch {
      JsonValueKind.Object => !value.EnumerateObject().Any(),
      JsonValueKind.Array => value.GetArrayLength() == 0,
      JsonValueKind.String => value.GetString()!.Length == 0,
      JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined =>
        true,
      _ => false
    };
  }

  private static void WriteDist(Utf8JsonWriter writer, VersionRecord version,
                                string name, string base_url) {
    writer.WriteStartObject("dist");
    writer.WriteString("integrity", version.Integrity);
    writer.WriteString("shasum", version.Shasum);
    writer.WriteString(
      "tarball",
      TarballUrl(base_url, name, version.Version)
    );
    writer.WriteNumber("fileCount", version.FileCount);
    writer.WriteNumber("unpackedSize", version.UnpackedSize);
    writer.WriteEndObject();
  }

  /// <summary>The stored manifest with <c>dist</c> appended.</summary>
  private static byte[] WithDist(VersionRecord version, string name,
                                 string base_url) {
    ArrayBufferWriter<byte> dist = new(512);
    using (Utf8JsonWriter writer = new(dist, S_WRITER_OPTIONS_)) {
      writer.WriteStartObject();
      WriteDist(writer, version, name, base_url);
      writer.WriteEndObject();
    }

    // {"dist":{...}} without its braces, spliced in before the manifest's
    // closing brace.
    ReadOnlySpan<byte> manifest = version.Manifest.Utf8;
    ReadOnlySpan<byte> dist_member = dist.WrittenSpan[1..^1];
    int close = manifest.LastIndexOf((byte)'}');
    bool empty = manifest[..close].TrimEnd(" \t\r\n"u8).EndsWith("{"u8);
    byte[] result = new byte[close + (empty ? 0 : 1) + dist_member.Length + 1];
    manifest[..close].CopyTo(result);
    int position = close;
    if (!empty) {
      result[position++] = (byte)',';
    }

    dist_member.CopyTo(result.AsSpan(position));
    result[^1] = (byte)'}';
    return result;
  }
}
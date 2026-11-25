using System.IO.Compression;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Noodle.Api.Advisories;
using Noodle.Api.Packages;

namespace Noodle.Api.Json;

/// <summary>
///   Source generated serializers for stored documents and responses.
/// </summary>
[JsonSourceGenerationOptions(
  PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
  DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
)]
[JsonSerializable(typeof(PackageDocument))]
[JsonSerializable(typeof(VersionDocument))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, long>>))]
[JsonSerializable(typeof(AdvisoryData))]
[JsonSerializable(typeof(UploadResponse))]
[JsonSerializable(typeof(OperationResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(AdvisoryStatus))]
[JsonSerializable(typeof(Statistics))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, List<string>>))]
[JsonSerializable(typeof(string))]
public sealed partial class NoodleJsonContext : JsonSerializerContext;

/// <summary>Response to an upload.</summary>
public sealed class UploadResponse {
  public required string Name { get; init; }
  public required string Version { get; init; }

  /// <summary><c>created</c>, <c>exists</c> or <c>conflict</c>.</summary>
  public required string Status { get; init; }

  public string? Integrity { get; init; }
  public string? Tarball { get; init; }
  public string? Message { get; init; }
}

/// <summary>Response to an administrative operation.</summary>
public sealed class OperationResponse {
  public required string Status { get; init; }
  public string? Name { get; init; }
  public string? Version { get; init; }
  public string? Tag { get; init; }
  public string? Message { get; init; }
}

/// <summary>npm's error body: <c>{"error":"Not found"}</c>.</summary>
public sealed class ErrorResponse {
  public required string Error { get; init; }
}

public sealed class HealthResponse {
  public required string Status { get; init; }
  public string Version { get; init; } = NoodleVersion.S_CURRENT;
  public int? Packages { get; init; }
  public long? Versions { get; init; }
  public AdvisoryStatus? Advisories { get; init; }
  public string? Rebuild { get; init; }
  public Statistics? Stats { get; init; }
}

/// <summary>Counters since the instance started.</summary>
public sealed class Statistics {
  public long Commits { get; init; }

  /// <summary>Lost conditional-write races that were retried.</summary>
  public long Conflicts { get; init; }

  public long WrittenBytes { get; init; }
  public long PackumentCacheHits { get; init; }
  public long PackumentCacheMisses { get; init; }
  public long PackumentCacheBytes { get; init; }
  public long TarballCacheHits { get; init; }
  public long TarballCacheMisses { get; init; }
  public long TarballCacheBytes { get; init; }
  public long StorageGets { get; init; }
  public long StorageNotModified { get; init; }
  public long StorageHeads { get; init; }
  public long StoragePuts { get; init; }
  public long StorageDeletes { get; init; }
  public long StorageLists { get; init; }
}

public sealed class AdvisoryStatus {
  public required bool Imported { get; init; }
  public DateTimeOffset? ImportedAt { get; init; }
  public int? Count { get; init; }
  public int? Packages { get; init; }
  public string? Source { get; init; }
}

public static class NoodleVersion {
  /// <summary>
  ///   The release version (<c>/p:Version</c>), without build metadata.
  /// </summary>
  public static readonly string S_CURRENT =
    (typeof(NoodleVersion).Assembly
                          .GetCustomAttribute<
                            AssemblyInformationalVersionAttribute>()
                          ?.InformationalVersion ??
     "0.0.0").Split('+')[0];
}

public static class NoodleJson {
  /// <summary>
  ///   Like npm, non-ASCII text and characters such as <c>+</c> are not
  ///   escaped (responses are never embedded in HTML).
  /// </summary>
  public static readonly NoodleJsonContext S_CONTEXT = new(
    // Options given here replace the attribute's, so repeat them.
    new JsonSerializerOptions {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
      Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
      MaxDepth = 256
    }
  );

  public static byte[] SerializeGzip<T>(T value, JsonTypeInfo<T> type) {
    using MemoryStream buffer = new();
    using (GZipStream gzip = new(buffer, CompressionLevel.Optimal, true)) {
      JsonSerializer.Serialize(gzip, value, type);
    }

    return buffer.ToArray();
  }

  public static T? DeserializeGzip<T>(byte[] data, JsonTypeInfo<T> type) {
    using GZipStream gzip = new(
      new MemoryStream(data, false),
      CompressionMode.Decompress
    );
    return JsonSerializer.Deserialize(gzip, type);
  }
}
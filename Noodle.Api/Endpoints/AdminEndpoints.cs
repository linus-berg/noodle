using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Noodle.Api.Advisories;
using Noodle.Api.Catalog;
using Noodle.Api.Configuration;
using Noodle.Api.Json;
using Noodle.Api.Npm;
using Noodle.Api.Packages;

namespace Noodle.Api.Endpoints;

/// <summary>
///   Write operations, all requiring an API key: tarball uploads, purging
///   versions, dist-tags (npm's own <c>npm dist-tag</c> API), advisory
///   imports and rebuilds.
/// </summary>
public static class AdminEndpoints {
  private const long C_MAX_ADVISORY_BYTES_ = 1024L * 1024 * 1024;

  public static void MapAdminEndpoints(this IEndpointRouteBuilder app) {
    app.MapMethods(
         "/-/noodle/upload",
         [
           HttpMethods.Put,
           HttpMethods.Post
         ],
         UploadAsync
       )
       .RequireApiKey();
    app.MapDelete("/-/noodle/package/{**path}", PurgeAsync).RequireApiKey();
    app.MapPut("/-/noodle/advisories", ImportAdvisoriesAsync).RequireApiKey();
    app.MapDelete("/-/noodle/advisories", DeleteAdvisoriesAsync)
       .RequireApiKey();
    app.MapPost("/-/noodle/rebuild", Rebuild).RequireApiKey();

    // dist-tags: npm dist-tag ls / add / rm.
    app.MapGet("/-/package/{**path}", GetDistTags);
    app.MapPut("/-/package/{**path}", SetDistTagAsync).RequireApiKey();
    app.MapPost("/-/package/{**path}", SetDistTagAsync).RequireApiKey();
    app.MapDelete("/-/package/{**path}", RemoveDistTagAsync).RequireApiKey();
  }

  private static async Task<IResult> UploadAsync(HttpContext context,
                                                 PackageIngestor ingestor,
                                                 IOptions<NoodleOptions>
                                                   options,
                                                 ILoggerFactory logger_factory,
                                                 CancellationToken ct) {
    long max_bytes = options.Value.MaxTarballBytes;
    IHttpMaxRequestBodySizeFeature? limit =
      context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) {
      limit.MaxRequestBodySize = max_bytes;
    }

    bool wait = context.Request.Query["wait"].ToString() is "true" or "1";
    string path = Path.Combine(
      Path.GetTempPath(),
      $"noodle-upload-{Guid.NewGuid():N}.tgz"
    );
    ILogger logger = logger_factory.CreateLogger(typeof(AdminEndpoints));
    try {
      Received received = await ReceiveAsync(
                            context.Request.Body,
                            path,
                            max_bytes,
                            ct
                          );
      if (received.Size == 0) {
        return HttpHelpers.Error(
          StatusCodes.Status400BadRequest,
          "The request contains no tarball."
        );
      }

      IngestResult result = await ingestor.IngestAsync(
                              path,
                              received.Size,
                              received.Sha512,
                              received.Sha1,
                              wait,
                              ct
                            );
      (int status, string text, string? message) = result.Status switch {
        ChangeStatus.CREATED or ChangeStatus.ACCEPTED =>
          (StatusCodes.Status201Created, "created", null),
        ChangeStatus.EXISTS => (StatusCodes.Status200OK, "exists",
                                "This tarball is already present."),
        _ => (StatusCodes.Status409Conflict, "conflict",
              "A different tarball with this name and version exists; " +
              "the first one stays.")
      };
      return Results.Json(
        new UploadResponse {
          Name = result.Name,
          Version = result.Version,
          Status = text,
          Integrity = result.Integrity,
          Tarball = PackumentWriter.TarballUrl(
            context.GetBaseUrl(),
            result.Name,
            result.Version
          ),
          Message = message
        },
        NoodleJson.S_CONTEXT.UploadResponse,
        "application/json",
        status
      );
    } catch (Exception ex) when (ex is InvalidTarballException
                                   or InvalidManifestException) {
      logger.LogWarning("Rejected upload: {Reason}", ex.Message);
      return HttpHelpers.Error(StatusCodes.Status400BadRequest, ex.Message);
    } catch (TooLargeException) {
      return TooLarge(max_bytes);
    } catch (BadHttpRequestException ex) when (
      ex.StatusCode == StatusCodes.Status413PayloadTooLarge) {
      return TooLarge(max_bytes);
    } finally {
      File.Delete(path);
    }
  }

  /// <summary>
  ///   Removes a version (or with no version, every version) for good.
  /// </summary>
  private static async Task<IResult> PurgeAsync(string path,
                                                PackageCommitter committer,
                                                PackageRepository repository,
                                                CancellationToken ct) {
    if (!RegistryEndpoints.TryParsePath(
          path,
          out string name,
          out string[] rest
        ) ||
        rest.Length > 1) {
      return HttpHelpers.NotFound();
    }

    List<string> versions;
    if (rest.Length == 1) {
      versions = [rest[0]];
    } else {
      StoredDocument? stored = await repository.LoadAsync(name, ct);
      versions = stored?.Document.Versions.Select(v => v.Version).ToList() ??
                 [];
    }

    List<Task<ChangeResult>> removals = versions.Select(
                                                  v => committer.SubmitAsync(
                                                    name,
                                                    new RemoveVersion(v),
                                                    ct
                                                  )
                                                )
                                                .ToList();
    ChangeResult[] results = await Task.WhenAll(removals);
    int removed = results.Count(r => r.Status == ChangeStatus.REMOVED);
    if (removed == 0) {
      return HttpHelpers.Error(
        StatusCodes.Status404NotFound,
        "No such package version."
      );
    }

    return Results.Json(
      new OperationResponse {
        Status = "deleted",
        Name = name,
        Version = rest.Length == 1 ? rest[0] : null,
        Message = $"{removed} version(s) removed"
      },
      NoodleJson.S_CONTEXT.OperationResponse
    );
  }

  private static IResult GetDistTags(string path, PackageCatalog catalog) {
    if (!TryParseTagPath(path, out string name, out string? tag) ||
        tag != null) {
      return HttpHelpers.NotFound();
    }

    CatalogEntry? entry = catalog.Find(name);
    if (entry == null) {
      return Results.Json(
        "Not Found",
        NoodleJson.S_CONTEXT.String,
        "application/json",
        StatusCodes.Status404NotFound
      );
    }

    return Results.Json(
      entry.GetDistTags(),
      NoodleJson.S_CONTEXT.SortedDictionaryStringString
    );
  }

  /// <summary>
  ///   <c>npm dist-tag add pkg@1.2.3 beta</c>: the body is the version as a
  ///   JSON string.
  /// </summary>
  private static async Task<IResult> SetDistTagAsync(HttpContext context,
    string path,
    PackageCommitter committer,
    CancellationToken ct) {
    if (!TryParseTagPath(path, out string name, out string? tag) ||
        tag == null) {
      return HttpHelpers.NotFound();
    }

    if (!IsValidTag(tag)) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        "Tags are letters, digits, '.', '-' and '_', and must not look " +
        "like a version."
      );
    }

    string? version;
    try {
      using JsonDocument body = await JsonDocument.ParseAsync(
                                  context.Request.Body,
                                  cancellationToken: ct
                                );
      version = body.RootElement.ValueKind == JsonValueKind.String
                  ? body.RootElement.GetString()
                  : null;
    } catch (JsonException) {
      version = null;
    }

    version = NpmVersion.Clean(version);
    if (version == null) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        "The body must be the version as a JSON string, e.g. \"1.2.3\"."
      );
    }

    ChangeResult result = await committer.SubmitAsync(
                            name,
                            new SetTag(tag, version),
                            ct
                          );
    return result.Status == ChangeStatus.NOT_FOUND
             ? HttpHelpers.Error(
               StatusCodes.Status404NotFound,
               $"{name}@{version} does not exist."
             )
             : Results.Json(
               new OperationResponse {
                 Status = "ok",
                 Name = name,
                 Tag = tag,
                 Version = version
               },
               NoodleJson.S_CONTEXT.OperationResponse
             );
  }

  private static async Task<IResult> RemoveDistTagAsync(string path,
    PackageCommitter committer,
    CancellationToken ct) {
    if (!TryParseTagPath(path, out string name, out string? tag) ||
        tag == null) {
      return HttpHelpers.NotFound();
    }

    ChangeResult result =
      await committer.SubmitAsync(name, new RemoveTag(tag), ct);
    return result.Status == ChangeStatus.NOT_FOUND
             ? HttpHelpers.Error(
               StatusCodes.Status404NotFound,
               tag == "latest"
                 ? "latest is not pinned; it follows the highest version."
                 : $"{name} has no tag {tag}."
             )
             : Results.Json(
               new OperationResponse {
                 Status = "ok",
                 Name = name,
                 Tag = tag
               },
               NoodleJson.S_CONTEXT.OperationResponse
             );
  }

  private static async Task<IResult> ImportAdvisoriesAsync(HttpContext context,
    AdvisoryStore advisories, ChangeFeed feed, CancellationToken ct) {
    IHttpMaxRequestBodySizeFeature? limit =
      context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if (limit is { IsReadOnly: false }) {
      limit.MaxRequestBodySize = C_MAX_ADVISORY_BYTES_;
    }

    string path = Path.Combine(
      Path.GetTempPath(),
      $"noodle-advisories-{Guid.NewGuid():N}"
    );
    try {
      await ReceiveAsync(context.Request.Body, path, C_MAX_ADVISORY_BYTES_, ct);
      string source = context.Request.Query["source"].ToString();
      AdvisoryData data = await advisories.ImportAsync(
                            path,
                            string.IsNullOrWhiteSpace(source) ? null : source,
                            ct
                          );
      await feed.AppendAsync(ChangeKind.ADVISORIES, "import", null, ct);
      return Results.Json(
        new AdvisoryStatus {
          Imported = true,
          ImportedAt = data.ImportedAt,
          Count = data.Advisories.Count,
          Packages = advisories.PackageCount,
          Source = data.Source
        },
        NoodleJson.S_CONTEXT.AdvisoryStatus
      );
    } catch (Exception ex) when (ex is InvalidAdvisoryDataException
                                   or InvalidDataException
                                   or InvalidTarException) {
      return HttpHelpers.Error(
        StatusCodes.Status400BadRequest,
        $"Not OSV advisory data: {ex.Message}"
      );
    } catch (TooLargeException) {
      return HttpHelpers.Error(
        StatusCodes.Status413PayloadTooLarge,
        "Advisory files may be at most 1 GiB."
      );
    } finally {
      File.Delete(path);
    }
  }

  private static async Task<IResult> DeleteAdvisoriesAsync(
    AdvisoryStore advisories, ChangeFeed feed, CancellationToken ct) {
    if (!await advisories.DeleteAsync(ct)) {
      return HttpHelpers.NotFound();
    }

    await feed.AppendAsync(ChangeKind.ADVISORIES, "delete", null, ct);
    return Results.Json(
      new OperationResponse {
        Status = "deleted"
      },
      NoodleJson.S_CONTEXT.OperationResponse
    );
  }

  /// <summary>
  ///   Rebuilds in the background (it can take minutes); progress shows in
  ///   <c>/health</c>. <c>?wait=true</c> waits for it.
  /// </summary>
  private static async Task<IResult> Rebuild(HttpContext context,
                                             CatalogSynchronizer synchronizer,
                                             PackageCatalog catalog,
                                             ILoggerFactory logger_factory,
                                             IHostApplicationLifetime
                                               lifetime) {
    RebuildScope scope =
      context.Request.Query["scope"].ToString().ToLowerInvariant() switch {
        "versions" => RebuildScope.VERSIONS,
        "packuments" => RebuildScope.PACKUMENTS,
        _ => RebuildScope.CATALOG
      };
    Task rebuild = Task.Run(
      () => synchronizer.RebuildAsync(scope, lifetime.ApplicationStopping)
    );
    if (context.Request.Query["wait"].ToString() is "true" or "1") {
      await rebuild;
      return Results.Json(
        new HealthResponse {
          Status = "rebuilt",
          Packages = catalog.Count,
          Rebuild = synchronizer.RebuildStatus
        },
        NoodleJson.S_CONTEXT.HealthResponse
      );
    }

    ILogger logger = logger_factory.CreateLogger(typeof(AdminEndpoints));
    _ = rebuild.ContinueWith(
      t => logger.LogError(t.Exception, "Rebuild failed"),
      TaskContinuationOptions.OnlyOnFaulted
    );
    return Results.Json(
      new HealthResponse {
        Status = "started",
        Rebuild = synchronizer.RebuildStatus
      },
      NoodleJson.S_CONTEXT.HealthResponse,
      "application/json",
      StatusCodes.Status202Accepted
    );
  }

  /// <summary>Parses <c>{name}/dist-tags[/{tag}]</c>.</summary>
  private static bool TryParseTagPath(string path, out string name,
                                      out string? tag) {
    tag = null;
    if (!RegistryEndpoints.TryParsePath(path, out name, out string[] rest) ||
        rest.Length is 0 or > 2 ||
        rest[0] != "dist-tags") {
      return false;
    }

    tag = rest.Length == 2 ? rest[1] : null;
    return true;
  }

  public static bool IsValidTag(string tag) {
    if (tag.Length is 0 or > 128 ||
        !char.IsAsciiLetterOrDigit(tag[0]) ||
        tag.Any(
          c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
        )) {
      return false;
    }

    // npm refuses tags that are valid version ranges ("1", "1.x", "v2").
    return NpmVersion.Clean(tag) == null &&
           !Semver.SemVersionRange.TryParseNpm(tag, out _);
  }

  private static IResult TooLarge(long max_bytes) {
    return HttpHelpers.Error(
      StatusCodes.Status413PayloadTooLarge,
      $"Tarballs may be at most {max_bytes:N0} bytes (Noodle:MaxTarballBytes)."
    );
  }

  /// <summary>
  ///   Streams a request body to a file, hashing it on the way.
  /// </summary>
  private static async Task<Received> ReceiveAsync(Stream body, string path,
                                                   long max_bytes,
                                                   CancellationToken ct) {
    using IncrementalHash sha512 =
      IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    using IncrementalHash sha1 =
      IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    byte[] buffer = new byte[128 * 1024];
    long size = 0;
    await using FileStream file = new(
      path,
      FileMode.CreateNew,
      FileAccess.Write,
      FileShare.None,
      buffer.Length,
      FileOptions.Asynchronous
    );
    int read;
    while ((read = await body.ReadAsync(buffer, ct)) > 0) {
      size += read;
      if (size > max_bytes) {
        throw new TooLargeException();
      }

      sha512.AppendData(buffer, 0, read);
      sha1.AppendData(buffer, 0, read);
      await file.WriteAsync(buffer.AsMemory(0, read), ct);
    }

    return new Received {
      Size = size,
      Sha512 = Convert.ToBase64String(sha512.GetHashAndReset()),
      Sha1 = Convert.ToHexStringLower(sha1.GetHashAndReset())
    };
  }

  private sealed class Received {
    public required long Size { get; init; }
    public required string Sha512 { get; init; }
    public required string Sha1 { get; init; }
  }

  private sealed class TooLargeException : Exception;
}
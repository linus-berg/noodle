using Noodle.Api.Advisories;
using Noodle.Api.Catalog;
using Noodle.Api.Json;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Api.Endpoints;

/// <summary>
///   Health, ping, and the account and signing endpoints a read-only
///   registry without accounts or signing keys answers with errors (so
///   clients fail fast instead of hanging).
/// </summary>
public static class MiscEndpoints {
  public static void MapMiscEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/-/ping", () => Results.Text("{}", "application/json"));
    app.MapGet("/health", Health);
    app.MapGet("/health/ready", Ready);

    // Accounts: there are none.
    app.MapGet("/-/whoami", NoAccounts);
    app.MapMethods(
      "/-/v1/login",
      [
        HttpMethods.Post
      ],
      NoAccounts
    );
    app.MapMethods(
      "/-/user/{**rest}",
      [
        HttpMethods.Get,
        HttpMethods.Put,
        HttpMethods.Post,
        HttpMethods.Delete
      ],
      NoAccounts
    );
    app.MapMethods(
      "/-/npm/v1/{area:regex(^(user|tokens|org|team)$)}/{**rest}",
      [
        HttpMethods.Get,
        HttpMethods.Put,
        HttpMethods.Post,
        HttpMethods.Delete
      ],
      NoAccounts
    );

    // No registry signatures or provenance: answered like a registry
    // without signing keys.
    app.MapGet("/-/npm/v1/keys", () => HttpHelpers.NotFound());
    app.MapGet("/-/npm/v1/attestations/{**spec}", () => HttpHelpers.NotFound());
  }

  private static IResult NoAccounts() {
    return HttpHelpers.Error(
      StatusCodes.Status401Unauthorized,
      "This registry has no user accounts; it is read-only."
    );
  }

  private static IResult Health(PackageCatalog catalog,
                                AdvisoryStore advisories,
                                CatalogSynchronizer synchronizer,
                                PackageCommitter committer,
                                PackumentCache cache,
                                TarballCache tarballs,
                                IObjectStore store) {
    return Results.Json(
      new HealthResponse {
        Status = catalog.IsReady ? "ok" : "loading",
        Packages = catalog.IsReady ? catalog.Count : null,
        Versions = catalog.IsReady ? catalog.CountVersions() : null,
        Advisories = new AdvisoryStatus {
          Imported = advisories.HasData,
          ImportedAt = advisories.Data?.ImportedAt,
          Count = advisories.Data?.Advisories.Count,
          Packages = advisories.HasData ? advisories.PackageCount : null,
          Source = advisories.Data?.Source
        },
        Rebuild = synchronizer.RebuildStatus,
        Stats = new Statistics {
          Commits = committer.Commits,
          Conflicts = committer.Conflicts,
          WrittenBytes = committer.WrittenBytes,
          PackumentCacheHits = cache.Hits,
          PackumentCacheMisses = cache.Misses,
          PackumentCacheBytes = cache.Size,
          TarballCacheHits = tarballs.Hits,
          TarballCacheMisses = tarballs.Misses,
          TarballCacheBytes = tarballs.Size,
          StorageGets = store.Counters.Gets,
          StorageNotModified = store.Counters.NotModified,
          StorageHeads = store.Counters.Heads,
          StoragePuts = store.Counters.Puts,
          StorageDeletes = store.Counters.Deletes,
          StorageLists = store.Counters.Lists
        }
      },
      NoodleJson.S_CONTEXT.HealthResponse
    );
  }

  private static IResult Ready(PackageCatalog catalog) {
    return catalog.IsReady
             ? Results.Json(
               new HealthResponse {
                 Status = "ok",
                 Packages = catalog.Count
               },
               NoodleJson.S_CONTEXT.HealthResponse
             )
             : Results.Json(
               new HealthResponse {
                 Status = "loading"
               },
               NoodleJson.S_CONTEXT.HealthResponse,
               "application/json",
               StatusCodes.Status503ServiceUnavailable
             );
  }
}
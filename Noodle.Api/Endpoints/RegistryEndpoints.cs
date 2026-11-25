using System.IO.Compression;
using System.Text;
using Microsoft.Net.Http.Headers;
using Noodle.Api.Catalog;
using Noodle.Api.Json;
using Noodle.Api.Npm;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Api.Endpoints;

/// <summary>
///   The package endpoints every client uses: packuments
///   (<c>/{name}</c>), single versions (<c>/{name}/{version|tag}</c>) and
///   tarballs (<c>/{name}/-/{file}.tgz</c>). Scoped names come as
///   <c>/@scope%2fname</c> or <c>/@scope/name</c>.
/// </summary>
public static class RegistryEndpoints {
  private const string C_PACKUMENT_CACHE_ = "public, max-age=300";
  private const string C_TARBALL_CACHE_ = "public, immutable, max-age=31557600";

  public static void MapRegistryEndpoints(this IEndpointRouteBuilder app) {
    app.MapMethods(
         "/{**path}",
         [
           HttpMethods.Get,
           HttpMethods.Head
         ],
         HandleReadAsync
       )
       .WithOrder(1000);
    app.MapMethods(
         "/{**path}",
         [
           HttpMethods.Put,
           HttpMethods.Post,
           HttpMethods.Delete,
           HttpMethods.Patch
         ],
         HandleWrite
       )
       .WithOrder(1000);
  }

  /// <summary>Splits a request path into package name and the rest.</summary>
  public static bool TryParsePath(string path, out string name,
                                  out string[] rest) {
    name = "";
    rest = [];
    // Request.Path keeps an encoded slash encoded; npm encodes the one in
    // scoped names.
    string decoded = path.Replace("%2F", "/", StringComparison.Ordinal)
                         .Replace("%2f", "/", StringComparison.Ordinal)
                         .TrimStart('/');
    if (decoded.Length == 0) {
      return false;
    }

    string[] segments = decoded.Split('/');
    int used = 1;
    if (segments[0].StartsWith('@')) {
      if (segments.Length < 2) {
        return false;
      }

      name = segments[0] + "/" + segments[1];
      used = 2;
    } else {
      name = segments[0];
    }

    rest = segments[used..];
    if (rest.Length > 0 && rest[^1].Length == 0) {
      // A trailing slash (/express/) means the same as none.
      rest = rest[..^1];
    }

    return PackageName.IsValid(name);
  }

  private static async Task<IResult> HandleReadAsync(HttpContext context,
    PackageCatalog catalog, PackageRepository repository,
    PackumentCache cache, IObjectStore store, DownloadStatistics downloads,
    PackageCommitter committer, TarballCache tarballs,
    ILoggerFactory logger_factory, CancellationToken ct) {
    string path = context.Request.Path.Value ?? "/";
    if (path.StartsWith("/-/", StringComparison.Ordinal) ||
        !TryParsePath(path, out string name, out string[] rest) ||
        rest.Length > 2) {
      // /-/ is the registry's own namespace: an endpoint we lack.
      return Unhandled(context, logger_factory);
    }

    if (rest.Length == 2) {
      return rest[0] == "-" &&
             rest[1].EndsWith(".tgz", StringComparison.Ordinal)
               ? await GetTarballAsync(
                   context,
                   name,
                   rest[1],
                   catalog,
                   store,
                   downloads,
                   tarballs,
                   ct
                 )
               : Unhandled(context, logger_factory);
    }

    if (!catalog.MayExist(name)) {
      return PackageNotFound(context);
    }

    if (rest.Length == 1) {
      return await GetVersionAsync(
               context,
               name,
               rest[0],
               catalog,
               repository,
               cache,
               ct
             );
    }

    return await GetPackumentAsync(
             context,
             name,
             HttpHelpers.WantsCorgi(context.Request),
             catalog,
             repository,
             cache,
             committer,
             true,
             ct
           );
  }

  private static IResult HandleWrite(HttpContext context,
                                     ILoggerFactory logger_factory) {
    if (context.Request.Path.StartsWithSegments("/-")) {
      return Unhandled(context, logger_factory);
    }

    // npm publish, unpublish, deprecate, owner changes, ...
    return HttpHelpers.Error(
      StatusCodes.Status405MethodNotAllowed,
      "This registry is read-only: packages are added by uploading their " +
      "tarballs, not with npm publish."
    );
  }

  private static async Task<IResult> GetPackumentAsync(HttpContext context,
    string name, bool corgi, PackageCatalog catalog,
    PackageRepository repository, PackumentCache cache,
    PackageCommitter committer, bool retry, CancellationToken ct) {
    CatalogEntry? entry = catalog.Find(name);
    string hash;
    string stored_base;
    DateTimeOffset modified;
    if (entry != null) {
      hash = CatalogEntry.FormatHash(corgi ? entry.CorgiHash : entry.FullHash);
      stored_base = entry.BaseUrl;
      modified = new DateTimeOffset(entry.ModifiedTicks, TimeSpan.Zero);
    } else if (!catalog.IsReady) {
      // Still loading: the document's metadata names the packuments.
      (string Corgi, string Full)? refs =
        await repository.HeadPackumentsAsync(name, ct);
      if (refs == null || refs.Value.Corgi.Length == 0) {
        return PackageNotFound(context);
      }

      hash = corgi ? refs.Value.Corgi : refs.Value.Full;
      stored_base = committer.StoredBaseUrl;
      modified = DateTimeOffset.UtcNow;
    } else {
      return PackageNotFound(context);
    }

    string base_url = context.GetBaseUrl();
    bool rebase = base_url != stored_base;
    // Rewritten documents differ per host, and so does their ETag.
    string host_hash = PackumentWriter.Hash(Encoding.UTF8.GetBytes(base_url));
    string etag = rebase ? $"\"{hash}-{host_hash[..8]}\"" : $"\"{hash}\"";
    HttpResponse response = context.Response;
    response.Headers.ETag = etag;
    response.Headers.CacheControl = C_PACKUMENT_CACHE_;
    response.Headers.LastModified = modified.ToString("R");
    response.Headers.Vary = "Accept-Encoding, Accept";
    response.Headers.AccessControlAllowOrigin = "*";
    HttpHelpers.SetSafeContentHeaders(response);
    if (HttpHelpers.IfNoneMatch(context.Request, etag) ||
        HttpHelpers.NotModifiedSince(context.Request, modified)) {
      return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    string key = StorageKeys.Packument(name, hash, corgi);
    byte[]? gzip = await cache.GetAsync(
                     key,
                     rebase ? base_url : "",
                     rebase
                       ? stored => PackumentWriter.RebaseGzip(
                         stored,
                         stored_base,
                         base_url
                       )
                       : null,
                     ct
                   );
    if (gzip == null) {
      // This instance has not caught up with a change yet and the packument
      // it knows was cleaned up: read the current document and try again.
      StoredDocument? current = await repository.LoadAsync(name, ct);
      catalog.Apply(name, current?.Document);
      if (!retry || current == null) {
        return PackageNotFound(context);
      }

      return await GetPackumentAsync(
               context,
               name,
               corgi,
               catalog,
               repository,
               cache,
               committer,
               false,
               ct
             );
    }

    response.ContentType =
      corgi ? HttpHelpers.C_CORGI_TYPE : "application/json";
    if (HttpMethods.IsHead(context.Request.Method)) {
      return Results.Empty;
    }

    if (HttpHelpers.AcceptsGzip(context.Request)) {
      response.Headers.ContentEncoding = "gzip";
      response.ContentLength = gzip.Length;
      await response.Body.WriteAsync(gzip, ct);
      return Results.Empty;
    }

    await using GZipStream decompressor = new(
      new MemoryStream(gzip, false),
      CompressionMode.Decompress
    );
    await decompressor.CopyToAsync(response.Body, ct);
    return Results.Empty;
  }

  private static async Task<IResult> GetVersionAsync(HttpContext context,
    string name, string spec, PackageCatalog catalog,
    PackageRepository repository, PackumentCache cache,
    CancellationToken ct) {
    string? version = null;
    CatalogEntry? entry = catalog.Find(name);
    if (entry != null) {
      version = entry.HasVersion(spec) ? spec : entry.ResolveTag(spec);
      if (version == null &&
          NpmVersion.Clean(spec) is { } clean &&
          entry.HasVersion(clean)) {
        version = clean;
      }
    } else if (!catalog.IsReady) {
      StoredDocument? stored = await repository.LoadAsync(name, ct);
      PackageDocument? document = stored?.Document;
      if (document == null) {
        return PackageNotFound(context);
      }

      version = document.Find(spec)?.Version ??
                (document.GetDistTags().TryGetValue(spec, out string? tagged)
                   ? tagged
                   : null);
    }

    if (version == null) {
      return Results.Json(
        $"version not found: {spec}",
        NoodleJson.S_CONTEXT.String,
        "application/json",
        StatusCodes.Status404NotFound
      );
    }

    string base_url = context.GetBaseUrl();
    // The package's state is part of the key: a version purged and uploaded
    // again has another manifest.
    string stamp = entry?.CacheStamp ?? Guid.NewGuid().ToString();
    string variant = $"manifest|{base_url}|{stamp}";
    byte[]? json = await cache.GetAsync(
                     StorageKeys.Version(name, version),
                     variant,
                     stored => {
                       VersionDocument document = NoodleJson.DeserializeGzip(
                         stored,
                         NoodleJson.S_CONTEXT
                                   .VersionDocument
                       )!;
                       return PackumentWriter.WriteVersion(
                         document.Record,
                         name,
                         base_url
                       );
                     },
                     ct
                   );
    if (json == null) {
      return PackageNotFound(context);
    }

    string etag = $"\"{PackumentWriter.Hash(json)}\"";
    HttpResponse response = context.Response;
    response.Headers.ETag = etag;
    response.Headers.CacheControl = "max-age=300";
    response.Headers.AccessControlAllowOrigin = "*";
    HttpHelpers.SetSafeContentHeaders(response);
    if (HttpHelpers.IfNoneMatch(context.Request, etag)) {
      return Results.StatusCode(StatusCodes.Status304NotModified);
    }

    return HttpMethods.IsHead(context.Request.Method)
             ? Results.Empty
             : Results.Bytes(json, "application/json");
  }

  private static async Task<IResult> GetTarballAsync(HttpContext context,
    string name, string file, PackageCatalog catalog, IObjectStore store,
    DownloadStatistics downloads, TarballCache tarballs,
    CancellationToken ct) {
    string prefix = PackageName.GetBareName(name) + "-";
    if (!file.StartsWith(prefix, StringComparison.Ordinal) ||
        file.Length <= prefix.Length + 4) {
      return HttpHelpers.NotFound();
    }

    string version = file[prefix.Length..^4];
    CatalogEntry? entry = catalog.Find(name);
    if (NpmVersion.Parse(version) == null ||
        (catalog.IsReady && entry?.HasVersion(version) != true)) {
      return HttpHelpers.NotFound();
    }

    string key = StorageKeys.Tarball(name, version);
    HttpResponse response = context.Response;
    bool head = HttpMethods.IsHead(context.Request.Method);
    // Only cache what the catalog vouches for (its state is in the key).
    string? stamp = catalog.IsReady ? entry?.CacheStamp : null;
    CachedTarball? cached = stamp == null ? null : tarballs.Find(key, stamp);
    if (cached != null) {
      SetTarballHeaders(response, cached.Info);
      if (cached.Info.ETag != null &&
          HttpHelpers.IfNoneMatch(context.Request, cached.Info.ETag)) {
        return Results.StatusCode(StatusCodes.Status304NotModified);
      }

      if (!head) {
        downloads.Record(name);
        await response.Body.WriteAsync(cached.Content, ct);
      }

      return Results.Empty;
    }

    if (head || context.Request.Headers.IfNoneMatch.Count > 0) {
      ObjectInfo? info = await store.HeadAsync(key, ct);
      if (info == null) {
        return HttpHelpers.NotFound();
      }

      SetTarballHeaders(response, info);
      if (info.ETag != null &&
          HttpHelpers.IfNoneMatch(context.Request, info.ETag)) {
        return Results.StatusCode(StatusCodes.Status304NotModified);
      }

      if (head) {
        return Results.Empty;
      }
    }

    await using ObjectReadStream? stream = await store.OpenReadAsync(key, ct);
    if (stream == null) {
      return HttpHelpers.NotFound();
    }

    SetTarballHeaders(response, stream.Info);
    downloads.Record(name);
    if (stamp != null && stream.Info.Size <= tarballs.MaxBytes) {
      byte[] content = new byte[stream.Info.Size];
      await stream.Stream.ReadExactlyAsync(content, ct);
      tarballs.Add(
        key,
        stamp,
        new CachedTarball {
          Content = content,
          Info = stream.Info
        }
      );
      await response.Body.WriteAsync(content, ct);
      return Results.Empty;
    }

    await stream.Stream.CopyToAsync(response.Body, ct);
    return Results.Empty;
  }

  private static void SetTarballHeaders(HttpResponse response,
                                        ObjectInfo info) {
    response.ContentType = "application/octet-stream";
    response.ContentLength = info.Size;
    if (info.ETag != null) {
      response.Headers.ETag = info.ETag;
    }

    response.Headers.LastModified = info.LastModified.ToString("R");
    response.Headers.CacheControl = C_TARBALL_CACHE_;
    response.Headers.AccessControlAllowOrigin = "*";
    HttpHelpers.SetSafeContentHeaders(response);
  }

  private static IResult PackageNotFound(HttpContext context) {
    // Not cached for long: the package may be uploaded any minute.
    context.Response.Headers.CacheControl =
      CacheControlHeaderValue.NoCacheString;
    return HttpHelpers.NotFound();
  }

  /// <summary>
  ///   Anything a client asks for that is not implemented shows up in the log.
  /// </summary>
  public static IResult Unhandled(HttpContext context,
                                  ILoggerFactory logger_factory) {
    logger_factory.CreateLogger("Noodle.Unhandled")
                  .LogWarning(
                    "No handler for {Method} {Path}{Query} " +
                    "(User-Agent: {UserAgent})",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString,
                    context.Request.Headers.UserAgent.ToString()
                  );
    return HttpHelpers.NotFound();
  }
}
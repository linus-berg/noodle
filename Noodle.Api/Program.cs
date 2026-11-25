using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Noodle.Api.Advisories;
using Noodle.Api.Catalog;
using Noodle.Api.Configuration;
using Noodle.Api.Endpoints;
using Noodle.Api.Json;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Api;

/// <summary>
///   Noodle: an offline npm registry backed only by S3 that speaks
///   registry.npmjs.org's protocol, so registry.npmjs.org can be pointed at
///   it.
/// </summary>
public class Program {
  public static async Task Main(string[] args) {
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.Services.Configure<NoodleOptions>(
      builder.Configuration.GetSection(NoodleOptions.C_SECTION)
    );
    builder.Services.Configure<StorageOptions>(
      builder.Configuration.GetSection(StorageOptions.C_SECTION)
    );
    builder.Services.AddSingleton<IObjectStore>(
      services => new S3ObjectStore(
        services.GetRequiredService<IOptions<StorageOptions>>().Value,
        services.GetRequiredService<ILogger<S3ObjectStore>>()
      )
    );
    builder.Services.AddSingleton<DownloadStatistics>();
    builder.Services.AddSingleton<AdvisoryStore>();
    builder.Services.AddSingleton<IVulnerabilityLookup>(
      services => services.GetRequiredService<AdvisoryStore>()
    );
    builder.Services.AddSingleton<PackageCatalog>();
    builder.Services.AddSingleton<ChangeFeed>();
    builder.Services.AddSingleton<PackageRepository>();
    builder.Services.AddSingleton<PackageCommitter>();
    builder.Services.AddSingleton<PackageIngestor>();
    builder.Services.AddSingleton<PackumentCache>();
    builder.Services.AddSingleton<TarballCache>();
    builder.Services.AddSingleton<CatalogSynchronizer>();
    builder.Services.AddHostedService<CatalogSyncService>();
    // npm sends audit requests gzipped.
    builder.Services.AddRequestDecompression();
    builder.Services.AddResponseCompression(
      options => {
        // Search and audit answers; packuments are stored gzipped and sent
        // as they are, tarballs are compressed already.
        options.EnableForHttps = true;
        options.Providers.Add<GzipCompressionProvider>();
        options.Providers.Add<BrotliCompressionProvider>();
        options.MimeTypes = [
          "application/json"
        ];
      }
    );
    builder.Services.Configure<GzipCompressionProviderOptions>(
      options => options.Level = CompressionLevel.Fastest
    );
    builder.Services.Configure<BrotliCompressionProviderOptions>(
      options => options.Level = CompressionLevel.Fastest
    );

    WebApplication app = builder.Build();
    LogConfiguration(app);

    app.Use(LogRequestsAsync);
    app.UseExceptionHandler(
      new ExceptionHandlerOptions {
        ExceptionHandler = HandleErrorAsync
      }
    );
    app.UseRequestDecompression();
    app.UseResponseCompression();
    app.MapMiscEndpoints();
    app.MapSearchEndpoints();
    app.MapAuditEndpoints();
    app.MapAdminEndpoints();
    app.MapRegistryEndpoints();

    await app.RunAsync();
  }

  /// <summary>
  ///   Errors as npm-style JSON. A busy or unreachable store is a 503 that
  ///   clients (and upload scripts) retry.
  /// </summary>
  private static async Task HandleErrorAsync(HttpContext context) {
    Exception? error = context.Features
                              .Get<Microsoft.AspNetCore.Diagnostics.
                                IExceptionHandlerFeature>()
                              ?.Error;
    bool storage = error is Amazon.Runtime.AmazonServiceException or
                     HttpRequestException;
    context.Response.StatusCode = storage
                                    ? StatusCodes.Status503ServiceUnavailable
                                    : StatusCodes.Status500InternalServerError;
    if (storage) {
      context.Response.Headers.RetryAfter = "5";
    }

    await context.Response.WriteAsJsonAsync(
      new ErrorResponse {
        Error = storage
                  ? "The storage is unavailable or busy; please retry."
                  : "Internal server error."
      },
      NoodleJson.S_CONTEXT.ErrorResponse
    );
  }

  private static void LogConfiguration(WebApplication app) {
    ILogger logger = app.Services
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("Noodle");
    NoodleOptions options = app.Services
                               .GetRequiredService<IOptions<NoodleOptions>>()
                               .Value;
    logger.LogInformation("Noodle {Version}", NoodleVersion.S_CURRENT);
    if (options.GetApiKeys().Count == 0) {
      logger.LogWarning(
        "No API key configured: uploads are disabled. Set Noodle__ApiKey " +
        "(or Noodle__ApiKeys__0, ...) to enable them"
      );
    }

    logger.LogInformation(
      "Tarball URLs: {Url}",
      string.IsNullOrWhiteSpace(options.PublicBaseUrl)
        ? "taken from each request's host"
        : options.PublicBaseUrl
    );
  }

  private static async Task LogRequestsAsync(HttpContext context,
                                             RequestDelegate next) {
    long start = Stopwatch.GetTimestamp();
    await next(context);
    if (context.Request.Path.StartsWithSegments("/health") ||
        !context.RequestServices
                .GetRequiredService<IOptions<NoodleOptions>>()
                .Value
                .LogRequests) {
      return;
    }

    context.RequestServices
           .GetRequiredService<ILoggerFactory>()
           .CreateLogger("Noodle.Requests")
           .LogInformation(
             "{Method} {Host}{Path}{Query} -> {Status} in {Elapsed:0} ms " +
             "({UserAgent})",
             context.Request.Method,
             context.Request.Host.Value,
             context.Request.Path,
             context.Request.QueryString,
             context.Response.StatusCode,
             Stopwatch.GetElapsedTime(start).TotalMilliseconds,
             context.Request.Headers.UserAgent.ToString()
           );
  }
}
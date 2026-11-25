using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Noodle.Api.Configuration;
using Noodle.Api.Json;

namespace Noodle.Api.Endpoints;

public static class HttpHelpers {
  public const string C_API_KEY_HEADER = "X-Api-Key";
  public const string C_CORGI_TYPE = "application/vnd.npm.install-v1+json";

  /// <summary>
  ///   The base URL clients reached this server under (scheme and host of the
  ///   request, after forwarded headers), or the configured public one.
  /// </summary>
  public static string GetBaseUrl(this HttpContext context) {
    NoodleOptions options = context.RequestServices
                                   .GetRequiredService<
                                     IOptions<NoodleOptions>>()
                                   .Value;
    if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl)) {
      return options.PublicBaseUrl.TrimEnd('/');
    }

    HttpRequest request = context.Request;
    return $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}"
      .TrimEnd('/');
  }

  public static IResult Error(int status, string message) {
    return Results.Json(
      new ErrorResponse {
        Error = message
      },
      NoodleJson.S_CONTEXT.ErrorResponse,
      "application/json",
      status
    );
  }

  public static IResult NotFound() {
    return Error(StatusCodes.Status404NotFound, "Not found");
  }

  /// <summary>Whether the client asked for the abbreviated packument.</summary>
  public static bool WantsCorgi(HttpRequest request) {
    foreach (string? value in request.Headers.Accept) {
      if (value == null) {
        continue;
      }

      foreach (string part in value.Split(',')) {
        string[] pieces = part.Split(';');
        if (!pieces[0]
             .Trim()
             .Equals(C_CORGI_TYPE, StringComparison.OrdinalIgnoreCase)) {
          continue;
        }

        bool excluded = pieces.Skip(1)
                              .Select(p => p.Trim())
                              .Any(
                                p => p.StartsWith(
                                       "q=",
                                       StringComparison.OrdinalIgnoreCase
                                     ) &&
                                     double.TryParse(
                                       p[2..],
                                       System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo
                                             .InvariantCulture,
                                       out double q
                                     ) &&
                                     q <= 0
                              );
        if (!excluded) {
          return true;
        }
      }
    }

    return false;
  }

  public static bool AcceptsGzip(HttpRequest request) {
    foreach (string? value in request.Headers.AcceptEncoding) {
      if (value == null) {
        continue;
      }

      foreach (string part in value.Split(',')) {
        string[] pieces = part.Split(';');
        string coding = pieces[0].Trim();
        if ((coding.Equals("gzip", StringComparison.OrdinalIgnoreCase) ||
             coding == "*") &&
            !pieces.Skip(1)
                   .Any(
                     p => p.Trim() is "q=0" or "q=0.0" or "q=0.00" or "q=0.000"
                   )) {
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  ///   Whether <c>If-None-Match</c> lists the ETag (weak or strong).
  /// </summary>
  public static bool IfNoneMatch(HttpRequest request, string etag) {
    string bare = etag.StartsWith("W/", StringComparison.Ordinal)
                    ? etag[2..]
                    : etag;
    foreach (string? value in request.Headers.IfNoneMatch) {
      if (value == null) {
        continue;
      }

      foreach (string part in value.Split(',')) {
        string candidate = part.Trim();
        if (candidate == "*") {
          return true;
        }

        if (candidate.StartsWith("W/", StringComparison.Ordinal)) {
          candidate = candidate[2..];
        }

        if (candidate == bare) {
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>
  ///   Whether <c>If-Modified-Since</c> is at or after the modification time.
  /// </summary>
  public static bool NotModifiedSince(HttpRequest request,
                                      DateTimeOffset modified) {
    if (request.Headers.IfNoneMatch.Count > 0) {
      // If-None-Match takes precedence (RFC 9110).
      return false;
    }

    DateTimeOffset? since = request.GetTypedHeaders().IfModifiedSince;
    return since != null &&
           modified.ToUnixTimeSeconds() <= since.Value.ToUnixTimeSeconds();
  }

  /// <summary>
  ///   The API key a client sent (<c>Authorization: Bearer</c> or
  ///   <c>X-Api-Key</c>).
  /// </summary>
  public static string? GetApiKey(HttpRequest request) {
    string header = request.Headers[C_API_KEY_HEADER].ToString();
    if (header.Length > 0) {
      return header;
    }

    string authorization = request.Headers.Authorization.ToString();
    return authorization.StartsWith(
             "Bearer ",
             StringComparison.OrdinalIgnoreCase
           )
             ? authorization["Bearer ".Length..].Trim()
             : null;
  }

  public static bool IsValidApiKey(string? key, IReadOnlyList<string> keys) {
    if (string.IsNullOrEmpty(key)) {
      return false;
    }

    // Compare hashes in constant time so timing reveals nothing about keys.
    byte[] given = SHA256.HashData(Encoding.UTF8.GetBytes(key));
    bool valid = false;
    foreach (string expected in keys) {
      byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
      valid |= CryptographicOperations.FixedTimeEquals(given, hash);
    }

    return valid;
  }

  /// <summary>
  ///   <c>null</c> when the request carries a valid API key, else the error to
  ///   send.
  /// </summary>
  public static IResult? CheckApiKey(HttpContext context) {
    IReadOnlyList<string> keys = context.RequestServices
                                        .GetRequiredService<
                                          IOptions<NoodleOptions>>()
                                        .Value
                                        .GetApiKeys();
    if (keys.Count == 0) {
      return Error(
        StatusCodes.Status403Forbidden,
        "Writing is disabled: no API key is configured on the server."
      );
    }

    string? key = GetApiKey(context.Request);
    if (!IsValidApiKey(key, keys)) {
      return Error(
        StatusCodes.Status401Unauthorized,
        string.IsNullOrEmpty(key)
          ? "An API key is required (Authorization: Bearer <key>)."
          : "The API key is not valid."
      );
    }

    return null;
  }

  /// <summary>
  ///   Requires a valid API key for the endpoint (uploads, deletes, ...).
  /// </summary>
  public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder)
    where TBuilder : IEndpointConventionBuilder {
    return builder.AddEndpointFilter(
      async (context, next) =>
        CheckApiKey(context.HttpContext) ?? await next(context)
    );
  }

  /// <summary>
  ///   Package-provided content: never let a browser run it on this origin.
  /// </summary>
  public static void SetSafeContentHeaders(HttpResponse response) {
    response.Headers.XContentTypeOptions = "nosniff";
    response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
  }
}
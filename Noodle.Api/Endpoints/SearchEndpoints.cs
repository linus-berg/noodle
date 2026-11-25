using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Noodle.Api.Catalog;
using Noodle.Api.Packages;

namespace Noodle.Api.Endpoints;

/// <summary>
///   <c>GET /-/v1/search</c>, as <c>npm search</c> and the website use it.
/// </summary>
public static class SearchEndpoints {
  private static readonly JsonWriterOptions S_WRITER_OPTIONS_ = new() {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
  };

  public static void MapSearchEndpoints(this IEndpointRouteBuilder app) {
    app.MapGet("/-/v1/search", Search);
  }

  private static IResult Search(HttpContext context, PackageCatalog catalog) {
    IQueryCollection query = context.Request.Query;
    string text = query["text"].ToString();
    if (text.Trim().Length == 0) {
      return Results.Json(
        new Dictionary<string, string> {
          ["error"] = "'text' query parameter is required",
          ["code"] = "ERR_TEXT_MISSING"
        },
        Json.NoodleJson.S_CONTEXT.DictionaryStringString,
        "application/json",
        StatusCodes.Status400BadRequest
      );
    }

    if (!catalog.IsReady) {
      return HttpHelpers.Error(
        StatusCodes.Status503ServiceUnavailable,
        "The search index is still loading."
      );
    }

    SearchResults results = catalog.Search(
      new SearchRequest {
        Text = text,
        From = GetInt(query, "from", 0),
        Size = GetInt(query, "size", 20),
        Quality = GetDouble(query, "quality", 0.65),
        Popularity = GetDouble(query, "popularity", 0.98),
        Maintenance = GetDouble(query, "maintenance", 0.5)
      }
    );
    ArrayBufferWriter<byte> buffer = new(16 * 1024);
    using (Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_)) {
      writer.WriteStartObject();
      writer.WriteStartArray("objects");
      foreach (SearchHit hit in results.Hits) {
        WriteHit(writer, hit);
      }

      writer.WriteEndArray();
      writer.WriteNumber("total", results.Total);
      writer.WriteString(
        "time",
        PackumentWriter.FormatTime(DateTimeOffset.UtcNow)
      );
      writer.WriteEndObject();
    }

    return Results.Bytes(buffer.WrittenSpan.ToArray(), "application/json");
  }

  private static void WriteHit(Utf8JsonWriter writer, SearchHit hit) {
    CatalogEntry entry = hit.Entry;
    SearchFields fields = entry.Search;
    string date = PackumentWriter.FormatTime(
      new DateTimeOffset(entry.LatestTicks, TimeSpan.Zero)
    );
    writer.WriteStartObject();
    writer.WriteStartObject("downloads");
    writer.WriteNumber("monthly", hit.Monthly);
    writer.WriteNumber("weekly", hit.Weekly);
    writer.WriteEndObject();
    writer.WriteString("dependents", "0");
    writer.WriteString("updated", date);
    writer.WriteNumber("searchScore", hit.Score);
    writer.WriteStartObject("package");
    writer.WriteString("name", entry.Name);
    writer.WriteStartArray("keywords");
    foreach (string keyword in fields.GetLines(SearchFields.C_KEYWORDS)) {
      writer.WriteStringValue(keyword);
    }

    writer.WriteEndArray();
    writer.WriteString("version", entry.Latest);
    string description = fields.GetString(SearchFields.C_DESCRIPTION);
    if (description.Length > 0) {
      writer.WriteString("description", description);
    }

    writer.WriteString("sanitized_name", entry.Name);
    List<(string Name, string Email)> author = People(
      fields,
      SearchFields.C_AUTHOR
    );
    List<(string Name, string Email)> maintainers = People(
      fields,
      SearchFields.C_MAINTAINERS
    );
    if (maintainers.Count == 0) {
      maintainers = author;
    }

    // There are no npm accounts here; the author stands in for the publisher.
    if (author.Count > 0) {
      writer.WriteStartObject("publisher");
      WritePerson(writer, author[0]);
      writer.WriteEndObject();
    }

    writer.WriteStartArray("maintainers");
    foreach ((string Name, string Email) person in maintainers) {
      writer.WriteStartObject();
      WritePerson(writer, person);
      writer.WriteEndObject();
    }

    writer.WriteEndArray();
    string license = fields.GetString(SearchFields.C_LICENSE);
    if (license.Length > 0) {
      writer.WriteString("license", license);
    }

    writer.WriteString("date", date);
    writer.WriteStartObject("links");
    WriteLink(writer, "homepage", fields.GetString(SearchFields.C_HOMEPAGE));
    WriteLink(
      writer,
      "repository",
      fields.GetString(SearchFields.C_REPOSITORY)
    );
    WriteLink(writer, "bugs", fields.GetString(SearchFields.C_BUGS));
    writer.WriteString("npm", $"https://www.npmjs.com/package/{entry.Name}");
    writer.WriteEndObject();
    writer.WriteEndObject();
    writer.WriteStartObject("score");
    writer.WriteNumber("final", hit.Score);
    writer.WriteStartObject("detail");
    writer.WriteNumber("popularity", Math.Round(hit.Popularity, 5));
    writer.WriteNumber("quality", Math.Round(hit.Quality, 5));
    writer.WriteNumber("maintenance", hit.Maintenance);
    writer.WriteEndObject();
    writer.WriteEndObject();
    writer.WriteStartObject("flags");
    writer.WriteNumber("insecure", hit.Insecure ? 1 : 0);
    if (entry.Latest.StartsWith("0.", StringComparison.Ordinal)) {
      writer.WriteBoolean("unstable", true);
    }

    writer.WriteEndObject();
    writer.WriteEndObject();
  }

  private static List<(string Name, string Email)> People(SearchFields fields,
    int segment) {
    List<(string, string)> people = [];
    foreach (string line in fields.GetLines(segment)) {
      int tab = line.IndexOf('\t');
      people.Add(tab < 0 ? (line, "") : (line[..tab], line[(tab + 1)..]));
    }

    return people;
  }

  private static void WritePerson(Utf8JsonWriter writer,
                                  (string Name, string Email) person) {
    if (person.Email.Length > 0) {
      writer.WriteString("email", person.Email);
    }

    writer.WriteString("username", person.Name);
  }

  private static void WriteLink(Utf8JsonWriter writer, string name,
                                string value) {
    if (value.Length > 0) {
      writer.WriteString(name, value);
    }
  }

  private static int GetInt(IQueryCollection query, string name, int fallback) {
    return int.TryParse(
             query[name].ToString(),
             NumberStyles.Integer,
             CultureInfo.InvariantCulture,
             out int value
           )
             ? value
             : fallback;
  }

  private static double GetDouble(IQueryCollection query, string name,
                                  double fallback) {
    return double.TryParse(
             query[name].ToString(),
             NumberStyles.Float,
             CultureInfo.InvariantCulture,
             out double value
           ) &&
           double.IsFinite(value)
             ? value
             : fallback;
  }
}
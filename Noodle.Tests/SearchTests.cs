using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noodle.Tests;

/// <summary>npm search with qualifiers.</summary>
public sealed class SearchTests {
  [Fact]
  public async Task
    SearchFindsByNameKeywordsAndDescriptionAndRanksExactNamesFirst() {
    await using NoodleFactory noodle = await SeedAsync();
    JsonElement result =
      await noodle.GetJsonAsync("/-/v1/search?text=react&size=10");
    Assert.Equal("react", Names(result)[0]);
    Assert.Contains("react-dom", Names(result));
    Assert.Contains("ui-kit", Names(result));
    Assert.Equal(Names(result).Count, result.GetProperty("total").GetInt32());

    JsonElement hit = result.GetProperty("objects")[0];
    Assert.Equal(
      "react",
      hit.GetProperty("package").GetProperty("sanitized_name").GetString()
    );
    Assert.Equal(
      "https://www.npmjs.com/package/react",
      hit.GetProperty("package")
         .GetProperty("links")
         .GetProperty("npm")
         .GetString()
    );
    Assert.True(hit.GetProperty("score").GetProperty("final").GetDouble() > 0);
    Assert.Equal(
      0,
      hit.GetProperty("flags").GetProperty("insecure").GetInt32()
    );
    Assert.Equal(
      JsonValueKind.Array,
      hit.GetProperty("package").GetProperty("maintainers").ValueKind
    );

    JsonElement no_boost = await noodle.GetJsonAsync(
                             "/-/v1/search?text=react%20boost-exact:false&size=10"
                           );
    Assert.Equal(Names(result).Count, Names(no_boost).Count);
  }

  [Fact]
  public async Task QualifiersFilter() {
    await using NoodleFactory noodle = await SeedAsync();
    Assert.Equal(
      ["ui-kit"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=keywords:widgets"))
    );
    Assert.Equal(
      ["react-dom", "ui-kit"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=keywords:widgets,dom"))
        .Order()
        .ToList()
    );
    Assert.Empty(
      Names(await noodle.GetJsonAsync("/-/v1/search?text=keywords:widgets+dom"))
    );
    Assert.Equal(
      ["@acme/react-thing"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=scope:acme"))
    );
    Assert.Equal(
      ["@acme/react-thing"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=react%20scope:@acme"))
    );
    Assert.Equal(
      ["ui-kit"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=author:jane"))
    );
    Assert.Equal(
      ["ui-kit"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=maintainer:bob"))
    );
    Assert.Equal(
      ["react-dom"],
      Names(await noodle.GetJsonAsync("/-/v1/search?text=react%20is:unstable"))
    );
    Assert.DoesNotContain(
      "react-dom",
      Names(await noodle.GetJsonAsync("/-/v1/search?text=react%20not:unstable"))
    );
  }

  [Fact]
  public async Task PagingAndEmptyText() {
    await using NoodleFactory noodle = await SeedAsync();
    JsonElement first =
      await noodle.GetJsonAsync("/-/v1/search?text=react&size=1&from=0");
    JsonElement second =
      await noodle.GetJsonAsync("/-/v1/search?text=react&size=1&from=1");
    Assert.Single(Names(first));
    Assert.NotEqual(Names(first)[0], Names(second)[0]);
    using HttpResponseMessage empty =
      await noodle.GetAsync("/-/v1/search?text=");
    Assert.Equal(System.Net.HttpStatusCode.BadRequest, empty.StatusCode);
  }

  [Fact]
  public async Task DownloadsRaiseThePopularityOfOtherwiseEqualPackages() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "twin-a",
        "1.0.0",
        new JsonObject {
          ["description"] = "twin"
        }
      )
    );
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "twin-b",
        "1.0.0",
        new JsonObject {
          ["description"] = "twin"
        }
      )
    );
    for (int i = 0; i < 20; i++) {
      using HttpResponseMessage download =
        await noodle.GetAsync("/twin-b/-/twin-b-1.0.0.tgz");
    }

    JsonElement result = await noodle.GetJsonAsync("/-/v1/search?text=twin");
    Assert.Equal("twin-b", Names(result)[0]);
    Assert.Equal(
      20,
      result.GetProperty("objects")[0]
            .GetProperty("downloads")
            .GetProperty("monthly")
            .GetInt64()
    );
  }

  private static async Task<NoodleFactory> SeedAsync() {
    NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "react",
        "18.2.0",
        new JsonObject {
          ["description"] =
            "React is a JavaScript library for building user interfaces.",
          ["keywords"] = new JsonArray("react")
        }
      )
    );
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "react-dom",
        "0.14.0",
        new JsonObject {
          ["description"] = "React package for working with the DOM.",
          ["keywords"] = new JsonArray("react", "dom")
        }
      )
    );
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "ui-kit",
        "2.0.0",
        new JsonObject {
          ["description"] = "Widgets built with react",
          ["keywords"] = new JsonArray("widgets"),
          ["author"] = "Jane Roe <jane@example.com>",
          ["maintainers"] = new JsonArray("Bob <bob@example.com>")
        }
      )
    );
    await noodle.UploadOkAsync(
      TestTarballs.Create("@acme/react-thing", "1.0.0")
    );
    await noodle.UploadOkAsync(TestTarballs.Create("unrelated", "1.0.0"));
    return noodle;
  }

  private static List<string> Names(JsonElement result) {
    return result.GetProperty("objects")
                 .EnumerateArray()
                 .Select(
                   o => o.GetProperty("package")
                         .GetProperty("name")
                         .GetString()!
                 )
                 .ToList();
  }
}
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noodle.Tests;

/// <summary>
///   Everything derived from a package depends on the set of versions
///   present, never on the order or time they arrived in.
/// </summary>
public sealed class ArrivalOrderTests {
  private static readonly string[] S_VERSIONS_ = [
    "0.9.0",
    "1.0.0-alpha.1",
    "1.0.0-alpha.10",
    "1.0.0-alpha.2",
    "1.0.0-beta",
    "1.0.0",
    "1.0.1+build.7",
    "1.10.0",
    "1.2.0",
    "2.0.0-rc.1",
    "2.0.0",
    "2.1.0-next.0",
    "3.0.0-beta.1"
  ];

  private static readonly string[] S_PRERELEASE_ONLY_ = [
    "0.1.0-dev.3",
    "0.1.0-dev.20",
    "0.0.1-a"
  ];

  [Fact]
  public async Task ShuffledSequentialUploadsGiveTheSameResult() {
    string? expected = null;
    for (int seed = 0; seed < 6; seed++) {
      await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
      foreach (byte[] tarball in Shuffle(Tarballs(), seed)) {
        await noodle.UploadOkAsync(tarball);
      }

      string observed = await DescribeAsync(noodle);
      expected ??= observed;
      Assert.Equal(expected, observed);
    }

    JsonElement summary = JsonDocument.Parse(expected!).RootElement;
    Assert.Equal(
      "2.0.0",
      summary.GetProperty("ordered")
             .GetProperty("dist-tags")
             .GetProperty("latest")
             .GetString()
    );
    Assert.Equal(
      "0.1.0-dev.20",
      summary.GetProperty("prerelease-only")
             .GetProperty("dist-tags")
             .GetProperty("latest")
             .GetString()
    );
  }

  [Fact]
  public async Task
    ParallelAndDeferredUploadsThroughTwoInstancesGiveTheSameResult() {
    string? expected = null;
    for (int seed = 0; seed < 4; seed++) {
      InMemoryObjectStore store = new() {
        WriteDelay = TimeSpan.FromMilliseconds(seed % 2)
      };
      await using NoodleFactory a = await new NoodleFactory(store)
                                          .WithSetting(
                                            "Noodle:Ingest:BatchDelayMilliseconds",
                                            "20"
                                          )
                                          .StartAsync();
      await using NoodleFactory b = await new NoodleFactory(store)
                                          .WithSetting(
                                            "Noodle:Ingest:BatchDelayMilliseconds",
                                            "20"
                                          )
                                          .StartAsync();
      List<byte[]> tarballs = Shuffle(Tarballs(), seed + 100);
      // Without ?wait=true, busy packages are acknowledged and batched.
      await Task.WhenAll(
        tarballs.Select(
          (t, i) => (i % 2 == 0 ? a : b).UploadOkAsync(t, wait: seed % 2 == 0)
        )
      );
      await a.Committer.DrainAsync(TimeSpan.FromSeconds(30));
      await b.Committer.DrainAsync(TimeSpan.FromSeconds(30));
      await a.Synchronizer.PollAsync(CancellationToken.None);
      await b.Synchronizer.PollAsync(CancellationToken.None);
      string from_a = await DescribeAsync(a);
      string from_b = await DescribeAsync(b);
      Assert.Equal(from_a, from_b);
      expected ??= from_a;
      Assert.Equal(expected, from_a);
    }

    // Same as one instance, one at a time.
    await using NoodleFactory single = await new NoodleFactory().StartAsync();
    foreach (byte[] tarball in Tarballs()) {
      await single.UploadOkAsync(tarball);
    }

    Assert.Equal(expected, await DescribeAsync(single));
  }

  [Fact]
  public async Task RemovingTheLatestVersionRecomputesEverything() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    foreach (byte[] tarball in Tarballs()) {
      await noodle.UploadOkAsync(tarball);
    }

    using HttpResponseMessage response = await noodle.SendWithKeyAsync(
                                           HttpMethod.Delete,
                                           "/-/noodle/package/ordered/2.0.0"
                                         );
    Assert.True(response.IsSuccessStatusCode);
    JsonElement full = await noodle.GetJsonAsync("/ordered");
    Assert.Equal(
      "1.10.0",
      full.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    Assert.Equal("Version 1.10.0", full.GetProperty("description").GetString());
    Assert.Equal("Readme of 1.10.0", full.GetProperty("readme").GetString());
    Assert.False(full.GetProperty("versions").TryGetProperty("2.0.0", out _));
    Assert.False(full.GetProperty("time").TryGetProperty("2.0.0", out _));
  }

  /// <summary>
  ///   Everything clients see of the test packages, times left out.
  /// </summary>
  private static async Task<string> DescribeAsync(NoodleFactory noodle) {
    JsonObject result = new();
    foreach (string name in new[] {
               "ordered",
               "prerelease-only"
             }) {
      JsonElement full = await noodle.GetJsonAsync(
                           "/" + name,
                           "application/json"
                         );
      JsonElement corgi = await noodle.GetJsonAsync(
                            "/" + name,
                            NoodleFactory.C_CORGI
                          );
      JsonObject described = new() {
        ["dist-tags"] =
          JsonNode.Parse(full.GetProperty("dist-tags").GetRawText()),
        ["versions"] = new JsonArray(
          full.GetProperty("versions")
              .EnumerateObject()
              .Select(p => (JsonNode?)p.Name)
              .ToArray()
        ),
        ["time-keys"] = new JsonArray(
          full.GetProperty("time")
              .EnumerateObject()
              .Select(p => (JsonNode?)p.Name)
              .ToArray()
        ),
        ["corgi-versions"] = new JsonArray(
          corgi.GetProperty("versions")
               .EnumerateObject()
               .Select(p => (JsonNode?)p.Name)
               .ToArray()
        ),
        ["manifests"] =
          JsonNode.Parse(full.GetProperty("versions").GetRawText()),
        ["corgi"] = JsonNode.Parse(corgi.GetProperty("versions").GetRawText())
      };
      foreach (string field in new[] {
                 "description",
                 "keywords",
                 "homepage",
                 "license",
                 "readme",
                 "readmeFilename"
               }) {
        if (full.TryGetProperty(field, out JsonElement value)) {
          described[field] = JsonNode.Parse(value.GetRawText());
        }
      }

      JsonElement search =
        await noodle.GetJsonAsync($"/-/v1/search?text={name}&size=1");
      JsonElement package =
        search.GetProperty("objects")[0].GetProperty("package");
      described["search"] = new JsonObject {
        ["name"] = package.GetProperty("name").GetString(),
        ["version"] = package.GetProperty("version").GetString(),
        ["description"] = package.GetProperty("description").GetString(),
        ["keywords"] =
          JsonNode.Parse(package.GetProperty("keywords").GetRawText())
      };
      result[name] = described;
    }

    return result.ToJsonString();
  }

  private static List<byte[]> Tarballs() {
    List<byte[]> tarballs = [];
    foreach (string version in S_VERSIONS_) {
      tarballs.Add(Tarball("ordered", version));
    }

    foreach (string version in S_PRERELEASE_ONLY_) {
      tarballs.Add(Tarball("prerelease-only", version));
    }

    return tarballs;
  }

  /// <summary>
  ///   Each version describes itself, so top-level fields show where they came
  ///   from.
  /// </summary>
  private static byte[] Tarball(string name, string version) {
    string clean = version.Split('+')[0];
    return TestTarballs.Create(
      name,
      version,
      new JsonObject {
        ["description"] = $"Version {clean}",
        ["keywords"] = new JsonArray($"k-{clean}"),
        ["homepage"] = $"https://example.com/{clean}",
        ["license"] = clean.StartsWith('2') ? "MIT" : "ISC"
      },
      new Dictionary<string, string> {
        ["README.md"] = $"Readme of {clean}"
      }
    );
  }

  private static List<byte[]> Shuffle(List<byte[]> items, int seed) {
    Random random = new(seed);
    return items.OrderBy(_ => random.Next()).ToList();
  }
}
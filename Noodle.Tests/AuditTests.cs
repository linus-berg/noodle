using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noodle.Api.Advisories;

namespace Noodle.Tests;

/// <summary>npm audit with and without imported advisory data.</summary>
public sealed class AuditTests {
  private const string C_LODASH_GHSA_ = "GHSA-35jh-r3h4-6jhm";

  [Fact]
  public async Task WithoutDataAuditsFailInsteadOfReportingNothing() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    using HttpResponseMessage bulk = await PostAsync(
                                       noodle,
                                       "/-/npm/v1/security/advisories/bulk",
                                       """{"lodash":["4.17.15"]}"""
                                     );
    Assert.Equal(HttpStatusCode.FailedDependency, bulk.StatusCode);
    Assert.Contains(
      "No security advisory data",
      await bulk.Content.ReadAsStringAsync()
    );
    using HttpResponseMessage quick = await PostAsync(
                                        noodle,
                                        "/-/npm/v1/security/audits/quick",
                                        "{}"
                                      );
    Assert.Equal(HttpStatusCode.FailedDependency, quick.StatusCode);
    JsonElement health = await noodle.GetJsonAsync("/health");
    Assert.False(
      health.GetProperty("advisories").GetProperty("imported").GetBoolean()
    );
  }

  [Fact]
  public async Task ImportedOsvDataAnswersBulkAndQuickAudits() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("lodash", "4.17.15"));
    JsonElement imported = await ImportAsync(
                             noodle,
                             Encoding.UTF8.GetBytes(Osv().ToJsonString())
                           );
    Assert.Equal(2, imported.GetProperty("count").GetInt32());

    using HttpResponseMessage bulk = await PostAsync(
                                       noodle,
                                       "/-/npm/v1/security/advisories/bulk",
                                       """{"lodash":["4.17.15"],"express":["5.1.0"],"left-pad":["1.0.0"]}""",
                                       gzip: true
                                     );
    Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
    JsonElement result = JsonDocument
                         .Parse(await bulk.Content.ReadAsStringAsync())
                         .RootElement;
    Assert.False(result.TryGetProperty("express", out _));
    JsonElement advisory =
      result.GetProperty("lodash").EnumerateArray().Single();
    Assert.Equal(
      OsvImporter.IdFromGhsa(C_LODASH_GHSA_),
      advisory.GetProperty("id").GetInt64()
    );
    Assert.Equal(
      $"https://github.com/advisories/{C_LODASH_GHSA_}",
      advisory.GetProperty("url").GetString()
    );
    Assert.Equal(
      "Command Injection in lodash",
      advisory.GetProperty("title").GetString()
    );
    Assert.Equal("high", advisory.GetProperty("severity").GetString());
    Assert.Equal(
      "<4.17.21",
      advisory.GetProperty("vulnerable_versions").GetString()
    );
    Assert.Equal("CWE-77", advisory.GetProperty("cwe")[0].GetString());
    Assert.Equal(
      7.2,
      advisory.GetProperty("cvss").GetProperty("score").GetDouble()
    );
    // The other advisory affects >=4.17.20 only, so it is not listed.
    Assert.Equal(1, result.GetProperty("lodash").GetArrayLength());

    using HttpResponseMessage quick = await PostAsync(
                                        noodle,
                                        "/-/npm/v1/security/audits/quick",
                                        """
                                        {"name":"app","version":"1.0.0","requires":{"lodash":"^4.17.15"},
                                         "dependencies":{"lodash":{"version":"4.17.15"},
                                                         "helper":{"version":"1.0.0","requires":{"lodash":"4.17.20"},
                                                                   "dependencies":{"lodash":{"version":"4.17.20","dev":true}}}}}
                                        """
                                      );
    JsonElement report = JsonDocument
                         .Parse(await quick.Content.ReadAsStringAsync())
                         .RootElement;
    JsonElement found = report.GetProperty("advisories")
                              .GetProperty(
                                OsvImporter.IdFromGhsa(C_LODASH_GHSA_)!.Value
                                           .ToString()
                              );
    Assert.Equal("lodash", found.GetProperty("module_name").GetString());
    Assert.Equal(2, found.GetProperty("findings").GetArrayLength());
    Assert.Equal(
      "helper>lodash",
      found.GetProperty("findings")[1].GetProperty("paths")[0].GetString()
    );
    Assert.Equal(
      2,
      report.GetProperty("metadata")
            .GetProperty("vulnerabilities")
            .GetProperty("high")
            .GetInt32()
    );
    Assert.Equal(
      1,
      report.GetProperty("metadata")
            .GetProperty("vulnerabilities")
            .GetProperty("moderate")
            .GetInt32()
    );

    JsonElement search = await noodle.GetJsonAsync("/-/v1/search?text=lodash");
    Assert.Equal(
      1,
      search.GetProperty("objects")[0]
            .GetProperty("flags")
            .GetProperty("insecure")
            .GetInt32()
    );
    JsonElement insecure =
      await noodle.GetJsonAsync("/-/v1/search?text=lodash%20not:insecure");
    Assert.Equal(0, insecure.GetProperty("total").GetInt32());

    JsonElement health = await noodle.GetJsonAsync("/health");
    Assert.True(
      health.GetProperty("advisories").GetProperty("imported").GetBoolean()
    );
  }

  [Fact]
  public async Task ZipArchivesAreImportedAndEachImportReplacesTheData() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    using MemoryStream zip = new();
    using (ZipArchive archive = new(zip, ZipArchiveMode.Create, true)) {
      foreach (JsonNode? advisory in Osv()) {
        ZipArchiveEntry entry = archive.CreateEntry($"{advisory!["id"]}.json");
        await using Stream stream = entry.Open();
        await stream.WriteAsync(
          Encoding.UTF8.GetBytes(advisory.ToJsonString())
        );
      }

      // Not npm: ignored.
      ZipArchiveEntry other = archive.CreateEntry("GHSA-2222-3333-4444.json");
      await using Stream other_stream = other.Open();
      await other_stream.WriteAsync(
        Encoding.UTF8.GetBytes(
          """{"id":"GHSA-2222-3333-4444","affected":[{"package":{"ecosystem":"PyPI","name":"x"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"1"}]}]}]}"""
        )
      );
    }

    JsonElement first = await ImportAsync(noodle, zip.ToArray());
    Assert.Equal(2, first.GetProperty("count").GetInt32());
    JsonArray single = new(Osv()[0]!.DeepClone());
    JsonElement second = await ImportAsync(
                           noodle,
                           Encoding.UTF8.GetBytes(single.ToJsonString())
                         );
    Assert.Equal(1, second.GetProperty("count").GetInt32());
    using HttpResponseMessage bad = await noodle.SendWithKeyAsync(
                                      HttpMethod.Put,
                                      "/-/noodle/advisories",
                                      new ByteArrayContent(
                                        "nonsense"u8.ToArray()
                                      )
                                    );
    Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
  }

  [Fact]
  public async Task EachAffectedRangeIsAnAdvisoryOfItsOwnLikeNpms() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await ImportAsync(
      noodle,
      """
        {"id":"GHSA-7q8q-rj6j-mhjq","summary":"Two ranges",
         "affected":[{"package":{"ecosystem":"npm","name":"axios"},"ranges":[{"type":"SEMVER","events":[{"introduced":"0.8.0"},{"fixed":"0.33.0"}]}]},
                     {"package":{"ecosystem":"npm","name":"axios"},"ranges":[{"type":"SEMVER","events":[{"introduced":"1.0.0"},{"fixed":"1.18.0"}]}],
                      "database_specific":{"last_known_affected_version_range":"<= 1.17.4"}}],
         "database_specific":{"severity":"MODERATE"}}
        """u8.ToArray()
    );
    using HttpResponseMessage one = await PostAsync(
                                      noodle,
                                      "/-/npm/v1/security/advisories/bulk",
                                      """{"axios":["1.10.0"]}"""
                                    );
    JsonElement only_new = JsonDocument
                           .Parse(await one.Content.ReadAsStringAsync())
                           .RootElement.GetProperty("axios");
    Assert.Equal(
      ">=1.0.0 <=1.17.4",
      only_new.EnumerateArray()
              .Single()
              .GetProperty("vulnerable_versions")
              .GetString()
    );
    using HttpResponseMessage both = await PostAsync(
                                       noodle,
                                       "/-/npm/v1/security/advisories/bulk",
                                       """{"axios":["0.21.0","1.10.0"]}"""
                                     );
    JsonElement two = JsonDocument.Parse(await both.Content.ReadAsStringAsync())
                                  .RootElement.GetProperty("axios");
    Assert.Equal(2, two.GetArrayLength());
    Assert.NotEqual(
      two[0].GetProperty("id").GetInt64(),
      two[1].GetProperty("id").GetInt64()
    );
    Assert.All(
      two.EnumerateArray(),
      a => Assert.True(a.GetProperty("id").GetInt64() < 9007199254740991)
    );
  }

  [Fact]
  public void CvssScoresAndIdsAreComputedLikeGitHubs() {
    Assert.Equal(
      7.2,
      CvssV3.BaseScore("CVSS:3.1/AV:N/AC:L/PR:H/UI:N/S:U/C:H/I:H/A:H")
    );
    Assert.Equal(
      7.4,
      CvssV3.BaseScore("CVSS:3.1/AV:N/AC:H/PR:N/UI:N/S:U/C:N/I:H/A:H")
    );
    Assert.Equal(
      5.3,
      CvssV3.BaseScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:L")
    );
    Assert.Equal(
      10.0,
      CvssV3.BaseScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:C/C:H/I:H/A:H")
    );
    Assert.Equal(
      6.1,
      CvssV3.BaseScore("CVSS:3.1/AV:N/AC:L/PR:N/UI:R/S:C/C:L/I:L/A:N")
    );
    Assert.Null(
      CvssV3.BaseScore(
        "CVSS:4.0/AV:N/AC:L/AT:N/PR:N/UI:N/VC:H/VI:H/VA:H/SC:N/SI:N/SA:N"
      )
    );
    long? id = OsvImporter.IdFromGhsa(C_LODASH_GHSA_);
    Assert.NotNull(id);
    Assert.True(id < 9007199254740991);
    Assert.Equal(id, OsvImporter.IdFromGhsa(C_LODASH_GHSA_.ToUpperInvariant()));
    Assert.NotEqual(id, OsvImporter.IdFromGhsa("GHSA-p6mc-m468-83gw"));
  }

  private static JsonArray Osv() {
    return JsonNode.Parse(
      """
      [
        {"id":"GHSA-35jh-r3h4-6jhm","summary":"Command Injection in lodash","details":"`lodash` versions prior to 4.17.21 are vulnerable.",
         "aliases":["CVE-2021-23337"],"published":"2021-05-06T16:05:51Z","modified":"2024-01-01T00:00:00Z",
         "severity":[{"type":"CVSS_V3","score":"CVSS:3.1/AV:N/AC:L/PR:H/UI:N/S:U/C:H/I:H/A:H"}],
         "affected":[{"package":{"ecosystem":"npm","name":"lodash"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"},{"fixed":"4.17.21"}]}]}],
         "references":[{"type":"WEB","url":"https://nvd.nist.gov/vuln/detail/CVE-2021-23337"}],
         "database_specific":{"severity":"HIGH","cwe_ids":["CWE-77","CWE-94"],"github_reviewed":true}},
        {"id":"GHSA-29mw-wpgm-hmr9","summary":"ReDoS in lodash",
         "severity":[{"type":"CVSS_V3","score":"CVSS:3.1/AV:N/AC:L/PR:N/UI:N/S:U/C:N/I:N/A:L"}],
         "affected":[{"package":{"ecosystem":"npm","name":"lodash"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"4.17.20"}]}],
                      "database_specific":{"last_known_affected_version_range":"<= 4.17.22"}}],
         "database_specific":{"severity":"MODERATE","cwe_ids":["CWE-400"]}},
        {"id":"GHSA-wwww-xxxx-xxxx","withdrawn":"2024-01-01T00:00:00Z",
         "affected":[{"package":{"ecosystem":"npm","name":"lodash"},"ranges":[{"type":"ECOSYSTEM","events":[{"introduced":"0"}]}]}]},
        {"id":"MAL-2024-1","affected":[{"package":{"ecosystem":"npm","name":"evil"},"versions":["1.0.0"]}]}
      ]
      """
    )!.AsArray();
  }

  private static async Task<JsonElement> ImportAsync(
    NoodleFactory noodle, byte[] data) {
    // (data is OSV JSON, a zip or a tar.gz)
    using HttpResponseMessage response = await noodle.SendWithKeyAsync(
                                           HttpMethod.Put,
                                           "/-/noodle/advisories?source=test",
                                           new ByteArrayContent(data)
                                         );
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, body);
    return JsonDocument.Parse(body).RootElement.Clone();
  }

  private static async Task<HttpResponseMessage> PostAsync(
    NoodleFactory noodle, string url,
    string json, bool gzip = false) {
    using HttpClient client = noodle.Client();
    byte[] body = Encoding.UTF8.GetBytes(json);
    ByteArrayContent content;
    if (gzip) {
      using MemoryStream compressed = new();
      using (GZipStream stream = new(
               compressed,
               CompressionLevel.Fastest,
               true
             )) {
        stream.Write(body);
      }

      content = new ByteArrayContent(compressed.ToArray());
      content.Headers.ContentEncoding.Add("gzip");
    } else {
      content = new ByteArrayContent(body);
    }

    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    return await client.PostAsync(url, content);
  }
}
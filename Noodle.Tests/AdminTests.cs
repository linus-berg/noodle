using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Noodle.Api.Storage;

namespace Noodle.Tests;

/// <summary>dist-tags and purging versions.</summary>
public sealed class AdminTests {
  [Fact]
  public async Task PinnedTagsStayUntilRemovedAndUploadsNeverMoveThem() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("tagged", "1.0.0"));
    await noodle.UploadOkAsync(TestTarballs.Create("tagged", "2.0.0-beta.1"));
    using (HttpResponseMessage set = await PutTagAsync(
                                       noodle,
                                       "@scope%2ftagged",
                                       "beta",
                                       "2.0.0-beta.1"
                                     )) {
      Assert.Equal(HttpStatusCode.NotFound, set.StatusCode);
    }

    using (HttpResponseMessage set = await PutTagAsync(
                                       noodle,
                                       "tagged",
                                       "beta",
                                       "2.0.0-beta.1"
                                     )) {
      Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    using (HttpResponseMessage pin = await PutTagAsync(
                                       noodle,
                                       "tagged",
                                       "latest",
                                       "1.0.0"
                                     )) {
      Assert.Equal(HttpStatusCode.OK, pin.StatusCode);
    }

    await noodle.UploadOkAsync(TestTarballs.Create("tagged", "3.0.0"));
    JsonElement tags = await noodle.GetJsonAsync("/-/package/tagged/dist-tags");
    Assert.Equal("1.0.0", tags.GetProperty("latest").GetString());
    Assert.Equal("2.0.0-beta.1", tags.GetProperty("beta").GetString());
    JsonElement corgi =
      await noodle.GetJsonAsync("/tagged", NoodleFactory.C_CORGI);
    Assert.Equal(
      "1.0.0",
      corgi.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    JsonElement beta = await noodle.GetJsonAsync("/tagged/beta");
    Assert.Equal("2.0.0-beta.1", beta.GetProperty("version").GetString());

    // Unpinning latest brings the automatic one back.
    using (HttpResponseMessage unpin = await noodle.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/-/package/tagged/dist-tags/latest"
                                       )) {
      Assert.Equal(HttpStatusCode.OK, unpin.StatusCode);
    }

    tags = await noodle.GetJsonAsync("/-/package/tagged/dist-tags");
    Assert.Equal("3.0.0", tags.GetProperty("latest").GetString());
    using (HttpResponseMessage again = await noodle.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/-/package/tagged/dist-tags/latest"
                                       )) {
      Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }
  }

  [Fact]
  public async Task TagsAreValidatedAndNeedTheKey() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("validated", "1.0.0"));
    foreach (string tag in new[] {
               "1.x",
               "v2",
               "-dash",
               "bad tag"
             }) {
      using HttpResponseMessage response = await PutTagAsync(
                                             noodle,
                                             "validated",
                                             Uri.EscapeDataString(tag),
                                             "1.0.0"
                                           );
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    using HttpResponseMessage missing =
      await PutTagAsync(noodle, "validated", "next", "9.9.9");
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    using HttpClient client = noodle.Client();
    using HttpResponseMessage anonymous = await client.PutAsync(
                                            "/-/package/validated/dist-tags/next",
                                            new StringContent(
                                              "\"1.0.0\"",
                                              Encoding.UTF8,
                                              "application/json"
                                            )
                                          );
    Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
  }

  [Fact]
  public async Task PurgingRemovesTheVersionItsFilesAndItsTags() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("purged", "1.0.0"));
    await noodle.UploadOkAsync(TestTarballs.Create("purged", "1.1.0"));
    using (HttpResponseMessage tag = await PutTagAsync(
                                       noodle,
                                       "purged",
                                       "old",
                                       "1.0.0"
                                     )) {
      Assert.Equal(HttpStatusCode.OK, tag.StatusCode);
    }

    using (HttpResponseMessage purge = await noodle.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/-/noodle/package/purged/1.0.0"
                                       )) {
      Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
    }

    Assert.DoesNotContain(
      StorageKeys.Tarball("purged", "1.0.0"),
      noodle.Store.Keys
    );
    Assert.DoesNotContain(
      StorageKeys.Version("purged", "1.0.0"),
      noodle.Store.Keys
    );
    JsonElement tags = await noodle.GetJsonAsync("/-/package/purged/dist-tags");
    Assert.False(tags.TryGetProperty("old", out _));
    using HttpResponseMessage tarball =
      await noodle.GetAsync("/purged/-/purged-1.0.0.tgz");
    Assert.Equal(HttpStatusCode.NotFound, tarball.StatusCode);

    // The same version can be uploaded again afterwards, even different bytes.
    using HttpResponseMessage again = await noodle.UploadAsync(
                                        TestTarballs.Create(
                                          "purged",
                                          "1.0.0",
                                          new System.Text.Json.Nodes.
                                            JsonObject {
                                              ["description"] = "new"
                                            }
                                        )
                                      );
    Assert.Equal(HttpStatusCode.Created, again.StatusCode);

    // Purging every version removes the package.
    using (HttpResponseMessage purge = await noodle.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/-/noodle/package/purged"
                                       )) {
      Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
    }

    using HttpResponseMessage gone = await noodle.GetAsync("/purged");
    Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    Assert.DoesNotContain(
      noodle.Store.Keys,
      k => k.Contains("purged") && !k.StartsWith(StorageKeys.C_CHANGES_PREFIX)
    );
  }

  private static async Task<HttpResponseMessage> PutTagAsync(
    NoodleFactory noodle,
    string name, string tag,
    string version) {
    StringContent body = new($"\"{version}\"", Encoding.UTF8);
    body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    return await noodle.SendWithKeyAsync(
             HttpMethod.Put,
             $"/-/package/{name}/dist-tags/{tag}",
             body
           );
  }
}
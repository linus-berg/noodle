using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Noodle.Api.Storage;

namespace Noodle.Tests;

/// <summary>
///   Uploads: statuses, duplicates, conflicts, damaged and hostile tarballs.
/// </summary>
public sealed class UploadTests {
  [Fact]
  public async Task DuplicatesAreFineAndDifferentBytesConflict() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] first = TestTarballs.Create("dup", "1.0.0");
    byte[] other = TestTarballs.Create(
      "dup",
      "1.0.0",
      new JsonObject {
        ["description"] = "different bytes"
      }
    );
    using HttpResponseMessage created = await noodle.UploadAsync(first);
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    using HttpResponseMessage again = await noodle.UploadAsync(first);
    Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    Assert.Equal(
      "exists",
      JsonDocument.Parse(await again.Content.ReadAsStringAsync())
                  .RootElement.GetProperty("status")
                  .GetString()
    );
    using HttpResponseMessage conflict = await noodle.UploadAsync(other);
    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    // The first one stays.
    using HttpResponseMessage download =
      await noodle.GetAsync("/dup/-/dup-1.0.0.tgz");
    Assert.Equal(first, await download.Content.ReadAsByteArrayAsync());
  }

  [Fact]
  public async Task UploadsNeedAValidKey() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Create("secured", "1.0.0");
    using HttpResponseMessage none = await noodle.UploadAsync(tarball, null);
    Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
    using HttpResponseMessage
      wrong = await noodle.UploadAsync(tarball, "wrong");
    Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

    await using NoodleFactory keyless = await new NoodleFactory()
                                              .WithSetting("Noodle:ApiKey", "")
                                              .StartAsync();
    using HttpResponseMessage disabled = await keyless.UploadAsync(tarball);
    Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
  }

  [Fact]
  public async Task SeveralKeysAreAccepted() {
    await using NoodleFactory noodle = await new NoodleFactory()
                                             .WithSetting(
                                               "Noodle:ApiKeys:0",
                                               "second-key"
                                             )
                                             .StartAsync();
    using HttpResponseMessage response = await noodle.UploadAsync(
                                           TestTarballs.Create("keys", "1.0.0"),
                                           "second-key"
                                         );
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
  }

  [Theory]
  [InlineData(1)]
  [InlineData(4)]
  [InlineData(8)]
  [InlineData(100)]
  public async Task TruncatedTarballsAreRejected(int cut) {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Real("express-5.1.0.tgz");
    using HttpResponseMessage response =
      await noodle.UploadAsync(tarball[..^cut]);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.DoesNotContain(noodle.Store.Keys, k => k.StartsWith("tarballs/"));
    Assert.DoesNotContain(noodle.Store.Keys, k => k.StartsWith("packages/"));
  }

  [Fact]
  public async Task CorruptedAndNonTarballsAreRejected() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Real("express-5.1.0.tgz");
    byte[] flipped = tarball.ToArray();
    flipped[flipped.Length / 2] ^= 0x55;
    foreach (byte[] bad in new[] {
               flipped,
               "not a tarball at all"u8.ToArray(),
               Gzip("just text, no tar"u8.ToArray()),
               TestTarballs.Pack(
                 new Dictionary<string, byte[]> {
                   ["package/index.js"] = "x"u8.ToArray()
                 }
               ),
               TestTarballs.Pack(
                 new Dictionary<string, byte[]> {
                   ["package/package.json"] = "{ not json"u8.ToArray()
                 }
               ),
               TestTarballs.Create("Bad Name", "1.0.0"),
               TestTarballs.Create("../evil", "1.0.0"),
               TestTarballs.Create("ok-name", "not-a-version")
             }) {
      using HttpResponseMessage response = await noodle.UploadAsync(bad);
      Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    Assert.DoesNotContain(noodle.Store.Keys, k => !k.StartsWith("catalog/"));
  }

  [Fact]
  public async Task GzipBombsAreRejected() {
    await using NoodleFactory noodle = await new NoodleFactory()
                                             .WithSetting(
                                               "Noodle:MaxUnpackedBytes",
                                               "10000000"
                                             )
                                             .StartAsync();
    // 64 MB of zeros as a file inside a valid tar: a few kB compressed.
    byte[] bomb = TestTarballs.Pack(
      new Dictionary<string, byte[]> {
        ["package/package.json"] =
          """{"name":"bomb","version":"1.0.0"}"""u8.ToArray(),
        ["package/zeros"] = new byte[64 * 1024 * 1024]
      }
    );
    Assert.True(bomb.Length < 200_000);
    using HttpResponseMessage response = await noodle.UploadAsync(bomb);
    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.Contains(
      "unpacks to more than",
      await response.Content.ReadAsStringAsync()
    );
  }

  [Fact]
  public async Task OversizedUploadsAre413() {
    await using NoodleFactory noodle = await new NoodleFactory()
                                             .WithSetting(
                                               "Noodle:MaxTarballBytes",
                                               "1000"
                                             )
                                             .StartAsync();
    using HttpResponseMessage response =
      await noodle.UploadAsync(TestTarballs.Real("express-5.1.0.tgz"));
    Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
  }

  [Fact]
  public async Task LinksAndOddEntriesAreIgnoredAndPathsCannotEscape() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Pack(
      new Dictionary<string, byte[]> {
        ["package/package.json"] =
          """{"name":"odd","version":"1.0.0","main":"../../etc/passwd"}"""u8
            .ToArray(),
        ["package/../../outside.js"] = "x"u8.ToArray(),
        ["package/README"] = "Odd readme"u8.ToArray()
      }
    );
    await noodle.UploadOkAsync(tarball);
    JsonElement version = await noodle.GetJsonAsync("/odd/1.0.0");
    Assert.False(version.TryGetProperty("types", out _));
    Assert.All(noodle.Store.Keys, k => Assert.DoesNotContain("..", k));
  }

  [Fact]
  public async Task TheFileNameIsIrrelevantAndBuildMetadataIsDroppedLikeNpm() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    JsonElement result =
      await noodle.UploadOkAsync(
        TestTarballs.Create("meta", "=v1.2.3+build.5")
      );
    Assert.Equal("1.2.3", result.GetProperty("version").GetString());
    using HttpResponseMessage conflict =
      await noodle.UploadAsync(TestTarballs.Create("meta", "1.2.3+other"));
    Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    Assert.Contains("tarballs/meta/1.2.3.tgz", noodle.Store.Keys);
  }

  [Fact]
  public async Task ParallelUploadsOfOneVersionStoreItOnce() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Create("race", "1.0.0");
    HttpResponseMessage[] responses = await Task.WhenAll(
                                        Enumerable.Range(0, 12)
                                                  .Select(
                                                    _ => noodle.UploadAsync(
                                                      tarball
                                                    )
                                                  )
                                      );
    Assert.All(
      responses,
      r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString())
    );
    Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
    JsonElement packument = await noodle.GetJsonAsync("/race");
    Assert.Single(packument.GetProperty("versions").EnumerateObject());
  }

  [Fact]
  public async Task ARepeatedUploadRepairsAVersionWhoseCommitWasLost() {
    InMemoryObjectStore store = new();
    await using NoodleFactory noodle =
      await new NoodleFactory(store).StartAsync();
    byte[] tarball = TestTarballs.Create("repair", "1.0.0");
    await noodle.UploadOkAsync(tarball);
    // Simulate a crash after the tarball and version document were written
    // but before the package document: delete the package document.
    await store.DeleteAsync(
      StorageKeys.Package("repair"),
      CancellationToken.None
    );
    noodle.Catalog.Remove("repair");
    using HttpResponseMessage response = await noodle.UploadAsync(tarball);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    JsonElement packument = await noodle.GetJsonAsync("/repair");
    Assert.Equal(
      "1.0.0",
      packument.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
  }

  private static byte[] Gzip(byte[] data) {
    using MemoryStream output = new();
    using (GZipStream gzip = new(output, CompressionLevel.Fastest, true)) {
      gzip.Write(data);
    }

    return output.ToArray();
  }
}
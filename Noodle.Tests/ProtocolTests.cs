using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noodle.Tests;

/// <summary>
///   The npm protocol: packuments, versions, tarballs, headers.
/// </summary>
public sealed class ProtocolTests {
  [Fact]
  public async Task ARealTarballBecomesAPackumentLikeTheRegistrysOne() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    JsonElement upload =
      await noodle.UploadOkAsync(TestTarballs.Real("express-5.1.0.tgz"));
    Assert.Equal("express", upload.GetProperty("name").GetString());
    Assert.Equal("5.1.0", upload.GetProperty("version").GetString());
    Assert.Equal("created", upload.GetProperty("status").GetString());

    JsonElement full = await noodle.GetJsonAsync(
                         "/express",
                         "application/json"
                       );
    Assert.Equal("express", full.GetProperty("_id").GetString());
    Assert.Equal(
      "5.1.0",
      full.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    JsonElement version = full.GetProperty("versions").GetProperty("5.1.0");
    JsonElement dist = version.GetProperty("dist");
    // The values registry.npmjs.org has for express@5.1.0.
    Assert.Equal(
      "sha512-DT9ck5YIRU+8GYzzU5kT3eHGA5iL+1Zd0EutOmTE9Dtk+Tvuzd23VBU+ec7HPNSTxXYO55gPV/hq4pSBJDjFpA==",
      dist.GetProperty("integrity").GetString()
    );
    Assert.Equal(
      "d31beaf715a0016f0d53f47d3b4d7acf28c75cc9",
      dist.GetProperty("shasum").GetString()
    );
    Assert.Equal(11, dist.GetProperty("fileCount").GetInt32());
    Assert.Equal(197014, dist.GetProperty("unpackedSize").GetInt64());
    Assert.Equal(
      "http://localhost/express/-/express-5.1.0.tgz",
      dist.GetProperty("tarball").GetString()
    );
    Assert.Equal("express@5.1.0", version.GetProperty("_id").GetString());
    Assert.Equal(
      "git+https://github.com/expressjs/express.git",
      version.GetProperty("repository").GetProperty("url").GetString()
    );
    Assert.Equal(
      "https://github.com/expressjs/express/issues",
      version.GetProperty("bugs").GetProperty("url").GetString()
    );
    Assert.Equal(
      "TJ Holowaychuk",
      version.GetProperty("author").GetProperty("name").GetString()
    );
    Assert.False(version.TryGetProperty("files", out _));
    Assert.False(version.TryGetProperty("readme", out _));
    Assert.False(version.GetProperty("_hasShrinkwrap").GetBoolean());
    Assert.Equal(
      JsonValueKind.Object,
      version.GetProperty("directories").ValueKind
    );
    Assert.StartsWith(
      "[![Express Logo]",
      full.GetProperty("readme").GetString()
    );
    Assert.Equal("Readme.md", full.GetProperty("readmeFilename").GetString());
    Assert.Equal(
      "Fast, unopinionated, minimalist web framework",
      full.GetProperty("description").GetString()
    );
    Assert.True(full.GetProperty("time").TryGetProperty("5.1.0", out _));

    JsonElement corgi =
      await noodle.GetJsonAsync("/express", NoodleFactory.C_CORGI);
    JsonElement corgi_version =
      corgi.GetProperty("versions").GetProperty("5.1.0");
    Assert.Equal(
      [
        "dependencies",
        "devDependencies",
        "dist",
        "engines",
        "funding",
        "name",
        "version"
      ],
      corgi_version.EnumerateObject()
                   .Select(p => p.Name)
                   .Order(StringComparer.Ordinal)
    );
    Assert.Equal(
      [
        "dist-tags",
        "modified",
        "name",
        "versions"
      ],
      corgi.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
    );
  }

  [Fact]
  public async Task PackumentsHaveNpmsHeadersAndAnswerConditionalRequests() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("headers", "1.0.0"));
    using HttpClient client = noodle.Client();
    using HttpRequestMessage request = new(HttpMethod.Get, "/headers");
    request.Headers.TryAddWithoutValidation("Accept", NoodleFactory.C_CORGI);
    request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
    using HttpResponseMessage response = await client.SendAsync(request);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(
      "application/vnd.npm.install-v1+json",
      response.Content.Headers.ContentType!.MediaType
    );
    Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
    Assert.Equal(
      "public, max-age=300",
      response.Headers.CacheControl!.ToString()
    );
    string etag = response.Headers.ETag!.ToString();

    using HttpRequestMessage again = new(HttpMethod.Get, "/headers");
    again.Headers.TryAddWithoutValidation("Accept", NoodleFactory.C_CORGI);
    again.Headers.TryAddWithoutValidation("If-None-Match", etag);
    using HttpResponseMessage not_modified = await client.SendAsync(again);
    Assert.Equal(HttpStatusCode.NotModified, not_modified.StatusCode);

    using HttpRequestMessage since = new(HttpMethod.Get, "/headers");
    since.Headers.IfModifiedSince = DateTimeOffset.UtcNow.AddMinutes(1);
    using HttpResponseMessage
      not_modified_since = await client.SendAsync(since);
    Assert.Equal(HttpStatusCode.NotModified, not_modified_since.StatusCode);

    // The full document has its own ETag.
    using HttpResponseMessage full =
      await noodle.GetAsync("/headers", "application/json");
    Assert.NotEqual(etag, full.Headers.ETag!.ToString());
    Assert.Equal(
      "application/json",
      full.Content.Headers.ContentType!.MediaType
    );
  }

  [Fact]
  public async Task UnknownPackagesAre404WithoutAskingS3() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    int gets = noodle.Store.GetCount;
    using HttpResponseMessage response =
      await noodle.GetAsync("/does-not-exist");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal(
      "{\"error\":\"Not found\"}",
      await response.Content.ReadAsStringAsync()
    );
    using HttpResponseMessage tarball =
      await noodle.GetAsync("/does-not-exist/-/does-not-exist-1.0.0.tgz");
    Assert.Equal(HttpStatusCode.NotFound, tarball.StatusCode);
    Assert.Equal(gets, noodle.Store.GetCount);
  }

  [Fact]
  public async Task ScopedPackagesWorkWithEncodedAndPlainSlashes() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] tarball = TestTarballs.Create("@scope/thing", "1.2.3");
    await noodle.UploadOkAsync(tarball);
    foreach (string path in new[] {
               "/@scope%2fthing",
               "/@scope%2Fthing",
               "/@scope/thing"
             }) {
      JsonElement packument =
        await noodle.GetJsonAsync(path, NoodleFactory.C_CORGI);
      Assert.Equal("@scope/thing", packument.GetProperty("name").GetString());
      Assert.Equal(
        "http://localhost/@scope/thing/-/thing-1.2.3.tgz",
        packument.GetProperty("versions")
                 .GetProperty("1.2.3")
                 .GetProperty("dist")
                 .GetProperty("tarball")
                 .GetString()
      );
    }

    using HttpResponseMessage download =
      await noodle.GetAsync("/@scope/thing/-/thing-1.2.3.tgz");
    Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    Assert.Equal(tarball, await download.Content.ReadAsByteArrayAsync());
    Assert.Equal(
      "application/octet-stream",
      download.Content.Headers.ContentType!.MediaType
    );
    Assert.Equal(tarball.Length, download.Content.Headers.ContentLength);

    JsonElement version = await noodle.GetJsonAsync("/@scope%2fthing/1.2.3");
    Assert.Equal("1.2.3", version.GetProperty("version").GetString());
    JsonElement latest = await noodle.GetJsonAsync("/@scope/thing/latest");
    Assert.Equal("1.2.3", latest.GetProperty("version").GetString());
    JsonElement tags =
      await noodle.GetJsonAsync("/-/package/@scope%2fthing/dist-tags");
    Assert.Equal("1.2.3", tags.GetProperty("latest").GetString());
  }

  [Fact]
  public async Task NamesAreCaseSensitive() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("JSONStream", "1.3.5"));
    await noodle.UploadOkAsync(TestTarballs.Create("jsonstream", "0.0.1"));
    JsonElement upper = await noodle.GetJsonAsync("/JSONStream");
    JsonElement lower = await noodle.GetJsonAsync("/jsonstream");
    Assert.Equal(
      "1.3.5",
      upper.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    Assert.Equal(
      "0.0.1",
      lower.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    using HttpResponseMessage missing = await noodle.GetAsync("/JsonStream");
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
  }

  [Fact]
  public async Task TarballUrlsFollowTheRequestHost() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("hosted", "1.0.0"));
    foreach (string host in new[] {
               "https://registry.npmjs.org/",
               "https://registry.yarnpkg.com/",
               "http://npm.corp.example:8080/"
             }) {
      using HttpClient client = noodle.Client(host);
      string json = await client.GetStringAsync("/hosted");
      JsonElement packument = JsonDocument.Parse(json).RootElement;
      Assert.Equal(
        $"{host}hosted/-/hosted-1.0.0.tgz",
        packument.GetProperty("versions")
                 .GetProperty("1.0.0")
                 .GetProperty("dist")
                 .GetProperty("tarball")
                 .GetString()
      );
    }
  }

  [Fact]
  public async Task UnknownVersionsAndTagsAre404() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("versions", "1.0.0"));
    using HttpResponseMessage response =
      await noodle.GetAsync("/versions/9.9.9");
    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    Assert.Equal(
      "\"version not found: 9.9.9\"",
      await response.Content.ReadAsStringAsync()
    );
    using HttpResponseMessage tarball =
      await noodle.GetAsync("/versions/-/versions-9.9.9.tgz");
    Assert.Equal(HttpStatusCode.NotFound, tarball.StatusCode);
  }

  [Fact]
  public async Task WritesFromNpmAreRefusedAndAccountsDoNotExist() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    using HttpClient client = noodle.Client();
    using HttpResponseMessage publish = await client.PutAsync(
                                          "/some-package",
                                          new StringContent("{}")
                                        );
    Assert.Equal(HttpStatusCode.MethodNotAllowed, publish.StatusCode);
    using HttpResponseMessage whoami = await client.GetAsync("/-/whoami");
    Assert.Equal(HttpStatusCode.Unauthorized, whoami.StatusCode);
    using HttpResponseMessage login = await client.PutAsync(
                                        "/-/user/org.couchdb.user:someone",
                                        new StringContent("{}")
                                      );
    Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    using HttpResponseMessage keys = await client.GetAsync("/-/npm/v1/keys");
    Assert.Equal(HttpStatusCode.NotFound, keys.StatusCode);
    using HttpResponseMessage attestations =
      await client.GetAsync("/-/npm/v1/attestations/x@1.0.0");
    Assert.Equal(HttpStatusCode.NotFound, attestations.StatusCode);
    Assert.Equal("{}", await client.GetStringAsync("/-/ping"));
  }

  [Fact]
  public async Task CorgiOmitsEmptyValuesAndFlagsInstallScripts() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Real("bcrypt-5.1.1.tgz"));
    JsonElement corgi =
      await noodle.GetJsonAsync("/bcrypt", NoodleFactory.C_CORGI);
    JsonElement version = corgi.GetProperty("versions").GetProperty("5.1.1");
    Assert.True(version.GetProperty("hasInstallScript").GetBoolean());
    Assert.False(version.TryGetProperty("directories", out _));
    Assert.False(version.TryGetProperty("_hasShrinkwrap", out _));
    JsonElement full = await noodle.GetJsonAsync("/bcrypt/5.1.1");
    Assert.False(full.TryGetProperty("hasInstallScript", out _));
    Assert.Equal(
      "node-pre-gyp install --fallback-to-build",
      full.GetProperty("scripts").GetProperty("install").GetString()
    );
  }

  [Fact]
  public async Task LegacyTarballLayoutsAreRead() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    // underscore 1.1.0's top folder is not "package/".
    await noodle.UploadOkAsync(TestTarballs.Real("underscore-1.1.0.tgz"));
    await noodle.UploadOkAsync(TestTarballs.Real("graceful-fs-1.0.0.tgz"));
    JsonElement underscore = await noodle.GetJsonAsync("/underscore/1.1.0");
    Assert.Equal("underscore", underscore.GetProperty("name").GetString());
    // 37 files and 3 folders.
    Assert.Equal(
      40,
      underscore.GetProperty("dist").GetProperty("fileCount").GetInt32()
    );
    JsonElement graceful = await noodle.GetJsonAsync("/graceful-fs");
    Assert.Equal(
      "1.0.0",
      graceful.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
  }

  [Fact]
  public async Task NormalizationFollowsNpmPublish() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(
      TestTarballs.Create(
        "normalized",
        "v1.0.0",
        new JsonObject {
          ["bin"] = "./cli.js",
          ["bundledDependencies"] = new JsonArray("dep"),
          ["dependencies"] = new JsonObject {
            ["dep"] = "^1.0.0",
            ["gitdep"] = "user/repo"
          },
          ["repository"] = "github:user/normalized",
          ["author"] = "Jane Doe <jane@example.com> (https://example.com)",
          ["_private"] = "removed",
          ["keywords"] = "one, two"
        },
        new Dictionary<string, string> {
          ["cli.js"] = "#!/usr/bin/env node\n",
          ["binding.gyp"] = "{}",
          ["server.js"] = "",
          ["index.d.ts"] = "export {};",
          ["README.md"] = "# normalized\n\nA test package.\n"
        }
      )
    );
    JsonElement version = await noodle.GetJsonAsync("/normalized/1.0.0");
    Assert.Equal("1.0.0", version.GetProperty("version").GetString());
    Assert.Equal(
      "cli.js",
      version.GetProperty("bin").GetProperty("normalized").GetString()
    );
    Assert.Equal(
      "dep",
      version.GetProperty("bundleDependencies")[0].GetString()
    );
    Assert.False(version.TryGetProperty("bundledDependencies", out _));
    Assert.Equal(
      "github:user/repo",
      version.GetProperty("dependencies").GetProperty("gitdep").GetString()
    );
    Assert.Equal(
      "git+https://github.com/user/normalized.git",
      version.GetProperty("repository").GetProperty("url").GetString()
    );
    Assert.Equal(
      "https://github.com/user/normalized#readme",
      version.GetProperty("homepage").GetString()
    );
    Assert.Equal(
      "https://github.com/user/normalized/issues",
      version.GetProperty("bugs").GetProperty("url").GetString()
    );
    Assert.Equal(
      "jane@example.com",
      version.GetProperty("author").GetProperty("email").GetString()
    );
    Assert.Equal(
      "https://example.com",
      version.GetProperty("author").GetProperty("url").GetString()
    );
    Assert.Equal(
      "node-gyp rebuild",
      version.GetProperty("scripts").GetProperty("install").GetString()
    );
    Assert.Equal(
      "node server.js",
      version.GetProperty("scripts").GetProperty("start").GetString()
    );
    Assert.True(version.GetProperty("gypfile").GetBoolean());
    Assert.Equal("./index.d.ts", version.GetProperty("types").GetString());
    Assert.False(version.TryGetProperty("readmeFilename", out _));
    Assert.False(version.TryGetProperty("_private", out _));
    Assert.Equal(2, version.GetProperty("keywords").GetArrayLength());
    JsonElement corgi = await noodle.GetJsonAsync(
                          "/normalized",
                          NoodleFactory.C_CORGI
                        );
    Assert.True(
      corgi.GetProperty("versions")
           .GetProperty("1.0.0")
           .GetProperty("hasInstallScript")
           .GetBoolean()
    );
  }

  [Fact]
  public async Task SmallTarballsAreServedFromMemoryUntilThePackageChanges() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    byte[] first = TestTarballs.Create("cached", "1.0.0");
    await noodle.UploadOkAsync(first);
    using (HttpResponseMessage download =
           await noodle.GetAsync("/cached/-/cached-1.0.0.tgz")) {
      Assert.Equal(first, await download.Content.ReadAsByteArrayAsync());
    }

    int gets = noodle.Store.GetCount;
    for (int i = 0; i < 5; i++) {
      using HttpResponseMessage again =
        await noodle.GetAsync("/cached/-/cached-1.0.0.tgz");
      Assert.Equal(first, await again.Content.ReadAsByteArrayAsync());
    }

    Assert.Equal(gets, noodle.Store.GetCount);

    // Purged and uploaded again with other bytes: never the old ones.
    using (HttpResponseMessage purge = await noodle.SendWithKeyAsync(
                                         HttpMethod.Delete,
                                         "/-/noodle/package/cached/1.0.0"
                                       )) {
      Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
    }

    byte[] second = TestTarballs.Create(
      "cached",
      "1.0.0",
      new JsonObject {
        ["description"] = "other"
      }
    );
    await noodle.UploadOkAsync(second);
    using HttpResponseMessage fresh =
      await noodle.GetAsync("/cached/-/cached-1.0.0.tgz");
    Assert.Equal(second, await fresh.Content.ReadAsByteArrayAsync());
  }
}
using System.Net;
using System.Text.Json;
using Noodle.Api.Json;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Tests;

/// <summary>
///   Several instances sharing one bucket, snapshots and recovery.
/// </summary>
public sealed class SyncTests {
  [Fact]
  public async Task UploadsThroughOneInstanceReachTheOthers() {
    InMemoryObjectStore store = new();
    await using NoodleFactory a = await new NoodleFactory(store).StartAsync();
    await using NoodleFactory b = await new NoodleFactory(store).StartAsync();
    await a.UploadOkAsync(TestTarballs.Create("shared", "1.0.0"));
    // Not in b's catalog before it polls, so b answers 404 without S3.
    using (HttpResponseMessage before = await b.GetAsync("/shared")) {
      Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
    }

    await b.Synchronizer.PollAsync(CancellationToken.None);
    JsonElement packument =
      await b.GetJsonAsync("/shared", NoodleFactory.C_CORGI);
    Assert.Equal(
      "1.0.0",
      packument.GetProperty("dist-tags").GetProperty("latest").GetString()
    );

    await b.UploadOkAsync(TestTarballs.Create("shared", "2.0.0"));
    // a still knows the old packument and serves it until it polls ...
    JsonElement stale = await a.GetJsonAsync("/shared");
    Assert.Equal(
      "1.0.0",
      stale.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    await a.Synchronizer.PollAsync(CancellationToken.None);
    JsonElement fresh = await a.GetJsonAsync("/shared");
    Assert.Equal(
      "2.0.0",
      fresh.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
  }

  [Fact]
  public async Task ConcurrentUploadsThroughSeveralInstancesKeepEveryVersion() {
    InMemoryObjectStore store = new() {
      WriteDelay = TimeSpan.FromMilliseconds(2)
    };
    await using NoodleFactory a = await new NoodleFactory(store).StartAsync();
    await using NoodleFactory b = await new NoodleFactory(store).StartAsync();
    await Task.WhenAll(
      Enumerable.Range(0, 40)
                .Select(
                  i => (i % 2 == 0 ? a : b).UploadOkAsync(
                    TestTarballs.Create("contended", $"1.0.{i}")
                  )
                )
    );
    await a.Synchronizer.PollAsync(CancellationToken.None);
    await b.Synchronizer.PollAsync(CancellationToken.None);
    foreach (NoodleFactory instance in new[] {
               a,
               b
             }) {
      JsonElement packument = await instance.GetJsonAsync("/contended");
      Assert.Equal(
        40,
        packument.GetProperty("versions").EnumerateObject().Count()
      );
    }

    // Lost write races happened and were retried.
    Assert.True(store.ConflictCount > 0);
    Assert.True(a.Committer.Conflicts + b.Committer.Conflicts > 0);
  }

  [Fact]
  public async Task BusyPackagesAreCommittedInBatches() {
    await using NoodleFactory noodle = await new NoodleFactory()
                                             .WithSetting(
                                               "Noodle:Ingest:BatchDelayMilliseconds",
                                               "100"
                                             )
                                             .StartAsync();
    const int C_VERSIONS = 120;
    for (int i = 0; i < C_VERSIONS; i++) {
      // One at a time, like a sequential bulk load.
      await noodle.UploadOkAsync(
        TestTarballs.Create("bulk", $"1.0.{i}"),
        wait: false
      );
    }

    await noodle.Committer.DrainAsync(TimeSpan.FromSeconds(30));
    JsonElement packument = await noodle.GetJsonAsync("/bulk");
    Assert.Equal(
      C_VERSIONS,
      packument.GetProperty("versions").EnumerateObject().Count()
    );
    Assert.True(
      noodle.Committer.Commits < C_VERSIONS / 4,
      $"{noodle.Committer.Commits} commits for {C_VERSIONS} versions"
    );
    Assert.DoesNotContain(
      noodle.Store.Keys,
      k => k.StartsWith(StorageKeys.C_PENDING_PREFIX)
    );
  }

  [Fact]
  public async Task
    AcknowledgedVersionsLeftBehindAreCommittedByAnotherInstance() {
    InMemoryObjectStore store = new();
    await using NoodleFactory survivor = await new NoodleFactory(store)
                                               .WithSetting(
                                                 "Noodle:Ingest:RecoverAfterSeconds",
                                                 "1"
                                               )
                                               .StartAsync();
    await survivor.UploadOkAsync(TestTarballs.Create("orphaned", "1.0.0"));
    // What a crashed instance leaves behind: tarball, version document and
    // pending marker, but no commit.
    byte[] version = store.Read(StorageKeys.Version("orphaned", "1.0.0"))!;
    VersionDocument document = NoodleJson.DeserializeGzip(
      version,
      NoodleJson.S_CONTEXT.VersionDocument
    )!;
    VersionDocument lost = new() {
      Name = "orphaned",
      Record = new VersionRecord {
        Version = "1.1.0",
        Arrived = DateTimeOffset.UtcNow,
        Integrity = document.Record.Integrity,
        Shasum = document.Record.Shasum,
        Size = document.Record.Size,
        FileCount = document.Record.FileCount,
        UnpackedSize = document.Record.UnpackedSize,
        Manifest = new RawJson(
          """{"name":"orphaned","version":"1.1.0","_id":"orphaned@1.1.0"}"""u8
            .ToArray()
        )
      }
    };
    await store.PutAsync(
      StorageKeys.Version("orphaned", "1.1.0"),
      NoodleJson.SerializeGzip(lost, NoodleJson.S_CONTEXT.VersionDocument),
      new PutOptions(),
      CancellationToken.None
    );
    await store.PutAsync(
      StorageKeys.Pending("orphaned", "1.1.0"),
      Array.Empty<byte>(),
      new PutOptions(),
      CancellationToken.None
    );
    store.Age(
      StorageKeys.Pending("orphaned", "1.1.0"),
      TimeSpan.FromMinutes(5)
    );

    Assert.Equal(
      1,
      await survivor.Synchronizer.RecoverPendingAsync(CancellationToken.None)
    );
    JsonElement packument = await survivor.GetJsonAsync("/orphaned");
    Assert.Equal(
      "1.1.0",
      packument.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    Assert.DoesNotContain(StorageKeys.Pending("orphaned", "1.1.0"), store.Keys);
  }

  [Fact]
  public async Task NewInstancesLoadTheSnapshotAndReplayLaterChanges() {
    InMemoryObjectStore store = new();
    await using (NoodleFactory first =
                 await new NoodleFactory(store).StartAsync()) {
      await first.UploadOkAsync(TestTarballs.Create("snap-one", "1.0.0"));
      using (HttpResponseMessage download =
             await first.GetAsync("/snap-one/-/snap-one-1.0.0.tgz")) {
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
      }

      await first.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
      // After the snapshot: must come from the change log.
      await first.UploadOkAsync(TestTarballs.Create("snap-two", "1.0.0"));
      using (HttpResponseMessage download =
             await first.GetAsync("/snap-one/-/snap-one-1.0.0.tgz")) {
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
      }

      await first.Synchronizer.FlushDownloadsAsync(CancellationToken.None);
    }

    Assert.Contains(StorageKeys.C_SNAPSHOT, store.Keys);
    await using NoodleFactory second =
      await new NoodleFactory(store).StartAsync();
    Assert.Equal(2, second.Catalog.Count);
    JsonElement hit =
      (await second.GetJsonAsync("/-/v1/search?text=snap-one")).GetProperty(
        "objects"
      )[0];
    // One download in the snapshot, one in the change log; neither twice.
    Assert.Equal(
      2,
      hit.GetProperty("downloads").GetProperty("monthly").GetInt64()
    );
    Assert.NotNull(second.Catalog.Find("snap-two"));
  }

  [Fact]
  public async Task TheCatalogIsRebuiltWhenTheSnapshotIsMissingOrBroken() {
    InMemoryObjectStore store = new();
    await using (NoodleFactory first =
                 await new NoodleFactory(store).StartAsync()) {
      await first.UploadOkAsync(TestTarballs.Create("rebuild-one", "1.0.0"));
      await first.UploadOkAsync(TestTarballs.Create("rebuild-two", "1.0.0"));
      await first.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
    }

    store.Replace(StorageKeys.C_SNAPSHOT, "garbage"u8.ToArray());
    await using NoodleFactory second =
      await new NoodleFactory(store).StartAsync();
    Assert.Equal(2, second.Catalog.Count);
    for (int i = 0;
         i < 100 && store.Read(StorageKeys.C_SNAPSHOT)!.Length == 7;
         i++) {
      await Task.Delay(50);
    }

    Assert.NotEqual("garbage"u8.ToArray(), store.Read(StorageKeys.C_SNAPSHOT));
  }

  [Fact]
  public async Task RebuildFromVersionsRecreatesLostPackageDocuments() {
    InMemoryObjectStore store = new();
    await using NoodleFactory noodle =
      await new NoodleFactory(store).StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("lost", "1.0.0"));
    await noodle.UploadOkAsync(TestTarballs.Create("lost", "1.1.0"));
    await noodle.UploadOkAsync(TestTarballs.Create("kept", "1.0.0"));
    // The package document and packuments disappear; versions stay.
    foreach (string key in store.Keys.Where(
               k => k.Contains("lost") &&
                    (k.StartsWith("packages/") || k.StartsWith("packuments/"))
             )) {
      await store.DeleteAsync(key, CancellationToken.None);
    }

    using HttpResponseMessage response = await noodle.SendWithKeyAsync(
                                           HttpMethod.Post,
                                           "/-/noodle/rebuild?scope=versions&wait=true"
                                         );
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    JsonElement packument = await noodle.GetJsonAsync("/lost");
    Assert.Equal(
      2,
      packument.GetProperty("versions").EnumerateObject().Count()
    );
    Assert.Equal(2, noodle.Catalog.Count);
  }

  [Fact]
  public async Task RegeneratingPackumentsKeepsTheirContent() {
    await using NoodleFactory noodle = await new NoodleFactory().StartAsync();
    await noodle.UploadOkAsync(TestTarballs.Create("regenerated", "1.0.0"));
    JsonElement before = await noodle.GetJsonAsync(
                           "/regenerated",
                           NoodleFactory.C_CORGI
                         );
    using HttpResponseMessage response = await noodle.SendWithKeyAsync(
                                           HttpMethod.Post,
                                           "/-/noodle/rebuild?scope=packuments&wait=true"
                                         );
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    JsonElement after = await noodle.GetJsonAsync(
                          "/regenerated",
                          NoodleFactory.C_CORGI
                        );
    Assert.Equal(
      before.GetProperty("versions").GetRawText(),
      after.GetProperty("versions").GetRawText()
    );
  }

  [Fact]
  public async Task SnapshotsRemoveChangesOlderThanTheRetentionPeriod() {
    InMemoryObjectStore store = new();
    await using NoodleFactory noodle = await new NoodleFactory(store)
                                             .WithSetting(
                                               "Noodle:Sync:ChangeRetentionHours",
                                               "1"
                                             )
                                             .StartAsync();
    long old_ticks = DateTime.UtcNow.AddHours(-3).Ticks;
    string old_key =
      $"{StorageKeys.C_CHANGES_PREFIX}{old_ticks:D19}-{Guid.NewGuid():N}-p-old";
    await store.PutAsync(
      old_key,
      Array.Empty<byte>(),
      new PutOptions(),
      CancellationToken.None
    );
    await noodle.UploadOkAsync(TestTarballs.Create("recent", "1.0.0"));
    await noodle.Synchronizer.WriteSnapshotAsync(CancellationToken.None);
    List<string> changes = store.Keys
                                .Where(
                                  k => k.StartsWith(
                                    StorageKeys.C_CHANGES_PREFIX
                                  )
                                )
                                .ToList();
    Assert.DoesNotContain(old_key, changes);
    Assert.Contains(changes, k => k.EndsWith("-p-recent"));
  }

  [Fact]
  public async Task InstallsWorkFromS3WhileTheCatalogIsLoading() {
    InMemoryObjectStore store = new();
    await using (NoodleFactory first =
                 await new NoodleFactory(store).StartAsync()) {
      await first.UploadOkAsync(TestTarballs.Create("@early/bird", "1.0.0"));
    }

    TaskCompletionSource release = new();
    store.BeforeOpen = key =>
      key == StorageKeys.C_SNAPSHOT ? release.Task : Task.CompletedTask;
    await using NoodleFactory second = new(store);
    _ = second.Server;
    Assert.False(second.Catalog.IsReady);
    using (HttpResponseMessage ready = await second.GetAsync("/health/ready")) {
      Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    JsonElement packument = await second.GetJsonAsync(
                              "/@early%2fbird",
                              NoodleFactory.C_CORGI
                            );
    Assert.Equal(
      "1.0.0",
      packument.GetProperty("dist-tags").GetProperty("latest").GetString()
    );
    JsonElement version = await second.GetJsonAsync("/@early/bird/latest");
    Assert.Equal("1.0.0", version.GetProperty("version").GetString());
    using (HttpResponseMessage tarball =
           await second.GetAsync("/@early/bird/-/bird-1.0.0.tgz")) {
      Assert.Equal(HttpStatusCode.OK, tarball.StatusCode);
    }

    using (HttpResponseMessage missing = await second.GetAsync("/not-there")) {
      Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    Assert.False(second.Catalog.IsReady);
    release.SetResult();
    await second.Catalog.WhenReady.WaitAsync(TimeSpan.FromSeconds(10));
  }
}
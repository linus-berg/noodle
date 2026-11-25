using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Noodle.Api;
using Noodle.Api.Catalog;
using Noodle.Api.Packages;
using Noodle.Api.Storage;

namespace Noodle.Tests;

/// <summary>
///   Runs Noodle in-process on an <see cref="InMemoryObjectStore" />. Several
///   factories sharing one store behave like several instances.
/// </summary>
public sealed class NoodleFactory : WebApplicationFactory<Program> {
  public const string C_API_KEY = "test-api-key";

  public const string C_CORGI =
    "application/vnd.npm.install-v1+json; q=1.0, application/json; q=0.8, */*";

  private readonly Dictionary<string, string?> settings_ = new();

  public NoodleFactory(InMemoryObjectStore? store = null) {
    Store = store ?? new InMemoryObjectStore();
  }

  public InMemoryObjectStore Store { get; }

  public PackageCatalog Catalog =>
    Services.GetRequiredService<PackageCatalog>();

  public CatalogSynchronizer Synchronizer =>
    Services.GetRequiredService<CatalogSynchronizer>();

  public PackageCommitter Committer =>
    Services.GetRequiredService<PackageCommitter>();

  public NoodleFactory WithSetting(string key, string? value) {
    settings_[key] = value;
    return this;
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder) {
    builder.UseEnvironment("Testing");
    builder.UseSetting("Noodle:ApiKey", C_API_KEY);
    builder.UseSetting("Noodle:LogRequests", "false");
    // Tests drive synchronization explicitly.
    builder.UseSetting("Noodle:Sync:PollIntervalSeconds", "0");
    foreach (KeyValuePair<string, string?> setting in settings_) {
      builder.UseSetting(setting.Key, setting.Value);
    }

    builder.ConfigureTestServices(
      services => { services.AddSingleton<IObjectStore>(Store); }
    );
  }

  /// <summary>
  ///   Starts the server and waits until the catalog is loaded.
  /// </summary>
  public async Task<NoodleFactory> StartAsync() {
    _ = Server;
    await Catalog.WhenReady.WaitAsync(TimeSpan.FromSeconds(30));
    return this;
  }

  public HttpClient Client(string? base_address = null) {
    HttpClient client = CreateClient(
      new WebApplicationFactoryClientOptions {
        BaseAddress = new Uri(base_address ?? "http://localhost/"),
        AllowAutoRedirect = false
      }
    );
    return client;
  }

  public async Task<HttpResponseMessage> UploadAsync(byte[] tarball,
    string? api_key = C_API_KEY,
    bool wait = true) {
    using HttpClient client = Client();
    ByteArrayContent content = new(tarball);
    content.Headers.ContentType =
      new MediaTypeHeaderValue("application/octet-stream");
    using HttpRequestMessage request = new(
      HttpMethod.Put,
      "/-/noodle/upload" + (wait ? "?wait=true" : "")
    ) {
      Content = content
    };
    if (api_key != null) {
      request.Headers.Authorization =
        new AuthenticationHeaderValue("Bearer", api_key);
    }

    return await client.SendAsync(request);
  }

  public async Task<JsonElement>
    UploadOkAsync(byte[] tarball, bool wait = true) {
    using HttpResponseMessage response = await UploadAsync(tarball, wait: wait);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
    return JsonDocument.Parse(body).RootElement.Clone();
  }

  public async Task<HttpResponseMessage> SendWithKeyAsync(HttpMethod method,
    string url, HttpContent? content = null) {
    using HttpClient client = Client();
    using HttpRequestMessage request = new(method, url) {
      Content = content
    };
    request.Headers.Authorization =
      new AuthenticationHeaderValue("Bearer", C_API_KEY);
    return await client.SendAsync(request);
  }

  public async Task<JsonElement>
    GetJsonAsync(string url, string? accept = null) {
    using HttpClient client = Client();
    using HttpRequestMessage request = new(HttpMethod.Get, url);
    if (accept != null) {
      request.Headers.TryAddWithoutValidation("Accept", accept);
    }

    using HttpResponseMessage response = await client.SendAsync(request);
    string body = await response.Content.ReadAsStringAsync();
    Assert.True(
      response.IsSuccessStatusCode,
      $"GET {url}: {response.StatusCode} {body}"
    );
    return JsonDocument.Parse(body).RootElement.Clone();
  }

  public async Task<HttpResponseMessage> GetAsync(
    string url, string? accept = null) {
    using HttpClient client = Client();
    using HttpRequestMessage request = new(HttpMethod.Get, url);
    if (accept != null) {
      request.Headers.TryAddWithoutValidation("Accept", accept);
    }

    return await client.SendAsync(request);
  }
}
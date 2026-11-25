using System.Text;
using System.Text.Json.Nodes;
using Noodle.Api.Endpoints;
using Noodle.Api.Npm;

namespace Noodle.Tests;

public sealed class UnitTests {
  [Theory]
  [InlineData("express", true)]
  [InlineData("JSONStream", true)]
  [InlineData("@types/node", true)]
  [InlineData("left-pad", true)]
  [InlineData("a.b_c-d~e!f'g(h)i*", true)]
  [InlineData("", false)]
  [InlineData(".hidden", false)]
  [InlineData("_private", false)]
  [InlineData("has space", false)]
  [InlineData("node_modules", false)]
  [InlineData("favicon.ico", false)]
  [InlineData("@scope", false)]
  [InlineData("@/name", false)]
  [InlineData("@scope/", false)]
  [InlineData("@scope/.name", false)]
  [InlineData("@scope/a/b", false)]
  [InlineData("a/b", false)]
  [InlineData("..", false)]
  [InlineData("%2e%2e", false)]
  [InlineData("bad\u0001name", false)]
  [InlineData("ünïcode", false)]
  public void PackageNamesFollowNpmsLegacyRules(string name, bool valid) {
    Assert.Equal(valid, PackageName.IsValid(name));
  }

  [Theory]
  [InlineData("1.2.3", "1.2.3")]
  [InlineData("v1.2.3", "1.2.3")]
  [InlineData("=v1.2.3", "1.2.3")]
  [InlineData(" 1.2.3 ", "1.2.3")]
  [InlineData("1.2.3+build.5", "1.2.3")]
  [InlineData("1.2.3-beta.01", "1.2.3-beta.1")]
  [InlineData("1.0.0rc2", "1.0.0-rc2")]
  [InlineData("01.02.03", "1.2.3")]
  [InlineData("1.2", null)]
  [InlineData("latest", null)]
  [InlineData("", null)]
  public void VersionsAreCleanedLikeNpmPublish(string input, string? expected) {
    Assert.Equal(expected, NpmVersion.Clean(input));
  }

  [Fact]
  public void VersionsSortByNpmPrecedence() {
    string[] sorted = [
      "0.0.1",
      "1.0.0-alpha",
      "1.0.0-alpha.1",
      "1.0.0-alpha.beta",
      "1.0.0-beta",
      "1.0.0-beta.2",
      "1.0.0-beta.11",
      "1.0.0-rc.1",
      "1.0.0",
      "1.2.0",
      "1.10.0",
      "10.0.0"
    ];
    string[] shuffled = sorted.Reverse().ToArray();
    Array.Sort(shuffled, NpmVersionComparer.S_INSTANCE);
    Assert.Equal(sorted, shuffled);
  }

  [Theory]
  [InlineData(
    "expressjs/express",
    "shortcut",
    "git+https://github.com/expressjs/express.git"
  )]
  [InlineData(
    "github:user/repo",
    "shortcut",
    "git+https://github.com/user/repo.git"
  )]
  [InlineData(
    "https://github.com/user/repo",
    "https",
    "git+https://github.com/user/repo.git"
  )]
  [InlineData(
    "git+https://github.com/user/repo.git",
    "https",
    "git+https://github.com/user/repo.git"
  )]
  [InlineData(
    "git://github.com/user/repo.git",
    "git",
    "git://github.com/user/repo.git"
  )]
  [InlineData(
    "git@github.com:user/repo.git",
    "sshurl",
    "git+ssh://git@github.com/user/repo.git"
  )]
  [InlineData(
    "git+ssh://git@gitlab.com/group/sub/repo.git",
    "sshurl",
    "git+ssh://git@gitlab.com/group/sub/repo.git"
  )]
  [InlineData(
    "https://bitbucket.org/user/repo",
    "https",
    "git+https://bitbucket.org/user/repo.git"
  )]
  public void RepositoryUrlsAreNormalizedLikeHostedGitInfo(
    string url, string representation,
    string normalized) {
    HostedGitInfo? info = HostedGitInfo.FromUrl(url);
    Assert.NotNull(info);
    Assert.Equal(representation, info.DefaultRepresentation);
    Assert.Equal(
      normalized,
      representation == "shortcut" ? info.Https() : info.ToString()
    );
  }

  [Fact]
  public void HostedGitInfoDerivesLinksAndIgnoresOtherHosts() {
    HostedGitInfo info =
      HostedGitInfo.FromUrl("git+https://github.com/user/repo.git#v1.0.0")!;
    Assert.Equal("https://github.com/user/repo/issues", info.Bugs());
    Assert.Equal(
      "https://github.com/user/repo/tree/v1.0.0#readme",
      info.Docs()
    );
    Assert.Equal("github:user/repo#v1.0.0", info.Shortcut());
    Assert.Null(
      HostedGitInfo.FromUrl("https://git.corp.example/user/repo.git")
    );
    Assert.Null(HostedGitInfo.FromUrl("^1.0.0"));
    Assert.Null(HostedGitInfo.FromUrl("file:../local"));
    Assert.Null(HostedGitInfo.FromUrl("npm:other@1"));
  }

  [Theory]
  [InlineData("1", "1")]
  [InlineData("1.5", "1.5")]
  [InlineData("1.0", "1")]
  [InlineData("1e21", "1e+21")]
  [InlineData("1e-7", "1e-7")]
  [InlineData("0.000001", "0.000001")]
  [InlineData("-0.5", "-0.5")]
  [InlineData("123456789012345678", "123456789012345680")]
  public void
    NumbersAreWrittenLikeJsonStringify(string input, string expected) {
    JsonNode? node = JsJson.Parse(Encoding.UTF8.GetBytes(input));
    Assert.Equal(expected, Encoding.UTF8.GetString(JsJson.Serialize(node)));
  }

  [Fact]
  public void DuplicateKeysKeepTheLastValueInTheFirstPlace() {
    JsonNode? node = JsJson.Parse("""{"a":1,"b":2,"a":3}"""u8);
    Assert.Equal(
      """{"a":3,"b":2}""",
      Encoding.UTF8.GetString(JsJson.Serialize(node))
    );
  }

  [Theory]
  [InlineData("./lib/index.js", "./lib", "index", ".js")]
  [InlineData("index", ".", "index", "")]
  [InlineData("lib/", ".", "lib", "")]
  [InlineData("a/b/.hidden", "a/b", ".hidden", "")]
  public void NodePathMatchesNode(string path, string dirname,
                                  string basename_without_ext,
                                  string extname) {
    Assert.Equal(dirname, NodePath.Dirname(path));
    Assert.Equal(extname, NodePath.Extname(path));
    Assert.Equal(basename_without_ext, NodePath.Basename(path, extname));
  }

  [Theory]
  [InlineData("bin/cli.js", "bin/cli.js")]
  [InlineData("./bin/cli.js", "bin/cli.js")]
  [InlineData("../../etc/passwd", "etc/passwd")]
  [InlineData("C:\\tools\\cli.js", "C/tools/cli.js")]
  public void BinPathsCannotLeaveThePackage(string input, string expected) {
    Assert.Equal(expected, ManifestNormalizer.SecureAndUnixifyPath(input));
  }

  [Theory]
  [InlineData("package/index.js", "index.js")]
  [InlineData("./package/lib/a.js", "lib/a.js")]
  [InlineData("anything/package.json", "package.json")]
  [InlineData("package.json", null)]
  [InlineData("package/../../x", null)]
  public void TheTopFolderIsStrippedLikeNpm(string path, string? expected) {
    Assert.Equal(expected, TarballInspector.StripRoot(path));
  }

  [Theory]
  [InlineData("beta", true)]
  [InlineData("latest-4", true)]
  [InlineData("next_1", true)]
  [InlineData("1.x", false)]
  [InlineData("v2", false)]
  [InlineData("1.2.3", false)]
  [InlineData("", false)]
  [InlineData("a b", false)]
  public void TagsCannotLookLikeVersions(string tag, bool valid) {
    Assert.Equal(valid, AdminEndpoints.IsValidTag(tag));
  }

  [Theory]
  [InlineData("/express", "express", 0)]
  [InlineData("/express/1.0.0", "express", 1)]
  [InlineData("/@types%2fnode", "@types/node", 0)]
  [InlineData("/@types%2Fnode/latest", "@types/node", 1)]
  [InlineData("/@types/node/-/node-1.0.0.tgz", "@types/node", 2)]
  [InlineData("/express/", "express", 0)]
  public void RequestPathsAreSplitIntoNameAndRest(
    string path, string name, int rest) {
    Assert.True(
      RegistryEndpoints.TryParsePath(
        path,
        out string parsed,
        out string[] segments
      )
    );
    Assert.Equal(name, parsed);
    Assert.Equal(rest, segments.Length);
  }

  [Theory]
  [InlineData("/../etc/passwd")]
  [InlineData("/@scope")]
  [InlineData("/")]
  public void BadRequestPathsAreRejected(string path) {
    Assert.False(RegistryEndpoints.TryParsePath(path, out _, out _));
  }
}
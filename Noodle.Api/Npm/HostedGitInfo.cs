using System.Text.RegularExpressions;

namespace Noodle.Api.Npm;

/// <summary>
///   A port of npm's <c>hosted-git-info</c> (9.x), which <c>npm publish</c>
///   uses to normalize <c>repository</c> URLs and git dependencies and to
///   derive <c>bugs</c> and <c>homepage</c> links. Only GitHub, GitLab,
///   Bitbucket, Gists and SourceHut are known, like upstream.
/// </summary>
public sealed partial class HostedGitInfo {
  private static readonly Dictionary<string, Host> S_HOSTS_ =
    new(StringComparer.Ordinal) {
      ["github"] = new Host {
        Name = "github",
        Domain = "github.com",
        Protocols = [
          "git:",
          "http:",
          "git+ssh:",
          "git+https:",
          "ssh:",
          "https:"
        ],
        TreePath = "tree",
        HasGitTemplate = true
      },
      ["bitbucket"] = new Host {
        Name = "bitbucket",
        Domain = "bitbucket.org",
        Protocols = [
          "git+ssh:",
          "git+https:",
          "ssh:",
          "https:"
        ],
        TreePath = "src"
      },
      ["gitlab"] = new Host {
        Name = "gitlab",
        Domain = "gitlab.com",
        Protocols = [
          "git+ssh:",
          "git+https:",
          "ssh:",
          "https:"
        ],
        TreePath = "tree"
      },
      ["gist"] = new Host {
        Name = "gist",
        Domain = "gist.github.com",
        Protocols = [
          "git:",
          "git+ssh:",
          "git+https:",
          "ssh:",
          "https:"
        ],
        HasGitTemplate = true
      },
      ["sourcehut"] = new Host {
        Name = "sourcehut",
        Domain = "git.sr.ht",
        Protocols = [
          "git+ssh:",
          "https:"
        ],
        TreePath = "tree"
      }
    };

  // Protocol -> representation name (null: the protocol without colon).
  private static readonly Dictionary<string, (string? Name, bool Auth)>
    S_PROTOCOLS_ = new(StringComparer.Ordinal) {
      ["git+ssh:"] = ("sshurl", false),
      ["ssh:"] = ("sshurl", false),
      ["git+https:"] = ("https", true),
      ["git:"] = (null, true),
      ["http:"] = (null, true),
      ["https:"] = (null, true),
      ["git+http:"] = (null, true),
      ["github:"] = ("github", false),
      ["bitbucket:"] = ("bitbucket", false),
      ["gitlab:"] = ("gitlab", false),
      ["gist:"] = ("gist", false),
      ["sourcehut:"] = ("sourcehut", false)
    };

  private readonly Host host_;

  private HostedGitInfo(Host host, string? user, string? auth, string project,
                        string? committish, string representation) {
    host_ = host;
    User = user;
    Auth = auth;
    Project = project;
    Committish = string.IsNullOrEmpty(committish) ? null : committish;
    DefaultRepresentation = representation;
  }

  public string? User { get; }
  public string? Auth { get; }
  public string Project { get; }
  public string? Committish { get; }

  /// <summary>
  ///   <c>shortcut</c>, <c>sshurl</c>, <c>https</c>, <c>git</c>, <c>ssh</c>,
  ///   ...
  /// </summary>
  public string DefaultRepresentation { get; }

  /// <summary>
  ///   <c>hosted-git-info</c>'s <c>fromUrl</c>: <c>null</c> for anything not on
  ///   a known host.
  /// </summary>
  public static HostedGitInfo? FromUrl(string? git_url) {
    if (string.IsNullOrEmpty(git_url)) {
      return null;
    }

    try {
      return Parse(git_url);
    } catch (FormatException) {
      return null;
    }
  }

  /// <summary>The URL in its original style (<c>toString()</c>).</summary>
  public override string ToString() {
    return DefaultRepresentation switch {
      "shortcut" => Shortcut(),
      "https" => Https(),
      "git" when host_.HasGitTemplate => Git(),
      "ssh" => Ssh(),
      _ => SshUrl()
    };
  }

  public string Https() {
    string domain = host_.Domain;
    if (host_.Name == "gist") {
      return $"git+https://{domain}/{Project}.git{Hash()}";
    }

    if (host_.Name == "sourcehut") {
      return $"https://{domain}/{User}/{Project}{Hash()}";
    }

    string auth = Auth == null ? "" : Auth + "@";
    return $"git+https://{auth}{domain}/{User}/{Project}.git{Hash()}";
  }

  public string Git() {
    string auth = Auth == null ? "" : Auth + "@";
    return host_.Name == "gist"
             ? $"git://{host_.Domain}/{Project}.git{Hash()}"
             : $"git://{auth}{host_.Domain}/{User}/{Project}.git{Hash()}";
  }

  public string Ssh() {
    return host_.Name == "gist"
             ? $"git@{host_.Domain}:{Project}.git{Hash()}"
             : $"git@{host_.Domain}:{User}/{Project}.git{Hash()}";
  }

  public string SshUrl() {
    return host_.Name == "gist"
             ? $"git+ssh://git@{host_.Domain}/{Project}.git{Hash()}"
             : $"git+ssh://git@{host_.Domain}/{User}/{Project}.git{Hash()}";
  }

  public string Shortcut() {
    return host_.Name == "gist"
             ? $"{host_.Name}:{Project}{Hash()}"
             : $"{host_.Name}:{User}/{Project}{Hash()}";
  }

  /// <summary>The issue tracker, or <c>null</c> (SourceHut has none).</summary>
  public string? Bugs() {
    return host_.Name switch {
      "sourcehut" => null,
      "gist" => $"https://{host_.Domain}/{Project}",
      _ => $"https://{host_.Domain}/{User}/{Project}/issues"
    };
  }

  /// <summary>The project page (npm's default <c>homepage</c>).</summary>
  public string Docs() {
    string committish = Committish == null
                          ? ""
                          : JsJson.EncodeUriComponent(Committish);
    if (host_.Name == "gist") {
      return $"https://{host_.Domain}/{Project}" +
             (committish.Length > 0 ? "/" + committish : "");
    }

    string tree = committish.Length > 0 && host_.TreePath != null
                    ? $"/{host_.TreePath}/{committish}"
                    : "";
    return $"https://{host_.Domain}/{User}/{Project}{tree}#readme";
  }

  private string Hash() {
    return Committish == null ? "" : "#" + Committish;
  }

  private static HostedGitInfo? Parse(string git_url) {
    string corrected = IsGitHubShorthand(git_url)
                         ? "github:" + git_url
                         : git_url;
    WhatwgUrl? parsed = ParseUrl(corrected);
    if (parsed == null) {
      return null;
    }

    string hostname = parsed.Hostname.StartsWith(
                        "www.",
                        StringComparison.Ordinal
                      )
                        ? parsed.Hostname[4..]
                        : parsed.Hostname;
    string shortcut = parsed.Protocol.Length > 1
                        ? parsed.Protocol[..^1]
                        : "";
    Host? by_shortcut = S_HOSTS_.GetValueOrDefault(shortcut);
    Host? by_domain = S_HOSTS_.Values.FirstOrDefault(h => h.Domain == hostname);
    Host? host = by_shortcut ?? by_domain;
    if (host == null) {
      return null;
    }

    string? auth = null;
    if (S_PROTOCOLS_.TryGetValue(
          parsed.Protocol,
          out (string? Name, bool Auth) protocol
        ) &&
        protocol.Auth &&
        (parsed.Username.Length > 0 || parsed.Password.Length > 0)) {
      auth = parsed.Username +
             (parsed.Password.Length > 0 ? ":" + parsed.Password : "");
    }

    if (by_shortcut != null) {
      string pathname = parsed.Pathname.StartsWith('/')
                          ? parsed.Pathname[1..]
                          : parsed.Pathname;
      int first_at = pathname.IndexOf('@');
      if (first_at > -1) {
        pathname = pathname[(first_at + 1)..];
      }

      string? user = null;
      string project;
      int last_slash = pathname.LastIndexOf('/');
      if (last_slash > -1) {
        user = Decode(pathname[..last_slash]);
        if (user.Length == 0) {
          user = null;
        }

        project = Decode(pathname[(last_slash + 1)..]);
      } else {
        project = Decode(pathname);
      }

      if (project.EndsWith(".git", StringComparison.Ordinal)) {
        project = project[..^4];
      }

      string? committish = parsed.Hash.Length > 1
                             ? Decode(parsed.Hash[1..])
                             : null;
      return new HostedGitInfo(
        host,
        user,
        null,
        project,
        committish,
        "shortcut"
      );
    }

    if (!host.Protocols.Contains(parsed.Protocol)) {
      return null;
    }

    (string? User, string Project, string? Committish)? segments =
      Extract(host, parsed);
    if (segments == null) {
      return null;
    }

    string representation = protocol.Name ?? parsed.Protocol[..^1];
    return new HostedGitInfo(
      host,
      segments.Value.User == null ? null : Decode(segments.Value.User),
      auth,
      Decode(segments.Value.Project),
      segments.Value.Committish == null
        ? null
        : Decode(segments.Value.Committish),
      representation
    );
  }

  private static (string? User, string Project, string? Committish)? Extract(
    Host host, WhatwgUrl url) {
    string hash = url.Hash.Length > 0 ? url.Hash[1..] : "";
    string[] parts = url.Pathname.Split('/');

    string? Part(int index) {
      return index < parts.Length ? parts[index] : null;
    }

    switch (host.Name) {
      case "github": {
        // pathname.split('/', 5): ['', user, project, type, committish]
        string? user = Part(1);
        string? project = Part(2);
        string? type = Part(3);
        string? committish = Part(4);
        if (!string.IsNullOrEmpty(type) && type != "tree") {
          return null;
        }

        if (string.IsNullOrEmpty(type)) {
          committish = hash;
        }

        project = StripGit(project);
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) {
          return null;
        }

        return (user, project, committish);
      }
      case "bitbucket":
      case "sourcehut": {
        string? user = Part(1);
        string? project = StripGit(Part(2));
        string? aux = Part(3);
        if (aux == (host.Name == "bitbucket" ? "get" : "archive")) {
          return null;
        }

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(project)) {
          return null;
        }

        return (user, project, hash);
      }
      case "gitlab": {
        string path = url.Pathname.Length > 0 ? url.Pathname[1..] : "";
        if (path.Contains("/-/", StringComparison.Ordinal) ||
            path.Contains("/archive.tar.gz", StringComparison.Ordinal)) {
          return null;
        }

        int last = path.LastIndexOf('/');
        string project = StripGit(last < 0 ? path : path[(last + 1)..])!;
        string user = last < 0 ? "" : path[..last];
        if (user.Length == 0 || project.Length == 0) {
          return null;
        }

        return (user, project, hash);
      }
      default: {
        string? user = Part(1);
        string? project = Part(2);
        if (Part(3) == "raw") {
          return null;
        }

        if (string.IsNullOrEmpty(project)) {
          if (string.IsNullOrEmpty(user)) {
            return null;
          }

          project = user;
          user = null;
        }

        return (user, StripGit(project)!, hash);
      }
    }
  }

  private static string? StripGit(string? project) {
    return project != null && project.EndsWith(".git", StringComparison.Ordinal)
             ? project[..^4]
             : project;
  }

  private static string Decode(string value) {
    try {
      return Uri.UnescapeDataString(value);
    } catch (UriFormatException) {
      throw new FormatException();
    }
  }

  /// <summary>Recognizes <c>user/repo</c> GitHub shorthands.</summary>
  private static bool IsGitHubShorthand(string arg) {
    int first_hash = arg.IndexOf('#');
    int first_slash = arg.IndexOf('/');
    int second_slash = first_slash < 0 ? -1 : arg.IndexOf('/', first_slash + 1);
    int first_colon = arg.IndexOf(':');
    Match space = WhitespaceRegex().Match(arg);
    int first_at = arg.IndexOf('@');
    bool space_only_after_hash = !space.Success ||
                                 (first_hash > -1 && space.Index > first_hash);
    bool at_only_after_hash = first_at == -1 ||
                              (first_hash > -1 && first_at > first_hash);
    bool colon_only_after_hash = first_colon == -1 ||
                                 (first_hash > -1 && first_colon > first_hash);
    bool second_slash_only_after_hash =
      second_slash == -1 || (first_hash > -1 && second_slash > first_hash);
    bool has_slash = first_slash > 0;
    bool does_not_end_with_slash = first_hash > -1
                                     ? arg[first_hash - 1] != '/'
                                     : !arg.EndsWith('/');
    bool does_not_start_with_dot = !arg.StartsWith('.');
    return space_only_after_hash &&
           has_slash &&
           does_not_end_with_slash &&
           does_not_start_with_dot &&
           at_only_after_hash &&
           colon_only_after_hash &&
           second_slash_only_after_hash;
  }

  private static WhatwgUrl? ParseUrl(string git_url) {
    string with_protocol = CorrectProtocol(git_url);
    return WhatwgUrl.TryParse(with_protocol) ??
           WhatwgUrl.TryParse(CorrectUrl(with_protocol));
  }

  /// <summary>
  ///   Turns <c>git:github.com:user/repo</c> into a parsable URL.
  /// </summary>
  private static string CorrectProtocol(string arg) {
    int first_colon = arg.IndexOf(':');
    string proto = arg[..(first_colon + 1)];
    if (S_PROTOCOLS_.ContainsKey(proto)) {
      return arg;
    }

    if (first_colon >= 0 &&
        arg.AsSpan(first_colon).StartsWith("://", StringComparison.Ordinal)) {
      return arg;
    }

    int first_at = arg.IndexOf('@');
    if (first_at > -1) {
      return first_at > first_colon ? "git+ssh://" + arg : arg;
    }

    return arg[..(first_colon + 1)] + "//" + arg[(first_colon + 1)..];
  }

  /// <summary>
  ///   Turns scp style URLs (<c>git@github.com:user/repo</c>) into ssh URLs.
  /// </summary>
  private static string CorrectUrl(string git_url) {
    int first_at = LastIndexOfBefore(git_url, '@', '#');
    int last_colon_before_hash = LastIndexOfBefore(git_url, ':', '#');
    if (last_colon_before_hash > first_at) {
      git_url = git_url[..last_colon_before_hash] +
                "/" +
                git_url[(last_colon_before_hash + 1)..];
    }

    if (LastIndexOfBefore(git_url, ':', '#') == -1 &&
        !git_url.Contains("//", StringComparison.Ordinal)) {
      git_url = "git+ssh://" + git_url;
    }

    return git_url;
  }

  private static int LastIndexOfBefore(string value, char c, char before) {
    int start = value.IndexOf(before);
    return start > -1
             ? value.LastIndexOf(c, start)
             : value.LastIndexOf(c);
  }

  [GeneratedRegex(@"\s")]
  private static partial Regex WhitespaceRegex();

  private sealed class Host {
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required string[] Protocols { get; init; }
    public string? TreePath { get; init; }
    public bool HasGitTemplate { get; init; }
  }
}

/// <summary>
///   The parts of a URL as the WHATWG URL parser (Node's <c>new URL</c>)
///   reports them, for the inputs <see cref="HostedGitInfo" /> deals with.
/// </summary>
public sealed partial class WhatwgUrl {
  public required string Protocol { get; init; }
  public required string Username { get; init; }
  public required string Password { get; init; }
  public required string Hostname { get; init; }
  public required string Pathname { get; init; }
  public required string Hash { get; init; }

  /// <summary>Like <c>URL.canParse</c> for absolute URLs.</summary>
  public static bool CanParse(string? value) {
    return value != null && TryParse(value) != null;
  }

  public static WhatwgUrl? TryParse(string input) {
    string value = input.Trim();
    Match scheme = SchemeRegex().Match(value);
    if (!scheme.Success) {
      return null;
    }

    string protocol = scheme.Groups[1].Value.ToLowerInvariant() + ":";
    string rest = value[scheme.Length..];
    string hash = "";
    int hash_index = rest.IndexOf('#');
    if (hash_index >= 0) {
      hash = rest[hash_index..];
      rest = rest[..hash_index];
      if (hash == "#") {
        hash = "";
      }
    }

    int query = rest.IndexOf('?');
    if (query >= 0) {
      rest = rest[..query];
    }

    bool special = protocol is "http:" or "https:" or "ftp:" or "ws:"
                     or "wss:" or "file:";
    if (special) {
      rest = rest.Replace('\\', '/');
      // Special schemes ignore any number of slashes before the host.
      rest = "//" + rest.TrimStart('/');
    }

    if (!rest.StartsWith("//", StringComparison.Ordinal)) {
      // Opaque path, e.g. "github:user/repo".
      return new WhatwgUrl {
        Protocol = protocol,
        Username = "",
        Password = "",
        Hostname = "",
        Pathname = rest,
        Hash = hash
      };
    }

    string authority_and_path = rest[2..];
    int path_start = authority_and_path.IndexOf('/');
    string authority = path_start < 0
                         ? authority_and_path
                         : authority_and_path[..path_start];
    string pathname = path_start < 0 ? "" : authority_and_path[path_start..];
    string username = "";
    string password = "";
    int at = authority.LastIndexOf('@');
    if (at >= 0) {
      string credentials = authority[..at];
      authority = authority[(at + 1)..];
      int colon = credentials.IndexOf(':');
      username = colon < 0 ? credentials : credentials[..colon];
      password = colon < 0 ? "" : credentials[(colon + 1)..];
    }

    string hostname = authority;
    Match port = PortRegex().Match(authority);
    if (port.Success) {
      hostname = authority[..port.Index];
    }

    if (hostname.Length == 0 && special && protocol != "file:") {
      return null;
    }

    if (HostRegex().IsMatch(hostname)) {
      return null;
    }

    if (special && pathname.Length == 0) {
      pathname = "/";
    }

    return new WhatwgUrl {
      Protocol = protocol,
      Username = username,
      Password = password,
      Hostname = special ? hostname.ToLowerInvariant() : hostname,
      Pathname = pathname,
      Hash = hash
    };
  }

  [GeneratedRegex("^([a-zA-Z][a-zA-Z0-9+.-]*):")]
  private static partial Regex SchemeRegex();

  [GeneratedRegex(@":\d*$")]
  private static partial Regex PortRegex();

  // Characters the URL parser rejects in a host.
  [GeneratedRegex(@"[\s#%/:<>?@\[\\\]^|]")]
  private static partial Regex HostRegex();
}
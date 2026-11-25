namespace Noodle.Api.Npm;

/// <summary>
///   npm package names (<c>express</c>, <c>@types/node</c>). Names are case
///   sensitive and kept exactly: legacy packages such as <c>JSONStream</c>
///   have capital letters.
/// </summary>
public static class PackageName {
  /// <summary>
  ///   npm allows longer legacy names (only new ones are limited to 214
  ///   characters); this bound keeps storage keys well under S3's limit.
  /// </summary>
  public const int C_MAX_LENGTH = 256;

  /// <summary>
  ///   Whether <paramref name="name" /> is a name npm accepts for existing
  ///   packages (validate-npm-package-name's <c>validForOldPackages</c>):
  ///   URL safe characters, optionally one scope, not starting with a dot
  ///   or underscore.
  /// </summary>
  public static bool IsValid(string? name) {
    if (string.IsNullOrEmpty(name) || name.Length > C_MAX_LENGTH) {
      return false;
    }

    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase)) {
      return false;
    }

    if (name[0] == '@') {
      int slash = name.IndexOf('/');
      if (slash < 2 || slash == name.Length - 1) {
        return false;
      }

      ReadOnlySpan<char> scope = name.AsSpan(1, slash - 1);
      ReadOnlySpan<char> bare = name.AsSpan(slash + 1);
      return scope[0] != '.' &&
             bare[0] != '.' &&
             bare[0] != '_' &&
             IsUrlSafe(scope) &&
             IsUrlSafe(bare);
    }

    return name[0] != '.' && name[0] != '_' && IsUrlSafe(name);
  }

  /// <summary>
  ///   The scope without <c>@</c> (<c>types</c>), or <c>null</c>.
  /// </summary>
  public static string? GetScope(string name) {
    if (name.Length == 0 || name[0] != '@') {
      return null;
    }

    int slash = name.IndexOf('/');
    return slash < 0 ? null : name[1..slash];
  }

  /// <summary>
  ///   The name without its scope (<c>node</c> for <c>@types/node</c>).
  /// </summary>
  public static string GetBareName(string name) {
    int slash = name.IndexOf('/');
    return slash < 0 ? name : name[(slash + 1)..];
  }

  /// <summary>File name of a tarball, as in npm's tarball URLs.</summary>
  public static string TarballFileName(string name, string version) {
    return $"{GetBareName(name)}-{version}.tgz";
  }

  /// <summary>
  ///   The URL path of a tarball (<c>@types/node/-/node-1.0.0.tgz</c>).
  /// </summary>
  public static string TarballPath(string name, string version) {
    return $"{name}/-/{TarballFileName(name, version)}";
  }

  /// <summary>Characters <c>encodeURIComponent</c> leaves alone.</summary>
  private static bool IsUrlSafe(ReadOnlySpan<char> value) {
    foreach (char c in value) {
      bool safe = c is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '-'
                    or '_'
                    or '.'
                    or '!'
                    or '~'
                    or '*'
                    or '\''
                    or '('
                    or ')';
      if (!safe) {
        return false;
      }
    }

    return true;
  }
}
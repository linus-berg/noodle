namespace Noodle.Api.Storage;

/// <summary>
///   Bucket layout. Package names are kept exactly (npm names are case
///   sensitive); scoped names contain a slash and so one more level.
/// </summary>
/// <remarks>
///   <code>
///   tarballs/{name}/{version}.tgz         the tarball, byte for byte
///   versions/{name}/{version}.json.gz     manifest, readme, arrival time
///   packages/{name}.json.gz               all versions (the commit point)
///   packuments/{name}/{hash}.corgi.json.gz  generated packuments, named
///   packuments/{name}/{hash}.full.json.gz   by their content hash
///   pending/{name}/{version}              acknowledged, not yet committed
///   catalog/snapshot.bin.gz               compact catalog of all packages
///   changes/{ticks}-{instance}-{kind}-{subject}  change log
///   advisories/advisories.json.gz         imported security advisories
///   </code>
/// </remarks>
public static class StorageKeys {
  public const string C_TARBALLS_PREFIX = "tarballs/";
  public const string C_VERSIONS_PREFIX = "versions/";
  public const string C_PACKAGES_PREFIX = "packages/";
  public const string C_PACKUMENTS_PREFIX = "packuments/";
  public const string C_PENDING_PREFIX = "pending/";
  public const string C_CHANGES_PREFIX = "changes/";
  public const string C_SNAPSHOT = "catalog/snapshot.bin.gz";
  public const string C_ADVISORIES = "advisories/advisories.json.gz";

  private const string C_DOCUMENT_SUFFIX_ = ".json.gz";

  public static string Tarball(string name, string version) {
    return $"{C_TARBALLS_PREFIX}{name}/{version}.tgz";
  }

  public static string Version(string name, string version) {
    return $"{C_VERSIONS_PREFIX}{name}/{version}{C_DOCUMENT_SUFFIX_}";
  }

  public static string Package(string name) {
    return C_PACKAGES_PREFIX + name + C_DOCUMENT_SUFFIX_;
  }

  public static string Packument(string name, string hash, bool corgi) {
    return $"{C_PACKUMENTS_PREFIX}{name}/{hash}." +
           $"{(corgi ? "corgi" : "full")}{C_DOCUMENT_SUFFIX_}";
  }

  public static string Pending(string name, string version) {
    return $"{C_PENDING_PREFIX}{name}/{version}";
  }

  /// <summary>
  ///   The package name of a package document key, or <c>null</c>.
  /// </summary>
  public static string? NameFromPackageKey(string key) {
    if (!key.StartsWith(C_PACKAGES_PREFIX, StringComparison.Ordinal) ||
        !key.EndsWith(C_DOCUMENT_SUFFIX_, StringComparison.Ordinal)) {
      return null;
    }

    return key[C_PACKAGES_PREFIX.Length..^C_DOCUMENT_SUFFIX_.Length];
  }

  /// <summary>
  ///   Splits <c>{prefix}{name}/{version}{suffix}</c> keys (version objects,
  ///   pending markers) into name and version.
  /// </summary>
  public static (string Name, string Version)? SplitVersionKey(string key,
    string prefix, string suffix) {
    if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
        !key.EndsWith(suffix, StringComparison.Ordinal)) {
      return null;
    }

    string rest = key[prefix.Length..^suffix.Length];
    int slash = rest.LastIndexOf('/');
    if (slash <= 0 || slash == rest.Length - 1) {
      return null;
    }

    return (rest[..slash], rest[(slash + 1)..]);
  }

  /// <summary>
  ///   Whether a key is safe to send to S3: no empty, <c>.</c> or <c>..</c>
  ///   segments, no backslashes or control characters.
  /// </summary>
  public static bool IsSafeKey(string key) {
    if (key.Length == 0 || key.Length > 900) {
      return false;
    }

    foreach (char c in key) {
      if (c < 0x20 || c == 0x7F || c == '\\') {
        return false;
      }
    }

    foreach (string segment in key.Split('/')) {
      if (segment.Length == 0 || segment == "." || segment == "..") {
        return false;
      }
    }

    return true;
  }
}
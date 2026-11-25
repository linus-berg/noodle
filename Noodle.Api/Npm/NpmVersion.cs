using System.Globalization;
using System.Text.RegularExpressions;

namespace Noodle.Api.Npm;

/// <summary>
///   A version parsed by npm's rules (the <c>semver</c> package): SemVer
///   2.0.0 precedence, numeric prerelease identifiers compared as numbers,
///   build metadata ignored.
/// </summary>
public sealed partial class NpmVersion : IComparable<NpmVersion> {
  /// <summary>npm's <c>MAX_LENGTH</c>.</summary>
  public const int C_MAX_LENGTH = 256;

  private const long C_MAX_SAFE_INTEGER_ = 9007199254740991;

  private NpmVersion(long major, long minor, long patch,
                     object[] prerelease) {
    Major = major;
    Minor = minor;
    Patch = patch;
    Prerelease = prerelease;
    string text = string.Create(
      CultureInfo.InvariantCulture,
      $"{major}.{minor}.{patch}"
    );
    if (prerelease.Length > 0) {
      text += "-" +
              string.Join(
                '.',
                prerelease.Select(
                  p => p is long n
                         ? n.ToString(CultureInfo.InvariantCulture)
                         : (string)p
                )
              );
    }

    Text = text;
  }

  public long Major { get; }
  public long Minor { get; }
  public long Patch { get; }

  /// <summary>
  ///   Identifiers: <see cref="long" /> for numeric ones, else <see
  ///   cref="string" />.
  /// </summary>
  public object[] Prerelease { get; }

  /// <summary>
  ///   The formatted version (<c>1.2.3-beta.1</c>), without build metadata.
  /// </summary>
  public string Text { get; }

  public int CompareTo(NpmVersion? other) {
    if (other == null) {
      return 1;
    }

    int result = Major.CompareTo(other.Major);
    if (result == 0) {
      result = Minor.CompareTo(other.Minor);
    }

    if (result == 0) {
      result = Patch.CompareTo(other.Patch);
    }

    return result != 0 ? result : ComparePrerelease(other);
  }

  public override string ToString() {
    return Text;
  }

  /// <summary>
  ///   Parses like <c>new SemVer(version, { loose })</c>; <c>null</c> when
  ///   the text is not a version.
  /// </summary>
  public static NpmVersion? Parse(string? text, bool loose = false) {
    if (text == null || text.Length > C_MAX_LENGTH) {
      return null;
    }

    Match match = (loose ? LooseRegex() : StrictRegex()).Match(text.Trim());
    if (!match.Success ||
        !TryNumber(match.Groups[1].Value, out long major) ||
        !TryNumber(match.Groups[2].Value, out long minor) ||
        !TryNumber(match.Groups[3].Value, out long patch)) {
      return null;
    }

    object[] prerelease = [];
    if (match.Groups[4].Success) {
      prerelease = match.Groups[4]
                        .Value.Split('.')
                        .Select(ParseIdentifier)
                        .ToArray();
    }

    return new NpmVersion(major, minor, patch, prerelease);
  }

  /// <summary>
  ///   What <c>npm publish</c> turns a <c>package.json</c> version into:
  ///   <c>semver.clean</c> with loose parsing (leading <c>v</c> or
  ///   <c>=</c>, whitespace, missing dash before the prerelease), which also
  ///   drops build metadata. <c>null</c> when npm would reject it.
  /// </summary>
  public static string? Clean(string? text) {
    if (text == null) {
      return null;
    }

    string trimmed = text.Trim().TrimStart('=', 'v');
    return Parse(trimmed, true)?.Text;
  }

  /// <summary>Compares two clean version strings by precedence.</summary>
  public static int Compare(string a, string b) {
    NpmVersion? left = Parse(a, true);
    NpmVersion? right = Parse(b, true);
    if (left == null || right == null) {
      return string.CompareOrdinal(a, b);
    }

    return left.CompareTo(right);
  }

  private int ComparePrerelease(NpmVersion other) {
    if (Prerelease.Length == 0 || other.Prerelease.Length == 0) {
      // A version without prerelease ranks above one with.
      return other.Prerelease.Length.CompareTo(Prerelease.Length);
    }

    for (int i = 0;; i++) {
      if (i == Prerelease.Length && i == other.Prerelease.Length) {
        return 0;
      }

      if (i == other.Prerelease.Length) {
        return 1;
      }

      if (i == Prerelease.Length) {
        return -1;
      }

      int result = CompareIdentifiers(Prerelease[i], other.Prerelease[i]);
      if (result != 0) {
        return result;
      }
    }
  }

  private static int CompareIdentifiers(object a, object b) {
    return (a, b) switch {
      (long x, long y) => x.CompareTo(y),
      (long, _) => -1,
      (_, long) => 1,
      _ => string.CompareOrdinal((string)a, (string)b)
    };
  }

  private static object ParseIdentifier(string identifier) {
    // Like semver: all digits -> a number, unless it is too large.
    foreach (char c in identifier) {
      if (c is < '0' or > '9') {
        return identifier;
      }
    }

    return long.TryParse(
             identifier,
             NumberStyles.None,
             CultureInfo.InvariantCulture,
             out long number
           ) &&
           number < C_MAX_SAFE_INTEGER_
             ? number
             : identifier;
  }

  private static bool TryNumber(string text, out long value) {
    return long.TryParse(
             text,
             NumberStyles.None,
             CultureInfo.InvariantCulture,
             out value
           ) &&
           value <= C_MAX_SAFE_INTEGER_;
  }

  // semver's FULL: v?MAINVERSION PRERELEASE? BUILD?
  [GeneratedRegex(
    @"^v?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)" +
    @"(?:-((?:\d*[a-zA-Z-][a-zA-Z0-9-]*|0|[1-9]\d*)" +
    @"(?:\.(?:\d*[a-zA-Z-][a-zA-Z0-9-]*|0|[1-9]\d*))*))?" +
    @"(?:\+([a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)*))?$",
    RegexOptions.CultureInvariant
  )]
  private static partial Regex StrictRegex();

  // semver's LOOSE: [v=\s]* MAINVERSIONLOOSE PRERELEASELOOSE? BUILD?
  [GeneratedRegex(
    @"^[v=\s]*(\d+)\.(\d+)\.(\d+)" +
    @"(?:-?((?:\d*[a-zA-Z-][a-zA-Z0-9-]*|\d+)" +
    @"(?:\.(?:\d*[a-zA-Z-][a-zA-Z0-9-]*|\d+))*))?" +
    @"(?:\+([a-zA-Z0-9-]+(?:\.[a-zA-Z0-9-]+)*))?$",
    RegexOptions.CultureInvariant
  )]
  private static partial Regex LooseRegex();
}

/// <summary>Orders version strings by npm precedence (oldest first).</summary>
public sealed class NpmVersionComparer : IComparer<string> {
  public static readonly NpmVersionComparer S_INSTANCE = new();

  public int Compare(string? x, string? y) {
    return NpmVersion.Compare(x ?? "", y ?? "");
  }
}
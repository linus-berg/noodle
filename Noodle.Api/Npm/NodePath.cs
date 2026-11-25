using System.Text;

namespace Noodle.Api.Npm;

/// <summary>
///   Node's <c>path.posix</c> functions that npm's normalization uses.
/// </summary>
public static class NodePath {
  public static string Join(params string[] parts) {
    string joined = string.Join('/', parts.Where(p => p.Length > 0));
    return joined.Length == 0 ? "." : Normalize(joined);
  }

  public static string Normalize(string path) {
    if (path.Length == 0) {
      return ".";
    }

    bool absolute = path[0] == '/';
    bool trailing = path[^1] == '/';
    string normalized = NormalizeString(path, !absolute);
    if (normalized.Length == 0) {
      if (absolute) {
        return "/";
      }

      return trailing ? "./" : ".";
    }

    if (trailing) {
      normalized += "/";
    }

    return absolute ? "/" + normalized : normalized;
  }

  public static string Dirname(string path) {
    if (path.Length == 0) {
      return ".";
    }

    bool has_root = path[0] == '/';
    int end = -1;
    bool matched_slash = true;
    for (int i = path.Length - 1; i >= 1; i--) {
      if (path[i] == '/') {
        if (!matched_slash) {
          end = i;
          break;
        }
      } else {
        matched_slash = false;
      }
    }

    if (end == -1) {
      return has_root ? "/" : ".";
    }

    if (has_root && end == 1) {
      return "//";
    }

    return path[..end];
  }

  public static string Basename(string path, string? suffix = null) {
    // The last segment, ignoring trailing slashes.
    int end = path.Length;
    while (end > 0 && path[end - 1] == '/') {
      end--;
    }

    int start = path.LastIndexOf('/', Math.Max(0, end - 1));
    string segment = end == 0 ? "" : path[(start + 1)..end];
    if (!string.IsNullOrEmpty(suffix) &&
        segment != suffix &&
        segment.EndsWith(suffix, StringComparison.Ordinal)) {
      return segment[..^suffix.Length];
    }

    return segment;
  }

  public static string Extname(string path) {
    string segment = Basename(path);
    int dot = segment.LastIndexOf('.');
    // No dot, or only a leading one (".bashrc"), or "..".
    if (dot <= 0 || segment == "..") {
      return "";
    }

    return segment[dot..];
  }

  private static string NormalizeString(string path, bool allow_above_root) {
    List<string> segments = [];
    foreach (string segment in path.Split('/')) {
      if (segment.Length == 0 || segment == ".") {
        continue;
      }

      if (segment == "..") {
        if (segments.Count > 0 && segments[^1] != "..") {
          segments.RemoveAt(segments.Count - 1);
        } else if (allow_above_root) {
          segments.Add("..");
        }

        continue;
      }

      segments.Add(segment);
    }

    StringBuilder result = new();
    foreach (string segment in segments) {
      if (result.Length > 0) {
        result.Append('/');
      }

      result.Append(segment);
    }

    return result.ToString();
  }
}
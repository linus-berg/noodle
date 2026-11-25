using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Noodle.Api.Npm;

/// <summary>A version manifest as the registry would have stored it.</summary>
public sealed class NormalizedManifest {
  public required string Name { get; init; }
  public required string Version { get; init; }

  /// <summary>
  ///   The version manifest without <c>dist</c> and <c>readme</c>.
  /// </summary>
  public required JsonObject Manifest { get; init; }

  /// <summary>
  ///   README text (also when it came from <c>package.json</c>), or
  ///   <c>null</c>.
  /// </summary>
  public string? Readme { get; init; }

  public string? ReadmeFilename { get; init; }
}

/// <summary>
///   The manifest cannot be published (npm would refuse it too).
/// </summary>
public sealed class InvalidManifestException(string message)
  : Exception(message);

/// <summary>
///   Turns a tarball's <c>package.json</c> into the version manifest the npm
///   registry stores, by replaying what <c>npm publish</c> does
///   (<c>@npmcli/package-json</c> 7: the <c>fix</c> steps, then the
///   <c>prepare</c> steps, then libnpmpublish) with the tarball's files in
///   place of the package folder, and what the registry adds and removes
///   (<c>_hasShrinkwrap</c>, <c>directories</c>, <c>files</c>, <c>readme</c>).
/// </summary>
public static partial class ManifestNormalizer {
  public const string C_NO_README = "ERROR: No README data found!";

  /// <summary>
  ///   Readmes are capped; the npm website only shows the start anyway.
  /// </summary>
  public const int C_MAX_README_CHARS = 512 * 1024;

  private static readonly string[] S_DEPENDENCY_TYPES_ = [
    "dependencies",
    "devDependencies",
    "optionalDependencies"
  ];

  public static NormalizedManifest Normalize(TarballContents contents) {
    JsonNode? parsed;
    try {
      parsed = JsJson.Parse(contents.PackageJson);
    } catch (JsonException ex) {
      throw new InvalidManifestException(
        $"package.json is not valid JSON: {ex.Message}"
      );
    }

    if (parsed is not JsonObject data) {
      throw new InvalidManifestException("package.json is not an object.");
    }

    // npm publish: PackageJson.fix() ...
    NormalizePackageBin(data);
    FixName(data);
    FixVersion(data);
    FixBundleDependencies(data, false);
    FixScripts(data);
    FixRepository(data);
    FixDependencies(data);

    // ... then prepare(): the steps that look at the package's files ...
    JsonObject scripts = data["scripts"] as JsonObject ?? new JsonObject();
    AddGypInstall(data, scripts, contents);
    AddServerStart(data, scripts, contents);
    AddAuthors(data, contents);
    AddReadme(data, contents);
    ExpandMans(data, contents);
    ExpandBinDirectory(data, contents);
    FillTypes(data, contents);

    // ... and the ones that only look at the manifest.
    FixName(data);
    FixVersion(data);
    RemoveUnderscoreAttributes(data);
    string name = JsJson.AsString(data["name"])!;
    string version = JsJson.AsString(data["version"]) ?? "";
    if (name.Length > 0 && version.Length > 0) {
      data["_id"] = $"{name}@{version}";
    }

    FixBundledDependenciesTypo(data);
    FixBundleDependencies(data, true);
    FixScripts(data);
    FixRepository(data);
    FixDependencies(data);
    NormalizeData(data);

    // libnpmpublish: the version must be strict SemVer once cleaned.
    string? clean = NpmVersion.Clean(version);
    if (clean == null || NpmVersion.Parse(clean) == null) {
      throw new InvalidManifestException($"Invalid version: \"{version}\".");
    }

    if (!PackageName.IsValid(name)) {
      throw new InvalidManifestException($"Invalid name: \"{name}\".");
    }

    data["version"] = clean;
    data["_id"] = $"{name}@{clean}";

    // What the registry does with a publish: the readme moves to the
    // packument, "files" is dropped, two fields are added.
    string? readme = JsJson.AsString(data["readme"]);
    if (readme == C_NO_README || readme?.Length == 0) {
      readme = null;
    }

    if (readme is { Length: > C_MAX_README_CHARS }) {
      readme = readme[..C_MAX_README_CHARS];
    }

    string? readme_file = JsJson.AsString(data["readmeFilename"]);
    data.Remove("readme");
    // The registry keeps the file name only at the top of the packument.
    data.Remove("readmeFilename");
    data.Remove("files");
    if (!data.ContainsKey("directories")) {
      data["directories"] = new JsonObject();
    }

    data["_hasShrinkwrap"] = contents.HasShrinkwrap;
    data.Remove("dist");
    return new NormalizedManifest {
      Name = name,
      Version = clean,
      Manifest = data,
      Readme = readme,
      ReadmeFilename = readme == null ? null : readme_file
    };
  }

  private static void FixName(JsonObject data) {
    JsonNode? node = data["name"];
    if (!JsJson.IsTruthy(node)) {
      throw new InvalidManifestException("package.json has no name.");
    }

    string? name = JsJson.AsString(node);
    if (name == null) {
      throw new InvalidManifestException("name field must be a string.");
    }

    string trimmed = name.Trim();
    if (trimmed != name) {
      data["name"] = trimmed;
    }

    if (!PackageName.IsValid(trimmed)) {
      throw new InvalidManifestException(
        $"Invalid name: {JsJson.ToJsString(node)}."
      );
    }
  }

  private static void FixVersion(JsonObject data) {
    JsonNode? node = data["version"];
    if (!JsJson.IsTruthy(node)) {
      data["version"] = "";
      return;
    }

    string? clean = NpmVersion.Clean(JsJson.AsString(node));
    if (clean == null) {
      throw new InvalidManifestException(
        $"Invalid version: \"{JsJson.ToJsString(node)}\"."
      );
    }

    data["version"] = clean;
  }

  private static void RemoveUnderscoreAttributes(JsonObject data) {
    foreach (string key in data.Select(p => p.Key).ToList()) {
      if (key.StartsWith('_')) {
        data.Remove(key);
      }
    }
  }

  private static void FixBundledDependenciesTypo(JsonObject data) {
    if (!data.ContainsKey("bundleDependencies") &&
        data.ContainsKey("bundledDependencies")) {
      data["bundleDependencies"] = JsJson.Clone(data["bundledDependencies"]);
    }

    data.Remove("bundledDependencies");
  }

  private static void FixBundleDependencies(JsonObject data,
                                            bool delete_false) {
    JsonNode? bd = data["bundleDependencies"];
    if (JsJson.IsFalse(bd) && !delete_false) {
      data["bundleDependencies"] = new JsonArray();
    } else if (JsJson.IsTrue(bd)) {
      data["bundleDependencies"] = new JsonArray(
        Keys(data["dependencies"]).Select(k => (JsonNode?)k).ToArray()
      );
    } else if (JsJson.IsTruthy(bd) && JsJson.IsObjectLike(bd)) {
      if (bd is JsonObject obj) {
        data["bundleDependencies"] = new JsonArray(
          obj.Select(p => (JsonNode?)p.Key).ToArray()
        );
      }
    } else if (data.ContainsKey("bundleDependencies")) {
      data.Remove("bundleDependencies");
    }
  }

  private static void FixScripts(JsonObject data) {
    if (!data.ContainsKey("scripts")) {
      return;
    }

    JsonNode? scripts = data["scripts"];
    if (scripts == null) {
      // typeof null === "object": kept, nothing to iterate.
      return;
    }

    if (scripts is JsonObject obj) {
      foreach (KeyValuePair<string, JsonNode?> pair in obj.ToList()) {
        string? script = JsJson.AsString(pair.Value);
        if (script == null) {
          obj.Remove(pair.Key);
        } else if (ScriptPathRegex().IsMatch(script)) {
          obj[pair.Key] = ScriptPathRegex().Replace(script, "", 1);
        }
      }
    } else if (scripts is not JsonArray) {
      data.Remove("scripts");
    }
  }

  private static void FixRepository(JsonObject data) {
    JsonNode? repositories = data["repositories"];
    if (JsJson.IsTruthy(repositories)) {
      // data.repository = data.repositories[0], whatever that is.
      JsonNode? first = repositories switch {
        JsonArray array => array.Count > 0 ? JsJson.Clone(array[0]) : null,
        JsonObject obj => JsJson.Clone(obj["0"]),
        _ => JsJson.AsString(repositories) is { Length: > 0 } text
               ? JsonValue.Create(text[..1])
               : null
      };
      if (first == null) {
        data.Remove("repository");
      } else {
        data["repository"] = first;
      }
    }

    JsonNode? repository = data["repository"];
    if (!JsJson.IsTruthy(repository)) {
      return;
    }

    if (JsJson.AsString(repository) is { } url_text) {
      data["repository"] = new JsonObject {
        ["type"] = "git",
        ["url"] = url_text
      };
      repository = data["repository"];
    }

    if (repository is JsonObject repo &&
        JsJson.IsTruthy(repo["url"]) &&
        HostedGitInfo.FromUrl(JsJson.AsString(repo["url"])) is { } hosted) {
      repo["url"] = hosted.DefaultRepresentation == "shortcut"
                      ? hosted.Https()
                      : hosted.ToString();
    }
  }

  private static void FixDependencies(JsonObject data) {
    foreach (string type in S_DEPENDENCY_TYPES_) {
      JsonNode? deps = data[type];
      if (!JsJson.IsTruthy(deps)) {
        continue;
      }

      JsonArray? list = null;
      if (JsJson.AsString(deps) is { } text) {
        list = new JsonArray(
          DependencySplitRegex()
            .Split(text.Trim())
            .Select(s => (JsonNode?)s)
            .ToArray()
        );
      } else if (deps is JsonArray array) {
        list = array;
      }

      if (list != null) {
        JsonObject converted = new();
        foreach (JsonNode? item in list) {
          if (JsJson.AsString(item) is not { } spec) {
            continue;
          }

          // dep.trim().split(/(:?[@\s><=])/): name, then the rest joined.
          string[] parts = DependencySpecRegex().Split(spec.Trim());
          string dependency = parts[0];
          string range = string.Join("", parts.Skip(1));
          if (range.StartsWith('@')) {
            range = range[1..];
          }

          converted[dependency] = range.Trim();
        }

        data[type] = converted;
      }
    }

    foreach (string type in new[] {
               "dependencies",
               "devDependencies"
             }) {
      if (!data.ContainsKey(type)) {
        continue;
      }

      JsonNode? deps = data[type];
      if (!JsJson.IsTruthy(deps) || !JsJson.IsObjectLike(deps)) {
        data.Remove(type);
        continue;
      }

      if (deps is not JsonObject obj) {
        continue;
      }

      foreach (KeyValuePair<string, JsonNode?> pair in obj.ToList()) {
        string? range = JsJson.AsString(pair.Value);
        if (range == null) {
          obj.Remove(pair.Key);
          continue;
        }

        string? hosted = HostedGitInfo.FromUrl(range)?.ToString();
        if (hosted != null && hosted != range) {
          obj[pair.Key] = hosted;
        }
      }
    }
  }

  private static void AddGypInstall(JsonObject data, JsonObject scripts,
                                    TarballContents contents) {
    if (JsJson.IsTruthy(scripts["install"]) ||
        JsJson.IsTruthy(scripts["preinstall"]) ||
        JsJson.IsFalse(data["gypfile"])) {
      return;
    }

    bool has_gyp = contents.Files.Any(
      f => !f.Contains('/') && f.EndsWith(".gyp", StringComparison.Ordinal)
    );
    if (!has_gyp) {
      return;
    }

    scripts["install"] = "node-gyp rebuild";
    SetScripts(data, scripts);
    data["gypfile"] = true;
  }

  private static void AddServerStart(JsonObject data, JsonObject scripts,
                                     TarballContents contents) {
    if (JsJson.IsTruthy(scripts["start"]) ||
        !(contents.HasFile("server.js") ||
          contents.Files.Any(
            f => f.StartsWith("server.js/", StringComparison.Ordinal)
          ))) {
      return;
    }

    scripts["start"] = "node server.js";
    SetScripts(data, scripts);
  }

  private static void SetScripts(JsonObject data, JsonObject scripts) {
    if (!ReferenceEquals(data["scripts"], scripts)) {
      data["scripts"] = scripts.Parent == null
                          ? scripts
                          : scripts.DeepClone();
    }
  }

  private static void AddAuthors(JsonObject data, TarballContents contents) {
    if (JsJson.IsTruthy(data["contributors"]) ||
        !contents.RootTexts.TryGetValue("AUTHORS", out string? text)) {
      return;
    }

    JsonArray authors = new();
    foreach (string line in NewlineRegex().Split(text)) {
      string cleaned = CommentLineRegex().Replace(line, "").Trim();
      if (cleaned.Length > 0) {
        authors.Add(cleaned);
      }
    }

    data["contributors"] = authors;
  }

  private static void AddReadme(JsonObject data, TarballContents contents) {
    if (JsJson.IsTruthy(data["readme"])) {
      return;
    }

    string? chosen = null;
    foreach (string file in contents.Files) {
      if (file.Contains('/') ||
          !(file.Equals("README", StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith("README.", StringComparison.OrdinalIgnoreCase))) {
        continue;
      }

      if (MarkdownRegex().IsMatch(file)) {
        chosen = file;
        break;
      }

      if (file.EndsWith("README", StringComparison.Ordinal)) {
        chosen = file;
      }
    }

    if (chosen != null &&
        contents.RootTexts.TryGetValue(chosen, out string? text)) {
      data["readme"] = text;
      data["readmeFilename"] = chosen;
    }

    if (!JsJson.IsTruthy(data["readme"])) {
      data["readme"] = C_NO_README;
    }
  }

  private static void ExpandMans(JsonObject data, TarballContents contents) {
    if (data["directories"] is JsonObject directories &&
        JsJson.AsString(directories["man"]) is { Length: > 0 } man_dir &&
        !JsJson.IsTruthy(data["man"])) {
      string dir = SecureAndUnixifyPath(man_dir);
      string prefix = dir.Length == 0 ? "" : dir.TrimEnd('/') + "/";
      JsonArray mans = new();
      foreach (string file in contents.Files) {
        if (file.StartsWith(prefix, StringComparison.Ordinal) &&
            ManPageRegex().IsMatch(file)) {
          mans.Add(file);
        }
      }

      data["man"] = mans;
    }

    // normalizePackageMan
    JsonNode? man = data["man"];
    if (JsJson.IsTruthy(man)) {
      List<JsonNode?> items = man is JsonArray array
                                ? array.ToList()
                                : [man];
      JsonArray result = new();
      foreach (JsonNode? item in items) {
        if (JsJson.AsString(item) is { } path) {
          result.Add(SecureAndUnixifyPath(path));
        }
      }

      if (result.Count > 0) {
        data["man"] = result;
        return;
      }
    }

    data.Remove("man");
  }

  private static void ExpandBinDirectory(JsonObject data,
                                         TarballContents contents) {
    if (data["directories"] is JsonObject directories &&
        JsJson.AsString(directories["bin"]) is { Length: > 0 } bin_dir &&
        !JsJson.IsTruthy(data["bin"])) {
      string bin_path = SecureAndUnixifyPath(bin_dir);
      string prefix = bin_path.Length == 0 ? "" : bin_path.TrimEnd('/') + "/";
      JsonObject bins = new();
      HashSet<string> seen = new(StringComparer.Ordinal);
      foreach (string file in contents.Files) {
        if (!file.StartsWith(prefix, StringComparison.Ordinal)) {
          continue;
        }

        // glob("**") lists files and the folders leading to them.
        string relative = file[prefix.Length..];
        string[] segments = relative.Split('/');
        for (int i = 1; i <= segments.Length; i++) {
          string entry = string.Join('/', segments.Take(i));
          // glob skips dot files and folders.
          if (segments.Take(i).Any(s => s.StartsWith('.')) ||
              !seen.Add(entry)) {
            continue;
          }

          bins[segments[i - 1]] = $"{bin_path}/{SecureAndUnixifyPath(entry)}";
        }
      }

      data["bin"] = bins;
      return;
    }

    NormalizePackageBin(data);
  }

  private static void FillTypes(JsonObject data, TarballContents contents) {
    if (data.ContainsKey("types") || data.ContainsKey("typings")) {
      return;
    }

    JsonNode? main = data["main"];
    string? index = JsJson.IsTruthy(main) ? JsJson.AsString(main) : "index.js";
    if (index == null) {
      // npm publish would fail here ("main" must be a string); keep going.
      return;
    }

    string extless = NodePath.Join(
      NodePath.Dirname(index),
      NodePath.Basename(index, NodePath.Extname(index))
    );
    string dts = $"./{extless}.d.ts";
    string file = NodePath.Normalize(dts);
    if (!file.StartsWith("../", StringComparison.Ordinal) &&
        !file.StartsWith('/') &&
        (contents.HasFile(file) ||
         contents.Files.Any(
           f => f.StartsWith(file + "/", StringComparison.Ordinal)
         ))) {
      data["types"] = dts;
    }
  }

  /// <summary>npm-normalize-package-bin.</summary>
  private static void NormalizePackageBin(JsonObject data) {
    JsonNode? bin = data["bin"];
    if (!JsJson.IsTruthy(bin)) {
      data.Remove("bin");
      return;
    }

    if (JsJson.AsString(bin) is { } single &&
        JsJson.AsString(data["name"]) is { Length: > 0 } name) {
      data["bin"] = new JsonObject {
        [name] = single
      };
    } else if (bin is JsonArray array) {
      JsonObject converted = new();
      foreach (JsonNode? item in array) {
        string text = JsJson.ToJsString(item);
        converted[NodePath.Basename(text)] = text;
      }

      data["bin"] = converted;
    }

    if (data["bin"] is not JsonObject bins) {
      data.Remove("bin");
      return;
    }

    foreach (KeyValuePair<string, JsonNode?> pair in bins.ToList()) {
      string? target_text = JsJson.AsString(pair.Value);
      if (target_text == null) {
        bins.Remove(pair.Key);
        continue;
      }

      string base_name = NodePath.Basename(SecureAndUnixifyPath(pair.Key));
      if (base_name.Length == 0) {
        bins.Remove(pair.Key);
        continue;
      }

      string target = SecureAndUnixifyPath(target_text);
      if (target.Length == 0) {
        bins.Remove(pair.Key);
        continue;
      }

      if (base_name != pair.Key) {
        bins.Remove(pair.Key);
      }

      bins[base_name] = target;
    }

    if (bins.Count == 0) {
      data.Remove("bin");
    }
  }

  /// <summary>The part of normalize-package-data npm still runs.</summary>
  private static void NormalizeData(JsonObject data) {
    // description
    if (data.ContainsKey("description") &&
        JsJson.IsTruthy(data["description"]) &&
        !JsJson.IsString(data["description"])) {
      data.Remove("description");
    }

    string? readme = JsJson.AsString(data["readme"]);
    if (JsJson.IsTruthy(data["readme"]) &&
        !JsJson.IsTruthy(data["description"]) &&
        readme != C_NO_README) {
      data["description"] =
        ExtractDescription(JsJson.ToJsString(data["readme"]));
    }

    // modules
    if (JsJson.IsTruthy(data["modules"])) {
      data.Remove("modules");
    }

    // files
    JsonNode? files = data["files"];
    if (JsJson.IsTruthy(files) && files is not JsonArray) {
      data.Remove("files");
    } else if (files is JsonArray file_list) {
      data["files"] = new JsonArray(
        file_list.Where(f => JsJson.IsTruthy(f) && JsJson.IsString(f))
                 .Select(JsJson.Clone)
                 .ToArray()
      );
    }

    // man
    if (JsJson.AsString(data["man"]) is { Length: > 0 } man) {
      data["man"] = new JsonArray(man);
    }

    FixBugs(data);
    FixKeywords(data);
    FixBundleDependencyList(data);
    FixHomepage(data);
    if (!JsJson.IsTruthy(data["readme"])) {
      data["readme"] = C_NO_README;
    }

    // people
    if (JsJson.IsTruthy(data["author"])) {
      data["author"] = StringifyPerson(data["author"]);
    }

    foreach (string set in new[] {
               "maintainers",
               "contributors"
             }) {
      if (data[set] is JsonArray people) {
        data[set] = new JsonArray(people.Select(StringifyPerson).ToArray());
      }
    }
  }

  private static void FixBugs(JsonObject data) {
    JsonNode? bugs = data["bugs"];
    if (!JsJson.IsTruthy(bugs)) {
      if (data["repository"] is JsonObject repository &&
          JsJson.IsTruthy(repository["url"]) &&
          HostedGitInfo.FromUrl(JsJson.AsString(repository["url"])) is {
          } hosted &&
          hosted.Bugs() is { } url) {
        data["bugs"] = new JsonObject {
          ["url"] = url
        };
      }

      return;
    }

    if (JsJson.AsString(bugs) is { } text) {
      if (IsEmail(text)) {
        data["bugs"] = new JsonObject {
          ["email"] = text
        };
      } else if (WhatwgUrl.CanParse(text)) {
        data["bugs"] = new JsonObject {
          ["url"] = text
        };
      }
    } else if (bugs is JsonObject old) {
      foreach (string key in new[] {
                 "web",
                 "name"
               }) {
        if (old.ContainsKey(key)) {
          old["url"] = JsJson.Clone(old[key]);
          old.Remove(key);
        }
      }

      JsonObject fixed_bugs = new();
      if (JsJson.IsTruthy(old["url"])) {
        if (JsJson.AsString(old["url"]) is { } url && WhatwgUrl.CanParse(url)) {
          fixed_bugs["url"] = url;
        }
      }

      if (JsJson.IsTruthy(old["email"])) {
        if (JsJson.AsString(old["email"]) is { } email && IsEmail(email)) {
          fixed_bugs["email"] = email;
        }
      }

      data["bugs"] = fixed_bugs;
    }

    if (data["bugs"] is JsonObject result &&
        !JsJson.IsTruthy(result["email"]) &&
        !JsJson.IsTruthy(result["url"])) {
      data.Remove("bugs");
    }
  }

  private static void FixKeywords(JsonObject data) {
    if (JsJson.AsString(data["keywords"]) is { } text) {
      data["keywords"] = new JsonArray(
        KeywordSplitRegex().Split(text).Select(k => (JsonNode?)k).ToArray()
      );
    }

    JsonNode? keywords = data["keywords"];
    if (JsJson.IsTruthy(keywords) && keywords is not JsonArray) {
      data.Remove("keywords");
    } else if (keywords is JsonArray list) {
      data["keywords"] = new JsonArray(
        list.Where(k => JsJson.AsString(k) is { Length: > 0 })
            .Select(JsJson.Clone)
            .ToArray()
      );
    }
  }

  private static void FixBundleDependencyList(JsonObject data) {
    if (JsJson.IsTruthy(data["bundledDependencies"]) &&
        !JsJson.IsTruthy(data["bundleDependencies"])) {
      data["bundleDependencies"] = JsJson.Clone(data["bundledDependencies"]);
      data.Remove("bundledDependencies");
    }

    JsonNode? bd = data["bundleDependencies"];
    if (JsJson.IsTruthy(bd) && bd is not JsonArray) {
      data.Remove("bundleDependencies");
    } else if (bd is JsonArray list) {
      JsonArray kept = new();
      foreach (JsonNode? item in list) {
        if (JsJson.AsString(item) is not { Length: > 0 } dependency) {
          continue;
        }

        if (data["dependencies"] is not JsonObject dependencies) {
          dependencies = new JsonObject();
          data["dependencies"] = dependencies;
        }

        if (!dependencies.ContainsKey(dependency)) {
          dependencies[dependency] = "*";
        }

        kept.Add(dependency);
      }

      data["bundleDependencies"] = kept;
    }
  }

  private static void FixHomepage(JsonObject data) {
    if (!JsJson.IsTruthy(data["homepage"]) &&
        data["repository"] is JsonObject repository &&
        JsJson.IsTruthy(repository["url"]) &&
        HostedGitInfo.FromUrl(
          JsJson.AsString(repository["url"])
        ) is { } hosted) {
      data["homepage"] = hosted.Docs();
    }

    JsonNode? homepage = data["homepage"];
    if (!JsJson.IsTruthy(homepage)) {
      return;
    }

    if (JsJson.AsString(homepage) is not { } text) {
      data.Remove("homepage");
    } else if (!WhatwgUrl.CanParse(text)) {
      data["homepage"] = "http://" + text;
    }
  }

  /// <summary>
  ///   Parses <c>"Name &lt;email&gt; (url)"</c> people into objects.
  /// </summary>
  private static JsonNode StringifyPerson(JsonNode? person) {
    string text;
    if (JsJson.AsString(person) is { } value) {
      text = value;
    } else {
      JsonObject? obj = person as JsonObject;
      string name = JsJson.IsTruthy(obj?["name"])
                      ? JsJson.ToJsString(obj!["name"])
                      : "";
      JsonNode? url = JsJson.IsTruthy(obj?["url"]) ? obj!["url"] : obj?["web"];
      string wrapped_url = JsJson.IsTruthy(url)
                             ? " (" + JsJson.ToJsString(url) + ")"
                             : "";
      JsonNode? email = JsJson.IsTruthy(obj?["email"])
                          ? obj!["email"]
                          : obj?["mail"];
      string wrapped_email = JsJson.IsTruthy(email)
                               ? " <" + JsJson.ToJsString(email) + ">"
                               : "";
      text = name + wrapped_email + wrapped_url;
    }

    JsonObject parsed = new();
    Match matched_name = PersonNameRegex().Match(text);
    if (matched_name.Success && matched_name.Value.Trim().Length > 0) {
      parsed["name"] = matched_name.Value.Trim();
    }

    Match matched_email = PersonEmailRegex().Match(text);
    if (matched_email.Success) {
      parsed["email"] = matched_email.Groups[1].Value;
    }

    Match matched_url = PersonUrlRegex().Match(text);
    if (matched_url.Success) {
      parsed["url"] = matched_url.Groups[1].Value;
    }

    return parsed;
  }

  /// <summary>The first paragraph of a markdown readme.</summary>
  private static string ExtractDescription(string readme) {
    string[] lines = readme.Trim().Split('\n');
    int start = 0;
    while (start < lines.Length &&
           HeadingOrEmptyRegex().IsMatch(lines[start].Trim())) {
      start++;
    }

    int end = start + 1;
    while (end < lines.Length && lines[end].Trim().Length > 0) {
      end++;
    }

    return start >= lines.Length
             ? ""
             : string.Join(' ', lines[start..Math.Min(end, lines.Length)])
                     .Trim();
  }

  private static bool IsEmail(string text) {
    int at = text.IndexOf('@');
    return at >= 0 && at < text.LastIndexOf('.');
  }

  /// <summary>
  ///   npm's secureAndUnixifyPath: relative, forward slashes, no way out of
  ///   the package folder (empty when it would leave it).
  /// </summary>
  public static string SecureAndUnixifyPath(string reference) {
    string unixified = Unixify(reference);
    string secured = Unixify(NodePath.Join(".", NodePath.Join("/", unixified)));
    return secured.StartsWith("./", StringComparison.Ordinal) ? "" : secured;
  }

  private static string Unixify(string reference) {
    return reference.Replace('\\', '/').Replace(':', '/');
  }

  private static IEnumerable<string> Keys(JsonNode? node) {
    return node switch {
      JsonObject obj => obj.Select(p => p.Key),
      JsonArray array => Enumerable.Range(0, array.Count)
                                   .Select(i => i.ToString()),
      _ => []
    };
  }

  [GeneratedRegex(@"^(\.[/\\])?node_modules[/\\].bin[\\/]")]
  private static partial Regex ScriptPathRegex();

  [GeneratedRegex(@"[\n\r\s\t ,]+")]
  private static partial Regex DependencySplitRegex();

  [GeneratedRegex(@"(:?[@\s><=])")]
  private static partial Regex DependencySpecRegex();

  [GeneratedRegex(@"\r?\n")]
  private static partial Regex NewlineRegex();

  [GeneratedRegex(@"^\s*#.*$")]
  private static partial Regex CommentLineRegex();

  [GeneratedRegex(@"\.m?a?r?k?d?o?w?n?$", RegexOptions.IgnoreCase)]
  private static partial Regex MarkdownRegex();

  [GeneratedRegex(@"\.[0-9]$")]
  private static partial Regex ManPageRegex();

  [GeneratedRegex(@",\s+")]
  private static partial Regex KeywordSplitRegex();

  [GeneratedRegex(@"^([^(<]+)")]
  private static partial Regex PersonNameRegex();

  [GeneratedRegex(@"<([^<>]+)>")]
  private static partial Regex PersonEmailRegex();

  [GeneratedRegex(@"\(([^()]+)\)")]
  private static partial Regex PersonUrlRegex();

  [GeneratedRegex("^(#|$)")]
  private static partial Regex HeadingOrEmptyRegex();
}
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Noodle.Tests;

/// <summary>Builds npm tarballs for tests.</summary>
public static class TestTarballs {
  /// <summary>
  ///   A tarball with <c>package/package.json</c> and the given extra files.
  /// </summary>
  public static byte[] Create(string name, string version,
                              JsonObject? extra = null,
                              IDictionary<string, string>? files = null,
                              string root = "package") {
    JsonObject manifest = new() {
      ["name"] = name,
      ["version"] = version,
      ["description"] = $"Test package {name}"
    };
    if (extra != null) {
      foreach (KeyValuePair<string, JsonNode?> pair in extra) {
        manifest[pair.Key] = pair.Value?.DeepClone();
      }
    }

    Dictionary<string, string> all = new(StringComparer.Ordinal) {
      ["package.json"] = manifest.ToJsonString(),
      ["index.js"] = "module.exports = 42;\n"
    };
    if (files != null) {
      foreach (KeyValuePair<string, string> file in files) {
        all[file.Key] = file.Value;
      }
    }

    return Pack(
      all.ToDictionary(
        p => $"{root}/{p.Key}",
        p => Encoding.UTF8.GetBytes(p.Value)
      )
    );
  }

  /// <summary>Gzipped tar of the given paths and contents.</summary>
  public static byte[] Pack(IDictionary<string, byte[]> entries) {
    using MemoryStream output = new();
    using (GZipStream gzip = new(output, CompressionLevel.Optimal, true))
    using (TarWriter tar = new(gzip, TarEntryFormat.Ustar, false)) {
      foreach (KeyValuePair<string, byte[]> entry in entries) {
        UstarTarEntry tar_entry = new(TarEntryType.RegularFile, entry.Key) {
          DataStream = new MemoryStream(entry.Value),
          ModificationTime = new DateTimeOffset(
            1985,
            10,
            26,
            8,
            15,
            0,
            TimeSpan.Zero
          )
        };
        tar.WriteEntry(tar_entry);
      }
    }

    return output.ToArray();
  }

  /// <summary>
  ///   Real tarballs from registry.npmjs.org, copied into the test data folder.
  /// </summary>
  public static byte[] Real(string file) {
    return File.ReadAllBytes(
      Path.Combine(AppContext.BaseDirectory, "TestData", file)
    );
  }
}
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Noodle.Api.Npm;

/// <summary>What npm would see after unpacking a tarball.</summary>
public sealed class TarballContents {
  /// <summary><c>package.json</c> as stored in the tarball.</summary>
  public required byte[] PackageJson { get; init; }

  /// <summary>
  ///   Entries (files and folders), as npm's registry counts them
  ///   (<c>dist.fileCount</c>).
  /// </summary>
  public required int FileCount { get; init; }

  /// <summary>
  ///   Sum of the regular files' sizes (<c>dist.unpackedSize</c>).
  /// </summary>
  public required long UnpackedSize { get; init; }

  /// <summary>
  ///   An <c>npm-shrinkwrap.json</c> sits next to <c>package.json</c>.
  /// </summary>
  public required bool HasShrinkwrap { get; init; }

  /// <summary>
  ///   Paths of the regular files relative to the package folder, in archive
  ///   order (the first component, normally <c>package/</c>, is stripped
  ///   like npm does).
  /// </summary>
  public required IReadOnlyList<string> Files { get; init; }

  /// <summary>
  ///   Text of the top-level files npm's publish step reads
  ///   (<c>README*</c>, <c>AUTHORS</c>), by their name in the archive.
  /// </summary>
  public required IReadOnlyDictionary<string, string> RootTexts { get; init; }

  public bool HasFile(string path) {
    foreach (string file in Files) {
      if (file == path) {
        return true;
      }
    }

    return false;
  }
}

/// <summary>The upload is not a usable npm tarball.</summary>
public sealed class InvalidTarballException(string message)
  : Exception(message);

/// <summary>
///   Reads an uploaded tarball completely: every gzip and tar byte, so that
///   truncated or damaged files are rejected rather than stored. Nothing is
///   written to disk and nothing from the archive is executed.
/// </summary>
public static class TarballInspector {
  public const int C_MAX_PACKAGE_JSON_BYTES = 16 * 1024 * 1024;

  /// <summary>Largest README (or AUTHORS) that is read.</summary>
  public const int C_MAX_TEXT_BYTES = 1024 * 1024;

  private const int C_MAX_ENTRIES_ = 1_000_000;

  public static TarballContents Inspect(string path, long max_unpacked_bytes) {
    using FileStream file = File.OpenRead(path);
    if (file.Length < 18) {
      throw new InvalidTarballException("The file is too small for a tarball.");
    }

    Span<byte> magic = stackalloc byte[2];
    file.ReadExactly(magic);
    if (magic[0] != 0x1F || magic[1] != 0x8B) {
      throw new InvalidTarballException("Not a gzip file (.tgz).");
    }

    // gzip's trailer: CRC-32 and length (mod 2^32) of the uncompressed data.
    Span<byte> trailer = stackalloc byte[8];
    file.Seek(-8, SeekOrigin.End);
    file.ReadExactly(trailer);
    uint expected_crc = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    uint expected_length =
      BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
    file.Seek(0, SeekOrigin.Begin);

    try {
      using GZipStream gzip = new(file, CompressionMode.Decompress);
      using CheckedStream checked_stream = new(gzip, max_unpacked_bytes);
      TarballContents contents = ReadArchive(checked_stream);
      // The tar end marker is not the end of the gzip stream: read the rest,
      // or the trailer cannot be checked.
      checked_stream.Drain();
      if (checked_stream.Crc != expected_crc ||
          (uint)checked_stream.BytesRead != expected_length) {
        throw new InvalidTarballException(
          "The tarball is damaged or truncated (gzip checksum mismatch)."
        );
      }

      return contents;
    } catch (InvalidDataException ex) {
      throw new InvalidTarballException(
        $"The tarball is damaged (gzip: {ex.Message})."
      );
    } catch (InvalidTarException ex) {
      throw new InvalidTarballException(
        $"The tarball is damaged: {ex.Message}"
      );
    } catch (EndOfStreamException) {
      throw new InvalidTarballException("The tarball is truncated.");
    }
  }

  private static TarballContents ReadArchive(Stream stream) {
    TarArchive archive = new(stream);
    byte[]? package_json = null;
    bool shrinkwrap = false;
    int files = 0;
    long unpacked = 0;
    int entries = 0;
    List<string> paths = [];
    Dictionary<string, string> texts = new(StringComparer.Ordinal);
    while (archive.Next() is { } entry) {
      if (++entries > C_MAX_ENTRIES_) {
        throw new InvalidTarballException("The tarball has too many entries.");
      }

      // The registry counts folders too (@types/node: 67 files and 9
      // folders make a fileCount of 76).
      files++;
      if (entry.Kind != TarEntryKind.FILE) {
        continue;
      }

      unpacked += entry.Size;
      string? relative = StripRoot(entry.Path);
      if (relative == null) {
        continue;
      }

      paths.Add(relative);
      switch (relative) {
        case "package.json":
          // When several top folders hold one, the last extracted wins.
          package_json = archive.ReadAll(C_MAX_PACKAGE_JSON_BYTES) ??
                         throw new InvalidTarballException(
                           "package.json is too large."
                         );
          break;
        case "npm-shrinkwrap.json":
          shrinkwrap = true;
          break;
        default:
          if (!relative.Contains('/') && IsRootText(relative)) {
            // Larger ones are cut off; readmes are capped anyway.
            texts[relative] = Encoding.UTF8.GetString(
              archive.ReadPrefix(C_MAX_TEXT_BYTES)
            );
          }

          break;
      }
    }

    if (package_json == null) {
      throw new InvalidTarballException(
        "The tarball contains no package.json (expected package/package.json)."
      );
    }

    return new TarballContents {
      PackageJson = package_json,
      FileCount = files,
      UnpackedSize = unpacked,
      HasShrinkwrap = shrinkwrap,
      Files = paths,
      RootTexts = texts
    };
  }

  /// <summary>
  ///   The path below the top folder (npm unpacks with <c>strip: 1</c>), or
  ///   <c>null</c> for entries outside of it.
  /// </summary>
  public static string? StripRoot(string path) {
    string normalized = path.Replace('\\', '/');
    while (normalized.StartsWith("./", StringComparison.Ordinal)) {
      normalized = normalized[2..];
    }

    normalized = normalized.TrimStart('/');
    int slash = normalized.IndexOf('/');
    if (slash < 0) {
      return null;
    }

    string relative = normalized[(slash + 1)..];
    if (relative.Length == 0) {
      return null;
    }

    foreach (string segment in relative.Split('/')) {
      if (segment == "..") {
        // npm refuses to unpack such entries.
        return null;
      }
    }

    return relative;
  }

  private static bool IsRootText(string name) {
    return name == "AUTHORS" ||
           name.Equals("README", StringComparison.OrdinalIgnoreCase) ||
           name.StartsWith("README.", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  ///   Counts and checksums what is read, and enforces the size limit.
  /// </summary>
  private sealed class CheckedStream(Stream inner, long max_bytes) : Stream {
    private readonly Crc32 crc_ = new();

    public long BytesRead { get; private set; }

    public uint Crc => crc_.GetCurrentHashAsUInt32();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => BytesRead;

    public override long Position {
      get => BytesRead;
      set => throw new NotSupportedException();
    }

    public void Drain() {
      byte[] buffer = new byte[64 * 1024];
      while (Read(buffer, 0, buffer.Length) > 0) {
      }
    }

    public override int Read(byte[] buffer, int offset, int count) {
      return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer) {
      int read = inner.Read(buffer);
      if (read > 0) {
        BytesRead += read;
        if (BytesRead > max_bytes) {
          throw new InvalidTarballException(
            $"The tarball unpacks to more than {max_bytes:N0} bytes."
          );
        }

        crc_.Append(buffer[..read]);
      }

      return read;
    }

    public override void Flush() {
    }

    public override long Seek(long offset, SeekOrigin origin) {
      throw new NotSupportedException();
    }

    public override void SetLength(long value) {
      throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) {
      throw new NotSupportedException();
    }
  }
}
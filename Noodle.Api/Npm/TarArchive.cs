using System.Globalization;
using System.Text;

namespace Noodle.Api.Npm;

/// <summary>The kind of a tar entry, as far as npm cares.</summary>
public enum TarEntryKind {
  FILE,
  DIRECTORY,

  /// <summary>Hard and symbolic links: npm skips them, and so do we.</summary>
  LINK,

  /// <summary>Devices, FIFOs and unknown types.</summary>
  OTHER
}

public sealed class TarEntry {
  public required string Path { get; init; }
  public required TarEntryKind Kind { get; init; }
  public required long Size { get; init; }
}

/// <summary>The archive is not a readable tar file.</summary>
public sealed class InvalidTarException(string message) : Exception(message);

/// <summary>
///   A forward-only tar reader for hostile input: ustar, GNU (long names,
///   base-256 sizes) and pax headers, with every header checksum verified
///   and the size of metadata records bounded.
/// </summary>
public sealed class TarArchive {
  private const int C_BLOCK_ = 512;

  // pax and GNU long-name records hold a few paths; anything larger is an
  // attempt to make us allocate.
  private const int C_MAX_META_BYTES_ = 1024 * 1024;

  private readonly byte[] block_ = new byte[C_BLOCK_];
  private readonly Stream stream_;
  private string? gnu_long_name_;
  private Dictionary<string, string> global_pax_ = new(StringComparer.Ordinal);
  private Dictionary<string, string>? pax_;
  private long remaining_;
  private long padding_;
  private bool finished_;

  public TarArchive(Stream stream) {
    stream_ = stream;
  }

  /// <summary>
  ///   Advances to the next entry (skipping the rest of the current one), or
  ///   returns <c>null</c> at the end of the archive.
  /// </summary>
  public TarEntry? Next() {
    Skip(remaining_ + padding_);
    remaining_ = 0;
    padding_ = 0;
    while (!finished_) {
      if (!ReadBlock(true)) {
        // The archive simply ends (no end-of-archive blocks); npm accepts
        // that, and the gzip trailer check catches real truncation.
        finished_ = true;
        return null;
      }

      if (IsZeroBlock()) {
        finished_ = true;
        return null;
      }

      VerifyChecksum();
      char type = (char)block_[156];
      long size = ParseSize();
      if (size < 0) {
        throw new InvalidTarException("Invalid entry size.");
      }

      switch (type) {
        case 'L':
          gnu_long_name_ = ReadString(ReadMeta(size)).TrimEnd('\0');
          continue;
        case 'K':
          // Long link names: links are skipped anyway.
          ReadMeta(size);
          continue;
        case 'x':
          pax_ = ParsePax(ReadMeta(size));
          continue;
        case 'g':
          global_pax_ = new Dictionary<string, string>(
            global_pax_,
            StringComparer.Ordinal
          );
          foreach (KeyValuePair<string, string> pair in
                   ParsePax(ReadMeta(size))) {
            global_pax_[pair.Key] = pair.Value;
          }

          continue;
      }

      string path = HeaderPath();
      if (pax_ != null || global_pax_.Count > 0) {
        if (TryGetPax("path", out string pax_path)) {
          path = pax_path;
        }

        if (TryGetPax("size", out string pax_size)) {
          if (!long.TryParse(
                pax_size,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out size
              )) {
            throw new InvalidTarException("Invalid pax size.");
          }
        }
      }

      if (gnu_long_name_ != null) {
        path = gnu_long_name_;
      }

      gnu_long_name_ = null;
      pax_ = null;
      TarEntryKind kind = type switch {
        '0' or '\0' or '7' => TarEntryKind.FILE,
        '5' => TarEntryKind.DIRECTORY,
        '1' or '2' => TarEntryKind.LINK,
        _ => TarEntryKind.OTHER
      };
      // Old tar writers marked directories with a trailing slash only.
      if (kind == TarEntryKind.FILE && path.EndsWith('/')) {
        kind = TarEntryKind.DIRECTORY;
      }

      // Links and directories carry no data, whatever their size says.
      if (kind is TarEntryKind.LINK or TarEntryKind.DIRECTORY) {
        size = 0;
      }

      remaining_ = size;
      padding_ = (C_BLOCK_ - size % C_BLOCK_) % C_BLOCK_;
      return new TarEntry {
        Path = path,
        Kind = kind,
        Size = size
      };
    }

    return null;
  }

  /// <summary>Reads (part of) the current entry's data.</summary>
  public int Read(Span<byte> buffer) {
    if (remaining_ == 0) {
      return 0;
    }

    int wanted = (int)Math.Min(buffer.Length, remaining_);
    int read = stream_.Read(buffer[..wanted]);
    if (read == 0) {
      throw new InvalidTarException("The archive ends inside an entry.");
    }

    remaining_ -= read;
    return read;
  }

  /// <summary>
  ///   Reads the whole current entry, or <c>null</c> when it is larger than the
  ///   limit.
  /// </summary>
  public byte[]? ReadAll(int max_bytes) {
    if (remaining_ > max_bytes) {
      return null;
    }

    byte[] data = new byte[remaining_];
    int offset = 0;
    while (offset < data.Length) {
      offset += Read(data.AsSpan(offset));
    }

    return data;
  }

  /// <summary>
  ///   Reads at most <paramref name="max_bytes" /> of the current entry.
  /// </summary>
  public byte[] ReadPrefix(int max_bytes) {
    byte[] data = new byte[Math.Min(remaining_, max_bytes)];
    int offset = 0;
    while (offset < data.Length) {
      offset += Read(data.AsSpan(offset));
    }

    return data;
  }

  private bool TryGetPax(string key, out string value) {
    if (pax_ != null && pax_.TryGetValue(key, out string? local)) {
      value = local;
      return true;
    }

    if (global_pax_.TryGetValue(key, out string? global)) {
      value = global;
      return true;
    }

    value = "";
    return false;
  }

  private string HeaderPath() {
    string name = ReadField(0, 100);
    // ustar ("ustar\0") and old GNU ("ustar  ") headers; only POSIX ustar
    // has a prefix field (GNU uses those bytes for times).
    bool ustar = block_.AsSpan(257, 6).SequenceEqual("ustar\0"u8);
    if (ustar) {
      string prefix = ReadField(345, 155);
      if (prefix.Length > 0) {
        return prefix + "/" + name;
      }
    }

    return name;
  }

  private long ParseSize() {
    ReadOnlySpan<byte> field = block_.AsSpan(124, 12);
    if ((field[0] & 0x80) != 0) {
      // GNU base-256: big endian, the first byte's high bit is the marker.
      long value = field[0] & 0x7F;
      for (int i = 1; i < field.Length; i++) {
        if (value > long.MaxValue >> 8) {
          return -1;
        }

        value = (value << 8) | field[i];
      }

      return value;
    }

    return ParseOctal(field);
  }

  private static long ParseOctal(ReadOnlySpan<byte> field) {
    long value = 0;
    bool digits = false;
    foreach (byte b in field) {
      if (b is (byte)' ' or 0) {
        if (digits) {
          break;
        }

        continue;
      }

      if (b is < (byte)'0' or > (byte)'7' || value > long.MaxValue >> 3) {
        return -1;
      }

      digits = true;
      value = value * 8 + (b - '0');
    }

    return value;
  }

  private void VerifyChecksum() {
    long expected = ParseOctal(block_.AsSpan(148, 8));
    long unsigned_sum = 0;
    long signed_sum = 0;
    for (int i = 0; i < C_BLOCK_; i++) {
      // The checksum field itself counts as spaces.
      byte b = i is >= 148 and < 156 ? (byte)' ' : block_[i];
      unsigned_sum += b;
      signed_sum += (sbyte)b;
    }

    if (expected < 0 || (expected != unsigned_sum && expected != signed_sum)) {
      throw new InvalidTarException("Tar header checksum mismatch.");
    }
  }

  private bool IsZeroBlock() {
    foreach (byte b in block_) {
      if (b != 0) {
        return false;
      }
    }

    return true;
  }

  private string ReadField(int offset, int length) {
    ReadOnlySpan<byte> field = block_.AsSpan(offset, length);
    int end = field.IndexOf((byte)0);
    return ReadString(end < 0 ? field : field[..end]);
  }

  private static string ReadString(ReadOnlySpan<byte> bytes) {
    return Encoding.UTF8.GetString(bytes);
  }

  private byte[] ReadMeta(long size) {
    if (size > C_MAX_META_BYTES_) {
      throw new InvalidTarException("Oversized tar metadata record.");
    }

    byte[] data = new byte[size];
    ReadExactly(data);
    Skip((C_BLOCK_ - size % C_BLOCK_) % C_BLOCK_);
    return data;
  }

  private static Dictionary<string, string> ParsePax(ReadOnlySpan<byte> data) {
    // Records: "<length> <key>=<value>\n", length counting the whole record.
    Dictionary<string, string> result = new(StringComparer.Ordinal);
    while (data.Length > 0) {
      int space = data.IndexOf((byte)' ');
      if (space <= 0 ||
          !int.TryParse(
            data[..space],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int length
          ) ||
          length <= space + 1 ||
          length > data.Length) {
        throw new InvalidTarException("Invalid pax record.");
      }

      ReadOnlySpan<byte> record = data[(space + 1)..length];
      if (record.Length > 0 && record[^1] == '\n') {
        record = record[..^1];
      }

      int equals = record.IndexOf((byte)'=');
      if (equals > 0) {
        result[ReadString(record[..equals])] =
          ReadString(record[(equals + 1)..]);
      }

      data = data[length..];
    }

    return result;
  }

  private bool ReadBlock(bool allow_end) {
    int offset = 0;
    while (offset < C_BLOCK_) {
      int read = stream_.Read(block_, offset, C_BLOCK_ - offset);
      if (read == 0) {
        if (offset == 0 && allow_end) {
          return false;
        }

        throw new InvalidTarException("The archive ends inside a header.");
      }

      offset += read;
    }

    return true;
  }

  private void ReadExactly(Span<byte> buffer) {
    while (buffer.Length > 0) {
      int read = stream_.Read(buffer);
      if (read == 0) {
        throw new InvalidTarException("The archive ends inside an entry.");
      }

      buffer = buffer[read..];
    }
  }

  private void Skip(long count) {
    Span<byte> buffer = stackalloc byte[4096];
    while (count > 0) {
      int read = stream_.Read(buffer[..(int)Math.Min(buffer.Length, count)]);
      if (read == 0) {
        throw new InvalidTarException("The archive ends inside an entry.");
      }

      count -= read;
    }
  }
}
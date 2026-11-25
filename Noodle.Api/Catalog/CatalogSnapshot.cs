using System.IO.Compression;
using System.Text;

namespace Noodle.Api.Catalog;

/// <summary>
///   The catalog as stored in <c>catalog/snapshot.bin.gz</c>: a compact
///   binary format (a million packages load in seconds), versioned so a
///   newer release can still read it or rebuild instead.
/// </summary>
public sealed class CatalogSnapshot {
  private const string C_MAGIC_ = "NOODLE-CATALOG";
  private const int C_FORMAT_ = 1;

  public DateTimeOffset Created { get; init; }

  /// <summary>
  ///   Changes up to here (and <see cref="RecentChanges" />) are included.
  /// </summary>
  public long WatermarkTicks { get; init; }

  public List<string> RecentChanges { get; init; } = [];
  public List<CatalogEntry> Packages { get; init; } = [];

  public List<(string Name, long Total, (int Day, long Count)[] Days)>
    Downloads { get; init; } = [];

  public void Write(Stream output) {
    using GZipStream gzip = new(output, CompressionLevel.Fastest, true);
    using BufferedStream buffered = new(gzip, 1024 * 1024);
    using BinaryWriter writer = new(buffered, Encoding.UTF8, true);
    writer.Write(C_MAGIC_);
    writer.Write(C_FORMAT_);
    writer.Write(Created.UtcTicks);
    writer.Write(WatermarkTicks);
    writer.Write(RecentChanges.Count);
    foreach (string key in RecentChanges) {
      writer.Write(key);
    }

    writer.Write(Packages.Count);
    foreach (CatalogEntry entry in Packages) {
      writer.Write(entry.Name);
      writer.Write(entry.Revision);
      writer.Write(entry.ModifiedTicks);
      writer.Write(entry.LatestTicks);
      WriteHash(writer, entry.CorgiHash);
      WriteHash(writer, entry.FullHash);
      writer.Write(entry.CorgiSize);
      writer.Write(entry.FullSize);
      writer.Write(entry.BaseUrl);
      writer.Write(entry.Latest);
      writer.Write(entry.Tags?.Length ?? 0);
      foreach (string tag in entry.Tags ?? []) {
        writer.Write(tag);
      }

      writer.Write(entry.Versions.Length);
      writer.Write(entry.Versions);
      writer.Write(entry.Search.Data.Length);
      writer.Write(entry.Search.Data);
    }

    writer.Write(Downloads.Count);
    foreach ((string name, long total, (int Day, long Count)[] days) in
             Downloads) {
      writer.Write(name);
      writer.Write(total);
      writer.Write(days.Length);
      foreach ((int day, long count) in days) {
        writer.Write(day);
        writer.Write(count);
      }
    }
  }

  /// <exception cref="InvalidDataException">
  ///   Not a snapshot this release can read.
  /// </exception>
  public static CatalogSnapshot Read(Stream input) {
    using GZipStream gzip = new(input, CompressionMode.Decompress, true);
    using BufferedStream buffered = new(gzip, 1024 * 1024);
    using BinaryReader reader = new(buffered, Encoding.UTF8, true);
    try {
      if (reader.ReadString() != C_MAGIC_ || reader.ReadInt32() != C_FORMAT_) {
        throw new InvalidDataException("Unknown catalog snapshot format.");
      }

      long created = reader.ReadInt64();
      long watermark = reader.ReadInt64();
      int recent_count = reader.ReadInt32();
      List<string> recent = new(recent_count);
      for (int i = 0; i < recent_count; i++) {
        recent.Add(reader.ReadString());
      }

      int count = reader.ReadInt32();
      List<CatalogEntry> packages = new(count);
      for (int i = 0; i < count; i++) {
        string name = reader.ReadString();
        long revision = reader.ReadInt64();
        long modified = reader.ReadInt64();
        long latest_ticks = reader.ReadInt64();
        UInt128 corgi = ReadHash(reader);
        UInt128 full = ReadHash(reader);
        long corgi_size = reader.ReadInt64();
        long full_size = reader.ReadInt64();
        string base_url = VersionPool.Intern(reader.ReadString());
        string latest = VersionPool.Intern(reader.ReadString());
        int tag_count = reader.ReadInt32();
        string[]? tags = tag_count == 0 ? null : new string[tag_count];
        for (int t = 0; t < tag_count; t++) {
          tags![t] = VersionPool.Intern(reader.ReadString());
        }

        byte[] versions = reader.ReadBytes(reader.ReadInt32());
        byte[] search = reader.ReadBytes(reader.ReadInt32());
        packages.Add(
          new CatalogEntry {
            Name = name,
            Revision = revision,
            ModifiedTicks = modified,
            LatestTicks = latest_ticks,
            CorgiHash = corgi,
            FullHash = full,
            CorgiSize = corgi_size,
            FullSize = full_size,
            BaseUrl = base_url,
            Latest = latest,
            Tags = tags,
            Versions = versions,
            Search = new SearchFields(search)
          }
        );
      }

      int download_count = reader.ReadInt32();
      List<(string, long, (int, long)[])> downloads = new(download_count);
      for (int i = 0; i < download_count; i++) {
        string name = reader.ReadString();
        long total = reader.ReadInt64();
        (int, long)[] days = new (int, long)[reader.ReadInt32()];
        for (int d = 0; d < days.Length; d++) {
          days[d] = (reader.ReadInt32(), reader.ReadInt64());
        }

        downloads.Add((name, total, days));
      }

      return new CatalogSnapshot {
        Created = new DateTimeOffset(created, TimeSpan.Zero),
        WatermarkTicks = watermark,
        RecentChanges = recent,
        Packages = packages,
        Downloads = downloads
      };
    } catch (EndOfStreamException) {
      throw new InvalidDataException("The catalog snapshot is truncated.");
    }
  }

  private static void WriteHash(BinaryWriter writer, UInt128 hash) {
    writer.Write((ulong)(hash >> 64));
    writer.Write((ulong)hash);
  }

  private static UInt128 ReadHash(BinaryReader reader) {
    ulong upper = reader.ReadUInt64();
    ulong lower = reader.ReadUInt64();
    return new UInt128(upper, lower);
  }
}
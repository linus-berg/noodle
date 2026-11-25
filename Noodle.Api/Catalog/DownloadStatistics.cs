using System.Collections.Concurrent;
using System.Globalization;

namespace Noodle.Api.Catalog;

/// <summary>
///   Tarball downloads served by this registry, per package and day (the
///   last month is kept), for search ranking and npm's weekly and monthly
///   counts. Shared between instances through the change log and kept in
///   snapshots. They are statistics: a crash loses at most a minute.
/// </summary>
public sealed class DownloadStatistics {
  public const int C_DAYS = 32;

  private readonly ConcurrentDictionary<string, PackageDownloads> counts_ =
    new(StringComparer.Ordinal);

  private long changes_;
  private ConcurrentDictionary<(string Name, int Day), long> pending_ = new();

  public static int Today =>
    (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay);

  /// <summary>Changes whenever counts change.</summary>
  public long Changes => Interlocked.Read(ref changes_);

  /// <summary>Counts one download served by this instance.</summary>
  public void Record(string name) {
    int day = Today;
    Add(name, day, 1);
    pending_.AddOrUpdate((name, day), 1, (_, n) => n + 1);
  }

  public (long Monthly, long Weekly) Get(string name) {
    if (!counts_.TryGetValue(name, out PackageDownloads? downloads)) {
      return (0, 0);
    }

    (long monthly, long weekly, _) = downloads.Get(Today);
    return (monthly, weekly);
  }

  public long GetMonthly(string name) {
    return counts_.TryGetValue(name, out PackageDownloads? downloads)
             ? downloads.Get(Today).Monthly
             : 0;
  }

  /// <summary>
  ///   Takes the downloads recorded since the last call, as
  ///   <c>name -&gt; day -&gt; count</c>.
  /// </summary>
  public Dictionary<string, Dictionary<string, long>> TakePending() {
    ConcurrentDictionary<(string, int), long> pending = Interlocked.Exchange(
      ref pending_,
      new ConcurrentDictionary<(string Name, int Day), long>()
    );
    Dictionary<string, Dictionary<string, long>> result =
      new(StringComparer.Ordinal);
    foreach (KeyValuePair<(string Name, int Day), long> pair in pending) {
      if (!result.TryGetValue(
            pair.Key.Name,
            out Dictionary<string, long>? days
          )) {
        days = new Dictionary<string, long>(StringComparer.Ordinal);
        result[pair.Key.Name] = days;
      }

      days[pair.Key.Day.ToString(CultureInfo.InvariantCulture)] = pair.Value;
    }

    return result;
  }

  /// <summary>Puts counts back after a failed flush.</summary>
  public void RestorePending(
    Dictionary<string, Dictionary<string, long>> pending) {
    foreach ((string name, int day, long count) in Flatten(pending)) {
      pending_.AddOrUpdate((name, day), count, (_, n) => n + count);
    }
  }

  /// <summary>Adds counts recorded by another instance.</summary>
  public void Apply(Dictionary<string, Dictionary<string, long>> delta) {
    foreach ((string name, int day, long count) in Flatten(delta)) {
      Add(name, day, count);
    }
  }

  public void Load(
    IEnumerable<(string Name, long Total, (int Day, long Count)[] Days)>
      counts) {
    Interlocked.Increment(ref changes_);
    counts_.Clear();
    foreach ((string name, long total, (int Day, long Count)[] days) in
             counts) {
      PackageDownloads downloads = counts_.GetOrAdd(
        name,
        _ => new PackageDownloads()
      );
      downloads.Load(total, days);
    }
  }

  public IEnumerable<(string Name, long Total, (int Day, long Count)[] Days)>
    Export() {
    foreach (KeyValuePair<string, PackageDownloads> pair in counts_) {
      (long total, (int, long)[] days) = pair.Value.Export();
      yield return (pair.Key, total, days);
    }
  }

  private void Add(string name, int day, long count) {
    Interlocked.Increment(ref changes_);
    counts_.GetOrAdd(name, _ => new PackageDownloads()).Add(day, count);
  }

  private static IEnumerable<(string, int, long)> Flatten(
    Dictionary<string, Dictionary<string, long>> counts) {
    foreach (KeyValuePair<string, Dictionary<string, long>> package in counts) {
      foreach (KeyValuePair<string, long> day in package.Value) {
        if (int.TryParse(
              day.Key,
              NumberStyles.None,
              CultureInfo.InvariantCulture,
              out int number
            )) {
          yield return (package.Key, number, day.Value);
        }
      }
    }
  }

  /// <summary>A ring of daily counts plus the all-time total.</summary>
  private sealed class PackageDownloads {
    private readonly long[] counts_ = new long[C_DAYS];
    private readonly int[] days_ = new int[C_DAYS];
    private readonly Lock lock_ = new();
    private long total_;

    public void Add(int day, long count) {
      lock (lock_) {
        total_ += count;
        int slot = day % C_DAYS;
        if (days_[slot] != day) {
          if (days_[slot] > day) {
            // Older than anything the ring keeps.
            return;
          }

          days_[slot] = day;
          counts_[slot] = 0;
        }

        counts_[slot] += count;
      }
    }

    public (long Monthly, long Weekly, long Total) Get(int today) {
      lock (lock_) {
        long monthly = 0;
        long weekly = 0;
        for (int i = 0; i < C_DAYS; i++) {
          int age = today - days_[i];
          if (age is >= 0 and < 30) {
            monthly += counts_[i];
            if (age < 7) {
              weekly += counts_[i];
            }
          }
        }

        return (monthly, weekly, total_);
      }
    }

    public void Load(long total, (int Day, long Count)[] days) {
      lock (lock_) {
        total_ = total;
        foreach ((int day, long count) in days) {
          int slot = day % C_DAYS;
          if (days_[slot] < day) {
            days_[slot] = day;
            counts_[slot] = count;
          }
        }
      }
    }

    public (long Total, (int Day, long Count)[] Days) Export() {
      lock (lock_) {
        List<(int, long)> days = [];
        for (int i = 0; i < C_DAYS; i++) {
          if (counts_[i] > 0) {
            days.Add((days_[i], counts_[i]));
          }
        }

        return (total_, days.ToArray());
      }
    }
  }
}
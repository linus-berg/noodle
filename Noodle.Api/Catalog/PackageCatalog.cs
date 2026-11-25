using System.Collections.Concurrent;
using System.Text;
using Noodle.Api.Packages;

namespace Noodle.Api.Catalog;

/// <summary>Tells search which packages have known vulnerabilities.</summary>
public interface IVulnerabilityLookup {
  bool IsVulnerable(string name, string version);
}

public sealed class SearchRequest {
  public string? Text { get; init; }
  public int From { get; init; }
  public int Size { get; init; } = 20;
  public double Quality { get; init; } = 0.65;
  public double Popularity { get; init; } = 0.98;
  public double Maintenance { get; init; } = 0.5;
}

public sealed class SearchHit {
  public required CatalogEntry Entry { get; init; }
  public required double Score { get; init; }
  public required double Popularity { get; init; }
  public required double Quality { get; init; }
  public required double Maintenance { get; init; }
  public required long Monthly { get; init; }
  public required long Weekly { get; init; }
  public required bool Insecure { get; init; }
}

public sealed class SearchResults {
  public required int Total { get; init; }
  public required IReadOnlyList<SearchHit> Hits { get; init; }
}

/// <summary>
///   All packages and versions in memory. Answers search, dist-tags and
///   "does this exist" (404s) without S3, and knows which packument objects
///   are current.
/// </summary>
public sealed class PackageCatalog(
  DownloadStatistics downloads,
  IVulnerabilityLookup vulnerabilities
) {
  private static readonly IComparer<Match> S_WORST_FIRST_ =
    Comparer<Match>.Create((a, b) => Compare(b, a));

  private readonly ConcurrentDictionary<string, CatalogEntry> packages_ =
    new(StringComparer.Ordinal);

  private readonly TaskCompletionSource ready_ =
    new(TaskCreationOptions.RunContinuationsAsynchronously);

  private ArraySnapshot? array_;
  private long generation_;
  private (long Generation, long Count)? version_count_;

  /// <summary>
  ///   Whether the catalog has been loaded. Before that, "not in the catalog"
  ///   does not mean "does not exist".
  /// </summary>
  public bool IsReady => ready_.Task.IsCompleted;

  public Task WhenReady => ready_.Task;

  public int Count => packages_.Count;

  /// <summary>Changes with every update (cache keys, snapshots).</summary>
  public long Generation => Interlocked.Read(ref generation_);

  public void MarkReady() {
    ready_.TrySetResult();
  }

  public void Load(IEnumerable<CatalogEntry> entries) {
    packages_.Clear();
    foreach (CatalogEntry entry in entries) {
      packages_[entry.Name] = entry;
    }

    Changed();
  }

  /// <summary>
  ///   Updates one package from its document (<c>null</c> = deleted).
  ///   Documents older than the one already applied are ignored, so racing
  ///   local and remote updates cannot go back in time.
  /// </summary>
  public void Apply(string name, PackageDocument? document) {
    CatalogEntry? entry =
      document == null ? null : CatalogEntry.FromDocument(document);
    if (entry == null) {
      packages_.TryRemove(name, out _);
    } else {
      packages_.AddOrUpdate(
        name,
        entry,
        (_, existing) => existing.Revision > entry.Revision ? existing : entry
      );
    }

    Changed();
  }

  /// <summary>Removes a package that no longer has a document.</summary>
  public void Remove(string name) {
    if (packages_.TryRemove(name, out _)) {
      Changed();
    }
  }

  public CatalogEntry? Find(string name) {
    return packages_.GetValueOrDefault(name);
  }

  /// <summary>
  ///   <c>false</c> only when the catalog is loaded and does not know the
  ///   package, so a request can be answered with 404 without asking S3.
  /// </summary>
  public bool MayExist(string name) {
    return !IsReady || packages_.ContainsKey(name);
  }

  public bool HasVersion(string name, string version) {
    return packages_.TryGetValue(name, out CatalogEntry? entry) &&
           entry.HasVersion(version);
  }

  public IReadOnlyList<CatalogEntry> GetAll() {
    return GetArray();
  }

  /// <summary>Versions of all packages (counted again after changes).</summary>
  public long CountVersions() {
    long generation = Interlocked.Read(ref generation_);
    (long Generation, long Count)? cached = version_count_;
    if (cached != null && cached.Value.Generation == generation) {
      return cached.Value.Count;
    }

    long total = 0;
    foreach (CatalogEntry entry in GetArray()) {
      total += entry.VersionCount;
    }

    version_count_ = (generation, total);
    return total;
  }

  public SearchResults Search(SearchRequest request) {
    SearchQuery query = SearchQuery.Parse(request.Text);
    int from = Math.Max(0, request.From);
    int size = Math.Clamp(request.Size, 0, 250);
    int wanted = from + size;
    CatalogEntry[] entries = GetArray();
    List<Match> matches = [];
    Lock gate = new();
    Weights weights = new(
      Math.Max(0, request.Quality),
      Math.Max(0, request.Popularity),
      Math.Max(0, request.Maintenance)
    );
    TermBytes[] terms = query.Terms.Select(t => new TermBytes(t)).ToArray();
    // A linear scan: no index to maintain, substring matches for free, and
    // fast enough for a million packages when nothing is allocated per
    // package that does not match.
    Parallel.For(
      0,
      entries.Length,
      new ParallelOptions {
        MaxDegreeOfParallelism = entries.Length > 20_000
                                   ? Environment.ProcessorCount
                                   : 1
      },
      () => new List<Match>(),
      (index, _, local) => {
        CatalogEntry entry = entries[index];
        double relevance = Relevance(entry, query, terms);
        if (relevance <= 0) {
          return local;
        }

        bool insecure = vulnerabilities.IsVulnerable(entry.Name, entry.Latest);
        if (query.Insecure != null && insecure != query.Insecure) {
          return local;
        }

        local.Add(Score(entry, relevance, weights, insecure));
        return local;
      },
      local => {
        lock (gate) {
          matches.AddRange(local);
        }
      }
    );
    List<Match> top = Top(matches, wanted);
    List<SearchHit> page = [];
    for (int i = from; i < Math.Min(wanted, top.Count); i++) {
      Match match = top[i];
      (long monthly, long weekly) = downloads.Get(match.Entry.Name);
      page.Add(
        new SearchHit {
          Entry = match.Entry,
          Score = match.Score,
          Popularity = match.Popularity,
          Quality = match.Quality,
          Maintenance = 1,
          Monthly = monthly,
          Weekly = weekly,
          Insecure = match.Insecure
        }
      );
    }

    return new SearchResults {
      Total = matches.Count,
      Hits = page
    };
  }

  private Match Score(CatalogEntry entry, double relevance, Weights weights,
                      bool insecure) {
    long monthly = downloads.GetMonthly(entry.Name);
    double popularity = 0.1 + 0.9 * Math.Min(1, Math.Log10(1 + monthly) / 6);
    double quality = 0.4;
    if (entry.Search.Get(SearchFields.C_DESCRIPTION).Length > 0) {
      quality += 0.2;
    }

    if (entry.Search.Get(SearchFields.C_REPOSITORY).Length > 0) {
      quality += 0.2;
    }

    if (entry.Search.Get(SearchFields.C_LICENSE).Length > 0) {
      quality += 0.1;
    }

    if (entry.Search.Get(SearchFields.C_KEYWORDS).Length > 0) {
      quality += 0.1;
    }

    double total_weight = weights.Quality +
                          weights.Popularity +
                          weights.Maintenance;
    double blend = total_weight <= 0
                     ? 1
                     : (weights.Quality * quality +
                        weights.Popularity * popularity +
                        weights.Maintenance) /
                       total_weight;
    return new Match(
      entry,
      Math.Round(relevance * blend, 5),
      popularity,
      quality,
      monthly,
      insecure
    );
  }

  /// <summary>
  ///   Text relevance; 0 when a filter or any word does not match.
  /// </summary>
  private static double Relevance(CatalogEntry entry, SearchQuery query,
                                  TermBytes[] terms) {
    string name = entry.Name;
    SearchFields fields = entry.Search;
    foreach (string scope in query.Scopes) {
      if (!name.StartsWith('@') ||
          !name.AsSpan(1)
               .StartsWith(scope, StringComparison.OrdinalIgnoreCase) ||
          name.Length <= scope.Length + 1 ||
          name[scope.Length + 1] != '/') {
        return 0;
      }
    }

    if (query.Unstable != null &&
        entry.Latest.StartsWith("0.", StringComparison.Ordinal) !=
        query.Unstable) {
      return 0;
    }

    ReadOnlySpan<byte> keywords = fields.Get(SearchFields.C_KEYWORDS);
    if (query.KeywordGroups.Count > 0) {
      bool any = false;
      foreach (List<string> group in query.KeywordGroups) {
        bool all = true;
        foreach (string keyword in group) {
          if (!HasKeyword(keywords, keyword)) {
            all = false;
            break;
          }
        }

        if (all) {
          any = true;
          break;
        }
      }

      if (!any) {
        return 0;
      }
    }

    foreach (string excluded in query.ExcludedKeywords) {
      if (HasKeyword(keywords, excluded)) {
        return 0;
      }
    }

    foreach (string author in query.Authors) {
      if (!Contains(fields.Get(SearchFields.C_AUTHOR), author)) {
        return 0;
      }
    }

    foreach (string maintainer in query.Maintainers) {
      if (!Contains(fields.Get(SearchFields.C_MAINTAINERS), maintainer) &&
          !Contains(fields.Get(SearchFields.C_AUTHOR), maintainer)) {
        return 0;
      }
    }

    if (terms.Length == 0) {
      return 1;
    }

    double total = 0;
    foreach (TermBytes term in terms) {
      double score = ScoreName(name, term.Text);
      score += ScoreKeywords(keywords, term);
      if (ContainsBytes(fields.Get(SearchFields.C_DESCRIPTION), term.Utf8)) {
        score += 2;
      }

      if (score == 0 &&
          (ContainsBytes(fields.Get(SearchFields.C_AUTHOR), term.Utf8) ||
           ContainsBytes(fields.Get(SearchFields.C_MAINTAINERS), term.Utf8))) {
        score = 1;
      }

      if (score == 0) {
        return 0;
      }

      total += score;
    }

    if (query.BoostExact &&
        name.Equals(query.Text, StringComparison.OrdinalIgnoreCase)) {
      total += 1000;
    }

    return total;
  }

  private static double ScoreName(string name, string term) {
    if (name.Equals(term, StringComparison.OrdinalIgnoreCase)) {
      return 100;
    }

    string bare = name.StartsWith('@') && name.IndexOf('/') is var slash and > 0
                    ? name[(slash + 1)..]
                    : name;
    if (bare.Equals(term, StringComparison.OrdinalIgnoreCase)) {
      return 60;
    }

    if (bare.StartsWith(term, StringComparison.OrdinalIgnoreCase)) {
      return 40;
    }

    int index = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
    if (index < 0) {
      return 0;
    }

    // At the start of a word of the name ("react-dom" for "dom").
    while (index >= 0) {
      if (index == 0 || name[index - 1] is '-' or '.' or '_' or '/' or '@') {
        return 30;
      }

      index = name.IndexOf(term, index + 1, StringComparison.OrdinalIgnoreCase);
    }

    return 15;
  }

  private static double ScoreKeywords(ReadOnlySpan<byte> keywords,
                                      TermBytes term) {
    double score = 0;
    while (keywords.Length > 0) {
      int end = keywords.IndexOf((byte)'\n');
      ReadOnlySpan<byte> keyword = end < 0 ? keywords : keywords[..end];
      if (Ascii.EqualsIgnoreCase(keyword, term.Utf8)) {
        return 10;
      }

      if (score == 0 && ContainsBytes(keyword, term.Utf8)) {
        score = 4;
      }

      if (end < 0) {
        break;
      }

      keywords = keywords[(end + 1)..];
    }

    return score;
  }

  private static bool HasKeyword(ReadOnlySpan<byte> keywords, string keyword) {
    byte[] needle = Encoding.UTF8.GetBytes(keyword);
    while (keywords.Length > 0) {
      int end = keywords.IndexOf((byte)'\n');
      ReadOnlySpan<byte> item = end < 0 ? keywords : keywords[..end];
      if (Ascii.EqualsIgnoreCase(item, needle)) {
        return true;
      }

      if (end < 0) {
        break;
      }

      keywords = keywords[(end + 1)..];
    }

    return false;
  }

  private static bool Contains(ReadOnlySpan<byte> text, string value) {
    return ContainsBytes(text, Encoding.UTF8.GetBytes(value));
  }

  /// <summary>
  ///   Case-insensitive (ASCII) substring search in UTF-8 text. The needle
  ///   is lower case.
  /// </summary>
  public static bool ContainsBytes(ReadOnlySpan<byte> text,
                                   ReadOnlySpan<byte> needle) {
    if (needle.Length == 0) {
      return true;
    }

    byte first = needle[0];
    byte upper = first is >= (byte)'a' and <= (byte)'z'
                   ? (byte)(first - 32)
                   : first;
    ReadOnlySpan<byte> rest = needle[1..];
    int offset = 0;
    while (offset <= text.Length - needle.Length) {
      int index = text[offset..(text.Length - needle.Length + 1)]
        .IndexOfAny(first, upper);
      if (index < 0) {
        return false;
      }

      int start = offset + index;
      if (Ascii.EqualsIgnoreCase(
            text.Slice(start + 1, rest.Length),
            rest
          )) {
        return true;
      }

      offset = start + 1;
    }

    return false;
  }

  /// <summary>
  ///   The best <paramref name="count" /> matches in order (bounded heap).
  /// </summary>
  private static List<Match> Top(List<Match> matches, int count) {
    if (count >= matches.Count / 4) {
      matches.Sort(Compare);
      return matches;
    }

    PriorityQueue<Match, Match> heap = new(count + 1, S_WORST_FIRST_);
    foreach (Match match in matches) {
      if (heap.Count < count) {
        heap.Enqueue(match, match);
      } else if (count > 0 && Compare(match, heap.Peek()) < 0) {
        heap.EnqueueDequeue(match, match);
      }
    }

    List<Match> top = new(heap.Count);
    while (heap.TryDequeue(out Match match, out _)) {
      top.Add(match);
    }

    top.Reverse();
    return top;
  }

  private static int Compare(Match a, Match b) {
    int result = b.Score.CompareTo(a.Score);
    if (result == 0) {
      result = b.Monthly.CompareTo(a.Monthly);
    }

    return result != 0
             ? result
             : string.CompareOrdinal(a.Entry.Name, b.Entry.Name);
  }

  /// <summary>All packages as an array, rebuilt only after changes.</summary>
  private CatalogEntry[] GetArray() {
    long generation = Interlocked.Read(ref generation_);
    ArraySnapshot? current = Volatile.Read(ref array_);
    if (current != null && current.Generation == generation) {
      return current.Entries;
    }

    CatalogEntry[] entries = packages_.Values.ToArray();
    Volatile.Write(
      ref array_,
      new ArraySnapshot {
        Generation = generation,
        Entries = entries
      }
    );
    return entries;
  }

  private void Changed() {
    Interlocked.Increment(ref generation_);
  }

  private sealed class ArraySnapshot {
    public required long Generation { get; init; }
    public required CatalogEntry[] Entries { get; init; }
  }

  private readonly record struct Weights(
    double Quality,
    double Popularity,
    double Maintenance
  );

  private readonly record struct Match(
    CatalogEntry Entry,
    double Score,
    double Popularity,
    double Quality,
    long Monthly,
    bool Insecure
  );

  private sealed class TermBytes(string text) {
    public string Text { get; } = text;
    public byte[] Utf8 { get; } = Encoding.UTF8.GetBytes(text);
  }
}
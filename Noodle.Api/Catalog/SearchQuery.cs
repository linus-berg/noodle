namespace Noodle.Api.Catalog;

/// <summary>
///   A search in npm's syntax: free words plus qualifiers
///   (<c>keywords:</c>, <c>author:</c>, <c>maintainer:</c>, <c>scope:</c>,
///   <c>is:</c>/<c>not:</c> <c>unstable</c>/<c>insecure</c>,
///   <c>boost-exact:</c>).
/// </summary>
public sealed class SearchQuery {
  public List<string> Terms { get; } = [];

  /// <summary>
  ///   keywords:a,b+c,-d: any group must match (OR), all in a group (AND).
  /// </summary>
  public List<List<string>> KeywordGroups { get; } = [];

  public List<string> ExcludedKeywords { get; } = [];
  public List<string> Authors { get; } = [];
  public List<string> Maintainers { get; } = [];
  public List<string> Scopes { get; } = [];
  public bool? Unstable { get; private set; }
  public bool? Insecure { get; private set; }
  public bool BoostExact { get; private set; } = true;

  /// <summary>The free text (lower case), for exact name matches.</summary>
  public string Text { get; private set; } = "";

  public bool HasFilters =>
    KeywordGroups.Count > 0 ||
    ExcludedKeywords.Count > 0 ||
    Authors.Count > 0 ||
    Maintainers.Count > 0 ||
    Scopes.Count > 0 ||
    Unstable != null ||
    Insecure != null;


  public static SearchQuery Parse(string? text) {
    SearchQuery query = new();
    List<string> words = [];
    foreach (string token in (text ?? "").Split(
               (char[]) [
                 ' ',
                 '\t',
                 '\n'
               ],
               StringSplitOptions.RemoveEmptyEntries
             )) {
      int colon = token.IndexOf(':');
      string qualifier = colon > 0 ? token[..colon].ToLowerInvariant() : "";
      string value = colon > 0 ? token[(colon + 1)..] : token;
      switch (qualifier) {
        case "keywords":
        case "keyword":
          query.AddKeywords(value);
          continue;
        case "author":
          AddValue(query.Authors, value);
          continue;
        case "maintainer":
          AddValue(query.Maintainers, value);
          continue;
        case "scope":
          AddValue(query.Scopes, value.TrimStart('@'));
          continue;
        case "is":
        case "not":
          bool flag = qualifier == "is";
          switch (value.ToLowerInvariant()) {
            case "unstable":
              query.Unstable = flag;
              continue;
            case "insecure":
              query.Insecure = flag;
              continue;
          }

          continue;
        case "boost-exact":
          query.BoostExact = !value.Equals(
                               "false",
                               StringComparison.OrdinalIgnoreCase
                             );
          continue;
      }

      string word = token.Trim('"').ToLowerInvariant();
      if (word.Length > 0) {
        query.Terms.Add(word);
        words.Add(word);
      }
    }

    query.Text = string.Join(' ', words);
    return query;
  }

  private void AddKeywords(string value) {
    foreach (string group in value.Split(
               ',',
               StringSplitOptions.RemoveEmptyEntries
             )) {
      if (group.StartsWith('-')) {
        AddValue(ExcludedKeywords, group[1..]);
        continue;
      }

      List<string> all = group.Split('+', StringSplitOptions.RemoveEmptyEntries)
                              .Select(k => k.ToLowerInvariant())
                              .ToList();
      if (all.Count > 0) {
        KeywordGroups.Add(all);
      }
    }
  }

  private static void AddValue(List<string> list, string value) {
    if (value.Length > 0) {
      list.Add(value.ToLowerInvariant());
    }
  }
}
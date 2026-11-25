namespace Noodle.Api.Advisories;

/// <summary>
///   CVSS v3.0/v3.1 base scores from vector strings (OSV only carries the
///   vector; npm's advisories carry the score too).
/// </summary>
public static class CvssV3 {
  /// <summary>
  ///   The base score, or <c>null</c> when the vector is not CVSS v3.
  /// </summary>
  public static double? BaseScore(string? vector) {
    if (vector == null ||
        !(vector.StartsWith("CVSS:3.1/", StringComparison.Ordinal) ||
          vector.StartsWith("CVSS:3.0/", StringComparison.Ordinal))) {
      return null;
    }

    Dictionary<string, string> metrics = new(StringComparer.Ordinal);
    foreach (string part in vector.Split('/').Skip(1)) {
      int colon = part.IndexOf(':');
      if (colon > 0) {
        metrics[part[..colon]] = part[(colon + 1)..];
      }
    }

    if (!metrics.TryGetValue("AV", out string? av) ||
        !metrics.TryGetValue("AC", out string? ac) ||
        !metrics.TryGetValue("PR", out string? pr) ||
        !metrics.TryGetValue("UI", out string? ui) ||
        !metrics.TryGetValue("S", out string? scope) ||
        !metrics.TryGetValue("C", out string? c) ||
        !metrics.TryGetValue("I", out string? i) ||
        !metrics.TryGetValue("A", out string? a)) {
      return null;
    }

    bool changed = scope == "C";
    double? attack_vector = av switch {
      "N" => 0.85,
      "A" => 0.62,
      "L" => 0.55,
      "P" => 0.2,
      _ => null
    };
    double? complexity = ac switch {
      "L" => 0.77,
      "H" => 0.44,
      _ => null
    };
    double? privileges = pr switch {
      "N" => 0.85,
      "L" => changed ? 0.68 : 0.62,
      "H" => changed ? 0.5 : 0.27,
      _ => null
    };
    double? interaction = ui switch {
      "N" => 0.85,
      "R" => 0.62,
      _ => null
    };
    double? confidentiality = Impact(c);
    double? integrity = Impact(i);
    double? availability = Impact(a);
    if (attack_vector == null ||
        complexity == null ||
        privileges == null ||
        interaction == null ||
        confidentiality == null ||
        integrity == null ||
        availability == null) {
      return null;
    }

    double iss = 1 -
                 (1 - confidentiality.Value) *
                 (1 - integrity.Value) *
                 (1 - availability.Value);
    double impact = changed
                      ? 7.52 * (iss - 0.029) - 3.25 * Math.Pow(iss - 0.02, 15)
                      : 6.42 * iss;
    double exploitability = 8.22 *
                            attack_vector.Value *
                            complexity.Value *
                            privileges.Value *
                            interaction.Value;
    if (impact <= 0) {
      return 0;
    }

    double score = changed
                     ? Math.Min(1.08 * (impact + exploitability), 10)
                     : Math.Min(impact + exploitability, 10);
    return RoundUp(score);
  }

  /// <summary>npm's severity names for a score.</summary>
  public static string Severity(double score) {
    return score switch {
      >= 9 => "critical",
      >= 7 => "high",
      >= 4 => "moderate",
      > 0 => "low",
      _ => "info"
    };
  }

  private static double? Impact(string value) {
    return value switch {
      "H" => 0.56,
      "L" => 0.22,
      "N" => 0,
      _ => null
    };
  }

  /// <summary>
  ///   CVSS v3.1's Roundup: the smallest one-decimal number not below the
  ///   input.
  /// </summary>
  private static double RoundUp(double value) {
    long integer = (long)Math.Round(value * 100000);
    if (integer % 10000 == 0) {
      return integer / 100000.0;
    }

    return (Math.Floor(integer / 10000.0) + 1) / 10.0;
  }
}
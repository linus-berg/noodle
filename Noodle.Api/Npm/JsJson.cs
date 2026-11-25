using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Noodle.Api.Npm;

/// <summary>
///   JSON with JavaScript's semantics, so manifests come out the way npm
///   (<c>JSON.parse</c> / <c>JSON.stringify</c>) would write them: the last
///   of duplicate keys wins (in the first one's place), numbers are doubles,
///   and only what must be escaped is.
/// </summary>
public static class JsJson {
  public const int C_MAX_DEPTH = 64;

  private static readonly JsonWriterOptions S_WRITER_OPTIONS_ = new() {
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    SkipValidation = false
  };

  /// <exception cref="JsonException">
  ///   Not valid JSON (or nested too deeply).
  /// </exception>
  public static JsonNode? Parse(ReadOnlySpan<byte> utf8) {
    if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) {
      // npm strips a byte order mark before parsing.
      utf8 = utf8[3..];
    }

    Utf8JsonReader reader = new(
      utf8,
      new JsonReaderOptions {
        MaxDepth = C_MAX_DEPTH,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
      }
    );
    using JsonDocument document = JsonDocument.ParseValue(ref reader);
    return ToNode(document.RootElement);
  }

  public static byte[] Serialize(JsonNode? node) {
    using MemoryStream buffer = new();
    using (Utf8JsonWriter writer = new(buffer, S_WRITER_OPTIONS_)) {
      Write(writer, node);
    }

    return buffer.ToArray();
  }

  public static void Write(Utf8JsonWriter writer, JsonNode? node) {
    switch (node) {
      case null:
        writer.WriteNullValue();
        break;
      case JsonObject obj:
        writer.WriteStartObject();
        foreach (KeyValuePair<string, JsonNode?> pair in obj) {
          writer.WritePropertyName(pair.Key);
          Write(writer, pair.Value);
        }

        writer.WriteEndObject();
        break;
      case JsonArray array:
        writer.WriteStartArray();
        foreach (JsonNode? item in array) {
          Write(writer, item);
        }

        writer.WriteEndArray();
        break;
      default:
        JsonValue value = node.AsValue();
        switch (value.GetValueKind()) {
          case JsonValueKind.String:
            writer.WriteStringValue(value.GetValue<string>());
            break;
          case JsonValueKind.Number:
            writer.WriteRawValue(FormatNumber(ToDouble(value)), true);
            break;
          case JsonValueKind.True:
            writer.WriteBooleanValue(true);
            break;
          case JsonValueKind.False:
            writer.WriteBooleanValue(false);
            break;
          default:
            writer.WriteNullValue();
            break;
        }

        break;
    }
  }

  /// <summary>
  ///   JavaScript truthiness of a JSON value (<c>undefined</c> = missing =
  ///   false).
  /// </summary>
  public static bool IsTruthy(JsonNode? node) {
    if (node == null) {
      return false;
    }

    if (node is JsonObject or JsonArray) {
      return true;
    }

    JsonValue value = node.AsValue();
    return value.GetValueKind() switch {
      JsonValueKind.String => value.GetValue<string>().Length > 0,
      JsonValueKind.Number => ToDouble(value) is not 0 and not double.NaN,
      JsonValueKind.True => true,
      _ => false
    };
  }

  /// <summary>
  ///   The string value, or <c>null</c> when the node is not a string.
  /// </summary>
  public static string? AsString(JsonNode? node) {
    return node is JsonValue value &&
           value.GetValueKind() == JsonValueKind.String
             ? value.GetValue<string>()
             : null;
  }

  public static bool IsString(JsonNode? node) {
    return AsString(node) != null;
  }

  public static bool IsFalse(JsonNode? node) {
    return node is JsonValue value &&
           value.GetValueKind() == JsonValueKind.False;
  }

  public static bool IsTrue(JsonNode? node) {
    return node is JsonValue value &&
           value.GetValueKind() == JsonValueKind.True;
  }

  /// <summary>
  ///   <c>typeof x === 'object'</c> for a present, non-null value.
  /// </summary>
  public static bool IsObjectLike(JsonNode? node) {
    return node is JsonObject or JsonArray;
  }

  /// <summary>
  ///   JavaScript's <c>String(value)</c> for primitives (what a template
  ///   string makes of a manifest value).
  /// </summary>
  public static string ToJsString(JsonNode? node) {
    return node switch {
      null => "undefined",
      JsonObject => "[object Object]",
      JsonArray array => string.Join(",", array.Select(ToJsString)),
      _ => node.AsValue().GetValueKind() switch {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => FormatNumber(ToDouble(node.AsValue())),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => "null"
      }
    };
  }

  /// <summary>A deep copy (nodes can only have one parent).</summary>
  public static JsonNode? Clone(JsonNode? node) {
    return node?.DeepClone();
  }

  /// <summary>JavaScript's <c>encodeURIComponent</c>.</summary>
  public static string EncodeUriComponent(string value) {
    StringBuilder result = new(value.Length);
    foreach (byte b in Encoding.UTF8.GetBytes(value)) {
      char c = (char)b;
      if (c is >= 'a' and <= 'z'
          or >= 'A' and <= 'Z'
          or >= '0' and <= '9'
          or '-'
          or '_'
          or '.'
          or '!'
          or '~'
          or '*'
          or '\''
          or '('
          or ')') {
        result.Append(c);
      } else {
        result.Append('%')
              .Append(b.ToString("X2", CultureInfo.InvariantCulture));
      }
    }

    return result.ToString();
  }

  /// <summary>
  ///   Number::toString, the way <c>JSON.stringify</c> prints numbers.
  /// </summary>
  public static string FormatNumber(double value) {
    if (double.IsNaN(value) || double.IsInfinity(value)) {
      return "null";
    }

    if (value == 0) {
      return "0";
    }

    string sign = value < 0 ? "-" : "";
    // Shortest round-trip digits and the decimal exponent.
    string r = Math.Abs(value).ToString("E16", CultureInfo.InvariantCulture);
    string shortest =
      Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
    (string digits, int exponent) = Decompose(shortest);
    if (digits.Length == 0) {
      (digits, exponent) = Decompose(r);
    }

    // n: position of the decimal point relative to the digits.
    int n = exponent + 1;
    int k = digits.Length;
    string body;
    if (k <= n && n <= 21) {
      body = digits + new string('0', n - k);
    } else if (0 < n && n <= 21) {
      body = digits[..n] + "." + digits[n..];
    } else if (-6 < n && n <= 0) {
      body = "0." + new string('0', -n) + digits;
    } else {
      int e = n - 1;
      string mantissa = k == 1 ? digits : digits[..1] + "." + digits[1..];
      body = mantissa +
             "e" +
             (e >= 0 ? "+" : "-") +
             Math.Abs(e).ToString(CultureInfo.InvariantCulture);
    }

    return sign + body;
  }

  private static (string Digits, int Exponent) Decompose(string text) {
    int e = text.IndexOfAny(['E', 'e']);
    string mantissa = e < 0 ? text : text[..e];
    int exponent = e < 0
                     ? 0
                     : int.Parse(text[(e + 1)..], CultureInfo.InvariantCulture);
    int point = mantissa.IndexOf('.');
    string integer_part = point < 0 ? mantissa : mantissa[..point];
    string fraction = point < 0 ? "" : mantissa[(point + 1)..];
    string all = integer_part + fraction;
    // Exponent of the first digit.
    int first = all.Length - all.TrimStart('0').Length;
    all = all.Trim('0');
    if (all.Length == 0) {
      return ("", 0);
    }

    exponent += integer_part.Length - 1 - first;
    return (all, exponent);
  }

  private static double ToDouble(JsonValue value) {
    if (value.TryGetValue(out JsonElement element)) {
      return double.TryParse(
               element.GetRawText(),
               NumberStyles.Float,
               CultureInfo.InvariantCulture,
               out double parsed
             )
               ? parsed
               : double.NaN;
    }

    return value.GetValue<double>();
  }

  private static JsonNode? ToNode(JsonElement element) {
    switch (element.ValueKind) {
      case JsonValueKind.Object: {
        JsonObject obj = new();
        foreach (JsonProperty property in element.EnumerateObject()) {
          // Like JSON.parse: a repeated key keeps its first position and
          // takes the last value.
          obj[property.Name] = ToNode(property.Value);
        }

        return obj;
      }
      case JsonValueKind.Array: {
        JsonArray array = new();
        foreach (JsonElement item in element.EnumerateArray()) {
          array.Add(ToNode(item));
        }

        return array;
      }
      case JsonValueKind.String:
        return JsonValue.Create(element.GetString()!);
      case JsonValueKind.Number:
        return JsonValue.Create(
          double.Parse(element.GetRawText(), CultureInfo.InvariantCulture)
        );
      case JsonValueKind.True:
        return JsonValue.Create(true);
      case JsonValueKind.False:
        return JsonValue.Create(false);
      default:
        return null;
    }
  }
}
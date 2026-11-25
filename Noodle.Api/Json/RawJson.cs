using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noodle.Api.Json;

/// <summary>
///   A JSON value kept as its UTF-8 text (version manifests): stored and
///   written back without being parsed into objects.
/// </summary>
[JsonConverter(typeof(RawJsonConverter))]
public sealed class RawJson(byte[] utf8) {
  public byte[] Utf8 { get; } = utf8;

  public JsonDocument Parse() {
    return JsonDocument.Parse(
      Utf8,
      new JsonDocumentOptions {
        MaxDepth = 256
      }
    );
  }
}

public sealed class RawJsonConverter : JsonConverter<RawJson> {
  public override RawJson Read(ref Utf8JsonReader reader, Type type_to_convert,
                               JsonSerializerOptions options) {
    JsonElement element = JsonElement.ParseValue(ref reader);
    return new RawJson(JsonMarshal.GetRawUtf8Value(element).ToArray());
  }

  public override void Write(Utf8JsonWriter writer, RawJson value,
                             JsonSerializerOptions options) {
    writer.WriteRawValue(value.Utf8, true);
  }
}
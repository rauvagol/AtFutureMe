using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtFutureMe.Services;

/// <summary>
/// String enum converter that falls back to the enum default instead of failing
/// the entire document when a renamed/removed member appears in saved JSON.
/// </summary>
// auto model
sealed class TolerantJsonStringEnumConverter : JsonConverterFactory
{
    readonly JsonNamingPolicy? _naming;

    public TolerantJsonStringEnumConverter(JsonNamingPolicy? naming = null) =>
        _naming = naming;

    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var convType = typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(convType, _naming)!;
    }

    sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        readonly JsonNamingPolicy? _naming;

        public TolerantEnumConverter(JsonNamingPolicy? naming) => _naming = naming;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number
                && reader.TryGetInt32(out var num)
                && Enum.IsDefined(typeof(T), num))
                return (T)Enum.ToObject(typeof(T), num);

            if (reader.TokenType == JsonTokenType.String)
            {
                var raw = reader.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    if (Enum.TryParse(raw, ignoreCase: true, out T parsed) && Enum.IsDefined(parsed))
                        return parsed;

                    // camelCase / naming-policy forms
                    foreach (T value in Enum.GetValues<T>())
                    {
                        var name = value.ToString();
                        if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
                            return value;
                        if (_naming != null
                            && string.Equals(_naming.ConvertName(name), raw, StringComparison.OrdinalIgnoreCase))
                            return value;
                    }
                }
            }

            // Unknown / renamed member — keep loading the rest of the file.
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            var name = value.ToString();
            writer.WriteStringValue(_naming != null ? _naming.ConvertName(name) : name);
        }
    }
}

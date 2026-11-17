using System.Text.Json;
using System.Text.Json.Serialization;

namespace NashmiKeys.Core.Configuration;

/// <summary>
/// Reads mode values case-insensitively and falls back to Hold instead of
/// rejecting the whole config when a value is unknown.
/// </summary>
public sealed class TolerantHotkeyModeConverter : JsonConverter<HotkeyMode>
{
    public override HotkeyMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (Enum.TryParse<HotkeyMode>(text, ignoreCase: true, out var mode) &&
                Enum.IsDefined(typeof(HotkeyMode), mode))
            {
                return mode;
            }

            return HotkeyMode.Hold;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number))
        {
            return Enum.IsDefined(typeof(HotkeyMode), number) ? (HotkeyMode)number : HotkeyMode.Hold;
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return HotkeyMode.Hold;
        }

        reader.Skip();
        return HotkeyMode.Hold;
    }

    public override void Write(Utf8JsonWriter writer, HotkeyMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

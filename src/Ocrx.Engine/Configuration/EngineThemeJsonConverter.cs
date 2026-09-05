using System.Text.Json;
using System.Text.Json.Serialization;
using Ocrx.Engine.Tray;

namespace Ocrx.Engine;

/// <summary>
/// Reads <c>"theme"</c> as <c>"system"</c>/<c>"light"</c>/<c>"dark"</c>, case-insensitively,
/// defaulting to <see cref="EngineTheme.System"/> for anything else — a value from a newer engine, or
/// a typo from hand-editing, must never fail config load. Writes the lowercase form back out.
/// </summary>
public sealed class EngineThemeJsonConverter : JsonConverter<EngineTheme>
{
    public override EngineTheme Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return EngineTheme.System;
        }

        return reader.GetString()?.ToLowerInvariant() switch
        {
            "light" => EngineTheme.Light,
            "dark" => EngineTheme.Dark,
            _ => EngineTheme.System,
        };
    }

    public override void Write(Utf8JsonWriter writer, EngineTheme value, JsonSerializerOptions options)
        => writer.WriteStringValue(value switch
        {
            EngineTheme.Light => "light",
            EngineTheme.Dark => "dark",
            _ => "system",
        });
}

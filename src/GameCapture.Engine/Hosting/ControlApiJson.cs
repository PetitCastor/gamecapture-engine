using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameCapture.Engine;

/// <summary>
/// The one <see cref="JsonSerializerOptions"/> instance every control-API response, request body and
/// WebSocket payload serializes through — camelCase properties and camelCase string enums, so
/// <see cref="Tray.PluginRowState"/>, <see cref="Plugins.ReleaseChannel"/> and
/// <see cref="Tray.EngineTheme"/> all read the same way a hand-written JSON API would write them.
/// Shared rather than re-declared per file so <see cref="ControlApi"/> and
/// <see cref="ControlApiEventHub"/> can never drift into serializing the same types differently.
/// </summary>
internal static class ControlApiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

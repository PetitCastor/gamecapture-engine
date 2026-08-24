namespace GameCapture.Sdk;

/// <summary>
/// One entry in <see cref="PluginConfig.Outputs"/> — a tagged shape the JSON binds to. Which
/// properties matter depends on <see cref="Type"/>; <see cref="SinkFactory"/> is what interprets
/// them.
/// </summary>
public sealed class SinkSpec
{
    /// <summary><c>"json"</c>, <c>"csv"</c>, <c>"http"</c>, or <c>"overlay"</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Wrap the built sink in <see cref="ChangeDedupeSink"/>. Ignored for <c>"overlay"</c>,
    /// which always wants every observation and clear.</summary>
    public bool DedupeOnChange { get; set; } = true;

    /// <summary>Let <see cref="RecordKind.Cleared"/> records reach the file/HTTP sink, instead of the
    /// default of dropping them.</summary>
    public bool RecordClears { get; set; }

    /// <summary>File path for <c>"json"</c>/<c>"csv"</c>. Resolved against the config file's own
    /// directory by <see cref="PluginConfig.Load{T}"/> when relative.</summary>
    public string? Path { get; set; }

    /// <summary>CSV field-column order for <c>"csv"</c>.</summary>
    public IReadOnlyList<string>? Columns { get; set; }

    /// <summary>Endpoint for <c>"http"</c>.</summary>
    public string? Url { get; set; }

    /// <summary>Request timeout, in seconds, for <c>"http"</c>.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Settings for <c>"overlay"</c>, defined by the companion overlay package.</summary>
    public OverlaySpec? Overlay { get; set; }
}

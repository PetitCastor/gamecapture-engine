namespace GameCapture.Sdk;

/// <summary>Builds one <see cref="IRecordSink"/> from a <see cref="SinkSpec"/>, applying
/// <see cref="ChangeDedupeSink"/> per spec.</summary>
internal static class SinkFactory
{
    /// <exception cref="ArgumentException">The spec's type is unknown, or a type-required field
    /// (<c>Path</c>/<c>Url</c>) is missing — a malformed <c>outputs</c> entry, meant to abort
    /// startup with a clear message rather than fail on the first emit.</exception>
    public static IRecordSink Build(SinkSpec spec, Func<bool> isReplay, IPluginOutput log,
        IOverlaySinkFactory? overlayFactory)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (spec.Type == "overlay")
            return overlayFactory?.Create(spec.Overlay ?? new(), log) ?? NullRecordSink.Instance;

        IRecordSink sink = spec.Type switch
        {
            "json" => new JsonRecordSink(RequirePath(spec), isReplay, spec.RecordClears),
            "csv" => new CsvRecordSink(RequirePath(spec), spec.Columns ?? [], isReplay, spec.RecordClears),
            "http" => new HttpRecordSink(ParseUrl(spec), isReplay, spec.RecordClears,
                timeout: TimeSpan.FromSeconds(spec.TimeoutSeconds)),
            _ => throw new ArgumentException($"unknown output type '{spec.Type}'"),
        };
        return spec.DedupeOnChange ? new ChangeDedupeSink(sink) : sink;
    }

    private static string RequirePath(SinkSpec spec) => !string.IsNullOrWhiteSpace(spec.Path)
        ? spec.Path
        : throw new ArgumentException($"output type '{spec.Type}' requires 'path'");

    private static Uri ParseUrl(SinkSpec spec)
    {
        var url = !string.IsNullOrWhiteSpace(spec.Url)
            ? spec.Url
            : throw new ArgumentException($"output type '{spec.Type}' requires 'url'");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException(
                $"output type '{spec.Type}' has an invalid 'url': '{url}' (expected http/https)");
        }

        return uri;
    }
}


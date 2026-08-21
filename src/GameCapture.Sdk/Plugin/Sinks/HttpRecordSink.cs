using System.Net.Http.Json;

namespace GameCapture.Sdk;

/// <summary>POSTs each record as JSON to a configured endpoint over one reused
/// <see cref="HttpClient"/>. No-ops under replay mode; never throws out of
/// <see cref="EmitAsync"/> — a bad status or a network failure is logged and swallowed.</summary>
public sealed class HttpRecordSink : IRecordSink
{
    private readonly bool _replayMode;
    private readonly bool _recordClears;
    private readonly Uri _endpoint;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public HttpRecordSink(Uri endpoint, bool replayMode, bool recordClears = false,
        HttpClient? client = null, TimeSpan? timeout = null)
    {
        _replayMode = replayMode;
        _recordClears = recordClears;
        _endpoint = endpoint;
        _ownsClient = client is null;
        _client = client ?? new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(5) };
        if (_replayMode)
            Console.Error.WriteLine($"HttpRecordSink: replay mode, '{endpoint}' will not be posted to.");
    }

    public async ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        if (_replayMode) return;
        if (record.Kind == RecordKind.Cleared && !_recordClears) return;

        var obj = new Dictionary<string, object?>
        {
            ["timestamp"] = record.Timestamp,
            ["plugin"] = record.Plugin,
            ["trigger"] = record.Trigger.ToString(),
            ["kind"] = record.Kind.ToString(),
            ["rawText"] = record.RawText,
        };
        if (record.Fields is not null)
            foreach (var (k, v) in record.Fields) obj[k] = v;

        try
        {
            using var response = await _client.PostAsJsonAsync(_endpoint, obj, ct);
            if (!response.IsSuccessStatusCode)
                Console.Error.WriteLine($"HttpRecordSink: POST to '{_endpoint}' returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"HttpRecordSink: POST to '{_endpoint}' failed: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClient) _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

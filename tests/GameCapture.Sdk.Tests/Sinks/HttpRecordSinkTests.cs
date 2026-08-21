using System.Net;
using System.Text.Json;
using Xunit;

namespace GameCapture.Sdk.Tests.Sinks;

public class HttpRecordSinkTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond(request);
        }
    }

    [Fact]
    public async Task EmitAsync_PostsTheRecordAsJson()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        await using var sink = new HttpRecordSink(new Uri("http://example.test/records"), replayMode: false, client: client);

        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("one", doc.RootElement.GetProperty("rawText").GetString());
    }

    [Fact]
    public async Task EmitAsync_OnNonSuccessStatus_DoesNotThrow()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new HttpClient(handler);
        await using var sink = new HttpRecordSink(new Uri("http://example.test/records"), replayMode: false, client: client);

        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);
    }

    [Fact]
    public async Task EmitAsync_OnTransportException_DoesNotThrow()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("boom"));
        var client = new HttpClient(handler);
        await using var sink = new HttpRecordSink(new Uri("http://example.test/records"), replayMode: false, client: client);

        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);
    }

    [Fact]
    public async Task EmitAsync_UnderReplayMode_NeverPosts()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        var client = new HttpClient(handler);
        await using var sink = new HttpRecordSink(new Uri("http://example.test/records"), replayMode: true, client: client);

        await sink.EmitAsync(new CaptureRecord(DateTime.Now, "refinery", TriggerKind.Auto, "one"), CancellationToken.None);

        Assert.Null(handler.LastRequest);
    }
}

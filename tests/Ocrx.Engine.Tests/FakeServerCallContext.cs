using Grpc.Core;

namespace Ocrx.Engine.Tests;

/// <summary>
/// The minimum <see cref="ServerCallContext"/> a test calling <see cref="CaptureGrpcService.Track"/>
/// directly needs to supply. Track only ever reads <see cref="CancellationToken"/> off its context —
/// everything else throws, so a test that starts relying on some other member fails loudly instead
/// of silently exercising a context that does not behave like the real one.
/// </summary>
internal sealed class FakeServerCallContext : ServerCallContext
{
    private readonly CancellationToken _cancellationToken;

    public FakeServerCallContext(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;

    protected override string MethodCore => throw new NotSupportedException();
    protected override string HostCore => throw new NotSupportedException();
    protected override string PeerCore => throw new NotSupportedException();
    protected override DateTime DeadlineCore => throw new NotSupportedException();
    protected override Metadata RequestHeadersCore => throw new NotSupportedException();
    protected override CancellationToken CancellationTokenCore => _cancellationToken;
    protected override Metadata ResponseTrailersCore => throw new NotSupportedException();
    protected override Status StatusCore { get; set; }
    protected override WriteOptions? WriteOptionsCore { get; set; }
    protected override AuthContext AuthContextCore => throw new NotSupportedException();
    protected override IDictionary<object, object> UserStateCore => throw new NotSupportedException();

    protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
        => throw new NotSupportedException();

    protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
        => throw new NotSupportedException();
}

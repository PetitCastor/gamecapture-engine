using GameCapture.Contracts.Proto;
using Grpc.Core;

namespace GameCapture.Engine.Tests;

/// <summary>
/// A request stream that yields one Hello outside the engine's supported protocol range, then ends
/// — with no delay anywhere in the sequence. Driving <see cref="CaptureGrpcService.Track"/> with
/// this, instead of over a real pipe, strips out the wire's own scheduling noise: the request
/// pump's fault and the drain loop's first iteration now race each other as tightly as the .NET
/// thread pool will schedule them, rather than after a socket round-trip, which is what let the
/// original TOCTOU window go unseen roughly two runs in three.
/// </summary>
internal sealed class UnsupportedHelloRequestStream : IAsyncStreamReader<TrackRequest>
{
    private readonly uint _protocolVersion;
    private bool _sent;

    public UnsupportedHelloRequestStream(uint protocolVersion) => _protocolVersion = protocolVersion;

    public TrackRequest Current { get; private set; } = new();

    public Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        if (_sent)
            return Task.FromResult(false);

        _sent = true;
        Current = new TrackRequest
        {
            Hello = new Hello { ClientName = "race-regression", ProtocolVersion = _protocolVersion },
        };
        return Task.FromResult(true);
    }
}

using GameCapture.Contracts.Proto;
using Grpc.Core;

namespace GameCapture.Engine.Tests;

/// <summary>Records every response a service method under direct test writes, in order — a stand-in
/// for the real gRPC-generated response stream when the call is driven without a transport.</summary>
internal sealed class RecordingResponseStreamWriter : IServerStreamWriter<TrackResponse>
{
    public List<TrackResponse> Written { get; } = [];

    public WriteOptions? WriteOptions { get; set; }

    public Task WriteAsync(TrackResponse message)
    {
        Written.Add(message);
        return Task.CompletedTask;
    }
}

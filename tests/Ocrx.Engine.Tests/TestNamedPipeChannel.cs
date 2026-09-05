using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;

namespace Ocrx.Engine.Tests;

/// <summary>Creates a raw gRPC channel for engine wire-level integration tests.</summary>
/// <remarks>
/// The public SDK intentionally hides its transport and generated proto client. These tests exercise
/// the engine's raw protocol directly, so their test-only channel adapter stays here rather than
/// widening the plugin-facing SDK surface.
/// </remarks>
internal static class TestNamedPipeChannel
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static GrpcChannel Create(string pipeName)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = ConnectTimeout,
            ConnectCallback = async (_, cancellationToken) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
                    PipeOptions.WriteThrough | PipeOptions.Asynchronous,
                    TokenImpersonationLevel.Anonymous);
                try
                {
                    await pipe.ConnectAsync(cancellationToken);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }
            },
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            ThrowOperationCanceledOnCancellation = true,
        });
    }
}

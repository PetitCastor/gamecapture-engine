using Ocrx.Contracts;
using Ocrx.Engine.Grpc;
using Grpc.Core;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// Regression coverage for the drain loop's pump-observation race described on
/// <see cref="CaptureGrpcService.Track"/>: a request pump that faults on an unsupported-version
/// Hello must always surface as the version refusal, never as a hang, regardless of exactly when
/// the pump's fault lands relative to the loop's single read of its state. Calls the service method
/// directly with fakes instead of a real transport — <see cref="ProtocolHandshakeTests"/> already
/// covers the wire, and its own equivalent case (<c>Hello_v999_faults_with_supported_protocol_range</c>)
/// is exactly the test that used to pass roughly two runs in three: the wire's own scheduling noise
/// means it cannot reliably land in the narrow window the race lived in. Removing the transport
/// removes that noise, so this either faults the same way on every run or hangs on every run —
/// nothing timing-dependent is left to get lucky on.
/// </summary>
public class CaptureGrpcServiceTrackTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Outside anything the engine speaks, and outside anything it plausibly will.</summary>
    private const uint UnsupportedVersion = 999;

    [Fact]
    public async Task Track_WhenThePumpFaultsOnAnUnsupportedHello_SurfacesTheRefusalNotATimeout()
    {
        var status = new EngineStatus("en-US", replayMode: false);
        var registry = new SubscriptionRegistry(status);
        var config = new EngineConfig();

        // The scan loop and OCR pipeline are constructor dependencies Track itself never touches;
        // built but never run, exactly as GrpcHostTests.GetStatus_OverNamedPipe_ReturnsEngineStatus
        // does for the same reason.
        using var sink = new ConsoleSink();
        using var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var ocr = new OcrPipeline();
        using var scanLoop = new ScanLoop(source, ocr, registry, status, sink, config, verbose: false);

        var service = new CaptureGrpcService(status, registry, scanLoop, ocr, config);

        var requestStream = new UnsupportedHelloRequestStream(UnsupportedVersion);
        var responseWriter = new RecordingResponseStreamWriter();
        using var cts = new CancellationTokenSource(TestTimeout);
        var ctx = new FakeServerCallContext(cts.Token);

        // Assert.ThrowsAsync awaits Track to completion: under the race this regresses, Track
        // would instead park on the channel read until `cts` fired, and this would throw
        // OperationCanceledException — the observable difference between "fixed" and "broken".
        var ex = await Assert.ThrowsAsync<RpcException>(
            () => service.Track(requestStream, responseWriter, ctx));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Equal(ProtocolVersion.Min.ToString(), ex.Trailers.GetValue("ocrx-protocol-min"));
        Assert.Equal(ProtocolVersion.Current.ToString(), ex.Trailers.GetValue("ocrx-protocol-max"));

        // The refusal reached the caller directly — no HelloAck, still less a tick, was ever
        // written ahead of it.
        Assert.Empty(responseWriter.Written);
    }
}

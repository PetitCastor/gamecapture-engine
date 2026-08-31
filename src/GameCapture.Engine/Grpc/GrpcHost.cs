using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GameCapture.Engine.Grpc;

/// <summary>
/// Builds the engine's gRPC host. Program and the integration tests share this one factory so
/// a test can never pass because it configured Kestrel differently from the real engine.
/// </summary>
internal static class GrpcHost
{
    /// <summary>
    /// A named pipe rather than a TCP port: the engine is a per-user process talking to
    /// plugins on the same machine, and a pipe inherits the session's ACL instead of exposing
    /// a listening socket. HTTP/2 is forced because gRPC requires it and pipes carry no TLS
    /// to negotiate it with.
    /// </summary>
    /// <remarks>
    /// Every engine component is registered as a singleton and the service itself is registered
    /// as an instance: gRPC's activator resolves a registered service from DI and only falls back
    /// to reflective construction (which cannot see the internal constructor) when it finds none.
    /// </remarks>
    /// <param name="sourceSelection">Monitor list/index the control API's <c>/api/monitors</c> serves.
    /// Only needed when <paramref name="controlApiToken"/> is non-null.</param>
    /// <param name="controlApiToken">Non-null enables the loopback control API (TASK-UI-03): a second
    /// Kestrel listener on <see cref="IPAddress.Loopback"/>, port 0, token-gating every <c>/api/*</c>
    /// route and the WebSocket. Null (a headless replay/video run) leaves the engine exactly as it was
    /// before this task — the named pipe is the only listener, and no loopback socket is ever opened.</param>
    /// <param name="controlApiState">Late-bound handoff for the tray's callback bundle and the latest
    /// metrics sample; required together with <paramref name="controlApiToken"/>.</param>
    public static WebApplication BuildGrpcHost(
        string pipeName,
        EngineStatus status,
        SubscriptionRegistry registry,
        ScanLoop scanLoop,
        OcrPipeline ocr,
        EngineConfig config,
        ConsoleSink sink,
        FrameSourceSelection? sourceSelection = null,
        ControlApiToken? controlApiToken = null,
        ControlApiState? controlApiState = null)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.ClearProviders(); // the ConsoleSink owns the console, incl. the status bar
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenNamedPipe(pipeName, o => o.Protocols = HttpProtocols.Http2);

            // Loopback only, OS-assigned port: this socket is real (unlike the pipe's session ACL),
            // so it exists only when the control API is enabled and every request into it is
            // token-gated by ControlApi.Map below.
            if (controlApiToken is not null)
                k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http1AndHttp2);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton(status);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(scanLoop);
        builder.Services.AddSingleton(ocr);
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(new CaptureGrpcService(status, registry, scanLoop, ocr, config));

        var app = builder.Build();
        app.MapGrpcService<CaptureGrpcService>();

        if (controlApiToken is not null)
            ControlApi.Map(app, controlApiToken, controlApiState!, status, sourceSelection, config, sink);

        return app;
    }
}

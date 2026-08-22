using GameCapture.Contracts;

namespace GameCapture.Sdk.Tests;

/// <summary>A pre-<see cref="IPluginServices.EmitCleared"/> service implementation.</summary>
internal sealed class LegacyPluginServices : IPluginServices
{
    public EngineInfo Engine { get; } = new("legacy", 1, 1, 1, ReplayMode: false, OcrLanguage: "en",
        ConnectedClients: [], ScanInterval: TimeSpan.FromMilliseconds(250));

    public List<CaptureRecord> Emitted { get; } = [];

    public void Emit(CaptureRecord record) => Emitted.Add(record);

    public Task<string?> DumpFrameAsync(RoiRect? roi, string prefix, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task<OcrRegionResult?> ReadRoiAsync(RoiSubscription roi, CancellationToken ct)
        => Task.FromResult<OcrRegionResult?>(null);

    public void Log(string message) { }

    public void LogVerbose(string message) { }
}

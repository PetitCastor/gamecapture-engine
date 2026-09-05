using Ocrx.Contracts.Proto;
using Ocrx.Engine.Metrics;
using Ocrx.Engine.Tray;
using Xunit;

namespace Ocrx.Engine.Tests.Tray;

/// <summary>
/// Pins how <see cref="TrayViewBuilder"/> composes an engine status snapshot plus a metrics sample
/// into the render-ready <see cref="TrayView"/> — the icon-state derivation, the mode/frame/ocr
/// strings, and the three metrics-line states (present, sampling, disabled).
/// </summary>
public class TrayViewBuilderTests
{
    private static StatusResponse Status(
        bool replay = false,
        uint width = 2560,
        uint height = 1440,
        ulong seq = 42,
        string ocr = "en-US",
        string version = "1.2.3",
        string[]? clients = null)
    {
        var response = new StatusResponse
        {
            ReplayMode = replay,
            FrameWidth = width,
            FrameHeight = height,
            FrameSeq = seq,
            OcrLanguage = ocr,
            EngineVersion = version,
        };
        response.ConnectedClients.AddRange(clients ?? []);
        return response;
    }

    private static MetricsSnapshot Sample()
        => new(DateTime.UnixEpoch, 12.5, 400L * 1024 * 1024, 500L * 1024 * 1024, 80L * 1024 * 1024, 30, 256L * 1024 * 1024);

    [Fact]
    public void LiveWithAConnectedPlugin_IsCapturing()
    {
        var view = TrayViewBuilder.Build(Status(clients: ["MissionPlugin"]), Sample(), fps: 2.0, metricsEnabled: true);

        Assert.Equal(TrayIconState.Capturing, view.IconState);
        Assert.Equal("Live", view.Mode);
        Assert.Equal("2560x1440", view.Frame);
        Assert.Equal("en-US", view.OcrLanguage);
        Assert.Equal("2.0/s", view.Fps);
        Assert.Equal(new[] { "MissionPlugin" }, view.Plugins);
        Assert.Equal(MetricsFormatter.Format(Sample()), view.Metrics);
    }

    [Fact]
    public void LiveWithNoPlugins_IsIdle()
    {
        var view = TrayViewBuilder.Build(Status(), Sample(), fps: null, metricsEnabled: true);

        Assert.Equal(TrayIconState.Idle, view.IconState);
        Assert.Equal("—", view.Fps);
        Assert.Empty(view.Plugins);
    }

    [Fact]
    public void ReplayMode_IsReplayRegardlessOfPlugins()
    {
        var view = TrayViewBuilder.Build(Status(replay: true, clients: ["RefineryPlugin"]), Sample(), fps: 60, metricsEnabled: true);

        Assert.Equal(TrayIconState.Replay, view.IconState);
        Assert.Equal("Replay", view.Mode);
    }

    [Fact]
    public void NoFrameScannedYet_ShowsPlaceholderInsteadOfZeroByZero()
    {
        var view = TrayViewBuilder.Build(Status(width: 0, height: 0), Sample(), fps: null, metricsEnabled: true);

        Assert.Equal("— (no frame yet)", view.Frame);
    }

    [Fact]
    public void NoMetricsYet_DistinguishesSamplingFromDisabled()
    {
        var sampling = TrayViewBuilder.Build(Status(), metrics: null, fps: null, metricsEnabled: true);
        var disabled = TrayViewBuilder.Build(Status(), metrics: null, fps: null, metricsEnabled: false);

        Assert.Equal("sampling…", sampling.Metrics);
        Assert.Equal("metrics disabled", disabled.Metrics);
    }

    [Fact]
    public void EmptyVersionAndLanguage_FallBackToPlaceholders()
    {
        var view = TrayViewBuilder.Build(Status(ocr: "", version: ""), Sample(), fps: null, metricsEnabled: true);

        Assert.Equal("0.0.0", view.EngineVersion);
        Assert.Equal("—", view.OcrLanguage);
    }

    [Fact]
    public void Tooltip_StaysWithinTheNotifyIconLimit()
    {
        var view = TrayViewBuilder.Build(Status(clients: ["MissionPlugin"]), Sample(), fps: 2.0, metricsEnabled: true);

        Assert.True(view.Tooltip.Length <= TrayViewBuilder.TooltipMaxLength);
        Assert.Contains("Live", view.Tooltip);
    }

    [Fact]
    public void Tooltip_EndsWithTheEngineVersion()
    {
        var view = TrayViewBuilder.Build(Status(version: "1.2.3"), Sample(), fps: null, metricsEnabled: true);

        Assert.EndsWith("v1.2.3", view.Tooltip);
    }

    [Fact]
    public void Tooltip_TruncatesTheVersionBeforeTheLiveStatusFields()
    {
        var longVersion = "1.2.3-preview.45+a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6";
        var view = TrayViewBuilder.Build(Status(version: longVersion, clients: ["MissionPlugin"]), Sample(), fps: 2.0, metricsEnabled: true);

        Assert.True(view.Tooltip.Length <= TrayViewBuilder.TooltipMaxLength);
        Assert.Contains("Live", view.Tooltip);
        Assert.Contains("2560x1440", view.Tooltip);
        Assert.Contains("1 plugin(s)", view.Tooltip);
    }
}

namespace GameCapture.Sdk.Testing;

/// <summary>The knobs on <see cref="ReplayHarness.RunAsync"/>.</summary>
public sealed class ReplayOptions
{
    /// <summary>Path to a built <c>GameCapture.Engine.exe</c>. Use <see cref="EngineLocator.Resolve"/>.</summary>
    public required string EnginePath { get; init; }

    /// <summary>
    /// A directory of PNGs the engine replays, in the format <c>ReplayFrameSource</c> reads. Set
    /// exactly one of this or <see cref="VideoPath"/> — <see cref="ReplayHarness.RunAsync"/> throws
    /// if both or neither is set. No longer <c>required</c> now that a video is the alternative
    /// source; the "did you forget a source?" check has moved from the compiler to a runtime guard.
    /// </summary>
    public string? CorpusDir { get; init; }

    /// <summary>
    /// An MP4 the engine replays via <c>--video</c> (TASK-25), decoded frame by frame through its
    /// scan loop. Set exactly one of this or <see cref="CorpusDir"/>. Deterministic drain only:
    /// the harness never asks for <c>--video-realtime</c>/<c>--video-loop</c>, which exist for
    /// interactive dev against a live <c>Program.cs</c>, not an automated run that drains to EOF
    /// and asserts.
    /// </summary>
    public string? VideoPath { get; init; }

    /// <summary>
    /// Frames-per-second the engine steps a <see cref="VideoPath"/> at (<c>--video-fps</c>).
    /// <c>null</c> leaves the engine on its own default (<c>1000 / ScanIntervalMs</c>). Ignored
    /// when replaying a <see cref="CorpusDir"/> — that source has no pacing knob.
    /// </summary>
    public double? VideoFps { get; init; }

    /// <summary>The plugin under test, driven through its real <see cref="GameCapturePluginHost"/> path.</summary>
    public required IGameCapturePlugin Plugin { get; init; }

    /// <summary>
    /// A hang bound, not a performance budget. Fired means the engine never came up, the source
    /// never exhausted, or the plugin never returned — all of which are bugs, not slow runs.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

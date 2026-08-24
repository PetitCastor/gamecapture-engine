namespace GameCapture.Engine;

/// <summary>
/// Pacing knobs for <see cref="VideoFrameSource"/>. <see cref="FrameInterval"/> always applies;
/// <see cref="Realtime"/> and <see cref="Loop"/> only matter for the game-stand-in mode (TASK-25
/// mode B) — deterministic stepping (mode A) ignores both.
/// </summary>
internal sealed record VideoFrameSourceOptions
{
    /// <summary>Fixed spacing between sampled frames along the video's own timeline.</summary>
    public required TimeSpan FrameInterval { get; init; }

    /// <summary>
    /// When true, <see cref="VideoFrameSource.ReadFrameAsync"/> waits for each frame's presentation
    /// time against a monotonic clock instead of returning frames as fast as they decode.
    /// </summary>
    public bool Realtime { get; init; }

    /// <summary>When true, end of stream restarts the video at its first frame instead of ending the run.</summary>
    public bool Loop { get; init; }

    /// <summary>Monotonic clock used to anchor realtime pacing without depending on wall-clock jumps.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

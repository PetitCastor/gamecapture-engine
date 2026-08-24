namespace GameCapture.Engine;

internal static class FrameSourceModeExtensions
{
    public static bool UsesReplayFlow(this FrameSourceMode mode)
        => mode switch
        {
            FrameSourceMode.LiveCapture => false,
            FrameSourceMode.ReplayCorpus or
            FrameSourceMode.DeterministicVideo or
            FrameSourceMode.RealtimeVideo => true,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown frame source mode."),
        };

    public static bool IsInteractive(this FrameSourceMode mode)
        => mode switch
        {
            FrameSourceMode.LiveCapture or FrameSourceMode.RealtimeVideo => true,
            FrameSourceMode.ReplayCorpus or FrameSourceMode.DeterministicVideo => false,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown frame source mode."),
        };
}

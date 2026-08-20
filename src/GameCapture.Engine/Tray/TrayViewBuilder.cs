using System.Globalization;
using GameCapture.Contracts.Proto;
using GameCapture.Engine.Metrics;

namespace GameCapture.Engine.Tray;

/// <summary>
/// Pure composition of an engine <see cref="StatusResponse"/> plus the latest process-health
/// <see cref="MetricsSnapshot"/> into a render-ready <see cref="TrayView"/>. No UI, no clock, no I/O
/// — the whole tray display is decided here and pinned by tests; the WinForms layer only paints it.
/// </summary>
public static class TrayViewBuilder
{
    /// <summary>
    /// NotifyIcon tooltip length ceiling. WinForms throws once <c>NotifyIcon.Text</c> exceeds 63
    /// characters, so the builder truncates deterministically rather than risk the exception.
    /// </summary>
    internal const int TooltipMaxLength = 63;

    /// <param name="status">The engine's own <see cref="StatusResponse"/> snapshot.</param>
    /// <param name="metrics">Latest sample, or <c>null</c> before the first one arrives.</param>
    /// <param name="fps">Scanned rate from <see cref="FrameRateTracker"/>, or <c>null</c> until established.</param>
    /// <param name="metricsEnabled">
    /// Whether the metrics sampler is running at all. Distinguishes "sampling, none yet" from
    /// "metrics turned off in config" so the popup does not imply a stuck sampler.
    /// </param>
    public static TrayView Build(StatusResponse status, MetricsSnapshot? metrics, double? fps, bool metricsEnabled)
    {
        var pluginCount = status.ConnectedClients.Count;
        var iconState = status.ReplayMode
            ? TrayIconState.Replay
            : pluginCount > 0 ? TrayIconState.Capturing : TrayIconState.Idle;

        var mode = status.ReplayMode ? "Replay" : "Live";
        var frame = status is { FrameWidth: > 0, FrameHeight: > 0 }
            ? Invariant($"{status.FrameWidth}x{status.FrameHeight}")
            : "— (no frame yet)";

        var metricsLine = metrics is not null
            ? MetricsFormatter.Format(metrics)
            : metricsEnabled ? "sampling…" : "metrics disabled";

        var fpsLine = fps is { } rate ? Invariant($"{rate:0.0}/s") : "—";

        var tooltip = Truncate(
            Invariant($"GameCapture {mode} · {frame} · {pluginCount} plugin(s)"),
            TooltipMaxLength);

        return new TrayView(
            iconState,
            tooltip,
            mode,
            string.IsNullOrEmpty(status.EngineVersion) ? "0.0.0" : status.EngineVersion,
            frame,
            string.IsNullOrEmpty(status.OcrLanguage) ? "—" : status.OcrLanguage,
            fpsLine,
            metricsLine,
            status.ConnectedClients.ToList());
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

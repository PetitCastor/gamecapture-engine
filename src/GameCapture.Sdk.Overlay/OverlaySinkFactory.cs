using GameCapture.Sdk;

namespace GameCapture.Sdk.Overlay;

/// <summary>Creates the opt-in desktop overlay sink used by <see cref="PluginHostOptions"/>.</summary>
public sealed class OverlaySinkFactory : IOverlaySinkFactory
{
    /// <summary>Create an overlay sink, or a no-op sink when the current platform cannot host one.</summary>
    public static IRecordSink Create(OverlaySpec spec, IPluginOutput log)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(log);

        if (!OperatingSystem.IsWindows())
            return NoOpRecordSink.Instance;

        Win32OverlayWindow? window = null;
        try
        {
            window = new Win32OverlayWindow(spec, log);
            return new OverlayRecordSink(spec, window);
        }
        catch (Exception ex)
        {
            window?.Dispose();
            log.WriteLine($"overlay disabled: {ex.Message}");
            return NoOpRecordSink.Instance;
        }
    }

    IRecordSink IOverlaySinkFactory.Create(OverlaySpec spec, IPluginOutput log) => Create(spec, log);
}

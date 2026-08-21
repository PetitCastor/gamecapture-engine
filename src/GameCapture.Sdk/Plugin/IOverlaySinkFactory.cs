namespace GameCapture.Sdk;

/// <summary>
/// Builds the overlay sink from an <see cref="OverlaySpec"/>. The core SDK takes no dependency on
/// the (Windows-only) overlay package — a plugin that references it registers the implementation via
/// <see cref="PluginHostOptions.OverlayFactory"/>; an <c>"overlay"</c> output with no factory
/// registered routes to <see cref="NullRecordSink"/> instead.
/// </summary>
public interface IOverlaySinkFactory
{
    IRecordSink Create(OverlaySpec spec, IPluginOutput log);
}

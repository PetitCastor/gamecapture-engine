namespace Ocrx.Engine;

/// <summary>Desired, idempotent auto-start state for one plugin — whether the engine launches it
/// for the user the next time it starts.</summary>
internal sealed record PluginAutoStartPatch(bool? Enabled);

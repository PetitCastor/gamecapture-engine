namespace Ocrx.Engine.Plugins;

/// <summary>JSON shape persisted by <see cref="PluginManagerSettings"/>.</summary>
/// <param name="IncludePreviews">Whether preview plugins are offered.</param>
/// <param name="AutoStartDisabled">Ids the user turned auto-start off for; absent or null in a
/// document written before the setting existed, which reads as "every installed plugin starts".</param>
internal sealed record PluginManagerSettingsDocument(bool IncludePreviews, IReadOnlyList<string>? AutoStartDisabled = null);

using Ocrx.Engine.Tray;

namespace Ocrx.Engine;

/// <summary>
/// Outcome of a settings save routed through <see cref="EngineDesktopLifetime.SaveSettings"/>. The
/// tray and the control API share this: what was actually written may differ from what was submitted
/// — an unavailable OCR pack, an unparseable hotkey or an unusable pipe name all fall back to a safe
/// value instead of persisting garbage — and the caller needs to know both that and whether the
/// engine must restart to apply it.
/// </summary>
/// <param name="Settings">The settings as actually persisted (or left unchanged, if nothing differed
/// from the current values).</param>
/// <param name="RestartPending">Whether the engine needs to restart to apply the change — always
/// <c>false</c> for a patch that touches only <see cref="EngineTheme"/>.</param>
/// <param name="Error">Persistence failure safe to show to the local user, or <c>null</c> on success.</param>
public sealed record SettingsSaveResult(EngineSettings Settings, bool RestartPending, string? Error = null)
{
    public bool Succeeded => Error is null;
}

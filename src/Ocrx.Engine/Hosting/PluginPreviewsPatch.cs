namespace Ocrx.Engine;

/// <summary>Request body for <c>POST /api/plugins/settings</c> — the only plugin-manager preference
/// exposed over the control API (TASK-UI-05 section 4's "Include preview builds" checkbox).</summary>
internal sealed record PluginPreviewsPatch(bool IncludePreviews);

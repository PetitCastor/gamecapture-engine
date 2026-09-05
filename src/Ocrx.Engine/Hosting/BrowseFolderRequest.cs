namespace Ocrx.Engine;

/// <summary>
/// Request body for <c>POST /api/settings/browse</c>. The web page cannot show a native folder
/// picker itself (TASK-UI-05 section 5), so it asks the control API to open one on the UI thread and
/// hands over whatever directory is currently in the field, so the dialog starts there instead of
/// wherever <see cref="System.Windows.Forms.FolderBrowserDialog"/> defaults to.
/// </summary>
internal sealed record BrowseFolderRequest(string? InitialDirectory = null);

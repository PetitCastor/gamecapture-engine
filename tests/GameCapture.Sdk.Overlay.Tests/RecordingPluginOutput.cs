using GameCapture.Sdk;

namespace GameCapture.Sdk.Overlay.Tests;

internal sealed class RecordingPluginOutput : IPluginOutput
{
    public List<string> Lines { get; } = [];

    public void WriteLine(string message = "") => Lines.Add(message);

    public void UpdateStatus(string statusText)
    {
    }
}

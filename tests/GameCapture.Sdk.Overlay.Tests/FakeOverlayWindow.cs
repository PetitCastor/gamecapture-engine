using GameCapture.Sdk.Overlay;

namespace GameCapture.Sdk.Overlay.Tests;

internal sealed class FakeOverlayWindow : IOverlayWindow
{
    public int StartCount { get; private set; }

    public List<(string Text, TimeSpan Linger)> Shows { get; } = [];

    public int HideCount { get; private set; }

    public bool IsDisposed { get; private set; }

    public void Start() => StartCount++;

    public void Show(string text, TimeSpan linger) => Shows.Add((text, linger));

    public void Hide() => HideCount++;

    public void Dispose() => IsDisposed = true;
}

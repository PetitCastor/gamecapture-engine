namespace GameCapture.Sdk.Overlay;

internal interface IOverlayWindow : IDisposable
{
    void Start();

    void Show(string text, TimeSpan linger);

    void Hide();
}

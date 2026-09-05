using System.Drawing;
using Ocrx.Contracts.Proto;
using Ocrx.Engine.Plugins;

namespace Ocrx.Engine.Tests;

internal sealed class RoiOverlayControllerFixture : IDisposable
{
    private readonly GatedFrameSource _source = new(EngineTestFixtures.ReplayDir, isReplay: false);
    private Action? _launcherChanged;

    public RoiOverlayControllerFixture(bool hasFrame = true)
    {
        Status = new EngineStatus("en-US", replayMode: false);
        Registry = new SubscriptionRegistry(Status);
        Renderer = new RecordingRoiOverlayRenderer();
        var selection = new FrameSourceSelection(
            _source,
            "test",
            ["Test monitor"],
            CurrentMonitorIndex: 0,
            CaptureMonitor: new MonitorInfo(IntPtr.Zero, "TEST", 1920, 1080, IsPrimary: true)
            {
                Bounds = new Rectangle(-1920, 0, 1920, 1080),
            });
        Controller = new RoiOverlayController(
            id => IsPluginRunning && id == Entry.Id,
            handler => _launcherChanged += handler,
            handler => _launcherChanged -= handler,
            Registry,
            Status,
            selection,
            Renderer);
        if (hasFrame)
            Status.OnFrame(1920, 1080, 1);
    }

    public CatalogEntry Entry { get; } = new(
        "signature-plugin",
        "SignaturePlugin",
        "test",
        "https://github.com/PetitCastor/ocrx-plugins/releases/latest/download/SignaturePlugin-win-x64.zip",
        ClientName: "SignaturePlugin");
    public EngineStatus Status { get; }
    public SubscriptionRegistry Registry { get; }
    public RecordingRoiOverlayRenderer Renderer { get; }
    public RoiOverlayController Controller { get; }
    public ClientSubscription FirstSubscription { get; private set; } = null!;
    public bool IsPluginRunning { get; set; } = true;

    public void AddSubscription(string id, uint x)
    {
        var subscription = Registry.Register(replayMode: false);
        subscription.Name = "SignaturePlugin";
        subscription.SetRois(new RoiSetUpdate { Rois = { Roi(id, x) } });
        FirstSubscription ??= subscription;
    }

    public void RaiseLauncherChanged() => _launcherChanged?.Invoke();

    public void Dispose()
    {
        Controller.Dispose();
        _source.Dispose();
    }

    private static RoiSpec Roi(string id, uint x)
        => new()
        {
            Id = id,
            Rect = new Rect { X = x, Y = 100, Width = 80, Height = 40 },
        };
}

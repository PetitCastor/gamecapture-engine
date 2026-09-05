using Ocrx.Contracts;
using Ocrx.Contracts.Proto;
using Ocrx.Sdk;

namespace Ocrx.Engine.Tests;

/// <summary>
/// Shared corpus and ROI definitions for the engine tests. The ROIs are the real ones a plugin
/// will subscribe (the refinery panel-state region and a REFINE-toggle colour probe), so the
/// tests exercise the same geometry path — reference space in, frame space out — that production
/// will, rather than a convenient synthetic rectangle.
/// </summary>
internal static class EngineTestFixtures
{
    /// <summary>Three frames copied from the monolith's refinery-confirm replay corpus.</summary>
    public const string ReplayDir = "Fixtures/engine-smoke";

    /// <summary>
    /// Synthetic 320x180, 3.0s/30fps MP4 for VideoFrameSourceTests: every frame is a keyframe (so
    /// <c>GetThumbnailAsync</c> lands on real content at arbitrary timestamps, not stale P-frame
    /// data) and carries a burned-in timestamp so frames sampled at different points are visually
    /// distinct rather than a black rectangle repeated 90 times.
    /// </summary>
    public const string VideoPath = "Fixtures/video-frame-source/sample.mp4";

    /// <summary>The refinery panel-state ROI: SETUP | PROCESSING | COMPLETED.</summary>
    public static RoiSpec PanelStateRoi(string id = "panel") => new()
    {
        Id = id,
        Rect = new Rect { X = 900, Y = 265, Width = 250, Height = 55 },
        Scale = 3.0,
        Mode = RoiMode.Text,
    };

    /// <summary>A small colour probe: PIXELS ROIs are for toggle strips, not screenshots.</summary>
    public static RoiSpec ToggleStripRoi(string id = "toggle") => new()
    {
        Id = id,
        Rect = new Rect { X = 640, Y = 700, Width = 40, Height = 40 },
        Mode = RoiMode.Pixels,
    };

    /// <summary>An ROI a plugin could only produce by mistyping a constant: nowhere near the frame.</summary>
    public static RoiSpec OffFrameRoi(string id = "offscreen") => new()
    {
        Id = id,
        Rect = new Rect { X = 9000, Y = 5000, Width = 200, Height = 60 },
        Scale = 2.0,
        Mode = RoiMode.Text,
    };

    /// <summary>The same ROIs as a plugin expresses through the public SDK surface.</summary>
    public static RoiSubscription PanelStateSubscription(string id = "panel")
        => new(id, new RoiRect(900, 265, 250, 55), 3.0, RoiKind.Text);

    public static RoiSubscription ToggleStripSubscription(string id = "toggle")
        => new(id, new RoiRect(640, 700, 40, 40), 0, RoiKind.Pixels);

    public static RoiSubscription OffFrameSubscription(string id = "offscreen")
        => new(id, new RoiRect(9000, 5000, 200, 60), 2.0, RoiKind.Text);

    /// <summary>Corpus frames in the order the scan loop will hand them out.</summary>
    public static string[] ExpectedFrameNames() => ReplayFrameSource
        .EnumerateCorpus(ReplayDir)
        .Select(Path.GetFileName)
        .ToArray()!;
}

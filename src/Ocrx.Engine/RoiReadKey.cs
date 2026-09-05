using Ocrx.Contracts;
using Ocrx.Contracts.Proto;

namespace Ocrx.Engine;

/// <summary>
/// Identifies the work needed to read an ROI from one frame. The client-local ROI id is
/// deliberately absent: equivalent subscriptions may name the same read differently.
/// </summary>
internal readonly record struct RoiReadKey(
    RoiMode Mode,
    uint X,
    uint Y,
    uint Width,
    uint Height,
    double Scale)
{
    public static RoiReadKey From(RoiSpec spec)
    {
        var rect = spec.Rect;
        var scale = spec.Mode == RoiMode.Pixels
            ? WireLimits.DefaultOcrScale
            : WireLimits.NormalizeOcrScale(spec.Scale);

        return new RoiReadKey(
            spec.Mode,
            rect?.X ?? 0,
            rect?.Y ?? 0,
            rect?.Width ?? 0,
            rect?.Height ?? 0,
            scale);
    }
}

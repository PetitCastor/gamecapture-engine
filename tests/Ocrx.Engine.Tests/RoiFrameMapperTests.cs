using Ocrx.Contracts;
using Xunit;

namespace Ocrx.Engine.Tests;

public sealed class RoiFrameMapperTests
{
    [Fact]
    public void MapAccepted_UsesTheSameEdgeScalingAsTheCaptureCrop()
    {
        var bounds = RoiFrameMapper.MapAccepted(new RoiRect(1264, 454, 70, 44), 1920, 1080);

        Assert.Equal((948u, 340u, 52u, 34u), (bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    [Fact]
    public void ProjectRequested_PreservesAnInvalidRequestForDiagnosticRendering()
    {
        var projected = RoiFrameMapper.ProjectRequested(new RoiRect(3000, 400, 90, 60), 2560, 1440);

        Assert.Equal((3000, 400, 90, 60), (projected.X, projected.Y, projected.Width, projected.Height));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RoiFrameMapper.MapAccepted(new RoiRect(3000, 400, 90, 60), 2560, 1440));
    }

    [Fact]
    public void MapAccepted_ClampsAReferenceRoiThatExtendsBeyondTheFrameEdge()
    {
        var bounds = RoiFrameMapper.MapAccepted(new RoiRect(2559, 100, 8, 12), 2560, 1440);

        Assert.Equal((2559u, 100u, 1u, 12u), (bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }
}

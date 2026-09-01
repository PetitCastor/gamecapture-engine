using System.Drawing;
using GameCapture.Contracts.Proto;
using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class RoiOverlayControllerTests
{
    [Fact]
    public void VisibleOverlay_DrawsEveryMatchingSubscriptionAndRefreshesOnRoiReplacement()
    {
        using var fixture = new RoiOverlayControllerFixture();
        fixture.AddSubscription("one", 100);
        fixture.AddSubscription("two", 300);

        var state = fixture.Controller.SetVisible(fixture.Entry, visible: true);

        Assert.True(state.CanShow);
        Assert.True(state.IsVisible);
        Assert.Equal(2, fixture.Renderer.Shapes.Count);
        Assert.Contains(fixture.Renderer.Shapes, shape => shape.Label.EndsWith("/ one", StringComparison.Ordinal));
        Assert.Contains(fixture.Renderer.Shapes, shape => shape.Label.EndsWith("/ two", StringComparison.Ordinal));

        fixture.FirstSubscription.SetRois(new RoiSetUpdate
        {
            Rois = { Roi("replacement", 500) },
        });

        Assert.Equal(2, fixture.Renderer.Shapes.Count);
        Assert.Contains(fixture.Renderer.Shapes, shape => shape.Label.EndsWith("/ replacement", StringComparison.Ordinal));
    }

    [Fact]
    public void ConnectionAndPluginStop_AutomaticallyHideTheOverlay()
    {
        using var fixture = new RoiOverlayControllerFixture();
        fixture.AddSubscription("one", 100);
        fixture.Controller.SetVisible(fixture.Entry, visible: true);

        fixture.Registry.Unregister(fixture.FirstSubscription);

        Assert.False(fixture.Controller.GetState(fixture.Entry).CanShow);
        Assert.True(fixture.Renderer.HideCount > 0);

        fixture.AddSubscription("two", 300);
        fixture.Controller.SetVisible(fixture.Entry, visible: true);
        fixture.IsPluginRunning = false;
        fixture.RaiseLauncherChanged();

        Assert.False(fixture.Controller.GetState(fixture.Entry).CanShow);
        Assert.True(fixture.Renderer.HideCount > 1);
    }

    [Fact]
    public void Show_RejectsNoFrameAndNeverCallsTheRenderer()
    {
        using var fixture = new RoiOverlayControllerFixture(hasFrame: false);
        fixture.AddSubscription("one", 100);

        var error = Assert.Throws<InvalidOperationException>(
            () => fixture.Controller.SetVisible(fixture.Entry, visible: true));

        Assert.Contains("No captured frame", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Renderer.ShowCount);
    }

    private static RoiSpec Roi(string id, uint x)
        => new()
        {
            Id = id,
            Rect = new Rect { X = x, Y = 100, Width = 80, Height = 40 },
        };

}

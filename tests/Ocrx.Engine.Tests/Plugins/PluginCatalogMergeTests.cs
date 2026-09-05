using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests.Plugins;

/// <summary>Pins <see cref="PluginCatalogMerge"/>: the id namespace stays a strict partition.</summary>
public class PluginCatalogMergeTests
{
    private static CatalogEntry Entry(string id, ReleaseChannel channel = ReleaseChannel.Stable)
        => new(id, id, "d", $"https://github.com/PetitCastor/ocrx-plugins/releases/{id}.zip", channel);

    [Fact]
    public void Combine_AppendsPreviewsThatDoNotCollide()
    {
        var stable = new[] { Entry("mission-plugin") };
        var previews = new[] { Entry("refinery-plugin", ReleaseChannel.Preview) };

        var combined = PluginCatalogMerge.Combine(stable, previews, out var dropped);

        Assert.Equal(["mission-plugin", "refinery-plugin"], combined.Select(e => e.Id));
        Assert.Empty(dropped);
    }

    [Fact]
    public void Combine_DropsOnlyTheCollidingPreviewEntry()
    {
        var stable = new[] { Entry("mission-plugin"), Entry("refinery-plugin") };
        var previews = new[]
        {
            Entry("mission-plugin", ReleaseChannel.Preview),
            Entry("signature-plugin", ReleaseChannel.Preview),
        };

        var combined = PluginCatalogMerge.Combine(stable, previews, out var dropped);

        Assert.Equal(["mission-plugin", "refinery-plugin", "signature-plugin"], combined.Select(e => e.Id));
        Assert.Equal(["mission-plugin"], dropped);
    }
}

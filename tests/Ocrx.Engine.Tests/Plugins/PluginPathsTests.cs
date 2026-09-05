using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests.Plugins;

/// <summary>
/// Pins the last guard between a catalog id and the filesystem: an id that did not pass the catalog's
/// alphabet never becomes a path at all.
/// </summary>
public class PluginPathsTests
{
    [Fact]
    public void ValidId_BecomesAFolderUnderTheRoot()
        => Assert.Equal(
            Path.Combine(@"C:\plugins", "mission-plugin"),
            PluginPaths.PluginDirectory(@"C:\plugins", "mission-plugin"));

    [Theory]
    [InlineData("../escape")]
    [InlineData(@"..\escape")]
    [InlineData("C:")]
    [InlineData("")]
    public void IdThatCouldEscapeTheRoot_IsRefused(string id)
        => Assert.Throws<ArgumentException>(() => PluginPaths.PluginDirectory(@"C:\plugins", id));

    [Fact]
    public void StateFile_SitsInTheRoot()
        => Assert.Equal(
            Path.Combine(@"C:\plugins", PluginPaths.StateFileName),
            PluginPaths.StateFile(@"C:\plugins"));
}

using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests.Plugins;

/// <summary>
/// Pins how a release tag is read back out of the URL GitHub redirects a latest-download link to.
/// The catalog carries no version field, so this is the engine's only source of "which release is
/// this?" — and a wrong answer here is what would invent or hide an update.
/// </summary>
public class ReleaseVersionResolverTests
{
    private static bool Extract(string url, out string tag)
        => ReleaseVersionResolver.TryExtractTag(new Uri(url), out tag);

    [Fact]
    public void VersionedAssetUrl_YieldsTheTag()
    {
        Assert.True(Extract(
            "https://github.com/PetitCastor/ocrx-plugins/releases/download/v1.0.4/MissionPlugin-win-x64.zip",
            out var tag));

        Assert.Equal("v1.0.4", tag);
    }

    [Fact]
    public void PreReleaseTag_IsAccepted()
    {
        Assert.True(Extract(
            "https://github.com/PetitCastor/ocrx-plugins/releases/download/v2.0.0-rc.1/A.zip",
            out var tag));

        Assert.Equal("v2.0.0-rc.1", tag);
    }

    [Fact]
    public void LatestDownloadUrl_HasNoTagYet()
    {
        // This is the pre-redirect form. Reading the asset name as a version is exactly the mistake
        // the tag-shape check exists to prevent.
        Assert.False(Extract(
            "https://github.com/PetitCastor/ocrx-plugins/releases/latest/download/MissionPlugin-win-x64.zip",
            out var tag));

        Assert.Equal("", tag);
    }

    [Fact]
    public void SignedContentHostUrl_HasNoTag()
        => Assert.False(Extract("https://objects.githubusercontent.com/github-production-release-asset/1/2?token=abc", out _));

    [Fact]
    public void TrailingDownloadSegment_HasNothingToRead()
        => Assert.False(Extract("https://github.com/PetitCastor/ocrx-plugins/releases/download", out _));

    [Fact]
    public void EscapedTag_IsUnescaped()
    {
        Assert.True(Extract("https://github.com/PetitCastor/ocrx-plugins/releases/download/v1%2E2%2E3/A.zip", out var tag));

        Assert.Equal("v1.2.3", tag);
    }
}

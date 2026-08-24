using VerifyXunit;
using Xunit;

namespace GameCapture.Sdk.Tests;

public sealed class PluginConfigSnapshotTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"gc-sdk-snap-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public Task ApproveSerializedPluginConfigShape()
    {
        var path = Path.Combine(_dir, "config.json");
        PluginConfig.Load<SnapshotPluginConfig>(path);
        return Verify(File.ReadAllText(path), extension: "json");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

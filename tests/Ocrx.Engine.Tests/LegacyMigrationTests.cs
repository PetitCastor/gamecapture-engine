using Ocrx.Engine.Migration;
using Xunit;

namespace Ocrx.Engine.Tests;

public sealed class LegacyMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ocrx-migration-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SuccessfulUninstall_DeletesLegacyDataAndWritesMarker()
    {
        var updater = CreateLegacyInstall();
        var legacyData = Directory.CreateDirectory(Path.Combine(_root, "GameCapture")).FullName;
        File.WriteAllText(Path.Combine(legacyData, "config.json"), "legacy");

        var result = await LegacyMigration.TryCompleteAsync(
            _root,
            (path, _) => Task.FromResult<int?>(path == updater ? 0 : 1),
            _ => { });

        Assert.True(result);
        Assert.False(Directory.Exists(legacyData));
        Assert.True(File.Exists(Path.Combine(_root, "OCRX", ".gamecapture-v1-removed")));
    }

    [Fact]
    public async Task FailedUninstall_LeavesLegacyDataAndDoesNotWriteMarker()
    {
        CreateLegacyInstall();
        var legacyData = Directory.CreateDirectory(Path.Combine(_root, "GameCapture")).FullName;

        var result = await LegacyMigration.TryCompleteAsync(
            _root,
            (_, _) => Task.FromResult<int?>(23),
            _ => { });

        Assert.False(result);
        Assert.True(Directory.Exists(legacyData));
        Assert.False(File.Exists(Path.Combine(_root, "OCRX", ".gamecapture-v1-removed")));
    }

    [Fact]
    public async Task MissingUpdater_LeavesLegacyDataUntouched()
    {
        Directory.CreateDirectory(Path.Combine(_root, "GameCaptureEngine"));
        var legacyData = Directory.CreateDirectory(Path.Combine(_root, "GameCapture")).FullName;

        var result = await LegacyMigration.TryCompleteAsync(
            _root,
            (_, _) => Task.FromResult<int?>(0),
            _ => { });

        Assert.False(result);
        Assert.True(Directory.Exists(legacyData));
    }

    [Fact]
    public async Task NoLegacyInstall_StillRemovesLegacyData()
    {
        var legacyData = Directory.CreateDirectory(Path.Combine(_root, "GameCapture")).FullName;

        var result = await LegacyMigration.TryCompleteAsync(
            _root,
            (_, _) => throw new InvalidOperationException("Uninstaller must not run."),
            _ => { });

        Assert.True(result);
        Assert.False(Directory.Exists(legacyData));
    }

    private string CreateLegacyInstall()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "GameCaptureEngine")).FullName;
        var updater = Path.Combine(directory, "Update.exe");
        File.WriteAllText(updater, "test double");
        return updater;
    }
}

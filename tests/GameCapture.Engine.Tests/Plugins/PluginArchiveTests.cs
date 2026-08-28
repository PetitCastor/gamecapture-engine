using System.IO.Compression;
using System.Text;
using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests.Plugins;

/// <summary>
/// Pins <see cref="PluginArchive"/>: the downloaded zip is remote content, so unpacking it has to
/// stay inside the plugin folder and has to end up with exactly one executable. Both rules are
/// asserted against hand-built archives rather than a real release asset.
/// </summary>
public class PluginArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gc-archive-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public void SingleExecutable_IsUnpackedAndReturned()
    {
        using var zip = Zip(("MissionPlugin.exe", "binary"));
        var destination = Path.Combine(_root, "mission-plugin");

        var executable = PluginArchive.Extract(zip, destination);

        Assert.Equal(Path.Combine(destination, "MissionPlugin.exe"), executable);
        Assert.Equal("binary", File.ReadAllText(executable));
    }

    [Fact]
    public void SupportingFilesAlongsideTheExecutable_AreKept()
    {
        using var zip = Zip(("SignaturePlugin.exe", "binary"), ("config.json", "{}"));
        var destination = Path.Combine(_root, "signature-plugin");

        PluginArchive.Extract(zip, destination);

        Assert.True(File.Exists(Path.Combine(destination, "config.json")));
    }

    [Fact]
    public void EntryEscapingTheDestination_IsRejected()
    {
        using var zip = Zip(("../escaped.exe", "binary"));

        var ex = Assert.Throws<InvalidDataException>(
            () => PluginArchive.Extract(zip, Path.Combine(_root, "mission-plugin")));

        Assert.Contains("outside", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.exe")));
    }

    [Theory]
    [InlineData(@"..\escaped.exe")]
    [InlineData("nested/../../escaped.exe")]
    [InlineData(@"C:\escaped.exe")]
    [InlineData("/escaped.exe")]
    public void OtherTraversalShapes_AreRejectedToo(string name)
    {
        using var zip = Zip((name, "binary"));

        Assert.Throws<InvalidDataException>(() => PluginArchive.Extract(zip, Path.Combine(_root, "mission-plugin")));
    }

    [Fact]
    public void EntryThatUnpacksToMoreThanItDeclares_IsStopped()
    {
        // The declared uncompressed size is the archive's own claim about itself. Extracting on the
        // strength of it would let a highly compressible entry that reports a few bytes write until
        // the disk fills, so the write is what has to be bounded.
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("MissionPlugin.exe");
            using var stream = entry.Open();
            var block = new byte[1024 * 1024];
            for (var i = 0; i < 8; i++)
                stream.Write(block);
        }
        buffer.Position = 0;

        var ex = Assert.Throws<InvalidDataException>(
            () => PluginArchive.Extract(buffer, Path.Combine(_root, "mission-plugin"), maxTotalBytes: 4 * 1024 * 1024));

        Assert.Contains("declares", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SecondExecutable_IsRejected()
    {
        using var zip = Zip(("MissionPlugin.exe", "a"), ("Extra.exe", "b"));

        Assert.Throws<InvalidDataException>(() => PluginArchive.Extract(zip, Path.Combine(_root, "mission-plugin")));
    }

    [Fact]
    public void ArchiveWithNoExecutable_IsRejected()
    {
        using var zip = Zip(("readme.txt", "hello"));

        Assert.Throws<InvalidDataException>(() => PluginArchive.Extract(zip, Path.Combine(_root, "mission-plugin")));
    }
}

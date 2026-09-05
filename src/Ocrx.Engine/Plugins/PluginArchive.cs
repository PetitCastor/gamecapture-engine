using System.IO.Compression;

namespace Ocrx.Engine.Plugins;

/// <summary>
/// Unpacks a downloaded plugin release asset. Pure file work over a stream — the download that
/// produced the stream is <see cref="PluginInstaller"/>'s job — so the extraction rules below are
/// testable without touching the network.
/// </summary>
/// <remarks>
/// The archive is remote content, so it is treated as hostile even though it arrived over a
/// validated URL: every entry's resolved path must stay inside the destination, and the archive must
/// look like what the plugins repository actually publishes — one self-contained exe and nothing
/// else. An archive that unpacks somewhere unexpected, or that hides a second executable, is
/// rejected outright rather than partially installed.
/// </remarks>
public static class PluginArchive
{
    /// <summary>Refuses an asset far larger than any plugin the repository publishes.</summary>
    private const long MaxEntryBytes = 512L * 1024 * 1024;

    /// <summary>Ceiling across all entries, so many small ones cannot add up past the per-entry cap.</summary>
    private const long MaxTotalBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Extracts <paramref name="zip"/> into <paramref name="destinationRoot"/>, which must not
    /// already exist or must be empty.
    /// </summary>
    /// <returns>Full path of the plugin's executable.</returns>
    /// <param name="maxTotalBytes">Ceiling on what the whole archive may write. Overridable so the
    /// bound itself can be tested without building a gigabyte-sized fixture.</param>
    /// <exception cref="InvalidDataException">The archive is malformed or fails a safety rule.</exception>
    public static string Extract(Stream zip, string destinationRoot, long maxTotalBytes = MaxTotalBytes)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);

        string? executable = null;
        var budget = maxTotalBytes;
        foreach (var entry in archive.Entries)
        {
            // Directory entries carry an empty Name; the directories are created from the file paths.
            if (entry.Name.Length == 0)
                continue;

            // entry.Length is the size the archive claims, which a crafted archive is free to
            // understate — it is checked here only to reject an obviously oversized entry cheaply.
            // What actually bounds the write is the budget passed to CopyBounded below.
            if (entry.Length > MaxEntryBytes)
                throw new InvalidDataException($"'{entry.FullName}' is larger than the {MaxEntryBytes / (1024 * 1024)} MB limit.");

            var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"'{entry.FullName}' would unpack outside the plugin folder.");

            if (Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (executable is not null)
                    throw new InvalidDataException("The archive contains more than one executable.");
                executable = target;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using var source = entry.Open();
            using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            budget -= CopyBounded(source, destination, Math.Min(budget, MaxEntryBytes), entry.FullName);
        }

        return executable ?? throw new InvalidDataException("The archive contains no executable.");
    }

    // Copies at most `budget` bytes and fails past it, so what the archive claims an entry weighs
    // never decides how much gets written. A zip whose entries decompress to far more than their
    // declared size is stopped here rather than filling the disk.
    private static long CopyBounded(Stream source, Stream destination, long budget, string entryName)
    {
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(buffer)) > 0)
        {
            written += read;
            if (written > budget)
                throw new InvalidDataException($"'{entryName}' unpacks to more than the archive declares.");

            destination.Write(buffer.AsSpan(0, read));
        }

        return written;
    }
}

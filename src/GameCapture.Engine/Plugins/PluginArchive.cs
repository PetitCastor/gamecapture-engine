using System.IO.Compression;

namespace GameCapture.Engine.Plugins;

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

    /// <summary>
    /// Extracts <paramref name="zip"/> into <paramref name="destinationRoot"/>, which must not
    /// already exist or must be empty.
    /// </summary>
    /// <returns>Full path of the plugin's executable.</returns>
    /// <exception cref="InvalidDataException">The archive is malformed or fails a safety rule.</exception>
    public static string Extract(Stream zip, string destinationRoot)
    {
        var root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);

        string? executable = null;
        foreach (var entry in archive.Entries)
        {
            // Directory entries carry an empty Name; the directories are created from the file paths.
            if (entry.Name.Length == 0)
                continue;

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
            entry.ExtractToFile(target, overwrite: true);
        }

        return executable ?? throw new InvalidDataException("The archive contains no executable.");
    }
}

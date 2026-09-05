using System.Diagnostics;

namespace Ocrx.Engine.Migration;

/// <summary>Removes a verified GameCapture v1 install and its data after OCRX starts successfully.</summary>
internal static class LegacyMigration
{
    private const string LegacyInstallDirectoryName = "GameCaptureEngine";
    private const string LegacyDataDirectoryName = "GameCapture";
    private const string OcrxDataDirectoryName = "OCRX";
    private const string MarkerFileName = ".gamecapture-v1-removed";
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(2);

    public static Task<bool> TryCompleteAsync(ConsoleSink sink, CancellationToken cancellationToken = default) =>
        TryCompleteAsync(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RunUninstallerAsync,
            sink.WriteLine,
            cancellationToken);

    internal static async Task<bool> TryCompleteAsync(
        string localApplicationData,
        Func<string, CancellationToken, Task<int?>> runUninstaller,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        var localRoot = Path.GetFullPath(localApplicationData);
        var ocrxRoot = Path.GetFullPath(Path.Combine(localRoot, OcrxDataDirectoryName));
        var markerPath = Path.Combine(ocrxRoot, MarkerFileName);
        if (File.Exists(markerPath))
            return true;

        var legacyInstall = Path.GetFullPath(Path.Combine(localRoot, LegacyInstallDirectoryName));
        var legacyData = Path.GetFullPath(Path.Combine(localRoot, LegacyDataDirectoryName));
        EnsureLiteralChild(localRoot, legacyInstall, LegacyInstallDirectoryName);
        EnsureLiteralChild(localRoot, legacyData, LegacyDataDirectoryName);

        try
        {
            if (Directory.Exists(legacyInstall))
            {
                var updater = Path.Combine(legacyInstall, "Update.exe");
                if (!File.Exists(updater))
                {
                    report($"GameCapture migration is partial: '{legacyInstall}' exists but its Update.exe is missing. Legacy data was not deleted.");
                    return false;
                }

                var exitCode = await runUninstaller(updater, cancellationToken);
                if (exitCode is null)
                {
                    report("GameCapture migration is partial: the silent uninstaller timed out. Legacy data was not deleted.");
                    return false;
                }

                if (exitCode != 0)
                {
                    report($"GameCapture migration is partial: the silent uninstaller exited with code {exitCode}. Legacy data was not deleted.");
                    return false;
                }
            }

            if (Directory.Exists(legacyData))
                Directory.Delete(legacyData, recursive: true);

            Directory.CreateDirectory(ocrxRoot);
            await File.WriteAllTextAsync(markerPath, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            report("GameCapture v1 migration completed.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            report($"GameCapture migration is partial: {ex.Message} Legacy data cleanup stopped.");
            return false;
        }
    }

    private static async Task<int?> RunUninstallerAsync(string updaterPath, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = updaterPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "uninstall", "--silent" },
        }) ?? throw new InvalidOperationException($"Could not start legacy uninstaller '{updaterPath}'.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UninstallTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);

            return null;
        }
    }

    private static void EnsureLiteralChild(string root, string candidate, string expectedName)
    {
        var expected = Path.GetFullPath(Path.Combine(root, expectedName));
        if (!candidate.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(candidate)!.Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing legacy cleanup outside the literal '{expectedName}' application-data directory.");
        }
    }
}

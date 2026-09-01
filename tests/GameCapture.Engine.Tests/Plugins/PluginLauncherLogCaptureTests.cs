using GameCapture.Engine.Plugins;
using Xunit;

namespace GameCapture.Engine.Tests;

/// <summary>
/// The one thing the pure seams cannot prove: that attaching the handlers before <c>Start</c> and
/// beginning the reads after it actually delivers a child's output into the buffer. Real processes, so
/// every case is an Integration test — but not the engine and not OCR, just two Windows binaries that
/// print something and exit.
/// </summary>
/// <remarks>
/// <see cref="PluginLauncher.Start"/> passes no arguments and runs with <c>UseShellExecute = false</c>,
/// which rules out a <c>.cmd</c> subject, so these use executables from System32 that say something
/// useful with a bare invocation.
/// </remarks>
public class PluginLauncherLogCaptureTests
{
    private static InstalledPlugin SystemBinary(string id, string exeName)
        => new(
            id,
            exeName,
            "v0",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exeName),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Drives the launcher until the child has been noticed as gone. Pruning is what writes the exit
    /// notice, and it only happens when something asks about the running set — exactly as it does in
    /// production, where the control API's poll timer is the thing asking.
    /// </summary>
    private static async Task WaitUntilPrunedAsync(PluginLauncher launcher, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (launcher.IsRunning(id) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(launcher.IsRunning(id));
    }

    /// <summary>
    /// Waits for the exit to be noticed, then for the buffer to settle. The readers finish
    /// asynchronously after the process ends, so a line can still be in flight the instant the exit
    /// notice is written — polling for the shape the test needs beats sleeping blind for it.
    /// </summary>
    private static async Task<PluginLogPage> ReadAfterExitAsync(
        PluginLogStore logs,
        PluginLauncher launcher,
        string id,
        PluginLogStream expected)
    {
        await WaitUntilPrunedAsync(launcher, id);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        PluginLogPage page;
        do
        {
            page = logs.Read(id, after: -1, limit: 1000);
            var sawOutput = page.Lines.Any(line => line.Stream == expected && line.Text.Length > 0);
            var sawExit = page.Lines.Any(line => line.Text.StartsWith("-- exited", StringComparison.Ordinal));
            if (sawOutput && sawExit)
                break;
            await Task.Delay(50);
        }
        while (DateTime.UtcNow < deadline);

        return page;
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AProcessThatWritesToStdout_HasItsLinesCaptured()
    {
        var logs = new PluginLogStore();
        using var launcher = new PluginLauncher { Logs = logs };
        var plugin = SystemBinary("whoami-probe", "whoami.exe");

        launcher.Start(plugin);
        var page = await ReadAfterExitAsync(logs, launcher, plugin.Id, PluginLogStream.Stdout);

        Assert.Contains(page.Lines, line => line.Stream == PluginLogStream.Stdout && line.Text.Length > 0);
        Assert.Contains(page.Lines, line => line.Stream == PluginLogStream.Engine && line.Text.StartsWith("-- started", StringComparison.Ordinal));
    }

    /// <summary>
    /// stderr is where the SDK reports the failures worth reading — usage errors and
    /// <c>invalid output configuration</c> — so it has to arrive tagged as stderr, not folded into
    /// stdout. The subject's actual wording is localized, so only the tag and the exit code are asserted.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AProcessThatWritesToStderr_HasThoseLinesTaggedStderr()
    {
        var logs = new PluginLogStore();
        using var launcher = new PluginLauncher { Logs = logs };
        var plugin = SystemBinary("findstr-probe", "findstr.exe");

        launcher.Start(plugin);
        var page = await ReadAfterExitAsync(logs, launcher, plugin.Id, PluginLogStream.Stderr);

        Assert.Contains(page.Lines, line => line.Stream == PluginLogStream.Stderr && line.Text.Length > 0);
    }

    /// <summary>
    /// The reason the feature exists. A plugin that dies immediately leaves a row that says "stopped"
    /// and a user with no explanation; the output and the exit code have to survive the process, the
    /// prune, and the disposal of the <see cref="System.Diagnostics.Process"/> that produced them.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AProcessThatExitsImmediately_LeavesItsOutputAndExitCodeReadable()
    {
        var logs = new PluginLogStore();
        using var launcher = new PluginLauncher { Logs = logs };
        var plugin = SystemBinary("findstr-probe", "findstr.exe");

        launcher.Start(plugin);
        var page = await ReadAfterExitAsync(logs, launcher, plugin.Id, PluginLogStream.Stderr);

        Assert.Contains(page.Lines, line => line.Stream == PluginLogStream.Stderr);
        var exit = Assert.Single(page.Lines, line => line.Text.StartsWith("-- exited", StringComparison.Ordinal));
        Assert.Equal(PluginLogStream.Engine, exit.Stream);
        Assert.DoesNotContain("unknown", exit.Text, StringComparison.Ordinal);

        // Still readable through the store after everything the launcher owned is gone.
        Assert.True(logs.Has(plugin.Id));
    }

    /// <summary>
    /// File.Exists says nothing about whether a file can be executed, so a corrupt download throws out
    /// of Process.Start rather than returning false. The launch still has to leave the row consistent:
    /// the buffer that was opened before the attempt says why it failed, and Changed has to fire so the
    /// UI learns there is now something to read — otherwise the row keeps its stale shape until some
    /// unrelated plugin happens to start or stop.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenTheExecutableCannotBeRun_TheFailureIsRecordedAndAnnounced()
    {
        var logs = new PluginLogStore();
        using var launcher = new PluginLauncher { Logs = logs };
        var changed = 0;
        launcher.Changed += () => Interlocked.Increment(ref changed);

        var notAnExecutable = Path.Combine(Path.GetTempPath(), $"gc-not-an-exe-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(notAnExecutable, "this is not a PE file");
        try
        {
            var plugin = new InstalledPlugin("broken-plugin", "Broken", "v0", notAnExecutable, DateTimeOffset.UtcNow);

            Assert.ThrowsAny<Exception>(() => launcher.Start(plugin));

            Assert.False(launcher.IsRunning(plugin.Id));
            Assert.True(logs.Has(plugin.Id));
            Assert.Contains(
                logs.Read(plugin.Id, after: -1, limit: 100).Lines,
                line => line.Stream == PluginLogStream.Engine && line.Text.StartsWith("-- failed to start", StringComparison.Ordinal));
            Assert.True(changed > 0);
        }
        finally
        {
            File.Delete(notAnExecutable);
        }
    }

    /// <summary>
    /// Stopping kills the child and records it, and the running set reflects that immediately — the
    /// kill happens outside the lock, so this also covers that nothing was left half-removed by moving
    /// it out.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void StoppingAPlugin_RecordsTheNoticeAndClearsTheRunningSet()
    {
        var logs = new PluginLogStore();
        using var launcher = new PluginLauncher { Logs = logs };
        var plugin = SystemBinary("whoami-probe", "whoami.exe");

        launcher.Start(plugin);

        // Whether the child is still alive or has already exited on its own is a race this must not
        // care about: Stop removes it either way, and Terminate tolerates a process that is gone.
        launcher.Stop(plugin.Id);

        Assert.False(launcher.IsRunning(plugin.Id));
        Assert.DoesNotContain(plugin.Id, launcher.RunningIds);
        Assert.Contains(
            logs.Read(plugin.Id, after: -1, limit: 100).Lines,
            line => line.Stream == PluginLogStream.Engine && line.Text == "-- stopped by the engine --");
    }

    /// <summary>
    /// A launcher without a store must not redirect anything: the child keeps inheriting the engine's
    /// streams, which is what every existing test that only cares about the running set relies on.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WithoutAStore_TheProcessStillRunsAndIsPruned()
    {
        using var launcher = new PluginLauncher();
        var plugin = SystemBinary("whoami-probe", "whoami.exe");

        launcher.Start(plugin);
        await WaitUntilPrunedAsync(launcher, plugin.Id);
    }
}

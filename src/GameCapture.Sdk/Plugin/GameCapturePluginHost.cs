using GameCapture.Contracts;

namespace GameCapture.Sdk;

/// <summary>
/// Runs a plugin: loads its config, parses the shared command line, connects, subscribes, feeds it
/// ticks, reconnects when the engine goes away, and prints a summary on the way out.
/// </summary>
/// <remarks>
/// This is the ~90 lines both existing plugins carried as a verbatim copy of each other, comments
/// included (<c>MissionPlugin/Program.cs</c> and <c>RefineryRunner.cs</c>). The comments came along,
/// because every one of them documents a trap that was hit for real: what an
/// <see cref="OperationCanceledException"/> means depending on who cancelled, why the engine wait
/// needs a finite budget, why a reconnect has to be paced, why one bad tick must not end a run.
/// </remarks>
public static class GameCapturePluginHost
{
    /// <summary>
    /// Runs until the tick stream ends normally (replay finished, or the engine shut down), Ctrl+C,
    /// or a failure a reconnect cannot fix.
    /// </summary>
    /// <returns>
    /// 0 for every orderly ending, including Ctrl+C — a plugin stopped on purpose did not fail. 1 for
    /// a usage error and for a protocol mismatch, which are the two endings a supervisor should not
    /// simply restart.
    /// </returns>
    public static async Task<int> RunAsync(IGameCapturePlugin plugin, string[] args,
        PluginHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        ArgumentNullException.ThrowIfNull(args);

        options ??= new PluginHostOptions();

        // The host owns a ConsoleSink only when nobody handed it an output. Constructed first so
        // every later write goes through it and disposal (status-bar erase, cursor restore) is
        // guaranteed on every return path — the reason both Program.cs files opened with this line.
        var ownedSink = options.Output is null ? new ConsoleSink() : null;
        var output = options.Output ?? ownedSink!;

        try
        {
            return await RunCoreAsync(plugin, args, options, output);
        }
        finally
        {
            ownedSink?.Dispose();
        }
    }

    private static async Task<int> RunCoreAsync(IGameCapturePlugin plugin, string[] args,
        PluginHostOptions options, IPluginOutput output)
    {
        output.WriteLine($"=== GameCapture — {plugin.Name} ===");

        if (options.ExtraArgHandler?.Invoke(args) is { } extraError)
        {
            Console.Error.WriteLine(extraError);
            return 1;
        }

        var config = LoadConfig(options);

        var parsed = PluginArgs.Parse(args, config.PipeName, out var usageError);
        if (parsed is null)
        {
            Console.Error.WriteLine(usageError);
            return 1;
        }

        var records = new List<CaptureRecord>();

        // Linked rather than the caller's token directly: the Ctrl+C handler below has to be able to
        // cancel, and an embedding host's token must cancel this run too.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(options.ShutdownToken);
        var ct = cts.Token;

        ConsoleCancelEventHandler? cancelHandler = null;
        if (options.HandleCancelKeyPress)
        {
            cancelHandler = (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
        }

        try
        {
            using var client = new CaptureClient(parsed.PipeName);

            // Read once services exists below — a sink built from config needs it to know replay
            // mode at emit time, but sinks have to be composed before PluginServices can be
            // constructed with them. The lambda closes over the variable, not its (not yet assigned)
            // value, so this is legal: nothing calls isReplay until well after services is set.
            PluginServices? services = null;
            bool IsReplay() => services?.Engine.ReplayMode ?? false;

            PluginOutputPipeline outputPipeline;
            try
            {
                outputPipeline = await PluginOutputPipeline.CreateAsync(options, config, IsReplay,
                    output);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"invalid output configuration: {ex.Message}");
                return 1;
            }

            // Debug dumps are the engine's to write — the frame never crosses the boundary, only the
            // path it was written to. Null switches the whole debug path off inside the plugin.
            // recordSink is null here (not options.RecordSink): the output pipeline already adapts
            // the legacy callback as a sink. Passing it a second time would invoke it twice per
            // record, once synchronously and once off the drain thread.
            services = new PluginServices(records, output, parsed.Verbose,
                config.SaveDebugFrames ? client.DumpFrameAsync : null,
                client.ReadRoiAsync, recordSink: null, outputPipeline: outputPipeline);
            services.StartDraining(ct);

            output.WriteLine($"Pipe:      {parsed.PipeName}");
            output.WriteLine($"Debug:     {(config.SaveDebugFrames
                ? "asking the engine for a PNG per capture"
                : "in-memory only, no files")}");
            output.WriteLine();

            int exitCode;
            try
            {
                var sessionRunner = new PluginSessionRunner(plugin, client, services, output,
                    options, parsed.PipeName);
                var (reason, code) = await sessionRunner.RunAsync(ct);
                exitCode = code;
                plugin.OnSessionEvent(new SessionEvent.Ended(reason));
            }
            finally
            {
                // Always flush, even if OnSessionEvent above threw — and always before the summary,
                // since the summary must reflect what the sinks already received.
                await services.CompleteAndDrainAsync();
                WriteSummary(plugin, records, output);
            }
            return exitCode;
        }
        finally
        {
            if (cancelHandler is not null)
                Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static void WriteSummary(IGameCapturePlugin plugin, List<CaptureRecord> records,
        IPluginOutput output)
    {
        output.WriteLine();
        output.WriteLine($"=== Summary: {records.Count} captures ===");
        foreach (var g in records.GroupBy(r => (r.Plugin, r.Trigger)))
            output.WriteLine($"  {g.Key.Plugin} ({g.Key.Trigger}): {g.Count()}");

        // After the host's own lines, so a plugin's totals read as an elaboration of them rather
        // than as a competing summary.
        foreach (var line in plugin.SummaryLines())
            output.WriteLine(line);
    }

    /// <summary>
    /// The config the host itself needs. A plugin with settings of its own loads them with
    /// <see cref="PluginConfig.Load{T}"/> and passes the instance through
    /// <see cref="PluginHostOptions.Config"/>; the host then does not touch the file, because that
    /// load already wrote the defaults on a first run.
    /// </summary>
    private static PluginConfig LoadConfig(PluginHostOptions options)
    {
        if (options.Config is { } supplied)
            return supplied;

        if (options.ConfigFileName is not { Length: > 0 } fileName)
            return new HostPluginConfig();

        return PluginConfig.Load<HostPluginConfig>(
            Path.Combine(AppContext.BaseDirectory, fileName));
    }

    /// <summary>The base settings and nothing else, for a plugin that needs no settings of its own.</summary>
    private sealed class HostPluginConfig : PluginConfig;
}

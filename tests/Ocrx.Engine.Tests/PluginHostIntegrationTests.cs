using Ocrx.Contracts.Proto;
using Ocrx.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace Ocrx.Engine.Tests;

/// <summary>
/// <see cref="OcrxPluginHost"/> against a real engine over a real pipe. The host is ~90 lines of
/// lifecycle whose every interesting decision is about a failure â€” a stream that ended, an engine
/// that vanished, a tick that threw â€” and none of those can be honestly staged against a stub of the
/// thing that is supposed to produce them.
/// </summary>
/// <remarks>
/// Lives with the engine's suite because this is the only project that can own both ends of the
/// pipe. <see cref="NullPlugin"/> is deliberately the plugin under all of it: a real parser's
/// behaviour would be indistinguishable from the host's in the assertions.
/// </remarks>
public class PluginHostIntegrationTests(ITestOutputHelper output)
{
    /// <summary>
    /// A hang bound, not a performance budget: the corpus is three frames of real Windows OCR, which
    /// measures in seconds. Anything near this means something is stuck rather than slow.
    /// </summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Frames the engine scans before the plugin process exists, and frames it scans afterwards.
    /// Both are small on purpose: the first only has to make "already capturing" true, and the
    /// second only has to be more than the handshake could account for. <see cref="GatedFrameSource"/>
    /// cycles the corpus, so neither is bounded by how many PNGs are in it.
    /// </summary>
    private const int FramesBeforeJoin = 3;
    private const int FramesAfterJoin = 3;

    private static string NewPipeName() => $"sc-host-{Guid.NewGuid():N}";

    /// <summary>
    /// Config loading off and the console left alone: a test host must not have a config.json written
    /// next to its assembly, and installing a Ctrl+C handler would take the test runner's own
    /// interrupt.
    /// </summary>
    private static PluginHostOptions Options(RecordingOutput sink, CancellationToken shutdown = default)
        => new()
        {
            Output = sink,
            ConfigFileName = null,
            HandleCancelKeyPress = false,
            ShutdownToken = shutdown,
            ReconnectDelay = TimeSpan.FromMilliseconds(50),
        };

    /// <summary>
    /// The acceptance test for the whole task: a plugin that is nothing but an
    /// <see cref="IOcrxPlugin"/> runs to completion over the corpus without owning one line of
    /// lifecycle code, and sees every frame the engine scanned.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task OverReplayCorpus_DispatchesEveryTickThenEndsWithReplayCompleted()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Started before anyone subscribes: in replay the loop holds the corpus until a client is
        // ready, so no frame is burned before the plugin is listening.
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin();
        plugin.Summary.Add("null: nothing to report");

        var exit = await OcrxPluginHost
            .RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        output.WriteLine($"{frameCount} frame(s) replayed, {plugin.TickCount} tick(s) dispatched");

        Assert.False(cts.IsCancellationRequested, "timed out");
        Assert.Equal(0, exit);

        // Non-empty first: an empty corpus would satisfy the equality below while proving nothing.
        Assert.NotEqual(0, frameCount);
        Assert.Equal(frameCount, plugin.TickCount);

        var connected = Assert.Single(plugin.EventsOf<SessionEvent.Connected>());
        Assert.NotEmpty(connected.Engine.EngineVersion);
        Assert.True(connected.Engine.ReplayMode);
        Assert.Equal(1u, connected.Engine.NegotiatedProtocol);

        // ReplayCompleted rather than EngineShutdown: the engine ran out of corpus, and a plugin
        // that persists anything cares which of the two happened.
        var ended = Assert.Single(plugin.EventsOf<SessionEvent.Ended>());
        Assert.Equal(StreamEndReason.ReplayCompleted, ended.Reason);

        Assert.Empty(plugin.EventsOf<SessionEvent.Reconnecting>());
        Assert.Empty(plugin.EventsOf<SessionEvent.TicksDropped>());

        // The summary is the host's, and the plugin's lines sit under it.
        Assert.Contains("=== Summary: 0 captures ===", hostOutput.Lines);
        Assert.Contains("null: nothing to report", hostOutput.Lines);
    }

    /// <summary>
    /// One bad tick must not end the run. The monolith swallowed a tracker's exception per tick for
    /// the same reason: one unparseable frame out of thousands is normal, and a plugin that dies on
    /// it loses every order it had accumulated.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenEveryTickThrows_TheRunStillCompletes()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin { ThrowOnTick = new InvalidOperationException("parser exploded") };

        var exit = await OcrxPluginHost
            .RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);

        // Reported rather than swallowed silently, and naming the plugin: with two plugins on one
        // engine, "tick failed" alone does not say whose.
        Assert.Contains("null: tick failed: parser exploded", hostOutput.Text);

        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// AbortTick against a ROI the engine genuinely cannot read. The failure is real â€” an off-frame
    /// rect, the mistake a mistyped constant produces â€” rather than a synthesised error result, so
    /// the policy is tested against the thing it exists to protect against.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AbortTickPolicy_WithdrawsEveryTickThatCarriesAFailedRoi()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.AbortTick);

        Assert.Equal(0, exit);
        Assert.NotEqual(0, frameCount);

        // Not one tick reaches the plugin: every frame fails the same ROI.
        Assert.Equal(0, plugin.TickCount);

        // Reported once per failure stretch, not once per tick â€” at the engine's cadence the latter
        // is a line twice a second for as long as the plugin runs.
        Assert.Single(hostOutput.Lines, l => l.Contains("ROI failure"));

        // And the run still ends cleanly: a persistently failing ROI is a plugin bug to be reported,
        // not a reason for the host to give up.
        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// PassThrough is what both existing plugins do de facto today: the host filters nothing and the
    /// plugin decides. Named so it is a decision rather than an omission.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task PassThroughPolicy_DeliversTicksWithFailedRoisAndSaysNothing()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.PassThrough);

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.DoesNotContain("ROI failure", hostOutput.Text);
    }

    /// <summary>
    /// SkipErrored delivers the tick having said which regions failed; the plugin is expected to
    /// check before trusting a reading.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task SkipErroredPolicy_DeliversTheTickAndNamesTheFailedRoi()
    {
        var (exit, plugin, hostOutput, frameCount) = await RunWithOffFrameRoiAsync(RoiErrorPolicy.SkipErrored);

        Assert.Equal(0, exit);
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.Contains("offscreen", hostOutput.Text);
    }

    /// <summary>
    /// The engine going away mid-session, which is the failure the whole reconnect loop exists for.
    /// The first engine serves the handshake and then dies without ever scanning a frame; the second
    /// takes over the same pipe and runs the corpus. A host that treated the drop as a clean stream
    /// end would exit 0 here with zero ticks â€” passing every other assertion in this file.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenTheEngineRestarts_TheHostReconnectsAndFinishesTheRun()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var plugin = new NullPlugin();

        // Scan loop deliberately not started: the client connects, subscribes, and is waiting on a
        // tick that will never come when the engine is pulled out from under it.
        var first = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), engineSink, verbose: false);
        await first.StartAsync(cts.Token);

        var run = OcrxPluginHost.RunAsync(plugin, ["--pipe", pipeName], Options(hostOutput, cts.Token));

        await WaitUntilAsync(() => plugin.EventsOf<SessionEvent.Connected>().Count == 1, cts.Token);

        await first.DisposeAsync();

        // Same pipe, fresh engine, corpus running this time.
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var second = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await second.StartAsync(cts.Token);
        var scan = second.RunScanAsync(cts.Token);

        var exit = await run.WaitAsync(TestTimeout);

        await scan;
        await second.StopAsync();

        output.WriteLine($"{plugin.TickCount} tick(s) after reconnect; events: " +
            string.Join(", ", plugin.Events.Select(e => e.GetType().Name)));

        Assert.False(cts.IsCancellationRequested, "timed out");
        Assert.Equal(0, exit);

        // Reconnected rather than exited: two connects, at least one reconnect notice between them.
        Assert.Equal(2, plugin.EventsOf<SessionEvent.Connected>().Count);
        Assert.NotEmpty(plugin.EventsOf<SessionEvent.Reconnecting>());
        Assert.Contains("engine connection lost â€” reconnecting", hostOutput.Text);

        // The attempt counter restarts per disconnected stretch, so the first notice of this one is 1.
        Assert.Equal(1, plugin.EventsOf<SessionEvent.Reconnecting>()[0].Attempt);

        // And the second engine's corpus actually reached the plugin.
        Assert.Equal(frameCount, plugin.TickCount);
        Assert.Equal(StreamEndReason.ReplayCompleted,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);

        // The gap tracker was reset across the reconnect: the second engine's sequence numbers start
        // over, and reporting that as dropped ticks would fire the event on every reconnect there is.
        Assert.Empty(plugin.EventsOf<SessionEvent.TicksDropped>());
    }

    /// <summary>
    /// Ctrl+C mid-run. The host installs a handler that does nothing but cancel this token, so
    /// cancelling it drives the identical path without the test process interrupting itself.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenCancelledMidSession_ExitsZeroWithTheSummary()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var plugin = new NullPlugin();

        // No scan loop: the plugin is parked on a stream that is alive and silent, which is where a
        // real Ctrl+C almost always lands.
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), engineSink, verbose: false);
        await engine.StartAsync(cts.Token);

        var run = OcrxPluginHost.RunAsync(plugin, ["--pipe", pipeName],
            Options(hostOutput, shutdown.Token));

        await WaitUntilAsync(() => plugin.EventsOf<SessionEvent.Connected>().Count == 1, cts.Token);
        await shutdown.CancelAsync();

        var exit = await run.WaitAsync(TestTimeout);

        Assert.Equal(0, exit);
        Assert.Contains("=== Summary: 0 captures ===", hostOutput.Lines);

        // Cancelled, not EngineShutdown: the engine is still up, and this plugin chose to stop.
        Assert.Equal(StreamEndReason.Cancelled,
            Assert.Single(plugin.EventsOf<SessionEvent.Ended>()).Reason);
    }

    /// <summary>
    /// The engine is already capturing when the plugin turns up â€” a user installing a plugin from
    /// the tray while the game is running, which is how a plugin is normally added at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other test in this file runs a replay source, and replay makes this scenario
    /// unreachable: <c>ScanLoop.RunAsync</c> waits on <c>WaitForAnySubscribedAsync</c> before it
    /// touches the corpus, so the plugin is always early no matter when it is started. Live mode has
    /// no such gate, and that difference is the whole subject here â€” the loop has been scanning,
    /// discarding ticks into an empty subscriber set, since before this plugin's process existed.
    /// </para>
    /// <para>
    /// <c>ProtocolHandshakeTests</c> covers the neighbouring case of a late <c>Hello</c>, but its
    /// <c>Track</c> call is opened before the frames are released and it never sends a
    /// <c>RoiSetUpdate</c>. What is unproven until here is the one the user actually performs: a
    /// connection opened after the fact, subscribing real ROIs, and getting results for them.
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task WhenTheEngineIsAlreadyCapturing_ALateStartingHostStillGetsTicks()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var plugin = new NullPlugin();

        // Owned by the host, which disposes it â€” hence no `using` of our own. Live mode for the
        // reason in the remarks; the gate is what lets this test state "N frames were scanned" as a
        // fact rather than as a hope about timing.
        var source = new GatedFrameSource(EngineTestFixtures.ReplayDir, isReplay: false);

        // The live path sleeps a scan interval between frames; at the floor this costs the test a
        // fraction of a second instead of several.
        var config = new EngineConfig { ScanIntervalMs = 100 };

        await using var engine = EngineHost.Create(pipeName, config, new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Capturing with nothing installed: no plugin process exists yet, and the loop does not care.
        var scan = engine.RunScanAsync(scanCts.Token);
        try
        {
            using var channel = TestNamedPipeChannel.Create(pipeName);
            var grpc = new CaptureEngineService.CaptureEngineServiceClient(channel);

            // Polled rather than slept, for the reason ProtocolHandshakeTests polls: the frames must
            // genuinely be scanned before the plugin starts, or this passes on timing instead.
            source.Release(FramesBeforeJoin);
            await WaitForFrameSeqAsync(grpc, FramesBeforeJoin, cts.Token);

            // Only now is the plugin launched. Nothing releases frames during the handshake, so
            // every tick it sees is one it is subscribed for.
            var run = OcrxPluginHost.RunAsync(plugin, ["--pipe", pipeName],
                Options(hostOutput, shutdown.Token));

            await WaitUntilAsync(() => plugin.EventsOf<SessionEvent.Connected>().Count == 1, cts.Token);

            // Frames it can only have seen by having joined late. Waited on the ANSWERED count:
            // an empty tick would satisfy the raw count while proving nothing about the ROI set.
            source.Release(FramesAfterJoin);
            await WaitUntilAsync(() => plugin.AnsweredTickCount >= FramesAfterJoin, cts.Token);

            // A live stream never ends on its own; the plugin is the one that stops.
            await shutdown.CancelAsync();
            var exit = await run.WaitAsync(TestTimeout);

            output.WriteLine($"{FramesBeforeJoin} frame(s) scanned before the plugin existed, " +
                $"{plugin.TickCount} tick(s) dispatched after it connected");

            Assert.False(cts.IsCancellationRequested, "timed out");
            Assert.Equal(0, exit);

            // The claim under test, and exact rather than "at least": the gate released exactly
            // FramesAfterJoin permits and nothing else can produce a frame, so a count above this
            // would mean a late joiner is being served ticks twice.
            Assert.Equal(FramesAfterJoin, plugin.TickCount);

            // And every one of them actually carried the ROI this plugin subscribed. Without this
            // the test passes on empty ticks â€” the engine ticks a client from the moment its Track
            // call opens, which is before its RoiSetUpdate has been applied, so "a tick arrived" and
            // "my subscription was honoured" are genuinely different claims.
            Assert.Equal(FramesAfterJoin, plugin.AnsweredTickCount);

            var connected = Assert.Single(plugin.EventsOf<SessionEvent.Connected>());
            Assert.False(connected.Engine.ReplayMode);

            // A late joiner's HelloAck is built from a status snapshot that has already seen a
            // frame, so it carries real dimensions â€” where a plugin started alongside the engine
            // gets the documented 0/0 and has to wait for its first tick to learn them.
            Assert.NotEqual(0, connected.Engine.FrameWidth);
            Assert.NotEqual(0, connected.Engine.FrameHeight);

            // The assertion that would actually catch a regression. The engine's frame sequence is
            // already past FramesBeforeJoin when this plugin's first tick arrives, and reporting
            // that head start as dropped ticks would tell every late-joining plugin it had missed
            // frames it was never entitled to.
            Assert.Empty(plugin.EventsOf<SessionEvent.TicksDropped>());
            Assert.Empty(plugin.EventsOf<SessionEvent.Reconnecting>());
        }
        finally
        {
            // Stop the loop before the host disposes the gated source out from under it.
            scanCts.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Runs the corpus with a plugin subscribing one readable ROI and one that cannot be read at all,
    /// under the given policy.
    /// </summary>
    private async Task<(int Exit, NullPlugin Plugin, RecordingOutput Output, int FrameCount)>
        RunWithOffFrameRoiAsync(RoiErrorPolicy policy)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var engineSink = new ConsoleSink();
        var hostOutput = new RecordingOutput();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, engineSink, verbose: false);
        await engine.StartAsync(cts.Token);
        var scan = engine.RunScanAsync(cts.Token);

        var plugin = new NullPlugin(policy)
        {
            Rois =
            [
                EngineTestFixtures.PanelStateSubscription(),
                EngineTestFixtures.OffFrameSubscription(),
            ],
        };

        // Verbose so the host's ROI-failure line is written at all: it is diagnostic, and a plugin
        // author reads it while calibrating rather than during a normal run.
        var exit = await OcrxPluginHost
            .RunAsync(plugin, ["--pipe", pipeName, "--verbose"], Options(hostOutput, cts.Token))
            .WaitAsync(TestTimeout);

        await scan;
        await engine.StopAsync();

        Assert.False(cts.IsCancellationRequested, "timed out");
        return (exit, plugin, hostOutput, frameCount);
    }

    /// <summary>
    /// Polls a condition the host reaches on its own schedule. A fixed delay would either be flaky on
    /// a loaded box or slow on an idle one, and there is no event to await â€” the host raises its
    /// notifications into the plugin, which is what is being polled.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }

    /// <summary>Waits until the engine reports having scanned at least <paramref name="minSeq"/> frames.</summary>
    /// <remarks>
    /// A copy of the poll in <c>ProtocolHandshakeTests</c> rather than a shared helper: it is two
    /// lines, and widening that one's accessibility to reach it would be the larger change. It polls
    /// the engine directly rather than the plugin, because at the point of use there is no plugin.
    /// </remarks>
    private static async Task WaitForFrameSeqAsync(
        CaptureEngineService.CaptureEngineServiceClient grpc, ulong minSeq, CancellationToken ct)
    {
        while ((await grpc.GetStatusAsync(new StatusRequest(), cancellationToken: ct)).FrameSeq < minSeq)
            await Task.Delay(10, ct);
    }
}

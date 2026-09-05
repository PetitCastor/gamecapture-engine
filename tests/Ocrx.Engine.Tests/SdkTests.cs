using Ocrx.Contracts;
using Ocrx.Sdk;
using Xunit;

namespace Ocrx.Engine.Tests;

/// <summary>
/// The SDK against a real engine over a real pipe. Everything the SDK does is a translation â€”
/// subscriptions out, ticks in â€” and a translation layer is exactly the kind of code that passes
/// its own unit tests while disagreeing with the thing on the other side of the wire, so these
/// drive the in-proc engine host rather than a stub.
/// </summary>
public class SdkTests
{
    /// <summary>Generous: the session tests OCR the whole fixture corpus over the pipe.</summary>
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a plugin is willing to wait for an engine that is already up.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private static string NewPipeName() => $"sc-sdk-{Guid.NewGuid():N}";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task WaitForEngineAsync_AgainstRunningHost_ReturnsStatus()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        var status = await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        Assert.NotEmpty(status.EngineVersion);
        Assert.True(status.ReplayMode);

        // The scan loop was never started, so the wait must have succeeded on the RPC answering â€”
        // not on any frame having been produced. Read off the raw status: the frame sequence is
        // engine bookkeeping, deliberately not part of what EngineInfo tells a plugin.
        // The engine reports the cadence it actually scans at, so a plugin counting ticks does not
        // have to assume 500 ms (TASK-08).
        Assert.Equal(TimeSpan.FromMilliseconds(new EngineConfig().ScanIntervalMs), status.ScanInterval);
    }

    [Fact]
    public async Task WaitForEngineAsync_AgainstDeadPipe_ThrowsTimeout()
    {
        using var cts = new CancellationTokenSource(TestTimeout);

        // Nothing is listening: connecting to an absent pipe blocks rather than failing, which is
        // precisely why the wait bounds each attempt by its remaining budget instead of trusting
        // the first call to come back.
        using var client = new CaptureClient(NewPipeName());

        var timeout = TimeSpan.FromSeconds(1);
        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => client.WaitForEngineAsync(timeout, cts.Token));

        Assert.Contains("did not answer", ex.Message);
    }

    /// <summary>
    /// Ctrl+C during the wait. A plugin host shuts down on OperationCanceledException; if its own
    /// cancellation came back as RpcException(Cancelled) instead, the host would take a clean stop
    /// for an engine failure â€” and the deadline-bounded attempt is where nearly all of the wait is
    /// actually spent, so this is the likely place for it to land.
    /// </summary>
    [Fact]
    public async Task WaitForEngineAsync_WhenTheCallerCancels_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        using var client = new CaptureClient(NewPipeName());

        // Long budget against a pipe nobody serves: the wait is parked inside the RPC, not
        // between polls, when the token fires.
        var wait = client.WaitForEngineAsync(TimeSpan.FromMinutes(1), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    /// <summary>
    /// The shape every plugin will have: connect, subscribe a mixed ROI set, consume ticks until
    /// the engine says there are no more. The ending matters as much as the ticks â€” a plugin runs
    /// its finalisers off the stream completing, so a replay that finished without reaching the
    /// SDK would leave a tracker's last order uncommitted.
    /// </summary>
    // TreatWarningsAsErrors (Directory.Build.props) makes obsolete-member use a build failure, and
    // the tests from here down to the matching #restore use TickData's obsolete
    // Text/Ocr/Pixels/Error accessors on purpose: those members still ship, so their documented
    // behaviour â€” a failed region and an absent one both reading as "nothing", which is exactly why
    // they are deprecated â€” still has to be pinned. Migrating these call sites to the Try- forms
    // would not modernise the suite, it would delete the only coverage the obsolete surface has.
    // Scoped to this region rather than the whole file so an accidental obsolete call elsewhere in
    // SdkTests is still caught. Pragma and assertions come out together, with the members.
#pragma warning disable CS0618 // Type or member is obsolete

    [Fact]
    [Trait("Category", "Integration")]
    public async Task TrackAsync_OverReplayCorpus_YieldsEveryTickThenCompletes()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        var source = new ReplayFrameSource(EngineTestFixtures.ReplayDir);
        var frameCount = source.FrameCount;

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        // Started before anyone subscribes: the loop holds the corpus until a client is ready.
        var scan = engine.RunScanAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        var ticks = new List<TickData>();
        await using (var session = await client.TrackAsync("test",
            [EngineTestFixtures.PanelStateSubscription(), EngineTestFixtures.ToggleStripSubscription()],
            cts.Token))
        {
            // Completing normally IS the assertion about replay end reaching the SDK: if the
            // engine failed to complete the stream this loop would hang until the timeout.
            await foreach (var tick in session.Ticks(cts.Token))
                ticks.Add(tick);
        }

        await scan;

        Assert.NotEmpty(ticks);
        Assert.Equal(frameCount, ticks.Count);

        for (var i = 0; i < ticks.Count; i++)
        {
            var tick = ticks[i];
            Assert.Equal((ulong)(i + 1), tick.FrameSeq);
            Assert.True(tick.FrameWidth > 0 && tick.FrameHeight > 0);
            Assert.False(tick.Manual);
            Assert.Null(tick.Error("panel"));
            Assert.Null(tick.Error("toggle"));

            var pixels = tick.Pixels("toggle");
            Assert.NotNull(pixels);
            Assert.True(pixels.Width > 0 && pixels.Height > 0);

            // Sampling by frame coordinates is the whole reason frame_rect crosses the wire: get
            // the origin wrong and this clamps to an edge or indexes out of the buffer. A patch
            // taken inside the ROI must therefore land on real pixels, not on the clamped black
            // an empty sampler returns.
            var (b, g, r) = pixels.AveragePatch(pixels.FrameX + pixels.Width / 2,
                pixels.FrameY + pixels.Height / 2);
            Assert.True(b > 0 || g > 0 || r > 0);
        }

        // The panel ROI is a text region in every fixture frame; if it OCRs empty everywhere, the
        // geometry made it across the wire wrong.
        Assert.Contains(ticks, t => t.Text("panel").Length > 0);
    }

    /// <summary>
    /// Re-subscribing without reopening the stream. A tracker changes its ROI set when the UI it
    /// watches changes screens, and it must not have to drop and re-establish the session â€” the
    /// gap would cost it ticks.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task UpdateRoisAsync_MidStream_LaterTicksCarryOnlyTheNewSet()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        var source = new GatedFrameSource(EngineTestFixtures.ReplayDir);

        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            source, sink, verbose: false);
        await engine.StartAsync(cts.Token);

        var scan = engine.RunScanAsync(scanCts.Token);
        try
        {
            using var client = new CaptureClient(pipeName);
            await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

            await using var session = await client.TrackAsync("switcher",
                [EngineTestFixtures.PanelStateSubscription()], cts.Token);

            await using var ticks = session.Ticks(cts.Token).GetAsyncEnumerator(cts.Token);

            source.Release();
            Assert.True(await ticks.MoveNextAsync());

            var beforeUpdate = ticks.Current;
            Assert.NotNull(beforeUpdate.Ocr("panel"));
            Assert.Null(beforeUpdate.Pixels("toggle"));

            await session.UpdateRoisAsync([EngineTestFixtures.ToggleStripSubscription()]);

            // The update is applied by the engine's request pump, which runs independently of the
            // scan loop, so no fixed number of ticks is both quick and safe: a frame already in
            // flight still carries the old set, and on a loaded machine the pump may not have been
            // scheduled at all yet. Read until the new set shows up rather than guessing how long
            // that takes; the enumerator's token bounds the wait, so a set that never arrives
            // fails the test instead of looping forever.
            TickData afterUpdate;
            do
            {
                source.Release();
                Assert.True(await ticks.MoveNextAsync());
                afterUpdate = ticks.Current;
            }
            while (afterUpdate.Pixels("toggle") is null);

            // Absent, not merely empty: a full replacement that behaved as a merge would still
            // answer Text("panel") with real OCR, and Ocr/Error tell absence from failure.
            Assert.Null(afterUpdate.Ocr("panel"));
            Assert.Null(afterUpdate.Error("panel"));
            Assert.Equal(string.Empty, afterUpdate.Text("panel"));
        }
        finally
        {
            // Stop the loop before the host disposes the gated source out from under it.
            scanCts.Cancel();
            try { await scan; } catch (OperationCanceledException) { }
        }
    }


#pragma warning restore CS0618 // Type or member is obsolete

    /// <summary>
    /// Disposing a session twice, which is what an `await using` plus an explicit cleanup path
    /// produces â€” TrackAsync's own failure handler is one. Cleanup code that throws is worse than
    /// no cleanup code, because it masks whatever sent the plugin down that path.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisposeAsync_CalledTwice_IsQuiet()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        var session = await client.TrackAsync("disposer",
            [EngineTestFixtures.PanelStateSubscription()], cts.Token);

        await session.DisposeAsync();
        await session.DisposeAsync();

        // And the stream really is closed: a write after dispose is the caller's bug and must say
        // so, rather than throwing out of the semaphore the first dispose used to destroy.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => session.UpdateRoisAsync([EngineTestFixtures.ToggleStripSubscription()]));
    }

    // ---------- ReadRoi (TASK-08) ----------

    /// <summary>
    /// The calibration read: one region, against the frame the last tick saw, with no session at
    /// all. This is what makes "is my ROI constant right" answerable without adding the region to a
    /// plugin's subscription and restarting it.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadRoiAsync_AfterAFrameHasBeenScanned_ReadsTheRegion()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        // A subscriber is what makes the loop hand out frames, and the corpus is drained rather than
        // cut short: in replay the loop blocks on a client that stops reading. The read below then
        // answers from the frame the loop retained, which is the point â€” no capture of its own.
        await using (var session = await client.TrackAsync("reader",
            [EngineTestFixtures.PanelStateSubscription()], cts.Token))
        {
            var scan = engine.RunScanAsync(cts.Token);
            await foreach (var _ in session.Ticks(cts.Token)) { }
            await scan;
        }

        var ocr = await client.ReadRoiAsync(EngineTestFixtures.PanelStateSubscription(), cts.Token);

        Assert.NotNull(ocr);
        Assert.True(ocr.EffectiveScale > 0);

        // Frame-space geometry, so a caller can map a word back to a screen pixel exactly as it can
        // from a tick â€” the wrapper must not flatten that away.
        Assert.True(ocr.RoiWidth > 0 && ocr.RoiHeight > 0);
    }

    /// <summary>
    /// Null, not an exception: an engine that has not scanned yet is the ordinary case for a plugin
    /// that starts with the engine, and a calibration helper that threw on it would be unusable in
    /// the first seconds of every run.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadRoiAsync_BeforeAnyFrame_IsNull()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        // The scan loop was never started, so there is no retained frame to read against.
        Assert.Null(await client.ReadRoiAsync(EngineTestFixtures.PanelStateSubscription(), cts.Token));
    }

    /// <summary>
    /// A region the engine could not read is raised, never folded into the null that means "no
    /// frame yet": those two are the same "nothing came back" to a caller, and only one of them is
    /// the caller's own mistake.
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadRoiAsync_OfAnOffFrameRegion_Throws()
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        using var sink = new ConsoleSink();

        var pipeName = NewPipeName();
        await using var engine = EngineHost.Create(pipeName, new EngineConfig(), new OcrPipeline(),
            new ReplayFrameSource(EngineTestFixtures.ReplayDir), sink, verbose: false);
        await engine.StartAsync(cts.Token);

        using var client = new CaptureClient(pipeName);
        await client.WaitForEngineAsync(ConnectTimeout, cts.Token);

        await using (var session = await client.TrackAsync("reader",
            [EngineTestFixtures.PanelStateSubscription()], cts.Token))
        {
            var scan = engine.RunScanAsync(cts.Token);
            await foreach (var _ in session.Ticks(cts.Token)) { }
            await scan;
        }

        var ex = await Assert.ThrowsAsync<RoiResultException>(() => client.ReadRoiAsync(
            EngineTestFixtures.OffFrameSubscription(), cts.Token));

        Assert.True(ex.ReportedByEngine);
    }
}

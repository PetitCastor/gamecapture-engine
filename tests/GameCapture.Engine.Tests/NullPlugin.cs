using GameCapture.Sdk;

namespace GameCapture.Engine.Tests;

/// <summary>
/// A plugin that parses nothing and counts everything. Subscribes one small ROI because a
/// subscription is what makes the engine consider this client worth ticking; the reading itself is
/// never looked at.
/// </summary>
/// <remarks>
/// The point of it is to leave only the host in the frame. A test driving RefineryPlugin through the
/// host proves the pair works together and says nothing about which of them was responsible when it
/// stops working — and the host's contract (a tick per frame, one Connected per connect, an Ended
/// with the right reason) is exactly what a real plugin's parser noise would bury.
/// </remarks>
internal sealed class NullPlugin : IGameCapturePlugin
{
    private readonly Lock _gate = new();
    private readonly List<SessionEvent> _events = [];

    public NullPlugin(RoiErrorPolicy errorPolicy = RoiErrorPolicy.PassThrough)
        => ErrorPolicy = errorPolicy;

    public string Name { get; init; } = "null";

    public IReadOnlyList<RoiSubscription> Rois { get; init; } =
        [EngineTestFixtures.PanelStateSubscription()];

    public RoiErrorPolicy ErrorPolicy { get; }

    /// <summary>Ticks the host actually dispatched — the count an error policy changes.</summary>
    public int TickCount => Volatile.Read(ref _tickCount);
    private int _tickCount;

    /// <summary>
    /// Ticks that carried a result — success or per-ROI error — under this plugin's own first
    /// subscribed id.
    /// </summary>
    /// <remarks>
    /// The engine registers a client when its Track call opens and starts ticking it immediately,
    /// which is before its RoiSetUpdate has been read; such a tick is well formed and completely
    /// empty. <see cref="TickCount"/> counts those exactly like any other, so a test that wants to
    /// say "the engine answered what I subscribed" cannot say it with that number alone.
    /// <see cref="RoiStatus.NotSubscribed"/> is precisely the "no result under this id" case, so
    /// anything else means the subscription had been applied by the time the frame was read.
    /// </remarks>
    public int AnsweredTickCount => Volatile.Read(ref _answeredTickCount);
    private int _answeredTickCount;

    public int ManualTickCount => Volatile.Read(ref _manualTickCount);
    private int _manualTickCount;

    /// <summary>
    /// Set to make every dispatched tick throw. The host must log it and keep going: one
    /// unparseable frame out of thousands is a normal event, and a plugin that dies on it loses
    /// everything it had accumulated.
    /// </summary>
    public Exception? ThrowOnTick { get; init; }

    public IReadOnlyList<SessionEvent> Events
    {
        get { lock (_gate) return _events.ToArray(); }
    }

    public List<string> Summary { get; } = [];

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _tickCount);

        if (Rois.Count > 0 && ctx.Tick.Status(Rois[0].Id) != RoiStatus.NotSubscribed)
            Interlocked.Increment(ref _answeredTickCount);

        return ThrowOnTick is null ? Task.CompletedTask : Task.FromException(ThrowOnTick);
    }

    public Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        Interlocked.Increment(ref _manualTickCount);
        return OnTickAsync(ctx, ct);
    }

    public void OnSessionEvent(SessionEvent evt)
    {
        lock (_gate) _events.Add(evt);
    }

    public IEnumerable<string> SummaryLines() => Summary;

    /// <summary>Every event of one kind, for asserting on counts as well as presence.</summary>
    public IReadOnlyList<T> EventsOf<T>() where T : SessionEvent
        => Events.OfType<T>().ToArray();
}

/// <summary>
/// An <see cref="IPluginOutput"/> that keeps what the host wrote, so a test can assert on the
/// user-visible half of the host's behaviour without touching the real console.
/// </summary>
internal sealed class RecordingOutput : IPluginOutput
{
    private readonly Lock _gate = new();
    private readonly List<string> _lines = [];

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public string Text => string.Join(Environment.NewLine, Lines);

    public void WriteLine(string message = "")
    {
        lock (_gate) _lines.Add(message);
    }

    public void UpdateStatus(string statusText) { }
}

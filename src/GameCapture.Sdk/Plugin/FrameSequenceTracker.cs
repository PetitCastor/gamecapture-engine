namespace GameCapture.Sdk;

/// <summary>
/// Watches <see cref="TickData.FrameSeq"/> for frames this client never received.
/// </summary>
/// <remarks>
/// Its own type, and unit-tested as one, because every interesting case here is a boundary the
/// integration tests cannot reach on demand: the first tick of a session (nothing to compare
/// against), the first tick after a reconnect (the engine's counter kept running while nobody was
/// listening), and a frame sequence that goes backwards (a restarted engine counting from zero again).
/// Provoking those against a live engine means racing it.
/// </remarks>
internal sealed class FrameSequenceTracker
{
    private ulong? _lastFrameSequence;

    /// <summary>
    /// Records <paramref name="frameSequence"/> and reports the frame-sequence gap used to reach it.
    /// </summary>
    /// <returns>
    /// True when frames were missed, with <paramref name="frameSequenceGap"/> set to how many.
    /// False — gap 0 — for a contiguous tick, for the first tick observed, and for a frame
    /// sequence that did not advance or ran backwards.
    /// </returns>
    /// <remarks>
    /// A frame sequence going backwards is a fresh engine, not a frame-sequence gap: the counter is per engine process,
    /// so a restart mid-run replays low numbers the client has already seen. Reporting that as a
    /// negative gap would be nonsense and reporting it as a huge one — the unsigned subtraction, if
    /// nobody thought about it — would be worse.
    /// </remarks>
    public bool TryObserve(ulong frameSequence, out ulong frameSequenceGap)
    {
        frameSequenceGap = 0;
        var previous = _lastFrameSequence;
        _lastFrameSequence = frameSequence;

        if (previous is not { } last || frameSequence <= last)
            return false;

        frameSequenceGap = frameSequence - last - 1;
        return frameSequenceGap > 0;
    }

    /// <summary>
    /// Forgets the frame sequence seen so far, so the next tick counts as a first observation.
    /// </summary>
    /// <remarks>
    /// Called on reconnect. The engine keeps scanning while a client is away, so the first tick of
    /// the new session is legitimately far ahead of the last of the old one — reporting that as
    /// dropped ticks would fire the event on every single reconnect, which is exactly the noise
    /// that teaches a plugin author to ignore it.
    /// </remarks>
    public void Reset() => _lastFrameSequence = null;
}

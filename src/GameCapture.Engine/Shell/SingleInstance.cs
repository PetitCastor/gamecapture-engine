namespace GameCapture.Engine.Shell;

/// <summary>
/// Enforces one running engine per Windows logon session. A named <see cref="Mutex"/> answers "is an
/// instance already running"; a named <see cref="EventWaitHandle"/> alongside it lets a second launch
/// hand off to the first instead of just failing — the second process signals the event and exits, the
/// first process's background wait fires <see cref="Signaled"/> so its caller can bring the existing
/// window forward. Preferred over <c>RegisterWindowMessage</c>/<c>SetForegroundWindow</c> broadcasting:
/// less code, and no window handle to discover from a process that has not created one yet.
/// </summary>
/// <remarks>
/// Both kernel objects live in the <c>Local\</c> namespace, not <c>Global\</c> — the engine is a
/// per-user process (the same reasoning already recorded for the named pipe in
/// <see cref="Grpc.GrpcHost"/>), so a second logon session on the same machine must be able to run its
/// own instance rather than being turned away by another user's.
/// </remarks>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _signalEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle signalEvent)
    {
        _mutex = mutex;
        _signalEvent = signalEvent;
    }

    /// <summary>
    /// Raised on a thread-pool thread when a second launch signals this, the first, instance. The
    /// handler is responsible for marshaling to the UI thread before touching any window — this event
    /// never fires on it.
    /// </summary>
    public event Action? Signaled;

    /// <summary>
    /// Attempts to become the one running instance for <paramref name="scope"/>. Returns the instance
    /// to hold for the rest of the process's lifetime when this is the first launch to claim it, or
    /// <c>null</c> when another instance already holds it — after signalling that instance so it can
    /// come forward. A <c>null</c> result is the expected "already running" outcome, not an error: the
    /// caller should exit immediately, with no console noise.
    /// </summary>
    /// <param name="scope">Identifies the mutex/event pair. Defaults to the engine's production scope;
    /// tests pass a unique value so parallel runs cannot collide with each other or with a real engine
    /// that happens to be running on the same machine.</param>
    public static SingleInstance? Acquire(string scope = "GameCapture.Engine")
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName(scope), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            SignalRunningInstance(scope);
            return null;
        }

        var signalEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(scope));
        var instance = new SingleInstance(mutex, signalEvent);
        instance._registeredWait = ThreadPool.RegisterWaitForSingleObject(
            signalEvent,
            (state, _) => ((SingleInstance)state!).Signaled?.Invoke(),
            instance,
            Timeout.Infinite,
            executeOnlyOnce: false);
        return instance;
    }

    private static void SignalRunningInstance(string scope)
    {
        try
        {
            using var existing = EventWaitHandle.OpenExisting(EventName(scope));
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // Lost a race with the first instance between its mutex creation and its event creation —
            // vanishingly unlikely, and harmless either way: that instance is already coming up, and
            // this launch is exiting regardless.
        }
    }

    private static string MutexName(string scope) => $@"Local\{scope}.SingleInstance.Mutex";

    private static string EventName(string scope) => $@"Local\{scope}.SingleInstance.Event";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _registeredWait?.Unregister(null);
        _signalEvent.Dispose();

        // No ReleaseMutex: this handle is held for the process's whole lifetime by design, and
        // Program.cs's top-level async Main resumes each await on whatever thread-pool thread the
        // continuation lands on — not the OS thread that acquired the mutex, which ReleaseMutex
        // requires. Closing the last handle (Dispose, below) is enough for Windows to release
        // ownership; abandoning it this way is exactly the normal teardown path for a mutex meant to
        // outlive any single thread.
        _mutex.Dispose();
    }
}

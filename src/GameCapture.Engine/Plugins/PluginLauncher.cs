using System.Diagnostics;

namespace GameCapture.Engine.Plugins;

/// <summary>
/// Starts and stops installed plugins as child processes of the engine, keyed by catalog id.
/// </summary>
/// <remarks>
/// Process edge, excluded from the coverage gate. Deliberately not a supervisor: nothing starts on
/// its own and a plugin that exits stays exited — the engine's contract with a plugin is the gRPC
/// connection, not its lifetime, and quietly resurrecting a plugin that crashed on every tick would
/// be worse than showing it stopped. What it does guarantee is the other direction: a plugin this
/// engine started never outlives it, so closing the tray does not leave invisible console processes
/// reconnecting to the next engine.
/// </remarks>
public sealed class PluginLauncher : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Process> _running = new(StringComparer.Ordinal);

    /// <summary>Raised after the running set changes.</summary>
    public event Action? Changed;

    /// <summary>Ids with a live child process, after pruning any that have exited.</summary>
    public IReadOnlyCollection<string> RunningIds
    {
        get
        {
            List<string> ids;
            bool pruned;
            lock (_gate)
            {
                pruned = Prune();
                ids = _running.Keys.ToList();
            }

            if (pruned)
                Changed?.Invoke();
            return ids;
        }
    }

    /// <summary>Whether this engine has a live child process for <paramref name="id"/>.</summary>
    public bool IsRunning(string id)
    {
        bool result;
        bool pruned;
        lock (_gate)
        {
            pruned = Prune();
            result = _running.ContainsKey(id);
        }

        if (pruned)
            Changed?.Invoke();
        return result;
    }

    /// <summary>
    /// Launches a plugin. No-op when one is already running for the same id.
    /// </summary>
    /// <exception cref="FileNotFoundException">The recorded executable is gone — the folder was
    /// deleted or moved behind the engine's back.</exception>
    public void Start(InstalledPlugin plugin)
    {
        var pruned = false;
        var started = false;
        try
        {
            lock (_gate)
            {
                pruned = Prune();
                if (_running.ContainsKey(plugin.Id))
                    return;

                if (!File.Exists(plugin.ExecutablePath))
                    throw new FileNotFoundException($"{plugin.Name} is not where it was installed. Reinstall it.", plugin.ExecutablePath);

                var startInfo = new ProcessStartInfo
                {
                    FileName = plugin.ExecutablePath,
                    // The SDK reads a plugin's seeded config relative to its own base directory, so the
                    // working directory has to be the plugin folder rather than the engine's.
                    WorkingDirectory = Path.GetDirectoryName(plugin.ExecutablePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                var process = Process.Start(startInfo)
                              ?? throw new InvalidOperationException($"{plugin.Name} could not be started.");
                _running[plugin.Id] = process;
                started = true;
            }
        }
        finally
        {
            // Raised after the lock is released: this event reaches ControlApiEventHub, which
            // broadcasts to WebSocket clients — a slow or stuck client must never add latency to
            // every Start/Stop/Prune call across the process by holding this up under _gate.
            if (pruned || started)
                Changed?.Invoke();
        }
    }

    /// <summary>Stops a plugin if this engine started it. No-op otherwise.</summary>
    public void Stop(string id)
    {
        bool stopped;
        lock (_gate)
        {
            stopped = _running.Remove(id, out var process);
            if (stopped)
                Terminate(process!);
        }

        if (stopped)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        bool changed;
        lock (_gate)
        {
            foreach (var process in _running.Values)
                Terminate(process);

            changed = _running.Count > 0;
            _running.Clear();
        }

        if (changed)
            Changed?.Invoke();
    }

    // A plugin is a windowless console process with no shutdown channel of its own — the gRPC stream
    // carries ticks, not commands — so there is nothing gentler than a kill to ask of it. It holds no
    // engine-side state, and its own sinks flush per record, so the cost of that is one dropped tick.
    private static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
        }
    }

    // Called under the lock. A plugin that exited on its own must stop counting as running, or its
    // row would offer Stop forever and never offer Update. Returns whether anything was pruned; the
    // caller raises Changed once, after releasing the lock.
    private bool Prune()
    {
        var pruned = false;
        foreach (var (id, process) in _running.ToList())
        {
            if (!process.HasExited)
                continue;

            process.Dispose();
            _running.Remove(id);
            pruned = true;
        }

        return pruned;
    }
}

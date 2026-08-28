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

    /// <summary>Ids with a live child process, after pruning any that have exited.</summary>
    public IReadOnlyCollection<string> RunningIds
    {
        get
        {
            lock (_gate)
            {
                Prune();
                return _running.Keys.ToList();
            }
        }
    }

    /// <summary>Whether this engine has a live child process for <paramref name="id"/>.</summary>
    public bool IsRunning(string id)
    {
        lock (_gate)
        {
            Prune();
            return _running.ContainsKey(id);
        }
    }

    /// <summary>
    /// Launches a plugin. No-op when one is already running for the same id.
    /// </summary>
    /// <exception cref="FileNotFoundException">The recorded executable is gone — the folder was
    /// deleted or moved behind the engine's back.</exception>
    public void Start(InstalledPlugin plugin)
    {
        lock (_gate)
        {
            Prune();
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
        }
    }

    /// <summary>Stops a plugin if this engine started it. No-op otherwise.</summary>
    public void Stop(string id)
    {
        lock (_gate)
        {
            if (!_running.Remove(id, out var process))
                return;

            Terminate(process);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var process in _running.Values)
                Terminate(process);

            _running.Clear();
        }
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
    // row would offer Stop forever and never offer Update.
    private void Prune()
    {
        foreach (var (id, process) in _running.ToList())
        {
            if (!process.HasExited)
                continue;

            process.Dispose();
            _running.Remove(id);
        }
    }
}

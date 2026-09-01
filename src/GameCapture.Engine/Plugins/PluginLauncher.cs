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

    /// <summary>
    /// Where each plugin's console output is kept, or null to leave the child's streams inherited.
    /// </summary>
    /// <remarks>
    /// An init property rather than a constructor parameter, mirroring
    /// <see cref="PluginServices.RoiOverlays"/>: capture is optional, and a test that only cares about
    /// the running set should not have to supply a store to get one.
    /// </remarks>
    public PluginLogStore? Logs { get; init; }

    /// <summary>Raised after the running set changes.</summary>
    public event Action? Changed;

    /// <summary>Ids with a live child process, after pruning any that have exited.</summary>
    public IReadOnlyCollection<string> RunningIds
    {
        get
        {
            List<string> ids;
            List<Process> exited;
            lock (_gate)
            {
                exited = Prune();
                ids = _running.Keys.ToList();
            }

            ReleaseAll(exited);
            if (exited.Count > 0)
                Changed?.Invoke();
            return ids;
        }
    }

    /// <summary>Whether this engine has a live child process for <paramref name="id"/>.</summary>
    public bool IsRunning(string id)
    {
        bool result;
        List<Process> exited;
        lock (_gate)
        {
            exited = Prune();
            result = _running.ContainsKey(id);
        }

        ReleaseAll(exited);
        if (exited.Count > 0)
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
        var exited = new List<Process>();
        var started = false;
        var opened = false;
        try
        {
            lock (_gate)
            {
                exited = Prune();
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

                // A buffer is opened before the process exists, and reused if this plugin has run
                // before, so a relaunch appends to the history rather than erasing it.
                var buffer = Logs?.Open(plugin.Id);
                opened = buffer is not null;
                if (buffer is not null)
                    PluginProcessCapture.Configure(startInfo);

                var process = new Process { StartInfo = startInfo };
                try
                {
                    // Handlers first, then Start, then the reads. Attaching after Start would lose
                    // whatever the child wrote in its first instants — which on the path this feature
                    // exists for, a plugin that dies during startup, is the entire message.
                    // BeginOutputReadLine cannot be called before Start at all. Both merely arm an
                    // async read, so neither blocks the gate.
                    if (buffer is not null)
                        PluginProcessCapture.Attach(process, buffer);

                    if (!process.Start())
                        throw new InvalidOperationException($"{plugin.Name} could not be started.");

                    if (buffer is not null)
                    {
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();
                        buffer.Append(PluginLogStream.Engine, $"-- started {plugin.Name} (pid {process.Id}) --");
                    }
                }
                catch (Exception ex)
                {
                    // File.Exists says nothing about whether the file can actually be executed, so a
                    // corrupt download or a blocked binary throws here rather than returning false.
                    // The instance is ours until it lands in _running, and nothing else can reach it
                    // afterwards, so this is the only place it can be let go of.
                    buffer?.Append(PluginLogStream.Engine, $"-- failed to start: {ex.Message} --");
                    Release(process);
                    throw;
                }

                _running[plugin.Id] = process;
                started = true;
            }
        }
        finally
        {
            ReleaseAll(exited);

            // Raised after the lock is released: this event reaches ControlApiEventHub, which
            // broadcasts to WebSocket clients — a slow or stuck client must never add latency to
            // every Start/Stop/Prune call across the process by holding this up under _gate.
            //
            // A launch that opened a buffer and then failed still counts as a change: HasLogs has
            // flipped, and the failure notice in that buffer is the only account of what went wrong.
            // Without this the row would keep its stale shape — no Show logs button — until some
            // unrelated plugin happened to start or stop.
            if (exited.Count > 0 || started || opened)
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
            {
                Logs?.Append(id, PluginLogStream.Engine, "-- stopped by the engine --");
                Terminate(process!);
            }
        }

        if (stopped)
            Changed?.Invoke();
    }

    public void Dispose()
    {
        bool changed;
        lock (_gate)
        {
            foreach (var (id, process) in _running)
            {
                Logs?.Append(id, PluginLogStream.Engine, "-- stopped by the engine --");
                Terminate(process);
            }

            changed = _running.Count > 0;
            _running.Clear();
        }

        // The buffers are not cleared: the launcher owns processes, not the record of what they said.
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
            Release(process);
        }
    }

    private static void ReleaseAll(List<Process> processes)
    {
        foreach (var process in processes)
            Release(process);
    }

    // Disposing a process abandons any async read still in flight, and the timeout overload of
    // WaitForExit does not wait for those readers to reach end of stream — only the parameterless one
    // does. So the last lines a crashing plugin wrote can be lost between its exit and our Dispose.
    // Waiting for that on a pool thread, holding no lock, costs nothing and closes the gap; the only
    // way the wait outlives the engine is a grandchild holding the stdout handle, which no SDK plugin
    // has and which Kill(entireProcessTree) covers on the Stop path anyway.
    private static void Release(Process process)
        => _ = Task.Run(() =>
        {
            try
            {
                process.WaitForExit();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Never started, or already reaped.
            }
            finally
            {
                process.Dispose();
            }
        });

    // Called under the lock. A plugin that exited on its own must stop counting as running, or its
    // row would offer Stop forever and never offer Update. Returns the processes that went, still
    // undisposed: the caller releases them after dropping the lock, and raises Changed once.
    private List<Process> Prune()
    {
        var exited = new List<Process>();
        foreach (var (id, process) in _running.ToList())
        {
            if (!process.HasExited)
                continue;

            // The exit code is the other half of a crash report, and this is the last moment it can be
            // read — which is also why nothing here needs an Exited event to go with the polling.
            Logs?.Append(id, PluginLogStream.Engine, $"-- exited with code {ExitCodeOf(process)} --");
            _running.Remove(id);
            exited.Add(process);
        }

        return exited;
    }

    private static string ExitCodeOf(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}

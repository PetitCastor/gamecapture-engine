using Ocrx.Engine.Plugins;
using Xunit;

namespace Ocrx.Engine.Tests.Hosting;

/// <summary>
/// Pins the TASK-UI-04 section 7 inversion: <see cref="EngineDesktopLifetime.BuildInteractiveControls"/>
/// (the half of <see cref="EngineDesktopLifetime.Start"/> that needs no WinForms UI thread) must build
/// <see cref="TrayControls"/> and <see cref="PluginServices"/> for every interactive run regardless of
/// <c>trayEnabled</c> — the bug found while building TASK-UI-03, where an engine with the tray turned
/// off used to come up with no window, no settings and no plugin management. <c>Start</c> itself is
/// never called here: it goes on to build the tray's real STA thread and window, which
/// <see cref="ControlApiHarness"/> also deliberately avoids (see its own remarks).
/// </summary>
public sealed class EngineDesktopLifetimeTrayEnabledTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildInteractiveControls_BuildsControlsAndPublishesThemToTheControlApi_RegardlessOfTrayEnabled(bool trayEnabled)
    {
        await using var fixture = await LifetimeFixture.StartAsync(trayEnabled);

        var controls = fixture.Lifetime.BuildInteractiveControls();

        Assert.NotNull(controls.Plugins);
        Assert.Same(controls, fixture.Engine.ControlApi!.Controls);
        Assert.Equal(trayEnabled, controls.Settings.TrayEnabled);
    }

    [Fact]
    public async Task Start_NonInteractiveSource_NeverBuildsControls()
    {
        // The one early return TASK-UI-04 must keep: a headless replay/video run has nobody to click
        // anything, so Start() must still bail before BuildInteractiveControls (and before touching a
        // UI thread) for a non-interactive source.
        await using var fixture = await LifetimeFixture.StartAsync(trayEnabled: true, interactive: false);

        fixture.Lifetime.Start();

        Assert.Null(fixture.Engine.ControlApi);
    }

    /// <summary>Minimal engine + lifetime bootstrap, deliberately lighter than
    /// <see cref="ControlApiHarness"/>: this suite only needs <see cref="EngineDesktopLifetime.BuildInteractiveControls"/>
    /// in isolation, never a live HTTP surface.</summary>
    private sealed class LifetimeFixture : IAsyncDisposable
    {
        private readonly EngineHost _engine;
        private readonly IFrameSource _source;
        private readonly ConsoleSink _sink;
        private readonly string _tempDir;

        public EngineDesktopLifetime Lifetime { get; }
        public EngineHost Engine => _engine;

        private LifetimeFixture(EngineHost engine, EngineDesktopLifetime lifetime, IFrameSource source, ConsoleSink sink, string tempDir)
        {
            _engine = engine;
            Lifetime = lifetime;
            _source = source;
            _sink = sink;
            _tempDir = tempDir;
        }

        public static async Task<LifetimeFixture> StartAsync(bool trayEnabled, bool interactive = true)
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "gc-desktoplifetime-tests", Guid.NewGuid().ToString("N"));
            var configPath = Path.Combine(tempDir, "engine-config.json");
            var config = EngineConfig.Load(configPath); // creates tempDir and a default file
            config.TrayEnabled = trayEnabled;

            var pipeName = $"sc-desktoplifetime-{Guid.NewGuid():N}";
            var sink = new ConsoleSink();

            var source = new GatedFrameSource(EngineTestFixtures.ReplayDir, isReplay: !interactive);
            var sourceSelection = new FrameSourceSelection(source, "test", ["Monitor 1"], CurrentMonitorIndex: 0);

            var engine = EngineHost.Create(pipeName, config, new OcrPipeline(), source, sink, verbose: false, sourceSelection);
            await engine.StartAsync();

            var lifetime = EngineDesktopLifetime.Create(engine, config, configPath, [], sourceSelection, saveFrames: false, sink);

            return new LifetimeFixture(engine, lifetime, source, sink, tempDir);
        }

        public async ValueTask DisposeAsync()
        {
            Lifetime.Dispose();
            _source.Dispose();
            await _engine.DisposeAsync();
            _sink.Dispose();

            try
            {
                if (Directory.Exists(_tempDir))
                    Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leftover temp folder costs disk space, not correctness.
            }
        }
    }
}

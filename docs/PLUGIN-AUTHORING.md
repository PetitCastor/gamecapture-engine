# Writing a tracker plugin

A plugin is a console process that says what regions of the screen it cares about and what to do
when a tick carrying them arrives. It never captures, never runs OCR, and never speaks gRPC:
`OcrxPluginHost` owns connecting, subscribing, reconnecting, cancellation, and the end-of-run
summary, and hands the plugin one `TickContext` at a time.

This is the golden path, in order. Every code block below is taken from a project that was built
and tested outside this repository against the real SDK — see [§8](#8-cold-start-checklist).

- [1. Prerequisites](#1-prerequisites)
- [2. Creating the project](#2-creating-the-project)
- [3. Anatomy of a plugin](#3-anatomy-of-a-plugin)
- [Outputs: sinks](#outputs-sinks)
- [4. ROIs: space, scale, calibration](#4-rois-space-scale-calibration)
- [5. Error handling](#5-error-handling)
- [6. Session events](#6-session-events)
- [7. Testing](#7-testing)
- [8. Cold-start checklist](#8-cold-start-checklist)
- [9. Config, CLI, and compatibility](#9-config-cli-and-compatibility)
- [Appendix: manual project setup](#appendix-manual-project-setup)

## 1. Prerequisites

- **.NET 10 SDK.** Every project in the system targets `net10.0`; only the engine adds the Windows
  flavor (`net10.0-windows10.0.22621.0`), and a plugin must never need it — see the TFM constraint
  in [`ARCHITECTURE.md`](ARCHITECTURE.md#frozen-constraints).
- **An engine to talk to.** Either a release zip
  ([Releases](https://github.com/PetitCastor/ocrx-engine/releases) ships
  `Ocrx.Engine-vX.Y.Z-win-x64.zip`, a self-contained exe) or a clone built with
  `dotnet build OcrxEngine.slnx`. Running the engine live needs Windows 10/11 with an OCR
  language pack installed; replaying a corpus needs the same, but no game. Note where the exe lands
  — parity tests need `OCRX_ENGINE_PATH` pointed at it ([§7](#7-testing)).
- **The packages, from nuget.org.** `Ocrx.Sdk`, `Ocrx.Contracts`, and
  `Ocrx.Sdk.Testing` restore like any other dependency; the opt-in
  `Ocrx.Sdk.Overlay` package is only needed for a desktop overlay. The template
  ([§2](#2-creating-the-project)) already references all three by package ID. No clone of this
  repository is involved in writing a plugin — clone it only to work on the engine itself, or to
  test against an unreleased SDK ([§2](#2-creating-the-project)).

Writing and unit-testing a plugin needs none of the above beyond the SDK — no Windows OCR, no game,
no engine process. That is the point of the plain-`net10.0` boundary.

## 2. Creating the project

```powershell
dotnet new install Ocrx.Plugin.Template
dotnet new ocrx-plugin -n MyPlugin
```

This scaffolds the whole project: `MyPlugin.csproj`, `Program.cs`, `Rois.cs`, `MyPlugin.cs` (the
class to rename and fill in — [§3](#3-anatomy-of-a-plugin) picks up from here), `config.json`, a
`tests/` project wired against `Ocrx.Sdk.Testing` ([§7](#7-testing)), and a CI workflow stub.
`dotnet new ocrx-plugin -h` lists every symbol, including `--SdkVersion` for pinning a
specific `Ocrx.Sdk`/`.Contracts`/`.Sdk.Testing` version.

**Testing against an unreleased SDK** (an engine change not yet published) is the one case that
needs a clone: pack the five projects into a local feed, add that feed as a source scoped to the new
project (not the machine-wide config `dotnet nuget add source` mutates without `--configfile`), and
pin the instantiated project at the packed prerelease with `--SdkVersion`:

```powershell
dotnet pack src/Ocrx.Contracts -c Release -o feed
dotnet pack src/Ocrx.Sdk -c Release -o feed
dotnet pack src/Ocrx.Sdk.Testing -c Release -o feed
dotnet pack src/Ocrx.Sdk.Overlay -c Release -o feed
dotnet pack templates/Ocrx.Plugin.Template.csproj -c Release -o feed

dotnet new install feed/Ocrx.Plugin.Template.*.nupkg
dotnet new ocrx-plugin -n MyPlugin --SdkVersion <version from feed/Ocrx.Sdk.*.nupkg>

dotnet new nugetconfig -o MyPlugin
dotnet nuget add source <full path to feed> --name local --configfile MyPlugin/nuget.config
```

`.github/workflows/ci.yml`'s `template-guard` job runs the Contracts/SDK/SDK.Testing/template portion
of this recipe on every PR (the anti-rot guard for the template itself: instantiate, build, test).
It deliberately does not pack the optional overlay because the generated template does not reference
it; pack the overlay as shown above when testing it from a local feed. Read the workflow for the
working detail, including the `Ocrx.Sdk.Testing` name collision filtered from the
`Ocrx.Sdk.*.nupkg` glob.

Setting the same project up by hand, without the template, is
[Appendix: manual project setup](#appendix-manual-project-setup).

## 3. Anatomy of a plugin

`IOcrxPlugin` (`src/Ocrx.Sdk/Plugin/IOcrxPlugin.cs`) has three required members — `Name`,
`Rois`, `OnTickAsync` — and four with defaults. The example below reads one text region, remembers
the last value, and emits a record whenever it changes.

The ROI set first, in its own file. It is static for the life of the process because the host reads
it once per connect and sends it as the initial subscription: per-tick atomicity means there is no
mid-tick round-trip that could add a region later.

```csharp
using Ocrx.Contracts;
using Ocrx.Sdk;

namespace MyPlugin;

/// <summary>
/// The regions this plugin subscribes, in reference space (2560x1440).
/// </summary>
public static class Rois
{
    /// <summary>The panel line the counter lives on. Scale is the OCR upscale factor —
    /// small text needs 2-4; 0 means "engine default".</summary>
    public static readonly RoiSubscription Counter =
        new("counter", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    /// <summary>A field, not <c>=> [Counter]</c>: the set never changes, and an
    /// expression-bodied property would build a fresh array on every read.</summary>
    public static readonly IReadOnlyList<RoiSubscription> All = [Counter];
}
```

The plugin itself:

```csharp
using Ocrx.Sdk;

namespace MyPlugin;

/// <summary>
/// Watches one region for a counter and emits a record every time the value changes.
/// </summary>
public sealed class CounterPlugin : IOcrxPlugin
{
    private string? _last;

    /// <summary>The client name on the Track stream and the tag on every record emitted.</summary>
    public string Name => "counter";

    public IReadOnlyList<RoiSubscription> Rois => MyPlugin.Rois.All;

    /// <summary>Default. The host skips any tick in which a subscribed region failed, so
    /// nothing below ever reads a degraded value.</summary>
    public RoiErrorPolicy ErrorPolicy => RoiErrorPolicy.AbortTick;

    public Task OnTickAsync(TickContext ctx, CancellationToken ct)
    {
        // TryGetText, not Text: a failed region and a genuinely blank panel both answer "",
        // and only the bool tells them apart.
        if (!ctx.Tick.TryGetText(MyPlugin.Rois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0 || value == _last)
            return Task.CompletedTask;

        _last = value;

        // The tick's own timestamp, not DateTime.Now: the engine buffers a few ticks per
        // client, so processing time can trail the frame it describes.
        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Auto, value));
        return Task.CompletedTask;
    }

    /// <summary>The hotkey means "capture what is on screen right now" here, so the current
    /// reading is emitted whether or not it changed.</summary>
    public Task OnManualTickAsync(TickContext ctx, CancellationToken ct)
    {
        if (!ctx.Tick.TryGetText(MyPlugin.Rois.Counter.Id, out var text))
            return Task.CompletedTask;

        var value = text.Trim();
        if (value.Length == 0)
            return Task.CompletedTask;

        // Advance the same state the auto path keeps. Without this, a press on a value that
        // has not been seen yet emits it as Manual and the very next tick emits it again as
        // Auto — one screen, two records.
        _last = value;

        ctx.Services.Emit(new CaptureRecord(ctx.Tick.Timestamp, Name, TriggerKind.Manual, value));
        return Task.CompletedTask;
    }

    /// <summary>Frames this plugin never saw. A tracker watching for an edge can miss it
    /// across a gap, so the next reading is re-reported as a fresh sighting rather than
    /// assumed to be the successor of the last one. A reconnect is NOT in here: the host
    /// deliberately keeps plugin state across one — see §6.</summary>
    public void OnSessionEvent(SessionEvent evt)
    {
        if (evt is SessionEvent.TicksDropped)
            _last = null;
    }

    public IEnumerable<string> SummaryLines() => [$"  last counter: {_last ?? "none"}"];
}
```

Members, and what each one is for:

| Member | Required | Notes |
| --- | --- | --- |
| `Name` | yes | Client name on the Track stream (what the engine lists in `GetStatus`, and what a user sees when two plugins share an engine) *and* the `CaptureRecord.Plugin` tag on everything emitted. |
| `Rois` | yes | Complete before the first tick; see [§4](#4-rois-space-scale-calibration). |
| `OnTickAsync` | yes | One scanned frame. Called sequentially — the host never overlaps two ticks — so plugin state needs no locking. Throwing does **not** end the run: the host logs it and delivers the next tick, because one unparseable frame out of thousands is normal. |
| `ErrorPolicy` | no (`AbortTick`) | See [§5](#5-error-handling). |
| `OnManualTickAsync` | no (→ `OnTickAsync`) | The tick on which the engine hotkey fired. Override when the hotkey means something other than "the normal capture, forced". |
| `OnSessionEvent` | no (no-op) | Connected / Reconnecting / TicksDropped / Ended. Runs on the host's loop — must not block. See [§6](#6-session-events). |
| `SummaryLines` | no (empty) | Extra lines printed under the host's own end-of-run summary. |

What the plugin gets back, through `ctx.Services` (`IPluginServices`):

- `Emit(CaptureRecord)` — records one captured event. The host both keeps it for the summary and
  prints it, so a plugin must not print the same thing itself.
- `Log` / `LogVerbose` — user-visible lines; `LogVerbose` is a no-op unless the run was started with
  `--verbose`, so it is safe to call on every tick.
- `DumpFrameAsync(roi, prefix, ct)` / `ReadRoiAsync(roi, ct)` — calibration aids, [§4](#4-rois-space-scale-calibration).
- `Engine` — an `EngineInfo`: version, negotiated protocol, frame size, OCR language, connected
  clients, `ScanInterval`, and `ReplayMode`. **A plugin that writes anywhere persistent must branch
  on `ReplayMode`** — a corpus run must not append to a real ledger.

## Outputs: sinks

`IPluginOutput` remains the host's text console: use `Log` and `LogVerbose` for lines a person
running the plugin should read. A sink is different: it receives `CaptureRecord` values after a
plugin calls `Emit` or `EmitCleared`, off the tick thread, for persistence, integration, or display.

Every record has a timestamp, plugin name, trigger, and `RawText`. Its `Kind` is `Observation` by
default; `Cleared` says the tracked value disappeared and deliberately carries no payload. Optional
`Fields` are named strings for structured sinks: JSON and HTTP add them as properties, CSV writes
only the configured column names, and an overlay template can interpolate them as `{fieldName}`.

Add an `outputs` array to the plugin's `config.json`. Relative file paths resolve from the directory
that contains that config file, not from the shell's working directory:

```json
{
  "pipeName": "Ocrx.Engine",
  "saveDebugFrames": false,
  "outputs": [
    {
      "type": "json",
      "path": "captures/records.jsonl",
      "dedupeOnChange": true,
      "recordClears": true
    },
    {
      "type": "csv",
      "path": "captures/records.csv",
      "columns": ["mission", "status"],
      "dedupeOnChange": false
    },
    {
      "type": "http",
      "url": "https://example.invalid/ocrx/records",
      "timeoutSeconds": 5
    },
    {
      "type": "overlay",
      "overlay": {
        "offsetY": 36,
        "template": "{mission}: {status}",
        "lingerMs": 5000,
        "foregroundColor": "#FFFFFF",
        "backgroundColor": "#111827"
      }
    }
  ]
}
```

The built-in `json` sink appends JSON Lines, `csv` writes a header followed by CSV rows, and `http`
POSTs one JSON object per record. `dedupeOnChange` defaults to `true` for those three sinks;
`recordClears` defaults to `false`. Their replay guarantee is strict: while `Engine.ReplayMode` is
true, they do not create files or make HTTP requests, so corpus runs cannot touch a real ledger or
endpoint.

The `overlay` sink is supplied by the separate, opt-in `Ocrx.Sdk.Overlay` package. Reference
that package and register `new OverlaySinkFactory()` through `PluginHostOptions.OverlayFactory`; an
unregistered overlay entry is a no-op, preserving portability for plugins that do not reference it.
Its `overlay` keys include `offsetX`/`offsetY`, `width`/`height`, colours, `template`, and `lingerMs`;
use numeric `anchor` (`0` for the default top-centre, `1` for custom `x`/`y`) when positioning must
be explicit. It is a topmost, click-through, no-activate Windows window: it never inspects the game
process and never requires elevation. A `Cleared` record hides it; `lingerMs: 0` disables auto-hide.

Unlike the file and HTTP sinks, the overlay receives every observation and clear so the on-screen
state stays current; it is intentionally not change-deduplicated.

## Your console output, seen from the engine

When the engine launches your plugin, it redirects the child's stdout and stderr and keeps them in a
bounded in-memory ring, which the main window shows behind **Show logs** on the plugin's row. You do
not opt into this and there is nothing to call: keep writing ordinary console text.

What follows from that:

- `Console.IsOutputRedirected` is true, so `ConsoleSink`'s live status row disables itself and
  `UpdateStatus` is dropped. That is by design — never put anything load-bearing there. `Log` and
  `LogVerbose` are captured normally.
- `Console.Error` is captured too and rendered in the danger colour, which is where the host's usage
  and `invalid output configuration:` failures already go.
- Prefer one `WriteLine` per logical line. A multi-line write is split on newlines by the engine, so
  it still reads correctly, but the line budget counts the result.
- Only the last **2,000 lines** (or 1 MB, whichever comes first) are retained per plugin, and a line
  longer than 2,000 characters is truncated with an ellipsis. The panel says so when it has discarded
  anything.
- **Nothing is persisted.** The buffer lives in the engine's memory, survives your plugin's exit so a
  startup crash is still readable, and is gone when the plugin is uninstalled or the engine closes.
  This is a diagnostic view, not an audit log — anything that has to survive belongs in a record sink.
- Treat your output as **ASCII**. A redirected .NET console encodes with the console code page and
  best-fit-maps what it cannot represent (an em dash arrives as `-`), so non-ASCII is already lossy on
  the way out of your process, before the engine reads a byte.

## 4. ROIs: space, scale, calibration

**Reference space is 2560x1440, always.** A ROI is declared against that grid and the *engine*
scales it to whatever resolution is actually being captured (`RoiScaler.ToFrame`), echoing the
frame-space rect it really read back on the result. A plugin never rescales a rect the engine
reports. This is what keeps a ROI table valid across monitors, and it is a frozen constraint, not a
convenience ([`ARCHITECTURE.md`](ARCHITECTURE.md#frozen-constraints)).

A region that cannot touch the frame at all — origin past the frame edge, or zero width/height — is
rejected as a per-ROI error rather than clamped to a meaningless sliver.

**Choosing `Kind`** (`RoiKind`, and the fuller table in
[`ENGINE-SERVICES.md`](ENGINE-SERVICES.md#roi-kinds)):

| Kind | Read it with | Use for |
| --- | --- | --- |
| `Text` | `TryGetText` | Panels where only the text matters — status lines, single-value readouts. |
| `Detailed` | `TryGetOcr` (per-word boxes) | Table-shaped UI where column position decides which value belongs to which row. |
| `Pixels` | `TryGetPixels` → `PixelPatchSampler` | Small colour probes — a toggle's state by its fill colour. Never for text. |

**Choosing `Scale`.** It is the OCR upscale factor and applies to `Text`/`Detailed` only; `0` (or
less) means "engine default", `1.0`. Small UI text usually needs 2-4 — the shipped plugins use 3.0
for a tab counter and 2.0 for a large pane. The engine clamps the request so the upscaled crop stays
under the Windows OCR maximum dimension and reports what it actually applied on
`RoiResult.effective_scale`, so asking for 8.0 on a large region silently gets less; size the region
down instead of the scale up.

**Sizing a `Pixels` region.** Its BGRA payload must stay under `EngineDefaults.MaxPixelBytes` or the
region fails with a per-ROI error — a colour probe should be a strip a few pixels tall, not a panel.
The budget is checked on the **frame-space** rect, after the engine has scaled the reference rect to
the actual capture resolution, so a probe that fits comfortably at 2560x1440 can still blow the cap
on a 4K screen. Size it against the largest resolution the plugin should support, not the reference.
Channel order is `EngineDefaults.PixelChannelOrder` (`BGRA`), and a red/blue swap produces perfectly
plausible wrong answers, so write colour predicates against B, G, R in that order.

**Calibrating.** Both aids read the engine's *most recently scanned* frame, not a fresh capture:

- `ReadRoiAsync(roi, ct)` runs the same read path a live tick uses against a candidate rectangle, so
  a calibration read behaves exactly as a subscribed ROI would have on that frame. Returns `null`
  when the engine has not scanned anything yet. **Nothing a plugin acts on may come from here** — it
  is a second round-trip and may land on a different frame than the tick in hand, which is the
  cross-frame mixing `TickData` exists to prevent. It throws `RoiResultException` if the engine
  flagged the region, or if it is a `Pixels` subscription (no OCR to return).
- `DumpFrameAsync(roi, prefix, ct)` asks the engine to write a PNG — full frame when `roi` is null,
  otherwise a crop through the same scaling path — and hands back the absolute path *on the engine's
  machine*. The frame itself never crosses the boundary. It returns `null` when the engine has not
  scanned yet or when `saveDebugFrames` is false, which is the ordinary case, so treat a dump as a
  debugging aid that is allowed to fail: emit the record first, then dump inside a `try`.

The practical loop is: run the engine, run the plugin with `--verbose` and `saveDebugFrames: true`,
compare the dumped PNG against what `LogVerbose` printed, nudge the rect, repeat. For a region that
is not subscribed at all, `ReadRoiAsync` will probe it without touching the subscription.

## 5. Error handling

Two independent things, and mixing them up is the classic way to corrupt a state machine.

**Per-region status.** A tick states what happened to each region:

```csharp
ctx.Tick.Status(id)          // Ok | Failed | NotSubscribed
ctx.Tick.HasErrors           // any region failed this tick
ctx.Tick.ErroredRois         // which ones, in wire order
ctx.Tick.ErrorMessage(id)    // what the engine said, or null if it did not fail
```

`Failed` and `NotSubscribed` are separate on purpose: a failed region was read and the read did not
work, while `NotSubscribed` means the id was never in the tick — a typo'd constant, which would
otherwise survive an entire session as "no reading".

Always prefer the `Try` accessors over the obsolete `Text`/`Ocr`/`Pixels`/`Error` ones. A region that
failed and a region that was genuinely blank *both* answer `""`, and for a state machine the
difference is everything: "the panel header read empty" can mean the panel closed, which files an
order that never completed.

**Whole-tick policy.** `ErrorPolicy` decides whether a degraded tick reaches the parser at all — one
decision made once, instead of a check every reader has to remember:

| Policy | Behaviour | When |
| --- | --- | --- |
| `AbortTick` (default) | The host skips the tick entirely. Nothing the plugin sees is ever degraded. | Almost always — especially any plugin holding state across ticks. |
| `SkipErrored` | The tick is delivered. The host notes which regions failed — through `LogVerbose`, so only under `--verbose`, and once per failure stretch rather than per tick — and the plugin is expected to check before trusting a reading. | Regions that are genuinely independent, where one failing should not blind the others. |
| `PassThrough` | Delivered with no host-side filtering or logging. | Rare; you are taking over the whole decision. |

Under `AbortTick` the host never calls `OnTickAsync` while a subscribed region is failed, so the
in-plugin guards above only matter under the other two policies and when tests drive the plugin
directly. Write them anyway — they are what makes the plugin correct under either.

Throwing out of `OnTickAsync` is survivable and logged; the run continues with the next tick. A
plugin that dies on one bad frame loses every bit of state it accumulated, which is why the host
refuses to let it.

## 6. Session events

`OnSessionEvent` is where the assumption "I have seen every frame since I started" stops holding. It
is deliberately void and not cancellable — these are notifications, not work — and it runs on the
host's loop, so anything slow belongs on the next tick.

| Event | Means | Typical response |
| --- | --- | --- |
| `Connected(EngineInfo)` | Subscribed and receiving. Raised **once per connect**, so a reconnect raises it again. | Read `Engine.ReplayMode` / `ScanInterval`; re-arm anything per-session. |
| `Reconnecting(Attempt)` | The session dropped; the host is about to dial again. `Attempt` counts from 1 within the current disconnected stretch and resets on the next `Connected`. | Escalate logging. Not a decision point — whether to keep going is the host's call. |
| `TicksDropped(Gap)` | A frame-sequence gap proves that frames were scanned which this plugin never saw. | Treat the next tick as a fresh observation rather than the successor of the last one. |
| `Ended(StreamEndReason)` | The run is over; the summary is about to print. The last chance to persist. | Flush. Branch on the reason. |

Dropped ticks are a **normal live-mode event**, not a transport failure: the engine's per-client
channel holds 4 ticks and drops the oldest rather than stalling the scan loop for every other
plugin. A replay session never drops (the engine blocks instead), which is what makes corpus runs
deterministic. A tracker watching for an *edge* — a counter incrementing, a panel appearing — can
miss the edge entirely across a frame-sequence gap, so the honest response is to reset the "what did I last see"
state rather than to compare across the hole.

`StreamEndReason` distinguishes four endings: `ReplayCompleted` (the corpus ran out),
`EngineShutdown` (a live engine completed the stream), `Cancelled` (Ctrl+C, or the embedding host's
token), and `Faulted` (unrecoverable — a protocol the engine refuses). The host exits 0 for every
orderly ending including Ctrl+C, and 1 for a usage error and for `Faulted`, which are the two
endings a supervisor should not simply restart.

Reconnects deliberately **keep** plugin state: what the plugin already saw is still true, and the
first read after reconnect is a re-sighting rather than a new event.

## 7. Testing

Two layers, both in `Ocrx.Sdk.Testing` — a real package, not `InternalsVisibleTo`, so it works
from a plugin's own repository.

The template ([§2](#2-creating-the-project)) scaffolds `tests/MyPlugin.Tests.csproj` already wired
with `Microsoft.NET.Test.Sdk`, `xunit`, and a `PackageReference` on `Ocrx.Sdk.Testing` —
nothing to set up by hand. Building a test project from scratch (no template available) is in
[Appendix: manual project setup](#appendix-manual-project-setup).

**Unit: `TickDataBuilder` + `FakePluginServices`.** The builder produces a `TickData` the way the
engine would have sent it (through the SDK's own wire mapping), so a tick that could never arrive on
the wire cannot pass a test. `FakePluginServices` records emissions and logs instead of printing.
For a host test, pass `FakeRecordSink` through the public `PluginHostOptions.Sinks` list; its public
`Received` list captures every delivered `CaptureRecord`, including clears.

```csharp
using Ocrx.Sdk;
using Ocrx.Sdk.Testing;
using Xunit;

namespace MyPlugin.Tests;

public class CounterPluginTests
{
    private static TickContext Tick(TickData tick, FakePluginServices services)
        => TickContext.ForTesting(tick, services);

    [Fact]
    public async Task Emits_once_per_change()
    {
        var plugin = new CounterPlugin();
        var services = new FakePluginServices();

        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "3/8").Build(), services), default);
        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "3/8").Build(), services), default);
        await plugin.OnTickAsync(Tick(new TickDataBuilder().Text("counter", "4/8").Build(), services), default);

        Assert.Equal(["3/8", "4/8"], services.Emitted.Select(r => r.RawText));
    }

    [Fact]
    public async Task Failed_roi_emits_nothing()
    {
        var plugin = new CounterPlugin();
        var services = new FakePluginServices();
        var tick = new TickDataBuilder().Errored("counter", "region outside frame").Build();

        await plugin.OnTickAsync(Tick(tick, services), default);

        Assert.Empty(services.Emitted);
        Assert.Equal(RoiStatus.Failed, tick.Status("counter"));
    }
}
```

The builder covers every shape a tick can carry: `.Text(id, text)`, `.Detailed(id, lines...)` (an
`OcrLineSpec` converts implicitly from a string, or is built from `OcrWordSpec`s when the parser
reads word geometry), `.Pixels(id, b, g, r, w, h)`, `.Errored(id, message)`, plus `.Manual()`,
`.FrameSeq(n)` for gap tests, and `.At(instant)` to separate frame time from processing time.
`FakePluginServices` exposes `Emitted`, `Cleared`, `Logs`, `VerboseLogs`, a settable `Engine`, and
`DumpFrameHandler` / `ReadRoiHandler` for the calibration paths.

**Parity: `ReplayHarness`.** This spawns a real `Ocrx.Engine.exe` replaying a PNG corpus and drives
the plugin through its real `OcrxPluginHost` path — public SDK plus an engine binary, no in-proc
shortcuts. It is what a plugin's CI runs.

```csharp
/// A spawned engine owns a named pipe and a Windows OCR instance, so two of these must never
/// run at once. The CollectionDefinition is what actually serializes them — the [Collection]
/// attribute alone only groups; without DisableParallelization the group still runs beside
/// every other collection in the assembly.
[CollectionDefinition("ReplayParity", DisableParallelization = true)]
public class ReplayParityCollection;

[Collection("ReplayParity")]
[Trait("Category", "Integration")]
public class ReplayParityTests
{
    [Fact]
    public async Task Corpus_emits_one_record()
    {
        var corpusDir = ReplayCorpus.Resolve("Fixtures/Replay/my-corpus");
        Assert.True(Directory.Exists(corpusDir), $"corpus not copied to the test output: {corpusDir}");

        var result = await ReplayHarness.RunAsync(new ReplayOptions
        {
            EnginePath = EngineLocator.Resolve(),
            CorpusDir = corpusDir,
            Plugin = new CounterPlugin(),
        });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(StreamEndReason.ReplayCompleted, result.Reason);

        var record = Assert.Single(result.Records);
        Assert.Equal("counter", record.Plugin);
        Assert.Equal(TriggerKind.Auto, record.Trigger);
    }
}
```

**`OCRX_ENGINE_PATH` is effectively required for a plugin outside this repository.**
`EngineLocator.Resolve()` uses that env var when set, and otherwise falls back to walking up from the
test assembly's output looking for `src/Ocrx.Engine/bin` — a path that exists only inside a clone
of the engine repo. From a plugin's own repo the fallback finds nothing and `Resolve()` throws
`InvalidOperationException`, so point the variable at the engine you unpacked or built in
[§1](#1-prerequisites):

```powershell
$env:OCRX_ENGINE_PATH = "C:\tools\ocrx\Ocrx.Engine.exe"
```

CI does the same thing, pinning it to the exact artifact it downloaded or built.
`ReplayOptions.Timeout` (default 5 minutes) is a hang bound, not a performance budget — a handful of
frames measures in seconds, so a fired timeout means something is stuck.

**Capturing the corpus** is a live, in-game step: run the engine with `--save-frames`, press the
configured hotkey (`%LOCALAPPDATA%\Ocrx\engine-config.json`'s `hotkey`, default `Ctrl+Shift+F12`, logged at startup) at
each stage worth a frame. Each press writes one full-frame PNG into the engine's own output
directory — `%LOCALAPPDATA%\Ocrx\engine-config.json`'s `outputDir`, `captures/` by default, resolved relative to the
config file and printed as `Dumps:` on startup. Copy those PNGs into `Fixtures/Replay/<name>/` and
copy them to the test output:

```xml
<None Include="Fixtures\Replay\my-corpus\**\*.png" CopyToOutputDirectory="PreserveNewest" />
```

(A corpus shared between several test projects lives outside any one of them and needs
`Link=` to land at the same relative path in each output — that is the pattern this repository's
own `tests/fixtures/corpus/` uses.)

Frames are full captures — the engine applies ROI geometry itself — and play back in ordinal
filename order, which the engine's timestamped names already satisfy. Full details, including why
replay never drops a tick, are in [`REPLAY.md`](REPLAY.md).

## 8. Cold-start checklist

Verified against a project built outside this repository:

```powershell
dotnet new ocrx-plugin -n MyPlugin   # §2 — then rename/fill in MyPlugin.cs per §3
dotnet build MyPlugin -c Release            # SDK + contracts restore from nuget.org

# Unit tests only. The parity test from §7 spawns a real engine, so it is filtered out
# here — run it once OCRX_ENGINE_PATH and a corpus are in place.
dotnet test MyPlugin\tests\MyPlugin.Tests.csproj -c Release --filter "Category!=Integration"
```

Then, with an engine running:

```powershell
dotnet run --project <clone>\src\Ocrx.Engine     # terminal 1
dotnet run --project MyPlugin -- --verbose         # terminal 2
```

The plugin prints its banner, `waiting for engine on pipe '<name>'...`, and then — once connected —
the engine version, frame size, cadence, and its own ROI ids. If it waits forever, the pipe names
disagree: check `config.json` against `%LOCALAPPDATA%\Ocrx\engine-config.json`, or pass `--pipe <name>` to both.

## 9. Config, CLI, and compatibility

**Config.** `PluginConfig` carries the three settings every plugin has — `pipeName` (must match the
engine's), `saveDebugFrames`, and `outputs` ([Outputs: sinks](#outputs-sinks)) — plus
`configVersion`, which is bookkeeping for `ConfigSeed` rather than a setting anyone edits (see
below). Derive to add your
own; `PluginConfig.Load<T>(path)` writes a
defaults file on first run so settings are discoverable without documentation, and `AfterLoad` is
the hook for anything that must resolve relative to the config file's own location (a ledger path,
typically). Hand the loaded instance to the host so it does not re-read the file:

```csharp
// MyConfig.cs
public sealed class MyConfig : PluginConfig
{
    public string LedgerPath { get; set; } = "ledger.csv";

    // Runs on both the first-run and the read-back path. A bare relative path would otherwise
    // resolve against the working directory, which is whatever shell launched the plugin.
    protected override void AfterLoad(string configPath)
        => LedgerPath = Path.GetFullPath(LedgerPath, Path.GetDirectoryName(configPath)!);
}

// Program.cs, for a plugin that takes its settings as a constructor argument
var config = PluginConfig.Load<MyConfig>(Path.Combine(AppContext.BaseDirectory, "config.json"));
return await OcrxPluginHost.RunAsync(new MyPlugin.LedgerPlugin(config), args,
    new PluginHostOptions { Config = config });
```

The host cannot load a derived config itself — `Load<T>` needs the concrete type, and the plugin is
the only party that knows it and needs the typed instance back anyway.

Everything about *how* the screen is read — monitor, hotkey, OCR language, scan cadence — belongs to
the engine's config. A plugin that grew those knobs would be describing a capture stack it no longer
owns.

**Shipping defaults users have already run past.** A plugin that ships its `config.json` as an
embedded resource and copies it out on first run has a hole: a default added *later* never reaches
anyone who has run the plugin once. Nothing reports it either — `SinkFactory` routes a sink the user
has no entry for to a no-op, so the feature is absent rather than broken. `ConfigSeed` closes it:

```csharp
// UserConfig.cs — replaces the hand-rolled "write it if the file is missing" block
public static string Ensure() =>
    ConfigSeed.EnsureInLocalAppData(typeof(UserConfig).Assembly, "MyPlugin.config.json", "MyPlugin");
```

Seeding still happens on first run. Beyond that, each entry in the embedded `outputs` array carries
the version it was introduced in, and `ConfigSeed` offers only entries newer than the version
stamped on the user's file. So shipping a new default is two edits: add the entry with an `addedIn`,
and bump `configVersion` to match.

```json
{
  "configVersion": 2,
  "outputs": [
    { "type": "json",    "addedIn": 1, "path": "captures/records.jsonl" },
    { "type": "overlay", "addedIn": 2, "overlay": { "template": "{name}" } }
  ]
}
```

`addedIn` is bookkeeping about the shipped default, not a setting — it is stripped from what lands
in the user's file.

Tagging the entry rather than the file as a whole is what makes the guarantee hold for more than one
release. Each default is offered exactly **once**; delete it afterwards and it stays deleted, however
many versions ship later. Comparing the defaults against what the user currently has would instead
read "deleted" and "never offered" as the same state, and resurrect a declined default on the next
bump after the one that introduced it. Emptying `outputs` entirely is likewise a real choice, and
survives.

Three consequences worth knowing before relying on it:

- **An untagged entry is never merged.** It ships to new users through first-run seeding, but no
  existing file is offered it. Omit `configVersion` from the embedded default (version 0) and the
  whole plugin opts out, keeping the old first-run-only behaviour.
- **Outputs are also matched on `type`.** A genuinely new default is skipped if the user already has
  a sink of that type — they may have added one themselves, and a second sink of the same type
  quietly writes the same records to two places.
- **Anything it cannot read, it declines to edit**: invalid JSON, duplicate keys, a root that is not
  an object, an `outputs` that is not a list. The file is left exactly as it is for `Load<T>` to
  report. A malformed *embedded* default is likewise not allowed to disturb an existing user's
  working config.

Writes go through a temporary file and a rename, so an interrupted run cannot leave a user holding a
truncated config.

**CLI.** The host parses `--pipe <name>` (overriding the config) and `--verbose` for every plugin.
For flags of your own, use `PluginHostOptions.ExtraArgHandler`: it is handed the whole argument list
before anything else happens and returns an error message to abort with exit code 1, or null to
proceed. The host never consumes what it does not recognise, so a handler may read flags the host
also reads.

**Other host options.** `ConfigFileName` (null skips loading), `Output` (an `IPluginOutput` for
hosting outside a console), `ShutdownToken` and `HandleCancelKeyPress` (for an embedding process that
owns the lifetime), `RecordSink` (tee every emitted record — how the replay harness collects them),
`ReconnectDelay`, and `EngineWait`.

**Compatibility.** The `Track` stream opens with a handshake carrying an integer protocol version,
negotiated down to what both sides speak; an engine that refuses the version ends the run with
`StreamEndReason.Faulted` and exit 1 rather than retrying, because dialling again cannot fix it. That
version is distinct from any package or release version. The rules — what may change without a
protocol bump, and what may not — are in [`PROTOCOL.md`](PROTOCOL.md#version-policy); the released
matrix of which protocol/engine/SDK versions actually shipped together is in
[`COMPATIBILITY.md`](COMPATIBILITY.md). A plugin that stays on the SDK's own types (never a generated
proto type, never `Grpc.*`) is the one that survives a wire change; CI greps for exactly that
(`.github/workflows/ci.yml`, "Plugin boundary grep gate").

## Appendix: manual project setup

Only needed when `Ocrx.Plugin.Template` cannot be installed. Five files — three here, two in
[§3](#3-anatomy-of-a-plugin) — plus a test project.

A plugin is an ordinary console exe:

```powershell
dotnet new console -n MyPlugin
```

`MyPlugin.csproj` — plain `net10.0`, plus references to the SDK and the contracts:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- Plain net10.0. A plugin parses text; it never touches the capture stack, so the Windows
       TFM the engine needs must not appear here. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>MyPlugin</RootNamespace>
  </PropertyGroup>

  <!-- One version train: Sdk, Contracts and Sdk.Testing always move together, so pin all
       three to the same version — see COMPATIBILITY.md. -->
  <ItemGroup>
    <PackageReference Include="Ocrx.Sdk" Version="1.*" />
    <PackageReference Include="Ocrx.Contracts" Version="1.*" />
  </ItemGroup>

  <ItemGroup>
    <None Update="config.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

`config.json` — the two settings every plugin has (see [§9](#9-config-cli-and-compatibility)); the
pipe name must match the engine's:

```json
{
  "pipeName": "Ocrx.Engine",
  "saveDebugFrames": false
}
```

`Program.cs` — the whole entry point. Everything else the two shipped plugins used to carry here
(argument parsing, the connect/reconnect loop, Ctrl+C, the summary) lives in the host now:

```csharp
using Ocrx.Sdk;

return await OcrxPluginHost.RunAsync(new MyPlugin.CounterPlugin(), args);
```

`CounterPlugin.cs` and `Rois.cs` are [§3](#3-anatomy-of-a-plugin).

The test project — `dotnet new xunit -n MyPlugin.Tests`, then:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\MyPlugin\MyPlugin.csproj" />
    <!-- TickDataBuilder, FakePluginServices, ReplayHarness — same version as the SDK above. -->
    <PackageReference Include="Ocrx.Sdk.Testing" Version="1.*" />
  </ItemGroup>

</Project>
```

## See also

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — processes, projects, and the frozen constraints.
- [`ENGINE-SERVICES.md`](ENGINE-SERVICES.md) — every RPC, budget, and constant the engine offers.
- [`PROTOCOL.md`](PROTOCOL.md) — transport, handshake, version policy, coordinate spaces.
- [`REPLAY.md`](REPLAY.md) — corpus layout, capture, and replay.
- [`COMPATIBILITY.md`](COMPATIBILITY.md) — protocol/engine/SDK version matrix and the rules for
  bumping each.

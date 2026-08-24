# Replay corpora

A **corpus** is a directory of full-frame PNGs the engine can feed through its scan loop instead
of live capture — the mechanism behind `--replay`, `ReplayFrameSource`, and every replay-parity
test built on `ReplayHarness`: the engine's own thin smoke here in
`GameCapture.Engine.Tests/ReplayHarnessTests.cs`, and the plugin suites'
`ReplayParityTests.cs` over in
[`gamecapture-plugins`](https://github.com/PetitCastor/gamecapture-plugins).

## Layout

- One directory = one corpus. Every `*.png` directly inside it is one frame; no subdirectories.
- Filenames sort in **ordinal** order (`StringComparer.Ordinal`, not culture-aware) to produce
  playback order — `ReplayFrameSource.EnumerateCorpus` and every test source that has to agree
  with it share this rule. `FrameSaver`'s timestamped names (`frame_YYYYMMDD_HHmmss_fff.png`)
  already sort correctly this way; a hand-assembled corpus must preserve that property.
- Frames are full captures, not cropped ROIs — the engine applies ROI geometry itself, so a
  corpus exercises the same reference-space-in/frame-space-out path production does.
- Plugin corpora live in the plugins repo under `tests/fixtures/corpus/<name>/` and are linked into
  whichever test project needs them, one `<None Include="..\fixtures\corpus\<name>\**\*.png" Link="Fixtures\Replay\<name>\..." />`
  per corpus — see that repo's `RefineryPlugin.Tests.csproj` and `MissionPlugin.Tests.csproj` for
  the pattern. Scoped per corpus rather than a `corpus\**` catch-all, so one plugin's corpus never
  gets copied into every other plugin's test output. The
  engine's own `Fixtures/engine-smoke` corpus (`GameCapture.Engine.Tests`) stays local to that project —
  it is the corpus essentially the whole engine-side suite drives (scan loop, gRPC host, SDK
  client, handshake, plugin host, and the `ReplayHarness` smoke), not just the harness.

## Capturing a corpus in-game

1. Run the engine against the live game with `--save-frames`:
   `dotnet run --project src\GameCapture.Engine -- --save-frames`
2. Press the configured hotkey (`%LOCALAPPDATA%\GameCapture\engine-config.json`'s `hotkey`, logged on startup) at each stage
   worth a frame — e.g. for a refinery order: SETUP open, after each scroll, on a REFINE toggle,
   post-CONFIRM, and (for a parity corpus that needs it) one CANCEL. Each press saves one
   full-frame PNG under the configured dump directory.
3. Copy the resulting PNGs into a new `tests/fixtures/corpus/<name>/` directory. No renaming
   needed — the timestamped names already sort in capture order.

`--save-frames` and `--replay` are mutually exclusive (replay has no live screen to save from —
see `Program.cs`'s check) — a corpus is captured live, then replayed offline afterward.

## Replaying a corpus

- Engine CLI: `--replay <dir>` feeds the directory through the scan loop instead of live capture,
  in the same process shape production uses, then exits when the corpus is exhausted.
- SDK test harness: `GameCapture.Sdk.Testing.ReplayHarness.RunAsync` spawns a real `GameCapture.Engine.exe`
  with `--replay <dir> --pipe <generated>`, drives a plugin against it through the plugin's real
  `GameCapturePluginHost` path, and returns every emitted record plus how the run ended
  (`StreamEndReason`, exit code). This is what a plugin's own CI uses for parity, and what every
  replay-parity test in this repo is built on now (TASK-13). Set `ReplayOptions.VideoPath` (with an
  optional `VideoFps`) instead of `CorpusDir` to drive the same run from an MP4 — see
  [Video sources](#video-sources) — exactly one of the two must be set (TASK-26).

See [`docs/ENGINE-SERVICES.md`](ENGINE-SERVICES.md#replay-mode) for the engine's replay-mode flags
and determinism guarantees, [`docs/PROTOCOL.md`](PROTOCOL.md#backpressure-and-stream-end) for
the replay-vs-live backpressure policy, and
[`docs/PLUGIN-AUTHORING.md`](PLUGIN-AUTHORING.md#7-testing) for writing the parity test itself.

## Video sources

`--video <path>` feeds an MP4 through the exact same `ScanLoop` a PNG corpus does —
`VideoFrameSource` decodes via `MediaComposition.GetThumbnailAsync`, through the same
`BitmapDecoder` path `ReplayFrameSource.DecodeFrameAsync` already uses for PNGs. Reach for it
instead of a corpus when the session is long or still being iterated on: reaching a refinery
screen after collecting ore is a grind not worth repeating for every rerun of a feature, and a
recording is one file instead of thousands of committed PNGs. A PNG corpus stays the format for
anything that ships in a PR — lossless, diffable, reviewable frame by frame; a video is a working
dev-loop artifact, not a fixture.

Same source class, two modes, chosen by `--video-realtime`:

- **Deterministic (default)** — steps frames at a fixed interval (`--video-fps`, defaulting to
  `1000 / scanIntervalMs` so sampling matches the live scan cadence) and returns each as fast as it
  decodes, ending at EOF. Same shape as a PNG corpus: reproducible run to run, and the mode
  `VideoFrameSourceTests` exercises.
- **Interactive (`--video-realtime`)** — `NextFrameAsync` waits for each frame's presentation time
  against a wall clock, so the engine sees the session unfold at recorded speed: the manual hotkey
  and the metrics status bar are both live, same as against the real game. `--video-loop` restarts
  at EOF instead of ending the run.

`IFrameSource.IsReplay` is `true` in both modes — see `VideoFrameSource`'s own doc comment for why
realtime still counts as replay — so the `Wait`-mode backpressure and manual-subscription gating
below apply to `--video` exactly as they do to a PNG corpus.

The SDK test harness reaches the deterministic mode through `ReplayOptions.VideoPath` /
`VideoFps` (TASK-26): a parity test can point at a recording instead of a corpus with no other
change, and gets the same `StreamEndReason.ReplayCompleted` at EOF. The harness deliberately never
sets `--video-realtime`/`--video-loop` — those are interactive dev-loop knobs, not deterministic
assertions.

**OCR fidelity is a property of the recording, not the code.** Record at native resolution and a
high bitrate — a 1080p capture of a 1440p session loses the thin strokes Windows OCR needs, and no
amount of `OcrPipeline` upscaling recovers them. `MediaComposition.GetThumbnailAsync` was chosen
over the `MediaPlayer` frame-server alternative because a spike measured its OCR output against a
lossless `--save-frames` baseline and found it matched; that guarantee does not extend to a
recording that was itself downscaled. Sanity-check a new recording against a `--save-frames`
baseline of the same session before trusting it as a stand-in.

## Backpressure: replay vs. live

The engine's per-client channel picks its full-mode based on whether the session is a replay
(`ClientSubscription.cs`): `BoundedChannelFullMode.Wait` in replay, `DropOldest` live. A live client
that falls behind loses old ticks rather than stalling the scan loop; a replay client must never
lose one — parity means every frame in the corpus produces exactly one tick, deterministically,
regardless of how slowly a plugin consumes them.

## Finding the engine binary

`GameCapture.Sdk.Testing.EngineLocator.Resolve()` — the `GAMECAPTURE_ENGINE_PATH` env var if set (CI
pins this to the exact artifact it built), otherwise walks up from the running test assembly to
the solution root looking for `src/GameCapture.Engine/bin`, then picks the newest `GameCapture.Engine.exe`
under it (Release wins ties over Debug). Right for a dev running tests against whatever they last
built locally; set the env var to point at a specific build (CI, or testing a published release).

## Git LFS

Corpora are small PNG sets today (a handful of frames each) and live as ordinary tracked files.
A corpus approaching ~50 MB should move to Git LFS instead of growing the repo's blob history —
not needed yet, but worth doing before capturing anything long or high-resolution.

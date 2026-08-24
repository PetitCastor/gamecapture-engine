# Architecture

GameCapture is a screen-capture engine plus one process per plugin. The engine
owns capture, OCR, and pixel sampling; plugins own all game-semantic parsing and state. They
never share memory or a build — a plugin cannot take down capture, another plugin, or the process
that owns the screen.

## Working vocabulary

The codebase and docs use the following words consistently:

- **frame**: one captured image in frame space.
- **tick**: one per-client result assembled from one frame.
- **session**: one plugin host run connected to one engine channel.
- **subscription**: one client's current ROI set on its `Track` stream.
- **live**: a source backed by ongoing desktop capture.
- **playback**: a finite or looping source backed by replay corpora or video.
- **config**: persisted JSON settings read from disk.
- **options**: runtime-only construction or CLI inputs.
- **spec**: a declarative shape such as an ROI or sink entry.
- **host**: the component that owns process/runtime composition.
- **service**: an RPC or support component with a focused operational role.
- **sink**: a plugin output destination for emitted capture records.

## Process diagram

```mermaid
flowchart LR
    subgraph EngineProc["GameCapture.Engine (Windows-TFM exe, per-user, one instance)"]
        WGC["WGC monitor capture<br/>MonitorCapture / CaptureInterop"]
        OCR["Windows OCR<br/>OcrPipeline"]
        Hotkey["Low-level keyboard hook<br/>HotkeyListener"]
        Loop["ScanLoop<br/>(one frame in, one TickResult<br/>per client out)"]
        Reg["SubscriptionRegistry"]
        Svc["CaptureGrpcService<br/>(Track / ReadRoi / DumpFrame / GetStatus)"]

        WGC --> Loop
        OCR --> Loop
        Hotkey -. "manual flag<br/>(Interlocked.Exchange)" .-> Loop
        Loop <--> Reg
        Loop --> Svc
    end

    Svc <==>|"named pipe gRPC<br/>(HTTP/2, plaintext)"| SdkA["GameCapture.Sdk<br/>in MissionPlugin.exe"]
    Svc <==>|"named pipe gRPC"| SdkB["GameCapture.Sdk<br/>in RefineryPlugin.exe"]
    Svc <==>|"named pipe gRPC"| SdkN["GameCapture.Sdk<br/>in plugin N"]
```

One named pipe, one gRPC channel per plugin process, one `Track` bidi stream per channel. Every
scanned frame produces one `TickResult` per connected client — never a partial one — because all
OCR in the engine happens inside `ScanLoop`, one frame at a time
(`src/GameCapture.Engine/ScanLoop.cs`). The wire contract, handshake, and the guarantees a plugin
may rely on are the subject of [`docs/PROTOCOL.md`](PROTOCOL.md); this document is about the
processes and projects that contract sits between.

## Frozen constraints

These were decided once, during the original engine/plugin split, and are not up for revisiting
inside a task (the task docs that recorded the decision live under `tasks/`, which is gitignored
and not part of the repo history — the constraints below are restated in full rather than linked):

- **Windows TFM stops at the engine.** Only `GameCapture.Engine` targets
  `net10.0-windows10.0.22621.0` (`src/GameCapture.Engine/GameCapture.Engine.csproj`) — it is the only project
  that touches WGC or Windows OCR. `GameCapture.Contracts`, `GameCapture.Sdk`, `GameCapture.Sdk.Testing`, and every
  plugin target plain `net10.0`, so a plugin (or its CI) never needs a Windows OCR language pack to
  build or unit-test.
- **Per-tick atomicity.** Everything a plugin needs for one decision arrives in one `TickResult`
  read from one frame; a client's ROI set is swapped whole, never mutated mid-tick
  (`ClientSubscription.cs:22-26`). No mid-tick round-trips exist in the protocol.
- **OCR-results-only boundary.** Only OCR text/geometry and small pixel buffers cross the wire.
  Raw full frames never leave the engine process — `DumpFrame` writes a PNG to disk from inside the
  engine and returns a path, it does not send bytes (`CaptureGrpcService.cs:199-237`).
- **Engine cannot be a session-0 Windows Service.** Windows Graphics Capture requires an
  interactive desktop session, so the engine is a normal per-user console exe, never a background
  service.
- **Reference ROI space is 2560x1440**, and the engine does all scaling to actual frame pixels
  server-side (`RoiScaler.cs:12-13`, `ScanLoop.ReadOneAsync` in `ScanLoop.cs`). Plugins declare
  ROIs once and never rescale a rect the engine reports back.
- **net10.0 (or the Windows-flavored net10.0) everywhere** — no other TFM appears in the solution.

## Component table

| Component | Path | Role |
| --- | --- | --- |
| `protos/capture.proto` | `protos/capture.proto` | The wire contract: `CaptureEngineService` (`Track`/`ReadRoi`/`DumpFrame`/`GetStatus`) and every message shape. Governed by `buf lint` + `buf breaking` in CI (`docs/PROTOCOL.md`). |
| `GameCapture.Contracts` | `src/GameCapture.Contracts/` | Plain `net10.0` library: generated proto code, plus the pure shared types both sides use — `RoiScaler`, `WireLimits`, `OcrRegionResult`, `PixelPatchSampler`, `RoiRect`, `ProtoMapping`, `ProtocolVersion`. |
| `GameCapture.Engine` | `src/GameCapture.Engine/` | The only Windows-TFM project. WGC and frame sources live under `Capture/`; OCR and pixel work under `Processing/`; persisted settings under `Configuration/`; dumping and console output under `Operations/`; desktop lifecycle helpers under `Hosting/`; transport under `Grpc/`. `ScanLoop.cs` coordinates frames, `SubscriptionTickProcessor.cs` owns per-client tick construction and delivery, `RetainedFrameStore.cs` owns reference-counted frame lifetime and bounded unary reads, and `SubscriptionRegistry.cs` / `ClientSubscription.cs` own subscriptions. Composed by `EngineHost.cs`, entered from `Program.cs`. |
| `GameCapture.Sdk` | `src/GameCapture.Sdk/` | Plain `net10.0` client library. `NamedPipeChannel`/`CaptureClient`/`TrackSession` own connection and session behavior; `RoiSubscription`/`RoiKind`/`TickData` declare and read ROIs; `ProtocolNegotiation` owns handshake/version errors; `Plugin/` holds the host layer; and `Plugin/Output/` holds sink contracts, specs, factories, and implementations. |
| `GameCapture.Sdk.Testing` | `src/GameCapture.Sdk.Testing/` | Public testing companion package (no `InternalsVisibleTo`): `TickDataBuilder`, `FakePluginServices`, `ReplayHarness`, `EngineLocator` — spawns a real `GameCapture.Engine.exe` against a corpus and drives a plugin's real `GameCapturePluginHost` path for parity tests. |
| `GameCapture.Sdk.Overlay` | `src/GameCapture.Sdk.Overlay/` | Opt-in plain-`net10.0` package that implements the SDK's overlay factory and a Windows click-through `IRecordSink`; on non-Windows it resolves to a no-op, so the core SDK remains portable. |
| `MissionPlugin` | [`gamecapture-plugins`](https://github.com/PetitCastor/gamecapture-plugins) | Tracks mission-board text via `IGameCapturePlugin`. Lives in the plugins repo — a pure SDK consumer, not built here. |
| `RefineryPlugin` | [`gamecapture-plugins`](https://github.com/PetitCastor/gamecapture-plugins) | Tracks refinery work orders via `IGameCapturePlugin`; owns the order ledger. Same repo as above. |
| `tests/` | `tests/` | One test project per component that lives here. Plugin replay corpora live with the plugins, in the plugins repo (`docs/REPLAY.md`). |

## See also

- [`docs/PROTOCOL.md`](PROTOCOL.md) — transport, handshake, version policy, coordinate spaces, tick
  atomicity, backpressure: the agreements the wire contract carries.
- [`docs/ENGINE-SERVICES.md`](ENGINE-SERVICES.md) — the engine's service catalog: what each RPC
  does, every budget and constant, replay mode, and the hotkey/`--save-frames` path.
- [`docs/REPLAY.md`](REPLAY.md) — corpus layout, capturing one in-game, and how `ReplayHarness`
  runs one against a real plugin.
- [`docs/PLUGIN-AUTHORING.md`](PLUGIN-AUTHORING.md) — how to write a new `IGameCapturePlugin` from
  scratch: project setup, the tick surface, ROI calibration, error policy, and testing.
- [`docs/COMPATIBILITY.md`](COMPATIBILITY.md) — protocol/engine/SDK version matrix and the rules for
  bumping each.

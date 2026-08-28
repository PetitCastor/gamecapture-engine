# GameCapture

[![CI](https://github.com/PetitCastor/gamecapture-engine/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/PetitCastor/gamecapture-engine/actions/workflows/ci.yml)
[![Release](https://github.com/PetitCastor/gamecapture-engine/actions/workflows/release.yml/badge.svg)](https://github.com/PetitCastor/gamecapture-engine/releases)

GameCapture turns text visible in a game's UI into data that your plugin can use.

The engine captures the screen and runs OCR. A plugin declares the areas it needs, receives one set
of readings per frame, and decides what to record. GameCapture only observes what is on screen: it
does not read game memory, modify game files, or inspect network traffic.

## How it works

1. Start the engine and choose the monitor to capture.
2. A plugin subscribes to one or more screen regions.
3. The engine sends the OCR result for each region.
4. The plugin turns those results into records, notifications, or its own state.

The engine and plugins are separate processes. The engine owns capture and OCR; plugins use the SDK
and communicate with it through a local named pipe.

Published plugins are installed from the tray icon's **Plugins…** menu, which downloads them from
the [gamecapture-plugins](https://github.com/PetitCastor/gamecapture-plugins) releases and launches
them on request. See [Installing](docs/INSTALLING.md) for what that stores and what it will refuse.

## Create a plugin

You do not need to clone this repository to write a plugin. Install the template, create a project,
then update its sample region and logic:

```powershell
dotnet new install GameCapture.Plugin.Template
dotnet new gamecapture-plugin -n MyPlugin
```

A plugin is small: declare a region of interest (ROI), then read its text on each tick.

```csharp
using GameCapture.Contracts;
using GameCapture.Sdk;

public sealed class ScorePlugin : IGameCapturePlugin
{
    private static readonly RoiSubscription Score =
        new("score", new RoiRect(1000, 110, 420, 100), 3.0, RoiKind.Text);

    public string Name => "score";
    public IReadOnlyList<RoiSubscription> Rois => [Score];

    public Task OnTickAsync(TickContext context, CancellationToken cancellationToken)
    {
        if (context.Tick.TryGetText(Score.Id, out var score))
            context.Services.Emit(new CaptureRecord(
                context.Tick.Timestamp, Name, TriggerKind.Auto, score));

        return Task.CompletedTask;
    }
}
```

The ROI uses a 2560×1440 reference coordinate system; the engine scales it to the captured display.
Run the engine and your plugin in separate terminals:

```powershell
# Engine
GameCapture.Engine.exe

# Plugin
dotnet run --project MyPlugin
```

Download the engine from [Releases](https://github.com/PetitCastor/gamecapture-engine/releases),
or see [Installing](docs/INSTALLING.md) for installation and configuration details.

## Work on the engine

Requirements: Windows 10/11 with an OCR language pack and the .NET 10 SDK.

```powershell
git clone https://github.com/PetitCastor/gamecapture-engine.git
cd gamecapture-engine
dotnet build GameCaptureEngine.slnx
dotnet run --project src/GameCapture.Engine
```

This repository contains the capture engine, protocol contracts, plugin SDK, test helpers, and the
plugin template. Game-specific plugins live in the separate
[gamecapture-plugins](https://github.com/PetitCastor/gamecapture-plugins) repository.

## Documentation

- [Plugin authoring](docs/PLUGIN-AUTHORING.md) — regions, calibration, configuration, and testing.
- [Architecture](docs/ARCHITECTURE.md) — process boundaries and project layout.
- [Protocol](docs/PROTOCOL.md) — local transport and compatibility rules.
- [Replay](docs/REPLAY.md) — test plugins against saved frames or video.
- [Compatibility](docs/COMPATIBILITY.md) — release and versioning policy.

## License

[MIT](LICENSE)

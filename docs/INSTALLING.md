# Installing the engine

Two ways to get a running `GameCapture.Engine.exe`. Pick the installer if you are a user; pick the
zip if you are a script.

## Installer (recommended)

1. Open [Releases](https://github.com/PetitCastor/gamecapture-engine/releases) and download
   `GameCaptureEngine-Setup.exe` from the latest release.
2. Run it. There are no prompts and no options — it installs per-user, so no administrator
   elevation is requested.
3. The engine launches straight to the notification area. There is no main window: normal launches
   are tray-only, and a console appears only when a debugger is attached. Right-click the tray icon
   for status and exit.

Shortcuts land on the Desktop and at the root of the Start menu. Uninstall through **Settings →
Apps → Installed apps** like any other application.

Windows will warn that the publisher is unknown, because the installer is unsigned — code signing
is deliberately deferred. Choose **More info → Run anyway**.

The engine installs to `%LOCALAPPDATA%\GameCaptureEngine`. Its configuration lives separately at
`%LOCALAPPDATA%\GameCapture\engine-config.json` and is written with defaults on first launch, so
neither installing nor uninstalling disturbs your settings.

## Zip (CI, portable use, side-by-side versions)

Every release also carries `GameCapture.Engine-vX.Y.Z-win-x64.zip`: the same self-contained exe,
with no installer, no shortcuts and no update feed. Extract it anywhere and run it.

This is what the plugins repo's CI downloads to get an engine binary for replay-parity tests, and
it is the right choice whenever you need a specific version at a path you control — for example
pointing `GAMECAPTURE_ENGINE_PATH` at it. The asset name is stable and is not going to change.

## Requirements

Windows 10/11 x64, with an OCR language pack installed for live capture. The build is
self-contained, so no .NET runtime install is required.

## The other release assets

`releases.win.json` and `GameCaptureEngine-X.Y.Z-full.nupkg` are the Velopack update feed and its
payload. They are consumed by an installed engine, not by you — ignore them.

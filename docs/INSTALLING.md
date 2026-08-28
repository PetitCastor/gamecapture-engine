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

## Getting plugins

The engine ships no plugins of its own. Right-click the tray icon and choose **Plugins…** to see the
published catalog, install what you want, and update it later. The dialog lists each plugin's status
— not installed, installed, or an update available — and installs are immediate: nothing about a
plugin is bound at engine startup, so the engine does not restart.

Once a plugin is installed, the tray menu gains a **Launch** entry for it, which becomes **Stop**
while it runs. Nothing starts on its own: the engine launches a plugin only when asked, does not
restart one that exits, and stops the ones it started when you exit the engine. Changing a setting
or the captured monitor restarts the engine, which also stops them.

Plugins install per user under `%LOCALAPPDATA%\GameCapture\plugins\<plugin-id>`, beside the engine
configuration and outside the engine's own install directory — so updating or uninstalling the
engine leaves them alone. Removing a plugin from the dialog deletes that folder.

Two limits are enforced in code rather than left to trust. The catalog URL and the download hosts
are fixed: the engine will only install from
[`gamecapture-plugins`](https://github.com/PetitCastor/gamecapture-plugins) releases, and it
re-checks every redirect a download follows, so an entry pointing anywhere else is shown as
**Blocked** with no way to install it. And a downloaded archive must unpack entirely inside its own
plugin folder and contain exactly one executable, or it is rejected before anything is written.

Plugin binaries are not code-signed, for the same reason the installer is not. They run as ordinary
processes with your user's permissions.

## Requirements

Windows 10/11 x64, with an OCR language pack installed for live capture. The build is
self-contained, so no .NET runtime install is required.

## The other release assets

`releases.win.json` and `GameCaptureEngine-X.Y.Z-full.nupkg` are the Velopack update feed and its
payload. They are consumed by an installed engine, not by you — ignore them.

# OCRX Engine

Private Windows capture and local OCR runtime for [OCRX](https://ocrx.org).

This repository contains the engine implementation only. Public integration surfaces live in
[ocrx-sdk](https://github.com/PetitCastor/ocrx-sdk); public Windows binaries and the Velopack
update feed live in [ocrx-releases](https://github.com/PetitCastor/ocrx-releases).

## Product boundary

- Windows 10/11 x64, .NET 10, Windows Graphics Capture, and Windows OCR.
- Default named pipe: `OCRX.Engine`.
- Install root: `%LOCALAPPDATA%\OcrxEngine`.
- Data root: `%LOCALAPPDATA%\OCRX`.
- No game-memory access, game-file modification, or network inspection.
- Engine source is private; the SDK, contracts, protocol source, template, testing tools, overlay,
  plugins, and website remain public.

## Build

OCRX SDK packages version `2.0.0` must be available from NuGet.

```powershell
dotnet restore OcrxEngine.slnx
dotnet build OcrxEngine.slnx
dotnet test OcrxEngine.slnx --filter "Category!=Integration"
```

## Release

The manual `Release engine` workflow builds and tests the private source, creates an unsigned
Velopack installer plus update metadata, writes SHA-256 checksums, and publishes only those generated
artifacts to `ocrx-releases`. It requires the `OCRX_RELEASES_TOKEN` Actions secret to have release
write access to that public repository.

The first OCRX release is `2.0.0`. The private engine tag and public binary release use the same
version. The engine checks `ocrx-releases` for updates.

## GameCapture v1 migration

After the v2 engine has successfully started its pipe for the first time, it runs the old literal
`%LOCALAPPDATA%\GameCaptureEngine\Update.exe uninstall --silent`. Only a successful uninstall permits
deletion of the verified literal `%LOCALAPPDATA%\GameCapture` data directory. A missing or failing
uninstaller stops cleanup and reports a partial migration; there is intentionally no backup or
confirmation prompt.

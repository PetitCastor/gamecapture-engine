# GameCapture.Sdk.Overlay

Opt-in `IRecordSink` implementation that paints the latest observation in a topmost,
click-through Windows overlay. Register `new OverlaySinkFactory()` through
`PluginHostOptions.OverlayFactory`; on non-Windows systems the factory returns a no-op sink.

The overlay uses physical pixels and requests per-monitor-v2 DPI awareness when the package loads.
The plugin executable should also declare the setting in its `app.manifest`, because a manifest is
applied before any process window can be created:

```xml
<application xmlns="urn:schemas-microsoft-com:asm.v3">
  <windowsSettings>
    <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">
      PerMonitorV2
    </dpiAwareness>
  </windowsSettings>
</application>
```

Reference that manifest from the plugin project:

```xml
<PropertyGroup>
  <ApplicationManifest>app.manifest</ApplicationManifest>
</PropertyGroup>
```

The window never reads or modifies the game process, never requests elevation, and uses
`WS_EX_TRANSPARENT` plus `WS_EX_NOACTIVATE` so mouse and keyboard focus stay with the game.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using GameCapture.Engine.Plugins;
using GameCapture.Engine.Tray;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace GameCapture.Engine;

/// <summary>
/// Maps the loopback control API (TASK-UI-03) onto the same <see cref="WebApplication"/> the named
/// pipe already serves: token-gated JSON endpoints for status, monitors, plugins and settings, plus
/// the <c>WS /api/events</c> push channel. Everything under <c>/api</c> requires the bearer token;
/// static assets do not, since they carry no data.
/// </summary>
internal static class ControlApi
{
    private static readonly JsonSerializerOptions JsonOptions = ControlApiJson.Options;

    /// <summary>Wires every route and returns the event hub so the caller (<see cref="Grpc.GrpcHost"/>)
    /// need not reach back into this class to find it again.</summary>
    public static ControlApiEventHub Map(
        WebApplication app,
        ControlApiToken token,
        ControlApiState state,
        EngineStatus status,
        FrameSourceSelection? sourceSelection,
        EngineConfig config,
        ConsoleSink sink)
    {
        var hub = new ControlApiEventHub(
            status, state, config.MetricsEnabled, sink,
            TimeSpan.FromMilliseconds(Math.Max(250, config.MetricsIntervalMs)));

        // Torn down the instant shutdown begins so a live socket can never extend or block the gRPC
        // client-drain EngineHost.StopAsync already does.
        app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(hub.Dispose);

        app.UseWebSockets();

        // The one gate for every /api/* route and the WebSocket upgrade, plus a last-resort guard so
        // an unexpected fault never reaches the client as a raw stack trace. Static assets live
        // outside "/api" and never reach this check — they carry no data, so there is nothing to
        // protect.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                // The named-pipe gRPC service and loopback web surface share one routing table. A
                // TCP peer may fetch static GET/HEAD assets, but must never reach a pipe-only gRPC
                // route through a non-API POST.
                if (context.Connection.RemoteIpAddress is not null
                    && !HttpMethods.IsGet(context.Request.Method)
                    && !HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await next(context);
                return;
            }

            // TASK-UI-07: a browser's WebSocket constructor has no API to set a custom request header,
            // so the /api/events upgrade cannot carry Authorization the way every other /api/* route
            // does. It proves the token instead via a "bearer.<token>" Sec-WebSocket-Protocol entry,
            // checked (and rejected with 401 before AcceptWebSocketAsync on a miss) inside the
            // /api/events handler itself — so this gate steps aside only for that one path, and only
            // for an actual WebSocket handshake; a plain GET/POST to the same path still needs the
            // header like everything else here.
            if (context.WebSockets.IsWebSocketRequest && context.Request.Path.StartsWithSegments("/api/events"))
            {
                await next(context);
                return;
            }

            if (!IsAuthorized(context.Request, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            try
            {
                await next(context);
            }
            catch (Exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "internal error" }, JsonOptions);
            }
        });

        // Plugin state shared by the routes below: the resolved catalog and latest-version map are
        // cached from the last GET /api/plugins so a POST action can rebuild a row set (for the WS
        // "plugins" push) without a network round trip, and "in flight" guards the same id against a
        // second action landing mid-operation.
        var inFlight = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var cacheGate = new Lock();
        IReadOnlyList<CatalogEntry> cachedCatalog = [];
        var cachedLatest = new Dictionary<string, string>(StringComparer.Ordinal);

        void SetHubPlugins(TrayControls? controls)
        {
            var plugins = controls?.Plugins;
            hub.SetPlugins(
                plugins,
                plugins is null ? null : () => BuildRowsFromCache(plugins));
        }

        state.ControlsChanged += SetHubPlugins;
        SetHubPlugins(state.Controls);

        app.MapGet("/api/status", () => Results.Json(hub.Current, JsonOptions));

        app.MapGet("/api/monitors", () => Results.Json(new
        {
            labels = sourceSelection?.MonitorLabels ?? (IReadOnlyList<string>)Array.Empty<string>(),
            currentIndex = sourceSelection?.CurrentMonitorIndex ?? 0,
        }, JsonOptions));

        app.MapGet("/api/plugins", async (HttpContext context) =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return Results.Json(Array.Empty<PluginRow>(), JsonOptions);

            return Results.Json(await RefreshPluginRowsAsync(plugins, context.RequestAborted), JsonOptions);
        });

        app.MapPost("/api/plugins/{id}/install", (string id, HttpContext context) => HandlePluginActionAsync(id, "install", context));
        app.MapPost("/api/plugins/{id}/update", (string id, HttpContext context) => HandlePluginActionAsync(id, "update", context));
        app.MapPost("/api/plugins/{id}/uninstall", (string id, HttpContext context) => HandlePluginActionAsync(id, "uninstall", context));
        app.MapPost("/api/plugins/{id}/start", (string id, HttpContext context) => HandlePluginActionAsync(id, "start", context));
        app.MapPost("/api/plugins/{id}/stop", (string id, HttpContext context) => HandlePluginActionAsync(id, "stop", context));
        app.MapPost("/api/plugins/{id}/roi-overlay", async (string id, HttpContext context) =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return ServiceUnavailable("plugin management is unavailable");
            if (plugins.RoiOverlays is not { } overlays)
                return ServiceUnavailable("ROI overlays are unavailable");

            RoiOverlayPatch? patch;
            try
            {
                patch = await context.Request.ReadFromJsonAsync<RoiOverlayPatch>(JsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                return BadRequest("invalid ROI overlay body");
            }

            if (patch?.Visible is null)
                return BadRequest("invalid ROI overlay body");

            var entry = FindEntry(id, plugins);
            if (entry is null)
                return BadRequest("unknown plugin id");

            try
            {
                var result = overlays.SetVisible(entry, patch.Visible.Value);
                return Results.Json(new { visible = result.IsVisible }, JsonOptions);
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ex.Message);
            }
        });

        // Per-plugin, so it sits with the other /api/plugins/{id}/* actions rather than with the
        // manager-wide preview toggle below. It shares inFlight with install and uninstall: the
        // preference belongs to this particular install, so an uninstall must not clear it between
        // this endpoint's installed check and its write. The response is the one row field that
        // changed, not a refreshed row set — a checkbox must not cost a catalog fetch and a version
        // probe per plugin.
        app.MapPost("/api/plugins/{id}/autostart", async (string id, HttpContext context) =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return ServiceUnavailable("plugin management is unavailable");

            if (!inFlight.TryAdd(id, 0))
                return Conflict("an operation for this plugin is already in progress");

            try
            {
                PluginAutoStartPatch? patch;
                try
                {
                    patch = await context.Request.ReadFromJsonAsync<PluginAutoStartPatch>(JsonOptions, context.RequestAborted);
                }
                catch (JsonException)
                {
                    return BadRequest("invalid auto-start body");
                }

                if (patch?.Enabled is null)
                    return BadRequest("invalid auto-start body");

                // Only an installed plugin can be auto-started, so an id that is merely in the catalog is
                // rejected rather than silently recorded against a future install.
                if (!plugins.Installer.State.TryGet(id, out _))
                    return BadRequest("unknown plugin id");

                try
                {
                    // Mutation and write in one locked step inside the settings object, so two toggles in
                    // flight at once (two rows, or one checkbox double-clicked) cannot lose each other's
                    // change or leave memory disagreeing with disk.
                    plugins.Settings.SetAutoStart(id, patch.Enabled.Value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Results.Json(new { error = "the operation failed" }, JsonOptions, statusCode: StatusCodes.Status500InternalServerError);
                }

                return Results.Json(new { autoStart = patch.Enabled.Value }, JsonOptions);
            }
            finally
            {
                inFlight.TryRemove(id, out _);
            }
        });

        // Read-only, so no inFlight guard: nothing here mutates a plugin and two readers cannot collide.
        // The drawer polls this with a cursor rather than riding /api/events, because that hub has no
        // per-client subscription — every message goes to every socket — and was built for a change-only
        // push at 250 ms or slower, not for one broadcast per line of a chatty plugin.
        app.MapGet("/api/plugins/{id}/logs", (string id, long? after, int? limit) =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return ServiceUnavailable("plugin management is unavailable");
            if (plugins.Launcher.Logs is not { } logs)
                return ServiceUnavailable("plugin logs are unavailable");
            if (FindEntry(id, plugins) is null)
                return BadRequest("unknown plugin id");

            // A plugin that has never been started reads as an empty page, not a 404: "produced no
            // output" is a normal answer for a row whose button is showing.
            var page = logs.Read(id, after ?? -1, Math.Clamp(limit ?? PluginLogStore.DefaultMaxLines, 0, PluginLogStore.DefaultMaxLines));
            return Results.Json(page, JsonOptions);
        });

        // Not a per-plugin action, so it stands alongside them rather than under /api/plugins/{id}/*:
        // toggles PluginManagerSettings.IncludePreviews (the one plugin-manager preference the deleted
        // PluginsForm used to own, TASK-UI-05 section 4/7) and hands back the same row shape /api/plugins
        // returns, so the client can re-render from the response instead of issuing a second request.
        app.MapPost("/api/plugins/settings", async (HttpContext context) =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return ServiceUnavailable("plugin management is unavailable");

            PluginPreviewsPatch? patch;
            try
            {
                patch = await context.Request.ReadFromJsonAsync<PluginPreviewsPatch>(JsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                return BadRequest("invalid settings body");
            }

            if (patch is null)
                return BadRequest("invalid settings body");

            try
            {
                plugins.Settings.IncludePreviews = patch.IncludePreviews;
                plugins.Settings.Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Mirrors HandlePluginActionAsync's own catch below — same failure modes, same shape.
                return Results.Json(new { error = "the operation failed" }, JsonOptions, statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(await RefreshPluginRowsAsync(plugins, context.RequestAborted), JsonOptions);
        });

        app.MapGet("/api/settings", () =>
        {
            if (state.Controls is not { } controls)
                return ServiceUnavailable("settings are unavailable");

            return Results.Json(new
            {
                settings = controls.Settings,
                ocrLanguages = controls.AvailableOcrLanguages,
                monitors = controls.MonitorLabels,
                // The plugins pane's "Include preview builds" checkbox (TASK-UI-05 section 4) needs a
                // seed value on first load; neither GET /api/plugins (a bare row array, pinned as such
                // by existing tests) nor POST /api/plugins/settings (which only echoes refreshed rows)
                // exposes it. This aggregator already carries other settings-adjacent read-only context
                // (ocrLanguages, monitors), so the per-user preview preference joins it rather than
                // growing the /api/plugins response's shape.
                includePreviews = controls.Plugins?.Settings.IncludePreviews ?? false,
            }, JsonOptions);
        });

        app.MapPost("/api/settings", async (HttpContext context) =>
        {
            if (state.Controls is not { } controls)
                return ServiceUnavailable("settings are unavailable");

            EngineSettingsPatch? patch;
            try
            {
                patch = await context.Request.ReadFromJsonAsync<EngineSettingsPatch>(JsonOptions, context.RequestAborted);
            }
            catch (JsonException)
            {
                return BadRequest("invalid settings body");
            }

            if (patch is null)
                return BadRequest("invalid settings body");

            // Routed through the same validation the tray uses (unavailable OCR pack, unparseable
            // hotkey, unusable pipe name all fall back to the previous value) rather than
            // reimplemented — this endpoint is a second door into the same room and needs the same
            // lock. Respond with what was actually persisted, so a corrected value is visible.
            var result = controls.UpdateSettings(patch.ApplyTo);
            if (!result.Succeeded)
            {
                return Results.Json(
                    new { error = result.Error },
                    JsonOptions,
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Json(new { settings = result.Settings, restartPending = result.RestartPending }, JsonOptions);
        });

        // The page cannot open a native folder picker itself (TASK-UI-05 section 5); this opens
        // FolderBrowserDialog on the UI thread on its behalf and hands back the chosen path, or 204
        // when the dialog was cancelled (or no interactive surface exists to host one at all).
        app.MapPost("/api/settings/browse", async (HttpContext context) =>
        {
            if (state.Controls is not { } controls)
                return ServiceUnavailable("settings are unavailable");

            BrowseFolderRequest? body = null;
            if (context.Request.ContentLength is > 0)
            {
                try
                {
                    body = await context.Request.ReadFromJsonAsync<BrowseFolderRequest>(JsonOptions, context.RequestAborted);
                }
                catch (JsonException)
                {
                    return BadRequest("invalid browse request body");
                }
            }

            var chosen = await controls.BrowseFolderAsync(body?.InitialDirectory);
            return chosen is null
                ? Results.NoContent()
                : Results.Json(new { path = chosen }, JsonOptions);
        });

        app.MapPost("/api/exit", () =>
        {
            if (state.Controls is not { } controls)
                return ServiceUnavailable("exit is unavailable");

            controls.OnExit();
            return Results.Json(new { ok = true }, JsonOptions);
        });

        app.Map("/api/events", async (HttpContext context) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            // TASK-UI-07: this path is exempted from the Authorization-header gate above, so it is the
            // one place responsible for authenticating itself — via the "bearer.<token>"
            // Sec-WebSocket-Protocol entry a browser's WebSocket constructor can actually set. No match
            // means 401 before AcceptWebSocketAsync, same outcome as every other unauthorized /api/*
            // request; a match must echo exactly that one subprotocol back or the browser tears the
            // connection down (RFC 6455 requires a server that accepts a subprotocol to name it).
            if (!TryMatchBearerSubProtocol(context, token, out var subProtocol))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                new WebSocketAcceptContext { SubProtocol = subProtocol });
            await hub.RunAsync(socket, context.RequestAborted);
        });

        // Static assets: outside "/api", so the middleware above never gates them. Absent in a test
        // host — nothing copies ui/ next to a test assembly — so this is skipped rather than pointed
        // at a folder that does not exist, which PhysicalFileProvider would throw on.
        var uiRoot = Path.Combine(AppContext.BaseDirectory, "ui");
        if (Directory.Exists(uiRoot))
        {
            var provider = new PhysicalFileProvider(uiRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
        }

        return hub;

        // --- local functions below close over state/inFlight/cachedCatalog/cachedLatest above ---

        CatalogEntry? FindEntry(string id, PluginServices plugins)
        {
            CatalogEntry? cached;
            lock (cacheGate)
                cached = cachedCatalog.FirstOrDefault(e => e.Id == id);
            if (cached is not null)
                return cached;

            // A plugin can still be managed after it drops out of the catalog (the deleted PluginsForm
            // showed the same "catalog-orphaned" entries; RefreshPluginRowsAsync below still merges
            // them in), so an installed-but-uncatalogued id is still known.
            return plugins.Installer.State.TryGet(id, out var installed)
                ? new CatalogEntry(installed.Id, installed.Name, "", installed.DownloadUrl, installed.Channel, installed.ClientName)
                : null;
        }

        IReadOnlyList<PluginRow> BuildRowsFromCache(PluginServices plugins)
        {
            IReadOnlyList<CatalogEntry> catalog;
            IReadOnlyDictionary<string, string> latest;
            lock (cacheGate)
            {
                catalog = cachedCatalog;
                latest = cachedLatest;
            }

            return ControlApiPluginRows.Build(
                ControlApiPluginRows.MergeInstalled(catalog, plugins),
                plugins,
                latest);
        }

        // TASK-UI-05 section 7: cancellationToken is the request's own HttpContext.RequestAborted, not
        // CancellationToken.None — a dropped or reloaded page cancels its own in-flight catalog fetch
        // instead of leaking it, the equivalent of the deleted PluginsForm's _work cancellation.
        async Task<IReadOnlyList<PluginRow>> RefreshPluginRowsAsync(PluginServices plugins, CancellationToken cancellationToken)
        {
            try
            {
                var stable = await plugins.Installer.FetchCatalogAsync(cancellationToken);
                var catalog = stable;
                if (plugins.Settings.IncludePreviews)
                {
                    try
                    {
                        var previews = await plugins.Installer.FetchPreviewCatalogAsync(cancellationToken);
                        catalog = PluginCatalogMerge.Combine(stable, previews, out _);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
                    {
                        // Preview access is opt-in and best-effort; degrade to stable only.
                    }
                }

                var merged = ControlApiPluginRows.MergeInstalled(catalog, plugins);

                var latest = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var entry in merged)
                {
                    if (entry.Channel == ReleaseChannel.Preview && !plugins.Settings.IncludePreviews)
                        continue;
                    try
                    {
                        var version = await plugins.Installer.ResolveLatestVersionAsync(entry, cancellationToken);
                        if (version.Length > 0)
                            latest[entry.Id] = version;
                    }
                    catch (HttpRequestException)
                    {
                        // Leave this row without update information.
                    }
                }

                lock (cacheGate)
                {
                    cachedCatalog = merged;
                    cachedLatest = latest;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                // Network unavailable, the catalog document was unreadable, or the request (and its
                // token) was cancelled out from under this fetch: degrade to whatever is already
                // cached (possibly empty, on the very first call) rather than failing — this is a
                // routine read, not worth a 500 over a flaky network or a page that moved on.
                lock (cacheGate)
                    cachedCatalog = ControlApiPluginRows.MergeInstalled(cachedCatalog, plugins);
            }

            return BuildRowsFromCache(plugins);
        }

        async Task<IResult> HandlePluginActionAsync(string id, string action, HttpContext context)
        {
            if (state.Controls?.Plugins is not { } plugins)
                return ServiceUnavailable("plugin management is unavailable");

            if (!inFlight.TryAdd(id, 0))
                return Conflict("an operation for this plugin is already in progress");

            try
            {
                switch (action)
                {
                    case "install":
                    case "update":
                        {
                            var entry = FindEntry(id, plugins);
                            if (entry is null)
                            {
                                await RefreshPluginRowsAsync(plugins, context.RequestAborted);
                                entry = FindEntry(id, plugins);
                            }
                            if (entry is null)
                                return BadRequest("unknown plugin id");
                            await plugins.Installer.InstallAsync(entry, progress: null, context.RequestAborted);
                            break;
                        }
                    case "uninstall":
                        {
                            if (!plugins.Installer.State.TryGet(id, out _))
                                return BadRequest("unknown plugin id");
                            plugins.Launcher.Stop(id);
                            // After the stop, never before: a plugin still running could reopen a
                            // buffer between the drop and the kill and leave one behind for a plugin
                            // that no longer exists.
                            plugins.Launcher.Logs?.Drop(id);
                            plugins.Installer.Uninstall(id);
                            // The auto-start opt-out belongs to an install, not to an id: leaving it
                            // behind would make a later reinstall silently inherit a decision the user
                            // made about a plugin they have since deleted, and would grow the opt-out
                            // list forever.
                            try
                            {
                                plugins.Settings.SetAutoStart(id, true);
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                // The plugin is already gone; a preference the next install will simply
                                // default is not worth failing a completed uninstall over.
                            }
                            break;
                        }
                    case "start":
                        {
                            if (!plugins.Installer.State.TryGet(id, out var installed))
                                return BadRequest("unknown plugin id");
                            plugins.Launcher.Start(installed);
                            break;
                        }
                    case "stop":
                        {
                            if (FindEntry(id, plugins) is null)
                                return BadRequest("unknown plugin id");
                            plugins.Launcher.Stop(id);
                            break;
                        }
                    default:
                        return BadRequest("unknown action");
                }

                return Results.Json(new { ok = true }, JsonOptions);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                // A trust-rule rejection, or the recorded executable being gone — both are the
                // caller's to fix, not a server fault.
                return BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                return Results.Json(new { error = "the operation failed" }, JsonOptions, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The page navigated away, reloaded, or dropped mid-operation and its own token
                // cancelled the action out from under it — same "request moved on" case
                // RefreshPluginRowsAsync already treats as benign, not a server fault. The client is
                // already gone, so there is nothing meaningful to write back.
                return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
            }
            finally
            {
                inFlight.TryRemove(id, out _);
            }
        }
    }

    private static IResult BadRequest(string message)
        => Results.Json(new { error = message }, JsonOptions, statusCode: StatusCodes.Status400BadRequest);

    private static IResult ServiceUnavailable(string message)
        => Results.Json(new { error = message }, JsonOptions, statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult Conflict(string message)
        => Results.Json(new { error = message }, JsonOptions, statusCode: StatusCodes.Status409Conflict);

    // Never string equality: the token is a secret, so the comparison itself must not leak timing
    // information about how many leading bytes of a guess were right.
    private static bool IsAuthorized(HttpRequest request, ControlApiToken token)
    {
        var header = request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value)
            return false;

        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        return token.Matches(Encoding.UTF8.GetBytes(value[prefix.Length..]));
    }

    // TASK-UI-07: the WebSocket equivalent of IsAuthorized above. Never a query string (?token=) —
    // that lands in Kestrel's own request logging and any HTTP proxy log, exactly what the
    // never-in-a-URL rule exists to prevent — and never string equality, for the same timing-attack
    // reason IsAuthorized avoids it. context.WebSockets.WebSocketRequestedProtocols reflects every
    // value the client listed in Sec-WebSocket-Protocol; a client only ever sends one, but this scans
    // all of them rather than assuming that.
    private static bool TryMatchBearerSubProtocol(HttpContext context, ControlApiToken token, out string? subProtocol)
    {
        const string prefix = "bearer.";
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols)
        {
            if (protocol.StartsWith(prefix, StringComparison.Ordinal)
                && token.Matches(Encoding.UTF8.GetBytes(protocol[prefix.Length..])))
            {
                subProtocol = protocol;
                return true;
            }
        }

        subProtocol = null;
        return false;
    }
}

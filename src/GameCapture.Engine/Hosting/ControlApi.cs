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

        app.MapGet("/api/plugins", async () =>
        {
            if (state.Controls?.Plugins is not { } plugins)
                return Results.Json(Array.Empty<PluginRow>(), JsonOptions);

            return Results.Json(await RefreshPluginRowsAsync(plugins), JsonOptions);
        });

        app.MapPost("/api/plugins/{id}/install", (string id) => HandlePluginActionAsync(id, "install"));
        app.MapPost("/api/plugins/{id}/update", (string id) => HandlePluginActionAsync(id, "update"));
        app.MapPost("/api/plugins/{id}/uninstall", (string id) => HandlePluginActionAsync(id, "uninstall"));
        app.MapPost("/api/plugins/{id}/start", (string id) => HandlePluginActionAsync(id, "start"));
        app.MapPost("/api/plugins/{id}/stop", (string id) => HandlePluginActionAsync(id, "stop"));

        app.MapGet("/api/settings", () =>
        {
            if (state.Controls is not { } controls)
                return ServiceUnavailable("settings are unavailable");

            return Results.Json(new
            {
                settings = controls.Settings,
                ocrLanguages = controls.AvailableOcrLanguages,
                monitors = controls.MonitorLabels,
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

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
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

            // A plugin can still be managed after it drops out of the catalog (PluginsForm shows the
            // same "catalog-orphaned" entries), so an installed-but-uncatalogued id is still known.
            return plugins.Installer.State.TryGet(id, out var installed)
                ? new CatalogEntry(installed.Id, installed.Name, "", installed.DownloadUrl, installed.Channel)
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

        async Task<IReadOnlyList<PluginRow>> RefreshPluginRowsAsync(PluginServices plugins)
        {
            try
            {
                var stable = await plugins.Installer.FetchCatalogAsync(CancellationToken.None);
                var catalog = stable;
                if (plugins.Settings.IncludePreviews)
                {
                    try
                    {
                        var previews = await plugins.Installer.FetchPreviewCatalogAsync(CancellationToken.None);
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
                        var version = await plugins.Installer.ResolveLatestVersionAsync(entry, CancellationToken.None);
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
                // Network unavailable, or the catalog document was unreadable: degrade to whatever is
                // already cached (possibly empty, on the very first call) rather than failing the
                // request — this is a routine read, not worth a 500 over a flaky network.
                lock (cacheGate)
                    cachedCatalog = ControlApiPluginRows.MergeInstalled(cachedCatalog, plugins);
            }

            return BuildRowsFromCache(plugins);
        }

        async Task<IResult> HandlePluginActionAsync(string id, string action)
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
                                await RefreshPluginRowsAsync(plugins);
                                entry = FindEntry(id, plugins);
                            }
                            if (entry is null)
                                return BadRequest("unknown plugin id");
                            await plugins.Installer.InstallAsync(entry, progress: null, CancellationToken.None);
                            break;
                        }
                    case "uninstall":
                        {
                            if (!plugins.Installer.State.TryGet(id, out _))
                                return BadRequest("unknown plugin id");
                            plugins.Launcher.Stop(id);
                            plugins.Installer.Uninstall(id);
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
}

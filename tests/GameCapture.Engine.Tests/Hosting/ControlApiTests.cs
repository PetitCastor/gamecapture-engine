using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using GameCapture.Engine.Plugins;
using GameCapture.Engine.Tray;
using Xunit;

namespace GameCapture.Engine.Tests.Hosting;

/// <summary>
/// End to end over the real transport, matching <c>GrpcHostTests</c>'s own reasoning: Kestrel on a
/// real loopback socket via <see cref="EngineHost.Create"/>, a real <see cref="HttpClient"/> dialling
/// it, so a wiring mistake in <see cref="ControlApi.Map"/> — the auth middleware skipping a route, a
/// listener bound to the wrong address — shows up here instead of only in a hand-built
/// <c>TestServer</c> that configured Kestrel differently from the real engine.
/// </summary>
public class ControlApiTests
{
    [Fact]
    public async Task NoAuthorizationHeader_Returns401()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.UnauthenticatedClient();

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.ClientWithAuthorizationHeader("Bearer not-the-real-token");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MalformedAuthorizationHeader_Returns401()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        // No scheme separator and no token at all — not "Bearer <token>" in any sense.
        using var client = harness.ClientWithAuthorizationHeader("Bearer");

        var response = await client.GetAsync("/api/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Events_NoSubProtocol_RejectsTheHandshake()
    {
        await using var harness = await ControlApiHarness.StartAsync();

        // TASK-UI-07: the WebSocket upgrade authenticates via a "bearer.<token>"
        // Sec-WebSocket-Protocol entry, not the Authorization header a browser cannot set on a
        // WebSocket handshake. A routing-order regression (the /api/events bypass in ControlApi.Map's
        // middleware, or TryMatchBearerSubProtocol itself) would only show up here, not in the
        // /api/status tests above — this is a distinct code path through Kestrel's upgrade handling.
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(
            () => harness.ConnectEventsAsync(subProtocol: null));
    }

    [Fact]
    public async Task Events_WrongToken_RejectsTheHandshake()
    {
        await using var harness = await ControlApiHarness.StartAsync();

        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(
            () => harness.ConnectEventsAsync("bearer.not-the-real-token"));
    }

    [Fact]
    public async Task Events_CorrectBearerSubProtocol_AcceptsAndEchoesExactlyOneSubProtocol()
    {
        await using var harness = await ControlApiHarness.StartAsync();

        // RFC 6455: a server that accepts a subprotocol must echo exactly one back, or the browser
        // tears the connection down — this is the assertion that TryMatchBearerSubProtocol's result
        // actually reaches AcceptWebSocketAsync's SubProtocol, not just that the handshake succeeds.
        using var socket = await harness.ConnectEventsAsync();

        Assert.StartsWith("bearer.", socket.SubProtocol);
    }

    [Fact]
    public async Task Status_ReturnsTheTrayViewShape()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.GetAsync("/api/status");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Live", body.GetProperty("mode").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("tooltip").GetString()));
        Assert.False(string.IsNullOrEmpty(body.GetProperty("ocrLanguage").GetString()));
        Assert.Equal(0, body.GetProperty("plugins").GetArrayLength());
    }

    [Fact]
    public async Task Monitors_ReturnsLabelsAndCurrentIndex()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/monitors");

        Assert.Equal(0, body.GetProperty("currentIndex").GetInt32());
        Assert.Equal(
            ["Monitor 1", "Monitor 2"],
            body.GetProperty("labels").EnumerateArray().Select(element => element.GetString()));
    }

    [Fact]
    public async Task Plugins_ReturnsAnEmptyArray_WhenTheCatalogIsEmpty()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.GetAsync("/api/plugins");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Settings_ReturnsSettingsPlusOcrTagsPlusMonitors()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.GetAsync("/api/settings");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(harness.CurrentSettings.Hotkey, body.GetProperty("settings").GetProperty("hotkey").GetString());
        Assert.True(body.GetProperty("ocrLanguages").ValueKind == JsonValueKind.Array);
        Assert.Equal(
            ["Monitor 1", "Monitor 2"],
            body.GetProperty("monitors").EnumerateArray().Select(e => e.GetString()));
        Assert.False(body.GetProperty("includePreviews").GetBoolean());
    }

    [Fact]
    public async Task SaveSettings_UnavailableOcrTag_FallsBackToAuto()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var patch = harness.CurrentSettings with { OcrLanguage = "xx-not-a-real-tag" };

        var response = await client.PostAsJsonAsync("/api/settings", patch, ControlApiJson.Options);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", body.GetProperty("settings").GetProperty("ocrLanguage").GetString());
    }

    [Fact]
    public async Task SaveSettings_UnparseableHotkey_KeepsThePreviousHotkey()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var patch = harness.CurrentSettings with { Hotkey = "not a hotkey" };

        var response = await client.PostAsJsonAsync("/api/settings", patch, ControlApiJson.Options);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(harness.CurrentSettings.Hotkey, body.GetProperty("settings").GetProperty("hotkey").GetString());
    }

    [Fact]
    public async Task SaveSettings_ThemeOnlyChange_RestartPendingIsFalse()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsJsonAsync("/api/settings", new { theme = "dark" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("restartPending").GetBoolean());
        Assert.Equal("dark", body.GetProperty("settings").GetProperty("theme").GetString());
        Assert.Equal(harness.CurrentSettings.Hotkey, body.GetProperty("settings").GetProperty("hotkey").GetString());
    }

    [Fact]
    public async Task SaveSettings_ThemeOnlyChangesRemainCurrentAndCanBeReversed()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var dark = harness.CurrentSettings with { Theme = EngineTheme.Dark };
        var saveDark = await client.PostAsJsonAsync("/api/settings", dark, ControlApiJson.Options);
        saveDark.EnsureSuccessStatusCode();

        var afterDark = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("dark", afterDark.GetProperty("settings").GetProperty("theme").GetString());

        var system = dark with { Theme = EngineTheme.System };
        var saveSystem = await client.PostAsJsonAsync("/api/settings", system, ControlApiJson.Options);
        saveSystem.EnsureSuccessStatusCode();

        var afterSystem = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("system", afterSystem.GetProperty("settings").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task SaveSettings_MalformedBody_ReturnsJson400()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();
        using var content = new StringContent("{not-json");
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync("/api/settings", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid settings body", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SaveSettings_PersistenceFailure_ReturnsJson500WithoutChangingCurrentSettings()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();
        harness.BlockConfigWrites();

        var response = await client.PostAsJsonAsync("/api/settings", new { theme = "dark" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Could not save settings:", body.GetProperty("error").GetString());
        Assert.Equal(EngineTheme.System, harness.CurrentSettings.Theme);
    }

    [Fact]
    public async Task BrowseForFolder_NoInteractiveSurfaceRegistered_Returns204()
    {
        // ControlApiHarness never builds a MainWindow (see its own remarks), so TrayControls.OnBrowseFolder
        // is unset — the same "nobody can show a dialog" case a headless run hits. BrowseFolderAsync
        // must degrade to "cancelled" (204) here rather than throw.
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsJsonAsync("/api/settings/browse", new { initialDirectory = harness.CurrentSettings.OutputDir });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task BrowseForFolder_NoBody_StillReturns204()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsync("/api/settings/browse", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task BrowseForFolder_MalformedBody_ReturnsJson400()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();
        using var content = new StringContent("{not-json");
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync("/api/settings/browse", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid browse request body", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Exit_InvokesTheSharedExitCallback()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsync("/api/exit", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, harness.ExitCount);
    }

    [Fact]
    public async Task UnknownPluginId_Returns400()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsync("/api/plugins/does-not-exist/install", new StringContent(""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task UpdatePluginSettings_TogglesIncludePreviews_PersistsAndReturnsRefreshedRows()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();
        Assert.False(harness.PluginSettings.IncludePreviews);

        var response = await client.PostAsJsonAsync("/api/plugins/settings", new { includePreviews = true });

        response.EnsureSuccessStatusCode();
        Assert.True(harness.PluginSettings.IncludePreviews);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        // GET /api/settings is the checkbox's seed value on page load (TASK-UI-05 section 4) — must
        // reflect what was actually persisted, not just what this one POST's own response showed.
        var settings = await client.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.True(settings.GetProperty("includePreviews").GetBoolean());
    }

    [Fact]
    public async Task UpdatePluginSettings_MalformedBody_ReturnsJson400()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();
        using var content = new StringContent("{not-json");
        content.Headers.ContentType = new("application/json");

        var response = await client.PostAsync("/api/plugins/settings", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid settings body", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Install_DoesNotRequireFetchingPluginRowsFirst_AndCanBeUninstalled()
    {
        var handler = new FakePluginCatalogHandler();
        await using var harness = await ControlApiHarness.StartAsync(handler);
        using var client = harness.AuthorizedClient();

        var install = await client.PostAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/install",
            null);
        install.EnsureSuccessStatusCode();
        Assert.True(harness.Installer.State.TryGet(FakePluginCatalogHandler.PluginId, out _));

        var uninstall = await client.PostAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/uninstall",
            null);
        uninstall.EnsureSuccessStatusCode();
        Assert.False(harness.Installer.State.TryGet(FakePluginCatalogHandler.PluginId, out _));
    }

    [Fact]
    public async Task PluginLogs_WithoutAToken_Are401()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.UnauthenticatedClient();

        var response = await client.GetAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PluginLogs_ForAnUnknownId_Are400()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.AuthorizedClient();

        var response = await client.GetAsync("/api/plugins/no-such-plugin/logs");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unknown plugin id", body.GetProperty("error").GetString());
    }

    /// <summary>
    /// A known plugin that has never been started is not an error — the drawer needs to be able to say
    /// "no output yet" rather than render a failure.
    /// </summary>
    [Fact]
    public async Task PluginLogs_ForAPluginThatNeverStarted_ReturnAnEmptyPage()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        // Populates the catalog cache, so the id resolves without the plugin ever having run.
        await client.GetAsync("/api/plugins");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs");

        Assert.False(body.GetProperty("hasBuffer").GetBoolean());
        Assert.Empty(body.GetProperty("lines").EnumerateArray());
    }

    /// <summary>
    /// Pins the wire shape the drawer reads, including that PluginLogStream camelCases to "stdout" /
    /// "stderr" through <c>ControlApiJson.Options</c> rather than serializing as a number.
    /// </summary>
    [Fact]
    public async Task PluginLogs_ReturnSeededLinesWithCamelCaseFields()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        await client.GetAsync("/api/plugins");

        harness.Logs.Open(FakePluginCatalogHandler.PluginId);
        harness.Logs.Append(FakePluginCatalogHandler.PluginId, PluginLogStream.Stdout, "waiting for engine");
        harness.Logs.Append(FakePluginCatalogHandler.PluginId, PluginLogStream.Stderr, "invalid output configuration: nope");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs");

        Assert.True(body.GetProperty("hasBuffer").GetBoolean());
        Assert.Equal(1, body.GetProperty("nextSequence").GetInt64());
        Assert.False(body.GetProperty("truncated").GetBoolean());

        var lines = body.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].GetProperty("sequence").GetInt64());
        Assert.Equal("stdout", lines[0].GetProperty("stream").GetString());
        Assert.Equal("waiting for engine", lines[0].GetProperty("text").GetString());
        Assert.Equal("stderr", lines[1].GetProperty("stream").GetString());
        Assert.True(lines[1].TryGetProperty("timestamp", out _));
    }

    [Fact]
    public async Task PluginLogs_WithACursor_ReturnOnlyNewLines()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        await client.GetAsync("/api/plugins");

        harness.Logs.Open(FakePluginCatalogHandler.PluginId);
        foreach (var i in Enumerable.Range(0, 4))
            harness.Logs.Append(FakePluginCatalogHandler.PluginId, PluginLogStream.Stdout, $"line {i}");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs?after=1");

        var texts = body.GetProperty("lines").EnumerateArray()
            .Select(line => line.GetProperty("text").GetString())
            .ToList();
        Assert.Equal(["line 2", "line 3"], texts);
    }

    [Fact]
    public async Task PluginLogs_RespectTheLimit()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        await client.GetAsync("/api/plugins");

        harness.Logs.Open(FakePluginCatalogHandler.PluginId);
        foreach (var i in Enumerable.Range(0, 10))
            harness.Logs.Append(FakePluginCatalogHandler.PluginId, PluginLogStream.Stdout, $"line {i}");

        var body = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs?limit=3");

        Assert.Equal(3, body.GetProperty("lines").GetArrayLength());
        Assert.Equal(2, body.GetProperty("nextSequence").GetInt64());
    }

    [Fact]
    public async Task PluginLogs_NextSequenceCanBeReusedAsTheExclusiveCursor()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        await client.GetAsync("/api/plugins");

        harness.Logs.Open(FakePluginCatalogHandler.PluginId);
        foreach (var i in Enumerable.Range(0, 4))
            harness.Logs.Append(FakePluginCatalogHandler.PluginId, PluginLogStream.Stdout, $"line {i}");

        var first = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs?limit=2");
        var cursor = first.GetProperty("nextSequence").GetInt64();

        var second = await client.GetFromJsonAsync<JsonElement>(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/logs?after={cursor}");

        Assert.Equal(1, cursor);
        Assert.Equal(
            ["line 2", "line 3"],
            second.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("text").GetString()));
    }

    /// <summary>
    /// The button appears on any plugin with a buffer, running or not — that is the whole point of the
    /// buffer outliving the process — so the row's flag has to be on the wire.
    /// </summary>
    [Fact]
    public async Task PluginRows_ExposeHasLogsOnceAPluginHasABuffer()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/plugins");
        Assert.False(before.EnumerateArray().First().GetProperty("hasLogs").GetBoolean());

        harness.Logs.Open(FakePluginCatalogHandler.PluginId);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/plugins");
        Assert.True(after.EnumerateArray().First().GetProperty("hasLogs").GetBoolean());
    }

    /// <summary>
    /// Uninstalling is the one thing that ends a buffer's life before the engine exits: keeping output
    /// for a plugin the user has deleted would leave a row-less buffer nothing can ever show.
    /// </summary>
    [Fact]
    public async Task UninstallingAPlugin_DropsItsLogBuffer()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();
        harness.Logs.Open(FakePluginCatalogHandler.PluginId);

        var uninstall = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/uninstall", null);
        uninstall.EnsureSuccessStatusCode();

        Assert.False(harness.Logs.Has(FakePluginCatalogHandler.PluginId));
    }

    [Fact]
    public async Task PluginRows_ReportAutoStartOnByDefault()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/plugins");
        var row = rows.EnumerateArray().First();
        Assert.True(row.GetProperty("autoStart").GetBoolean());
        Assert.True(row.GetProperty("canSetAutoStart").GetBoolean());
    }

    /// <summary>The toggle is a choice about an install, so a catalog row with nothing on disk offers
    /// no box to tick.</summary>
    [Fact]
    public async Task PluginRow_DoesNotOfferAutoStartBeforeInstall()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/plugins");

        Assert.False(rows.EnumerateArray().First().GetProperty("canSetAutoStart").GetBoolean());
    }

    [Fact]
    public async Task TurningAutoStartOff_PersistsAndShowsOnTheRow()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart",
            new { enabled = false });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("autoStart").GetBoolean());
        Assert.False(harness.PluginSettings.IsAutoStartEnabled(FakePluginCatalogHandler.PluginId));

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/plugins");
        Assert.False(rows.EnumerateArray().First().GetProperty("autoStart").GetBoolean());
    }

    [Fact]
    public async Task TurningAutoStartBackOn_ShowsOnTheRow()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart", new { enabled = false });

        var response = await client.PostAsJsonAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart",
            new { enabled = true });

        response.EnsureSuccessStatusCode();
        Assert.True(harness.PluginSettings.IsAutoStartEnabled(FakePluginCatalogHandler.PluginId));
    }

    [Fact]
    public async Task AutoStartForAnUninstalledPlugin_Returns400()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();

        var response = await client.PostAsJsonAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart",
            new { enabled = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AutoStartWithoutAnEnabledField_Returns400()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync(
            $"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The opt-out is a decision about an install. Once that install is deleted, a later one
    /// of the same id must start from the default rather than inherit it.</summary>
    [Fact]
    public async Task UninstallingAPlugin_ForgetsItsAutoStartOptOut()
    {
        await using var harness = await ControlApiHarness.StartAsync(new FakePluginCatalogHandler());
        using var client = harness.AuthorizedClient();
        var install = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/install", null);
        install.EnsureSuccessStatusCode();
        await client.PostAsJsonAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/autostart", new { enabled = false });

        var uninstall = await client.PostAsync($"/api/plugins/{FakePluginCatalogHandler.PluginId}/uninstall", null);
        uninstall.EnsureSuccessStatusCode();

        Assert.True(harness.PluginSettings.IsAutoStartEnabled(FakePluginCatalogHandler.PluginId));
        Assert.Empty(harness.PluginSettings.AutoStartDisabledIds);
    }

    [Fact]
    public async Task ConcurrentPluginActionForTheSameId_Returns409()
    {
        var handler = new FakePluginCatalogHandler { BlockDownloads = true };
        await using var harness = await ControlApiHarness.StartAsync(handler);
        using var client = harness.AuthorizedClient();
        var route = $"/api/plugins/{FakePluginCatalogHandler.PluginId}/install";

        var firstRequest = client.PostAsync(route, null);
        await handler.DownloadStarted.WaitAsync(TimeSpan.FromSeconds(5));

        HttpResponseMessage secondResponse;
        try
        {
            secondResponse = await client.PostAsync(route, null);
        }
        finally
        {
            handler.ReleaseDownload();
        }

        using (secondResponse)
            Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        using var firstResponse = await firstRequest;
        firstResponse.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Events_SendCurrentStatusImmediately_ThenPushStatusChanges()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var socket = await harness.ConnectEventsAsync();

        var initial = await ReceiveUntilAsync(socket, "status");
        Assert.Equal("Live", initial.GetProperty("data").GetProperty("mode").GetString());

        harness.SetFrame(1280, 720, 1);
        var changed = await ReceiveUntilAsync(
            socket,
            "status",
            message => message.GetProperty("data").GetProperty("frame").GetString() == "1280x720");
        Assert.Equal("1280x720", changed.GetProperty("data").GetProperty("frame").GetString());
    }

    [Fact]
    public async Task Events_PushPluginChangesMadeOutsideTheHttpApi()
    {
        var handler = new FakePluginCatalogHandler();
        await using var harness = await ControlApiHarness.StartAsync(handler);
        using var socket = await harness.ConnectEventsAsync();
        _ = await ReceiveUntilAsync(socket, "status");

        var entry = new CatalogEntry(
            FakePluginCatalogHandler.PluginId,
            FakePluginCatalogHandler.PluginName,
            "Watches the mission board.",
            FakePluginCatalogHandler.DownloadUrl);
        await harness.Installer.InstallAsync(entry, progress: null, CancellationToken.None);

        var changed = await ReceiveUntilAsync(socket, "plugins");
        Assert.Equal(
            FakePluginCatalogHandler.PluginId,
            changed.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task EngineShutdownCompletesWithAnEventSocketConnected()
    {
        var harness = await ControlApiHarness.StartAsync();
        using var socket = await harness.ConnectEventsAsync();
        _ = await ReceiveUntilAsync(socket, "status");

        var dispose = harness.DisposeAsync().AsTask();
        try
        {
            await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            socket.Abort();
            await dispose;
        }
    }

    [Fact]
    public async Task LoopbackListener_DoesNotExposeThePipeOnlyGrpcService()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.UnauthenticatedClient();

        var response = await client.PostAsync("/capture.CaptureEngineService/GetStatus", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StaticAssetRequest_IsNotTokenGated()
    {
        await using var harness = await ControlApiHarness.StartAsync();
        using var client = harness.UnauthenticatedClient();

        var response = await client.GetAsync("/");

        // No ui/ folder next to the test assembly, so this 404s rather than serving a page — the
        // point of the assertion is that it is never 401, unlike every /api/* route above.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonElement> ReceiveUntilAsync(
        System.Net.WebSockets.ClientWebSocket socket,
        string type,
        Func<JsonElement, bool>? predicate = null)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var message = await ControlApiHarness.ReceiveEventAsync(socket);
            if (message.GetProperty("type").GetString() == type
                && (predicate is null || predicate(message)))
            {
                return message;
            }
        }

        throw new InvalidOperationException($"No '{type}' event matched the expected shape.");
    }
}

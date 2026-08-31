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
    public async Task Events_NoAuthorizationHeader_RejectsTheHandshake()
    {
        await using var harness = await ControlApiHarness.StartAsync();

        // The route middleware in ControlApi.Map gates the WebSocket upgrade the same way it gates
        // every other /api/* route, but a routing-order regression (UseWebSockets or the auth
        // middleware moved relative to the /api/events Map call) would only show up here, not in the
        // /api/status tests above — this is a distinct code path through Kestrel's upgrade handling.
        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(
            () => harness.ConnectEventsAsync(authorizationHeader: null));
    }

    [Fact]
    public async Task Events_WrongToken_RejectsTheHandshake()
    {
        await using var harness = await ControlApiHarness.StartAsync();

        await Assert.ThrowsAsync<System.Net.WebSockets.WebSocketException>(
            () => harness.ConnectEventsAsync("Bearer not-the-real-token"));
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

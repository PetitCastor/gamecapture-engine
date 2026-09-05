using System.IO.Compression;
using System.Net;
using Ocrx.Engine.Plugins;

namespace Ocrx.Engine.Tests.Hosting;

internal sealed class FakePluginCatalogHandler : HttpMessageHandler
{
    public const string PluginId = "mission-plugin";
    public const string PluginName = "MissionPlugin";
    public const string DownloadUrl = "https://github.com/PetitCastor/ocrx-plugins/releases/latest/download/MissionPlugin-win-x64.zip";
    private const string ResolvedDownloadUrl = "https://github.com/PetitCastor/ocrx-plugins/releases/download/v1.2.3/MissionPlugin-win-x64.zip";

    private static readonly string CatalogJson =
        $$"""
          [
            {
              "id": "{{PluginId}}",
              "name": "{{PluginName}}",
              "description": "Watches the mission board.",
              "downloadUrl": "{{DownloadUrl}}"
            }
          ]
          """;

    private readonly TaskCompletionSource _downloadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseDownload = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockDownloads { get; init; }
    public Task DownloadStarted => _downloadStarted.Task;

    public void ReleaseDownload() => _releaseDownload.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsoluteUri ?? "";
        if (request.Method == HttpMethod.Get && path == PluginCatalog.StableCatalogUrl)
            return Json(CatalogJson);

        if (request.Method == HttpMethod.Head && path == DownloadUrl)
            return Redirect(ResolvedDownloadUrl);

        if (request.Method == HttpMethod.Get && path == DownloadUrl)
            return Redirect(ResolvedDownloadUrl);

        if (request.Method == HttpMethod.Get && path == ResolvedDownloadUrl)
        {
            _downloadStarted.TrySetResult();
            if (BlockDownloads)
                await _releaseDownload.Task.WaitAsync(cancellationToken);
            return Zip();
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static HttpResponseMessage Redirect(string target)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(target);
        return response;
    }

    private static HttpResponseMessage Zip()
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry($"{PluginName}.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fake exe");
        }

        buffer.Position = 0;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(buffer)
            {
                Headers =
                {
                    ContentLength = buffer.Length,
                },
            },
        };
    }
}

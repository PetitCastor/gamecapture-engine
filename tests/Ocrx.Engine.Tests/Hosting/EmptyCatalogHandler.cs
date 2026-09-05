using System.Net;

namespace Ocrx.Engine.Tests.Hosting;

/// <summary>
/// Answers every request with an empty plugin catalog (<c>"[]"</c>), so <see cref="ControlApiTests"/>
/// never depends on network access or on GitHub actually serving <c>plugins.json</c>.
/// </summary>
internal sealed class EmptyCatalogHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
}

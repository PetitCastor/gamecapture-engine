using PublicApiGenerator;
using VerifyXunit;
using Xunit;

namespace GameCapture.Sdk.Overlay.Tests;

public sealed class PublicApiApprovalTests
{
    [Fact]
    public Task ApproveSdkOverlayPublicApi()
        => Verify(typeof(OverlaySinkFactory).Assembly.GeneratePublicApi());
}

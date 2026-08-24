using PublicApiGenerator;
using VerifyXunit;
using Xunit;

namespace GameCapture.Sdk.Tests;

public sealed class PublicApiApprovalTests
{
    [Fact]
    public Task ApproveSdkPublicApi()
        => Verify(typeof(IGameCapturePlugin).Assembly.GeneratePublicApi(new ApiGeneratorOptions
        {
            ExcludeAttributes =
            [
                "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute",
            ],
        }));
}

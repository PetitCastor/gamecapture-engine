using PublicApiGenerator;
using VerifyXunit;
using Xunit;

namespace GameCapture.Contracts.Tests;

public sealed class PublicApiApprovalTests
{
    [Fact]
    public Task ApproveContractsPublicApi()
        => Verify(typeof(RoiScaler).Assembly.GeneratePublicApi());
}

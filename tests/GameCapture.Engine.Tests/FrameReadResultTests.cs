using Xunit;

namespace GameCapture.Engine.Tests;

public sealed class FrameReadResultTests
{
    [Fact]
    public void Default_IsIdleWithoutPayload()
    {
        var read = default(FrameReadResult);

        Assert.Equal(FrameReadStatus.Idle, read.Status);
        Assert.Null(read.Bitmap);
    }
}

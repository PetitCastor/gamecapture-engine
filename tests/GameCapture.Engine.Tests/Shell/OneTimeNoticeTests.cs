using GameCapture.Engine.Shell;
using Xunit;

namespace GameCapture.Engine.Tests.Shell;

/// <summary>Pins the "fires exactly once, ever, even across a restart" contract the close-to-tray
/// balloon notice depends on.</summary>
public class OneTimeNoticeTests
{
    [Fact]
    public void FirstCall_Fires_AndReturnsTrue()
    {
        var fired = false;
        var notice = new OneTimeNotice(alreadyShown: false);

        var result = notice.TryFire(() => fired = true);

        Assert.True(result);
        Assert.True(fired);
        Assert.True(notice.HasShown);
    }

    [Fact]
    public void SecondCall_DoesNotFireAgain()
    {
        var fireCount = 0;
        var notice = new OneTimeNotice(alreadyShown: false);

        notice.TryFire(() => fireCount++);
        var secondResult = notice.TryFire(() => fireCount++);

        Assert.False(secondResult);
        Assert.Equal(1, fireCount);
    }

    [Fact]
    public void SeededAsAlreadyShown_NeverFires()
        // Models a restart after a prior run already showed the notice: the persisted flag must
        // suppress it for the rest of this instance's lifetime too.
    {
        var fired = false;
        var notice = new OneTimeNotice(alreadyShown: true);

        var result = notice.TryFire(() => fired = true);

        Assert.False(result);
        Assert.False(fired);
        Assert.True(notice.HasShown);
    }
}

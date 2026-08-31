using GameCapture.Engine.Shell;
using Xunit;

namespace GameCapture.Engine.Tests.Shell;

/// <summary>
/// Pins the mutex/event handoff in isolation from any window or UI thread. Each test uses its own
/// unique scope so the named kernel objects can never collide with a concurrently running test, a
/// parallel test class, or a real engine instance that happens to be running on the same machine.
/// </summary>
public sealed class SingleInstanceTests
{
    private static string UniqueScope([System.Runtime.CompilerServices.CallerMemberName] string member = "")
        => $"GameCapture.Engine.Tests.{member}.{Guid.NewGuid():N}";

    [Fact]
    public void Acquire_FirstCall_ReturnsAnInstance()
    {
        var scope = UniqueScope();

        using var first = SingleInstance.Acquire(scope);

        Assert.NotNull(first);
    }

    [Fact]
    public void Acquire_SecondCall_ReturnsNullAndSignalsTheFirst()
    {
        var scope = UniqueScope();
        using var first = SingleInstance.Acquire(scope);
        Assert.NotNull(first);

        using var signaled = new ManualResetEventSlim(false);
        first!.Signaled += () => signaled.Set();

        var second = SingleInstance.Acquire(scope);

        Assert.Null(second);
        Assert.True(signaled.Wait(TimeSpan.FromSeconds(5)), "the first instance was never signalled");
    }

    [Fact]
    public void Acquire_SecondCall_GrantsForegroundPermissionToTheFirstProcess()
    {
        var scope = UniqueScope();
        using var first = SingleInstance.Acquire(scope);
        Assert.NotNull(first);
        int? grantedProcessId = null;

        var second = SingleInstance.Acquire(scope, processId => grantedProcessId = processId);

        Assert.Null(second);
        Assert.Equal(Environment.ProcessId, grantedProcessId);
    }

    [Theory]
    [InlineData("--replay")]
    [InlineData("--REPLAY")]
    [InlineData("--video")]
    public void IsRequiredFor_HeadlessLaunch_ReturnsFalse(string sourceArgument)
        => Assert.False(SingleInstance.IsRequiredFor([sourceArgument, "source-path"]));

    [Theory]
    [InlineData()]
    [InlineData("--verbose")]
    [InlineData("--pipe", "custom-pipe")]
    public void IsRequiredFor_InteractiveLaunch_ReturnsTrue(params string[] arguments)
        => Assert.True(SingleInstance.IsRequiredFor(arguments));

    [Fact]
    public void Acquire_AfterFirstIsDisposed_CanClaimTheSameScopeAgain()
    {
        var scope = UniqueScope();
        var first = SingleInstance.Acquire(scope);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.Acquire(scope);

        Assert.NotNull(second);
    }

    [Fact]
    public void Acquire_DifferentScopes_DoNotInterfere()
    {
        using var a = SingleInstance.Acquire(UniqueScope());
        using var b = SingleInstance.Acquire(UniqueScope());

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Signaled_NeverFiresForTheFirstInstanceOnItsOwn()
    {
        var scope = UniqueScope();
        using var first = SingleInstance.Acquire(scope);
        var fired = false;
        first!.Signaled += () => fired = true;

        // Nothing else ever calls Acquire(scope) here — the point is that simply holding the
        // instance, with no second launch, must never spuriously fire the handoff.
        Thread.Sleep(50);

        Assert.False(fired);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var first = SingleInstance.Acquire(UniqueScope());
        Assert.NotNull(first);

        first!.Dispose();
        var exception = Record.Exception(() => first.Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public async Task Dispose_FromADifferentThreadThanAcquire_DoesNotThrow()
    {
        // Program.cs's top-level async Main acquires the mutex before any await, but its top-level
        // `using` disposes it after several awaits have resumed the continuation on an arbitrary
        // thread-pool thread — never guaranteed to be the thread that called Acquire. A Mutex only
        // allows the exact owning OS thread to ReleaseMutex; Dispose must not attempt that.
        var instance = SingleInstance.Acquire(UniqueScope());
        Assert.NotNull(instance);

        var exception = await Task.Run(() => Record.Exception(() => instance!.Dispose()));

        Assert.Null(exception);
    }
}

using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Serialized owner of the frame unary RPCs borrow. The store exposes the same gate/read pair the
/// gRPC layer already relies on, but keeps replacement, disposal, and manual-frame callbacks in
/// one place.
/// </summary>
internal sealed class RetainedFrameStore : IDisposable
{
    private readonly Action<string> _writeLine;
    private SoftwareBitmap? _frame;

    public RetainedFrameStore(Action<string> writeLine)
    {
        _writeLine = writeLine;
    }

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public SoftwareBitmap? Frame => _frame;

    /// <remarks>
    /// Deliberately not cancellable: the gate is only ever held for the length of one unary RPC,
    /// and bailing out here would leave the frame owned by nobody.
    /// </remarks>
    public async Task SwapAsync(
        SoftwareBitmap bitmap,
        Func<SoftwareBitmap, Task>? manualFrameHandler)
    {
        await Gate.WaitAsync();
        try
        {
            _frame?.Dispose();
            _frame = bitmap;

            if (manualFrameHandler is null)
                return;

            try
            {
                await manualFrameHandler(bitmap);
            }
            catch (Exception ex)
            {
                _writeLine($"[frames] failed to save frame: {ex.Message}");
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public void Dispose()
    {
        Gate.Wait();
        try
        {
            _frame?.Dispose();
            _frame = null;
        }
        finally
        {
            Gate.Release();
        }

        Gate.Dispose();
    }
}

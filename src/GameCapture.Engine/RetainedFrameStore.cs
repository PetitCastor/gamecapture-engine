using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// Owns the frame unary RPCs borrow. Replacement only holds the ownership lock long enough to
/// exchange references; bounded leases keep superseded bitmaps alive while expensive reads finish.
/// </summary>
internal sealed class RetainedFrameStore : IDisposable
{
    // Two unary/manual reads may overlap, but no more than two superseded frames can remain alive
    // through active operations. This removes the serialized bottleneck without unbounded memory.
    private const int MaxConcurrentOperations = 2;

    private readonly Action<string> _writeLine;
    private readonly Lock _ownershipLock = new();
    private readonly SemaphoreSlim _operationGate = new(MaxConcurrentOperations, MaxConcurrentOperations);
    private RetainedFrameEntry? _frame;
    private bool _disposed;

    public RetainedFrameStore(Action<string> writeLine)
    {
        _writeLine = writeLine;
    }

    public async ValueTask<RetainedFrameLease?> AcquireLeaseAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        var releaseOperation = true;
        try
        {
            lock (_ownershipLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_frame is null)
                    return null;

                var lease = _frame.AddLease(_operationGate);
                releaseOperation = false;
                return lease;
            }
        }
        finally
        {
            if (releaseOperation)
                _operationGate.Release();
        }
    }

    /// <remarks>
    /// Deliberately not cancellable: once the source hands the bitmap over, either the store must
    /// retain it or the scan loop must observe the failure and dispose it.
    /// </remarks>
    public Task SwapAsync(
        SoftwareBitmap bitmap,
        Func<SoftwareBitmap, Task>? manualFrameHandler)
    {
        if (manualFrameHandler is not null)
            return SwapAndHandleManualFrameAsync(bitmap, manualFrameHandler);

        RetainedFrameEntry? superseded;
        lock (_ownershipLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            superseded = _frame;
            _frame = new RetainedFrameEntry(bitmap);
        }

        superseded?.Release();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        RetainedFrameEntry? retained;
        lock (_ownershipLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            retained = _frame;
            _frame = null;
        }

        // Do not dispose the semaphore: a waiter admitted during shutdown must be able to observe
        // _disposed and return its permit. SemaphoreSlim disposal is not safe against those waits.
        retained?.Release();
    }

    private async Task SwapAndHandleManualFrameAsync(
        SoftwareBitmap bitmap,
        Func<SoftwareBitmap, Task> manualFrameHandler)
    {
        await _operationGate.WaitAsync();
        RetainedFrameLease? lease = null;
        try
        {
            RetainedFrameEntry? superseded;
            lock (_ownershipLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                superseded = _frame;
                _frame = new RetainedFrameEntry(bitmap);
                lease = _frame.AddLease(_operationGate);
            }

            superseded?.Release();
        }
        catch
        {
            if (lease is null)
                _operationGate.Release();
            else
                lease.Dispose();

            throw;
        }

        try
        {
            await manualFrameHandler(lease.Bitmap);
        }
        catch (Exception ex)
        {
            _writeLine($"[frames] failed to save frame: {ex.Message}");
        }
        finally
        {
            lease.Dispose();
        }
    }
}

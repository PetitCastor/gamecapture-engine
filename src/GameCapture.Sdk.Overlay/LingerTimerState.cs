namespace GameCapture.Sdk.Overlay;

internal sealed class LingerTimerState
{
    private nuint _next;

    public nuint Current { get; private set; }

    public nuint Reset()
    {
        unchecked
        {
            _next++;
            if (_next == 0)
                _next++;
        }

        Current = _next;
        return Current;
    }

    public bool IsCurrent(nuint timerId) => timerId != 0 && timerId == Current;

    public void Clear() => Current = 0;
}

namespace Ocrx.Engine;

internal sealed class FrameSourceCreationResult
{
    private FrameSourceCreationResult(FrameSourceSelection selection)
    {
        Selection = selection;
        Succeeded = true;
    }

    private FrameSourceCreationResult(string error)
    {
        Error = error;
        Succeeded = false;
    }

    public bool Succeeded { get; }

    public FrameSourceSelection? Selection { get; }

    public string? Error { get; }

    public static FrameSourceCreationResult Success(FrameSourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return new FrameSourceCreationResult(selection);
    }

    public static FrameSourceCreationResult Failure(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return new FrameSourceCreationResult(error);
    }
}

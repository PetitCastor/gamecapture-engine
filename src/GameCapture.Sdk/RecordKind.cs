namespace GameCapture.Sdk;

/// <summary>What a <see cref="CaptureRecord"/> represents in a tracked reading's lifecycle.</summary>
public enum RecordKind
{
    /// <summary>The current reading. Default; every pre-existing emit is this.</summary>
    Observation,

    /// <summary>The reading is gone — drives overlay hide. Carries no payload.</summary>
    Cleared,
}

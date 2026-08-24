using GameCapture.Contracts.Proto;

namespace GameCapture.Contracts;

internal static class RoiResultValidator
{
    public static void ValidateForOcr(RoiResult result)
    {
        ThrowIfEngineError(result);

        if (result.Kind == RoiResultKind.Pixels)
            throw WrongKind(result, "OCR");

        // effective_scale is engine output and is always > 0 on a successful result. A 0 here
        // means the engine never set the field, and ToFramePoint would divide by it: the double
        // division yields infinity and the unchecked cast to int yields int.MinValue, so the
        // plugin would get plausible-looking coordinates that are catastrophically wrong.
        if (!(result.EffectiveScale > 0))
            throw new RoiResultException(result.RoiId,
                $"effective_scale must be > 0 on a successful result (was {result.EffectiveScale}).",
                reportedByEngine: false);
    }

    public static void ValidateForPixels(RoiResult result)
    {
        ThrowIfEngineError(result);

        if (result.Kind is RoiResultKind.Text or RoiResultKind.Detailed)
            throw WrongKind(result, "pixel");
    }

    private static void ThrowIfEngineError(RoiResult result)
    {
        if (result.Error)
            throw new RoiResultException(result.RoiId,
                result.ErrorMessage.Length > 0
                    ? result.ErrorMessage
                    : "the engine reported a ROI failure.",
                reportedByEngine: true);
    }

    /// <summary>
    /// A result read as the mode it does not answer. Worth an exception rather than a best
    /// effort because the fields of the mode that was NOT filled are all proto3 defaults, and
    /// those defaults are indistinguishable from real readings: a PIXELS result read as OCR is
    /// an empty panel, and a TEXT result read as pixels is a valid 0x0 patch whose every sample
    /// clamps to black. Both keep a plugin's state machine running on a reading that never
    /// existed, and neither sets <c>error</c>, so nothing else would ever flag it.
    /// </summary>
    /// <remarks><see cref="RoiResultKind.Unspecified"/> is not a mismatch — it is an engine
    /// older than the field, and the payload checks still cover malformed data.</remarks>
    private static RoiResultException WrongKind(RoiResult result, string reading)
        => new(result.RoiId,
            $"result is {result.Kind} and cannot be read as {reading}; the subscription's RoiMode " +
            $"does not match how '{result.RoiId}' is being looked up on the tick.",
            reportedByEngine: false);
}

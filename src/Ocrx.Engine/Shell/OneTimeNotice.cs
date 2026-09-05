namespace Ocrx.Engine.Shell;

/// <summary>
/// Fires a callback at most once across the notice's lifetime. Seeded from a persisted "already
/// shown" flag so the latch survives a restart — used for the close-to-tray balloon tip, which the
/// spec requires firing exactly once, ever, not once per process launch.
/// </summary>
internal sealed class OneTimeNotice(bool alreadyShown)
{
    private bool _shown = alreadyShown;

    /// <summary>Whether the notice has fired, this run or a prior one.</summary>
    public bool HasShown => _shown;

    /// <summary>Fires <paramref name="notify"/> and latches if this is the first call ever; a no-op
    /// otherwise. Returns whether it fired.</summary>
    public bool TryFire(Action notify)
    {
        if (_shown)
            return false;

        _shown = true;
        notify();
        return true;
    }
}

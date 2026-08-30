namespace GameCapture.Engine.Plugins;

/// <summary>Distribution channel a plugin release belongs to.</summary>
public enum ReleaseChannel
{
    /// <summary>Production-ready plugins shown to every user.</summary>
    Stable,

    /// <summary>Opt-in prerelease plugins shown only when preview access is enabled.</summary>
    Preview,
}

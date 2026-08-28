namespace GameCapture.Engine.Plugins;

/// <summary>
/// Recovers a release tag from the URL a <c>releases/latest/download/…</c> link redirects to.
/// </summary>
/// <remarks>
/// The catalog's download URLs are version-less by design, so the engine has to learn the version
/// some other way to answer "is there an update?". GitHub answers it for free: a request for the
/// latest-download URL redirects to <c>…/releases/download/v1.0.4/&lt;asset&gt;</c>, which names the tag.
/// Reading it out of that <c>Location</c> costs one HEAD and, unlike the releases API, is neither
/// rate-limited nor dependent on a catalog schema change.
/// </remarks>
public static class ReleaseVersionResolver
{
    private const string DownloadSegment = "download";

    /// <summary>
    /// Extracts the release tag from a versioned release-asset URL, e.g.
    /// <c>https://github.com/PetitCastor/gamecapture-plugins/releases/download/v1.0.4/X.zip</c> → <c>v1.0.4</c>.
    /// Returns false for the version-less <c>latest/download</c> form and for anything unrecognised —
    /// an unknown version reads as "no update information", never as an update.
    /// </summary>
    public static bool TryExtractTag(Uri assetUri, out string tag)
    {
        tag = "";

        var segments = assetUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (!string.Equals(segments[i], DownloadSegment, StringComparison.Ordinal))
                continue;

            var candidate = Uri.UnescapeDataString(segments[i + 1]);
            if (!IsTagLike(candidate))
                return false;

            tag = candidate;
            return true;
        }

        return false;
    }

    // Release tags on both repositories are vX.Y.Z. Kept a shape check rather than a strict version
    // parse so a future pre-release suffix still reads through; "latest" deliberately fails it.
    private static bool IsTagLike(string candidate)
    {
        if (candidate.Length is < 2 or > 64 || candidate[0] is not ('v' or 'V') || !char.IsAsciiDigit(candidate[1]))
            return false;

        foreach (var c in candidate.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+'))
                return false;
        }

        return true;
    }
}

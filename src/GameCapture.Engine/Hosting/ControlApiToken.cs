using System.Security.Cryptography;
using System.Text;

namespace GameCapture.Engine;

/// <summary>
/// Bearer token gating the loopback control API (TASK-UI-03). Generated fresh with
/// <see cref="RandomNumberGenerator"/> once per process launch and held only in memory: it is never
/// written to disk, logged, or placed in a URL or query string, so a leaked log line or crash dump
/// can never hand out control of a running engine. <see cref="Matches"/> compares in fixed time so a
/// byte-by-byte timing attack cannot narrow the token down.
/// </summary>
internal sealed class ControlApiToken
{
    private readonly byte[] _expectedUtf8;

    public ControlApiToken()
    {
        _expectedUtf8 = Encoding.ASCII.GetBytes(Base64UrlEncode(RandomNumberGenerator.GetBytes(32)));
    }

    /// <summary>
    /// The bearer value exactly as a client must send it. Exposed only for trusted in-process
    /// consumers that must inject it (the WebView2 window, TASK-UI-04) — never log, persist, or
    /// place this in a URL.
    /// </summary>
    public string Value => Encoding.ASCII.GetString(_expectedUtf8);

    /// <summary>Fixed-time comparison against the UTF-8 bytes of a candidate token.</summary>
    public bool Matches(ReadOnlySpan<byte> candidateUtf8)
        => candidateUtf8.Length == _expectedUtf8.Length
           && CryptographicOperations.FixedTimeEquals(candidateUtf8, _expectedUtf8);

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

using System.Diagnostics;
using System.Text;

namespace GameCapture.Engine.Plugins;

/// <summary>
/// The two decisions behind reading a plugin's console output: how the child's streams are redirected
/// and how their bytes are decoded.
/// </summary>
/// <remarks>
/// This lives outside <see cref="PluginLauncher"/> on purpose. The launcher is the process edge and is
/// excluded from the coverage gate, so anything there is untested by construction; these are ordinary
/// decisions that deserve tests, and only the <c>Start</c>/<c>BeginOutputReadLine</c> sequencing is
/// genuinely untestable.
/// </remarks>
internal static class PluginProcessCapture
{
    // Deterministic rather than faithful, and the difference is worth stating.
    //
    // Measured: a redirected .NET child reports Console.OutputEncoding = the console output code page
    // (437 on the machine this was written on), not UTF-8. It best-fit-maps what it can on the way out,
    // so the SDK's own em dash in "=== GameCapture — {name} ===" reaches us as an ASCII hyphen; α and é
    // leave as single CP437 bytes. The loss happens in the child, before the engine sees anything.
    //
    // Decoding those bytes exactly is not on offer: Encoding.GetEncoding(437) throws
    // NotSupportedException without the System.Text.Encoding.CodePages package, and TextInfo.OEMCodePage
    // reports a different page (850) than the console actually uses. Leaving this null would decode with
    // the engine's own Console.OutputEncoding, which is the console code page in a debug run that
    // allocated a console and UTF-8 in a normal windowless one — the same plugin rendering differently
    // depending on how the engine was started. Pinning UTF-8 keeps ASCII exact and turns a stray high
    // byte into U+FFFD instead of a plausible-looking wrong character.
    //
    // The real fix is Console.OutputEncoding = Encoding.UTF8 in the SDK's plugin host, which is a
    // separate change to a separately versioned package.
    private static readonly Encoding CaptureEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Redirects the child's output streams so the engine can read them.</summary>
    internal static void Configure(ProcessStartInfo startInfo)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardOutputEncoding = CaptureEncoding;
        startInfo.StandardErrorEncoding = CaptureEncoding;

        // Standard input stays inherited. Redirecting it without ever closing the write end would hang
        // any plugin that reads from it, and nothing here wants to write to a plugin.
    }

    /// <summary>
    /// Points both streams at <paramref name="buffer"/>. Must be called before
    /// <see cref="Process.Start()"/>: a handler attached afterwards misses whatever the child has
    /// already written, which on the crash path is the whole message.
    /// </summary>
    /// <remarks>
    /// The handlers deliberately capture nothing but the buffer and the stream. A callback can still
    /// arrive after the process has been disposed, and one that touched the <see cref="Process"/> — or
    /// took the launcher's lock — would be a race or a deadlock; one that only appends to a buffer
    /// designed to outlive the process has nothing to race with. Keeping this a static method with two
    /// parameters makes that structural rather than a comment someone can edit past.
    /// </remarks>
    internal static void Attach(Process process, PluginLogBuffer buffer)
    {
        process.OutputDataReceived += (_, e) => buffer.Append(PluginLogStream.Stdout, e.Data);
        process.ErrorDataReceived += (_, e) => buffer.Append(PluginLogStream.Stderr, e.Data);
    }
}

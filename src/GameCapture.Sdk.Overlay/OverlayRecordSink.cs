using System.Text.RegularExpressions;
using GameCapture.Sdk;

namespace GameCapture.Sdk.Overlay;

/// <summary>
/// Displays the latest observation through a click-through desktop window and hides it on a clear.
/// Construct through <see cref="OverlaySinkFactory"/> so unsupported platforms fail closed to a
/// no-op sink.
/// </summary>
public sealed class OverlayRecordSink : IRecordSink
{
    private static readonly Regex Placeholder = new(
        "\\{(?<key>[^{}]+)\\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly OverlaySpec _options;
    private readonly IOverlayWindow _window;
    private readonly TimeSpan _linger;

    internal OverlayRecordSink(OverlaySpec options, IOverlayWindow window)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(window);
        Validate(options);

        _options = options;
        _window = window;
        _linger = TimeSpan.FromMilliseconds(options.LingerMs);
        _window.Start();
    }

    /// <inheritdoc />
    public ValueTask EmitAsync(CaptureRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (ct.IsCancellationRequested)
            return ValueTask.CompletedTask;

        if (record.Kind == RecordKind.Cleared)
            _window.Hide();
        else
            _window.Show(Render(_options.Template, record), _linger);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _window.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Render(string template, CaptureRecord record)
    {
        if (string.IsNullOrWhiteSpace(template))
            return record.RawText;

        var missing = false;
        var rendered = Placeholder.Replace(template, match =>
        {
            var key = match.Groups["key"].Value;
            if (record.Fields?.TryGetValue(key, out var value) == true)
                return value;

            missing = true;
            return match.Value;
        });

        return missing ? record.RawText : rendered;
    }

    private static void Validate(OverlaySpec options)
    {
        if (options.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay width must be positive");
        if (options.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay height must be positive");
        if (options.FontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay font size must be positive");
        if (options.BackgroundAlpha is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay background alpha must be 0..255");
        if (options.CornerRadius < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay corner radius cannot be negative");
        if (options.Padding < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay padding cannot be negative");
        if (options.LingerMs < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "overlay lingerMs cannot be negative");
        if (string.IsNullOrWhiteSpace(options.FontFamily))
            throw new ArgumentException("overlay font family must not be blank", nameof(options));
    }
}

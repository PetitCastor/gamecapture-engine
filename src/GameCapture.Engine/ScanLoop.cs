using GameCapture.Contracts;
using GameCapture.Contracts.Proto;
using Google.Protobuf;
using Windows.Graphics.Imaging;

namespace GameCapture.Engine;

/// <summary>
/// The engine's single thread of control: one frame at a time, OCR every subscribed ROI against
/// that one frame, hand each client a complete <see cref="TickResult"/>, repeat. All OCR in the
/// process happens here — that is what makes per-tick atomicity a structural property rather than
/// a convention, since no other code path can interleave a read from a different frame.
/// </summary>
internal sealed class ScanLoop : IDisposable
{
    /// <summary>Retry cadence while the screen is idle and WGC produces no new frames (carried over
    /// from the monolith's scan loop).</summary>
    private static readonly TimeSpan IdleRetry = TimeSpan.FromMilliseconds(200);

    /// <summary>Hand-edited config can hold 0; never let that become a tight OCR loop.</summary>
    private static readonly TimeSpan MinScanInterval = TimeSpan.FromMilliseconds(100);

    private readonly IFrameSource _source;
    private readonly OcrPipeline _ocr;
    private readonly SubscriptionRegistry _registry;
    private readonly EngineStatus _status;
    private readonly ConsoleSink _sink;
    private readonly TimeSpan _scanInterval;
    private readonly SubscriptionTickProcessor _tickProcessor;
    private readonly RetainedFrameStore _retainedFrameStore;
    private readonly bool _verbose;

    private ulong _seq;
    private int _manualFlag;
    private int _lastFrameWidth, _lastFrameHeight;
    private Func<SoftwareBitmap, Task>? _manualFrameHandler;

    public ScanLoop(
        IFrameSource source,
        OcrPipeline ocr,
        SubscriptionRegistry registry,
        EngineStatus status,
        ConsoleSink sink,
        EngineConfig config,
        bool verbose)
    {
        _source = source;
        _ocr = ocr;
        _registry = registry;
        _status = status;
        _sink = sink;
        _verbose = verbose;

        var configured = TimeSpan.FromMilliseconds(config.ScanIntervalMs);
        _scanInterval = configured < MinScanInterval ? MinScanInterval : configured;
        _tickProcessor = new SubscriptionTickProcessor(source.Mode, ReadOneAsync);
        _retainedFrameStore = new RetainedFrameStore(sink.WriteLine);
    }

    /// <summary>
    /// The cadence this loop actually sleeps for — the configured interval after the
    /// <see cref="MinScanInterval"/> clamp. Reported in GetStatus so a plugin counting ticks does
    /// not have to assume the default; the clamp is why it is read from here rather than from the
    /// config, which can hold a 0 the loop would never honour.
    /// </summary>
    public TimeSpan ScanInterval => _scanInterval;

    /// <summary>
    /// Marks the next tick as manually triggered. Called from the hotkey listener's hook thread,
    /// which must return fast — hence a flag the loop picks up rather than any work done here.
    /// </summary>
    public void TriggerManual() => Interlocked.Exchange(ref _manualFlag, 1);

    /// <summary>
    /// Marks the next tick as manual and invokes <paramref name="onFrame"/> with that tick's
    /// retained bitmap while the bounded retained-frame operation gate is held.
    /// </summary>
    public void TriggerManual(Func<SoftwareBitmap, Task> onFrame)
    {
        Interlocked.Exchange(ref _manualFrameHandler, onFrame);
        Interlocked.Exchange(ref _manualFlag, 1);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_source.Mode.UsesReplayFlow())
                await _registry.WaitForAnySubscribedAsync(ct); // don't burn frames into the void

            while (!ct.IsCancellationRequested)
            {
                var read = await _source.ReadFrameAsync(ct);
                SoftwareBitmap bitmap;
                switch (read.Status)
                {
                    case FrameReadStatus.Idle when read.Bitmap is null:
                        await Task.Delay(IdleRetry, ct);
                        continue;

                    case FrameReadStatus.FrameReady when read.Bitmap is { } frame:
                        bitmap = frame;
                        break;

                    case FrameReadStatus.EndOfStream when read.Bitmap is null:
                        return; // finite source exhausted

                    default:
                        read.Bitmap?.Dispose();
                        throw new InvalidOperationException(
                            $"Frame source returned invalid {read.Status} status/payload combination.");
                }
                _seq++;

                // Read once for the whole tick: two clients must not disagree about whether the
                // hotkey fired on this frame.
                var manual = Interlocked.Exchange(ref _manualFlag, 0) == 1;
                var manualFrameHandler = manual
                    ? Interlocked.Exchange(ref _manualFrameHandler, null)
                    : null;

                LogFrameSizeChanges(bitmap);

                var clients = _registry.Snapshot();

                // The retained frame takes ownership of the bitmap; until then this loop owns it
                // and must release it if the tick is abandoned (cancellation mid-distribution).
                var retained = false;
                try
                {
                    await _tickProcessor.ProcessAsync(clients, bitmap, _seq, manual, ct);
                    await _retainedFrameStore.SwapAsync(bitmap, manualFrameHandler);
                    retained = true;
                }
                finally
                {
                    if (!retained)
                        bitmap.Dispose();
                }

                _status.OnFrame((uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, _seq);

                if (_verbose)
                    _sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] tick {_seq}{DescribeSourceFrame()}"
                        + $" -> {clients.Count} client(s){(manual ? " (manual)" : "")}");

                // Replay runs flat out: the corpus is finite and the cadence is a live-capture
                // concern, not a semantic one.
                if (!_source.Mode.UsesReplayFlow())
                    await Task.Delay(_scanInterval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl+C / host shutdown. A normal exit, not a fault.
            return;
        }
        finally
        {
            // Replay end or engine shutdown: either way no further tick will be produced, so let
            // every Track stream complete instead of leaving plugins waiting on a dead engine.
            _registry.CompleteAll();
        }
    }

    /// <summary>
    /// Reads one ROI against one frame. Never throws: a bad ROI is that client's problem and must
    /// not take down the tick — every other result in the tick, for this client and all others,
    /// stays valid. Failures come back as <c>error</c> results with every payload field unset,
    /// because an empty text field is indistinguishable from a successfully read empty panel.
    /// </summary>
    internal async Task<RoiResult> ReadOneAsync(SoftwareBitmap bitmap, RoiSpec spec)
    {
        try
        {
            var reference = (spec.Rect ?? new Rect()).ToRoiRect();
            var width = bitmap.PixelWidth;
            var height = bitmap.PixelHeight;

            EnsureRoiInFrame(reference, width, height);

            var frameRect = RoiScaler.ToFrame(reference, width, height);
            var bounds = OcrPipeline.ClampToBitmap(frameRect.ToBounds(), width, height);
            var scale = WireLimits.NormalizeOcrScale(spec.Scale);

            var result = new RoiResult
            {
                RoiId = spec.Id,
                FrameRect = bounds.ToRoiRect().ToProto(),
            };

            switch (spec.Mode)
            {
                case RoiMode.Text:
                    result.Kind = RoiResultKind.Text;
                    result.Text = await _ocr.ReadRegionAsync(bitmap, bounds, scale);
                    result.EffectiveScale = OcrPipeline.EffectiveScale(bounds, scale);
                    break;

                case RoiMode.Detailed:
                    result.Kind = RoiResultKind.Detailed;
                    // ReadRegionDetailedAsync treats the rect it is given as the ROI origin, and
                    // it is given FRAME-space bounds — which is exactly what frame_rect promises,
                    // so OcrRegionResult.ToFramePoint yields real frame pixels on the far side.
                    result.FillFrom(await _ocr.ReadRegionDetailedAsync(bitmap, bounds, scale));
                    break;

                case RoiMode.Pixels:
                    result.Kind = RoiResultKind.Pixels;
                    if (!WireLimits.FitsPixelBudget(bounds.Width, bounds.Height))
                        throw new ArgumentOutOfRangeException(nameof(spec),
                            $"PIXELS ROI {bounds.Width}x{bounds.Height} exceeds the " +
                            $"{WireLimits.MaxPixelBytes / 1024} KiB per-ROI payload cap.");

                    var strip = await PixelStrip.CaptureAsync(_ocr, bitmap, bounds);
                    result.PixelsBgra = ByteString.CopyFrom(strip.Bgra);
                    result.PixelsStride = (uint)strip.Stride;
                    result.PixelsWidth = (uint)strip.Width;
                    result.PixelsHeight = (uint)strip.Height;
                    result.EffectiveScale = 1.0; // PIXELS is always captured 1:1
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(spec), $"Unknown ROI mode '{spec.Mode}'.");
            }

            return result;
        }
        catch (Exception ex)
        {
            // Deliberately a fresh result: the contract says every payload field is unset on an
            // error, so a half-filled one must not escape.
            return new RoiResult { RoiId = spec.Id, Error = true, ErrorMessage = ex.Message };
        }
    }

    public void Dispose()
        => _retainedFrameStore.Dispose();

    internal ValueTask<RetainedFrameLease?> AcquireRetainedFrameLeaseAsync(CancellationToken cancellationToken)
        => _retainedFrameStore.AcquireLeaseAsync(cancellationToken);

    /// <summary>
    /// Rejects a reference-space ROI that cannot touch the frame at all, instead of letting
    /// <see cref="RoiScaler.ToFrame"/> silently clamp it to a meaningless 1-pixel sliver at the
    /// edge — a read that succeeds and means nothing. Shared by every caller that turns a client
    /// ROI into a frame crop (<see cref="ReadOneAsync"/> and DumpFrame's crop path alike), so a
    /// mistyped constant is told, not fed, no matter which RPC it arrived on.
    /// </summary>
    internal static void EnsureRoiInFrame(RoiRect reference, int frameWidth, int frameHeight)
    {
        if (!LiesOutsideFrame(reference, frameWidth, frameHeight))
            return;

        throw new ArgumentOutOfRangeException(nameof(reference),
            $"ROI {reference.Width}x{reference.Height} at {reference.X},{reference.Y} (reference space) " +
            $"lies outside the {frameWidth}x{frameHeight} frame.");
    }

    /// <summary>
    /// Reference-space rect that cannot touch the frame at all. Both coordinates are unsigned, so
    /// "off the top/left" is impossible and only the far edges need checking.
    /// </summary>
    private static bool LiesOutsideFrame(RoiRect rect, int frameWidth, int frameHeight)
    {
        // Doubles rather than RoiScaler.ToFrameX/Y: a client rect is uint32 and casting a wild
        // value to int would wrap into a coordinate that looks in-bounds.
        var x = rect.X * (double)frameWidth / RoiScaler.ReferenceWidth;
        var y = rect.Y * (double)frameHeight / RoiScaler.ReferenceHeight;

        return rect.Width == 0 || rect.Height == 0 || x >= frameWidth || y >= frameHeight;
    }

    /// <summary>Logs the capture size on the first frame and again if it changes (window resize).</summary>
    private void LogFrameSizeChanges(SoftwareBitmap bitmap)
    {
        if (bitmap.PixelWidth == _lastFrameWidth && bitmap.PixelHeight == _lastFrameHeight)
            return;

        (_lastFrameWidth, _lastFrameHeight) = (bitmap.PixelWidth, bitmap.PixelHeight);
        _sink.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {RoiScaler.DescribeFrame(bitmap.PixelWidth, bitmap.PixelHeight)}");
    }

    private string DescribeSourceFrame()
        => _source is ReplayFrameSource replay && replay.LastFrameName is { } name ? $" {name}" : "";
}

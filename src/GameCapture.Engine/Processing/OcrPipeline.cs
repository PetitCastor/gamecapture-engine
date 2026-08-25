using System.Runtime.InteropServices.WindowsRuntime;
using GameCapture.Contracts;
using Windows.Globalization;
using Windows.Graphics.Capture;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace GameCapture.Engine;

/// <summary>
/// In-memory OCR service shared by all trackers: GPU frame -> SoftwareBitmap -> ROI crop +
/// upscale -> Windows OCR text. Nothing touches disk. Upscaling matters: Windows OCR misses
/// small game UI text at 1:1 (proven in the side-quest PoC).
/// </summary>
public sealed class OcrPipeline
{
    /// <summary>Name of the engine's config file, for the OCR-pack-missing error message.</summary>
    private const string ConfigFileName = "engine-config.json";

    private readonly OcrEngine _engine;

    /// <param name="languageTag">
    /// BCP-47 tag of the recognizer to use, e.g. "en-US". Null or blank falls back to the first
    /// user-profile language that has an OCR pack installed. Windows OCR cannot detect the
    /// language from the image itself, so a machine whose display language differs from the
    /// game's UI language must set this explicitly or every read comes back garbled.
    /// </param>
    public OcrPipeline(string? languageTag = null)
    {
        _engine = string.IsNullOrWhiteSpace(languageTag)
            ? OcrEngine.TryCreateFromUserProfileLanguages()
              ?? throw new InvalidOperationException(DescribeNoUserProfilePack(AvailableLanguageTags))
            : TryCreateFromTag(languageTag)
              ?? throw new InvalidOperationException(DescribeMissingPack(languageTag, AvailableLanguageTags));
    }

    /// <summary>Recognizer actually in use, as "Display name (tag)".</summary>
    public string Language =>
        $"{_engine.RecognizerLanguage.DisplayName} ({_engine.RecognizerLanguage.LanguageTag})";

    /// <summary>BCP-47 tag of the recognizer actually in use.</summary>
    public string LanguageTag => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>BCP-47 tags of every OCR pack installed on this machine.</summary>
    public static IReadOnlyList<string> AvailableLanguageTags =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToArray();

    /// <summary>Null when the tag is malformed or has no pack installed — both are user error, not a crash.</summary>
    private static OcrEngine? TryCreateFromTag(string tag)
    {
        try
        {
            return OcrEngine.TryCreateFromLanguage(new Language(tag));
        }
        catch (ArgumentException)
        {
            return null; // Language(..) rejects anything that isn't a well-formed BCP-47 tag
        }
    }

    internal static string DescribeMissingPack(string tag, IReadOnlyList<string> installed) =>
        $"No OCR language pack for '{tag}'. {DescribeInstalled(installed)}";

    internal static string DescribeNoUserProfilePack(IReadOnlyList<string> installed) =>
        $"No OCR language pack matches your Windows display language. {DescribeInstalled(installed)}";

    private static string DescribeInstalled(IReadOnlyList<string> installed) =>
        (installed.Count == 0
            ? "No OCR packs are installed at all."
            : $"Installed: {string.Join(", ", installed)}. Set \"ocrLanguage\" in {ConfigFileName} (or --ocr-lang) to one of these.")
        + " Packs are added under Settings > Time & language > Language & region >"
        + " <language> > Language options > Optional language features.";

    /// <summary>Downloads a captured GPU frame into a CPU bitmap (caller disposes).</summary>
    public static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Direct3D11CaptureFrame frame)
    {
        using var premultiplied = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
            frame.Surface, BitmapAlphaMode.Premultiplied);
        return SoftwareBitmap.Convert(premultiplied, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
    }

    /// <summary>OCRs one region of an already-downloaded frame.</summary>
    public async Task<string> ReadRegionAsync(SoftwareBitmap frame, BitmapBounds roi, double scale)
    {
        using var crop = await CropAndScaleAsync(frame, roi, scale);
        ApplyRedChannelGrayscale(crop);
        var result = await _engine.RecognizeAsync(crop);
        return result.Text;
    }

    /// <summary>
    /// OCRs one region keeping per-word geometry, for table-shaped UI where column layout
    /// matters. Word rects are in upscaled-crop space; the result records the scale that
    /// was actually applied so callers can map back to frame pixels.
    /// </summary>
    public async Task<OcrRegionResult> ReadRegionDetailedAsync(SoftwareBitmap frame, BitmapBounds roi, double scale)
    {
        // Clamp before computing frame_rect/effective_scale, not just before cropping: both are
        // reported to callers as "what was actually read" (capture.proto), so an out-of-frame
        // plugin rect must not leak unclamped bounds into ToFramePoint's coordinate mapping.
        var clamped = ClampToBitmap(roi, frame.PixelWidth, frame.PixelHeight);
        var effective = EffectiveScale(clamped, scale);
        using var crop = await CropAndScaleAsync(frame, clamped, scale);
        ApplyRedChannelGrayscale(crop);
        var result = await _engine.RecognizeAsync(crop);

        var lines = new List<OcrLineInfo>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = new List<OcrWordInfo>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWordInfo(word.Text, new RectF(r.X, r.Y, r.Width, r.Height)));
            }
            lines.Add(new OcrLineInfo(line.Text, words));
        }

        return new OcrRegionResult(result.Text, lines, effective, clamped.X, clamped.Y, clamped.Width, clamped.Height);
    }

    /// <summary>The scale actually applied after clamping to the OCR engine's max dimension.</summary>
    public static double EffectiveScale(BitmapBounds bounds, double scale)
    {
        var maxDim = OcrEngine.MaxImageDimension;
        var largestSide = Math.Max(bounds.Width, bounds.Height);
        return largestSide * scale > maxDim ? (double)maxDim / largestSide : scale;
    }

    /// <summary>
    /// Trims an ROI to the frame. The encoder rejects out-of-frame bounds outright, and a
    /// subscribed ROI is client-supplied data: <see cref="RoiScaler.ToFrame"/> clamps to the
    /// frame it was given, but nothing stops a plugin from sending rects for a resolution the
    /// engine is not capturing, so a mistyped constant would otherwise blow up the scan loop.
    /// </summary>
    public static BitmapBounds ClampToBitmap(BitmapBounds bounds, int width, int height)
    {
        var x = Math.Min(bounds.X, (uint)Math.Max(0, width));
        var y = Math.Min(bounds.Y, (uint)Math.Max(0, height));

        return new BitmapBounds
        {
            X = x,
            Y = y,
            Width = Math.Min(bounds.Width, (uint)Math.Max(0, width) - x),
            Height = Math.Min(bounds.Height, (uint)Math.Max(0, height) - y),
        };
    }

    /// <summary>
    /// Crops <paramref name="bounds"/> and upscales by <paramref name="scale"/>, clamped so the
    /// result stays within the OCR engine's max image dimension. Caller disposes the result.
    /// </summary>
    public async Task<SoftwareBitmap> CropAndScaleAsync(SoftwareBitmap source, BitmapBounds bounds, double scale)
    {
        bounds = ClampToBitmap(bounds, source.PixelWidth, source.PixelHeight);
        if (bounds.Width == 0 || bounds.Height == 0)
            throw new ArgumentOutOfRangeException(nameof(bounds),
                $"ROI lies outside the {source.PixelWidth}x{source.PixelHeight} frame.");

        scale = EffectiveScale(bounds, scale);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);

        // Crop while encoding so the stream only ever holds the ROI: a 400x40 region of a 1440p
        // frame costs ~46 KB instead of the ~10.8 MB a full-frame BMP takes, and the round trip
        // drops from ~4.8 ms to ~1.3 ms. Bounds is in *source* coordinates here.
        //
        // Do NOT also set ScaledWidth/ScaledHeight on the encoder: Bounds combined with a scale
        // throws ArgumentException at FlushAsync. Scaling stays on the decoder below, where
        // Bounds would instead be in the *scaled* coordinate space — but the stream is already
        // cropped by then, so no bounds are needed there at all.
        encoder.BitmapTransform.Bounds = bounds;
        await encoder.FlushAsync();

        var decoder = await BitmapDecoder.CreateAsync(stream);

        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(decoder.PixelWidth * scale),
            ScaledHeight = (uint)(decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Cubic,
        };

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    /// <summary>
    /// Replaces each pixel's B and G with its R value in place. The game's chromatic-aberration
    /// post-process fringes every glyph stroke red-left/cyan-right; Windows OCR's internal luma
    /// collapse averages the three shifted copies into a blur, which is where OCR read accuracy
    /// was going. A single channel keeps one sharp copy of the stroke — measured 57.1% -> 95.9%
    /// hit rate across 49 labelled samples, 0 regressions.
    /// <para>
    /// Called only from the OCR read paths (<see cref="ReadRegionAsync"/>,
    /// <see cref="ReadRegionDetailedAsync"/>) on their own local crop, after
    /// <see cref="CropAndScaleAsync"/> returns — never inside <see cref="CropAndScaleAsync"/>
    /// itself. That method is a shared true-color crop/scale utility: <c>PixelSampler</c> and
    /// <c>CaptureGrpcService.DumpFrame</c> both call it directly for non-OCR consumers
    /// (<c>RoiMode.Pixels</c> readers like <c>RefineryPlugin.IsRefineOn</c>, and ROI debug/corpus
    /// dumps) that need the real captured color, not an OCR-only grayscale collapse.
    /// </para>
    /// Runs after the crop+scale rather than before: <see cref="SoftwareBitmap"/> has no resize
    /// API, so doing this on the native-resolution crop would mean a second BMP encode/decode
    /// round trip to get scaled pixels back out. A linear pass over the already-scaled buffer is
    /// cheaper than that round trip even though it walks more pixels. Internal, not private: the
    /// test suite exercises it directly as a pure buffer transform, without needing a real OCR
    /// engine or a full crop/scale round trip.
    /// </summary>
    internal static void ApplyRedChannelGrayscale(SoftwareBitmap bitmap)
    {
        var pixels = new byte[4 * bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyToBuffer(pixels.AsBuffer());
        for (var i = 0; i < pixels.Length; i += 4) // Bgra8: B, G, R, A
        {
            var red = pixels[i + 2];
            pixels[i] = red;
            pixels[i + 1] = red;
        }
        bitmap.CopyFromBuffer(pixels.AsBuffer());
    }
}

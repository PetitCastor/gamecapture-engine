using System.Diagnostics.CodeAnalysis;
using GameCapture.Contracts.Proto;

// The proto namespace also declares a RectF (the wire mirror of the local one), so both names
// are in scope here. Alias them apart rather than fully qualifying at every use site.
using ProtoRectF = GameCapture.Contracts.Proto.RectF;
using LocalRectF = GameCapture.Contracts.RectF;

namespace GameCapture.Contracts;

/// <summary>
/// The public facade for converting proto types to and from the pure shared types. Keeping the
/// boundary centralized means the engine, the SDK and the plugins all agree on the wire semantics
/// by construction — in particular that a <see cref="RoiResult"/>'s frame_rect is what
/// <see cref="OcrRegionResult"/> treats as its ROI origin, so ToFramePoint keeps yielding real
/// frame pixels on the far side of the boundary.
/// </summary>
public static class ProtoMapping
{
    /// <summary>Reference- or frame-space rectangle to its wire form.</summary>
    public static Rect ToProto(this RoiRect r)
        => new() { X = r.X, Y = r.Y, Width = r.Width, Height = r.Height };

    /// <summary>Wire rectangle back to the plain struct.</summary>
    public static RoiRect ToRoiRect(this Rect r)
        => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// Copies OCR content into an existing <see cref="RoiResult"/>. roi_id and frame_rect stay
    /// the caller's business: only the engine knows which subscription the result answers and
    /// which frame-space rect it actually read.
    /// </summary>
    public static void FillFrom(this RoiResult target, OcrRegionResult source)
    {
        target.Text = source.Text;
        target.EffectiveScale = source.EffectiveScale;

        target.Lines.Clear();
        foreach (var line in source.Lines)
        {
            var protoLine = new OcrLine { Text = line.Text };
            foreach (var word in line.Words)
            {
                protoLine.Words.Add(new OcrWord
                {
                    Text = word.Text,
                    CropRect = new ProtoRectF
                    {
                        X = word.CropRect.X,
                        Y = word.CropRect.Y,
                        Width = word.CropRect.Width,
                        Height = word.CropRect.Height,
                    },
                });
            }
            target.Lines.Add(protoLine);
        }
    }

    /// <summary>
    /// Wire result back to an <see cref="OcrRegionResult"/>. RoiX/Y/Width/Height come from
    /// frame_rect, so <see cref="OcrRegionResult.ToFramePoint"/> yields real frame pixels —
    /// identical semantics to the monolith, where ReadRegionDetailedAsync received the
    /// frame-space rect.
    /// </summary>
    /// <exception cref="RoiResultException">
    /// The engine flagged the ROI as failed, the result answers a PIXELS subscription, or
    /// effective_scale is not positive. All three would otherwise produce a result
    /// indistinguishable from a successful read of an empty panel; see
    /// <see cref="TryToOcrRegionResult"/> for the skip-quietly path.
    /// </exception>
    public static OcrRegionResult ToOcrRegionResult(this RoiResult r)
    {
        RoiResultValidator.ValidateForOcr(r);

        var rect = r.FrameRect ?? new Rect();

        var lines = new List<OcrLineInfo>(r.Lines.Count);
        foreach (var line in r.Lines)
        {
            var words = new List<OcrWordInfo>(line.Words.Count);
            foreach (var word in line.Words)
            {
                var box = word.CropRect;
                words.Add(new OcrWordInfo(
                    word.Text,
                    box is null ? default : new LocalRectF(box.X, box.Y, box.Width, box.Height)));
            }
            lines.Add(new OcrLineInfo(line.Text, words));
        }

        return new OcrRegionResult(r.Text, lines, r.EffectiveScale,
            rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>
    /// Wire result of a ROI_MODE_PIXELS subscription to a sampler. FrameX/Y come from the
    /// frame_rect origin so callers keep addressing pixels in frame coordinates.
    /// </summary>
    /// <exception cref="RoiResultException">
    /// The engine flagged the ROI as failed, the result answers a TEXT/DETAILED subscription,
    /// or the buffer does not match the declared geometry. stride/width/height/bytes are four
    /// independent wire fields with no cross-check of their own; a truncated buffer or a stride
    /// that counts row padding the engine never sent would surface much later as an
    /// IndexOutOfRangeException inside a plugin parser, so the mismatch is caught here at the
    /// boundary instead.
    /// </exception>
    public static PixelPatchSampler ToPixelSampler(this RoiResult r)
    {
        RoiResultValidator.ValidateForPixels(r);
        return PixelPatchFactory.Create(r);
    }

    /// <summary>
    /// Non-throwing <see cref="ToOcrRegionResult"/> for the common plugin shape: skip the ROIs
    /// that failed this tick, log why, and leave the tracker's state untouched rather than
    /// feeding it an empty read.
    /// </summary>
    public static bool TryToOcrRegionResult(this RoiResult r,
        [NotNullWhen(true)] out OcrRegionResult? result,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            result = r.ToOcrRegionResult();
            error = null;
            return true;
        }
        catch (RoiResultException e)
        {
            result = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>Non-throwing <see cref="ToPixelSampler"/>; see <see cref="TryToOcrRegionResult"/>.</summary>
    public static bool TryToPixelSampler(this RoiResult r,
        [NotNullWhen(true)] out PixelPatchSampler? sampler,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            sampler = r.ToPixelSampler();
            error = null;
            return true;
        }
        catch (RoiResultException e)
        {
            sampler = null;
            error = e.Message;
            return false;
        }
    }

}

using SkiaSharp;

namespace BrotherRaster.Skia;

/// <summary>
/// Text that fills a box: the largest size at which every line fits. Narrow tape leaves little room,
/// so the lines share the height and the widest one sets the size.
/// </summary>
public static class LabelText
{
    private static readonly Lazy<SKTypeface> Bold = new(() =>
    {
        using var stream = typeof(LabelText).Assembly.GetManifestResourceStream("BrotherRaster.Skia.Fonts.IBMPlexSans-Bold.ttf")
            ?? throw new InvalidOperationException("The embedded font is missing.");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException("The embedded font did not load.");
    });

    /// <summary>
    /// IBM Plex Sans Bold, embedded (SIL Open Font License 1.1). Skia's Linux native assets without
    /// dependencies have no system fonts, so a typeface has to come from somewhere.
    /// </summary>
    public static SKTypeface DefaultTypeface => Bold.Value;

    /// <summary>The box <paramref name="lines"/> take at <paramref name="size"/> dots, as <see cref="DrawFitted"/> lays them out.</summary>
    public static SKSize Measure(IReadOnlyList<string> lines, float size, SKTypeface? typeface = null, float lineGap = 0.1f)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lines.Count);
        using var font = new SKFont(typeface ?? DefaultTypeface, size);
        var capHeight = font.Metrics.CapHeight;
        var height = (capHeight + font.Metrics.Descent) * lines.Count + capHeight * lineGap * (lines.Count - 1);
        return new SKSize(lines.Max(l => font.MeasureText(l)), height);
    }

    /// <summary>
    /// Draws <paramref name="lines"/> black, centred in <paramref name="box"/>, at the largest size at
    /// which all fit. Returns the font size used, in dots.
    /// </summary>
    public static float DrawFitted(SKCanvas canvas, SKRect box, IReadOnlyList<string> lines, SKTypeface? typeface = null, float lineGap = 0.1f, SKTextAlign align = SKTextAlign.Center)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lines.Count);
        using var font = new SKFont(typeface ?? DefaultTypeface, 100) { Edging = SKFontEdging.Alias, Subpixel = false };
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = false };

        // Measure at 100 dots and scale: the cap-to-baseline height of each line is the font's cap height.
        var capHeight = font.Metrics.CapHeight;
        var descent = font.Metrics.Descent;
        var lineHeight = capHeight + descent;
        var widest = lines.Max(l => font.MeasureText(l));
        var totalHeight = lineHeight * lines.Count + capHeight * lineGap * (lines.Count - 1);
        var scale = Math.Min(box.Width / Math.Max(widest, 1), box.Height / totalHeight);
        var size = 100 * scale;
        font.Size = size;

        capHeight *= scale;
        lineHeight *= scale;
        var gap = capHeight * lineGap;
        var top = box.Top + (box.Height - (lineHeight * lines.Count + gap * (lines.Count - 1))) / 2;
        for (var i = 0; i < lines.Count; i++)
        {
            var baseline = top + capHeight + i * (lineHeight + gap);
            var x = align switch
            {
                SKTextAlign.Left => box.Left,
                SKTextAlign.Right => box.Right,
                _ => box.MidX,
            };
            canvas.DrawText(lines[i], x, baseline, align, font, paint);
        }

        return size;
    }
}

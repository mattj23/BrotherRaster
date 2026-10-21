using BrotherRaster.Pt;
using BrotherRaster.Ql;
using SkiaSharp;

namespace BrotherRaster.Skia;

/// <summary>
/// A white Skia canvas the size of one label, the right way up for its encoder. Draw on
/// <see cref="Canvas"/> in dots, then call <see cref="ToLabel"/>.
/// </summary>
public sealed class LabelCanvas : IDisposable
{
    private LabelCanvas(int width, int height, float dotsPerMmX, float dotsPerMmY)
    {
        Bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Canvas = new SKCanvas(Bitmap);
        Canvas.Clear(SKColors.White);
        DotsPerMmX = dotsPerMmX;
        DotsPerMmY = dotsPerMmY;
    }

    public SKBitmap Bitmap { get; }

    public SKCanvas Canvas { get; }

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    /// <summary>Dots per millimetre across the canvas.</summary>
    public float DotsPerMmX { get; }

    /// <summary>Dots per millimetre down the canvas.</summary>
    public float DotsPerMmY { get; }

    /// <summary>
    /// A canvas for a QL roll: as wide as the roll prints, and <paramref name="lengthDots"/> long (300 dpi).
    /// A die-cut label's length is fixed, so <paramref name="lengthDots"/> is ignored for one.
    /// </summary>
    public static LabelCanvas ForQl(QlMedia media, int lengthDots = 0)
    {
        var length = media.Kind == QlMediaKind.DieCut ? media.PrintableLength : lengthDots;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length, nameof(lengthDots));
        return new LabelCanvas(media.PrintableDots, length, 300 / 25.4f, 300 / 25.4f);
    }

    /// <summary>
    /// A canvas for a P-touch tape, read along the tape: as tall as the tape prints, and
    /// <paramref name="lengthDots"/> long (180 dpi, or 360 dpi along the tape at high resolution).
    /// </summary>
    public static LabelCanvas ForPt(PtTape tape, int lengthDots, bool highResolution = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthDots);
        return new LabelCanvas(lengthDots, tape.PrintablePins, (highResolution ? 360 : 180) / 25.4f, 180 / 25.4f);
    }

    /// <inheritdoc cref="LabelImages.FromSKBitmap"/>
    public LabelBitmap ToLabel(int threshold = 128, bool dither = false)
    {
        Canvas.Flush();
        return LabelImages.FromSKBitmap(Bitmap, threshold, dither);
    }

    public void Dispose()
    {
        Canvas.Dispose();
        Bitmap.Dispose();
    }
}

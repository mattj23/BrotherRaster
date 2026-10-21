using SkiaSharp;

namespace BrotherRaster.Skia;

/// <summary>Converts between <see cref="LabelBitmap"/> and Skia bitmaps or PNG files.</summary>
public static class LabelImages
{
    public static byte[] ToPng(LabelBitmap bitmap)
    {
        using var image = ToSKBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Loads a PNG (or any format Skia decodes). See <see cref="FromSKBitmap"/> for how pixels become dots.</summary>
    public static LabelBitmap FromPng(byte[] png, int threshold = 128, bool dither = false)
    {
        using var decoded = SKBitmap.Decode(png) ?? throw new FormatException("Not a decodable image.");
        return FromSKBitmap(decoded, threshold, dither);
    }

    /// <summary>A white-background, black-dot grayscale image of the bitmap.</summary>
    public static SKBitmap ToSKBitmap(LabelBitmap bitmap)
    {
        var image = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var pixels = image.GetPixelSpan();
        var stride = image.RowBytes;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                pixels[y * stride + x] = bitmap[x, y] ? (byte)0 : (byte)255;
            }
        }

        return image;
    }

    /// <summary>
    /// Turns an image into dots. Transparent pixels count as white. Without dithering a pixel is a dot
    /// when its luma is below <paramref name="threshold"/>; with dithering, grays are spread as a
    /// Floyd–Steinberg pattern, which suits photos and logos better than text.
    /// </summary>
    public static LabelBitmap FromSKBitmap(SKBitmap image, int threshold = 128, bool dither = false)
    {
        var width = image.Width;
        var height = image.Height;
        var luma = new float[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = image.GetPixel(x, y);
                var gray = (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000f;
                var alpha = c.Alpha / 255f;
                luma[y * width + x] = gray * alpha + 255 * (1 - alpha);
            }
        }

        var result = new LabelBitmap(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var old = luma[y * width + x];
                var dot = old < threshold;
                result[x, y] = dot;
                if (!dither)
                {
                    continue;
                }

                var error = old - (dot ? 0 : 255);
                Spread(luma, width, height, x + 1, y, error * 7 / 16);
                Spread(luma, width, height, x - 1, y + 1, error * 3 / 16);
                Spread(luma, width, height, x, y + 1, error * 5 / 16);
                Spread(luma, width, height, x + 1, y + 1, error * 1 / 16);
            }
        }

        return result;
    }

    private static void Spread(float[] luma, int width, int height, int x, int y, float amount)
    {
        if (x >= 0 && x < width && y < height)
        {
            luma[y * width + x] += amount;
        }
    }
}

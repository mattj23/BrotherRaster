namespace BrotherRaster;

/// <summary>
/// A 1-bit image: <c>true</c> is a printed dot. Row-major, top row first, left to right as the label is
/// read. Each encoder documents which way round it expects the label.
/// </summary>
public sealed class LabelBitmap
{
    private readonly bool[] _dots;

    public LabelBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = width;
        Height = height;
        _dots = new bool[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public bool this[int x, int y]
    {
        get => _dots[y * Width + x];
        set => _dots[y * Width + x] = value;
    }

    /// <summary>Sets every dot in the rectangle, clipped to the bitmap.</summary>
    public void FillRect(int x, int y, int width, int height)
    {
        for (var yy = Math.Max(0, y); yy < Math.Min(Height, y + height); yy++)
        {
            for (var xx = Math.Max(0, x); xx < Math.Min(Width, x + width); xx++)
            {
                _dots[yy * Width + xx] = true;
            }
        }
    }

    public LabelBitmap Crop(int x, int y, int width, int height)
    {
        var result = new LabelBitmap(width, height);
        for (var yy = 0; yy < height; yy++)
        {
            for (var xx = 0; xx < width; xx++)
            {
                result[xx, yy] = this[x + xx, y + yy];
            }
        }

        return result;
    }

    /// <summary>A copy turned a quarter turn clockwise: the left edge becomes the top.</summary>
    public LabelBitmap RotateClockwise()
    {
        var result = new LabelBitmap(Height, Width);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                result[Height - 1 - y, x] = this[x, y];
            }
        }

        return result;
    }

    /// <summary>A copy turned a quarter turn counterclockwise: the right edge becomes the top.</summary>
    public LabelBitmap RotateCounterclockwise()
    {
        var result = new LabelBitmap(Height, Width);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                result[y, Width - 1 - x] = this[x, y];
            }
        }

        return result;
    }

    public bool ContentEquals(LabelBitmap other) =>
        Width == other.Width && Height == other.Height && _dots.AsSpan().SequenceEqual(other._dots);
}

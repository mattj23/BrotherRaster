namespace BrotherRaster.Pt;

public sealed record PtRasterOptions
{
    public PtTape Tape { get; init; } = PtTape.TZe12;

    /// <summary>Cut the tape after each label.</summary>
    public bool Cut { get; init; } = true;

    /// <summary>
    /// Half cut: cut through the laminate but not the backing, so labels stay on one strip. Not on the
    /// PT-P710BT.
    /// </summary>
    public bool HalfCut { get; init; }

    /// <summary>
    /// Chain printing: leave the last label in the printer instead of feeding it out, which saves the
    /// leading tape of the next job. Off by default, so a one-off label comes out ready to peel.
    /// </summary>
    public bool Chain { get; init; }

    /// <summary>180 x 360 dpi: twice as many raster lines per millimetre of tape. The bitmap is that much longer.</summary>
    public bool HighResolution { get; init; }

    /// <summary>Mirror the label, for the back of clear tape.</summary>
    public bool Mirror { get; init; }

    /// <summary>Feed margin at each end, in dots at the resolution chosen. The reference's minimum is 2 mm: 14 dots, or 28 at high resolution.</summary>
    public int? MarginDots { get; init; }

    /// <summary>PackBits-compress the rows, and send blank rows as <c>Z</c>. On by default, as Brother's own driver does.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>
    /// Tell the printer which cassette type the job expects (laminated, heat-shrink…), so it refuses the
    /// job if something else is loaded. The width is always checked.
    /// </summary>
    public bool CheckMediaType { get; init; }
}

/// <summary>
/// Builds a raster job for the PT-E550W, PT-P750W and PT-P710BT from their raster command reference.
///
/// The bitmap is the label as read along the tape: its height is the tape's
/// <see cref="PtTape.PrintablePins"/>, and its width is the label's length in dots (180 dpi, or 360 dpi
/// with <see cref="PtRasterOptions.HighResolution"/>). Each bitmap column becomes one raster line, left
/// end first.
/// </summary>
public static class PtRasterEncoder
{
    private const int BytesPerRow = PtTape.HeadPins / 8;
    private const int InvalidateBytes = 100;
    private const byte AutoCut = 0x40;
    private const byte MirrorPrinting = 0x80;
    private const byte AdvancedHalfCut = 0x04;
    private const byte AdvancedNoChain = 0x08;
    private const byte AdvancedHighResolution = 0x40;

    /// <summary>The reference's minimum label length, in raster lines (4.4 mm, or 4.2 mm at high resolution).</summary>
    public static int MinimumLength(bool highResolution) => highResolution ? 60 : 31;

    /// <summary>The reference's maximum label length, in raster lines (1 m of TZe).</summary>
    public static int MaximumLength(bool highResolution) => highResolution ? 14172 : 7086;

    public static byte[] Encode(LabelBitmap label, PtRasterOptions? options = null)
    {
        options ??= new PtRasterOptions();
        var tape = options.Tape;
        if (label.Height != tape.PrintablePins)
        {
            throw new ArgumentException($"A {tape.Name} label must be {tape.PrintablePins} dots high; this one is {label.Height}.", nameof(label));
        }

        var minimum = MinimumLength(options.HighResolution);
        var maximum = MaximumLength(options.HighResolution);
        if (label.Width < minimum || label.Width > maximum)
        {
            throw new ArgumentException($"A label must be {minimum} to {maximum} dots long; this one is {label.Width}.", nameof(label));
        }

        var margin = options.MarginDots ?? (options.HighResolution ? 28 : 14);

        var job = new RasterCommands(64 + label.Width * (BytesPerRow + 3));
        job.Invalidate(InvalidateBytes);
        job.Initialize();
        job.SwitchToRaster();

        var flags = PrintInformationFlags.Recover | PrintInformationFlags.MediaWidth;
        if (options.CheckMediaType)
        {
            flags |= PrintInformationFlags.MediaType;
        }

        job.PrintInformation(flags, options.CheckMediaType ? (byte)tape.Type : (byte)0, tape.WidthMm, 0, label.Width, firstPage: true);
        job.VariousMode((byte)((options.Cut ? AutoCut : 0) | (options.Mirror ? MirrorPrinting : 0)));
        if (options.Cut)
        {
            job.CutEvery(1);
        }

        job.ExpandedMode((byte)((options.HalfCut ? AdvancedHalfCut : 0) | (options.Chain ? 0 : AdvancedNoChain) | (options.HighResolution ? AdvancedHighResolution : 0)));
        job.Margin(margin);
        if (options.Compress)
        {
            job.SelectTiffCompression();
        }

        Span<byte> row = stackalloc byte[BytesPerRow];
        for (var x = 0; x < label.Width; x++)
        {
            row.Clear();
            var blank = true;
            for (var y = 0; y < label.Height; y++)
            {
                if (label[x, y])
                {
                    RasterRows.Set(row, HeadBit(tape, y));
                    blank = false;
                }
            }

            if (blank && options.Compress)
            {
                job.ZeroRow();
            }
            else
            {
                job.LongRow(row, options.Compress);
            }
        }

        job.PrintLastPage();
        return job.ToArray();
    }

    /// <summary>The head bit (from the most significant bit of the first byte) that prints row <paramref name="y"/> of the label.</summary>
    internal static int HeadBit(PtTape tape, int y) => tape.LeftMarginPins + y;
}

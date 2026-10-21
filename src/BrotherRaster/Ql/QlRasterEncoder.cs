namespace BrotherRaster.Ql;

public sealed record QlRasterOptions
{
    public QlMedia Media { get; init; } = QlMedia.Continuous62;

    /// <summary>Auto cut after each label, and cut at the end of the job.</summary>
    public bool Cut { get; init; } = true;

    /// <summary>PackBits-compress the rows. The QL-800 itself does not support compression.</summary>
    public bool Compress { get; init; }

    /// <summary>
    /// Send a two-color job, as the DK-22251 black/red roll needs. A QL-8xx refuses a one-color job on
    /// that roll, and a two-color job on plain tape: it shows ERROR with the red light blinking, and only
    /// switching it off and on clears it.
    /// </summary>
    public bool TwoColor { get; init; }

    /// <summary>Feed margin at each end of continuous tape, in dots. The reference's minimum is 35 (3 mm). Die-cut labels always use 0.</summary>
    public int MarginDots { get; init; } = 35;
}

/// <summary>
/// Builds a raster job for the QL-800 series (QL-800, QL-810W, QL-820NWB) from the QL-800 series raster
/// command reference.
///
/// The bitmap is the label as read with the tape coming out of the printer top first: its width is the
/// roll's <see cref="QlMedia.PrintableDots"/>, and each bitmap row is one raster line. A row is laid on
/// the head mirrored, after the roll's left margin, because the head lays it down reversed.
///
/// The job layout is the one that has run in production on QL-810Ws: switch to raster, 200 NULLs,
/// initialize, switch to raster again, a status request, then the print information with all validity
/// flags and quality set.
/// </summary>
public static class QlRasterEncoder
{
    private const int BytesPerRow = QlMedia.HeadPins / 8;
    private const int InvalidateBytes = 200;
    private const byte MediaContinuous = 0x0A;
    private const byte MediaDieCut = 0x0B;
    private const byte AutoCut = 0x40;
    private const byte ExpandedTwoColor = 0x01;
    private const byte ExpandedCutAtEnd = 0x08;
    private const byte RowCommand = 0x67;
    private const byte TwoColorRowCommand = 0x77;

    /// <summary>Encodes a one-color label, or a two-color label when <paramref name="red"/> is given.</summary>
    /// <param name="black">The black (or only) plane.</param>
    /// <param name="options">Roll and job settings; defaults to 62 mm continuous with cutting.</param>
    /// <param name="red">The red plane, the same size as <paramref name="black"/>. Needs <see cref="QlRasterOptions.TwoColor"/>.</param>
    public static byte[] Encode(LabelBitmap black, QlRasterOptions? options = null, LabelBitmap? red = null)
    {
        options ??= new QlRasterOptions();
        var media = options.Media;
        if (black.Width != media.PrintableDots)
        {
            throw new ArgumentException($"A {media.Name} label must be {media.PrintableDots} dots wide; this one is {black.Width}.", nameof(black));
        }

        if (media.Kind == QlMediaKind.DieCut && black.Height != media.PrintableLength)
        {
            throw new ArgumentException($"A {media.Name} die-cut label is {media.PrintableLength} rows long; this one is {black.Height}.", nameof(black));
        }

        if (red is not null)
        {
            if (!options.TwoColor)
            {
                throw new ArgumentException("A red plane needs TwoColor.", nameof(red));
            }

            if (red.Width != black.Width || red.Height != black.Height)
            {
                throw new ArgumentException("The red plane must be the same size as the black one.", nameof(red));
            }
        }

        var job = new RasterCommands(64 + black.Height * (options.TwoColor ? 2 : 1) * (BytesPerRow + 3));
        job.SwitchToRaster();
        job.Invalidate(InvalidateBytes);
        job.Initialize();
        job.SwitchToRaster();
        job.StatusRequest();

        var dieCut = media.Kind == QlMediaKind.DieCut;
        job.PrintInformation(
            PrintInformationFlags.Recover | PrintInformationFlags.MediaType | PrintInformationFlags.MediaWidth | PrintInformationFlags.MediaLength | PrintInformationFlags.Quality,
            dieCut ? MediaDieCut : MediaContinuous,
            media.WidthMm,
            media.LengthMm,
            black.Height,
            firstPage: true);

        if (options.Cut)
        {
            job.VariousMode(AutoCut);
            job.CutEvery(1);
        }

        job.ExpandedMode((byte)((options.Cut ? ExpandedCutAtEnd : 0) | (options.TwoColor ? ExpandedTwoColor : 0)));
        job.Margin(dieCut ? 0 : options.MarginDots);
        if (options.Compress)
        {
            job.SelectTiffCompression();
        }

        Span<byte> row = stackalloc byte[BytesPerRow];
        for (var y = 0; y < black.Height; y++)
        {
            Pack(black, media, y, row);
            if (options.TwoColor)
            {
                job.ShortRow(TwoColorRowCommand, 0x01, row, options.Compress);
                if (red is null)
                {
                    row.Clear();
                }
                else
                {
                    Pack(red, media, y, row);
                }

                job.ShortRow(TwoColorRowCommand, 0x02, row, options.Compress);
            }
            else
            {
                job.ShortRow(RowCommand, 0x00, row, options.Compress);
            }
        }

        job.PrintLastPage();
        return job.ToArray();
    }

    private static void Pack(LabelBitmap plane, QlMedia media, int y, Span<byte> row)
    {
        row.Clear();
        for (var x = 0; x < plane.Width; x++)
        {
            if (plane[x, y])
            {
                RasterRows.Set(row, HeadBit(media, x));
            }
        }
    }

    /// <summary>The head bit (from the most significant bit of the first byte) that prints column <paramref name="x"/>.</summary>
    internal static int HeadBit(QlMedia media, int x) => QlMedia.HeadPins - 1 - (media.LeftMarginPins + x);
}

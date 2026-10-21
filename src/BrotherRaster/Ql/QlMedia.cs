namespace BrotherRaster.Ql;

public enum QlMediaKind
{
    /// <summary>Continuous-length tape (DK-22xx): any length, cut where the job ends.</summary>
    Continuous,

    /// <summary>Pre-cut labels on a backing (DK-11xx): fixed length, and no feed margin.</summary>
    DieCut,
}

/// <summary>
/// A DK roll for the QL-800 series (720-dot head, 300 dpi). The dot counts come from the "Page size"
/// and "Raster line" tables of the QL-800 series raster command reference. Only
/// <see cref="Continuous62"/> (and the DK-22251 black/red roll, which has the same geometry) has been
/// printed on real hardware; treat the rest as the reference's word until they have been.
/// </summary>
/// <param name="Name">The width (and length for die-cut labels) the way Brother writes it.</param>
/// <param name="Kind">Continuous tape or die-cut labels.</param>
/// <param name="WidthMm">The media width the printer reports and is told in <c>ESC i z</c>.</param>
/// <param name="LengthMm">0 for continuous tape; the label length for die-cut labels.</param>
/// <param name="PrintableDots">Dots across the tape that print: the bitmap width.</param>
/// <param name="LeftMarginPins">Head pins outside the tape on the side the reference calls "left".</param>
/// <param name="PrintableLength">Rows a die-cut label takes (its printable length); 0 for continuous tape.</param>
public sealed record QlMedia(string Name, QlMediaKind Kind, byte WidthMm, byte LengthMm, int PrintableDots, int LeftMarginPins, int PrintableLength = 0)
{
    /// <summary>Pins on the QL-800 series head.</summary>
    public const int HeadPins = 720;

    public static QlMedia Continuous12 { get; } = new("12mm", QlMediaKind.Continuous, 12, 0, 106, 585);

    public static QlMedia Continuous29 { get; } = new("29mm", QlMediaKind.Continuous, 29, 0, 306, 408);

    public static QlMedia Continuous38 { get; } = new("38mm", QlMediaKind.Continuous, 38, 0, 413, 295);

    public static QlMedia Continuous50 { get; } = new("50mm", QlMediaKind.Continuous, 50, 0, 554, 154);

    public static QlMedia Continuous54 { get; } = new("54mm", QlMediaKind.Continuous, 54, 0, 590, 130);

    /// <summary>62 mm continuous (DK-22205, and DK-22251 black/red). Verified on the QL-810W.</summary>
    public static QlMedia Continuous62 { get; } = new("62mm", QlMediaKind.Continuous, 62, 0, 696, 12);

    public static QlMedia DieCut17x54 { get; } = new("17mm x 54mm", QlMediaKind.DieCut, 17, 54, 165, 555, 566);

    public static QlMedia DieCut17x87 { get; } = new("17mm x 87mm", QlMediaKind.DieCut, 17, 87, 165, 555, 956);

    public static QlMedia DieCut23x23 { get; } = new("23mm x 23mm", QlMediaKind.DieCut, 23, 23, 236, 442, 202);

    public static QlMedia DieCut29x42 { get; } = new("29mm x 42mm", QlMediaKind.DieCut, 29, 42, 306, 408, 425);

    public static QlMedia DieCut29x90 { get; } = new("29mm x 90mm", QlMediaKind.DieCut, 29, 90, 306, 408, 991);

    public static QlMedia DieCut38x90 { get; } = new("38mm x 90mm", QlMediaKind.DieCut, 38, 90, 413, 295, 991);

    public static QlMedia DieCut39x48 { get; } = new("39mm x 48mm", QlMediaKind.DieCut, 39, 48, 425, 289, 495);

    public static QlMedia DieCut52x29 { get; } = new("52mm x 29mm", QlMediaKind.DieCut, 52, 29, 578, 142, 271);

    public static QlMedia DieCut54x29 { get; } = new("54mm x 29mm", QlMediaKind.DieCut, 54, 29, 602, 59, 271);

    public static QlMedia DieCut60x86 { get; } = new("60mm x 86mm", QlMediaKind.DieCut, 60, 86, 672, 24, 954);

    public static QlMedia DieCut62x29 { get; } = new("62mm x 29mm", QlMediaKind.DieCut, 62, 29, 696, 12, 271);

    public static QlMedia DieCut62x100 { get; } = new("62mm x 100mm", QlMediaKind.DieCut, 62, 100, 696, 12, 1109);

    public static QlMedia DieCut12Round { get; } = new("12mm dia", QlMediaKind.DieCut, 12, 12, 94, 513, 94);

    public static QlMedia DieCut24Round { get; } = new("24mm dia", QlMediaKind.DieCut, 24, 24, 236, 442, 236);

    public static QlMedia DieCut58Round { get; } = new("58mm dia", QlMediaKind.DieCut, 58, 58, 618, 51, 618);

    public static IReadOnlyList<QlMedia> All { get; } =
    [
        Continuous12, Continuous29, Continuous38, Continuous50, Continuous54, Continuous62,
        DieCut17x54, DieCut17x87, DieCut23x23, DieCut29x42, DieCut29x90, DieCut38x90, DieCut39x48,
        DieCut52x29, DieCut54x29, DieCut60x86, DieCut62x29, DieCut62x100, DieCut12Round, DieCut24Round, DieCut58Round,
    ];

    /// <summary>Head pins outside the tape on the other side.</summary>
    public int RightMarginPins => HeadPins - PrintableDots - LeftMarginPins;

    /// <summary>The roll a printer reports in its status block (width and length in mm), if it is in the table.</summary>
    public static QlMedia? Find(int widthMm, int lengthMm) =>
        All.FirstOrDefault(m => m.WidthMm == widthMm && m.LengthMm == lengthMm);
}

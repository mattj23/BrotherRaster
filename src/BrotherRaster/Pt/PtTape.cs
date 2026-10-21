namespace BrotherRaster.Pt;

/// <summary>The cassette types the PT-E550W/P750W/P710BT status block reports, and that <c>ESC i z</c> can name.</summary>
public enum PtMediaType : byte
{
    None = 0x00,
    Laminated = 0x01,
    NonLaminated = 0x03,
    HeatShrink2To1 = 0x11,
    HeatShrink3To1 = 0x17,
    Incompatible = 0xFF,
}

/// <summary>
/// A P-touch cassette for the 128-pin, 180 dpi head of the PT-E550W, PT-P750W and PT-P710BT. The dot
/// counts come from the "Raster line" tables of their raster command reference. Only
/// <see cref="TZe12"/> has been printed on real hardware (a PT-P750W).
/// </summary>
/// <param name="Name">How Brother names the size.</param>
/// <param name="Type">The cassette type, for <see cref="PtRasterOptions.CheckMediaType"/>.</param>
/// <param name="WidthMm">The width the printer reports and is told in <c>ESC i z</c>. 3.5 mm tape reports as 4.</param>
/// <param name="PrintablePins">Pins that print across the tape: the bitmap height.</param>
/// <param name="LeftMarginPins">Pins outside the print area on the side the reference calls "left".</param>
public sealed record PtTape(string Name, PtMediaType Type, byte WidthMm, int PrintablePins, int LeftMarginPins)
{
    /// <summary>Pins on the head.</summary>
    public const int HeadPins = 128;

    /// <summary>Dots per inch across the tape, and along it in normal resolution.</summary>
    public const int Dpi = 180;

    public static PtTape TZe3_5 { get; } = new("3.5mm", PtMediaType.Laminated, 4, 24, 52);

    public static PtTape TZe6 { get; } = new("6mm", PtMediaType.Laminated, 6, 32, 48);

    public static PtTape TZe9 { get; } = new("9mm", PtMediaType.Laminated, 9, 50, 39);

    /// <summary>12 mm TZe. Verified on the PT-P750W.</summary>
    public static PtTape TZe12 { get; } = new("12mm", PtMediaType.Laminated, 12, 70, 29);

    public static PtTape TZe18 { get; } = new("18mm", PtMediaType.Laminated, 18, 112, 8);

    public static PtTape TZe24 { get; } = new("24mm", PtMediaType.Laminated, 24, 128, 0);










    public static IReadOnlyList<PtTape> All { get; } =
    [
        TZe3_5, TZe6, TZe9, TZe12, TZe18, TZe24,
    ];

    public int RightMarginPins => HeadPins - PrintablePins - LeftMarginPins;

    /// <summary>
    /// The TZe tape a status block describes; non-laminated TZe has the laminated geometry. Null for
    /// anything else: the reference gives heat-shrink geometry but not the width such a tube reports,
    /// so heat-shrink is left out until it has been seen on a printer.
    /// </summary>
    public static PtTape? Find(PtMediaType type, int widthMm) =>
        type is PtMediaType.Laminated or PtMediaType.NonLaminated ? All.FirstOrDefault(t => t.WidthMm == widthMm) : null;
}

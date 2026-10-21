namespace BrotherRaster.Status;

/// <summary>The family a status block's series code names.</summary>
public enum BrotherSeries
{
    Unknown,

    /// <summary>Series code '4' (0x34): QL-800, QL-810W, QL-820NWB.</summary>
    Ql,

    /// <summary>Series code '0' (0x30): PT-E550W, PT-P750W, PT-P710BT.</summary>
    Pt,
}

/// <summary>The status type byte (offset 18).</summary>
public enum BrotherStatusType : byte
{
    ReplyToRequest = 0x00,
    PrintingCompleted = 0x01,
    ErrorOccurred = 0x02,
    TurnedOff = 0x04,
    Notification = 0x05,
    PhaseChange = 0x06,
}

/// <summary>Error information 1 (offset 8). The bits both references share keep one name; the rest say which family uses them.</summary>
[Flags]
public enum BrotherError1 : byte
{
    None = 0,
    NoMedia = 0x01,

    /// <summary>QL: end of a die-cut roll.</summary>
    EndOfMedia = 0x02,
    CutterJam = 0x04,

    /// <summary>PT: weak batteries.</summary>
    WeakBatteries = 0x08,

    /// <summary>QL: printer in use.</summary>
    PrinterInUse = 0x10,

    /// <summary>QL: printer turned off.</summary>
    TurnedOff = 0x20,
    HighVoltageAdapter = 0x40,

    /// <summary>QL: fan motor error.</summary>
    FanMotor = 0x80,
}

/// <summary>Error information 2 (offset 9).</summary>
[Flags]
public enum BrotherError2 : byte
{
    None = 0,

    /// <summary>The loaded media does not match the job's print information.</summary>
    ReplaceMedia = 0x01,

    /// <summary>QL: expansion buffer full.</summary>
    ExpansionBufferFull = 0x02,

    /// <summary>QL: communication error.</summary>
    CommunicationError = 0x04,
    CoverOpen = 0x10,

    /// <summary>PT: overheating. (QL: cancel key, unused.)</summary>
    Overheating = 0x20,

    /// <summary>QL: media cannot be fed, also when the media end is detected.</summary>
    CannotFeed = 0x40,

    /// <summary>QL: system error.</summary>
    SystemError = 0x80,
}

/// <summary>
/// The 32-byte status block the QL-800 series and the PT-E550W/P750W/P710BT describe under
/// <c>ESC i S</c>. Over the network neither family sends it back on port 9100; both return it from
/// Brother's SNMP object <see cref="SnmpOid"/>.
/// </summary>
public sealed record BrotherStatusBlock
{
    /// <summary>Brother's SNMP object that holds the status block (seen on QL-810W fw 1.22 and PT-P750W fw 1.18).</summary>
    public const string SnmpOid = "1.3.6.1.4.1.2435.3.3.9.1.6.1.0";

    public const int Length = 32;

    private BrotherStatusBlock(byte[] raw) => Raw = raw;

    public byte[] Raw { get; }

    public byte SeriesCode => Raw[3];

    public byte ModelCode => Raw[4];

    public BrotherSeries Series => SeriesCode switch
    {
        0x34 => BrotherSeries.Ql,
        0x30 => BrotherSeries.Pt,
        _ => BrotherSeries.Unknown,
    };

    /// <summary>The model the codes name, or a description of the codes if neither reference lists them.</summary>
    public string Model => (SeriesCode, ModelCode) switch
    {
        (0x34, 0x38) => "QL-800",
        (0x34, 0x39) => "QL-810W",
        (0x34, 0x41) => "QL-820NWB",
        (0x30, 0x66) => "PT-E550W",
        (0x30, 0x68) => "PT-P750W",
        _ => $"series 0x{SeriesCode:X2} model 0x{ModelCode:X2}",
    };

    public BrotherError1 Error1 => (BrotherError1)Raw[8];

    public BrotherError2 Error2 => (BrotherError2)Raw[9];

    /// <summary>Loaded media width in mm; 0 with nothing loaded.</summary>
    public int MediaWidthMm => Raw[10];

    /// <summary>
    /// The media type byte. QL: 0x0A (or 0x4A, as the reference's table prints it) continuous, 0x0B/0x4B
    /// die-cut. PT: see <see cref="Pt.PtMediaType"/>. 0 is no media.
    /// </summary>
    public byte MediaType => Raw[11];

    /// <summary>The various mode settings value in force.</summary>
    public byte Mode => Raw[15];

    /// <summary>Die-cut label length in mm; 0 for continuous tape.</summary>
    public int MediaLengthMm => Raw[17];

    public BrotherStatusType StatusType => (BrotherStatusType)Raw[18];

    /// <summary>True while printing (phase type 1); false while waiting to receive.</summary>
    public bool Printing => Raw[19] == 0x01;

    /// <summary>
    /// PT only: the tape color ID at offset 24, by the reference's table (<see cref="TapeColorName"/>).
    /// Not reliable: a PT-P750W (fw 1.18) with a white 12 mm laminated cassette reports 0x06, which the
    /// table calls yellow. Treat it as a raw value.
    /// </summary>
    public byte TapeColor => Raw[24];

    /// <summary>PT only: the text color ID at offset 25, by the reference's table (<see cref="TextColorName"/>). Raw, like <see cref="TapeColor"/>.</summary>
    public byte TextColor => Raw[25];

    /// <summary>A status block, or null if the bytes are not one (wrong length, or no 0x80 0x20 'B' header).</summary>
    public static BrotherStatusBlock? TryParse(ReadOnlySpan<byte> raw) =>
        raw.Length == Length && raw[0] == 0x80 && raw[1] == 0x20 && raw[2] == 0x42 ? new BrotherStatusBlock(raw.ToArray()) : null;

    /// <summary>The PT reference's name for a tape color ID.</summary>
    public static string TapeColorName(byte id) => id switch
    {
        0x00 => "none",
        0x01 => "white",
        0x02 => "other",
        0x03 => "clear",
        0x04 => "red",
        0x05 => "blue",
        0x06 => "yellow",
        0x07 => "green",
        0x08 => "black",
        0x09 => "clear (white text)",
        0x20 => "matte white",
        0x21 => "matte clear",
        0x22 => "matte silver",
        0x23 => "satin gold",
        0x24 => "satin silver",
        0x30 => "blue (D)",
        0x31 => "red (D)",
        0x40 => "fluorescent orange",
        0x41 => "fluorescent yellow",
        0x50 => "berry pink (S)",
        0x51 => "light gray (S)",
        0x52 => "lime green (S)",
        0x60 => "yellow (F)",
        0x61 => "pink (F)",
        0x62 => "blue (F)",
        0x70 => "white (heat-shrink tube)",
        0x90 => "white (flex. ID)",
        0x91 => "yellow (flex. ID)",
        0xF0 => "cleaning",
        0xF1 => "stencil",
        0xFF => "incompatible",
        _ => $"0x{id:X2}",
    };

    /// <summary>The PT reference's name for a text color ID.</summary>
    public static string TextColorName(byte id) => id switch
    {
        0x00 => "none",
        0x01 => "white",
        0x02 => "other",
        0x04 => "red",
        0x05 => "blue",
        0x08 => "black",
        0x0A => "gold",
        0x62 => "blue (F)",
        0xF0 => "cleaning",
        0xF1 => "stencil",
        0xFF => "incompatible",
        _ => $"0x{id:X2}",
    };

    /// <summary>
    /// The loaded media in words, e.g. "12mm laminated" or "62mm continuous". The color bytes are left
    /// out because they have not matched the cassette on real hardware; see <see cref="TapeColor"/>.
    /// </summary>
    public string DescribeMedia()
    {
        if (MediaWidthMm == 0 || MediaType == 0)
        {
            return "no media";
        }

        if (Series == BrotherSeries.Pt)
        {
            var type = (Pt.PtMediaType)MediaType switch
            {
                Pt.PtMediaType.Laminated => "laminated",
                Pt.PtMediaType.NonLaminated => "non-laminated",
                Pt.PtMediaType.HeatShrink2To1 => "heat-shrink 2:1",
                Pt.PtMediaType.HeatShrink3To1 => "heat-shrink 3:1",
                Pt.PtMediaType.Incompatible => "incompatible",
                _ => $"type 0x{MediaType:X2}",
            };
            return $"{MediaWidthMm}mm {type}";
        }

        return MediaLengthMm == 0 ? $"{MediaWidthMm}mm continuous" : $"{MediaWidthMm}mm x {MediaLengthMm}mm die-cut";
    }
}

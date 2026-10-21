namespace BrotherRaster.Status;

public enum PrinterCondition
{
    /// <summary>Idle or printing, with suitable media loaded.</summary>
    Ready,

    /// <summary>The cover is open. A QL-810W or PT-P750W holds a job sent now and prints it once the cover is closed.</summary>
    CoverOpen,

    /// <summary>Nothing loaded. A QL-810W holds a job sent now and prints it once media is loaded and the cover closed.</summary>
    NoMedia,

    /// <summary>Media other than what the caller expects is loaded.</summary>
    WrongMedia,

    /// <summary>Any other fault the printer reports.</summary>
    Error,

    /// <summary>No SNMP answer: switched off, asleep, unplugged or off the network.</summary>
    Unreachable,
}

/// <summary>The raw values a status is judged from. Every field is optional: printers and firmware differ in what they answer.</summary>
public sealed record PrinterStatusSnapshot
{
    /// <summary>hrDeviceStatus (RFC 2790): 2 running, 3 warning, 5 down.</summary>
    public int? DeviceStatus { get; init; }

    /// <summary>hrPrinterStatus (RFC 2790): 3 idle, 4 printing.</summary>
    public int? PrinterStatus { get; init; }

    /// <summary>First octet of hrPrinterDetectedErrorState (RFC 2790).</summary>
    public byte ErrorBits { get; init; }

    /// <summary>prtConsoleDisplayBufferText (RFC 3805): what the printer's display (or status LED) says, e.g. "READY".</summary>
    public string? Display { get; init; }

    /// <summary>prtCoverStatus (RFC 3805): 3 open, 4 closed, 5 interlock open.</summary>
    public int? CoverStatus { get; init; }

    /// <summary>prtInputStatus (RFC 3805) of the media input.</summary>
    public int? InputStatus { get; init; }

    /// <summary>prtInputMediaName (RFC 3805), e.g. "62mm / 2.4\"" or "12mm(0.47\")".</summary>
    public string? MediaName { get; init; }

    /// <summary>Brother's 32-byte status block, when the printer exposes it over SNMP.</summary>
    public BrotherStatusBlock? StatusBlock { get; init; }
}

public sealed record PrinterStatus(PrinterCondition Condition, string Message, PrinterStatusSnapshot? Snapshot)
{
    /// <summary>Whether a job sent now prints now.</summary>
    public bool IsReady => Condition == PrinterCondition.Ready;
}

public interface IPrinterStatusSource
{
    Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns a snapshot into a condition, using the standard printer MIB and, when present, Brother's status
/// block. The MIB rules were worked out on a QL-810W in each physical state. One trap: with the roll
/// taken out while idle the display still says READY and no error bit is set; only the input status
/// gives it away.
/// </summary>
public static class StatusInterpreter
{
    // hrPrinterDetectedErrorState, first octet (RFC 2790).
    private const byte NoPaper = 0x40;
    private const byte DoorOpen = 0x08;
    private const byte Jammed = 0x04;
    private const byte Offline = 0x02;

    // prtCoverStatus coverOpen / interlockOpen (RFC 3805).
    private const int CoverStatusOpen = 3;
    private const int CoverStatusInterlockOpen = 5;

    // PrtSubUnitStatusTC bit 4: critical alert on the input (the roll holder).
    private const int InputCriticalAlert = 0x10;

    // hrDeviceStatus down(5).
    private const int DeviceDown = 5;

    private const BrotherError1 FaultBits1 = BrotherError1.CutterJam | BrotherError1.FanMotor | BrotherError1.WeakBatteries;
    private const BrotherError2 FaultBits2 = BrotherError2.Overheating | BrotherError2.SystemError | BrotherError2.CannotFeed | BrotherError2.CommunicationError;

    /// <param name="snapshot">What the printer answered; null when it did not answer at all.</param>
    /// <param name="expectedWidthMm">The media width jobs are made for, if the caller wants other media reported as <see cref="PrinterCondition.WrongMedia"/>.</param>
    public static PrinterStatus Interpret(PrinterStatusSnapshot? snapshot, int? expectedWidthMm = null)
    {
        if (snapshot is null)
        {
            return new(PrinterCondition.Unreachable, "The printer is not answering. It may be switched off, asleep or off the network.", null);
        }

        var block = snapshot.StatusBlock;
        var display = snapshot.Display?.Trim();

        if ((snapshot.ErrorBits & DoorOpen) != 0
            || snapshot.CoverStatus is CoverStatusOpen or CoverStatusInterlockOpen
            || block?.Error2.HasFlag(BrotherError2.CoverOpen) == true)
        {
            return new(PrinterCondition.CoverOpen, "The printer's cover is open.", snapshot);
        }

        if ((snapshot.ErrorBits & NoPaper) != 0
            || (snapshot.InputStatus is { } input && (input & InputCriticalAlert) != 0)
            || block?.Error1.HasFlag(BrotherError1.NoMedia) == true
            || block is { MediaWidthMm: 0 })
        {
            var load = expectedWidthMm is { } w ? $" Load {w}mm media." : "";
            return new(PrinterCondition.NoMedia, $"The printer has no media loaded.{load}", snapshot);
        }

        if (block?.Error2.HasFlag(BrotherError2.ReplaceMedia) == true)
        {
            return new(PrinterCondition.Error,
                "The printer refused a job that does not suit the loaded media. A QL-8xx stays in this state, with its red light blinking, until it is switched off and on; the job is lost.",
                snapshot);
        }

        if (expectedWidthMm is { } expected)
        {
            var loaded = block?.MediaWidthMm;
            var mediaName = snapshot.MediaName?.Trim();
            if (loaded is { } mm && mm != expected)
            {
                return new(PrinterCondition.WrongMedia, $"The printer has {block!.DescribeMedia()} loaded; the job needs {expected}mm.", snapshot);
            }

            if (loaded is null && !string.IsNullOrEmpty(mediaName) && !mediaName.StartsWith($"{expected}mm", StringComparison.OrdinalIgnoreCase))
            {
                return new(PrinterCondition.WrongMedia, $"The printer has {mediaName} loaded; the job needs {expected}mm.", snapshot);
            }
        }

        if ((snapshot.ErrorBits & (Jammed | Offline)) != 0
            || snapshot.DeviceStatus == DeviceDown
            || string.Equals(display, "ERROR", StringComparison.OrdinalIgnoreCase)
            || (block is not null && ((block.Error1 & FaultBits1) != 0 || (block.Error2 & FaultBits2) != 0)))
        {
            var detail = block is not null && (block.Error1 != 0 || block.Error2 != 0) ? $" (error bits 0x{(byte)block.Error1:X2} 0x{(byte)block.Error2:X2})" : "";
            return new(PrinterCondition.Error, $"The printer reports a fault{(string.IsNullOrEmpty(display) ? "" : $": {display}")}{detail}.", snapshot);
        }

        return new(PrinterCondition.Ready, "Ready.", snapshot);
    }
}

/// <summary>
/// Reads a Brother label printer's status over SNMPv1: the standard printer MIB plus Brother's status
/// block. Works for the QL-800 series and the PT-E550W/P750W, neither of which answers a status
/// request on port 9100.
/// </summary>
public sealed class BrotherSnmpStatusSource(string host) : IPrinterStatusSource
{
    public const string HrDeviceStatus = "1.3.6.1.2.1.25.3.2.1.5.1";
    public const string HrPrinterStatus = "1.3.6.1.2.1.25.3.5.1.1.1";
    public const string HrPrinterDetectedErrorState = "1.3.6.1.2.1.25.3.5.1.2.1";
    public const string PrtConsoleDisplayBufferText = "1.3.6.1.2.1.43.16.5.1.2.1.1";
    public const string PrtCoverStatus = "1.3.6.1.2.1.43.6.1.1.3.1.1";
    public const string PrtInputStatus = "1.3.6.1.2.1.43.8.2.1.11.1.1";
    public const string PrtInputMediaName = "1.3.6.1.2.1.43.8.2.1.12.1.1";

    private static readonly string[] Oids =
        [HrDeviceStatus, HrPrinterStatus, HrPrinterDetectedErrorState, PrtConsoleDisplayBufferText, PrtCoverStatus, PrtInputStatus, PrtInputMediaName, BrotherStatusBlock.SnmpOid];

    public string Host => host;

    public int Port { get; init; } = 161;

    public string Community { get; init; } = "public";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Report media of another width as <see cref="PrinterCondition.WrongMedia"/>.</summary>
    public int? ExpectedWidthMm { get; init; }

    public async Task<PrinterStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        StatusInterpreter.Interpret(await ReadSnapshotAsync(cancellationToken), ExpectedWidthMm);

    /// <summary>The raw values, or null when the printer does not answer.</summary>
    public async Task<PrinterStatusSnapshot?> ReadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var client = new SnmpV1Client(host, Community, Port) { Timeout = Timeout };
        try
        {
            var values = await client.GetAsync(Oids, cancellationToken);
            return values is null ? null : ToSnapshot(values);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
        {
            return null;
        }
    }

    public static PrinterStatusSnapshot ToSnapshot(IReadOnlyDictionary<string, SnmpValue> values)
    {
        int? Int(string oid) => values.TryGetValue(oid, out var v) && v.Integer is { } i ? (int)i : null;
        string? Text(string oid) => values.TryGetValue(oid, out var v) ? v.Text?.TrimEnd('\0') : null;

        return new PrinterStatusSnapshot
        {
            DeviceStatus = Int(HrDeviceStatus),
            PrinterStatus = Int(HrPrinterStatus),
            ErrorBits = values.TryGetValue(HrPrinterDetectedErrorState, out var e) && e.Bytes is { Length: > 0 } b ? b[0] : (byte)0,
            Display = Text(PrtConsoleDisplayBufferText),
            CoverStatus = Int(PrtCoverStatus),
            InputStatus = Int(PrtInputStatus),
            MediaName = Text(PrtInputMediaName),
            StatusBlock = values.TryGetValue(BrotherStatusBlock.SnmpOid, out var s) && s.Bytes is { } raw ? BrotherStatusBlock.TryParse(raw) : null,
        };
    }
}

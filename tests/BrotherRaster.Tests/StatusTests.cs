using System.Text.Json;
using BrotherRaster.Pt;
using BrotherRaster.Status;

namespace BrotherRaster.Tests;

/// <summary>
/// The interpreter against SNMP values captured from a QL-810W in each physical state (Fixtures/printer-status),
/// and the status block parser against blocks read from a PT-P750W and QL-810Ws.
/// </summary>
public sealed class StatusTests
{
    [Theory]
    [InlineData("ready", PrinterCondition.Ready)]
    [InlineData("cover-open", PrinterCondition.CoverOpen)]
    [InlineData("no-media-idle", PrinterCondition.NoMedia)] // display says READY: only the input status tells
    [InlineData("no-media-after-job", PrinterCondition.NoMedia)]
    [InlineData("powered-off", PrinterCondition.Unreachable)]
    [InlineData("job-refused", PrinterCondition.Error)] // a one-color job on the black/red roll
    public void Captured_states_are_recognised(string fixture, PrinterCondition expected)
    {
        var status = StatusInterpreter.Interpret(LoadFixture(fixture), 62);

        Assert.Equal(expected, status.Condition);
        Assert.Equal(expected == PrinterCondition.Ready, status.IsReady);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    [Fact]
    public void A_narrower_roll_is_wrong_media_only_when_a_width_is_expected()
    {
        var snapshot = LoadFixture("ready")! with { MediaName = "29mm / 1.1\"" };

        Assert.Equal(PrinterCondition.WrongMedia, StatusInterpreter.Interpret(snapshot, 62).Condition);
        Assert.Contains("29mm", StatusInterpreter.Interpret(snapshot, 62).Message);
        Assert.Equal(PrinterCondition.Ready, StatusInterpreter.Interpret(snapshot).Condition);
    }

    [Fact]
    public void A_jam_is_an_error()
    {
        var snapshot = LoadFixture("ready")! with { ErrorBits = 0x04, DeviceStatus = 5, Display = "ERROR           " };

        Assert.Equal(PrinterCondition.Error, StatusInterpreter.Interpret(snapshot).Condition);
    }

    [Fact]
    public void Cover_open_wins_over_the_media_it_hides()
    {
        var snapshot = LoadFixture("cover-open")! with { InputStatus = 19 };

        Assert.Equal(PrinterCondition.CoverOpen, StatusInterpreter.Interpret(snapshot).Condition);
    }

    [Fact]
    public void The_p750w_block_names_the_model_and_the_cassette()
    {
        var block = LoadBlock("pt-p750w-12mm-white");

        Assert.Equal(BrotherSeries.Pt, block.Series);
        Assert.Equal("PT-P750W", block.Model);
        Assert.Equal(12, block.MediaWidthMm);
        Assert.Equal(PtMediaType.Laminated, (PtMediaType)block.MediaType);
        Assert.Equal("12mm laminated", block.DescribeMedia());
        Assert.Equal(0x06, block.TapeColor); // the cassette is white (TZe-231); the reference's table says 0x06 is yellow
        Assert.Equal(0x08, block.TextColor);
        Assert.Equal(BrotherStatusType.ReplyToRequest, block.StatusType);
        Assert.Same(PtTape.TZe12, PtTape.Find((PtMediaType)block.MediaType, block.MediaWidthMm));
    }

    [Theory]
    [InlineData("ql-810w-62mm")]
    [InlineData("ql-810w-62mm-black-red")]
    public void The_ql_810w_block_names_the_model_and_the_roll(string id)
    {
        var block = LoadBlock(id);

        Assert.Equal(BrotherSeries.Ql, block.Series);
        Assert.Equal("QL-810W", block.Model);
        Assert.Equal("62mm continuous", block.DescribeMedia());
        Assert.Equal(0, (int)block.Error1 | (int)block.Error2);
    }

    [Fact]
    public void A_block_with_the_media_the_job_needs_is_ready()
    {
        var snapshot = LoadFixture("ready")! with { StatusBlock = LoadBlock("ql-810w-62mm") };

        Assert.Equal(PrinterCondition.Ready, StatusInterpreter.Interpret(snapshot, 62).Condition);
    }

    [Fact]
    public void The_block_reports_other_tape_widths_even_when_the_mib_media_name_is_missing()
    {
        var snapshot = new PrinterStatusSnapshot { DeviceStatus = 2, PrinterStatus = 3, Display = "READY", StatusBlock = LoadBlock("pt-p750w-12mm-white") };

        var status = StatusInterpreter.Interpret(snapshot, 24);

        Assert.Equal(PrinterCondition.WrongMedia, status.Condition);
        Assert.Contains("12mm laminated", status.Message);
        Assert.Equal(PrinterCondition.Ready, StatusInterpreter.Interpret(snapshot, 12).Condition);
    }

    [Theory]
    [InlineData(9, 0x10, PrinterCondition.CoverOpen)]
    [InlineData(8, 0x01, PrinterCondition.NoMedia)]
    [InlineData(10, 0x00, PrinterCondition.NoMedia)] // width 0: nothing loaded
    [InlineData(9, 0x01, PrinterCondition.Error)] // replace media: the printer refused a job
    [InlineData(8, 0x04, PrinterCondition.Error)] // cutter jam
    [InlineData(9, 0x20, PrinterCondition.Error)] // overheating
    public void Block_error_bits_set_the_condition(int offset, byte value, PrinterCondition expected)
    {
        var raw = LoadBlock("pt-p750w-12mm-white").Raw.ToArray();
        raw[offset] = value;
        var snapshot = new PrinterStatusSnapshot { DeviceStatus = 2, Display = "READY", StatusBlock = BrotherStatusBlock.TryParse(raw) };

        Assert.Equal(expected, StatusInterpreter.Interpret(snapshot).Condition);
    }

    [Theory]
    [InlineData("pt-p750w-cover-open", "COVER OPEN", 0x08, 4)]
    [InlineData("pt-p750w-cover-open-job-held", "ERROR", 0x00, 3)]
    public void The_p750w_with_its_cover_open_is_cover_open(string id, string display, byte errorBits, int coverStatus)
    {
        // As captured: the MIB contradicts itself (cover "closed" with the door bit set, then the bit cleared
        // with the cover "open"), but the status block says cover open throughout.
        var block = LoadBlock(id);
        var snapshot = new PrinterStatusSnapshot { DeviceStatus = 5, PrinterStatus = 1, ErrorBits = errorBits, Display = display, CoverStatus = coverStatus, MediaName = "No Tape", StatusBlock = block };

        Assert.Equal(PrinterCondition.CoverOpen, StatusInterpreter.Interpret(snapshot, 12).Condition);
        Assert.Equal(BrotherStatusType.ErrorOccurred, block.StatusType);
        Assert.Equal("no media", block.DescribeMedia()); // the cassette can't be read with the cover open
    }

    [Fact]
    public void Bytes_that_are_not_a_block_are_rejected()
    {
        Assert.Null(BrotherStatusBlock.TryParse(new byte[32]));
        Assert.Null(BrotherStatusBlock.TryParse(LoadBlock("ql-810w-62mm").Raw.AsSpan(0, 31)));
    }

    [Fact]
    public void The_snmp_values_become_a_snapshot_with_the_block()
    {
        var block = LoadBlock("pt-p750w-12mm-white");
        var snapshot = BrotherSnmpStatusSource.ToSnapshot(new Dictionary<string, SnmpValue>
        {
            [BrotherSnmpStatusSource.HrDeviceStatus] = new(SnmpBer.Integer, 2, null),
            [BrotherSnmpStatusSource.PrtConsoleDisplayBufferText] = new(SnmpBer.OctetString, null, "READY           "u8.ToArray()),
            [BrotherSnmpStatusSource.PrtInputMediaName] = new(SnmpBer.OctetString, null, "12mm(0.47\")"u8.ToArray()),
            [BrotherStatusBlock.SnmpOid] = new(SnmpBer.OctetString, null, block.Raw),
        });

        Assert.Equal("PT-P750W", snapshot.StatusBlock?.Model);
        Assert.Equal(PrinterCondition.Ready, StatusInterpreter.Interpret(snapshot, 12).Condition);
    }

    private static BrotherStatusBlock LoadBlock(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestBytes.FixturePath("status-blocks.json")));
        var hex = doc.RootElement.GetProperty("blocks").EnumerateArray().Single(b => b.GetProperty("id").GetString() == id).GetProperty("hex").GetString()!;
        return BrotherStatusBlock.TryParse(Convert.FromHexString(hex))!;
    }

    private static PrinterStatusSnapshot? LoadFixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestBytes.FixturePath("printer-status", $"{name}.json")));
        var values = doc.RootElement.GetProperty("values");
        if (values.TryGetProperty("snmp", out _))
        {
            return null; // no SNMP response was captured
        }

        int? Int(string key) => values.TryGetProperty(key, out var v) ? v.GetInt32() : null;
        string? Text(string key) => values.TryGetProperty(key, out var v) ? v.GetString() : null;

        return new PrinterStatusSnapshot
        {
            DeviceStatus = Int("hrDeviceStatus"),
            PrinterStatus = Int("hrPrinterStatus"),
            ErrorBits = Convert.FromHexString(Text("hrPrinterDetectedErrorState")!)[0],
            Display = Text("prtConsoleDisplayBuffer"),
            CoverStatus = Int("prtCoverStatus.1"),
            InputStatus = Int("prtInputStatus.1"),
            MediaName = Text("prtInputMediaName"),
        };
    }
}

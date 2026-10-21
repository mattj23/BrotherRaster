using BrotherRaster.Pt;

namespace BrotherRaster.Tests;

/// <summary>The PT encoder against the examples in the PT-E550W/P750W/P710BT raster command reference.</summary>
public sealed class PtRasterEncoderTests
{
    [Fact]
    public void Starts_with_the_references_initialization_and_control_codes()
    {
        var job = PtRasterEncoder.Encode(new LabelBitmap(100, 70));

        byte[] expected =
        [
            .. new byte[100], // invalidate: 100 bytes
            0x1B, 0x40, // initialize
            0x1B, 0x69, 0x61, 0x01, // raster mode
            0x1B, 0x69, 0x7A, 0x84, 0x00, 0x0C, 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00, // 12 mm, 100 lines
            0x1B, 0x69, 0x4D, 0x40, // auto cut
            0x1B, 0x69, 0x41, 0x01, // cut every label
            0x1B, 0x69, 0x4B, 0x08, // no chain printing
            0x1B, 0x69, 0x64, 0x0E, 0x00, // 2 mm margins at 180 dpi
            0x4D, 0x02, // TIFF
        ];
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(job.AsSpan(0, expected.Length)));
    }

    [Fact]
    public void Print_information_matches_the_references_24_mm_example()
    {
        // "When printing 100 mm on 24-mm-wide tape with the 180 dpi model": 682 raster lines.
        var job = PtRasterEncoder.Encode(new LabelBitmap(682, 128), new PtRasterOptions { Tape = PtTape.TZe24 });

        Assert.True(TestBytes.IndexOf(job, [0x1B, 0x69, 0x7A, 0x84, 0x00, 0x18, 0x00, 0xAA, 0x02, 0x00, 0x00, 0x00, 0x00]) > 0);
    }

    [Fact]
    public void Blank_columns_are_zero_rows_and_ends_with_print_and_feed()
    {
        var job = PtRasterEncoder.Encode(new LabelBitmap(40, 70));

        Assert.Equal(0x1A, job[^1]);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 40), job[^41..^1]);
    }

    [Fact]
    public void A_dot_lands_after_the_tapes_left_margin_pins()
    {
        var label = new LabelBitmap(40, 70);
        label[0, 0] = true; // first column, top row
        label[39, 69] = true; // last column, bottom row

        var job = RasterDecoder.Decode(PtRasterEncoder.Encode(label, new PtRasterOptions { Compress = false }));

        Assert.Equal(RasterFamily.Pt, job.Family);
        Assert.Equal(40, job.Lines);
        Assert.True(job.Black[29, 0]);
        Assert.True(job.Black[29 + 69, 39]);
        Assert.Equal(2, Enumerable.Range(0, 40).Sum(y => Enumerable.Range(0, 128).Count(x => job.Black[x, y])));
    }

    [Theory]
    [MemberData(nameof(Tapes))]
    public void Round_trips_on_every_tape(string name, bool compress)
    {
        var tape = PtTape.All.Single(t => t.Name == name);
        var rng = new Random(7);
        var label = new LabelBitmap(200, tape.PrintablePins);
        for (var i = 0; i < 2000; i++)
        {
            label[rng.Next(200), rng.Next(tape.PrintablePins)] = true;
        }

        var decoded = RasterDecoder.Decode(PtRasterEncoder.Encode(label, new PtRasterOptions { Tape = tape, Compress = compress }));

        Assert.Equal(tape.WidthMm, decoded.DeclaredWidthMm);
        Assert.Equal(200, decoded.DeclaredRasterLines);
        Assert.True(label.ContentEquals(decoded.ToPtLabel()));
    }

    public static TheoryData<string, bool> Tapes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var tape in PtTape.All)
        {
            data.Add(tape.Name, true);
            data.Add(tape.Name, false);
        }

        return data;
    }

    [Fact]
    public void Uncompressed_rows_are_always_16_bytes()
    {
        var label = new LabelBitmap(31, 70);
        label[3, 3] = true;

        var job = PtRasterEncoder.Encode(label, new PtRasterOptions { Compress = false });

        Assert.Equal(-1, TestBytes.IndexOf(job, [0x4D, 0x02]));
        Assert.Equal(31 * 19, job.Length - TestBytes.IndexOf(job, [0x1B, 0x69, 0x64, 0x0E, 0x00]) - 5 - 1);
    }

    [Fact]
    public void Options_set_the_documented_bits()
    {
        var job = RasterDecoder.Decode(PtRasterEncoder.Encode(new LabelBitmap(100, 70), new PtRasterOptions
        {
            Cut = false,
            Mirror = true,
            HalfCut = true,
            Chain = true,
            HighResolution = true,
            CheckMediaType = true,
        }));

        Assert.Contains("various_mode 0x80", job.Commands);
        Assert.DoesNotContain(job.Commands, c => c.StartsWith("cut_every"));
        Assert.Contains("expanded_mode 0x44", job.Commands);
        Assert.Contains("margins 28", job.Commands);
        Assert.Contains("media_quality flags=0x86 type=0x01 width=12 length=0 lines=100", job.Commands);
    }

    [Fact]
    public void Rejects_labels_that_do_not_fit_the_tape_or_the_length_limits()
    {
        Assert.Throws<ArgumentException>(() => PtRasterEncoder.Encode(new LabelBitmap(100, 64)));
        Assert.Throws<ArgumentException>(() => PtRasterEncoder.Encode(new LabelBitmap(30, 70)));
        Assert.Throws<ArgumentException>(() => PtRasterEncoder.Encode(new LabelBitmap(7087, 70)));
    }

    [Fact]
    public void Every_tape_fits_the_head()
    {
        foreach (var tape in PtTape.All)
        {
            Assert.True(tape.RightMarginPins >= 0, tape.Name);
            Assert.Equal(tape.LeftMarginPins, tape.RightMarginPins); // the reference's tables are symmetric
        }
    }
}

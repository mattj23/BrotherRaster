using System.Text.Json;
using BrotherRaster.Ql;
using BrotherRaster.Skia;

namespace BrotherRaster.Tests;

/// <summary>
/// The QL encoder against jobs brother_ql produced for the same bitmaps (Fixtures/brother-ql, made with
/// brother_ql 0.9.4 as a black box; see tools/brother-ql-oracle). Production QL-810Ws print these jobs.
/// </summary>
public sealed class QlRasterEncoderTests
{
    public static TheoryData<string> Fixtures()
    {
        var data = new TheoryData<string>();
        using var doc = JsonDocument.Parse(File.ReadAllText(TestBytes.FixturePath("brother-ql", "manifest.json")));
        foreach (var f in doc.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            data.Add(f.GetProperty("output").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Matches_brother_ql_byte_for_byte(string fixture)
    {
        // fixture: "<input>.<variant>.bin", or "<input>.<variant>.<media>.bin" for rolls other than 62 mm
        var parts = fixture.Split('.');
        var media = parts.Length == 4 ? QlMedia.All.Single(m => m.Name.Replace(" ", "") == parts[2]) : QlMedia.Continuous62;
        var black = LabelImages.FromPng(File.ReadAllBytes(TestBytes.FixturePath("brother-ql", $"{parts[0]}.png")));
        var redPath = TestBytes.FixturePath("brother-ql", $"{parts[0]}.red.png");
        LabelBitmap? red = parts[1] == "red" ? LabelImages.FromPng(File.ReadAllBytes(redPath)) : null;
        var options = parts[1] switch
        {
            "default" => new QlRasterOptions(),
            "nocut" => new QlRasterOptions { Cut = false },
            "compress" => new QlRasterOptions { Compress = true },
            "blackred" or "red" => new QlRasterOptions { TwoColor = true },
            var v => throw new InvalidOperationException(v),
        } with { Media = media };

        var expected = File.ReadAllBytes(TestBytes.FixturePath("brother-ql", fixture));

        TestBytes.Equal(expected, QlRasterEncoder.Encode(black, options, red));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Decoding_a_fixture_and_encoding_it_again_gives_the_same_job(string fixture)
    {
        var job = File.ReadAllBytes(TestBytes.FixturePath("brother-ql", fixture));
        var decoded = RasterDecoder.Decode(job);
        var media = QlMedia.Find(decoded.DeclaredWidthMm, decoded.DeclaredLengthMm)!;
        var options = new QlRasterOptions
        {
            Media = media,
            Cut = decoded.Commands.Contains("cut_every 1"),
            Compress = decoded.Commands.Contains("compression packbits"),
            TwoColor = decoded.Commands.Any(c => c.StartsWith("expanded_mode") && (Convert.ToByte(c[^2..], 16) & 0x01) != 0),
        };

        Assert.Equal(RasterFamily.Ql, decoded.Family);
        Assert.Equal(decoded.Lines, decoded.DeclaredRasterLines);

        var red = decoded.Red is null ? null : new RasterJob(decoded.Family, [], decoded.Red, null, 0, 0, 0).ToQlLabel(media);
        var black = new RasterJob(decoded.Family, [], decoded.Black, null, 0, 0, 0).ToQlLabel(media);
        TestBytes.Equal(job, QlRasterEncoder.Encode(black, options, red));
    }

    [Fact]
    public void A_two_color_job_with_no_red_decodes_to_the_same_label()
    {
        var label = new LabelBitmap(696, 50);
        label.FillRect(10, 10, 300, 20);

        var twoColor = RasterDecoder.Decode(QlRasterEncoder.Encode(label, new QlRasterOptions { TwoColor = true }));

        Assert.Contains("expanded_mode 0x09", twoColor.Commands);
        Assert.NotNull(twoColor.Red);
        Assert.True(twoColor.Red.ContentEquals(new LabelBitmap(QlMedia.HeadPins, 50))); // nothing in the red plane
        Assert.True(label.ContentEquals(twoColor.ToQlLabel()));
    }

    [Fact]
    public void Rejects_a_bitmap_of_the_wrong_width_for_the_roll()
    {
        Assert.Throws<ArgumentException>(() => QlRasterEncoder.Encode(new LabelBitmap(720, 10)));
        Assert.Throws<ArgumentException>(() => QlRasterEncoder.Encode(new LabelBitmap(696, 10), new QlRasterOptions { Media = QlMedia.Continuous29 }));
    }

    [Fact]
    public void A_die_cut_label_must_be_its_fixed_length_and_has_no_margin()
    {
        var media = QlMedia.DieCut62x29;
        Assert.Throws<ArgumentException>(() => QlRasterEncoder.Encode(new LabelBitmap(696, 100), new QlRasterOptions { Media = media }));

        var job = RasterDecoder.Decode(QlRasterEncoder.Encode(new LabelBitmap(696, media.PrintableLength), new QlRasterOptions { Media = media }));

        Assert.Contains("media_quality flags=0xCE type=0x0B width=62 length=29 lines=271", job.Commands);
        Assert.Contains("margins 0", job.Commands);
    }

    [Fact]
    public void A_red_plane_needs_a_two_color_job_of_the_same_size()
    {
        var black = new LabelBitmap(696, 10);
        Assert.Throws<ArgumentException>(() => QlRasterEncoder.Encode(black, red: new LabelBitmap(696, 10)));
        Assert.Throws<ArgumentException>(() => QlRasterEncoder.Encode(black, new QlRasterOptions { TwoColor = true }, new LabelBitmap(696, 11)));
    }

    [Fact]
    public void Every_roll_fills_the_head()
    {
        foreach (var media in QlMedia.All)
        {
            Assert.True(media.RightMarginPins >= 0, media.Name);
            Assert.Equal(QlMedia.HeadPins, media.LeftMarginPins + media.PrintableDots + media.RightMarginPins);
        }
    }
}

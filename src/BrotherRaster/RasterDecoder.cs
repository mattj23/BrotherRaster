using System.Buffers.Binary;
using BrotherRaster.Pt;
using BrotherRaster.Ql;

namespace BrotherRaster;

/// <summary>Which head a job was written for, judged from its row commands.</summary>
public enum RasterFamily
{
    /// <summary>QL-800 series: 720 pins, rows sent as <c>g</c> or <c>w</c>.</summary>
    Ql,

    /// <summary>PT-E550W/P750W/P710BT: 128 pins, rows sent as <c>G</c>.</summary>
    Pt,
}

/// <summary>
/// A parsed raster job. <see cref="Black"/> and <see cref="Red"/> hold the raster lines as sent: one
/// bitmap row per line, bit <c>x</c> of the line in column <c>x</c> (from the most significant bit of
/// the first byte), so they are head-wide and not yet the label as read.
/// </summary>
public sealed record RasterJob(
    RasterFamily Family,
    IReadOnlyList<string> Commands,
    LabelBitmap Black,
    LabelBitmap? Red,
    int DeclaredRasterLines,
    int DeclaredWidthMm,
    int DeclaredLengthMm)
{
    /// <summary>Number of raster lines in the job.</summary>
    public int Lines => Black.Height;

    /// <summary>The QL label as read, both planes inked. The roll defaults to the one the job declares.</summary>
    public LabelBitmap ToQlLabel(QlMedia? media = null)
    {
        media ??= QlMedia.Find(DeclaredWidthMm, DeclaredLengthMm)
            ?? throw new InvalidOperationException($"No QL roll is {DeclaredWidthMm} x {DeclaredLengthMm} mm.");
        var label = new LabelBitmap(media.PrintableDots, Lines);
        for (var y = 0; y < Lines; y++)
        {
            for (var x = 0; x < media.PrintableDots; x++)
            {
                var bit = QlRasterEncoder.HeadBit(media, x);
                label[x, y] = Black[bit, y] || (Red?[bit, y] ?? false);
            }
        }

        return label;
    }

    /// <summary>The PT label as read along the tape. The tape defaults to the TZe width the job declares.</summary>
    public LabelBitmap ToPtLabel(PtTape? tape = null)
    {
        tape ??= PtTape.Find(PtMediaType.Laminated, DeclaredWidthMm)
            ?? throw new InvalidOperationException($"No TZe tape is {DeclaredWidthMm} mm wide.");
        var label = new LabelBitmap(Lines, tape.PrintablePins);
        for (var x = 0; x < Lines; x++)
        {
            for (var y = 0; y < tape.PrintablePins; y++)
            {
                label[x, y] = Black[PtRasterEncoder.HeadBit(tape, y), x];
            }
        }

        return label;
    }
}

/// <summary>Parses QL and PT raster jobs, for tests, diagnostics and previews of captured jobs.</summary>
public static class RasterDecoder
{
    public static RasterJob Decode(ReadOnlySpan<byte> data)
    {
        var commands = new List<string>();
        var black = new List<byte[]?>(); // null: a Z row, sized once the family is known
        var red = new List<byte[]?>();
        RasterFamily? family = null;
        var compressed = false;
        int declared = 0, widthMm = 0, lengthMm = 0;
        var i = 0;

        while (i < data.Length)
        {
            var b = data[i];
            if (b == 0x00)
            {
                var start = i;
                while (i < data.Length && data[i] == 0x00)
                {
                    i++;
                }

                commands.Add($"invalidate {i - start}");
            }
            else if (b == 0x1B && i + 1 < data.Length && data[i + 1] == 0x40)
            {
                commands.Add("initialize");
                i += 2;
            }
            else if (b == 0x1B && i + 2 < data.Length && data[i + 1] == 0x69)
            {
                var c = data[i + 2];
                switch (c)
                {
                    case 0x61: commands.Add($"switch_mode {data[i + 3]}"); i += 4; break;
                    case 0x53: commands.Add("status_request"); i += 3; break;
                    case 0x21: commands.Add($"status_notification {data[i + 3]}"); i += 4; break;
                    case 0x7A:
                        widthMm = data[i + 5];
                        lengthMm = data[i + 6];
                        declared = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(i + 7, 4));
                        commands.Add($"media_quality flags=0x{data[i + 3]:X2} type=0x{data[i + 4]:X2} width={widthMm} length={lengthMm} lines={declared}");
                        i += 13;
                        break;
                    case 0x4D: commands.Add($"various_mode 0x{data[i + 3]:X2}"); i += 4; break;
                    case 0x41: commands.Add($"cut_every {data[i + 3]}"); i += 4; break;
                    case 0x4B: commands.Add($"expanded_mode 0x{data[i + 3]:X2}"); i += 4; break;
                    case 0x64: commands.Add($"margins {BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 3, 2))}"); i += 5; break;
                    default: throw new FormatException($"Unknown ESC i 0x{c:X2} at offset {i}.");
                }
            }
            else if (b == 0x4D)
            {
                compressed = data[i + 1] == 0x02;
                commands.Add($"compression {(compressed ? "packbits" : "none")}");
                i += 2;
            }
            else if (b is 0x67 or 0x77)
            {
                family = Expect(family, RasterFamily.Ql, i);
                var length = data[i + 2];
                var row = Row(data.Slice(i + 3, length), compressed);
                if (b == 0x77 && data[i + 1] == 0x02)
                {
                    red.Add(row);
                }
                else
                {
                    black.Add(row);
                }

                i += 3 + length;
            }
            else if (b == 0x47)
            {
                family = Expect(family, RasterFamily.Pt, i);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 1, 2));
                black.Add(Row(data.Slice(i + 3, length), compressed));
                i += 3 + length;
            }
            else if (b == 0x5A)
            {
                black.Add(null);
                red.Add(null);
                i++;
            }
            else if (b == 0x1A)
            {
                commands.Add("print_last");
                i++;
            }
            else if (b == 0x0C)
            {
                commands.Add("print_page");
                i++;
            }
            else
            {
                throw new FormatException($"Unknown byte 0x{b:X2} at offset {i}.");
            }
        }

        // A job of nothing but Z rows names no family; take it from the declared width.
        family ??= widthMm > 24 ? RasterFamily.Ql : RasterFamily.Pt;
        var pins = family == RasterFamily.Ql ? QlMedia.HeadPins : PtTape.HeadPins;
        var twoColor = red.Any(r => r is not null);

        commands.Insert(0, $"raster_rows {black.Count}");
        return new RasterJob(
            family.Value,
            commands,
            ToBitmap(black, pins),
            twoColor ? ToBitmap(red, pins) : null,
            declared,
            widthMm,
            lengthMm);
    }

    private static RasterFamily Expect(RasterFamily? seen, RasterFamily family, int offset) =>
        seen is null || seen == family ? family : throw new FormatException($"QL and PT row commands mixed at offset {offset}.");

    private static byte[] Row(ReadOnlySpan<byte> payload, bool compressed) => compressed ? PackBits.Decode(payload) : payload.ToArray();

    private static LabelBitmap ToBitmap(List<byte[]?> rows, int pins)
    {
        var bitmap = new LabelBitmap(pins, Math.Max(1, rows.Count));
        for (var y = 0; y < rows.Count; y++)
        {
            if (rows[y] is not { } row)
            {
                continue;
            }

            for (var x = 0; x < Math.Min(pins, row.Length * 8); x++)
            {
                bitmap[x, y] = RasterRows.Get(row, x);
            }
        }

        return bitmap;
    }
}

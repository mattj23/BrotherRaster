using System.Buffers.Binary;

namespace BrotherRaster;

/// <summary>
/// The commands both raster references (QL-800 series, PT-E550W/P750W/P710BT) define with the same
/// bytes. Each family's encoder decides which of them a job uses, and in what order.
/// </summary>
internal sealed class RasterCommands(int capacity)
{
    private readonly MemoryStream _out = new(capacity);

    /// <summary>NULL: skipped by the printer. A run of them flushes a half-received job.</summary>
    public void Invalidate(int count) => _out.Write(new byte[count]);

    /// <summary>ESC @: clears the mode settings.</summary>
    public void Initialize() => _out.Write([0x1B, 0x40]);

    /// <summary>ESC i a 01: raster mode, whatever the printer's Command Mode setting is.</summary>
    public void SwitchToRaster() => _out.Write([0x1B, 0x69, 0x61, 0x01]);

    /// <summary>ESC i S: status request. Answered only over USB (and not at all by the PT-P750W).</summary>
    public void StatusRequest() => _out.Write([0x1B, 0x69, 0x53]);

    /// <summary>ESC i z: print information.</summary>
    public void PrintInformation(PrintInformationFlags flags, byte mediaType, byte widthMm, byte lengthMm, int rasterLines, bool firstPage)
    {
        Span<byte> command = stackalloc byte[13];
        command[0] = 0x1B;
        command[1] = 0x69;
        command[2] = 0x7A;
        command[3] = (byte)flags;
        command[4] = mediaType;
        command[5] = widthMm;
        command[6] = lengthMm;
        BinaryPrimitives.WriteInt32LittleEndian(command[7..11], rasterLines);
        command[11] = firstPage ? (byte)0 : (byte)1;
        command[12] = 0x00;
        _out.Write(command);
    }

    /// <summary>ESC i M: various mode settings. Auto cut is 0x40 in both families.</summary>
    public void VariousMode(byte mode) => _out.Write([0x1B, 0x69, 0x4D, mode]);

    /// <summary>ESC i A: cut every n labels.</summary>
    public void CutEvery(byte labels) => _out.Write([0x1B, 0x69, 0x41, labels]);

    /// <summary>ESC i K: expanded (QL) or advanced (PT) mode. The bits differ by family.</summary>
    public void ExpandedMode(byte mode) => _out.Write([0x1B, 0x69, 0x4B, mode]);

    /// <summary>ESC i d: feed margin, in dots along the tape.</summary>
    public void Margin(int dots)
    {
        Span<byte> command = [0x1B, 0x69, 0x64, 0x00, 0x00];
        BinaryPrimitives.WriteUInt16LittleEndian(command[3..], checked((ushort)dots));
        _out.Write(command);
    }

    /// <summary>M 02: TIFF (PackBits) row compression.</summary>
    public void SelectTiffCompression() => _out.Write([0x4D, 0x02]);

    /// <summary>Z: an all-blank row. Only valid with TIFF compression selected.</summary>
    public void ZeroRow() => _out.WriteByte(0x5A);

    /// <summary>QL "g 00 n" or "w c n": one-byte length.</summary>
    public void ShortRow(byte command, byte parameter, ReadOnlySpan<byte> row, bool compress)
    {
        var data = compress ? PackBits.Encode(row) : row.ToArray();
        _out.Write([command, parameter, checked((byte)data.Length)]);
        _out.Write(data);
    }

    /// <summary>PT "G nL nH": two-byte length.</summary>
    public void LongRow(ReadOnlySpan<byte> row, bool compress)
    {
        var data = compress ? PackBits.Encode(row) : row.ToArray();
        Span<byte> header = [0x47, 0x00, 0x00];
        BinaryPrimitives.WriteUInt16LittleEndian(header[1..], checked((ushort)data.Length));
        _out.Write(header);
        _out.Write(data);
    }

    /// <summary>FF: print this page, more follow.</summary>
    public void PrintPage() => _out.WriteByte(0x0C);

    /// <summary>Control-Z: print the last page and feed it out.</summary>
    public void PrintLastPage() => _out.WriteByte(0x1A);

    public byte[] ToArray() => _out.ToArray();
}

[Flags]
internal enum PrintInformationFlags : byte
{
    None = 0,
    MediaType = 0x02,
    MediaWidth = 0x04,
    MediaLength = 0x08,
    Quality = 0x40,
    Recover = 0x80,
}

internal static class RasterRows
{
    /// <summary>Sets the dot at <paramref name="bit"/>, counted from the most significant bit of the first byte.</summary>
    public static void Set(Span<byte> row, int bit) => row[bit >> 3] |= (byte)(0x80 >> (bit & 7));

    public static bool Get(ReadOnlySpan<byte> row, int bit) => (row[bit >> 3] & (0x80 >> (bit & 7))) != 0;
}

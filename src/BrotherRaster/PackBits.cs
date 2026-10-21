namespace BrotherRaster;

/// <summary>
/// TIFF PackBits, as the raster references describe it under "Select compression mode". Runs of two or
/// more equal bytes are always encoded as runs. A run longer than 128 is split into 127-byte runs
/// until 128 or fewer remain, and literals are cut at 127 bytes except for the one that ends the data.
/// These choices are what the Python <c>packbits</c> package does, so compressed jobs are byte for byte
/// the same as those produced by brother_ql, which the tests use as an oracle.
/// </summary>
internal static class PackBits
{
    private const int MaxLiteral = 127;
    private const int MaxRun = 128;
    private const int SplitRun = 127;

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>(data.Length + data.Length / 64 + 2);
        var literal = new List<byte>(MaxLiteral + 1);
        var i = 0;

        while (i < data.Length)
        {
            var run = 1;
            while (i + run < data.Length && data[i + run] == data[i])
            {
                run++;
            }

            if (run >= 2)
            {
                FlushLiteral(result, literal);
                while (run > MaxRun)
                {
                    WriteRun(result, data[i], SplitRun);
                    run -= SplitRun;
                    i += SplitRun;
                }

                WriteRun(result, data[i], run);
                i += run;
            }
            else
            {
                if (literal.Count == MaxLiteral && i < data.Length - 1)
                {
                    FlushLiteral(result, literal);
                }

                literal.Add(data[i]);
                i++;
            }
        }

        FlushLiteral(result, literal);
        return [.. result];
    }

    public static byte[] Decode(ReadOnlySpan<byte> data)
    {
        var result = new List<byte>();
        var i = 0;
        while (i < data.Length)
        {
            var header = (sbyte)data[i++];
            if (header >= 0)
            {
                result.AddRange(data.Slice(i, header + 1));
                i += header + 1;
            }
            else if (header != -128)
            {
                for (var n = 0; n < 1 - header; n++)
                {
                    result.Add(data[i]);
                }

                i++;
            }
        }

        return [.. result];
    }

    private static void WriteRun(List<byte> result, byte value, int count)
    {
        result.Add((byte)(257 - count));
        result.Add(value);
    }

    private static void FlushLiteral(List<byte> result, List<byte> literal)
    {
        if (literal.Count == 0)
        {
            return;
        }

        result.Add((byte)(literal.Count - 1));
        result.AddRange(literal);
        literal.Clear();
    }
}

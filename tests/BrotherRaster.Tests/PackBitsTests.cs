namespace BrotherRaster.Tests;

public sealed class PackBitsTests
{
    [Fact]
    public void Encodes_the_example_in_the_raster_reference()
    {
        byte[] row = [.. new byte[20], 0x22, 0x22, 0x23, 0xBA, 0xBF, 0xA2, 0x22, 0x2B];

        Assert.Equal("ED00FF220523BABFA2222B", Convert.ToHexString(PackBits.Encode(row)));
    }

    [Theory]
    [InlineData(128, "8100")] // one run of 128
    [InlineData(129, "8200FF00")] // 127, then 2
    [InlineData(255, "82008100")] // 127, then 128
    public void Long_runs_split_like_the_python_packbits_package(int length, string expected) =>
        Assert.Equal(expected, Convert.ToHexString(PackBits.Encode(new byte[length])));

    [Fact]
    public void Round_trips()
    {
        var rng = new Random(42);
        for (var n = 0; n < 500; n++)
        {
            var data = new byte[rng.Next(1, 400)];
            for (var i = 0; i < data.Length; i++)
            {
                data[i] = rng.Next(3) == 0 ? (byte)rng.Next(256) : (byte)0;
            }

            Assert.Equal(data, PackBits.Decode(PackBits.Encode(data)));
        }
    }
}

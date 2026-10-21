namespace BrotherRaster.Tests;

internal static class TestBytes
{
    public static string FixturePath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Fixtures", .. parts]);

    public static void Equal(byte[] expected, byte[] actual)
    {
        var first = expected.Zip(actual).Select((p, i) => (p, i)).FirstOrDefault(x => x.p.First != x.p.Second, ((default, default), -1)).i;
        if (first == -1 && expected.Length != actual.Length)
        {
            first = Math.Min(expected.Length, actual.Length);
        }

        Assert.True(first == -1, $"lengths {expected.Length} vs {actual.Length}; first difference at offset {first}");
    }

    public static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
    }
}

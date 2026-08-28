
namespace PkLibSharp.Tests.Infrastructure;

/// <summary>
/// The sample inputs shared by the compression tests.
/// </summary>
/// <remarks>
/// Payloads are looked up by name rather than passed through <c>[MemberData]</c> directly, so that
/// test names stay readable and xUnit does not have to serialise hundreds of kilobytes per case.
/// </remarks>
public static class TestPayloads
{
    /// <summary>Fixed seed, so a failure is reproducible.</summary>
    private const int Seed = 20260828;

    private static readonly Dictionary<string, byte[]> Payloads = Build();

    /// <summary>Gets the name of every sample payload.</summary>
    public static IEnumerable<string> Names => Payloads.Keys;

    /// <summary>Gets the sample payload with the given name.</summary>
    public static byte[] Get(string name) => Payloads[name];

    /// <summary>Every payload crossed with every compression setting.</summary>
    public static TheoryData<string, CompressionType, DictionarySize> AllSettings()
    {
        TheoryData<string, CompressionType, DictionarySize> data = [];

        foreach (string name in Names)
        {
            foreach (DictionarySize dictionarySize in DictionarySizes)
            {
                foreach (CompressionType compressionType in CompressionTypes)
                {
                    data.Add(name, compressionType, dictionarySize);
                }
            }
        }

        return data;
    }

    /// <summary>Every payload, with the default compression settings.</summary>
    public static TheoryData<string> AllNames()
    {
        TheoryData<string> data = [];

        foreach (string name in Names)
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Every defined dictionary size.</summary>
    public static readonly DictionarySize[] DictionarySizes =
    [
        DictionarySize.Size1024,
        DictionarySize.Size2048,
        DictionarySize.Size4096,
    ];

    /// <summary>Every defined compression type.</summary>
    public static readonly CompressionType[] CompressionTypes =
    [
        CompressionType.Binary,
        CompressionType.Ascii,
    ];

    private static Dictionary<string, byte[]> Build()
    {
        Random random = new(Seed);

        Dictionary<string, byte[]> payloads = new()
        {
            ["empty"] = [],
            ["one byte"] = [0x5A],
            ["two bytes"] = "AB"u8.ToArray(),

            // The two cases the comments in implode.c call out by name.
            ["ARROCKFORT"] = "ARROCKFORT AROCKFORT ARROCKFORT AROCKFORT"u8.ToArray(),
            ["EEQQ pathological"] = System.Text.Encoding.ASCII.GetBytes(
                new string('E', 32) + new string('Q', 12) + "XYZ" + new string('E', 16) + new string('Q', 12)),

            ["ascii text"] = System.Text.Encoding.ASCII.GetBytes(
                string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400))),

            // A single repeated byte exercises the longest possible repetitions.
            ["all same byte"] = [.. Enumerable.Repeat((byte)0x37, 100_000)],

            // A 256 byte cycle: highly compressible, but only through long repetitions at a
            // fixed distance, which is a different path to the runs above.
            ["counting bytes"] = [.. Enumerable.Range(0, 100_000).Select(i => (byte)i)],
        };

        byte[] randomBinary = new byte[200_000];
        random.NextBytes(randomBinary);
        payloads["random binary"] = randomBinary;

        // Drawn from a small alphabet, so short repetitions are everywhere.
        byte[] lowEntropy = new byte[200_000];
        for (int i = 0; i < lowEntropy.Length; i++)
        {
            lowEntropy[i] = (byte)"abcdefgh"[random.Next(8)];
        }

        payloads["low entropy"] = lowEntropy;

        // Long runs interleaved with noise, so repetitions straddle the internal block boundaries.
        List<byte> mixed = [];
        while (mixed.Count < 300_000)
        {
            if (random.Next(2) == 0)
            {
                mixed.AddRange(Enumerable.Repeat((byte)random.Next(256), random.Next(1, 600)));
            }
            else
            {
                byte[] noise = new byte[random.Next(1, 200)];
                random.NextBytes(noise);
                mixed.AddRange(noise);
            }
        }

        payloads["mixed runs and noise"] = [.. mixed];

        // Sizes sitting exactly on the 0x1000 block and 0x2204 work buffer boundaries, where the
        // window slides and the final partial block is handled differently.
        foreach (int size in new[] { 0x0FFF, 0x1000, 0x1001, 0x1204, 0x2000, 0x2204, 0x3000 })
        {
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++)
            {
                data[i] = (byte)((i * 7) ^ (i >> 5));
            }

            payloads[$"boundary {size:X4}"] = data;
        }

        return payloads;
    }
}

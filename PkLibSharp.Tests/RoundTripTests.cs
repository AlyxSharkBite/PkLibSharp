using PkLibSharp.Tests.Infrastructure;

namespace PkLibSharp.Tests;

/// <summary>
/// End to end tests: whatever is compressed must come back unchanged.
/// </summary>
public class RoundTripTests
{
    public static TheoryData<string, CompressionType, DictionarySize> AllSettings => TestPayloads.AllSettings();

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_ThenDecompress_ReturnsTheOriginal(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] original = TestPayloads.Get(payloadName);

        // Compressing nothing produces a stream too short for the decompressor to accept. That is
        // the original C library's behaviour and is covered by its own test.
        if (original.Length == 0)
        {
            return;
        }

        byte[] compressed = Imploder.Compress(original, compressionType, dictionarySize);
        byte[] restored = Exploder.Decompress(compressed);

        Assert.Equal(original, restored);
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_ThenDecompress_ThroughStreams_ReturnsTheOriginal(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] original = TestPayloads.Get(payloadName);

        if (original.Length == 0)
        {
            return;
        }

        using MemoryStream source = new(original);
        using MemoryStream compressed = new();
        Imploder.Compress(source, compressed, compressionType, dictionarySize);

        compressed.Position = 0;
        using MemoryStream restored = new();
        Exploder.Decompress(compressed, restored);

        Assert.Equal(original, restored.ToArray());
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_ThroughStreams_MatchesCompressInMemory(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] original = TestPayloads.Get(payloadName);

        using MemoryStream source = new(original);
        using MemoryStream compressed = new();
        Imploder.Compress(source, compressed, compressionType, dictionarySize);

        Assert.Equal(Imploder.Compress(original, compressionType, dictionarySize), compressed.ToArray());
    }

    /// <summary>
    /// A source that only ever hands over a few bytes at a time must not change the result. The
    /// algorithm loops on short reads, so a bug there would show up as different output.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    [InlineData(0x1000)]
    public void Compress_WithPartialReads_ProducesTheSameOutput(int maxBytesPerRead)
    {
        byte[] original = TestPayloads.Get("mixed runs and noise");

        ChunkedReader reader = new(original, maxBytesPerRead);
        CollectingWriter writer = new();
        Imploder.Compress(reader.Read, writer.Write);

        Assert.Equal(Imploder.Compress(original), writer.ToArray());
    }

    /// <summary>
    /// The same, for the decompressor's input side.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0x800)]
    public void Decompress_WithPartialReads_ProducesTheSameOutput(int maxBytesPerRead)
    {
        byte[] original = TestPayloads.Get("mixed runs and noise");
        byte[] compressed = Imploder.Compress(original);

        ChunkedReader reader = new(compressed, maxBytesPerRead);
        CollectingWriter writer = new();
        Exploder.Decompress(reader.Read, writer.Write);

        Assert.Equal(original, writer.ToArray());
    }

    /// <summary>
    /// Randomised inputs biased towards small alphabets, where repetitions are dense and the
    /// repetition search is worked hardest.
    /// </summary>
    [Theory]
    [InlineData(CompressionType.Binary, DictionarySize.Size1024)]
    [InlineData(CompressionType.Binary, DictionarySize.Size2048)]
    [InlineData(CompressionType.Binary, DictionarySize.Size4096)]
    [InlineData(CompressionType.Ascii, DictionarySize.Size1024)]
    [InlineData(CompressionType.Ascii, DictionarySize.Size2048)]
    [InlineData(CompressionType.Ascii, DictionarySize.Size4096)]
    public void Compress_ThenDecompress_SurvivesFuzzedInput(CompressionType compressionType, DictionarySize dictionarySize)
    {
        Random random = new(1234);

        for (int iteration = 0; iteration < 150; iteration++)
        {
            byte[] original = new byte[random.Next(1, 9000)];
            int alphabet = 1 << random.Next(1, 9);

            for (int i = 0; i < original.Length; i++)
            {
                original[i] = (byte)random.Next(alphabet);
            }

            byte[] restored = Exploder.Decompress(Imploder.Compress(original, compressionType, dictionarySize));

            Assert.True(
                original.AsSpan().SequenceEqual(restored),
                $"iteration {iteration} failed: length {original.Length}, alphabet {alphabet}");
        }
    }

    /// <summary>
    /// Every length from nothing up to past the first internal block boundary, so an off by one in
    /// the window sliding cannot hide.
    /// </summary>
    [Fact]
    public void Compress_ThenDecompress_HandlesEveryLengthAcrossABlockBoundary()
    {
        byte[] source = TestPayloads.Get("low entropy");

        for (int length = 1; length < 0x1210; length++)
        {
            byte[] original = source[..length];
            byte[] restored = Exploder.Decompress(Imploder.Compress(original, CompressionType.Ascii, DictionarySize.Size1024));

            Assert.True(original.AsSpan().SequenceEqual(restored), $"length {length} failed");
        }
    }
}

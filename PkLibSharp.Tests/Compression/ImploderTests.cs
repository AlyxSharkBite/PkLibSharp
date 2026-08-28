using PkLibSharp.Tests.Infrastructure;

namespace PkLibSharp.Tests.Compression;

/// <summary>
/// Tests for <see cref="Imploder"/>.
/// </summary>
public class ImploderTests
{
    public static TheoryData<string, CompressionType, DictionarySize> AllSettings => TestPayloads.AllSettings();

    /// <summary>
    /// A stream assembled by hand from the format definition: binary mode, 1024 byte dictionary, the
    /// literals 'A' and 'B', then the 16 bit end of stream code. This anchors the encoder to the
    /// format rather than to the decoder, so a matching pair of bugs cannot pass unnoticed.
    /// </summary>
    [Fact]
    public void Compress_ProducesTheExpectedBytesForAKnownInput()
    {
        byte[] compressed = Imploder.Compress("AB"u8.ToArray(), CompressionType.Binary, DictionarySize.Size1024);

        Assert.Equal(new byte[] { 0x00, 0x04, 0x82, 0x08, 0x05, 0xFC, 0x03 }, compressed);
    }

    /// <summary>
    /// Compressing nothing yields the two header bytes and the end of stream code, and nothing else.
    /// The result is four bytes, which <see cref="Exploder"/> rejects as too short, exactly as the
    /// original C library does.
    /// </summary>
    [Fact]
    public void Compress_WithEmptyInput_ProducesHeaderAndTerminatorOnly()
    {
        byte[] compressed = Imploder.Compress(Array.Empty<byte>(), CompressionType.Binary, DictionarySize.Size1024);

        Assert.Equal(new byte[] { 0x00, 0x04, 0x01, 0xFF }, compressed);
        Assert.Equal(PkLibError.BadData, Exploder.TryDecompress(compressed, out _));
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_RecordsTheCompressionTypeInTheHeader(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] compressed = Imploder.Compress(TestPayloads.Get(payloadName), compressionType, dictionarySize);

        Assert.Equal((byte)compressionType, compressed[0]);
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_RecordsTheDictionarySizeInTheHeader(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] compressed = Imploder.Compress(TestPayloads.Get(payloadName), compressionType, dictionarySize);

        byte expectedBits = dictionarySize switch
        {
            DictionarySize.Size1024 => 4,
            DictionarySize.Size2048 => 5,
            _ => 6,
        };

        Assert.Equal(expectedBits, compressed[1]);
    }

    [Fact]
    public void Compress_DefaultsToBinaryWithTheLargestDictionary()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        byte[] withDefaults = Imploder.Compress(payload);
        byte[] explicitly = Imploder.Compress(payload, CompressionType.Binary, DictionarySize.Size4096);

        Assert.Equal(explicitly, withDefaults);
    }

    [Fact]
    public void Compress_DefaultsToBinary_EvenWhenOnlyTheDictionarySizeIsGiven()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        byte[] compressed = Imploder.Compress(payload, dictionarySize: DictionarySize.Size1024);

        Assert.Equal((byte)CompressionType.Binary, compressed[0]);
    }

    [Fact]
    public void Compress_ActuallyCompressesRepetitiveData()
    {
        byte[] payload = TestPayloads.Get("all same byte");

        byte[] compressed = Imploder.Compress(payload);

        Assert.True(compressed.Length < payload.Length / 50, $"expected heavy compression, got {compressed.Length} bytes from {payload.Length}");
    }

    [Fact]
    public void Compress_AsciiModeBeatsBinaryModeOnText()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        int ascii = Imploder.Compress(payload, CompressionType.Ascii).Length;
        int binary = Imploder.Compress(payload, CompressionType.Binary).Length;

        Assert.True(ascii < binary, $"expected ASCII mode to win on text, got {ascii} vs {binary}");
    }

    [Fact]
    public void Compress_BinaryModeBeatsAsciiModeOnRandomBytes()
    {
        byte[] payload = TestPayloads.Get("random binary");

        int ascii = Imploder.Compress(payload, CompressionType.Ascii).Length;
        int binary = Imploder.Compress(payload, CompressionType.Binary).Length;

        Assert.True(binary < ascii, $"expected binary mode to win on random data, got {binary} vs {ascii}");
    }

    [Fact]
    public void Compress_IsDeterministic()
    {
        byte[] payload = TestPayloads.Get("mixed runs and noise");

        Assert.Equal(
            Imploder.Compress(payload, CompressionType.Ascii, DictionarySize.Size2048),
            Imploder.Compress(payload, CompressionType.Ascii, DictionarySize.Size2048));
    }

    [Fact]
    public void Compress_WithUndefinedCompressionType_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Imploder.Compress(new byte[] { 1, 2, 3 }, (CompressionType)7));

        Assert.Equal("compressionType", exception.ParamName);
    }

    [Fact]
    public void Compress_WithUndefinedDictionarySize_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Imploder.Compress(new byte[] { 1, 2, 3 }, CompressionType.Binary, (DictionarySize)512));

        Assert.Equal("dictionarySize", exception.ParamName);
    }

    [Fact]
    public void Compress_WithNullSourceStream_Throws()
    {
        using MemoryStream destination = new();

        Assert.Throws<ArgumentNullException>(() => Imploder.Compress(null!, destination));
    }

    [Fact]
    public void Compress_WithNullDestinationStream_Throws()
    {
        using MemoryStream source = new();

        Assert.Throws<ArgumentNullException>(() => Imploder.Compress(source, null!));
    }

    [Fact]
    public void Compress_WithNullCallbacks_Throws()
    {
        CollectingWriter writer = new();
        ChunkedReader reader = new([1, 2, 3]);

        Assert.Throws<ArgumentNullException>(() => Imploder.Compress(null!, writer.Write));
        Assert.Throws<ArgumentNullException>(() => Imploder.Compress(reader.Read, null!));
    }

    /// <summary>
    /// The compressor writes in 0x800 byte blocks, so anything large enough must arrive in several
    /// calls rather than one.
    /// </summary>
    [Fact]
    public void Compress_WritesOutputIncrementally()
    {
        CollectingWriter writer = new();
        // Random data barely compresses, so the output comfortably exceeds one 0x800 block.
        ChunkedReader reader = new(TestPayloads.Get("random binary"));

        Imploder.Compress(reader.Read, writer.Write);

        Assert.True(writer.WriteCount > 1, $"expected several writes, got {writer.WriteCount}");
    }
}

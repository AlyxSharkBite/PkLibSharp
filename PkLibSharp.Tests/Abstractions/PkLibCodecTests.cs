using PkLibSharp.Tests.Infrastructure;

namespace PkLibSharp.Tests.Abstractions;

/// <summary>
/// Tests for the codec returned by <see cref="PkLibCodecFactory"/>.
/// </summary>
public class PkLibCodecTests
{
    private readonly IPkLibCodec _codec = PkLibCodecFactory.Default.Create();

    public static TheoryData<string, CompressionType, DictionarySize> AllSettings => TestPayloads.AllSettings();

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_MatchesTheStaticApi(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] payload = TestPayloads.Get(payloadName);

        Assert.Equal(
            Imploder.Compress(payload, compressionType, dictionarySize),
            _codec.Compress(payload, compressionType, dictionarySize));
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Decompress_MatchesTheStaticApi(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] payload = TestPayloads.Get(payloadName);

        if (payload.Length == 0)
        {
            return;
        }

        byte[] compressed = Imploder.Compress(payload, compressionType, dictionarySize);

        Assert.Equal(Exploder.Decompress(compressed), _codec.Decompress(compressed));
    }

    /// <summary>
    /// The point of moving the settings onto the call: one codec serves both modes, and using it
    /// for one must not affect the next call.
    /// </summary>
    [Fact]
    public void Compress_WithAlternatingModes_KeepsEachCallIndependent()
    {
        byte[] text = TestPayloads.Get("ascii text");
        byte[] binary = TestPayloads.Get("random binary");

        byte[] firstAscii = _codec.Compress(text, CompressionType.Ascii);
        byte[] asBinary = _codec.Compress(binary, CompressionType.Binary);
        byte[] secondAscii = _codec.Compress(text, CompressionType.Ascii);

        Assert.Equal(firstAscii, secondAscii);
        Assert.Equal((byte)CompressionType.Ascii, firstAscii[0]);
        Assert.Equal((byte)CompressionType.Binary, asBinary[0]);
        Assert.Equal(text, _codec.Decompress(firstAscii));
        Assert.Equal(binary, _codec.Decompress(asBinary));
    }

    [Fact]
    public void Compress_HonoursTheDictionarySizePerCall()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        byte[] small = _codec.Compress(payload, CompressionType.Ascii, DictionarySize.Size1024);
        byte[] large = _codec.Compress(payload, CompressionType.Ascii, DictionarySize.Size4096);

        Assert.Equal(4, small[1]);
        Assert.Equal(6, large[1]);
        Assert.Equal(payload, _codec.Decompress(small));
        Assert.Equal(payload, _codec.Decompress(large));
    }

    [Fact]
    public void Compress_WithNoSettings_UsesBinaryAndTheLargestDictionary()
    {
        byte[] payload = TestPayloads.Get("counting bytes");

        Assert.Equal(
            Imploder.Compress(payload, CompressionType.Binary, DictionarySize.Size4096),
            _codec.Compress(payload));
    }

    /// <summary>
    /// Decompression reads its settings from the stream, so the caller cannot get them wrong.
    /// </summary>
    [Fact]
    public void Decompress_IgnoresHowTheCallerWouldHaveCompressed()
    {
        byte[] payload = TestPayloads.Get("counting bytes");
        byte[] compressed = Imploder.Compress(payload, CompressionType.Binary, DictionarySize.Size1024);

        Assert.Equal(payload, _codec.Decompress(compressed));
    }

    [Theory]
    [MemberData(nameof(AllSettings))]
    public void Compress_ThroughStreams_MatchesCompressInMemory(string payloadName, CompressionType compressionType, DictionarySize dictionarySize)
    {
        byte[] payload = TestPayloads.Get(payloadName);

        using MemoryStream source = new(payload);
        using MemoryStream destination = new();
        _codec.Compress(source, destination, compressionType, dictionarySize);

        Assert.Equal(_codec.Compress(payload, compressionType, dictionarySize), destination.ToArray());
    }

    [Fact]
    public void Decompress_ThroughStreams_ReturnsTheOriginal()
    {
        byte[] payload = TestPayloads.Get("mixed runs and noise");
        byte[] compressed = _codec.Compress(payload, CompressionType.Ascii);

        using MemoryStream source = new(compressed);
        using MemoryStream destination = new();
        _codec.Decompress(source, destination);

        Assert.Equal(payload, destination.ToArray());
    }

    [Fact]
    public void Compress_ThroughCallbacks_MatchesCompressInMemory()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        ChunkedReader reader = new(payload);
        CollectingWriter writer = new();
        _codec.Compress(reader.Read, writer.Write, CompressionType.Ascii, DictionarySize.Size1024);

        Assert.Equal(_codec.Compress(payload, CompressionType.Ascii, DictionarySize.Size1024), writer.ToArray());
    }

    [Fact]
    public void Decompress_ThroughCallbacks_ReturnsTheOriginal()
    {
        byte[] payload = TestPayloads.Get("ascii text");
        byte[] compressed = _codec.Compress(payload, CompressionType.Ascii);

        ChunkedReader reader = new(compressed);
        CollectingWriter writer = new();
        _codec.Decompress(reader.Read, writer.Write);

        Assert.Equal(payload, writer.ToArray());
    }

    [Fact]
    public void TryDecompress_ReportsFailureWithoutThrowing()
    {
        Assert.Equal(PkLibError.InvalidDictionarySize, _codec.TryDecompress(new byte[] { 0, 3, 0, 0, 0, 0 }, out byte[]? result));
        Assert.Null(result);
    }

    [Fact]
    public void Compress_WithUndefinedCompressionType_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _codec.Compress(new byte[] { 1, 2, 3 }, (CompressionType)7));

        Assert.Equal("compressionType", exception.ParamName);
    }

    [Fact]
    public void Compress_WithUndefinedDictionarySize_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => _codec.Compress(new byte[] { 1, 2, 3 }, CompressionType.Binary, (DictionarySize)512));

        Assert.Equal("dictionarySize", exception.ParamName);
    }

    /// <summary>
    /// The codec keeps no state, so one instance is safe to share. Any buffer accidentally hoisted
    /// onto the codec would corrupt results here.
    /// </summary>
    [Fact]
    public void Codec_CanBeUsedConcurrentlyInBothModes()
    {
        byte[] payload = TestPayloads.Get("low entropy");

        byte[] expectedAscii = _codec.Compress(payload, CompressionType.Ascii, DictionarySize.Size2048);
        byte[] expectedBinary = _codec.Compress(payload, CompressionType.Binary, DictionarySize.Size1024);

        Parallel.For(0, 32, index =>
        {
            bool ascii = index % 2 == 0;

            byte[] produced = ascii
                ? _codec.Compress(payload, CompressionType.Ascii, DictionarySize.Size2048)
                : _codec.Compress(payload, CompressionType.Binary, DictionarySize.Size1024);

            Assert.Equal(ascii ? expectedAscii : expectedBinary, produced);
            Assert.Equal(payload, _codec.Decompress(produced));
        });
    }
}

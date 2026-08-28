using System.Text;

using PkLibSharp.Tests.Infrastructure;

namespace PkLibSharp.Tests.Decompression;

/// <summary>
/// Tests for <see cref="Exploder"/>.
/// </summary>
public class ExploderTests
{
    /// <summary>
    /// The same hand-assembled stream used in the encoder tests, decoded here. Because it was built
    /// from the format definition rather than produced by this library, it pins the decoder to the
    /// real format.
    /// </summary>
    private static readonly byte[] KnownStream = [0x00, 0x04, 0x82, 0x08, 0x05, 0xFC, 0x03];

    [Fact]
    public void Decompress_DecodesAKnownStream()
    {
        byte[] restored = Exploder.Decompress(KnownStream);

        Assert.Equal("AB", Encoding.ASCII.GetString(restored));
    }

    [Fact]
    public void Decompress_IgnoresTrailingGarbage()
    {
        // The end of stream code terminates decoding, so anything after it is not read.
        byte[] withGarbage = [.. KnownStream, 0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Equal("AB", Encoding.ASCII.GetString(Exploder.Decompress(withGarbage)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Decompress_WithTooFewBytes_ReportsBadData(int length)
    {
        // The original library refuses anything of four bytes or fewer without examining it.
        byte[] tooShort = new byte[length];

        Assert.Equal(PkLibError.BadData, Exploder.TryDecompress(tooShort, out byte[]? result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(255)]
    public void Decompress_WithInvalidDictionarySize_ReportsInvalidDictionarySize(byte dictionaryBits)
    {
        byte[] stream = [0x00, dictionaryBits, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(PkLibError.InvalidDictionarySize, Exploder.TryDecompress(stream, out _));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(255)]
    public void Decompress_WithInvalidCompressionType_ReportsInvalidMode(byte compressionType)
    {
        byte[] stream = [compressionType, 0x04, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(PkLibError.InvalidMode, Exploder.TryDecompress(stream, out _));
    }

    [Fact]
    public void Decompress_WithTruncatedStream_ReportsAborted()
    {
        // A valid header with no end of stream code: the decoder runs out of input mid-symbol.
        byte[] truncated = [0x00, 0x04, 0x00, 0x00, 0x00];

        Assert.Equal(PkLibError.Aborted, Exploder.TryDecompress(truncated, out _));
    }

    [Fact]
    public void Decompress_WithTruncatedStream_ThrowsCarryingTheError()
    {
        byte[] truncated = [0x00, 0x04, 0x00, 0x00, 0x00];

        PkLibException exception = Assert.Throws<PkLibException>(() => Exploder.Decompress(truncated));

        Assert.Equal(PkLibError.Aborted, exception.Error);
        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void Decompress_WithEveryTruncationOfAValidStream_NeverThrowsSomethingElse()
    {
        byte[] compressed = Imploder.Compress(TestPayloads.Get("ascii text"), CompressionType.Ascii);

        for (int length = 0; length < compressed.Length; length++)
        {
            byte[] truncated = compressed[..length];

            // Either it decodes what it has or it reports a PkLibError. Nothing else is acceptable.
            PkLibError error = Exploder.TryDecompress(truncated, out _);
            Assert.True(Enum.IsDefined(error), $"length {length} produced {error}");
        }
    }

    /// <summary>
    /// Malformed input must be rejected rather than crashing: no index out of range, no infinite
    /// loop, no unexpected exception type escaping.
    /// </summary>
    [Fact]
    public void Decompress_WithRandomInput_NeverThrowsAnUnexpectedException()
    {
        Random random = new(99);

        for (int iteration = 0; iteration < 5000; iteration++)
        {
            byte[] junk = new byte[random.Next(5, 400)];
            random.NextBytes(junk);

            // Give it a valid header so the fuzzing reaches the actual decoding loop.
            junk[0] = (byte)random.Next(2);
            junk[1] = (byte)random.Next(4, 7);

            Exception? unexpected = Record.Exception(() => Exploder.TryDecompress(junk, out _));

            Assert.True(unexpected is null, $"iteration {iteration} threw {unexpected?.GetType().Name}: {unexpected?.Message}");
        }
    }

    [Fact]
    public void TryDecompress_OnSuccess_ReturnsNoneAndTheData()
    {
        PkLibError error = Exploder.TryDecompress(KnownStream, out byte[]? result);

        Assert.Equal(PkLibError.None, error);
        Assert.NotNull(result);
        Assert.Equal("AB", Encoding.ASCII.GetString(result));
    }

    [Fact]
    public void Decompress_WithNullSourceStream_Throws()
    {
        using MemoryStream destination = new();

        Assert.Throws<ArgumentNullException>(() => Exploder.Decompress(null!, destination));
    }

    [Fact]
    public void Decompress_WithNullDestinationStream_Throws()
    {
        using MemoryStream source = new(KnownStream);

        Assert.Throws<ArgumentNullException>(() => Exploder.Decompress(source, null!));
    }

    [Fact]
    public void Decompress_WithNullCallbacks_Throws()
    {
        CollectingWriter writer = new();
        ChunkedReader reader = new(KnownStream);

        Assert.Throws<ArgumentNullException>(() => Exploder.Decompress(null!, writer.Write));
        Assert.Throws<ArgumentNullException>(() => Exploder.Decompress(reader.Read, null!));
    }

    /// <summary>
    /// The decompressor flushes in 0x1000 byte blocks, so a large result must arrive in several
    /// calls rather than being buffered whole.
    /// </summary>
    [Fact]
    public void Decompress_WritesOutputIncrementally()
    {
        byte[] compressed = Imploder.Compress(TestPayloads.Get("counting bytes"));

        CollectingWriter writer = new();
        ChunkedReader reader = new(compressed);
        Exploder.Decompress(reader.Read, writer.Write);

        Assert.True(writer.WriteCount > 1, $"expected several writes, got {writer.WriteCount}");
    }
}

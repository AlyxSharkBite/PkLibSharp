using PkLibSharp.Tests.Infrastructure;

namespace PkLibSharp.Tests.Checksums;

/// <summary>
/// Tests for <see cref="Crc32"/>.
/// </summary>
public class Crc32Tests
{
    /// <summary>
    /// A bit by bit implementation of the same routine, written from the polynomial rather than
    /// from a table, so it shares no code with the implementation under test.
    /// </summary>
    private static uint Reference(ReadOnlySpan<byte> data, uint seed = 0)
    {
        const uint Polynomial = 0xEDB88320;
        uint crc = seed;

        foreach (byte value in data)
        {
            uint next = (uint)((value ^ crc) & 0xFF);

            for (int bit = 0; bit < 8; bit++)
            {
                next = (next & 1) != 0 ? (next >> 1) ^ Polynomial : next >> 1;
            }

            crc = next ^ (crc >> 8);
        }

        return crc;
    }

    [Fact]
    public void Compute_WithNoBytes_ReturnsTheSeed()
    {
        // PKLib's routine is neither pre- nor post-inverted, so an empty checksum is zero.
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0x1234u, Crc32.Compute(ReadOnlySpan<byte>.Empty, 0x1234));
    }

    [Theory]
    [MemberData(nameof(PayloadNames))]
    public void Compute_MatchesABitwiseReference(string payloadName)
    {
        byte[] payload = TestPayloads.Get(payloadName);

        Assert.Equal(Reference(payload), Crc32.Compute(payload));
    }

    public static TheoryData<string> PayloadNames => TestPayloads.AllNames();

    [Fact]
    public void Compute_IsResumableAcrossCalls()
    {
        byte[] payload = TestPayloads.Get("random binary");

        uint whole = Crc32.Compute(payload);

        uint inParts = 0;
        for (int offset = 0; offset < payload.Length; offset += 7919)
        {
            int length = Math.Min(7919, payload.Length - offset);
            inParts = Crc32.Compute(payload.AsSpan(offset, length), inParts);
        }

        Assert.Equal(whole, inParts);
    }

    [Fact]
    public void Compute_OverAStream_MatchesComputeOverMemory()
    {
        byte[] payload = TestPayloads.Get("mixed runs and noise");

        using MemoryStream stream = new(payload);

        Assert.Equal(Crc32.Compute(payload), Crc32.Compute(stream));
    }

    [Fact]
    public void Compute_OverAStream_StartsFromTheCurrentPosition()
    {
        byte[] payload = TestPayloads.Get("ascii text");

        using MemoryStream stream = new(payload);
        stream.Position = 1000;

        Assert.Equal(Crc32.Compute(payload.AsSpan(1000)), Crc32.Compute(stream));
    }

    [Fact]
    public void Compute_DetectsASingleBitFlip()
    {
        byte[] payload = [.. TestPayloads.Get("ascii text")];
        uint before = Crc32.Compute(payload);

        payload[payload.Length / 2] ^= 0x01;

        Assert.NotEqual(before, Crc32.Compute(payload));
    }

    [Fact]
    public void Compute_WithNullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Crc32.Compute((Stream)null!));
    }
}

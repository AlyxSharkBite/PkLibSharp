
namespace PkLibSharp.Tests.Infrastructure;

/// <summary>
/// A <see cref="PkReadCallback"/> over a byte array that hands out at most a fixed number of bytes
/// per call, so that the algorithms are exercised against partial reads.
/// </summary>
/// <param name="source">The data to serve.</param>
/// <param name="maxBytesPerRead">The most bytes any one read may return.</param>
public sealed class ChunkedReader(byte[] source, int maxBytesPerRead = int.MaxValue)
{
    private int _position;

    /// <summary>Gets the number of times <see cref="Read"/> has been called.</summary>
    public int ReadCount { get; private set; }

    /// <summary>Reads the next chunk, returning zero once the source is exhausted.</summary>
    public int Read(Span<byte> buffer)
    {
        ReadCount++;

        int count = Math.Min(Math.Min(buffer.Length, maxBytesPerRead), source.Length - _position);
        source.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
}

/// <summary>
/// A <see cref="PkWriteCallback"/> that accumulates everything it is given.
/// </summary>
public sealed class CollectingWriter
{
    private readonly MemoryStream _buffer = new();

    /// <summary>Gets the number of times <see cref="Write"/> has been called.</summary>
    public int WriteCount { get; private set; }

    /// <summary>Appends a block of output.</summary>
    public void Write(ReadOnlySpan<byte> buffer)
    {
        WriteCount++;
        _buffer.Write(buffer);
    }

    /// <summary>Gets everything written so far.</summary>
    public byte[] ToArray() => _buffer.ToArray();
}

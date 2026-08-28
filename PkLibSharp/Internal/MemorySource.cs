namespace PkLibSharp;

/// <summary>
/// Adapts a block of memory to a <see cref="PkReadCallback"/>.
/// </summary>
/// <param name="source">The memory to read from.</param>
internal sealed class MemorySource(ReadOnlyMemory<byte> source)
{
    private int _position;

    internal int Read(Span<byte> buffer)
    {
        int count = Math.Min(buffer.Length, source.Length - _position);
        source.Span.Slice(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
}

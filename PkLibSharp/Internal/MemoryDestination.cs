using System.Buffers;

namespace PkLibSharp;

/// <summary>
/// Collects the output of a <see cref="PkWriteCallback"/> into a single array.
/// </summary>
internal sealed class MemoryDestination
{
    private readonly ArrayBufferWriter<byte> _writer = new();

    internal void Write(ReadOnlySpan<byte> buffer) => _writer.Write(buffer);

    internal byte[] ToArray() => _writer.WrittenSpan.ToArray();
}

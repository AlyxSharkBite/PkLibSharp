namespace PkLibSharp;

/// <summary>
/// Decompresses data produced by the PKWARE Data Compression Library's imploding algorithm.
/// </summary>
/// <remarks>
/// As in the original C library, a stream of four bytes or fewer is rejected as
/// <see cref="PkLibError.BadData"/> without being examined. See <see cref="Imploder"/> for the one
/// case where the compressor produces such a stream.
/// </remarks>
public static class Exploder
{
    /// <summary>
    /// Decompresses a block of data.
    /// </summary>
    /// <param name="source">The compressed data.</param>
    /// <returns>The decompressed data.</returns>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    public static byte[] Decompress(ReadOnlyMemory<byte> source)
    {
        MemorySource reader = new(source);
        MemoryDestination writer = new();

        Decompress(reader.Read, writer.Write);

        return writer.ToArray();
    }

    /// <summary>
    /// Decompresses a stream, reading it to the end from its current position.
    /// </summary>
    /// <param name="source">The stream holding the compressed data.</param>
    /// <param name="destination">The stream the decompressed data is written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    public static void Decompress(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        Decompress(source.Read, destination.Write);
    }

    /// <summary>
    /// Decompresses data supplied by a callback, writing the result through another callback.
    /// Use this overload to decompress a source that is neither a stream nor a contiguous block of memory.
    /// </summary>
    /// <param name="read">Supplies the compressed data; returns zero once the source is exhausted.</param>
    /// <param name="write">Receives the decompressed data.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    public static void Decompress(PkReadCallback read, PkWriteCallback write)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);

        PkLibError error = new ExplodeEngine(read, write).Run();

        if (error != PkLibError.None)
        {
            throw new PkLibException(error);
        }
    }

    /// <summary>
    /// Attempts to decompress a block of data without throwing if it turns out to be malformed.
    /// </summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="result">The decompressed data, or <see langword="null"/> if decompression failed.</param>
    /// <returns><see cref="PkLibError.None"/> on success, otherwise the reason the data was rejected.</returns>
    public static PkLibError TryDecompress(ReadOnlyMemory<byte> source, out byte[]? result)
    {
        MemorySource reader = new(source);
        MemoryDestination writer = new();

        PkLibError error = new ExplodeEngine(reader.Read, writer.Write).Run();

        result = error == PkLibError.None ? writer.ToArray() : null;
        return error;
    }
}

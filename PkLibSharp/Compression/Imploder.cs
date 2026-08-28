namespace PkLibSharp;

/// <summary>
/// Compresses data with the PKWARE Data Compression Library's imploding algorithm.
/// </summary>
/// <remarks>
/// The output is byte for byte what the original C library produces. That includes one quirk worth
/// knowing about: compressing nothing yields a four byte stream holding just the header and the
/// end of stream marker, and <see cref="Exploder"/> rejects streams that short, exactly as the
/// original does. Applications that need to round-trip empty input should special case it.
/// </remarks>
public static class Imploder
{
    /// <summary>The compression type used when none is specified.</summary>
    public const CompressionType DefaultCompressionType = CompressionType.Binary;

    /// <summary>The dictionary size used when none is specified.</summary>
    public const DictionarySize DefaultDictionarySize = DictionarySize.Size4096;

    /// <summary>
    /// Compresses a block of data.
    /// </summary>
    /// <param name="source">The data to compress.</param>
    /// <param name="compressionType">The literal encoding to use.</param>
    /// <param name="dictionarySize">The size of the sliding dictionary.</param>
    /// <returns>The compressed data.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compressionType"/> or <paramref name="dictionarySize"/> is not a defined value.
    /// </exception>
    public static byte[] Compress(
        ReadOnlyMemory<byte> source,
        CompressionType compressionType = DefaultCompressionType,
        DictionarySize dictionarySize = DefaultDictionarySize)
    {
        MemorySource reader = new(source);
        MemoryDestination writer = new();

        Compress(reader.Read, writer.Write, compressionType, dictionarySize);

        return writer.ToArray();
    }

    /// <summary>
    /// Compresses a stream, reading it to the end from its current position.
    /// </summary>
    /// <param name="source">The stream to compress.</param>
    /// <param name="destination">The stream the compressed data is written to.</param>
    /// <param name="compressionType">The literal encoding to use.</param>
    /// <param name="dictionarySize">The size of the sliding dictionary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compressionType"/> or <paramref name="dictionarySize"/> is not a defined value.
    /// </exception>
    public static void Compress(
        Stream source,
        Stream destination,
        CompressionType compressionType = DefaultCompressionType,
        DictionarySize dictionarySize = DefaultDictionarySize)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        Compress(source.Read, destination.Write, compressionType, dictionarySize);
    }

    /// <summary>
    /// Compresses data supplied by a callback, writing the result through another callback.
    /// Use this overload to compress a source that is neither a stream nor a contiguous block of memory.
    /// </summary>
    /// <param name="read">Supplies the data to compress; returns zero once the source is exhausted.</param>
    /// <param name="write">Receives the compressed data.</param>
    /// <param name="compressionType">The literal encoding to use.</param>
    /// <param name="dictionarySize">The size of the sliding dictionary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compressionType"/> or <paramref name="dictionarySize"/> is not a defined value.
    /// </exception>
    public static void Compress(
        PkReadCallback read,
        PkWriteCallback write,
        CompressionType compressionType = DefaultCompressionType,
        DictionarySize dictionarySize = DefaultDictionarySize)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(write);
        PkLibArguments.ThrowIfNotDefined(compressionType);
        PkLibArguments.ThrowIfNotDefined(dictionarySize);

        new ImplodeEngine(read, write, compressionType, dictionarySize).Run();
    }
}

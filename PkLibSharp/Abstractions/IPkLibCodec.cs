namespace PkLibSharp;

/// <summary>
/// Compresses and decompresses data using the PKWARE Data Compression Library's imploding and
/// exploding algorithms.
/// </summary>
/// <remarks>
/// <para>
/// Compression settings are given per call, so a single codec handles binary and ASCII data
/// side by side. Both settings are optional and default to binary literals with a 4096 byte
/// dictionary.
/// </para>
/// <para>
/// Decompression takes no settings at all: the compression type and dictionary size are recorded in
/// the compressed stream itself.
/// </para>
/// <para>
/// Implementations hold no state between calls and are safe to share between threads, so a single
/// instance can be registered as a singleton. Obtain one from <see cref="IPkLibCodecFactory"/>.
/// </para>
/// </remarks>
public interface IPkLibCodec
{
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
    byte[] Compress(
        ReadOnlyMemory<byte> source,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize);

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
    void Compress(
        Stream source,
        Stream destination,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize);

    /// <summary>
    /// Compresses data supplied by a callback, writing the result through another callback.
    /// </summary>
    /// <param name="read">Supplies the data to compress; returns zero once the source is exhausted.</param>
    /// <param name="write">Receives the compressed data.</param>
    /// <param name="compressionType">The literal encoding to use.</param>
    /// <param name="dictionarySize">The size of the sliding dictionary.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compressionType"/> or <paramref name="dictionarySize"/> is not a defined value.
    /// </exception>
    void Compress(
        PkReadCallback read,
        PkWriteCallback write,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize);

    /// <summary>
    /// Decompresses a block of data.
    /// </summary>
    /// <param name="source">The compressed data.</param>
    /// <returns>The decompressed data.</returns>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    byte[] Decompress(ReadOnlyMemory<byte> source);

    /// <summary>
    /// Decompresses a stream, reading it to the end from its current position.
    /// </summary>
    /// <param name="source">The stream holding the compressed data.</param>
    /// <param name="destination">The stream the decompressed data is written to.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    void Decompress(Stream source, Stream destination);

    /// <summary>
    /// Decompresses data supplied by a callback, writing the result through another callback.
    /// </summary>
    /// <param name="read">Supplies the compressed data; returns zero once the source is exhausted.</param>
    /// <param name="write">Receives the decompressed data.</param>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> or <paramref name="write"/> is <see langword="null"/>.</exception>
    /// <exception cref="PkLibException">The compressed data is malformed.</exception>
    void Decompress(PkReadCallback read, PkWriteCallback write);

    /// <summary>
    /// Attempts to decompress a block of data without throwing if it turns out to be malformed.
    /// </summary>
    /// <param name="source">The compressed data.</param>
    /// <param name="result">The decompressed data, or <see langword="null"/> if decompression failed.</param>
    /// <returns><see cref="PkLibError.None"/> on success, otherwise the reason the data was rejected.</returns>
    PkLibError TryDecompress(ReadOnlyMemory<byte> source, out byte[]? result);
}

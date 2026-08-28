namespace PkLibSharp;

/// <summary>
/// The default <see cref="IPkLibCodec"/>, forwarding each call to <see cref="Imploder"/> or
/// <see cref="Exploder"/>. It holds no state, so one instance serves every caller.
/// </summary>
internal sealed class PkLibCodec : IPkLibCodec
{
    /// <summary>
    /// Gets the shared instance. The codec is stateless, so there is nothing to gain from more.
    /// </summary>
    internal static PkLibCodec Instance { get; } = new();

    private PkLibCodec()
    {
    }

    /// <inheritdoc/>
    public byte[] Compress(
        ReadOnlyMemory<byte> source,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize)
        => Imploder.Compress(source, compressionType, dictionarySize);

    /// <inheritdoc/>
    public void Compress(
        Stream source,
        Stream destination,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize)
        => Imploder.Compress(source, destination, compressionType, dictionarySize);

    /// <inheritdoc/>
    public void Compress(
        PkReadCallback read,
        PkWriteCallback write,
        CompressionType compressionType = Imploder.DefaultCompressionType,
        DictionarySize dictionarySize = Imploder.DefaultDictionarySize)
        => Imploder.Compress(read, write, compressionType, dictionarySize);

    /// <inheritdoc/>
    public byte[] Decompress(ReadOnlyMemory<byte> source) => Exploder.Decompress(source);

    /// <inheritdoc/>
    public void Decompress(Stream source, Stream destination) => Exploder.Decompress(source, destination);

    /// <inheritdoc/>
    public void Decompress(PkReadCallback read, PkWriteCallback write) => Exploder.Decompress(read, write);

    /// <inheritdoc/>
    public PkLibError TryDecompress(ReadOnlyMemory<byte> source, out byte[]? result) => Exploder.TryDecompress(source, out result);
}

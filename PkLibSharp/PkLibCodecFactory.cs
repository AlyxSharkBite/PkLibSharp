namespace PkLibSharp;

/// <summary>
/// The default <see cref="IPkLibCodecFactory"/>.
/// </summary>
/// <remarks>
/// Use <see cref="Default"/> when no dependency injection container is involved:
/// <code>
/// IPkLibCodec codec = PkLibCodecFactory.Default.Create();
/// byte[] packed = codec.Compress(data, CompressionType.Ascii);
/// </code>
/// The factory holds no state, so registering it as a singleton is safe.
/// </remarks>
public sealed class PkLibCodecFactory : IPkLibCodecFactory
{
    /// <summary>
    /// Gets a shared factory instance.
    /// </summary>
    public static PkLibCodecFactory Default { get; } = new();

    /// <inheritdoc/>
    /// <remarks>
    /// Codecs are stateless and immutable, so every call returns the same shared instance rather
    /// than allocating a new one.
    /// </remarks>
    public IPkLibCodec Create() => PkLibCodec.Instance;
}

namespace PkLibSharp;

/// <summary>
/// Creates <see cref="IPkLibCodec"/> instances.
/// </summary>
/// <remarks>
/// Inject this, or <see cref="IPkLibCodec"/> itself, to keep compression out of your callers'
/// concrete dependencies and to allow a test to substitute a codec.
/// </remarks>
public interface IPkLibCodecFactory
{
    /// <summary>
    /// Creates a codec. Compression settings are chosen per call on the codec itself, so there is
    /// nothing to configure here.
    /// </summary>
    /// <returns>A codec ready to use.</returns>
    IPkLibCodec Create();
}

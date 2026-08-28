using System.Runtime.CompilerServices;

namespace PkLibSharp;

/// <summary>
/// Guards against enum values that no defined member covers, which only a cast can produce.
/// </summary>
internal static class PkLibArguments
{
    internal static void ThrowIfNotDefined(
        CompressionType value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is not (CompressionType.Binary or CompressionType.Ascii))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Unsupported compression type.");
        }
    }

    internal static void ThrowIfNotDefined(
        DictionarySize value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (value is not (DictionarySize.Size1024 or DictionarySize.Size2048 or DictionarySize.Size4096))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Unsupported dictionary size.");
        }
    }
}

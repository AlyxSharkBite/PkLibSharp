namespace PkLibSharp;

/// <summary>
/// Identifies the reason a PKWARE implode or explode operation failed.
/// The values match the <c>CMP_*</c> result codes of the original C library.
/// </summary>
public enum PkLibError
{
    /// <summary>The operation completed successfully.</summary>
    None = 0,

    /// <summary>The dictionary size recorded in the stream is not 1024, 2048 or 4096 bytes.</summary>
    InvalidDictionarySize = 1,

    /// <summary>The compression type recorded in the stream is neither binary nor ASCII.</summary>
    InvalidMode = 2,

    /// <summary>The compressed stream is truncated or otherwise malformed.</summary>
    BadData = 3,

    /// <summary>Decompression was aborted because an invalid code was encountered.</summary>
    Aborted = 4,
}

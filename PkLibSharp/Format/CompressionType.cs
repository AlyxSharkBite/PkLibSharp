namespace PkLibSharp;

/// <summary>
/// Selects the literal encoding used by the PKWARE imploding algorithm.
/// </summary>
public enum CompressionType
{
    /// <summary>
    /// Every literal is encoded with a fixed 9-bit code. Suitable for arbitrary binary data.
    /// </summary>
    Binary = 0,

    /// <summary>
    /// Literals are encoded with a static Huffman table tuned for ASCII text.
    /// Produces smaller output for text, but larger output for binary data.
    /// </summary>
    Ascii = 1,
}

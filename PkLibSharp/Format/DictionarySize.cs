namespace PkLibSharp;

/// <summary>
/// The size of the sliding dictionary used when imploding data. A larger dictionary allows
/// repetitions to be found further back in the stream, at the cost of more bits per distance.
/// </summary>
public enum DictionarySize
{
    /// <summary>A dictionary of 1024 bytes; distances are stored in 4 bits plus a code.</summary>
    Size1024 = 1024,

    /// <summary>A dictionary of 2048 bytes; distances are stored in 5 bits plus a code.</summary>
    Size2048 = 2048,

    /// <summary>A dictionary of 4096 bytes; distances are stored in 6 bits plus a code.</summary>
    Size4096 = 4096,
}

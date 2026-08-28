namespace PkLibSharp;

/// <summary>
/// Supplies uncompressed or compressed input to a PKWARE implode or explode operation.
/// </summary>
/// <param name="buffer">The buffer to fill.</param>
/// <returns>
/// The number of bytes written to <paramref name="buffer"/>, or zero once the source is exhausted.
/// A partial read is allowed; the algorithm calls back until it has enough data or reads zero.
/// </returns>
public delegate int PkReadCallback(Span<byte> buffer);

/// <summary>
/// Receives compressed or uncompressed output from a PKWARE implode or explode operation.
/// </summary>
/// <param name="buffer">The bytes produced by the algorithm. The buffer is reused after the call returns.</param>
public delegate void PkWriteCallback(ReadOnlySpan<byte> buffer);

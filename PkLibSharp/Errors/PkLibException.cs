namespace PkLibSharp;

/// <summary>
/// The exception thrown when a PKWARE implode or explode operation cannot be completed.
/// </summary>
public class PkLibException : IOException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PkLibException"/> class.
    /// </summary>
    /// <param name="error">The reason the operation failed.</param>
    public PkLibException(PkLibError error)
        : this(error, GetDefaultMessage(error))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PkLibException"/> class with a custom message.
    /// </summary>
    /// <param name="error">The reason the operation failed.</param>
    /// <param name="message">A message that describes the failure.</param>
    /// <param name="innerException">The exception that caused this failure, if any.</param>
    public PkLibException(PkLibError error, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Gets the reason the operation failed.
    /// </summary>
    public PkLibError Error { get; }

    private static string GetDefaultMessage(PkLibError error) => error switch
    {
        PkLibError.None => "The operation completed successfully.",
        PkLibError.InvalidDictionarySize => "The compressed data specifies an invalid dictionary size.",
        PkLibError.InvalidMode => "The compressed data specifies an invalid compression type.",
        PkLibError.BadData => "The compressed data is truncated or malformed.",
        PkLibError.Aborted => "The compressed data contains an invalid code and could not be decompressed.",
        _ => "The PKWARE data compression operation failed.",
    };
}

namespace PkLibSharp.Tests.Errors;

/// <summary>
/// Tests for <see cref="PkLibException"/>.
/// </summary>
public class PkLibExceptionTests
{
    [Theory]
    [InlineData(PkLibError.InvalidDictionarySize)]
    [InlineData(PkLibError.InvalidMode)]
    [InlineData(PkLibError.BadData)]
    [InlineData(PkLibError.Aborted)]
    public void Constructor_SuppliesADescriptiveMessageForEachError(PkLibError error)
    {
        PkLibException exception = new(error);

        Assert.Equal(error, exception.Error);
        Assert.NotEmpty(exception.Message);
    }

    [Fact]
    public void Constructor_KeepsACustomMessageAndInnerException()
    {
        InvalidOperationException inner = new("inner");

        PkLibException exception = new(PkLibError.BadData, "custom message", inner);

        Assert.Equal(PkLibError.BadData, exception.Error);
        Assert.Equal("custom message", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void Exception_IsAnIoException()
    {
        // Callers wrapping stream work in a catch for IOException should see decompression failures.
        Assert.IsAssignableFrom<IOException>(new PkLibException(PkLibError.BadData));
    }

    [Fact]
    public void ErrorCodes_MatchTheOriginalCmpValues()
    {
        Assert.Equal(0, (int)PkLibError.None);
        Assert.Equal(1, (int)PkLibError.InvalidDictionarySize);
        Assert.Equal(2, (int)PkLibError.InvalidMode);
        Assert.Equal(3, (int)PkLibError.BadData);
        Assert.Equal(4, (int)PkLibError.Aborted);
    }
}

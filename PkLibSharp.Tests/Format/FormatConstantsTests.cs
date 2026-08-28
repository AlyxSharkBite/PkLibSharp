namespace PkLibSharp.Tests.Format;

/// <summary>
/// The enum values are written into the compressed stream, so they are part of the file format and
/// cannot be renumbered.
/// </summary>
public class FormatConstantsTests
{
    [Fact]
    public void CompressionType_MatchesTheOriginalCmpValues()
    {
        Assert.Equal(0, (int)CompressionType.Binary);
        Assert.Equal(1, (int)CompressionType.Ascii);
    }

    [Fact]
    public void CompressionType_DefaultsToBinary()
    {
        Assert.Equal(CompressionType.Binary, default(CompressionType));
    }

    [Fact]
    public void DictionarySize_ValuesAreTheDictionarySizesInBytes()
    {
        Assert.Equal(1024, (int)DictionarySize.Size1024);
        Assert.Equal(2048, (int)DictionarySize.Size2048);
        Assert.Equal(4096, (int)DictionarySize.Size4096);
    }
}

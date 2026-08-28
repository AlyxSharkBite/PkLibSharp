namespace PkLibSharp.Tests;

/// <summary>
/// Tests for <see cref="PkLibCodecFactory"/>.
/// </summary>
public class PkLibCodecFactoryTests
{
    [Fact]
    public void Default_IsAvailableAndReusable()
    {
        Assert.NotNull(PkLibCodecFactory.Default);
        Assert.Same(PkLibCodecFactory.Default, PkLibCodecFactory.Default);
    }

    [Fact]
    public void Create_ReturnsAUsableCodec()
    {
        IPkLibCodec codec = PkLibCodecFactory.Default.Create();

        byte[] payload = "round trip through a freshly created codec"u8.ToArray();

        Assert.Equal(payload, codec.Decompress(codec.Compress(payload)));
    }

    [Fact]
    public void Create_ReturnsTheSharedStatelessCodec()
    {
        // Documented behaviour: the codec has no state, so there is nothing to gain from allocating.
        Assert.Same(PkLibCodecFactory.Default.Create(), PkLibCodecFactory.Default.Create());
    }

    [Fact]
    public void Factory_CanBeConstructedDirectly()
    {
        // A container registering the concrete type must be able to build it.
        IPkLibCodecFactory factory = new PkLibCodecFactory();

        Assert.NotNull(factory.Create());
    }

    [Fact]
    public void Factory_IsUsableThroughItsInterface()
    {
        IPkLibCodecFactory factory = PkLibCodecFactory.Default;
        IPkLibCodec codec = factory.Create();

        byte[] payload = "through the interface"u8.ToArray();

        Assert.Equal(payload, codec.Decompress(codec.Compress(payload, CompressionType.Ascii)));
    }
}

using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Tests;

public sealed class Dl1NameHashTests
{
    [Theory]
    [InlineData("bip01", 0x10F2DC54u)]
    [InlineData("BIP01", 0x10F2DC54u)]
    [InlineData("eyecamera", 0xD1987464u)]
    [InlineData("l_hand", 0xFA3D3F26u)]
    [InlineData("w", 0x00000077u)]
    [InlineData("fv", 0x000010CCu)]
    public void MatchesPythonOracle(string name, uint expected)
    {
        Assert.Equal(expected, Dl1NameHash.Compute(name));
    }
}

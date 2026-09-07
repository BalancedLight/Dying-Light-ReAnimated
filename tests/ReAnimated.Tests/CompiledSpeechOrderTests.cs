using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class CompiledSpeechOrderTests
{
    [Fact]
    public void ExactShortSpeechNameMustPrecedeItsLongerPrefixMatch()
    {
        Dl1OfficialModelCompiler.ValidateCompiledSpeechOrder(["w", "wide"], ["w", "wide"]);
        var error = Assert.Throws<InvalidDataException>(() =>
            Dl1OfficialModelCompiler.ValidateCompiledSpeechOrder(["w", "wide"], ["wide", "w"]));
        Assert.Contains("'W' to 'wide'", error.Message);
    }

    [Fact]
    public void NoSpeechInventoryDoesNotImposeSpeechNames()
    {
        Dl1OfficialModelCompiler.ValidateCompiledSpeechOrder(["smile"], ["smile"]);
    }
}

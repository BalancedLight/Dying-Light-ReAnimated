using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class CompiledMorphHalfRoundingTests
{
    [Theory]
    [InlineData(1.00048828125, 1.0f, 1.0009765625f)]
    [InlineData(-1.00048828125, -1.0f, -1.0009765625f)]
    [InlineData(0.0000000298023223876953125, 0.0f, 0.000000059604644775390625f)]
    [InlineData(-0.0000000298023223876953125, 0.0f, -0.000000059604644775390625f)]
    public void ExactMidpointAcceptsBothNearestHalfValues(double source, float first, float second)
    {
        Dl1OfficialModelCompiler.ValidateHalfMorphComponent(first, source, "rounding", 0, "X");
        Dl1OfficialModelCompiler.ValidateHalfMorphComponent(second, source, "rounding", 0, "X");
    }

    [Theory]
    [InlineData(1.0004, 1.0009765625f)]
    [InlineData(-1.0004, -1.0009765625f)]
    [InlineData(1.00048828125, 1.001953125f)]
    [InlineData(-1.00048828125, -1.001953125f)]
    [InlineData(1.0004, 1.0004f)]
    [InlineData(0.0, float.NaN)]
    public void NonNearestOrNonHalfValuesRemainRejected(double source, float actual) =>
        Assert.Throws<InvalidDataException>(() => Dl1OfficialModelCompiler.ValidateHalfMorphComponent(actual, source, "rounding", 0, "X"));
}

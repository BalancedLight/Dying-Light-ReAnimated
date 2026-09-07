using ReAnimated.Codecs.Fed;

namespace ReAnimated.Tests;

public sealed class FedWriterTests
{
    [Fact]
    public void UnicodeAndExtremeFiniteWeightsRoundTripExactly()
    {
        FedDocument source = new("Expressions", [new("Émotion", [new("mouth", -.4f), new("eye", 1.75f)]), new("Neutral", [])], []);
        byte[] bytes = FedWriter.Write(source);
        FedDocument loaded = FedReader.Read(new MemoryStream(bytes), source.Name);
        Assert.Equal(source.Expressions.Select(x => x.Name), loaded.Expressions.Select(x => x.Name));
        Assert.Equal(source.Expressions[0].Weights, loaded.Expressions[0].Weights);
        Assert.Equal(bytes, FedWriter.Write(loaded));
    }

    [Fact]
    public void WriterChecksLimitsBeforeTouchingDestination()
    {
        using MemoryStream destination = new();
        destination.WriteByte(42);
        FedDocument document = new("Expressions", [new("Long name", [])], []);
        Assert.Throws<InvalidDataException>(() => FedWriter.Write(destination, document, new FedLimits { MaximumStringBytes = 3 }));
        Assert.Equal(new byte[] { 42 }, destination.ToArray());
        Assert.Throws<InvalidDataException>(() => FedWriter.Write(document, new FedLimits { MaximumFileBytes = 2 }));
    }

    [Fact]
    public void DuplicateNamesRequireExplicitPermissivePolicy()
    {
        FedDocument source = new("Expressions", [new("A", []), new("a", [])], []);
        Assert.Throws<InvalidDataException>(() => FedWriter.Write(source, new FedLimits { RejectDuplicateNames = true }));
        Assert.Single(FedReader.Read(new MemoryStream(FedWriter.Write(source)), "Expressions").Diagnostics);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void NonFiniteWeightIsRejected(float value) => Assert.Throws<InvalidDataException>(() =>
        FedWriter.Write(new("Expressions", [new("A", [new("mouth", value)])], [])));

    [Theory]
    [InlineData("")]
    [InlineData("a\0b")]
    public void InvalidNameIsRejected(string name) => Assert.Throws<InvalidDataException>(() =>
        FedWriter.Write(new("Expressions", [new(name, [])], [])));

    [Fact]
    public void UnpairedSurrogateNameIsRejected()
    {
        string invalid = new((char)0xd800, 1);
        Assert.Throws<InvalidDataException>(() => FedWriter.Write(new("Expressions", [new(invalid, [])], [])));
    }
}

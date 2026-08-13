using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class Dl1ChrV4CodecTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "Codec")]
    public void OneDefaultVariantRoundTripsObjectOrderAndLocalTransforms()
    {
        ImmutableArray<Dl1ChrV4ObjectTransform> objects =
        [
            new("root", TransformMatrix.Identity),
            new("helper_socket", TransformMatrix.CreateTranslation(new Vector3D(1.25, -2.5, 3.75))),
            new(
                "mesh_draw_00",
                TransformMatrix.FromTrs(new TransformTRS(
                    new Vector3D(-4.5, 5.25, 6.125),
                    QuaternionD.FromAxisAngle(Vector3D.UnitY, 0.25),
                    new Vector3D(1.5, 0.75, 2.0)))),
        ];

        Dl1ChrV4Document document =
            Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(objects);
        byte[] payload = Dl1ChrV4Codec.Build(document);
        Dl1ChrV4Document reopened = Dl1ChrV4Codec.Parse(payload);

        Assert.Equal(Dl1ChrV4Codec.Version, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal(objects.Length, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(4)));
        Assert.Equal(objects.Select(static item => item.Name), reopened.ObjectNames);
        Dl1ChrV4Variant variant = Assert.Single(reopened.Variants);
        Assert.Equal("default", variant.Name);
        Assert.Equal(Vector3D.One, variant.Scale);
        Assert.Equal(objects.Length, variant.ObjectTransforms.Length);
        for (var index = 0; index < objects.Length; index++)
        {
            Assert.True(
                objects[index].LocalTransform.NearlyEquals(
                    variant.ObjectTransforms[index],
                    1e-5),
                $"Object {index} ('{objects[index].Name}') did not retain its global transform.");
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "Codec")]
    public async Task AtomicWriterReopensBeforePublishingChr()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string requestedPath = Path.Combine(directory, "menu_character");
            Dl1ChrV4WriteResult result =
                await Dl1ChrV4Codec.WriteEditorMenuOneDefaultVariantAtomicAsync(
                    requestedPath,
                    [
                        new Dl1ChrV4ObjectTransform("root", TransformMatrix.Identity),
                        new Dl1ChrV4ObjectTransform(
                            "mesh_draw_00",
                            TransformMatrix.CreateTranslation(new Vector3D(0.5, 1.0, 1.5))),
                    ]);

            Assert.Equal(Path.Combine(directory, "menu_character.chr"), result.OutputPath);
            Assert.True(File.Exists(result.OutputPath));
            Assert.Equal(2, result.ObjectCount);
            Assert.Equal(["default"], result.VariantNames.ToArray());
            Assert.Matches("^[0-9a-f]{64}$", result.OutputSha256);
            Dl1ChrV4Document reopened = Dl1ChrV4Codec.Parse(
                await File.ReadAllBytesAsync(result.OutputPath));
            Assert.Equal(["root", "mesh_draw_00"], reopened.ObjectNames.ToArray());
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "Codec")]
    public void InvalidChrContractsFailClosed()
    {
        Assert.Throws<ArgumentException>(() =>
            Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(
            [
                new Dl1ChrV4ObjectTransform("duplicate", TransformMatrix.Identity),
                new Dl1ChrV4ObjectTransform("DUPLICATE", TransformMatrix.Identity),
            ]));

        TransformMatrix nonAffine = TransformMatrix.Identity with { M44 = 2.0 };
        Assert.Throws<ArgumentException>(() =>
            Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(
            [
                new Dl1ChrV4ObjectTransform("root", nonAffine),
            ]));

        byte[] valid = Dl1ChrV4Codec.Build(
            Dl1ChrV4Codec.CreateEditorMenuOneDefaultVariant(
            [
                new Dl1ChrV4ObjectTransform("root", TransformMatrix.Identity),
            ]));
        Assert.Throws<InvalidDataException>(() => Dl1ChrV4Codec.Parse(valid.AsSpan(0, valid.Length - 1)));
    }
}

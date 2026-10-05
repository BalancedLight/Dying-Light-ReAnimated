using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterBoneBoundsAuthoringTests
{
    [Fact]
    public void ReviewedApplyPreservesModelDataAndCapturesReplayableBounds()
    {
        var source = CreateModel();
        string name = source.Package.Document.Bones[0].Name;
        var original = source.Package.Document.Bones[0].LocalBounds;
        Assert.Throws<InvalidOperationException>(() =>
            CharacterBoneBoundsAuthoring.Apply(source, name, new(4, 5, 6), new(2, 4, 6), reviewed: false));
        var applied = CharacterBoneBoundsAuthoring.Apply(source, name, new(4, 5, 6), new(2, 4, 6), reviewed: true);
        Assert.Equal(new Dl1AuthoredBoneBounds(new(4, 5, 6), new(1, 2, 3)), applied.Package.Document.Bones[0].LocalBounds);
        Assert.Equal(original, source.Package.Document.Bones[0].LocalBounds);
        Assert.Equal(source.Surfaces, applied.Surfaces);
        Assert.Equal(source.Package.SourceFbx, applied.Package.SourceFbx);
        Assert.Equal(source.Package.Document.Source, applied.Package.Document.Source);
        Assert.Equal(source.Package.Document.RigSignature, applied.Package.Document.RigSignature);
        Assert.Equal(source.Package.Document.MorphChannels, applied.Package.Document.MorphChannels);
        Assert.Equal(source.Package.Document.Materials, applied.Package.Document.Materials);
        for (int index = 0; index < source.Package.Document.Bones.Length; index++)
        {
            var expected = index == 0
                ? source.Package.Document.Bones[index] with { LocalBounds = applied.Package.Document.Bones[index].LocalBounds }
                : source.Package.Document.Bones[index];
            Assert.Equal(expected, applied.Package.Document.Bones[index]);
        }
        foreach (var entry in source.Package.CompanionPayloads)
            Assert.Equal(entry.Value, applied.Package.CompanionPayloads[entry.Key]);
        Assert.NotNull(applied.Package.Document.GeometryRevision);
        var replay = ModelGeometryRevisionCodec.Replay(source, applied.Package);
        Assert.Equal(applied.Package.Document.Bones[0].LocalBounds, replay.Package.Document.Bones[0].LocalBounds);
        var altered = applied.Package with
        {
            GeometryRevisionPayload = applied.Package.GeometryRevisionPayload.SetItem(
                0, (byte)(applied.Package.GeometryRevisionPayload[0] ^ 1)),
        };
        Assert.Throws<InvalidDataException>(() => ModelGeometryRevisionCodec.Replay(source, altered));
    }

    [Fact]
    public void IndependentAxisControlsRetainBaselineAndChangeOneHalfExtent()
    {
        var source = CreateModel();
        string name = source.Package.Document.Bones[0].Name;
        var original = source.Package.Document.Bones[0].LocalBounds!.Value;
        var controls = CharacterBoneBoundsAuthoring.CreateAxisControls(source, name, 1.5);
        Assert.Equal(4, controls.Length);
        Assert.Equal("baseline", controls[0].Name);
        Assert.Null(controls[0].Axis);
        Assert.Same(source, controls[0].Model);
        for (int axis = 0; axis < 3; axis++)
        {
            var control = controls[axis + 1];
            var bounds = control.Model.Package.Document.Bones[0].LocalBounds!.Value;
            Assert.Equal(original.Center, bounds.Center);
            Vector3D expected = axis switch
            {
                0 => original.HalfExtents with { X = original.HalfExtents.X * 1.5 },
                1 => original.HalfExtents with { Y = original.HalfExtents.Y * 1.5 },
                _ => original.HalfExtents with { Z = original.HalfExtents.Z * 1.5 },
            };
            Assert.Equal(expected, bounds.HalfExtents);
            Assert.Equal(source.Surfaces, control.Model.Surfaces);
            Assert.Equal(source.Package.SourceFbx, control.Model.Package.SourceFbx);
            foreach (var entry in source.Package.CompanionPayloads)
                Assert.Equal(entry.Value, control.Model.Package.CompanionPayloads[entry.Key]);
        }
        Assert.Equal(original, source.Package.Document.Bones[0].LocalBounds);
    }

    [Fact]
    public void FitIsReadOnlyAndUsesWeightedVerticesInTheExactBoneFrame()
    {
        var source = CreateModel();
        string name = source.Package.Document.Bones[0].Name;
        var before = source.Package.Document.Bones[0].LocalBounds;
        var fitted = CharacterBoneBoundsAuthoring.Fit(source, name);
        Assert.True(fitted.IsFiniteAndNonZero);
        Assert.True(fitted.HalfExtents.X >= .005 && fitted.HalfExtents.Y >= .005 && fitted.HalfExtents.Z >= .005);
        Assert.Equal(before, source.Package.Document.Bones[0].LocalBounds);
        var surface = source.Surfaces[0];
        var vertex = surface.Vertices[0] with { BoneIndices = [surface.PaletteBoneIndices.Length] };
        var malformed = source with { Surfaces = source.Surfaces.SetItem(0,
            surface with { Vertices = surface.Vertices.SetItem(0, vertex) }) };
        Assert.Throws<InvalidDataException>(() => CharacterBoneBoundsAuthoring.Fit(malformed, name));
    }

    [Fact]
    public void ZeroPivotsFiniteValuesExactNamesAndAcceptanceFlagsKeepTheirPolicy()
    {
        var source = CreateModel();
        var document = source.Package.Document;
        string name = document.Bones[0].Name;
        var zero = CharacterBoneBoundsAuthoring.ParseFullDimensions("1 2 3 0 0 0");
        var pivot = CharacterBoneBoundsAuthoring.UpdateDocument(document, name, zero, reviewed: true);
        Assert.Equal(Vector3D.Zero, pivot.Bones[0].LocalBounds!.Value.HalfExtents);
        Assert.Throws<InvalidOperationException>(() =>
            CharacterBoneBoundsAuthoring.CreateAxisControls(source with
            {
                Package = source.Package with { Document = pivot },
            }, name, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => CharacterBoneBoundsAuthoring.CreateAxisControls(source, name, 1));
        Assert.Throws<ArgumentException>(() =>
            CharacterBoneBoundsAuthoring.Apply(source, name, Vector3D.Zero, new(-1, 1, 1), reviewed: true));
        Assert.Throws<ArgumentException>(() =>
            CharacterBoneBoundsAuthoring.Apply(source, name, new(double.NaN, 0, 0), Vector3D.One, reviewed: true));
        Assert.Throws<ArgumentException>(() => CharacterBoneBoundsAuthoring.Fit(source, name + "_missing"));
        var inventory = document.CharacterResources!;
        var seeded = document with { CharacterResources = inventory with
        {
            CompiledSemanticSha256 = new string('a', 64),
            LoadedResourceSha256 = new string('b', 64),
            VerifiedPlayerScenarios = ["generic"],
        } };
        var updated = CharacterBoneBoundsAuthoring.UpdateDocument(seeded, name, zero, reviewed: true);
        Assert.Null(updated.CharacterResources!.CompiledSemanticSha256);
        Assert.Null(updated.CharacterResources.LoadedResourceSha256);
        Assert.Empty(updated.CharacterResources.VerifiedPlayerScenarios);
        Assert.Equal(inventory.Resources, updated.CharacterResources.Resources);
    }

    private static FbxModelAuthoringImportResult CreateModel()
    {
        var model = FbxModelAuthoringImporter.ImportPackage(CharacterBodyRegionAuthoringTests.CreateGenericBodyRegionPackage());
        var document = model.Package.Document;
        document = document with { Bones = document.Bones.SetItem(0, document.Bones[0] with
        {
            LocalBounds = new Dl1AuthoredBoneBounds(new(1, 2, 3), new(.5, 1, 1.5)),
        }) };
        return model with { Package = model.Package with { Document = document } };
    }
}

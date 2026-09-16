using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class AuthoredLayerHelperPersistenceTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void SeparateHelperPreservesAuthoredPayloadAndReopensThroughPreparation()
    {
        FbxModelAuthoringImportResult captured = CaptureWithStudio();
        ImmutableArray<byte> payload = captured.Package.AuthoredLayerPayload;
        CustomModelDocument document = CustomModelHelperAuthoring.DuplicateAsHelper(
            captured.Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "generic_contact");
        document = AddContactRecipe(document);
        FbxModelAuthoringImportResult withHelper = captured with
        {
            Package = captured.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
        };

        AuthoredModelLayer layer = CustomModelPackageSerializer.ValidateAuthoredLayer(withHelper.Package)!;
        Assert.Equal(CustomModelContractSignatures.ComputeRig(withHelper.Package.Document.Bones), layer.TargetRigSignature);
        Assert.Contains(layer.Components, component => component.Points.Any() && component.Morphs.Any() && component.InverseBinds.Any());
        FbxModelAuthoringImportResult recaptured = FbxAuthoredModelLayer.Capture(withHelper);
        Assert.True(payload.AsSpan().SequenceEqual(recaptured.Package.AuthoredLayerPayload.AsSpan()));

        string path = Path.Combine(_directory, "helper-persistence.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(withHelper.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        CustomModelAuthoredHelper helper = Assert.Single(reopened.Package.Document.AuthoredHelpers);
        Assert.Equal("generic_contact", helper.Name);
        Assert.Equal("generic-contact", Assert.Single(reopened.Package.Document.RiggingSession!.Recipe.Helpers).RoleId);
        Assert.True(payload.AsSpan().SequenceEqual(reopened.Package.AuthoredLayerPayload.AsSpan()));
        Assert.Equal(withHelper.Package.Document.RigSignature, reopened.Package.Document.RigSignature);
        Assert.Contains(Dl1CustomModelRigPreparer.Prepare(reopened).Contract.Nodes,
            node => node.Name == "generic_contact");

        Assert.Equal(withHelper.Surfaces.Length, reopened.Surfaces.Length);
        for (int surfaceIndex = 0; surfaceIndex < withHelper.Surfaces.Length; surfaceIndex++)
        {
            FbxModelSurface expected = withHelper.Surfaces[surfaceIndex];
            FbxModelSurface actual = reopened.Surfaces[surfaceIndex];
            Assert.Equal(expected.SourceGeometry!.Id, actual.SourceGeometry!.Id);
            Assert.Equal(expected.Vertices.Length, actual.Vertices.Length);
            Assert.Equal<int>(expected.PaletteBoneIndices.Order(), actual.PaletteBoneIndices.Order());
            Assert.Equal(expected.InverseBindMatrices.Length, actual.InverseBindMatrices.Length);
            for (int bindIndex = 0; bindIndex < expected.InverseBindMatrices.Length; bindIndex++)
                Assert.True(expected.InverseBindMatrices[bindIndex].NearlyEquals(
                    actual.InverseBindMatrices[actual.PaletteBoneIndices.IndexOf(expected.PaletteBoneIndices[bindIndex])], 1e-12));
            for (int vertexIndex = 0; vertexIndex < expected.Vertices.Length; vertexIndex++)
            {
                Assert.InRange((expected.Vertices[vertexIndex].Position - actual.Vertices[vertexIndex].Position).Length, 0, 1e-12);
                Assert.InRange((expected.Vertices[vertexIndex].Normal - actual.Vertices[vertexIndex].Normal).Length, 0, 1e-12);
                var expectedVertex = expected.Vertices[vertexIndex];
                var actualVertex = actual.Vertices[vertexIndex];
                Assert.Equal(
                    expectedVertex.BoneIndices.Select((slot, i) => (Bone: expected.PaletteBoneIndices[slot], Weight: expectedVertex.BoneWeights[i])).OrderBy(p => p.Bone),
                    actualVertex.BoneIndices.Select((slot, i) => (Bone: actual.PaletteBoneIndices[slot], Weight: actualVertex.BoneWeights[i])).OrderBy(p => p.Bone));
            }
            Assert.Equal(expected.MorphTargets.Length, actual.MorphTargets.Length);
            for (int morphIndex = 0; morphIndex < expected.MorphTargets.Length; morphIndex++)
                Assert.Equal<Vector3D>(expected.MorphTargets[morphIndex].PositionDeltas, actual.MorphTargets[morphIndex].PositionDeltas);
        }
    }

    [Fact]
    public void AuthoredLayerStillRejectsAChangedBaseRigWithSeparateHelpers()
    {
        FbxModelAuthoringImportResult captured = CaptureWithStudio();
        CustomModelDocument document = CustomModelHelperAuthoring.DuplicateAsHelper(
            captured.Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "generic_contact");
        ImmutableArray<CustomModelBone> bones = document.Bones.SetItem(
            1, document.Bones[1] with { Name = "changed_base_joint" });
        CustomModelDocument wrong = document with
        {
            Bones = bones,
            RigSignature = CustomModelContractSignatures.ComputeRig(
                (document with { Bones = bones }).CreateEffectiveBones()),
        };
        Assert.Throws<CustomModelFormatException>(() => CustomModelPackageSerializer.Serialize(
            captured.Package with { Document = wrong }));
    }

    [Fact]
    public void HelperFreeSchemaSevenAuthoredLayerRemainsCompatible()
    {
        FbxModelAuthoringImportResult captured = FbxAuthoredModelLayer.Capture(
            FbxAuthoredModelLayerTests.Author(FbxAuthoredModelLayerTests.Source()));
        Assert.Empty(captured.Package.Document.AuthoredHelpers);
        string path = Path.Combine(_directory, "helper-free.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(captured.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        Assert.Empty(reopened.Package.Document.AuthoredHelpers);
        Assert.True(captured.Package.AuthoredLayerPayload.AsSpan().SequenceEqual(
            reopened.Package.AuthoredLayerPayload.AsSpan()));
        Assert.Equal(captured.Package.Document.RigSignature, reopened.Package.Document.RigSignature);
    }

    private static FbxModelAuthoringImportResult CaptureWithStudio()
    {
        FbxModelAuthoringImportResult authored = FbxAuthoredModelLayerTests.Author(
            FbxAuthoredModelLayerTests.Source());
        CustomModelDocument document = authored.Package.Document;
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        authored = authored with
        {
            Package = authored.Package with { Document = document with { RiggingSession = session } },
        };
        return FbxAuthoredModelLayer.Capture(authored);
    }

    private static CustomModelDocument AddContactRecipe(CustomModelDocument document)
    {
        RiggingSession session = document.RiggingSession!;
        CustomModelAuthoredHelper helper = Assert.Single(document.AuthoredHelpers);
        Guid parent = session.Recipe.Entities.Single(entity => entity.NativeName == document.Bones[0].Name).EntityId;
        var evidence = new RigEvidenceReference
        {
            Id = "generic-contact-evidence",
            Kind = RigEvidenceKind.GeometryInference,
            ArtifactSha256 = document.Source.ContentSha256,
            Description = "Synthetic contact footprint evidence from the source-linked geometry.",
        };
        HelperRecipe recipe = new()
        {
            EntityId = helper.Id,
            OwnerAssetId = document.ModelId,
            RoleId = "generic-contact",
            ParentEntityId = parent,
            LocalFrame = helper.ExactLocalMatrix,
            FramePolicy = RigFramePolicy.Contact,
            BoundsCenter = Vector3D.Zero,
            BoundsHalfExtents = new(.05, .01, .08),
            PlacementProvenance = RigEvidenceKind.GeometryInference,
            Evidence = [evidence],
        };
        var entities = session.Recipe.Entities.Select(entity => entity.EntityId == parent
            ? entity with { Kind = RigNativeEntityKind.Bone }
            : entity).ToImmutableArray();
        RiggingSession replacement = RiggingSessions.Change(session,
            session with { Recipe = session.Recipe with { Entities = entities, Helpers = [recipe],
                FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != helper.Id).ToImmutableArray() } },
            RiggingEditKind.Helpers);
        return RiggingHelperMaterializer.Apply(document with { RiggingSession = replacement });
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CharacterReferenceAuthoringTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void AdoptionPreservesNeutralTargetAndArchivesReferenceWithoutInventingShapes()
    {
        FbxModelAuthoringImportResult target = Model(false);
        FbxModelAuthoringImportResult reference = Model(true);
        ImmutableArray<byte> targetSource = target.Package.SourceFbx;
        ImmutableArray<FbxModelSurface> targetSurfaces = target.Surfaces;
        ImmutableArray<CustomModelMorphChannel> targetChannels = target.Package.Document.MorphChannels;

        FbxModelAuthoringImportResult adopted = CharacterReferenceAuthoring.AdoptReference(target, reference);
        CharacterResourceInventory inventory = adopted.Package.Document.CharacterResources!;

        Assert.Null(target.Package.Document.CharacterResources);
        Assert.True(targetSource.SequenceEqual(adopted.Package.SourceFbx));
        Assert.Equal(target.Package.Document.Source.ContentSha256, adopted.Package.Document.Source.ContentSha256);
        Assert.Equal(targetChannels, adopted.Package.Document.MorphChannels);
        Assert.Equal(targetSurfaces, adopted.Surfaces);
        Assert.Single(adopted.Surfaces[0].MorphTargets);
        Assert.Contains(adopted.Surfaces[0].MorphTargets[0].PositionDeltas, delta => delta.X != 0);
        Assert.Equal(Sha(adopted.Package.DecodedCharacterPayload), inventory.DecodedSha256);
        Assert.Equal(target.Package.Document.Source.ContentSha256,
            inventory.Resources.Single(row => row.Id == inventory.RootResourceId).SourceFingerprint);

        CharacterResourceRecord raw = inventory.Resources.Single(row => row.Id.StartsWith("reference-source:", StringComparison.Ordinal));
        CharacterResourceRecord decoded = inventory.Resources.Single(row => row.Id.StartsWith("reference-decoded:", StringComparison.Ordinal));
        Assert.True(raw.IsOriginalArchive);
        Assert.True(decoded.IsOriginalArchive);
        Assert.True(reference.Package.SourceFbx.SequenceEqual(adopted.Package.CompanionPayloads[raw.EntryPath!]));
        Assert.True(reference.Package.DecodedCharacterPayload.SequenceEqual(adopted.Package.CompanionPayloads[decoded.EntryPath!]));
        Assert.Contains(reference.Package.Document.Source.ContentSha256, raw.ProviderIdentity, StringComparison.Ordinal);
        CharacterResourceRecord phx = inventory.Resources.Single(row => row.LogicalName == "reference.phx");
        Assert.Equal(CharacterDependencyStatus.Ambiguous, phx.Status);
        Assert.True(phx.Required);
        Assert.False(phx.IsOriginalArchive);

        Assert.Equal(0, inventory.MorphBindings.Single(binding => binding.Name == "smile").TargetChannelSlot);
        Assert.Equal(-1, inventory.MorphBindings.Single(binding => binding.Name == "frown").TargetChannelSlot);
        Assert.Contains(inventory.Resources, row => row.Subsystem == CharacterSubsystem.Morphs &&
            row.Status == CharacterDependencyStatus.Ambiguous && row.EntryPath is null);
        Assert.Equal(CharacterDependencyStatus.Decoded,
            inventory.Subsystems.Single(review => review.Subsystem == CharacterSubsystem.Geometry).Status);
        Assert.Equal(CharacterDependencyStatus.Ambiguous,
            inventory.Subsystems.Single(review => review.Subsystem == CharacterSubsystem.Ragdoll).Status);
        Assert.Null(inventory.CompiledSemanticSha256);
        Assert.Null(inventory.LoadedResourceSha256);
        Assert.Empty(inventory.VerifiedPlayerScenarios);
        Assert.NotEmpty(inventory.ExportBlockers);
        inventory.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ExistingTargetInventoryAndChangedReferenceSourceAreRefused()
    {
        FbxModelAuthoringImportResult target = Model(false);
        FbxModelAuthoringImportResult reference = Model(true);
        FbxModelAuthoringImportResult adopted = CharacterReferenceAuthoring.AdoptReference(target, reference);

        Assert.Throws<InvalidOperationException>(() => CharacterReferenceAuthoring.AdoptReference(adopted, reference));
        var changed = reference with { Package = reference.Package with { SourceFbx = [41, 42, 43] } };
        Assert.Throws<InvalidDataException>(() => CharacterReferenceAuthoring.AdoptReference(target, changed));
        Assert.Null(target.Package.Document.CharacterResources);
        Assert.True(reference.Package.Document.CharacterResources!.Resources.Any(row => row.Id == "stock-root"));
    }

    private static FbxModelAuthoringImportResult Model(bool stock)
    {
        byte[] source = stock ? [9, 8, 7, 6] : [1, 2, 3, 4];
        string sourceHash = Sha(ImmutableArray.Create(source));
        Guid materialId = new("0f3b72ad-bd49-4ae8-950e-37c3ca8910b5");
        var channels = stock
            ? ImmutableArray.Create(Channel(0, "smile", 0x11223344), Channel(1, "frown", 0x55667788))
            : ImmutableArray.Create(Channel(0, "smile", 0x11223344));
        var document = new CustomModelDocument
        {
            Name = stock ? "generic reference" : "generic neutral target",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                Kind = stock ? CustomModelSourceKind.StockCharacter : CustomModelSourceKind.BinaryFbx,
                OriginalFileName = stock ? "reference.msh" : "neutral.fbx",
                ContentSha256 = sourceHash,
                FbxVersion = 7400,
            },
            RigSignature = Sha(ImmutableArray.Create<byte>(1)),
            MorphSignature = Sha(ImmutableArray.Create<byte>(2)),
            Bones = [new CustomModelBone
            {
                Index = 0, FbxObjectId = 1, Name = "root", ParentIndex = -1,
                LocalBindTransform = TransformTRS.Identity,
                ExactLocalBindMatrix = TransformMatrix.Identity,
                Kind = BoneKind.Root, IsWeighted = true,
            }],
            MorphChannels = channels,
            Materials = [new CustomModelMaterial
            {
                Id = materialId, Name = "generic material", ExistingDl1MaterialReference = "generic.mat",
            }],
        };
        var vertices = ImmutableArray.Create(
            Vertex(0, 0), Vertex(1, 0), Vertex(0, 1));
        var surface = new FbxModelSurface("surface", "mesh", materialId, vertices,
            [0u, 1u, 2u], [0], [TransformMatrix.Identity], true)
        {
            SourceGeometry = new GeometrySourceComponent("surface", vertices.Select(vertex => vertex.Position).ToImmutableArray()),
            SourceCorners = [new(0, 0), new(1, 1), new(2, 2)],
            SourceTriangles = [new(0, 0)],
            MorphTargets = [new("smile", 0x11223344, 10, 10,
                [new Vector3D(.2, 0, 0), Vector3D.Zero, Vector3D.Zero])],
        };
        ImmutableArray<byte> decoded = [];
        ImmutableDictionary<string, ImmutableArray<byte>> companions = ImmutableDictionary<string, ImmutableArray<byte>>.Empty;
        if (stock)
        {
            byte[] sourceResource = [5, 4, 3];
            byte[] physics = [6, 7, 8];
            decoded = [11, 12, 13];
            string sourcePath = "character/resources/reference-source.bin";
            string physicsPath = "character/resources/reference-physics.bin";
            companions = companions.Add(sourcePath, ImmutableArray.Create(sourceResource))
                .Add(physicsPath, ImmutableArray.Create(physics));
            var inventory = new CharacterResourceInventory
            {
                RootResourceId = "stock-root",
                DecodedSha256 = Sha(decoded),
                DecodedByteLength = decoded.Length,
                Resources =
                [
                    new CharacterResourceRecord { Id = "stock-root", LogicalName = "reference.msh",
                        ContentSha256 = Sha(ImmutableArray.Create(sourceResource)), EntryPath = sourcePath,
                        ByteLength = sourceResource.Length, Subsystem = CharacterSubsystem.Geometry,
                        Status = CharacterDependencyStatus.Preserved },
                    new CharacterResourceRecord { Id = "physics", LogicalName = "reference.phx",
                        ContentSha256 = Sha(ImmutableArray.Create(physics)), EntryPath = physicsPath,
                        ByteLength = physics.Length, Subsystem = CharacterSubsystem.Ragdoll,
                        Status = CharacterDependencyStatus.Preserved },
                ],
                MorphBindings =
                [
                    new(0, 0, "smile", 0x11223344, "stock-face", 1, 0, 3, "VertexDeltasDecoded"),
                    new(1, 1, "frown", 0x55667788, "stock-face", 1, 0, 3, "VertexDeltasDecoded"),
                ],
                CompiledSemanticSha256 = Sha(ImmutableArray.Create<byte>(3)),
                LoadedResourceSha256 = Sha(ImmutableArray.Create<byte>(3)),
                VerifiedPlayerScenarios = ["stock-reuse", "facial", "ragdoll", "gore"],
            };
            document = document with { CharacterResources = inventory };
        }
        document.Validate();
        var package = new CustomModelPackage(document, ImmutableArray.Create(source),
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty)
        {
            DecodedCharacterPayload = decoded,
            CompanionPayloads = companions,
        };
        return new(package, null, [surface], ImmutableDictionary<Guid, AnimationClip>.Empty, null!);
    }

    private static CustomModelMorphChannel Channel(int index, string name, uint descriptor) => new()
    {
        Index = index, Name = name, DescriptorHash = descriptor,
        BlendShapeChannelObjectId = index + 10, ShapeObjectId = index + 10,
        GeometryObjectIds = [1],
    };

    private static FbxModelVertex Vertex(double x, double y) => new(
        new(x, y, 0), new(0, 0, 1), 0, 0, [0], [1]);

    private static string Sha(ImmutableArray<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()));
}

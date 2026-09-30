using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1StockBoneScriptPolicyProposalTests
{
    private const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ProposesObservedFlagsBySemanticIdentityAndLeavesExtraDestinationUnresolved()
    {
        (Dl1RigTemplate template, CompactMeshDocument source, CustomModelDocument destination,
            Dl1PreparedAuthoredRig prepared) = Fixture();
        var result = Dl1StockBoneScriptPolicyProposalService.Propose(
            template, source, SourceHash, destination, prepared);

        Assert.Equal(SourceHash, result.SourceSha256);
        Assert.Equal(4, result.Rows.Length);
        var root = Assert.Single(result.Rows.Where(row => row.SourceIndex == 0));
        Assert.Equal(Dl1StockPolicyProposalStatus.Proposed, root.Status);
        Assert.Equal(RigAnimationComponents.Position | RigAnimationComponents.Scale, root.ProposedMask);
        Assert.Equal(RigAnimationLod.Lod2, root.ProposedLod);
        Assert.Equal(RigAnimationComponents.Rotation, root.CurrentMask);
        Assert.Equal(RigAnimationLod.Lod1, root.CurrentLod);
        Assert.Equal(source.Entities[0].Flags, root.RawFlags);
        Assert.Equal(prepared.Contract.Nodes[0].SemanticEntityId, root.DestinationEntityId);

        var rootHelper = Assert.Single(result.Rows.Where(row => row.DestinationName == "socket"));
        Assert.Equal(Dl1StockPolicyProposalStatus.Proposed, rootHelper.Status);
        Assert.Equal(prepared.Contract.Nodes[3].SemanticEntityId, rootHelper.DestinationEntityId);

        var extra = Assert.Single(result.Rows.Where(row => row.Status == Dl1StockPolicyProposalStatus.UnresolvedExtraDestination));
        Assert.Equal("extra", extra.DestinationName);
        Assert.Null(extra.ProposedMask);
        Assert.Null(extra.ProposedLod);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void RejectsUndefinedSourceAnimationLod(int lod)
    {
        var fixture = Fixture();
        var source = fixture.Source with
        {
            Entities = fixture.Source.Entities.Select(entity => entity.Index == 0
                ? entity with { Flags = (entity.Flags & ~0x7000u) | ((uint)lod << 12) }
                : entity).ToArray(),
        };

        Assert.Throws<InvalidDataException>(() => Dl1StockBoneScriptPolicyProposalService.Propose(
            fixture.Template, source, SourceHash, fixture.Destination, fixture.Prepared));
    }

    [Fact]
    public void RejectsStaleSourceFingerprintAndAppliedOutputSignature()
    {
        var fixture = Fixture();
        Assert.Throws<InvalidDataException>(() => Dl1StockBoneScriptPolicyProposalService.Propose(
            fixture.Template, fixture.Source, new string('b', 64), fixture.Destination, fixture.Prepared));

        var stale = fixture.Destination with
        {
            RigConformance = fixture.Destination.RigConformance! with { AppliedOutputRigSignature = new string('b', 64) },
        };
        Assert.Throws<InvalidDataException>(() => Dl1StockBoneScriptPolicyProposalService.Propose(
            fixture.Template, fixture.Source, SourceHash, stale, fixture.Prepared));

        var staleFbx = fixture.Destination with
        {
            Source = fixture.Destination.Source with { ContentSha256 = new string('b', 64) },
        };
        Assert.Throws<InvalidDataException>(() => Dl1StockBoneScriptPolicyProposalService.Propose(
            fixture.Template, fixture.Source, SourceHash, staleFbx, fixture.Prepared));
    }

    [Fact]
    public void RejectsSourceRowThatDoesNotMatchTemplateIdentity()
    {
        var fixture = Fixture();
        var altered = fixture.Source with
        {
            Entities = fixture.Source.Entities.Select(entity => entity.Index == 1
                ? entity with { ParentIndex = -1 }
                : entity).ToArray(),
        };

        Assert.Throws<InvalidDataException>(() => Dl1StockBoneScriptPolicyProposalService.Propose(
            fixture.Template, altered, SourceHash, fixture.Destination, fixture.Prepared));
    }

    private static (Dl1RigTemplate Template, CompactMeshDocument Source, CustomModelDocument Destination,
        Dl1PreparedAuthoredRig Prepared) Fixture()
    {
        Guid rootId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        Guid childId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        Guid extraId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        Guid socketId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        TransformMatrix rootMatrix = TransformMatrix.Identity;
        TransformMatrix childLocal = TransformMatrix.CreateTranslation(new Vector3D(0, 1, 0));
        TransformMatrix childGlobal = rootMatrix * childLocal;
        var template = new Dl1RigTemplate("synthetic-profile", "synthetic-source", SourceHash,
        [
            new() { Index = 0, Name = "root", ParentIndex = -1, Kind = BoneKind.Root, IsDeform = false,
                LocalRestMatrix = rootMatrix, GlobalRestMatrix = rootMatrix },
            new() { Index = 1, Name = "joint", ParentIndex = 0, Kind = BoneKind.Deform, IsDeform = true,
                LocalRestMatrix = childLocal, GlobalRestMatrix = childGlobal },
            new() { Index = 2, Name = "socket", ParentIndex = -1, Kind = BoneKind.Root, IsDeform = false,
                LocalRestMatrix = rootMatrix, GlobalRestMatrix = rootMatrix },
        ]);

        var nodes = ImmutableArray.Create(
            Node(0, 0, "root", -1, BoneKind.Root, rootId, rootMatrix, rootMatrix),
            Node(1, 1, "joint", 0, BoneKind.Deform, childId, childLocal, childGlobal),
            Node(2, 2, "extra", 0, BoneKind.Helper, extraId, TransformMatrix.Identity, TransformMatrix.Identity),
            Node(3, 3, "socket", -1, BoneKind.Helper, socketId, TransformMatrix.Identity, TransformMatrix.Identity));
        var contract = new Dl1AuthoredRigContract("synthetic-destination", SourceHash, nodes);
        var prepared = new Dl1PreparedAuthoredRig(contract, contract.CreateRigDefinition(), [], []);
        var source = new CompactMeshDocument(3, 2, 0,
        [
            Entity(0, "root", -1, CompactMeshEntityType.Bone, 0x2500u),
            Entity(1, "joint", 0, CompactMeshEntityType.Bone, 0x4300u),
            Entity(2, "socket", -1, CompactMeshEntityType.Helper, 0x4000u),
        ], []);
        var currentPolicies = ImmutableArray.Create(
            Dl1BoneScriptPolicyTests.Policy(rootId, RigAnimationComponents.Rotation, RigAnimationLod.Lod1));
        var destination = new CustomModelDocument
        {
            Source = new CustomModelSourceIdentity { ContentSha256 = SourceHash },
            RigConformance = new CustomModelRigConformance
            {
                TemplateId = template.TemplateId,
                TemplateProfileName = template.ProfileName,
                TemplateSourceResourceName = template.SourceResourceName,
                TemplateFingerprint = SourceHash,
                SourceFbxSha256 = SourceHash,
                AppliedOutputRigSignature = RigSignature.Compute(prepared.PreviewRig),
            },
            RiggingSession = new RiggingSession
            {
                Recipe = new RuntimeRigRecipe { ComponentPolicies = currentPolicies },
            },
        };
        return (template, source, destination, prepared);
    }

    private static Dl1AuthoredRigNode Node(int physical, int source, string name, int parent, BoneKind kind,
        Guid entityId, TransformMatrix local, TransformMatrix global) => new()
    {
        PhysicalIndex = physical,
        SourceBoneIndex = source,
        Name = name,
        ParentPhysicalIndex = parent,
        Kind = kind,
        IsDeform = kind == BoneKind.Deform,
        SemanticEntityId = entityId,
        FramePolicy = RigFramePolicy.Manual,
        BoundsPolicy = RigBoundsPolicy.PreserveSource,
        LocalBindMatrix = local,
        GlobalBindMatrix = global,
        InverseGlobalReferenceMatrix = global.InvertedAffine(),
        Bounds = new Dl1AuthoredBoneBounds(Vector3D.Zero, new Vector3D(.1, .1, .1)),
        DescriptorHash = (uint)(physical + 1),
    };

    private static CompactMeshEntity Entity(int index, string name, short parent,
        CompactMeshEntityType type, uint flags) => new(index, name, flags, default, parent, type,
        0, 0, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0);
}

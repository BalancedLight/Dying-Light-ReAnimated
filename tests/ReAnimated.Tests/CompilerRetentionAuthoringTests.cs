using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CompilerRetentionAuthoringTests : IDisposable
{
    private readonly string directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void EligiblePreparedNodeAddsOneStableRetentionHelperWithoutTouchingExistingAuthoring()
    {
        FbxModelAuthoringImportResult source = Source();
        StructuralNodeReview row = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.Name == "Child");
        Assert.True(row.CanAddRetentionHelper);
        Dl1PreparedAuthoredRig beforePrepared = Dl1CustomModelRigPreparer.Prepare(source);
        string sourceSecondary = SecondaryMotionSetupSerializer.Serialize(source.Package.Document.SecondaryMotion);
        ImmutableArray<CustomModelAuthoredHelper> oldHelpers = source.Package.Document.AuthoredHelpers;

        StructuralHelperPreview preview = FbxCompilerRetentionAuthoring.Preview(source, row.EntityId);
        Assert.True(preview.HasChanges);
        Assert.True(FbxStructuralHelperAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult candidate));
        CustomModelDocument document = candidate.Package.Document;
        document.Validate();

        Assert.Equal(sourceSecondary, SecondaryMotionSetupSerializer.Serialize(source.Package.Document.SecondaryMotion));
        Assert.Equal<CustomModelBone>(source.Package.Document.Bones, document.Bones);
        Assert.Equal<FbxModelSurface>(source.Surfaces, candidate.Surfaces);
        Assert.Equal(source.AnimationClips, candidate.AnimationClips);
        Assert.Equal<AnimationComponentPolicy>(source.Package.Document.RiggingSession!.Recipe.ComponentPolicies,
            document.RiggingSession!.Recipe.ComponentPolicies.Where(p => source.Package.Document.RiggingSession.Recipe.ComponentPolicies.Any(old => old.EntityId == p.EntityId)));
        Assert.Equal<byte>(source.Package.AuthoredLayerPayload, candidate.Package.AuthoredLayerPayload);
        Assert.Equal(oldHelpers.Length + 1, document.AuthoredHelpers.Length);
        foreach (CustomModelAuthoredHelper oldHelper in oldHelpers)
            Assert.Equal(oldHelper, document.AuthoredHelpers.Single(helper => helper.Id == oldHelper.Id));

        CustomModelAuthoredHelper added = Assert.Single(document.AuthoredHelpers,
            helper => oldHelpers.All(old => old.Id != helper.Id));
        Assert.Equal(CustomModelAuthoredHelperKind.Helper, added.Kind);
        Assert.Equal(row.SourceIndex, added.ParentNodeIndex);
        Assert.Equal(TransformTRS.Identity, added.LocalTransform);
        Assert.Equal(TransformMatrix.Identity, added.ExactLocalMatrix);

        RiggingSession session = Assert.IsType<RiggingSession>(document.RiggingSession);
        HelperRecipe retention = Assert.Single(session.Recipe.Helpers, helper => helper.EntityId == added.Id);
        Assert.Equal("compiler.retention", retention.RoleId);
        Assert.Equal(row.EntityId, retention.ParentEntityId);
        Assert.Equal(TransformMatrix.Identity, retention.LocalFrame);
        Assert.True(retention.FollowPreparedParent);
        AnimationComponentPolicy components = Assert.Single(session.Recipe.ComponentPolicies,
            policy => policy.EntityId == added.Id);
        Assert.Equal<RigComponentOwner>([RigComponentOwner.BindInherited], components.Position.Owners);
        Assert.Equal<RigComponentOwner>([RigComponentOwner.BindInherited], components.Rotation.Owners);
        Assert.Equal<RigComponentOwner>([RigComponentOwner.BindInherited], components.Scale.Owners);
        Assert.Equal(RigAnimationComponents.None, components.EmittedMask);
        Assert.Equal(RigAnimationLod.Off, components.AnimationLod);

        Dl1PreparedAuthoredRig afterPrepared = Dl1CustomModelRigPreparer.Prepare(candidate);
        foreach (CustomModelBone bone in source.Package.Document.CreateEffectiveBones())
        {
            var oldNode = beforePrepared.Contract.Nodes.Single(node => node.SourceBoneIndex == bone.Index);
            var newNode = afterPrepared.Contract.Nodes.Single(node => node.SourceBoneIndex == bone.Index);
            Assert.True(oldNode.GlobalBindMatrix.NearlyEquals(newNode.GlobalBindMatrix, 1e-9), bone.Name);
            Assert.Equal(oldNode.Bounds, newNode.Bounds);
        }
        int addedIndex = document.CreateEffectiveBones().Single(bone => bone.Name == added.Name).Index;
        Assert.DoesNotContain(candidate.AnimationClips.Values.SelectMany(clip => clip.TransformTracks),
            track => track.BoneIndex == addedIndex);

        string path = Path.Combine(directory, "compiler-retention.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(candidate.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        CustomModelAuthoredHelper reopenedHelper = Assert.Single(reopened.Package.Document.AuthoredHelpers,
            helper => helper.Id == added.Id);
        Assert.Equal(added, reopenedHelper);
        Assert.Equal<byte>(source.Package.SourceFbx, reopened.Package.SourceFbx);
        Assert.All(reopened.Surfaces, surface =>
        {
            Assert.Equal("normal_marker_2", reopened.Package.Document.CreateEffectiveBones()[Assert.Single(surface.PaletteBoneIndices)].Name);
            Assert.All(surface.Vertices, vertex => Assert.Equal<double>([1d], vertex.BoneWeights));
        });
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(document.SecondaryMotion),
            SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
        Assert.Equal<CustomModelBone>(document.Bones, reopened.Package.Document.Bones);
        Assert.Equal(SecondaryMotionSetupSerializer.Serialize(document.SecondaryMotion),
            SecondaryMotionSetupSerializer.Serialize(reopened.Package.Document.SecondaryMotion));
    }

    [Fact]
    public void RetentionProposalRejectsExistingDependentAndWeightedBranchesAndStaleInputs()
    {
        FbxModelAuthoringImportResult source = Source();
        ImmutableArray<StructuralNodeReview> rows = FbxStructuralHelperAuthoring.Inspect(source);
        StructuralNodeReview weighted = rows.Single(row => row.Name == "normal_marker_2");
        Assert.False(weighted.CanAddRetentionHelper);
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.Preview(source, weighted.EntityId));

        StructuralNodeReview existingDependent = rows.Single(row => row.Name == "Root");
        Assert.False(existingDependent.CanAddRetentionHelper);
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.Preview(source, existingDependent.EntityId));

        StructuralNodeReview child = rows.Single(row => row.Name == "Child");
        StructuralHelperPreview first = FbxCompilerRetentionAuthoring.Preview(source, child.EntityId);
        FbxModelAuthoringImportResult candidate = first.Candidate;
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionAuthoring.Preview(candidate, child.EntityId));

        FbxModelAuthoringImportResult stale = source with
        {
            Package = source.Package with
            {
                Document = source.Package.Document with { Name = source.Package.Document.Name + "-stale" },
            },
        };
        Assert.False(FbxStructuralHelperAuthoring.TryApply(stale, first, out _));
        Assert.Throws<OperationCanceledException>(() =>
            FbxCompilerRetentionAuthoring.Preview(source, child.EntityId, new CancellationToken(true)));
    }

    [Fact]
    public void RetentionMarkersCannotSilentlyBecomeDeformersOrAnatomicalParents()
    {
        var source = Source();
        var selected = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.CanAddRetentionHelper);
        var candidate = FbxCompilerRetentionAuthoring.Preview(source, selected.EntityId).Candidate;
        int marker = candidate.Package.Document.CreateEffectiveBones().Length - 1;
        var weighted = candidate with { Surfaces = candidate.Surfaces.Select(surface => surface with
            { PaletteBoneIndices = [marker], InverseBindMatrices = [TransformMatrix.Identity],
                Vertices = surface.Vertices.Select(v => v with { BoneIndices = [0], BoneWeights = [1] }).ToImmutableArray() }).ToImmutableArray() };
        Assert.Throws<InvalidDataException>(() => Dl1CustomModelRigPreparer.Prepare(weighted));
        var doc = CustomModelHelperAuthoring.DuplicateAsHelper(candidate.Package.Document, marker, CustomModelAuthoredHelperKind.Helper, "dependent");
        Assert.Throws<InvalidDataException>(() => Dl1CustomModelRigPreparer.Prepare(candidate with
            { Package = candidate.Package with { Document = doc }, Rig = doc.CreateRigDefinition() }));
    }

    [Fact]
    public void ExistingNameCollisionDoesNotReplaceAnUnrelatedHelper()
    {
        var source = Source();
        var selected = FbxStructuralHelperAuthoring.Inspect(source).Single(r => r.CanAddRetentionHelper);
        var firstName = FbxCompilerRetentionAuthoring.Preview(source, selected.EntityId).Candidate.Package.Document.AuthoredHelpers[^1].Name;
        var doc = CustomModelHelperAuthoring.DuplicateAsHelper(source.Package.Document, 0, CustomModelAuthoredHelperKind.Helper, firstName);
        var occupied = doc.AuthoredHelpers[^1];
        source = source with { Package = source.Package with { Document = doc }, Rig = doc.CreateRigDefinition() };
        var result = FbxCompilerRetentionAuthoring.Preview(source, selected.EntityId).Candidate.Package.Document;
        Assert.Equal(occupied, result.AuthoredHelpers.Single(h => h.Id == occupied.Id));
        Assert.NotEqual(firstName, result.AuthoredHelpers[^1].Name);
    }

    internal static FbxModelAuthoringImportResult Source()
    {
        FbxModelAuthoringImportResult source = WeightedHelperLayerTests.WeightedSource();
        CustomModelDocument document = source.Package.Document;
        if (document.RiggingSession is null)
            document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        var session = document.RiggingSession!;
        var evidence = new RigEvidenceReference { Id = "synthetic-existing-policy", Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = document.Source.ContentSha256, Description = "Synthetic authored channel control" };
        var ownership = new RigChannelOwnership { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] };
        document = document with { RiggingSession = session with { Recipe = session.Recipe with
        {
            ComponentPolicies = session.Recipe.Entities.Select(e => new AnimationComponentPolicy
            { EntityId = e.EntityId, Position = ownership, Rotation = ownership, Scale = ownership,
                EmittedMask = RigAnimationComponents.None, AnimationLod = RigAnimationLod.Off,
                LodRuleId = "synthetic-existing-policy", LodEvidence = [evidence] }).ToImmutableArray(),
        } } };
        return FbxAuthoredModelLayer.Capture(source with
        {
            Package = source.Package with { Document = document },
            Rig = document.CreateRigDefinition(),
        });
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(directory);
}

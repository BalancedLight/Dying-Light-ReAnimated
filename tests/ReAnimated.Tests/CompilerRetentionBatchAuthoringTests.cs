using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class CompilerRetentionBatchAuthoringTests
{
    [Fact]
    public void ExplicitEligibleParentsProduceDeterministicAtomicBatchAndPreserveOriginalNodes()
    {
        FbxModelAuthoringImportResult source = SourceWithTwoEligibleNodes();
        ImmutableArray<StructuralNodeReview> eligible = FbxStructuralHelperAuthoring.Inspect(source)
            .Where(row => row.CanAddRetentionHelper).ToImmutableArray();
        Assert.Equal(2, eligible.Length);
        Guid[] selection = [eligible[1].EntityId, eligible[0].EntityId];
        var before = Dl1CustomModelRigPreparer.Prepare(source).Contract.Nodes;

        FbxCompilerRetentionBatchPreview preview = FbxCompilerRetentionBatchAuthoring.Preview(source, selection);

        Assert.True(preview.HasChanges);
        Assert.Equal(source.Package.Document.ModelId, preview.ModelId);
        Assert.Equal(source.Package.Document.RiggingSession!.Id, preview.SessionId);
        var depthBySource = before.ToDictionary(
            static node => node.SourceBoneIndex,
            node =>
            {
                int depth = 0;
                int parent = node.ParentPhysicalIndex;
                while (parent >= 0) { depth++; parent = before[parent].ParentPhysicalIndex; }
                return depth;
            });
        Assert.Equal(
            eligible.OrderBy(row => depthBySource[row.SourceIndex]).ThenBy(row => row.EntityId)
                .Select(static row => row.EntityId).ToArray(),
            preview.ParentEntityIds);
        Assert.Equal(2, preview.AddedHelperCount);
        Assert.Equal(2, preview.AddedHelperIds.Distinct().Count());
        Assert.Equal(2, preview.Candidate.Package.Document.RiggingSession!.Recipe.Helpers
            .Count(helper => helper.RoleId == FbxCompilerRetentionAuthoring.RoleId));
        Assert.True(FbxCompilerRetentionBatchAuthoring.TryApply(source, preview, out var applied));
        Assert.Same(preview.Candidate, applied);

        var after = Dl1CustomModelRigPreparer.Prepare(applied).Contract.Nodes;
        foreach (var oldNode in before)
        {
            var next = after.Single(node => node.SourceBoneIndex == oldNode.SourceBoneIndex);
            Assert.Equal(oldNode.Name, next.Name);
            Assert.Equal(oldNode.IsDeform, next.IsDeform);
            Assert.True(oldNode.GlobalBindMatrix.NearlyEquals(next.GlobalBindMatrix, 1e-9));
            Assert.Equal(oldNode.Bounds, next.Bounds);
        }
        Assert.Equal(source.Package.Document.Bones, applied.Package.Document.Bones);
        Assert.Equal(source.Surfaces, applied.Surfaces);
        Assert.Equal(source.AnimationClips, applied.AnimationClips);
    }

    [Fact]
    public void NestedUnusedBonesAreProcessedParentFirstWhenChildIdentifierSortsFirst()
    {
        FbxModelAuthoringImportResult source = SourceWithTwoEligibleNodes(nested: true);
        var rows = FbxStructuralHelperAuthoring.Inspect(source);
        var parent = rows.Single(row => row.Name == "Child");
        var child = rows.Single(row => row.Name == "SyntheticChild");
        Assert.True(parent.CanAddRetentionHelper);
        Assert.True(child.CanAddRetentionHelper);
        Assert.True(child.EntityId.CompareTo(parent.EntityId) < 0);
        var before = Dl1CustomModelRigPreparer.Prepare(source).Contract.Nodes;
        var parentNode = before.Single(node => node.SourceBoneIndex == parent.SourceIndex);
        var childNode = before.Single(node => node.SourceBoneIndex == child.SourceIndex);
        Assert.Equal(parentNode.PhysicalIndex, childNode.ParentPhysicalIndex);

        var preview = FbxCompilerRetentionBatchAuthoring.Preview(source, [child.EntityId, parent.EntityId]);

        Assert.Equal<Guid>([parent.EntityId, child.EntityId], preview.ParentEntityIds);
        Assert.Equal(2, preview.AddedHelperCount);
        Assert.True(FbxCompilerRetentionBatchAuthoring.TryApply(source, preview, out var applied));
        var after = Dl1CustomModelRigPreparer.Prepare(applied).Contract.Nodes;
        foreach (var original in before)
        {
            var retained = after.Single(node => node.SourceBoneIndex == original.SourceBoneIndex);
            Assert.Equal(original.Name, retained.Name);
            Assert.Equal(original.IsDeform, retained.IsDeform);
            Assert.True(original.LocalBindMatrix.NearlyEquals(retained.LocalBindMatrix, 1e-9));
            Assert.True(original.InverseGlobalReferenceMatrix.NearlyEquals(retained.InverseGlobalReferenceMatrix, 1e-9));
            Assert.Equal(original.Bounds, retained.Bounds);
        }
        Assert.Equal<CustomModelBone>(source.Package.Document.Bones, applied.Package.Document.Bones);
        Assert.Equal<FbxModelSurface>(source.Surfaces, applied.Surfaces);
        Assert.Equal(source.AnimationClips, applied.AnimationClips);
        Assert.Equal<byte>(source.Package.SourceFbx, applied.Package.SourceFbx);
    }

    [Fact]
    public void ViewportAdapterFocusesOneHelperAndRetainsWholeBatchCandidateAndSummary()
    {
        FbxModelAuthoringImportResult source = SourceWithTwoEligibleNodes();
        Guid[] selection = FbxStructuralHelperAuthoring.Inspect(source)
            .Where(row => row.CanAddRetentionHelper).Select(row => row.EntityId).ToArray();

        FbxCompilerRetentionBatchPreview batch = FbxCompilerRetentionBatchAuthoring.Preview(source, selection);
        StructuralHelperPreview overlay = batch.ToStructuralHelperPreview();

        Assert.Same(batch.Candidate, overlay.Candidate);
        Assert.Equal(batch.ParentEntityIds[0], overlay.EntityId);
        Assert.Equal(batch.AddedHelperIds[0], overlay.AddedHelperId);
        Assert.Equal(batch.Summary, overlay.Summary);
        Assert.Contains(batch.AddedHelperCount.ToString(CultureInfo.InvariantCulture), overlay.Summary, StringComparison.Ordinal);
        Assert.Equal(batch.AddedHelperCount, overlay.Candidate.Package.Document.RiggingSession!.Recipe.Helpers
            .Count(helper => helper.RoleId == FbxCompilerRetentionAuthoring.RoleId));
    }

    [Fact]
    public void RejectsEmptyDuplicateStaleAndIneligibleSelectionsWithoutPartialCandidate()
    {
        FbxModelAuthoringImportResult source = SourceWithTwoEligibleNodes();
        StructuralNodeReview eligible = FbxStructuralHelperAuthoring.Inspect(source).First(row => row.CanAddRetentionHelper);
        StructuralNodeReview ineligible = FbxStructuralHelperAuthoring.Inspect(source).First(row => !row.CanAddRetentionHelper);

        Assert.Throws<ArgumentException>(() => FbxCompilerRetentionBatchAuthoring.Preview(source, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => FbxCompilerRetentionBatchAuthoring.Preview(source,
            Enumerable.Range(0, FbxCompilerRetentionBatchAuthoring.MaximumBatchSize + 1).Select(_ => Guid.NewGuid())));
        Assert.Throws<ArgumentException>(() => FbxCompilerRetentionBatchAuthoring.Preview(source,
            [eligible.EntityId, eligible.EntityId]));
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionBatchAuthoring.Preview(source,
            [eligible.EntityId, Guid.NewGuid()]));
        Assert.Throws<InvalidOperationException>(() => FbxCompilerRetentionBatchAuthoring.Preview(source,
            [eligible.EntityId, ineligible.EntityId]));

        Assert.Empty(source.Package.Document.AuthoredHelpers.Where(helper =>
            source.Package.Document.RiggingSession!.Recipe.Helpers.Any(recipe =>
                recipe.EntityId == helper.Id && recipe.RoleId == FbxCompilerRetentionAuthoring.RoleId)));
        Assert.Equal(0, source.Package.Document.RiggingSession!.Recipe.Helpers
            .Count(helper => helper.RoleId == FbxCompilerRetentionAuthoring.RoleId));
    }

    [Fact]
    public void PreviewCannotBeAppliedToAnotherModelOrAnAlreadyChangedSession()
    {
        FbxModelAuthoringImportResult source = SourceWithTwoEligibleNodes();
        Guid parent = FbxStructuralHelperAuthoring.Inspect(source).First(row => row.CanAddRetentionHelper).EntityId;
        FbxCompilerRetentionBatchPreview preview = FbxCompilerRetentionBatchAuthoring.Preview(source, [parent]);

        var copiedSnapshot = source with { };
        Assert.False(FbxCompilerRetentionBatchAuthoring.TryApply(copiedSnapshot, preview, out var unchanged));
        Assert.Same(copiedSnapshot, unchanged);

        var staleSession = source with { Package = source.Package with { Document = source.Package.Document with
        {
            RiggingSession = source.Package.Document.RiggingSession! with { Id = Guid.NewGuid() },
        } } };
        Assert.False(FbxCompilerRetentionBatchAuthoring.TryApply(staleSession, preview, out unchanged));
        Assert.Same(staleSession, unchanged);
    }

    private static FbxModelAuthoringImportResult SourceWithTwoEligibleNodes(bool nested = false)
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        CustomModelDocument document = source.Package.Document;
        int oldBoneCount = document.Bones.Length;
        var bones = document.Bones.Add(new CustomModelBone
        {
            Index = document.Bones.Length,
            FbxObjectId = 0,
            Name = "SyntheticChild",
            ParentIndex = nested ? document.Bones.Single(bone => bone.Name == "Child").Index : 0,
            LocalBindTransform = new(new(0, 2, 0), QuaternionD.Identity, Vector3D.One),
            ExactLocalBindMatrix = TransformMatrix.CreateTranslation(new(0, 2, 0)),
            Kind = BoneKind.Deform,
            IsWeighted = true,
        });
        document = document with
        {
            Bones = bones,
            AuthoredHelpers = document.AuthoredHelpers.Select(helper => helper.ParentNodeIndex >= oldBoneCount
                ? helper with { ParentNodeIndex = helper.ParentNodeIndex + 1 }
                : helper).ToImmutableArray(),
            AuthoredLayer = null,
            RiggingSession = null,
        };
        document = document with { RigSignature = CustomModelContractSignatures.ComputeRig(document.CreateEffectiveBones()) };
        document = document with { RiggingSession = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig) };
        if (nested)
        {
            RiggingSession session = document.RiggingSession!;
            Guid oldParent = session.Recipe.Entities.Single(entity => entity.NativeName == "Child").EntityId;
            Guid oldChild = session.Recipe.Entities.Single(entity => entity.NativeName == "SyntheticChild").EntityId;
            Guid parentId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
            Guid childId = Guid.Parse("00000001-0000-0000-0000-000000000000");
            Guid Remap(Guid id) => id == oldParent ? parentId : id == oldChild ? childId : id;
            document = document with { RiggingSession = session with { Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.Select(entity => entity with { EntityId = Remap(entity.EntityId) }).ToImmutableArray(),
                Assignments = session.Recipe.Assignments.Select(row => row with { EntityId = Remap(row.EntityId) }).ToImmutableArray(),
                Helpers = session.Recipe.Helpers.Select(row => row with { EntityId = Remap(row.EntityId), ParentEntityId = Remap(row.ParentEntityId) }).ToImmutableArray(),
                ComponentPolicies = session.Recipe.ComponentPolicies.Select(row => row with { EntityId = Remap(row.EntityId) }).ToImmutableArray(),
                FramePolicies = session.Recipe.FramePolicies.Select(row => row with { EntityId = Remap(row.EntityId) }).ToImmutableArray(),
            } } };
        }
        var surfaces = source.Surfaces.Select(surface => surface with
        {
            PaletteBoneIndices = surface.PaletteBoneIndices.Select(index => index >= oldBoneCount ? index + 1 : index).ToImmutableArray(),
        }).ToImmutableArray();
        return FbxAuthoredModelLayer.Capture(source with
        {
            Package = source.Package with { Document = document, AuthoredLayerPayload = [] },
            Rig = document.CreateRigDefinition(),
            Surfaces = surfaces,
        });
    }
}

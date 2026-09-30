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
        Assert.Equal(selection.Order().ToArray(), preview.ParentEntityIds);
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

    private static FbxModelAuthoringImportResult SourceWithTwoEligibleNodes()
    {
        FbxModelAuthoringImportResult source = CompilerRetentionAuthoringTests.Source();
        CustomModelDocument document = source.Package.Document;
        int oldBoneCount = document.Bones.Length;
        var bones = document.Bones.Add(new CustomModelBone
        {
            Index = document.Bones.Length,
            FbxObjectId = 0,
            Name = "SyntheticChild",
            ParentIndex = 0,
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

using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectModelReimportReconcilerTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void ReplacementPackageDetachesOwnershipFromImmutableOldStack()
    {
        Guid previousAssetId = Guid.NewGuid();
        Guid replacementAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string rig = Sha('1');
        var model = new ProjectModelEntry
        {
            Id = modelId,
            AssetId = previousAssetId,
            Name = "Generic character",
            RigSignature = rig,
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Immutable embedded take",
            SourceAssetId = previousAssetId,
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = Guid.NewGuid(),
                    FbxObjectId = 42,
                    StackFingerprint = Sha('2'),
                    SourceRigSignature = rig,
                    Roles = AnimationSourceRoles.Body,
                },
            Presentation = new ProjectAnimationSourcePresentation
            {
                OriginKind =
                    ProjectAnimationSourceOriginKind.OwningCustomModel,
                OriginName = model.Name,
                OwningModelId = modelId,
                ProjectAssetId = previousAssetId,
                SourceRigIdentity = rig,
            },
            FrameCount = 2,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Name = source.Name,
            TargetModelId = modelId,
            TargetRigId = "generic-rig",
            TargetRigSignature = rig,
            BindingMode = ProjectAnimationBindingMode.ExactDirect,
        };
        DlraProject project = DlraProject.Create("Immutable source") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = previousAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Sources/character-old.dlrmodel",
                    ContentSha256 = Sha('3'),
                },
                new ProjectAssetReference
                {
                    Id = replacementAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Sources/character-new.dlrmodel",
                    ContentSha256 = Sha('4'),
                },
            ],
            Models = [model],
            AnimationSources = [source],
            AnimationVariants = [variant],
        };
        project.Validate();

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            model with { AssetId = replacementAssetId });

        ProjectAnimationSource retained = Assert.Single(
            result.AnimationSources);
        Assert.Equal(previousAssetId, retained.SourceAssetId);
        ProjectAnimationSourcePresentation presentation = Assert.IsType<
            ProjectAnimationSourcePresentation>(retained.Presentation);
        Assert.Equal(
            ProjectAnimationSourceOriginKind.ImportedFbxRig,
            presentation.OriginKind);
        Assert.Null(presentation.OwningModelId);
        Assert.Equal(previousAssetId, presentation.ProjectAssetId);
        result.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void RigChangeRetainsAuthoredRowsButInvalidatesDependentReviews()
    {
        Guid assetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        ProjectBoneMapping body = ReviewedBone();
        ProjectMorphBinding face = ReviewedMorph();
        var model = new ProjectModelEntry
        {
            Id = modelId,
            AssetId = assetId,
            Name = "Generic target",
            RigSignature = Sha('1'),
            MorphSignature = Sha('2'),
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Name = "Generic take",
            TargetModelId = modelId,
            TargetRigId = "generic-rig",
            TargetRigSignature = Sha('1'),
            MappingFingerprint = Sha('3'),
            BoneMappings = [body],
            TargetBindReviews =
            [
                new ProjectTargetBindReview
                {
                    TargetBoneIndex = 1,
                    TargetBoneName = "child",
                },
            ],
            MorphBindings = [face],
        };
        ProjectAnimation compatibility = Compatibility(
            variant,
            assetId,
            body,
            face);
        var project = new DlraProject
        {
            Models = [model],
            AnimationVariants = [variant],
            Animations = [compatibility],
        };
        ProjectModelEntry replacement = model with
        {
            RigSignature = Sha('4'),
        };

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            replacement);

        ProjectAnimationVariant updated = Assert.Single(
            result.AnimationVariants);
        Assert.Equal(Sha('4'), updated.TargetRigSignature);
        Assert.Null(updated.MappingFingerprint);
        Assert.Empty(updated.TargetBindReviews);
        ProjectBoneMapping retained = Assert.Single(updated.BoneMappings);
        Assert.Equal(body.SourceBoneName, retained.SourceBoneName);
        Assert.Equal(body.TargetBoneName, retained.TargetBoneName);
        Assert.Equal(body.Evidence, retained.Evidence);
        Assert.False(retained.IsReviewed);
        Assert.False(retained.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.None, retained.ReviewOrigin);
        Assert.Equal(face, Assert.Single(updated.MorphBindings));

        ProjectAnimation legacy = Assert.Single(result.Animations);
        Assert.Null(legacy.MappingFingerprint);
        Assert.False(Assert.Single(legacy.BoneMappings).IsReviewed);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void MorphChangeLeavesBodyReviewAndStalesOnlyFacialRows()
    {
        Guid assetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        ProjectBoneMapping body = ReviewedBone();
        ProjectMorphBinding face = ReviewedMorph();
        var model = new ProjectModelEntry
        {
            Id = modelId,
            AssetId = assetId,
            Name = "Generic target",
            RigSignature = Sha('1'),
            MorphSignature = Sha('2'),
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Name = "Generic take",
            TargetModelId = modelId,
            TargetRigId = "generic-rig",
            TargetRigSignature = Sha('1'),
            MappingFingerprint = Sha('3'),
            BoneMappings = [body],
            MorphBindings = [face],
        };
        var project = new DlraProject
        {
            Models = [model],
            AnimationVariants = [variant],
        };

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            model with { MorphSignature = Sha('5') });

        ProjectAnimationVariant updated = Assert.Single(
            result.AnimationVariants);
        Assert.Equal(body, Assert.Single(updated.BoneMappings));
        Assert.Equal(Sha('3'), updated.MappingFingerprint);
        ProjectMorphBinding retained = Assert.Single(updated.MorphBindings);
        Assert.Equal(face.SourceChannel, retained.SourceChannel);
        Assert.Equal(face.TargetMorph, retained.TargetMorph);
        Assert.Equal(face.Evidence, retained.Evidence);
        Assert.False(retained.IsReviewed);
        Assert.False(retained.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.None, retained.ReviewOrigin);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void StableContractsPreserveReviewsWhileUpdatingModelMetadata()
    {
        var model = new ProjectModelEntry
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            Name = "Original",
            RigSignature = Sha('1'),
            MorphSignature = Sha('2'),
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Name = "Generic take",
            TargetModelId = model.Id,
            TargetRigId = "generic-rig",
            BoneMappings = [ReviewedBone()],
            MorphBindings = [ReviewedMorph()],
        };
        var project = new DlraProject
        {
            Models = [model],
            AnimationVariants = [variant],
        };

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            model with { Name = "Renamed" });

        Assert.Equal("Renamed", Assert.Single(result.Models).Name);
        ProjectAnimationVariant retained = Assert.Single(
            result.AnimationVariants);
        Assert.Equal(variant.Id, retained.Id);
        Assert.Equal(model.RigSignature, retained.TargetRigSignature);
        Assert.Equal(variant.BoneMappings, retained.BoneMappings);
        Assert.Equal(variant.MorphBindings, retained.MorphBindings);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void ReplacementAssetRetargetsCompatibilityRowWithoutDiscardingSourceAsset()
    {
        Guid previousAssetId = Guid.NewGuid();
        Guid replacementAssetId = Guid.NewGuid();
        var model = new ProjectModelEntry
        {
            Id = Guid.NewGuid(),
            AssetId = previousAssetId,
            Name = "Generic target",
            RigSignature = Sha('1'),
            MorphSignature = Sha('2'),
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = Guid.NewGuid(),
            Name = "Generic take",
            TargetModelId = model.Id,
            TargetRigId = "generic-rig",
            TargetRigSignature = Sha('1'),
            BoneMappings = [ReviewedBone()],
        };
        ProjectAnimation compatibility = Compatibility(
            variant,
            previousAssetId,
            ReviewedBone(),
            ReviewedMorph());
        var project = new DlraProject
        {
            Models = [model],
            AnimationVariants = [variant],
            Animations = [compatibility],
        };

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            model with { AssetId = replacementAssetId });

        Assert.Equal(
            replacementAssetId,
            Assert.Single(result.Animations).TargetAssetId);
        Assert.Equal(
            replacementAssetId,
            Assert.Single(result.Models).AssetId);
        Assert.Equal(variant, Assert.Single(result.AnimationVariants));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void StableAuthoringContractDoesNotLeaveOldEmbeddedSourceFalselyExact()
    {
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string authoring = Sha('9');
        string skeleton = Sha('a');
        var model = new ProjectModelEntry
        {
            Id = modelId,
            AssetId = Guid.NewGuid(),
            Name = "Generic target",
            RigSignature = Sha('1'),
            AuthoringRigContractSignature = authoring,
            AnimationSkeletonSignature = skeleton,
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Immutable take",
            SourceAssetId = Guid.NewGuid(),
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = Guid.NewGuid(),
                    FbxObjectId = 42,
                    StackFingerprint = Sha('2'),
                    SourceRigSignature = Sha('1'),
                    SourceAnimationSkeletonSignature = skeleton,
                },
            SourceAnimationSkeletonSignature = skeleton,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Name = "Immutable take",
            TargetModelId = modelId,
            TargetRigId = "generic-rig",
            TargetRigSignature = Sha('1'),
            TargetAnimationSkeletonSignature = skeleton,
            BindingMode = ProjectAnimationBindingMode.ExactDirect,
            BoneMappings = [ReviewedBone()],
        };
        var project = new DlraProject
        {
            Models = [model],
            AnimationSources = [source],
            AnimationVariants = [variant],
        };
        ProjectModelEntry replacement = model with
        {
            AssetId = Guid.NewGuid(),
            RigSignature = Sha('4'),
        };

        DlraProject result = ProjectModelReimportReconciler.Apply(
            project,
            replacement);

        ProjectAnimationVariant updated = Assert.Single(
            result.AnimationVariants);
        Assert.Equal(variant.Id, updated.Id);
        Assert.Equal(Sha('4'), updated.TargetRigSignature);
        Assert.Equal(
            ProjectAnimationBindingMode.Retarget,
            updated.BindingMode);
        Assert.Null(updated.DirectBinding);
        Assert.Equal(
            variant.BoneMappings,
            updated.BoneMappings);
        Assert.Equal(
            authoring,
            Assert.Single(result.Models)
                .AuthoringRigContractSignature);
        Assert.Equal(
            Sha('1'),
            Assert.Single(result.AnimationSources)
                .SourceRigSignature);
    }

    private static ProjectAnimation Compatibility(
        ProjectAnimationVariant variant,
        Guid targetAssetId,
        ProjectBoneMapping body,
        ProjectMorphBinding face) => new()
        {
            Id = variant.Id,
            Name = variant.Name,
            VariantGroupId = variant.SourceId,
            SourceAssetId = Guid.NewGuid(),
            TargetAssetId = targetAssetId,
            TargetRigId = variant.TargetRigId,
            TargetRigSignature = variant.TargetRigSignature,
            MappingFingerprint = variant.MappingFingerprint,
            BoneMappings = [body],
            TargetBindReviews = variant.TargetBindReviews,
            MorphBindings = [face],
        };

    private static ProjectBoneMapping ReviewedBone() => new()
    {
        SourceBoneName = "source_root",
        TargetBoneName = "target_root",
        Method = "manual",
        Confidence = 1.0,
        Evidence = "Explicit author review.",
        ReviewOrigin = ProjectMappingReviewOrigin.Explicit,
        ScorerVersion = "manual-v1",
        EvidenceFingerprint = Sha('a'),
        IsReviewed = true,
        IsLocked = true,
    };

    private static ProjectMorphBinding ReviewedMorph() => new()
    {
        SourceChannel = "source_face",
        TargetMorph = "target_face",
        Method = "manual",
        Confidence = 1.0,
        Evidence = "Explicit author review.",
        ReviewOrigin = ProjectMappingReviewOrigin.Explicit,
        ScorerVersion = "manual-v1",
        EvidenceFingerprint = Sha('b'),
        IsReviewed = true,
        IsLocked = true,
    };

    private static string Sha(char value) => new(value, 64);
}

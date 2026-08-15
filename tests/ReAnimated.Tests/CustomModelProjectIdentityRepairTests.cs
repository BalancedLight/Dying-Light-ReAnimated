using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class CustomModelProjectIdentityRepairTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void KnownSchema2AuthoringSignatureStateRepairsAllRuntimeIdentities()
    {
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-character.fbx");
        RigDefinition rig = Assert.IsType<RigDefinition>(imported.Rig);
        Assert.NotEmpty(imported.Package.Document.AnimationClips);
        CustomModelAnimationClip clip = imported.Package.Document
            .AnimationClips[0];
        ProjectAssetReference asset = CreatePackageAsset(imported);
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        string legacyAuthoring = imported.Package.Document.RigSignature;
        var model = new ProjectModelEntry
        {
            Id = modelId,
            AssetId = asset.Id,
            Name = "Generic character",
            RigSignature = legacyAuthoring,
            MorphSignature = imported.Package.Document.MorphSignature,
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = clip.DisplayName,
            SourceAssetId = asset.Id,
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = clip.Id,
                    FbxObjectId = clip.FbxObjectId,
                    StackFingerprint = clip.SourceFingerprint,
                    SourceRigSignature = legacyAuthoring,
                    Roles = AnimationSourceRoles.Body,
                },
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = variantId,
            SourceId = sourceId,
            Name = clip.DisplayName,
            TargetModelId = modelId,
            TargetRigId = rig.Id,
            TargetRigSignature = legacyAuthoring,
        };
        var project = DlraProject.Create("Identity repair") with
        {
            Assets = [asset],
            Models = [model],
            AnimationSources = [source],
            AnimationVariants = [variant],
        };
        project.Validate();

        CustomModelProjectIdentityRepairResult result =
            CustomModelProjectIdentityRepair.Repair(
                project,
                asset,
                imported);

        string runtime = RigSignature.Compute(rig);
        string skeleton = AnimationSkeletonSignature.Compute(rig);
        Assert.True(result.WasRepaired);
        ProjectModelEntry repairedModel = Assert.Single(result.Project.Models);
        ProjectAnimationSource repairedSource = Assert.Single(
            result.Project.AnimationSources);
        ProjectAnimationVariant repairedVariant = Assert.Single(
            result.Project.AnimationVariants);
        Assert.Equal(modelId, repairedModel.Id);
        Assert.Equal(sourceId, repairedSource.Id);
        Assert.Equal(variantId, repairedVariant.Id);
        Assert.Equal(runtime, repairedModel.RigSignature);
        Assert.Equal(legacyAuthoring,
            repairedModel.AuthoringRigContractSignature);
        Assert.Equal(skeleton, repairedModel.AnimationSkeletonSignature);
        Assert.Equal(runtime, repairedSource.SourceRigSignature);
        Assert.Equal(skeleton,
            repairedSource.SourceAnimationSkeletonSignature);
        Assert.Equal(runtime, repairedVariant.TargetRigSignature);
        Assert.Equal(skeleton,
            repairedVariant.TargetAnimationSkeletonSignature);
        Assert.Equal(
            ProjectAnimationBindingMode.ExactDirect,
            repairedVariant.BindingMode);
        Assert.Null(repairedVariant.DirectBinding);
        result.Project.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void StackFingerprintMismatchRefusesAutomaticIdentityRepair()
    {
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-character.fbx");
        Assert.NotEmpty(imported.Package.Document.AnimationClips);
        CustomModelAnimationClip clip = imported.Package.Document
            .AnimationClips[0];
        ProjectAssetReference asset = CreatePackageAsset(imported);
        string legacyAuthoring = imported.Package.Document.RigSignature;
        var model = new ProjectModelEntry
        {
            AssetId = asset.Id,
            Name = "Generic character",
            RigSignature = legacyAuthoring,
        };
        var source = new ProjectAnimationSource
        {
            Name = clip.DisplayName,
            SourceAssetId = asset.Id,
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = clip.Id,
                    FbxObjectId = clip.FbxObjectId,
                    StackFingerprint = new string('f', 64),
                    SourceRigSignature = legacyAuthoring,
                },
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
        };
        DlraProject project = DlraProject.Create("Refusal") with
        {
            Assets = [asset],
            Models = [model],
            AnimationSources = [source],
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CustomModelProjectIdentityRepair.Repair(
                project,
                asset,
                imported));

        Assert.Contains(
            "stack identity",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void HistoricalSourceOnlyPackageRepairsWithoutCurrentModelOwner()
    {
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-source.fbx");
        RigDefinition rig = Assert.IsType<RigDefinition>(imported.Rig);
        CustomModelAnimationClip clip = imported.Package.Document
            .AnimationClips[0];
        ProjectAssetReference asset = CreatePackageAsset(imported);
        Guid sourceId = Guid.NewGuid();
        string authoring = imported.Package.Document.RigSignature;
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = clip.DisplayName,
            SourceAssetId = asset.Id,
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = clip.Id,
                    FbxObjectId = clip.FbxObjectId,
                    StackFingerprint = clip.SourceFingerprint,
                    SourceRigSignature = authoring,
                    Roles = AnimationSourceRoles.Body,
                },
            FrameRate = clip.FrameRate,
            FrameCount = clip.FrameCount,
        };
        DlraProject project = DlraProject.Create("Historical source") with
        {
            Assets = [asset],
            AnimationSources = [source],
        };
        project.Validate();

        CustomModelProjectIdentityRepairResult result =
            CustomModelProjectIdentityRepair.Repair(
                project,
                asset,
                imported);

        ProjectAnimationSource repaired = Assert.Single(
            result.Project.AnimationSources);
        Assert.Equal(sourceId, repaired.Id);
        Assert.Equal(RigSignature.Compute(rig), repaired.SourceRigSignature);
        Assert.Equal(
            AnimationSkeletonSignature.Compute(rig),
            repaired.SourceAnimationSkeletonSignature);
        Assert.Empty(result.Project.Models);
        result.Project.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void TargetAssignmentResolvesBindingAgainAfterCustomModelRepair()
    {
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-source-model.fbx");
        RigDefinition rig = Assert.IsType<RigDefinition>(imported.Rig);
        ProjectAssetReference modelAsset = CreatePackageAsset(imported);
        Guid animationAssetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string authoring = imported.Package.Document.RigSignature;
        var staleSource = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Generic local animation",
            SourceAssetId = animationAssetId,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalAnm2,
                AssetId = animationAssetId,
                Roles = AnimationSourceRoles.Body,
                SourceRigSignature = authoring,
                RetailSourceModelAssetId = modelAsset.Id,
                TimingProvenance =
                    AnimationTimingProvenance.UserSpecified,
                Partition = new Anm2TrackPartition
                {
                    BodyDescriptors = [0x12345678u],
                    Fingerprint = new string('b', 64),
                },
            },
            FrameCount = 2,
        };
        DlraProject project = DlraProject.Create("Target assignment repair") with
        {
            Assets =
            [
                modelAsset,
                new ProjectAssetReference
                {
                    Id = animationAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-local.anm2",
                    ContentSha256 = new string('c', 64),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    AssetId = modelAsset.Id,
                    Name = "Generic source model",
                    RigSignature = authoring,
                },
            ],
            AnimationSources = [staleSource],
        };
        project.Validate();

        CustomModelProjectIdentityRepairResult repair =
            CustomModelProjectIdentityRepair.Repair(
                project,
                modelAsset,
                imported);
        ProjectAnimationSource current =
            MainWindowViewModel.ResolveCurrentTargetAssignmentSource(
                repair.Project,
                sourceId);

        Assert.Equal(
            authoring,
            staleSource.SourceBinding!.SourceRigSignature);
        Assert.Equal(
            RigSignature.Compute(rig),
            current.SourceBinding!.SourceRigSignature);
        Assert.Equal(
            AnimationSkeletonSignature.Compute(rig),
            current.SourceAnimationSkeletonSignature);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void UnknownStoredRigSignatureRefusesAmbiguousRepair()
    {
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-character.fbx");
        ProjectAssetReference asset = CreatePackageAsset(imported);
        var model = new ProjectModelEntry
        {
            AssetId = asset.Id,
            Name = "Generic character",
            RigSignature = new string('b', 64),
        };
        DlraProject project = DlraProject.Create("Ambiguous repair") with
        {
            Assets = [asset],
            Models = [model],
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => CustomModelProjectIdentityRepair.Repair(
                project,
                asset,
                imported));

        Assert.Contains(
            "ambiguous",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectAssetReference CreatePackageAsset(
        FbxModelAuthoringImportResult imported) => new()
        {
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "Models/generic-character.dlrmodel",
            ResourceId =
                $"custom-model:{imported.Package.Document.ModelId:N}:generic-character",
            ContentSha256 = new string('a', 64),
        };
}

using System.Collections.Immutable;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspacePersistenceTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task DifferentCustomModelAddsLibraryEntryWithoutReplacingExistingVariant()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string projectPath = Path.Combine(directory, "multi-model.dlraproj");
        FbxModelAuthoringImportResult firstModel =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-first.fbx");
        FbxModelAuthoringImportResult secondModel = WithModelIdentity(
            firstModel,
            Guid.NewGuid(),
            "Generic second model");

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(directory, "assets.sqlite3"),
            Path.Combine(directory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(directory, "recovery.json")),
            new ProjectPathDialogs(projectPath),
            assets);

        SetModelsWorkspaceModel(viewModel, firstModel);
        DlraProject first = await PersistModelsWorkspaceAsync(
            viewModel,
            DlraProject.Create("Multi-model project"),
            projectPath);
        ProjectModelEntry firstEntry = Assert.Single(first.Models);
        ProjectAssetReference firstPackage = first.Assets.Single(
            asset => asset.Id == firstEntry.AssetId);

        Guid animationAssetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        ProjectAnimationSourceBinding binding = CreateFbxBinding(
            animationAssetId,
            firstEntry.RigSignature!);
        var animationAsset = new ProjectAssetReference
        {
            Id = animationAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/generic-motion.fbx",
            ContentSha256 = Sha('8'),
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Generic motion",
            SourceAssetId = animationAssetId,
            SourceBinding = binding,
            FrameCount = 2,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = variantId,
            SourceId = sourceId,
            Name = "Generic first-model variant",
            TargetModelId = firstEntry.Id,
            TargetRigId = "generic-rig",
            TargetRigSignature = firstEntry.RigSignature,
        };
        first = first with
        {
            Assets = first.Assets.Add(animationAsset),
            AnimationSources = [source],
            AnimationVariants = [variant],
        };
        first.Validate();

        SetModelsWorkspaceModel(viewModel, secondModel);
        DlraProject result = await PersistModelsWorkspaceAsync(
            viewModel,
            first,
            projectPath);
        ProjectSerializer.SaveAtomic(result, projectPath);
        DlraProject roundTripped = ProjectSerializer.Load(projectPath);

        Assert.Equal(2, roundTripped.Models.Length);
        Assert.Contains(
            roundTripped.Models,
            model => model.Id == firstEntry.Id &&
                     model.AssetId == firstPackage.Id);
        Assert.Contains(
            roundTripped.Assets,
            asset => asset == firstPackage);
        ProjectAnimationSource preservedSource = Assert.Single(
            roundTripped.AnimationSources,
            animationSource => animationSource.Id == source.Id);
        Assert.Equal(source.Name, preservedSource.Name);
        Assert.Equal(source.SourceAssetId, preservedSource.SourceAssetId);
        Assert.Equal(source.SourceBinding, preservedSource.SourceBinding);
        Assert.NotNull(preservedSource.Presentation);
        ProjectAnimationVariant preservedVariant = Assert.Single(
            roundTripped.AnimationVariants,
            animationVariant => animationVariant.Id == variant.Id);
        Assert.Equal(variant.SourceId, preservedVariant.SourceId);
        Assert.Equal(variant.Name, preservedVariant.Name);
        Assert.Equal(variant.TargetModelId, preservedVariant.TargetModelId);
        Assert.Equal(
            variant.TargetRigSignature,
            preservedVariant.TargetRigSignature);
        Assert.NotNull(preservedVariant.OwningAnimationLibraryId);
        Assert.False(string.IsNullOrWhiteSpace(
            preservedVariant.OutputAnm2Name));
        ProjectAnimationSource secondEmbeddedSource = Assert.Single(
            roundTripped.AnimationSources,
            static animationSource =>
                animationSource.EmbeddedCustomModelStack is not null);
        ProjectAnimationVariant secondDirectVariant = Assert.Single(
            roundTripped.AnimationVariants,
            animationVariant =>
                animationVariant.SourceId == secondEmbeddedSource.Id);
        Assert.Equal(
            roundTripped.Models.Single(model =>
                model.AssetId == secondEmbeddedSource.SourceAssetId).Id,
            secondDirectVariant.TargetModelId);
        ProjectModelsWorkspaceState workspace = Assert.IsType<ProjectModelsWorkspaceState>(
            roundTripped.ModelsWorkspace);
        ProjectAssetReference activePackage = roundTripped.Assets.Single(
            asset => asset.Id == workspace.PackageAssetId);
        Assert.Contains(
            secondModel.Package.Document.ModelId.ToString("N"),
            activePackage.ResourceId!,
            StringComparison.Ordinal);
        Assert.Null(roundTripped.Workflow.SelectedModelId);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task SameCustomModelReimportPreservesImmutableSourceAndStalesOnlyChangedContract()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string projectPath = Path.Combine(directory, "model-reimport.dlraproj");
        FbxModelAuthoringImportResult original =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "generic-target.fbx");

        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(directory, "assets.sqlite3"),
            Path.Combine(directory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(directory, "recovery.json")),
            new ProjectPathDialogs(projectPath),
            assets);

        SetModelsWorkspaceModel(viewModel, original);
        DlraProject first = await PersistModelsWorkspaceAsync(
            viewModel,
            DlraProject.Create("Reimport project"),
            projectPath);
        ProjectModelEntry originalEntry = Assert.Single(first.Models);
        Assert.Equal(0, originalEntry.ExportableEyeCameraHelperCount);
        Guid originalAssetId = originalEntry.AssetId;

        Guid animationAssetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        ProjectAnimationSourceBinding binding = CreateFbxBinding(
            animationAssetId,
            originalEntry.RigSignature!);
        ProjectBoneMapping bone = ReviewedBone();
        ProjectMorphBinding morph = ReviewedMorph();
        var animationAsset = new ProjectAssetReference
        {
            Id = animationAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/generic-reimport-motion.fbx",
            ContentSha256 = Sha('7'),
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Generic reimport motion",
            SourceAssetId = animationAssetId,
            SourceBinding = binding,
            FrameCount = 2,
        };
        var embeddedSource = new ProjectAnimationSource
        {
            Id = Guid.NewGuid(),
            Name = "Original embedded stack",
            SourceAssetId = originalAssetId,
            EmbeddedCustomModelStack =
                new ProjectEmbeddedAnimationStackIdentity
                {
                    ClipId = Guid.NewGuid(),
                    FbxObjectId = 42,
                    StackFingerprint = Sha('6'),
                    SourceRigSignature = originalEntry.RigSignature!,
                    Roles = AnimationSourceRoles.Body,
                },
            FrameCount = 2,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = variantId,
            SourceId = sourceId,
            Name = "Generic target variant",
            TargetModelId = originalEntry.Id,
            TargetRigId = "generic-rig",
            TargetRigSignature = originalEntry.RigSignature,
            MappingFingerprint = Sha('5'),
            BoneMappings = [bone],
            MorphBindings = [morph],
        };
        var compatibility = new ProjectAnimation
        {
            Id = variantId,
            VariantGroupId = sourceId,
            Name = variant.Name,
            SourceAssetId = animationAssetId,
            SourceBinding = binding,
            TargetAssetId = originalAssetId,
            TargetRigId = variant.TargetRigId,
            SourceRigSignature = originalEntry.RigSignature,
            TargetRigSignature = originalEntry.RigSignature,
            MappingFingerprint = variant.MappingFingerprint,
            FrameCount = 2,
            BoneMappings = [bone],
            MorphBindings = [morph],
        };
        first = first with
        {
            Assets = first.Assets.Add(animationAsset),
            AnimationSources = [source, embeddedSource],
            AnimationVariants = [variant],
            Animations = [compatibility],
        };
        first.Validate();

        CustomModelDocument replacementDocument =
            CustomModelHelperAuthoring.CreateEyeCameraHelper(
                original.Package.Document,
                sourceNodeIndex: 0);
        replacementDocument = CustomModelHelperAuthoring.SelectPreviewCamera(
            replacementDocument,
            nodeName: null);
        Assert.NotEqual(
            original.Package.Document.RigSignature,
            replacementDocument.RigSignature);
        Assert.Equal(
            original.Package.Document.MorphSignature,
            replacementDocument.MorphSignature);
        FbxModelAuthoringImportResult replacement = WithDocument(
            original,
            replacementDocument);

        SetModelsWorkspaceModel(viewModel, replacement);
        DlraProject result = await PersistModelsWorkspaceAsync(
            viewModel,
            first,
            projectPath);
        ProjectSerializer.SaveAtomic(result, projectPath);
        DlraProject roundTripped = ProjectSerializer.Load(projectPath);

        ProjectModelEntry updatedModel = Assert.Single(roundTripped.Models);
        Assert.Equal(originalEntry.Id, updatedModel.Id);
        Assert.NotEqual(originalAssetId, updatedModel.AssetId);
        string replacementRuntimeSignature = RigSignature.Compute(
            replacement.Rig!);
        Assert.Equal(replacementRuntimeSignature, updatedModel.RigSignature);
        Assert.Equal(
            replacementDocument.RigSignature,
            updatedModel.AuthoringRigContractSignature);
        Assert.Equal(
            AnimationSkeletonSignature.Compute(replacement.Rig!),
            updatedModel.AnimationSkeletonSignature);
        Assert.Null(updatedModel.PreviewCameraNodeName);
        Assert.Equal(1, updatedModel.ExportableEyeCameraHelperCount);
        Assert.Contains(roundTripped.Assets, asset => asset.Id == originalAssetId);
        Assert.Contains(roundTripped.Assets, asset => asset.Id == updatedModel.AssetId);
        Assert.Equal(
            originalAssetId,
            roundTripped.AnimationSources.Single(animationSource =>
                animationSource.Id == embeddedSource.Id).SourceAssetId);

        ProjectAnimationVariant updatedVariant =
            roundTripped.AnimationVariants.Single(
                animationVariant => animationVariant.Id == variantId);
        Assert.Equal(originalEntry.Id, updatedVariant.TargetModelId);
        Assert.Equal(
            replacementRuntimeSignature,
            updatedVariant.TargetRigSignature);
        Assert.Null(updatedVariant.MappingFingerprint);
        ProjectBoneMapping staleBone = Assert.Single(updatedVariant.BoneMappings);
        Assert.False(staleBone.IsReviewed);
        Assert.False(staleBone.IsLocked);
        Assert.Equal(ProjectMappingReviewOrigin.None, staleBone.ReviewOrigin);
        Assert.Equal(morph, Assert.Single(updatedVariant.MorphBindings));

        ProjectAnimation updatedCompatibility = Assert.Single(
            roundTripped.Animations);
        Assert.Equal(updatedModel.AssetId, updatedCompatibility.TargetAssetId);
        Assert.Equal(animationAssetId, updatedCompatibility.SourceAssetId);
        Assert.False(Assert.Single(updatedCompatibility.BoneMappings).IsReviewed);
        Assert.Equal(morph, Assert.Single(updatedCompatibility.MorphBindings));
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task ProjectSaveAndOpenRestoresOwnedCustomModelWithoutOriginalFbx()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string projectPath = Path.Combine(directory, "portable-model.dlraproj");
        FbxModelAuthoringImportResult imported =
            FbxModelAuthoringImporter.Import(
                BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                "portable_character.fbx");
        var restoredState = new ProjectModelsWorkspaceState
        {
            PackageAssetId = Guid.NewGuid(),
            PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            ShowMeshes = false,
            ShowBones = true,
            ShowHelpers = false,
            ShowCameraHelpers = true,
            ShowPropHelpers = false,
        };

        await using (var firstAssets = new Dl1AssetWorkspace(
                         Path.Combine(directory, "first-assets.sqlite3"),
                         Path.Combine(directory, "first-cache")))
        await using (var first = new MainWindowViewModel(
                         new JsonWorkspaceStateStore(
                             Path.Combine(directory, "first-recovery.json")),
                         new ProjectPathDialogs(projectPath),
                         firstAssets))
        {
            first.Models.CommitProjectRestore(
                new PreparedModelsWorkspaceRestore(
                    imported,
                    Path.Combine(directory, "not-an-original-fbx.dlrmodel"),
                    restoredState));
            first.Models.ModelName = "Portable character";
            first.Models.ResourceName = "portable_character";
            first.Models.CharacterId = "portable_character_id";
            first.Models.AnimationScriptAlias = "PortableLibrary";

            await first.SaveWorkspaceCommand.ExecuteAsync(null);

            Assert.True(File.Exists(projectPath), first.StatusText);
            Assert.NotNull(first.CurrentProject.ModelsWorkspace);
        }

        DlraProject saved = ProjectSerializer.Load(projectPath);
        ProjectModelsWorkspaceState savedState =
            Assert.IsType<ProjectModelsWorkspaceState>(
                saved.ModelsWorkspace);
        ProjectAssetReference packageAsset = Assert.Single(
            saved.Assets,
            asset => asset.Id == savedState.PackageAssetId);
        Assert.Equal(
            ProjectAssetKind.CustomModelSource,
            packageAsset.Kind);
        Assert.False(Path.IsPathRooted(packageAsset.RelativePath));
        string packagePath = Path.Combine(
            Path.GetDirectoryName(projectPath)!,
            packageAsset.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(packagePath));
        Assert.EndsWith(
            ".dlrmodel",
            packagePath,
            StringComparison.OrdinalIgnoreCase);

        await using var secondAssets = new Dl1AssetWorkspace(
            Path.Combine(directory, "second-assets.sqlite3"),
            Path.Combine(directory, "second-cache"));
        await using var second = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(directory, "second-recovery.json")),
            new ProjectPathDialogs(projectPath),
            secondAssets);

        await second.OpenWorkspaceCommand.ExecuteAsync(null);

        Assert.True(second.Models.HasModel, second.StatusText);
        Assert.Equal("Portable character", second.Models.ModelName);
        Assert.Equal("portable_character", second.Models.ResourceName);
        Assert.Equal("portable_character_id", second.Models.CharacterId);
        Assert.Equal("PortableLibrary", second.Models.AnimationScriptAlias);
        Assert.False(second.Models.ShowMeshes);
        Assert.True(second.Models.ShowBones);
        Assert.False(second.Models.ShowHelpers);
        Assert.True(second.Models.ShowCameraHelpers);
        Assert.False(second.Models.ShowPropHelpers);
        Assert.Equal(
            CustomModelPreviewMode.SourceFbx,
            second.Models.SelectedPreviewMode.Mode);
        Assert.Equal("Models", second.ActiveWorkspaceMode);
        Assert.Equal(
            imported.Package.Document.ModelId,
            second.Models.CaptureProjectSession()
                .Model!
                .Package
                .Document
                .ModelId);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public async Task FailedPackagePreparationRetainsActiveModelsWorkspaceSession()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        string corruptPackagePath = Path.Combine(directory, "broken.dlrmodel");
        await File.WriteAllTextAsync(corruptPackagePath, "not a custom-model package");

        using var viewModel = new ModelsWorkspaceViewModel(
            new NullProjectFileDialogs(),
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null);
        var state = new ProjectModelsWorkspaceState
        {
            PackageAssetId = Guid.NewGuid(),
            PreviewMode = ProjectCustomModelPreviewMode.SourceFbx,
            ShowMeshes = true,
            ShowBones = false,
            ShowHelpers = false,
            ShowCameraHelpers = true,
            ShowPropHelpers = false,
        };
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: true),
            Path.Combine(directory, "active.dlrmodel"),
            state));
        ModelsWorkspaceSessionSnapshot before = viewModel.CaptureProjectSession();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            viewModel.PrepareProjectRestoreAsync(
                corruptPackagePath,
                state,
                CancellationToken.None));

        ModelsWorkspaceSessionSnapshot after = viewModel.CaptureProjectSession();
        Assert.NotNull(before.Model);
        Assert.NotNull(after.Model);
        Assert.Equal(
            before.Model.Package.Document.ModelId,
            after.Model.Package.Document.ModelId);
        Assert.Equal(
            before.Model.Package.Document.Source.ContentSha256,
            after.Model.Package.Document.Source.ContentSha256);
        Assert.Equal(
            before.Model.Package.Document.RigSignature,
            after.Model.Package.Document.RigSignature);
        Assert.Equal(before.Model.Surfaces.Length, after.Model.Surfaces.Length);
        Assert.True(before.Model.Package.SourceFbx.SequenceEqual(after.Model.Package.SourceFbx));
        Assert.Equal(before.PackagePath, after.PackagePath);
        Assert.Equal(before.AuthoringRevision, after.AuthoringRevision);
        Assert.Equal(before.PreviewMode, after.PreviewMode);
        Assert.Equal(before.ShowMeshes, after.ShowMeshes);
        Assert.Equal(before.ShowBones, after.ShowBones);
        Assert.Equal(before.ShowHelpers, after.ShowHelpers);
        Assert.Equal(before.ShowCameraHelpers, after.ShowCameraHelpers);
        Assert.Equal(before.ShowPropHelpers, after.ShowPropHelpers);
        Assert.True(viewModel.HasModel);
        Assert.Equal("Synthetic preview model", viewModel.ModelName);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public void ModelsWorkspaceAuthorsEyeCameraOffsetsAndUndoWithoutChangingImportedBones()
    {
        var dialogs = new EyeCameraProjectDialogs();
        using var viewModel = new ModelsWorkspaceViewModel(
            dialogs,
            static _ => { },
            static _ => Task.CompletedTask,
            static () => null);
        FbxModelAuthoringImportResult imported =
            CustomModelPreviewSessionTests.CreateModel(
                flipTextureCoordinateV: true);
        int importedBoneCount = imported.Package.Document.Bones.Length;
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
            imported,
            "synthetic.dlrmodel",
            new ProjectModelsWorkspaceState
            {
                PackageAssetId = Guid.NewGuid(),
            }));

        viewModel.SelectPreviewCameraCommand.Execute(null);

        CustomModelDocument promoted = viewModel.CaptureProjectSession()
            .Model!.Package.Document;
        CustomModelAuthoredHelper eyeCamera = Assert.Single(
            promoted.AuthoredHelpers);
        Assert.Equal(importedBoneCount, promoted.Bones.Length);
        Assert.Equal("EyeCamera", eyeCamera.Name);
        Assert.Equal("EyeCamera", promoted.Camera.ActivePreviewNodeName);
        Assert.True(viewModel.CanEditSelectedHelper);
        Assert.Contains("EyeCamera", viewModel.PreviewCameraStatus, StringComparison.Ordinal);

        viewModel.HelperTranslationX = 0.125;
        viewModel.HelperRotationY = 15.0;
        viewModel.ApplyHelperTransformCommand.Execute(null);
        CustomModelAuthoredHelper moved = Assert.Single(
            viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.Equal(0.125, moved.LocalTransform.Translation.X, 8);
        Assert.NotEqual(QuaternionD.Identity, moved.LocalTransform.Rotation);

        viewModel.UndoHelperEditCommand.Execute(null);
        CustomModelAuthoredHelper restored = Assert.Single(
            viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        Assert.Equal(0.0, restored.LocalTransform.Translation.X, 8);
        Assert.True(viewModel.RedoHelperEditCommand.CanExecute(null));
        viewModel.RedoHelperEditCommand.Execute(null);
        Assert.Equal(moved, Assert.Single(viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers));
        viewModel.UndoHelperEditCommand.Execute(null);
        viewModel.UndoHelperEditCommand.Execute(null);
        Assert.Empty(viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        viewModel.RedoHelperEditCommand.Execute(null);
        Assert.Equal(eyeCamera.Id, Assert.Single(viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers).Id);
        viewModel.HelperTranslationX = .25;
        viewModel.ApplyHelperTransformCommand.Execute(null);
        Assert.False(viewModel.RedoHelperEditCommand.CanExecute(null));
    }

    [Fact]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ConformanceUndoRedoRestoresMeshBindingsWithTheDocument()
    {
        using var viewModel = new ModelsWorkspaceViewModel(new NullProjectFileDialogs(), static _ => { },
            static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (profile, _) => Task.FromResult(RigConformanceWizardTests.CreateResolution(profile)),
            // This test isolates history with an in-memory rig fixture. Binary source replay has separate real-FBX coverage.
            captureAuthoredLayer: static (model, _) => model);
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(RigConformanceWizardTests.CreateModel(), "synthetic.dlrmodel",
            new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        FbxModelAuthoringImportResult original = viewModel.CaptureProjectSession().Model!;
        await viewModel.Conformance.ResolveTemplateCommand.ExecuteAsync(null);
        Assert.True(viewModel.Conformance.ApplyConformanceCommand.CanExecute(null));
        viewModel.Conformance.ApplyConformanceCommand.Execute(null);
        FbxModelAuthoringImportResult conformed = viewModel.CaptureProjectSession().Model!;
        Assert.NotEqual(original.Package.Document.RigSignature, conformed.Package.Document.RigSignature);
        Assert.True(viewModel.UndoHelperEditCommand.CanExecute(null));
        viewModel.UndoHelperEditCommand.Execute(null);
        FbxModelAuthoringImportResult undone = viewModel.CaptureProjectSession().Model!;
        Assert.Equal(original.Package.Document.RigSignature, undone.Package.Document.RigSignature);
        Assert.Same(original.Surfaces[0], undone.Surfaces[0]);
        Assert.Equal(original.Package.Document.Bones.Select(b => b.Name), undone.Package.Document.Bones.Select(b => b.Name));
        viewModel.RedoHelperEditCommand.Execute(null);
        FbxModelAuthoringImportResult redone = viewModel.CaptureProjectSession().Model!;
        Assert.Equal(conformed.Package.Document.RigSignature, redone.Package.Document.RigSignature);
        Assert.Same(conformed.Surfaces[0], redone.Surfaces[0]);
        Assert.True(original.Package.SourceFbx.AsSpan().SequenceEqual(redone.Package.SourceFbx.AsSpan()));
    }

    [Fact]
    [Trait("Gate", "ViewModelWpf")]
    public void StudioHelperHistoryRestoresDecisionsWithoutRevivingStaleJobs()
    {
        using var viewModel = new ModelsWorkspaceViewModel(new EyeCameraProjectDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        var imported = CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: true);
        var session = RiggingSessions.Create(imported.Package.Document, RigStudioEntryPath.RepairExistingRig);
        imported = imported with { Package = imported.Package with { Document = imported.Package.Document with { RiggingSession = session } } };
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(imported, "synthetic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        viewModel.SelectPreviewCameraCommand.Execute(null);
        var created = viewModel.CaptureProjectSession().Model!.Package.Document;
        var token = created.RiggingSession!.CreateJobToken();
        Guid helperId = Assert.Single(created.AuthoredHelpers).Id;
        Assert.Contains(created.RiggingSession.Recipe.Entities, e => e.EntityId == helperId);
        viewModel.HelperTranslationX = .125;
        viewModel.ApplyHelperTransformCommand.Execute(null);
        var moved = viewModel.CaptureProjectSession().Model!.Package.Document;
        Assert.False(moved.RiggingSession!.Matches(token));
        Assert.NotEqual(created.RiggingSession.ComputeInputFingerprint(), moved.RiggingSession.ComputeInputFingerprint());
        viewModel.UndoHelperEditCommand.Execute(null);
        var restored = viewModel.CaptureProjectSession().Model!.Package.Document;
        Assert.Equal(created.RiggingSession.ComputeInputFingerprint(), restored.RiggingSession!.ComputeInputFingerprint());
        Assert.False(restored.RiggingSession.Matches(token));
        Assert.Equal(helperId, viewModel.SelectedBone!.AuthoredHelperId);
        viewModel.RedoHelperEditCommand.Execute(null);
        var redone = viewModel.CaptureProjectSession().Model!.Package.Document;
        Assert.Equal(moved.RiggingSession.ComputeInputFingerprint(), redone.RiggingSession!.ComputeInputFingerprint());
        Assert.True(redone.RiggingSession.Revision > moved.RiggingSession.Revision);
    }

    [Fact]
    [Trait("Gate", "ViewModelWpf")]
    public void ApplyingPreparedHelpersIsOneUndoableModelTransaction()
    {
        using var viewModel = new ModelsWorkspaceViewModel(new NullProjectFileDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        var imported = CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: true);
        imported = imported with { Package = imported.Package with { Document = RiggingHelperEditTests.DocumentWithPendingHelpers(imported.Package.Document) } };
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(imported, "synthetic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.True(viewModel.ApplyPreparedHelpersCommand.CanExecute(null));
        viewModel.ApplyPreparedHelpersCommand.Execute(null);
        var applied = viewModel.CaptureProjectSession().Model!;
        Assert.Equal(2, applied.Package.Document.AuthoredHelpers.Length);
        viewModel.UndoHelperEditCommand.Execute(null);
        Assert.Empty(viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers);
        viewModel.RedoHelperEditCommand.Execute(null);
        var restored = viewModel.CaptureProjectSession().Model!;
        Assert.Equal<CustomModelAuthoredHelper>(applied.Package.Document.AuthoredHelpers, restored.Package.Document.AuthoredHelpers);
        Assert.Same(imported.Surfaces[0], restored.Surfaces[0]);
    }

    [Fact]
    [Trait("Gate", "ViewModelWpf")]
    public void InterleavedBuildSettingEditHasItsOwnUndoEntry()
    {
        using var viewModel = new ModelsWorkspaceViewModel(new EyeCameraProjectDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(CustomModelPreviewSessionTests.CreateModel(flipTextureCoordinateV: true),
            "synthetic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        viewModel.SelectPreviewCameraCommand.Execute(null);
        Guid helper = Assert.Single(viewModel.CaptureProjectSession().Model!.Package.Document.AuthoredHelpers).Id;
        viewModel.FlipTextureCoordinateV = false;
        Assert.False(viewModel.CaptureProjectSession().Model!.Package.Document.BuildSettings.FlipTextureCoordinateV);
        viewModel.UndoHelperEditCommand.Execute(null);
        var undone = viewModel.CaptureProjectSession().Model!.Package.Document;
        Assert.True(undone.BuildSettings.FlipTextureCoordinateV);
        Assert.Equal(helper, Assert.Single(undone.AuthoredHelpers).Id);
        viewModel.RedoHelperEditCommand.Execute(null);
        Assert.False(viewModel.CaptureProjectSession().Model!.Package.Document.BuildSettings.FlipTextureCoordinateV);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task ReimportValidationCancelPreservesModelAndConfirmPreservesAuthoredHelpers()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] fbx = BlenderFbxStrictValidationTests.CreateValidModelFixture();
            string replacementPath = Path.Combine(directory, "generic-replacement.fbx");
            await File.WriteAllBytesAsync(replacementPath, fbx);
            FbxModelAuthoringImportResult imported =
                FbxModelAuthoringImporter.Import(fbx, "generic-original.fbx");
            CustomModelDocument authored = CustomModelHelperAuthoring.DuplicateAsHelper(
                imported.Package.Document,
                0,
                CustomModelAuthoredHelperKind.Prop,
                "generic_prop_anchor");
            imported = imported with
            {
                Package = new CustomModelPackage(
                    authored,
                    imported.Package.SourceFbx,
                    imported.Package.TexturePayloads),
                Rig = authored.CreateRigDefinition(),
            };
            var dialogs = new ReimportProjectDialogs();
            using var viewModel = new ModelsWorkspaceViewModel(
                dialogs,
                static _ => { },
                static _ => Task.CompletedTask,
                static () => null);
            viewModel.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
                imported,
                "generic.dlrmodel",
                new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                }));

            dialogs.ConfirmReimport = false;
            await viewModel.ImportPathAsync(
                replacementPath,
                imported.Package.Document.RigMode);
            ModelsWorkspaceSessionSnapshot canceled =
                viewModel.CaptureProjectSession();
            Assert.Equal(imported.Package.Document.ModelId,
                canceled.Model!.Package.Document.ModelId);
            Assert.Equal("generic_prop_anchor", Assert.Single(
                canceled.Model.Package.Document.AuthoredHelpers).Name);
            Assert.Contains("canceled", viewModel.BuildStatus,
                StringComparison.OrdinalIgnoreCase);

            dialogs.ConfirmReimport = true;
            await viewModel.ImportPathAsync(
                replacementPath,
                imported.Package.Document.RigMode);
            CustomModelDocument confirmed = viewModel.CaptureProjectSession()
                .Model!.Package.Document;
            Assert.Equal(imported.Package.Document.ModelId, confirmed.ModelId);
            Assert.Equal("generic_prop_anchor",
                Assert.Single(confirmed.AuthoredHelpers).Name);
            Assert.Equal(2, dialogs.ReimportConfirmationCalls);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static void SetModelsWorkspaceModel(
        MainWindowViewModel viewModel,
        FbxModelAuthoringImportResult model) =>
        viewModel.Models.CommitProjectRestore(
            new PreparedModelsWorkspaceRestore(
                model,
                "generic.dlrmodel",
                new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                }));

    private static async Task<DlraProject> PersistModelsWorkspaceAsync(
        MainWindowViewModel viewModel,
        DlraProject project,
        string projectPath)
    {
        MethodInfo method = typeof(MainWindowViewModel).GetMethod(
                "PersistModelsWorkspaceAsync",
                BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException(
                "Models-workspace persistence entry point was not found.");
        object? invocation = method.Invoke(
            viewModel,
            [project, projectPath, CancellationToken.None]);
        Task<DlraProject> task = Assert.IsAssignableFrom<Task<DlraProject>>(
            invocation);
        return await task;
    }

    private static FbxModelAuthoringImportResult WithModelIdentity(
        FbxModelAuthoringImportResult model,
        Guid modelId,
        string name) =>
        WithDocument(
            model,
            model.Package.Document with
            {
                ModelId = modelId,
                Name = name,
            });

    private static FbxModelAuthoringImportResult WithDocument(
        FbxModelAuthoringImportResult model,
        CustomModelDocument document)
    {
        document.Validate();
        return model with
        {
            Package = new CustomModelPackage(
                document,
                model.Package.SourceFbx,
                model.Package.TexturePayloads),
            Rig = document.Bones.IsEmpty
                ? null
                : document.CreateRigDefinition(),
        };
    }

    private static ProjectAnimationSourceBinding CreateFbxBinding(
        Guid assetId,
        string rigSignature) => new()
        {
            Kind = AnimationSourceKind.LocalFbx,
            AssetId = assetId,
            Roles = AnimationSourceRoles.Body,
            SourceRigSignature = rigSignature,
            TimingProvenance = AnimationTimingProvenance.EmbeddedFbx,
            SourceRangeStartFrame = 0,
            SourceRangeEndFrame = 1,
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

    private sealed class NullProjectFileDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }

    private sealed class EyeCameraProjectDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;

        public CustomModelPreviewCameraDecision ConfirmCustomModelPreviewCamera(
            string selectedNodeName,
            bool exactEyeCameraExists) =>
            CustomModelPreviewCameraDecision.CreateEyeCamera;
    }

    private sealed class ReimportProjectDialogs : IProjectFileDialogService
    {
        public bool ConfirmReimport { get; set; }

        public int ReimportConfirmationCalls { get; private set; }

        public string? ShowOpenProjectDialog(string? initialPath) => null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;

        public bool ConfirmCustomModelReimport(
            string replacementFileName,
            bool boneAndHelperMappingsBecomeStale,
            bool facialMappingsBecomeStale)
        {
            ReimportConfirmationCalls++;
            return ConfirmReimport;
        }
    }

    private sealed class ProjectPathDialogs(string projectPath) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            projectPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            projectPath;
    }
}

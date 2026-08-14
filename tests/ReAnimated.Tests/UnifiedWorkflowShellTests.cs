using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class UnifiedWorkflowShellTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Workflow-{Guid.NewGuid():N}");

    public UnifiedWorkflowShellTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task NewProjectStartsOnModelsAndStagesRemainFreelyNavigable()
    {
        await using var viewModel = CreateViewModel();

        Assert.Equal("Models", viewModel.ActiveWorkspaceMode);
        Assert.Equal(
            ["Models", "Animations", "Playback", "Retarget/Edit", "Export"],
            viewModel.WorkspaceModes);

        foreach (string stage in viewModel.WorkspaceModes)
        {
            viewModel.SelectWorkspaceCommand.Execute(stage);
            Assert.Equal(
                stage == "Retarget/Edit" ? "Retarget" : stage,
                viewModel.ActiveWorkspaceMode);
        }

        viewModel.ActiveWorkspaceMode = "Playback";
        viewModel.IsFppPlaybackEnabled = true;
        Assert.True(viewModel.IsPlaybackWorkspace);
        Assert.True(viewModel.IsFppPlaybackEnabled);
        viewModel.IsFppPlaybackEnabled = false;
        Assert.Equal("Playback", viewModel.ActiveWorkspaceMode);
    }

    [Fact]
    public void PlaybackCameraUsesModelSelectionWhileEyeCameraRemainsFallback()
    {
        var selected = new ProjectModelEntry
        {
            AssetId = Guid.NewGuid(),
            Name = "Generic rig",
            RigSignature = new string('a', 64),
            PreviewCameraNodeName = "preview_mount",
            ExportableEyeCameraHelperCount = 0,
        };

        Assert.Equal(
            "preview_mount",
            MainWindowViewModel.ResolvePlaybackPreviewCameraNodeName(
                selected));
        Assert.Equal(
            Dl1PreviewContract.EyeCameraBoneName,
            MainWindowViewModel.ResolvePlaybackPreviewCameraNodeName(
                null));
    }

    [Theory]
    [InlineData(ProjectWorkflowTab.Models, EditorWorkspaceMode.Models)]
    [InlineData(ProjectWorkflowTab.Animations, EditorWorkspaceMode.Animations)]
    [InlineData(ProjectWorkflowTab.Playback, EditorWorkspaceMode.Playback)]
    [InlineData(ProjectWorkflowTab.RetargetEdit, EditorWorkspaceMode.RetargetEdit)]
    [InlineData(ProjectWorkflowTab.Export, EditorWorkspaceMode.Export)]
    public void SchemaTwoWorkflowRestoresItsSavedTab(
        ProjectWorkflowTab tab,
        EditorWorkspaceMode expected)
    {
        DlraProject project = DlraProject.Create("Workflow") with
        {
            Workflow = new ProjectWorkflowState
            {
                ActiveTab = tab,
            },
        };

        Assert.Equal(
            expected,
            MainWindowViewModel.ResolveStartupWorkspace(project));
    }

    [Fact]
    public async Task AssistedReviewSettingDefaultsOffAndDoesNotMutateProject()
    {
        string settingsPath = Path.Combine(
            _tempDirectory,
            "settings",
            "retarget-review.json");
        var settings = new AssistedReviewSettingsStore(settingsPath);
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(Path.Combine(
                _tempDirectory,
                "recovery.json")),
            structuredLogger: null,
            assistedReviewSettingsStore: settings);
        DlraProject before = viewModel.CurrentProject;

        Assert.False(viewModel.IsAssistedReviewEnabled);
        viewModel.IsAssistedReviewEnabled = true;

        Assert.Same(before, viewModel.CurrentProject);
        Assert.True(new AssistedReviewSettingsStore(
            settingsPath).LoadEnabled());
        Assert.False(viewModel.ApplyAssistedReviewCommand.CanExecute(null));
    }

    [Fact]
    public void ExplicitFacialDecisionPreservesEvidenceAndRecordsItsOrigin()
    {
        var binding = new ProjectMorphBinding
        {
            SourceChannel = "Smile",
            SourceValueUnit = ProjectMorphSourceValueUnit.Percent,
            TargetMorph = "morph_smile",
            TargetDescriptorHash = 0x1000u,
            Confidence = 0.95,
            Method = "semantic_alias",
            Evidence = "A declared facial semantic alias matched uniquely.",
            ScorerVersion = "facial-score-v1",
            EvidenceFingerprint = new string('a', 64),
        };
        var row = new FacialMorphBindingReviewViewModel(binding)
        {
            IsReviewed = true,
            IsLocked = true,
        };

        ProjectMorphBinding reviewed = row.BuildBinding();

        Assert.Equal(
            ProjectMappingReviewOrigin.Explicit,
            reviewed.ReviewOrigin);
        Assert.Equal(binding.Evidence, reviewed.Evidence);
        Assert.Equal(binding.ScorerVersion, reviewed.ScorerVersion);
        Assert.Equal(
            binding.EvidenceFingerprint,
            reviewed.EvidenceFingerprint);
    }

    [Fact]
    public void SameRigFacialTracksDoNotRequireManufacturedMappings()
    {
        Assert.True(MainWindowViewModel.IsFacialMappingExportReady(
            hasFacialSource: true,
            directSameRig: true,
            []));
        Assert.False(MainWindowViewModel.IsFacialMappingExportReady(
            hasFacialSource: true,
            directSameRig: false,
            []));

        var staleAssisted = new ProjectMorphBinding
        {
            SourceChannel = "generic_smile",
            TargetMorph = "generic_smile",
            Confidence = 1.0,
            Evidence = "Unique exact identity.",
            ReviewOrigin = ProjectMappingReviewOrigin.Assisted,
            ScorerVersion = "superseded-facial-scorer",
            EvidenceFingerprint = new string('b', 64),
            Method = "exact_identity",
            IsReviewed = true,
            IsLocked = true,
        };
        Assert.False(MainWindowViewModel.IsFacialMappingExportReady(
            hasFacialSource: true,
            directSameRig: false,
            [staleAssisted]));
        Assert.True(MainWindowViewModel.IsFacialMappingExportReady(
            hasFacialSource: true,
            directSameRig: false,
            [staleAssisted with
            {
                ScorerVersion = ProjectMorphSuggestionScorer.PolicyVersion,
            }]));
    }

    [Fact]
    public void CompatibilityEditsUpdateSchemaTwoVariantAndPreserveEmbeddedSources()
    {
        string signature = new('a', 64);
        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        Guid embeddedAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        Guid embeddedSourceId = Guid.NewGuid();
        Guid embeddedVariantId = Guid.NewGuid();
        var binding = new ProjectAnimationSourceBinding
        {
            Kind = AnimationSourceKind.LocalFbx,
            AssetId = sourceAssetId,
            Roles = AnimationSourceRoles.Body,
            SourceRigSignature = signature,
            TimingProvenance =
                AnimationTimingProvenance.EmbeddedFbx,
            SourceRangeStartFrame = 0,
            SourceRangeEndFrame = 9,
        };
        var compatibility = new ProjectAnimation
        {
            Id = variantId,
            VariantGroupId = sourceId,
            Name = "Walk",
            SourceAssetId = sourceAssetId,
            SourceBinding = binding,
            TargetAssetId = targetAssetId,
            TargetRigId = "target",
            SourceRigSignature = signature,
            TargetRigSignature = signature,
            FrameCount = 10,
        };
        var project = DlraProject.Create("Sync") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "assets/walk.fbx",
                    ContentSha256 = new string('1', 64),
                },
                new ProjectAssetReference
                {
                    Id = targetAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "models/target.dlrmodel",
                    ContentSha256 = new string('2', 64),
                },
                new ProjectAssetReference
                {
                    Id = embeddedAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "models/source.dlrmodel",
                    ContentSha256 = new string('3', 64),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = targetAssetId,
                    Name = "Target",
                    RigSignature = signature,
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Walk",
                    SourceAssetId = sourceAssetId,
                    SourceBinding = binding,
                    FrameCount = 10,
                },
                new ProjectAnimationSource
                {
                    Id = embeddedSourceId,
                    Name = "Embedded",
                    SourceAssetId = embeddedAssetId,
                    EmbeddedCustomModelStack =
                        new ProjectEmbeddedAnimationStackIdentity
                        {
                            ClipId = Guid.NewGuid(),
                            FbxObjectId = 42,
                            StackFingerprint = new string('4', 64),
                            SourceRigSignature = signature,
                        },
                    FrameCount = 10,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = variantId,
                    SourceId = sourceId,
                    Name = "Walk",
                    TargetModelId = modelId,
                    TargetRigId = "target",
                    TargetRigSignature = signature,
                },
                new ProjectAnimationVariant
                {
                    Id = embeddedVariantId,
                    SourceId = embeddedSourceId,
                    Name = "Embedded",
                    TargetModelId = modelId,
                    TargetRigId = "target",
                    TargetRigSignature = signature,
                },
            ],
            Animations = [compatibility],
        };
        DlraProject edited = project with
        {
            Animations =
            [
                compatibility with
                {
                    RootMotionMode = Dl1RootMotionMode.InPlace,
                },
            ],
        };

        DlraProject synchronized = MainWindowViewModel
            .SynchronizeSchema2FromCompatibilityAnimations(
                project,
                edited);

        Assert.Equal(
            Dl1RootMotionMode.InPlace,
            synchronized.AnimationVariants.Single(variant =>
                variant.Id == variantId).RootMotionMode);
        Assert.Contains(
            synchronized.AnimationSources,
            source => source.Id == embeddedSourceId &&
                source.EmbeddedCustomModelStack is not null);
        Assert.Contains(
            synchronized.AnimationVariants,
            variant => variant.Id == embeddedVariantId);
    }

    [Fact]
    public void ExternalFbxStackProvenancePersistsObjectFingerprintAndExplicitUnit()
    {
        var stack = new FbxExternalAnimationStackDescriptor
        {
            StackObjectId = 17,
            Name = "GenericTake",
            LayerNames = ["BaseLayer"],
            SourceStartTick = -100,
            SourceStopTick = 200,
            Roles = AnimationSourceRoles.Body |
                AnimationSourceRoles.Facial,
            StackFingerprint = new string('b', 64),
        };

        string detail = MainWindowViewModel
            .CreateExternalFbxStackTimingDetail(
                stack,
                FbxFacialSourceValueUnit.Percent,
                usesSelectedModelRig: false);

        Assert.True(MainWindowViewModel
            .TryParseExternalFbxStackTimingDetail(
                detail,
                out long objectId,
                out string fingerprint,
                out FbxFacialSourceValueUnit unit,
                out bool selectedModelRig));
        Assert.Equal(17, objectId);
        Assert.Equal(stack.StackFingerprint, fingerprint);
        Assert.Equal(FbxFacialSourceValueUnit.Percent, unit);
        Assert.False(selectedModelRig);

        Guid sourceModelId = Guid.NewGuid();
        string facialOnlyDetail = MainWindowViewModel
            .CreateExternalFbxStackTimingDetail(
                stack,
                FbxFacialSourceValueUnit.Normalized,
                usesSelectedModelRig: true,
                selectedSourceModelId: sourceModelId);
        Assert.True(MainWindowViewModel
            .TryParseExternalFbxStackTimingDetail(
                facialOnlyDetail,
                out _,
                out _,
                out FbxFacialSourceValueUnit facialOnlyUnit,
                out bool facialOnlyUsesSelectedModel,
                out Guid? restoredSourceModelId));
        Assert.Equal(
            FbxFacialSourceValueUnit.Normalized,
            facialOnlyUnit);
        Assert.True(facialOnlyUsesSelectedModel);
        Assert.Equal(sourceModelId, restoredSourceModelId);
    }

    [Fact]
    public void DefaultDialogContractSelectsEveryImportableStackAsPercent()
    {
        IProjectFileDialogService dialogs =
            new DefaultDialogService();
        FbxExternalAnimationStackDescriptor[] rows =
        [
            new()
            {
                StackObjectId = 1,
                Name = "Body",
                LayerNames = ["Base"],
                Roles = AnimationSourceRoles.Body,
            },
            new()
            {
                StackObjectId = 2,
                Name = "Layered",
                LayerNames = ["A", "B"],
                Roles = AnimationSourceRoles.None,
                Diagnostics =
                [
                    new FbxExternalAnimationDiagnostic(
                        "layered_stack_requires_bake",
                        FbxExternalAnimationDiagnosticSeverity.Error,
                        "Bake or flatten the take."),
                ],
            },
        ];

        ExternalFbxAnimationStackSelection selection =
            Assert.IsType<ExternalFbxAnimationStackSelection>(
                dialogs.SelectExternalFbxAnimationStacks(
                    "generic.fbx",
                    rows,
                    [
                        new ExternalFbxTargetModelOption(
                            Guid.NewGuid(),
                            "Rigged",
                            "Project custom model",
                            "Rig ready",
                            IsStatic: false,
                            IsSelected: true),
                        new ExternalFbxTargetModelOption(
                            Guid.NewGuid(),
                            "Static",
                            "Base game reference",
                            "Static model",
                            IsStatic: true,
                            IsSelected: true),
                    ]));

        Assert.Equal(
            new long[] { 1 },
            selection.StackObjectIds.ToArray());
        Assert.Equal(
            FbxFacialSourceValueUnit.Percent,
            selection.FacialSourceValueUnit);
        Assert.Single(selection.TargetModelIds);
    }

    [Fact]
    public void ExternalFbxTargetChecklistKeepsStaticModelsVisibleButDisabled()
    {
        Guid modelId = Guid.NewGuid();
        var row = new ExternalFbxTargetSelectionRow(
            new ExternalFbxTargetModelOption(
                modelId,
                "Static prop",
                "Base game reference",
                "Static model - preview/export only",
                IsStatic: true,
                IsSelected: true));

        Assert.Equal(modelId, row.ModelId);
        Assert.False(row.CanTarget);
        Assert.False(row.IsSelected);

        row.IsSelected = true;

        Assert.False(row.IsSelected);
    }

    [Fact]
    public void ExternalFbxMultiTargetVariantsShareOneImmutableSource()
    {
        RigDefinition sourceRig = CreateRig(
            "source",
            ("root", -1));
        RigDefinition crossRig = CreateRig(
            "cross-target",
            ("root", -1),
            ("accessory", 0));
        var stack = new FbxExternalAnimationStackDescriptor
        {
            StackObjectId = 23,
            Name = "GenericTake",
            LayerNames = ["BaseLayer"],
            SourceStartTick = 0,
            SourceStopTick = 100,
            FrameCount = 2,
            Roles = AnimationSourceRoles.Body,
            StackFingerprint = new string('a', 64),
        };
        var take = new FbxExternalAnimationImportResult(
            stack,
            sourceRig,
            new AnimationClip(
                "GenericTake",
                new FrameRate(30, 1),
            frameCount: 2),
            Body: null,
            Facial: null,
            FacialSourceValueUnit:
                FbxFacialSourceValueUnit.Percent);
        var sourceAsset = new ProjectAssetReference
        {
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "assets/generic.fbx",
            ContentSha256 = new string('1', 64),
        };
        var directAsset = new ProjectAssetReference
        {
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "models/direct.dlrmodel",
            ContentSha256 = new string('2', 64),
        };
        var crossAsset = new ProjectAssetReference
        {
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = "models/cross.dlrmodel",
            ContentSha256 = new string('3', 64),
        };

        MainWindowViewModel.ExternalFbxTargetVariantBatch batch =
            MainWindowViewModel.CreateExternalFbxTargetVariants(
                take,
                sourceAsset,
                "assets/generic.fbx",
                [
                    new MainWindowViewModel.ExternalFbxVariantTarget(
                        directAsset,
                        sourceRig),
                    new MainWindowViewModel.ExternalFbxVariantTarget(
                        crossAsset,
                        crossRig),
                ]);

        Assert.Equal(2, batch.Animations.Length);
        Assert.Null(batch.Mappings[0]);
        Assert.NotNull(batch.Mappings[1]);
        Assert.All(batch.Animations, animation =>
        {
            Assert.Equal(sourceAsset.Id, animation.SourceAssetId);
            Assert.Equal(
                batch.Animations[0].VariantGroupId,
                animation.VariantGroupId);
        });
        Assert.Empty(batch.Animations[0].BoneMappings);
        Assert.Null(batch.Animations[0].MappingFingerprint);
        Assert.NotEmpty(batch.Animations[1].BoneMappings);
        Assert.NotNull(batch.Animations[1].MappingFingerprint);

        var directModel = new ProjectModelEntry
        {
            AssetId = directAsset.Id,
            Name = "Direct",
            RigSignature = RigSignature.Compute(sourceRig),
        };
        var crossModel = new ProjectModelEntry
        {
            AssetId = crossAsset.Id,
            Name = "Cross",
            RigSignature = RigSignature.Compute(crossRig),
        };
        DlraProject before = DlraProject.Create("Multi-target") with
        {
            Assets = [sourceAsset, directAsset, crossAsset],
            Models = [directModel, crossModel],
        };
        DlraProject synchronized = MainWindowViewModel
            .SynchronizeSchema2FromCompatibilityAnimations(
                before,
                before with
                {
                    Animations = batch.Animations,
                });

        Assert.Single(synchronized.AnimationSources);
        Assert.Equal(2, synchronized.AnimationVariants.Length);
        Assert.Collection(
            synchronized.AnimationVariants,
            variant => Assert.Equal(
                directModel.Id,
                variant.TargetModelId),
            variant => Assert.Equal(
                crossModel.Id,
                variant.TargetModelId));
        synchronized.Validate();
    }

    [Fact]
    public void MainWindowExposesModelFirstStagesAndArtifactReadiness()
    {
        string repository = LocateRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "ReAnimated.App",
            "MainWindow.xaml"));

        Assert.Contains("CommandParameter=\"Models\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"Animations\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"Playback\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"Retarget/Edit\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"Export\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"FPP camera\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ExportReadiness}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ExportModelSelections}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ExportCheckedPortableCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding DeployCheckedToDeveloperToolsCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding RollBackDeveloperToolsBatchCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedProjectModel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("DataContext=\"{Binding TargetViewport}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("active animation source and every target variant remain unchanged", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Face / FPP\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"Face\"", xaml, StringComparison.Ordinal);

        string stackDialog = File.ReadAllText(Path.Combine(
            repository,
            "src",
            "ReAnimated.App",
            "Infrastructure",
            "ExternalFbxStackSelectionDialog.xaml"));
        Assert.Contains("DeformPercent source unit", stackDialog, StringComparison.Ordinal);
        Assert.Contains("Diagnostics / bake guidance", stackDialog, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsSelected", stackDialog, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedVariantLibraryKeepsBodyAndFacialArtifactsFromOneVariant()
    {
        Guid variantId = Guid.Parse(
            "50000000-0000-0000-0000-000000000005");
        var variant = new ProjectAnimationVariant
        {
            Id = variantId,
            SourceId = Guid.Parse(
                "60000000-0000-0000-0000-000000000006"),
            TargetModelId = Guid.Parse(
                "70000000-0000-0000-0000-000000000007"),
            Name = "Combined",
            RootMotionMode = Dl1RootMotionMode.InPlace,
            RootBoneName = "Root",
        };
        Dl1PortableAnimationResource body = new()
        {
            VariantId = variantId,
            Name = "combined_body",
            Role = Dl1PortableAnimationRole.Body,
            Payload = [1],
            FrameCount = 2,
            FramesPerSecond = 30,
            SourceFingerprint = new string('e', 64),
        };
        Dl1PortableAnimationResource facial = body with
        {
            Name = "combined_mimic",
            Role = Dl1PortableAnimationRole.Facial,
        };

        PreparedCustomModelAnimationLibrary library =
            MainWindowViewModel.CreatePreparedTargetVariantLibrary(
                "CombinedLibrary",
                [body, facial],
                new Dictionary<Guid, ProjectAnimationVariant>
                {
                    [variantId] = variant,
                });

        Assert.Equal(2, library.Animations.Length);
        Assert.Equal(2, library.Sequences.Length);
        Assert.All(
            library.Animations,
            animation =>
            {
                Assert.Equal(Dl1RootMotionMode.InPlace,
                    animation.RootMotionMode);
                Assert.Equal("Root", animation.RootBoneName);
            });
        Assert.Contains("combined_body.anm2", library.LooseScriptText,
            StringComparison.Ordinal);
        Assert.Contains("combined_mimic.anm2", library.LooseScriptText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BlockedPackageVariantRemainsVisibleButCannotBeSelected()
    {
        var blocked = new ExportVariantSelectionViewModel(
            Guid.Parse("80000000-0000-0000-0000-000000000008"),
            "Needs review",
            "Bone review required",
            isEnabled: false,
            isSelected: true);
        var ready = new ExportVariantSelectionViewModel(
            Guid.Parse("90000000-0000-0000-0000-000000000009"),
            "Ready",
            "Ready - direct same-rig",
            isEnabled: true,
            isSelected: false);
        var model = new ExportModelSelectionViewModel(
            Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
            "Generic model",
            [blocked, ready]);

        Assert.False(blocked.IsSelected);
        blocked.IsSelected = true;
        Assert.False(blocked.IsSelected);
        model.IsSelected = true;
        Assert.False(blocked.IsSelected);
        Assert.True(ready.IsSelected);
        Assert.Contains(blocked, model.Variants);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel() =>
        new(new JsonWorkspaceStateStore(Path.Combine(
            _tempDirectory,
            $"recovery-{Guid.NewGuid():N}.json")));

    private static RigDefinition CreateRig(
        string id,
        params (string Name, int Parent)[] bones) =>
        new(
            id,
            id,
            bones.Select((bone, index) =>
                new BoneDefinition(
                    index,
                    bone.Name,
                    bone.Parent,
                    TransformTRS.Identity,
                    index == 0
                        ? BoneKind.Root
                        : BoneKind.Deform)));

    private sealed class DefaultDialogService :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) =>
            null;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
    }

    private static string LocateRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "DLReAnimated.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the DL ReAnimated repository root.");
    }
}

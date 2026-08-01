using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Tests;

public sealed class TransactionalPlaybackTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Transactional-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void CoordinatorRejectsStaleTargetAndFramePublications()
    {
        var coordinator = new EditorSessionCoordinator();
        Guid animation = Guid.NewGuid();
        TargetTransitionToken first =
            coordinator.BeginTargetTransition(animation, 12);
        TargetTransitionToken latest =
            coordinator.BeginTargetTransition(animation, 12);

        Assert.False(coordinator.TryCommitTargetTransition(
            first,
            animation,
            null,
            TargetBindingStatus.Direct));
        Assert.True(coordinator.TryCommitTargetTransition(
            latest,
            animation,
            null,
            TargetBindingStatus.Direct));

        PreviewPublicationToken accepted =
            coordinator.CreatePublicationToken(
                animation,
                new string('a', 64),
                new string('b', 64),
                null,
                12);
        Assert.True(coordinator.TryPublishFrame(
            new PreviewFramePair(accepted, 8, 8)));

        coordinator.Reset(animation, 13);
        Assert.False(coordinator.TryPublishFrame(
            new PreviewFramePair(accepted, 9, 9)));
        Assert.Null(coordinator.Current.LastPublishedFrame);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void CoordinatorDoesNotCommitWhenAtomicPublicationFails()
    {
        var coordinator = new EditorSessionCoordinator();
        Guid animation = Guid.NewGuid();
        TargetTransitionToken transition =
            coordinator.BeginTargetTransition(animation, 9);
        var binding = new EditorSessionBinding(
            new string('1', 64),
            new string('2', 64),
            new string('3', 64));

        Assert.Throws<InvalidOperationException>(() =>
            coordinator.TryCommitTargetTransition(
                transition,
                animation,
                new AnimationVariantKey(new string('4', 64)),
                TargetBindingStatus.Ready,
                static () => throw new InvalidOperationException(
                    "publication failed"),
                binding,
                isPlaying: true));

        Assert.True(coordinator.Current.IsTargetTransitioning);
        Assert.Null(coordinator.Current.ActiveVariant);
        Assert.Null(coordinator.Current.Binding);
        Assert.False(coordinator.Current.IsPlaying);
        Assert.True(coordinator.TryCancelTargetTransition(transition));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Renderer")]
    public async Task PairedViewportPublicationCannotBeCapturedHalfUpdated()
    {
        var coordinator = new LinkedViewportCoordinator();
        var source = new RenderSceneBuffer();
        var target = new RenderSceneBuffer();
        source.SetScene([], null, [], generation: 1);
        target.SetScene([], null, [], generation: 1);
        using var sourcePublished = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var captureStarted = new ManualResetEventSlim();

        Task publication = Task.Run(() =>
            coordinator.PublishScenePair(() =>
            {
                source.SetScene([], null, [], generation: 2);
                sourcePublished.Set();
                releasePublication.Wait();
                target.SetScene([], null, [], generation: 2);
            }));
        Assert.True(sourcePublished.Wait(TimeSpan.FromSeconds(5)));
        Task<(RenderFrameSnapshot Source, RenderFrameSnapshot Target)>
            capture = Task.Run(() =>
            {
                captureStarted.Set();
                return (
                    coordinator.CaptureScene(
                        ViewportSide.Source,
                        source),
                    coordinator.CaptureScene(
                        ViewportSide.Target,
                        target));
            });
        Assert.True(captureStarted.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.False(capture.IsCompleted);
        }
        finally
        {
            releasePublication.Set();
        }

        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        (RenderFrameSnapshot sourceFrame,
            RenderFrameSnapshot targetFrame) =
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sourceFrame.Generation);
        Assert.Equal(2, targetFrame.Generation);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Project")]
    public void VariantKeyUsesExactSourcePartitionAndTargetFingerprint()
    {
        (DlraProject project, ProjectAnimation animation,
            ProjectAssetReference _, ProjectAssetReference target) =
            CreatePoisonedProject();
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets =
            project.Assets.ToDictionary(static asset => asset.Id);
        AnimationVariantKey first = AnimationVariantKey.Create(
            animation,
            assets);
        ProjectAssetReference changedTarget = target with
        {
            Id = Guid.NewGuid(),
            ContentSha256 = new string('e', 64),
            RetailIdentity = target.RetailIdentity! with
            {
                ContentSha256 = new string('e', 64),
            },
        };
        var changedAssets = project.Assets
            .Add(changedTarget)
            .ToDictionary(static asset => asset.Id);
        AnimationVariantKey second = AnimationVariantKey.Create(
            animation with
            {
                TargetAssetId = changedTarget.Id,
            },
            changedAssets);

        Assert.NotEqual(first, second);
        Assert.Equal(
            AnimationVariantKey.CreateGroupId(animation, assets),
            AnimationVariantKey.CreateGroupId(
                animation with
                {
                    TargetAssetId = changedTarget.Id,
                },
                changedAssets));

        ProjectAnimation duplicateA = animation with
        {
            VariantGroupId = Guid.NewGuid(),
        };
        ProjectAnimation duplicateB = animation with
        {
            VariantGroupId = Guid.NewGuid(),
        };
        Assert.NotEqual(
            AnimationVariantKey.Create(duplicateA, assets),
            AnimationVariantKey.Create(duplicateB, assets));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Project")]
    public void CleanTargetVariantRetainsSourceAndTimingButClearsTargetLayers()
    {
        RigDefinition sourceRig = CreateRig("source", "root", "hand");
        RigDefinition targetRig = CreateRig("target", "pelvis", "claw");
        ProjectAssetReference target = CreateRetailAsset(
            Guid.NewGuid(),
            "armored",
            new string('d', 64));
        var source = new ProjectAnimation
        {
            Id = Guid.NewGuid(),
            Name = "clip",
            SourceAssetId = Guid.NewGuid(),
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalFbx,
                AssetId = Guid.NewGuid(),
                Roles = AnimationSourceRoles.Body |
                    AnimationSourceRoles.Facial,
                SourceRigSignature = RigSignature.Compute(sourceRig),
                TimingProvenance =
                    AnimationTimingProvenance.EmbeddedFbx,
            },
            SourceRigSignature = RigSignature.Compute(sourceRig),
            TargetRigId = sourceRig.Id,
            TargetRigSignature = RigSignature.Compute(sourceRig),
            FrameRate = new FrameRate(60, 1),
            FrameCount = 90,
            EditLayers = [CreateEditLayer()],
            MorphBindings = [CreateMorphBinding()],
            MorphEditLayers = [CreateMorphLayer()],
            IkLayers = [CreateIkLayer()],
            Attachments = [CreateAttachment()],
        };
        RetargetMap proposal = RetargetMapBuilder.CreateSuggested(
            sourceRig,
            targetRig);
        Guid group = Guid.NewGuid();

        ProjectAnimation variant =
            MainWindowViewModel.CreateCleanTargetVariant(
                source,
                group,
                target,
                targetRig,
                sourceRig,
                proposal);

        Assert.NotEqual(source.Id, variant.Id);
        Assert.Equal(group, variant.VariantGroupId);
        Assert.Equal(source.SourceBinding, variant.SourceBinding);
        Assert.Equal(source.FrameRate, variant.FrameRate);
        Assert.Equal(source.FrameCount, variant.FrameCount);
        Assert.Equal(target.Id, variant.TargetAssetId);
        Assert.NotEmpty(variant.BoneMappings);
        Assert.Empty(variant.TargetBindReviews);
        Assert.Empty(variant.EditLayers);
        Assert.Empty(variant.MorphBindings);
        Assert.Empty(variant.MorphEditLayers);
        Assert.Empty(variant.IkLayers);
        Assert.Empty(variant.Attachments);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Project")]
    public void RecoveryRetainsPoisonedVariantAndActivatesSafeDirectSibling()
    {
        (DlraProject project, ProjectAnimation poisoned,
            ProjectAssetReference sourceModel, _) =
            CreatePoisonedProject();

        ProjectVariantRecoveryNormalizationResult result =
            ProjectVariantRecoveryNormalizer.Normalize(project);

        ProjectVariantRecoveryRepair repair =
            Assert.Single(result.Repairs);
        Assert.Equal(poisoned.Id, repair.RetainedVariantId);
        Assert.Contains(
            result.Project.Animations,
            candidate => candidate.Id == poisoned.Id &&
                candidate.TargetAssetId == poisoned.TargetAssetId);
        ProjectAnimation safe = Assert.Single(
            result.Project.Animations,
            candidate => candidate.Id == repair.SafeVariantId);
        Assert.Equal(sourceModel.Id, safe.TargetAssetId);
        Assert.Equal(safe.SourceRigSignature, safe.TargetRigSignature);
        Assert.Empty(safe.BoneMappings);
        Assert.Empty(safe.EditLayers);
        Assert.Empty(safe.MorphBindings);
        Assert.Empty(safe.IkLayers);
        Assert.Empty(safe.Attachments);
        Assert.Equal(safe.Id, result.Project.ActiveAnimationId);
        Assert.Equal(
            EditorWorkspaceMode.Animate,
            MainWindowViewModel.ResolveStartupWorkspace(result.Project));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task AssetSelectionAloneChangesNoProjectOrPlaybackState()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        SetWorkspaceInstall(assets, @"C:\retail");
        var decoder = new ControlledMeshDecodeService("armored");
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "workspace.json")),
            new NoOpDialogs(),
            assets,
            new NullFingerprintService(),
            retailMeshDecodeService: decoder);
        viewModel.Timeline.CurrentFrame = 17;
        viewModel.Timeline.IsPlaying = true;
        WorkspaceSnapshot before = viewModel.CreateSnapshot();
        AssetItemViewModel row = CreateMeshRow("armored");
        viewModel.AssetBrowser.ReplaceAssets([row]);

        viewModel.AssetBrowser.SelectedAsset = row;
        decoder.Complete(
            "armored",
            CreatePreviewableMeshPayload(
                "armored",
                new string('c', 64),
                new Vector3(12, 0, 0)));
        await WaitUntilAsync(
            () => viewModel.TargetViewport.SceneSource
                .HasExternalPreviewScene,
            () =>
                $"Status: {viewModel.StatusText}; diagnostics: {string.Join(" | ", viewModel.Diagnostics.Select(static row => $"{row.Message}: {row.Detail}"))}");

        WorkspaceSnapshot after = viewModel.CreateSnapshot();
        Assert.Equal(before.Project, after.Project);
        Assert.Equal(17, viewModel.Timeline.CurrentFrame);
        Assert.True(viewModel.Timeline.IsPlaying);
        Assert.Equal("No target model", viewModel.ActiveTargetModelLabel);
        Assert.Contains("Previewing armored", viewModel.StatusText);
        Assert.Equal(
            "armored/preview",
            Assert.Single(
                    viewModel.TargetViewport.SceneSource
                        .CaptureFrame()
                        .Meshes)
                .Id);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task BrowsePreviewSceneAndCameraDoNotReplaceAnimateState()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "browse-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "browse-cache"));
        SetWorkspaceInstall(assets, @"C:\retail");
        var decoder = new ControlledMeshDecodeService("armored");
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(
                    _temporaryDirectory,
                    "browse-workspace.json")),
            new NoOpDialogs(),
            assets,
            new NullFingerprintService(),
            retailMeshDecodeService: decoder);
        MeshRenderData authoritative = CreateTriangleMesh(
            "authoritative-target",
            Vector3.Zero);
        viewModel.SetTargetPreviewScene([authoritative], null);
        RenderCamera authoringCamera = viewModel.TargetViewport
            .SceneSource.CaptureFrame().Camera;
        AssetItemViewModel row = CreateMeshRow("armored");
        viewModel.AssetBrowser.ReplaceAssets([row]);
        viewModel.AssetBrowser.SelectedAsset = row;

        Task preview = viewModel.PreviewSelectedAssetCommand
            .ExecuteAsync(null);
        decoder.Complete(
            "armored",
            CreatePreviewableMeshPayload(
                "armored",
                new string('d', 64),
                new Vector3(25, 0, 0)));
        await preview;

        Assert.True(
            viewModel.TargetViewport.SceneSource
                .HasExternalPreviewScene,
            $"Status: {viewModel.StatusText}; diagnostics: {string.Join(" | ", viewModel.Diagnostics.Select(static row => $"{row.Message}: {row.Detail}"))}");
        RenderFrameSnapshot browse = viewModel.TargetViewport.SceneSource
            .CaptureFrame();
        Assert.Equal(
            "armored/preview",
            Assert.Single(browse.Meshes).Id);
        Assert.True(browse.Camera.Target.X > 20.0f);
        RenderCamera browseCamera = browse.Camera;

        viewModel.ActiveWorkspaceMode = "Animate";

        Assert.False(viewModel.TargetViewport.SceneSource
            .HasExternalPreviewScene);
        RenderFrameSnapshot animate = viewModel.TargetViewport.SceneSource
            .CaptureFrame();
        Assert.Equal(
            authoritative.Id,
            Assert.Single(animate.Meshes).Id);
        Assert.Equal(authoringCamera, animate.Camera);

        viewModel.ActiveWorkspaceMode = "Browse";

        Assert.True(viewModel.TargetViewport.SceneSource
            .HasExternalPreviewScene);
        RenderFrameSnapshot restored = viewModel.TargetViewport.SceneSource
            .CaptureFrame();
        Assert.Equal(
            "armored/preview",
            Assert.Single(restored.Meshes).Id);
        Assert.Equal(browseCamera, restored.Camera);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task RapidTargetSwitchPublishesOnlyLatestCompletedRequest()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "rapid-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "rapid-cache"));
        SetWorkspaceInstall(assets, @"C:\retail");
        var decoder = new ControlledMeshDecodeService(
            "armored",
            "player_11_tpp");
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "rapid-workspace.json")),
            new NoOpDialogs(),
            assets,
            new NullFingerprintService(),
            retailMeshDecodeService: decoder);
        AssetItemViewModel armored = CreateMeshRow("armored");
        AssetItemViewModel player = CreateMeshRow("player_11_tpp");
        viewModel.AssetBrowser.ReplaceAssets([armored, player]);
        WorkspaceSnapshot before = viewModel.CreateSnapshot();

        viewModel.AssetBrowser.SelectedAsset = armored;
        Assert.True(
            viewModel.UseSelectedAssetAsTargetCommand.CanExecute(null));
        Task first = viewModel.UseSelectedAssetAsTargetCommand
            .ExecuteAsync(null);

        viewModel.AssetBrowser.SelectedAsset = player;
        Assert.True(
            viewModel.UseSelectedAssetAsTargetCommand.CanExecute(null));
        Task latest = viewModel.UseSelectedAssetAsTargetCommand
            .ExecuteAsync(null);

        decoder.Complete(
            "player_11_tpp",
            CreateMeshPayload(
                "player_11_tpp",
                new string('b', 64)));
        await latest.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("player_11_tpp", viewModel.ActiveTargetModelLabel);

        decoder.Complete(
            "armored",
            CreateMeshPayload(
                "armored",
                new string('c', 64)));
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("player_11_tpp", viewModel.ActiveTargetModelLabel);
        Assert.Equal(before.Project, viewModel.CreateSnapshot().Project);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.IsTargetSwitching);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public async Task GuidedDrawersRemainMutuallyExclusive()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "drawer-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "drawer-cache"));
        await using var viewModel = new MainWindowViewModel(
            new JsonWorkspaceStateStore(
                Path.Combine(_temporaryDirectory, "drawer-workspace.json")),
            new NoOpDialogs(),
            assets);

        viewModel.SelectWorkspaceCommand.Execute("Retarget/Edit");
        Assert.True(viewModel.IsInspectorPanelVisible);
        viewModel.IsDiagnosticsDrawerOpen = true;
        Assert.False(viewModel.IsInspectorPanelVisible);
        viewModel.IsDiagnosticsDrawerOpen = false;
        Assert.True(viewModel.IsInspectorPanelVisible);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "Recovery")]
    public void RecoveryBackupPreservesExactOriginalBytes()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "Recovery",
            "workspace.json");
        var store = new JsonWorkspaceStateStore(path);
        WorkspaceSnapshot snapshot = new(
            WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null,
            string.Empty,
            null,
            null,
            0,
            true,
            70,
            0.03f,
            "Browse");
        store.Save(snapshot);
        string original = File.ReadAllText(path);

        string backup = store.BackupCurrent();

        Assert.NotEqual(path, backup);
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.StartsWith(
            Path.Combine(_temporaryDirectory, "Recovery", "Backups"),
            backup,
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private static (DlraProject Project, ProjectAnimation Animation,
        ProjectAssetReference SourceModel,
        ProjectAssetReference Target) CreatePoisonedProject()
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid sourceModelId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        Guid propId = Guid.NewGuid();
        ProjectAssetReference sourceAsset = new()
        {
            Id = sourceAssetId,
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "sources/clip.anm2",
            ContentSha256 = new string('1', 64),
        };
        ProjectAssetReference sourceModel = CreateRetailAsset(
            sourceModelId,
            "player_11_tpp",
            new string('2', 64));
        ProjectAssetReference target = CreateRetailAsset(
            targetId,
            "armored",
            new string('3', 64));
        ProjectAssetReference prop = CreateRetailAsset(
            propId,
            "pipe",
            new string('6', 64));
        var partition = new Anm2TrackPartition
        {
            BodyDescriptors = [0x12345678],
            Fingerprint = new string('4', 64),
        };
        Guid animationId = Guid.NewGuid();
        var animation = new ProjectAnimation
        {
            Id = animationId,
            Name = "dncrs_0001_3dc_3p",
            SourceAssetId = sourceAssetId,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalAnm2,
                AssetId = sourceAssetId,
                Roles = AnimationSourceRoles.Body,
                SourceRigSignature = new string('a', 64),
                RetailSourceModelAssetId = sourceModelId,
                TimingProvenance =
                    AnimationTimingProvenance.Manual30FpsFallback,
                Partition = partition,
            },
            TargetAssetId = targetId,
            TargetRigId = "dl1:armored",
            SourceRigSignature = new string('a', 64),
            TargetRigSignature = new string('b', 64),
            MappingFingerprint = new string('5', 64),
            FrameRate = new FrameRate(30, 1),
            FrameCount = 120,
            BoneMappings =
            [
                new ProjectBoneMapping
                {
                    SourceBoneName = "bip01",
                    TargetBoneName = "bip01",
                    Method = BoneMappingMethod.Structural.ToString(),
                    IsReviewed = false,
                },
            ],
            EditLayers = [CreateEditLayer()],
            MorphBindings = [CreateMorphBinding()],
            MorphEditLayers = [CreateMorphLayer()],
            IkLayers = [CreateIkLayer()],
            Attachments = [CreateAttachment(propId)],
        };
        DlraProject project = DlraProject.Create("recovery") with
        {
            Assets = [sourceAsset, sourceModel, target, prop],
            Animations = [animation],
            ActiveAnimationId = animationId,
        };
        project.Validate();
        return (project, animation, sourceModel, target);
    }

    private static ProjectAssetReference CreateRetailAsset(
        Guid id,
        string name,
        string hash) => new()
    {
        Id = id,
        Kind = ProjectAssetKind.RetailGameResource,
        RelativePath = $"retail/{Rp6lResourceTypes.Mesh}/{id:N}",
        ResourceId = name,
        ContentSha256 = hash,
        RetailIdentity = new ProjectRetailAssetIdentity
        {
            InstallFingerprint = "test-install",
            ProviderId = "dl1-rpacks",
            ProviderPack = "DW/Data0.pak",
            ResourceType = Rp6lResourceTypes.Mesh,
            ResourceIndex = Math.Abs(id.GetHashCode()),
            ResourceName = name,
            Precedence = 100,
            ContentSha256 = hash,
        },
    };

    private static RigDefinition CreateRig(
        string id,
        string root,
        string child) => new(
        id,
        id,
        [
            new BoneDefinition(
                0,
                root,
                -1,
                TransformTRS.Identity,
                BoneKind.Root),
            new BoneDefinition(
                1,
                child,
                0,
                new TransformTRS(
                    new Vector3D(0, 1, 0),
                    QuaternionD.Identity,
                    Vector3D.One)),
        ]);

    private static BoneEditLayer CreateEditLayer() => new(
        Guid.NewGuid(),
        "edit",
        BoneEditBlendMode.Override,
        BoneEditLayerScope.AuthoredExportable,
        1,
        [
            new BoneEditTrack(
                0,
                [new TransformKeyframe(0, TransformTRS.Identity)]),
        ]);

    private static ProjectMorphBinding CreateMorphBinding() => new()
    {
        SourceChannel = "smile",
        TargetMorph = "smile",
        Method = "manual",
    };

    private static MorphEditLayer CreateMorphLayer() => new(
        Guid.NewGuid(),
        "face",
        MorphEditBlendMode.Override,
        MorphEditLayerScope.AuthoredExportable,
        1,
        [
            new MorphEditTrack(
                "smile",
                [new ScalarKeyframe(0, 0.5)]),
        ]);

    private static ProjectIkLayer CreateIkLayer() => new()
    {
        Id = Guid.NewGuid(),
        Name = "hand",
        ChainName = "hand",
        Keyframes =
        [
            new ProjectIkKeyframe
            {
                Frame = 0,
                Effector = Vector3D.Zero,
                Pole = Vector3D.UnitZ,
            },
        ],
    };

    private static AttachmentBinding CreateAttachment(
        Guid? assetId = null) => new(
        Guid.NewGuid(),
        assetId ?? Guid.NewGuid(),
        "prop",
        0,
        TransformTRS.Identity,
        AttachmentScope.AuthoredExportable,
        "bip01");

    private static AssetItemViewModel CreateMeshRow(string name)
    {
        RetailAssetId id = RetailAssetId.Create(
            RetailAssetLogicalId.Rpack(
                Rp6lResourceTypes.Mesh,
                name),
            "test-install",
            "dl1-rpacks",
            1,
            100,
            new string('a', 64));
        var record = new RetailAssetRecord(
            id,
            name,
            new RetailAssetSource(
                "dl1-rpacks",
                RetailAssetSourceKind.Rpack,
                100,
                @"C:\retail\common.mesh.rpack",
                name,
                1,
                0,
                128,
                DateTime.UtcNow));
        return new AssetItemViewModel(
            id.StableKey,
            name,
            AssetKind.Mesh,
            "dl1-rpacks",
            id.LogicalId.StableKey,
            record);
    }

    private static Dl1MeshPreviewPayload CreateMeshPayload(
        string name,
        string contentSha256)
    {
        RigDefinition rig = CreateRig(
            $"dl1:{name}",
            "bip01",
            "pelvis");
        var source = new Dl1MeshData(
            name,
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            new CompactMeshDocument(0, 0, 0, [], []),
            rig,
            [],
            [],
            [],
            [],
            [],
            []);
        return new Dl1MeshPreviewPayload(
            source,
            [],
            null,
            [],
            [],
            contentSha256);
    }

    private static Dl1MeshPreviewPayload CreatePreviewableMeshPayload(
        string name,
        string contentSha256,
        Vector3 offset)
    {
        Dl1MeshPreviewPayload empty = CreateMeshPayload(
            name,
            contentSha256);
        return empty with
        {
            Meshes = [CreateTriangleMesh($"{name}/preview", offset)],
        };
    }

    private static MeshRenderData CreateTriangleMesh(
        string id,
        Vector3 offset) => new(
        id,
        new MeshVertex[]
        {
            new(
                new Vector3(-0.5f, 0, 0),
                Vector3.UnitZ,
                Vector2.Zero,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0.5f, 0, 0),
                Vector3.UnitZ,
                Vector2.UnitX,
                Vector4.UnitX,
                Vector4.Zero),
            new(
                new Vector3(0, 1, 0),
                Vector3.UnitZ,
                Vector2.UnitY,
                Vector4.UnitX,
                Vector4.Zero),
        },
        new uint[] { 0, 1, 2 },
        Matrix4x4.CreateTranslation(offset),
        Array.Empty<Matrix4x4>(),
        IsSkinned: false);

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        Func<string>? describeFailure = null)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "The automatic Browse preview did not publish in time. " +
                    (describeFailure?.Invoke() ?? string.Empty));
            }

            await Task.Delay(20);
        }
    }

    private static void SetWorkspaceInstall(
        Dl1AssetWorkspace workspace,
        string installPath)
    {
        FieldInfo field = typeof(Dl1AssetWorkspace).GetField(
            "_install",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Dl1AssetWorkspace install field was not found.");
        field.SetValue(
            workspace,
            new Dl1InstallLocation(
                installPath,
                installPath,
                "Focused transactional test",
                true,
                null));
    }

    private sealed class ControlledMeshDecodeService(
        params string[] assetNames) : IRetailMeshDecodeService
    {
        private readonly Dictionary<string,
            TaskCompletionSource<Dl1MeshPreviewPayload>> _pending =
            assetNames.ToDictionary(
                static name => name,
                static _ => new TaskCompletionSource<Dl1MeshPreviewPayload>(
                    TaskCreationOptions.RunContinuationsAsynchronously),
                StringComparer.OrdinalIgnoreCase);

        public Task<Dl1MeshPreviewPayload> DecodeAsync(
            RetailAssetRecord asset,
            CancellationToken cancellationToken) =>
            _pending[asset.DisplayName].Task;

        public void Complete(
            string assetName,
            Dl1MeshPreviewPayload payload) =>
            _pending[assetName].SetResult(payload);
    }

    private sealed class NullFingerprintService :
        IDl1InstalledBuildFingerprintService
    {
        public Task<Dl1InstalledBuildFingerprint?> TryReadDiscoveredAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Dl1InstalledBuildFingerprint?>(null);

        public Task<Dl1InstalledBuildFingerprint> ReadAsync(
            string installPath,
            CancellationToken cancellationToken = default) =>
            Task.FromException<Dl1InstalledBuildFingerprint>(
                new FileNotFoundException());
    }

    private sealed class NoOpDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? currentPath) => null;
        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) => null;
        public string? ShowOpenAnimationDialog(string? currentPath) => null;
        public string? ShowOpenMimicAnimationDialog(string? currentPath) => null;
        public string? ShowOpenFacialFbxDialog(string? currentPath) => null;
        public string? ShowOpenFedDialog(string? currentPath) => null;
        public string? ShowSelectExportDirectoryDialog(string? currentPath) => null;
        public string? ShowSelectAdditionalRpackRootDialog(string? projectDirectory) => null;
    }
}

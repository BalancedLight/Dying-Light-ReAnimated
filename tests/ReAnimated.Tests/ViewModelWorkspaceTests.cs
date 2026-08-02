using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class ViewModelWorkspaceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), $"ReAnimated-ViewModelTests-{Guid.NewGuid():N}");

    [Fact]
    public void AssetBrowserDescribesAutomaticCachedCatalogLoadingClearly()
    {
        var browser = new AssetBrowserViewModel();

        Assert.Equal("Load catalog", browser.CatalogActionLabel);
        Assert.Contains(
            "saved asset catalog",
            browser.EmptyResultMessage,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Index the Dying Light",
            browser.EmptyResultMessage,
            StringComparison.OrdinalIgnoreCase);

        browser.SetCatalogLoading(true);

        Assert.True(browser.IsCatalogLoading);
        Assert.Equal("Loading...", browser.CatalogActionLabel);
        Assert.Equal("Loading saved catalog", browser.ResultSummary);
        Assert.Contains(
            "Loading the saved Dying Light 1 asset catalog",
            browser.EmptyResultMessage,
            StringComparison.Ordinal);
        Assert.False(browser.IndexGameCommand.CanExecute(null));

        browser.SetCatalogLoading(false);

        Assert.True(browser.IndexGameCommand.CanExecute(null));
    }

    [Fact]
    public void WorkspaceSnapshotRoundTripsEditorState()
    {
        JsonWorkspaceStateStore store = CreateStore();
        MainWindowViewModel first = new(store)
        {
            ProjectPath = @"C:\Projects\volatile-test.reanimated",
            IsViewportsLinked = false,
            ActiveWorkspaceMode = "FPP",
            ShowMeshes = false,
            ShowSkeletonOverlay = false,
        };
        first.AssetBrowser.SearchText = "volatile";
        first.Timeline.CurrentFrame = 42;
        first.FacialFpp.FieldOfView = 71.0f;
        first.FacialFpp.NearPlane = 0.035f;

        WorkspaceSnapshot snapshot = first.CreateSnapshot();
        MainWindowViewModel second = new(store);
        second.RestoreSnapshot(snapshot);

        Assert.Equal(first.ProjectPath, second.ProjectPath);
        Assert.Equal("volatile", second.AssetBrowser.SearchText);
        Assert.Equal(42, second.Timeline.CurrentFrame);
        Assert.False(second.IsViewportsLinked);
        Assert.Equal(71.0f, second.FacialFpp.FieldOfView);
        Assert.Equal(0.035f, second.FacialFpp.NearPlane);
        Assert.Equal("FPP", second.ActiveWorkspaceMode);
        Assert.False(second.ShowMeshes);
        Assert.False(second.ShowSkeletonOverlay);
        Assert.Contains(
            "FPP profile is active",
            second.FacialFpp.PreviewStatus,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewportInspectionBackgroundsRemainReadableAndDistinct()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "visibility-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "visibility-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(_temporaryDirectory, "unused-visibility.dlraproj")),
            assets);

        Vector4 source =
            viewModel.SourceViewport.SceneSource.CaptureFrame().ClearColor;
        Vector4 target =
            viewModel.TargetViewport.SceneSource.CaptureFrame().ClearColor;

        Assert.True(
            (source.X + source.Y + source.Z) / 3.0f >= 0.14f,
            $"Source viewport background is too dark for mesh inspection: {source}.");
        Assert.True(
            (target.X + target.Y + target.Z) / 3.0f >= 0.14f,
            $"Target viewport background is too dark for mesh inspection: {target}.");
        Assert.NotEqual(source, target);
    }

    [Fact]
    public async Task RetailFloatBindMatricesRemainSkinnableAfterEditorRefresh()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "retail-bind-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "retail-bind-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-retail-bind.dlraproj")),
            assets);
        Matrix4x4 retailLocal = Matrix4x4.Identity;
        retailLocal.M21 = 5.0e-6f;
        var skeleton = new SkeletonRenderData(
            [
                new BoneRenderData(
                    "Bip01",
                    -1,
                    retailLocal,
                    retailLocal,
                    false),
            ],
            Matrix4x4.Identity);
        var mesh = new MeshRenderData(
            "player_1_tpp/beard/lod0/part0",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.UnitX,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.UnitY,
                    Vector4.UnitX,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            IsSkinned: true);
        viewModel.SetSourcePreviewScene([mesh], skeleton);
        viewModel.SetTargetPreviewScene([mesh], skeleton);
        viewModel.SkeletonRoots.Add(
            new SkeletonNodeViewModel(
                "Bip01",
                "Bip01",
                0,
                -1,
                retailLocal,
                retailLocal));

        viewModel.RefreshEditableSkeletonPreview();

        RenderFrameSnapshot source =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot target =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.NotNull(source.Skeleton);
        Assert.NotNull(target.Skeleton);
        Assert.True(
            RenderMeshValidation.TryValidate(
                Assert.Single(source.Meshes),
                source.Skeleton,
                out string? sourceError),
            sourceError);
        Assert.True(
            RenderMeshValidation.TryValidate(
                Assert.Single(target.Meshes),
                target.Skeleton,
                out string? targetError),
            targetError);
        Assert.DoesNotContain(
            viewModel.Diagnostics,
            static diagnostic =>
                diagnostic.Message.Contains(
                    "fallback skeleton",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RawSourcePaneDoesNotBorrowTheTargetMesh()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "authored-target-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "authored-target-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-authored-target.dlraproj")),
            assets);
        RigDefinition targetRig = new(
            "dl1:authored-target",
            "Authored target",
            [
                new BoneDefinition(
                    0,
                    "bip01",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ]);
        SkeletonPose targetPose =
            targetRig.CreateBindPose();
        MeshRenderData targetMesh = new(
            "player_1_tpp/body/lod0/part0",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.UnitX,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.UnitY,
                    Vector4.UnitX,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            IsSkinned: true)
        {
            SkinBoneIndices = new int[] { 0 },
        };
        viewModel.SetTargetPreviewScene(
            [targetMesh],
            CorePreviewAdapter.ToRenderSkeleton(
                targetPose));
        EvaluationFrame frame = new(
            0.0,
            targetPose,
            targetPose,
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            PreviewProfile.RawAuthoring,
            null,
            [],
            [],
            null,
            []);

        viewModel.PublishAuthoredSourcePreview(frame);

        RenderFrameSnapshot authored =
            viewModel.SourceViewport.SceneSource
                .CaptureFrame();
        Assert.Empty(authored.Meshes);
        Assert.Equal(
            targetRig.BoneCount,
            Assert.IsType<SkeletonRenderData>(
                    authored.Skeleton)
                .Bones.Count);
        RenderFrameSnapshot target =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.True(
            RenderMeshValidation.TryValidate(
                Assert.Single(target.Meshes),
                target.Skeleton,
                out string? validationError),
            validationError);
        Assert.Equal(
            "Raw Source",
            viewModel.SourceViewport.Title);
        Assert.Contains(
            "skeleton-only exact decoded pose",
            viewModel.SourceViewport.FidelityLabel,
            StringComparison.Ordinal);

        viewModel.ShowMeshes = false;
        Assert.Empty(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Meshes);
        Assert.Empty(
            viewModel.TargetViewport.SceneSource
                .CaptureFrame()
                .Meshes);
        viewModel.ShowSkeletonOverlay = false;
        SkeletonRenderData hiddenSkeleton =
            Assert.IsType<SkeletonRenderData>(
                viewModel.SourceViewport.SceneSource
                    .CaptureFrame()
                    .Skeleton);
        Assert.False(hiddenSkeleton.ShowDeformBones);
        Assert.False(hiddenSkeleton.ShowHelpers);
        Assert.False(hiddenSkeleton.ShowCameraHelpers);
        Assert.False(hiddenSkeleton.ShowProps);

        viewModel.ShowMeshes = true;
        viewModel.ShowSkeletonOverlay = true;
        Assert.Empty(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Meshes);
        Assert.Equal(
            targetMesh.Id,
            Assert.Single(
                    viewModel.TargetViewport.SceneSource
                        .CaptureFrame()
                        .Meshes)
                .Id);
        Assert.True(
            Assert.IsType<SkeletonRenderData>(
                    viewModel.SourceViewport.SceneSource
                        .CaptureFrame()
                        .Skeleton)
                .ShowDeformBones);
    }

    [Fact]
    public async Task UneditableRetailHelperKeepsRawSkinnedPreviewAvailable()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "raw-bind-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "raw-bind-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-raw-bind.dlraproj")),
            assets);
        Matrix4x4 nonTrsHelper = Matrix4x4.Identity;
        nonTrsHelper.M21 = 0.02f;
        var skeleton = new SkeletonRenderData(
            [
                new BoneRenderData(
                    "weighted_bone",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false),
                new BoneRenderData(
                    "unweighted_helper",
                    0,
                    nonTrsHelper,
                    nonTrsHelper,
                    false)
                {
                    Role = BoneRenderRole.Helper,
                },
            ],
            Matrix4x4.Identity);
        var mesh = new MeshRenderData(
            "bind-pose-only",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.UnitX,
                    Vector4.UnitX,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.UnitY,
                    Vector4.UnitX,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            IsSkinned: true)
        {
            SkinBoneIndices = new int[] { 0 },
        };
        viewModel.SetSourcePreviewScene([mesh], skeleton);
        viewModel.SetTargetPreviewScene([mesh], skeleton);
        var root = new SkeletonNodeViewModel(
            "weighted_bone",
            "weighted_bone",
            0,
            -1);
        root.Children.Add(
            new SkeletonNodeViewModel(
                "unweighted_helper",
                "weighted_bone/unweighted_helper",
                1,
                0,
                nonTrsHelper,
                nonTrsHelper,
                BoneRenderRole.Helper));
        viewModel.SkeletonRoots.Add(root);

        viewModel.RefreshEditableSkeletonPreview();

        RenderFrameSnapshot source =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot target =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.Equal(2, Assert.IsType<SkeletonRenderData>(
            source.Skeleton).Bones.Count);
        Assert.Equal(2, Assert.IsType<SkeletonRenderData>(
            target.Skeleton).Bones.Count);
        Assert.True(
            RenderMeshValidation.TryValidate(
                Assert.Single(source.Meshes),
                source.Skeleton,
                out string? sourceError),
            sourceError);
        Assert.True(
            RenderMeshValidation.TryValidate(
                Assert.Single(target.Meshes),
                target.Skeleton,
                out string? targetError),
            targetError);
        Assert.Contains(
            viewModel.Diagnostics,
            static diagnostic =>
                diagnostic.Message.Contains(
                    "fallback skeleton",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ViewportAuthoringOverlayControlsPublishStableSceneState()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "overlay-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "overlay-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-overlays.dlraproj")),
            assets);
        MeshRenderData mesh = new(
            "selected-retail-model",
            new MeshVertex[]
            {
                new(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    Vector2.UnitX,
                    Vector4.Zero,
                    Vector4.Zero),
                new(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    Vector2.UnitY,
                    Vector4.Zero,
                    Vector4.Zero),
            },
            new uint[] { 0, 1, 2 },
            Matrix4x4.Identity,
            ReadOnlyMemory<Matrix4x4>.Empty,
            false);

        viewModel.SetTargetPreviewScene([mesh], null);
        viewModel.ShowBoneLocalAxes = true;
        viewModel.ShowDeformedBounds = true;
        viewModel.HighlightSelectedMeshes = true;
        viewModel.ShowRootMotionTrail = true;

        RenderFrameSnapshot source =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot target =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.False(
            source.AuthoringOverlays.Options.ShowRootMotionTrail);
        Assert.True(
            target.AuthoringOverlays.Options.ShowRootMotionTrail);
        Assert.True(
            target.AuthoringOverlays.Options.ShowBoneLocalAxes);
        Assert.True(
            target.AuthoringOverlays.Options.ShowDeformedBounds);
        Assert.True(
            target.AuthoringOverlays.Options.HighlightSelectedMeshes);
        Assert.Null(target.AuthoringOverlays.RootMotionTrail);
        Assert.True(Assert.Single(target.Meshes).IsSelected);
        Assert.Empty(viewModel.Jobs);
    }

    [Fact]
    public async Task NewWorkspaceClearsPublishedRootTrailState()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "new-overlay-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "new-overlay-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-new-overlay.dlraproj")),
            assets);
        viewModel.ShowRootMotionTrail = true;
        viewModel.TargetViewport.SceneSource.SetAuthoringOverlays(
            new RenderAuthoringOverlayState(
                new RenderAuthoringOverlayOptions(
                    ShowRootMotionTrail: true),
                new RootMotionTrailRenderData(
                    new Vector3[]
                    {
                        Vector3.Zero,
                        Vector3.UnitX,
                    })));
        Assert.NotNull(
            viewModel.TargetViewport.SceneSource
                .CaptureFrame()
                .AuthoringOverlays
                .RootMotionTrail);

        viewModel.NewWorkspaceCommand.Execute(null);

        RenderFrameSnapshot cleared =
            viewModel.TargetViewport.SceneSource.CaptureFrame();
        Assert.True(
            cleared.AuthoringOverlays.Options.ShowRootMotionTrail);
        Assert.Null(cleared.AuthoringOverlays.RootMotionTrail);
        Assert.Empty(cleared.Meshes);
    }

    [Fact]
    public async Task PreviewModeMakesRawAndDl1ProfileExplicitAndPersistent()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "preview-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "preview-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(_temporaryDirectory, "unused.dlraproj")),
            assets);

        Assert.Equal("DL1 profile", viewModel.SelectedPreviewMode);
        viewModel.SelectedPreviewMode = "Raw";

        Assert.Equal(
            ProjectPreviewMode.Raw,
            viewModel.CurrentProject.PreviewMode);
        Assert.Equal(
            PreviewFidelityTier.Raw,
            viewModel.ActivePreviewProfile.FidelityTier);
        Assert.Equal(
            Dl1PreviewContext.Raw,
            viewModel.ActivePreviewProfile.Context);
        Assert.Equal(
            "Raw",
            Assert.Single(
                viewModel.FidelityBadges,
                badge => badge.Label == "Preview fidelity").State);

        viewModel.ActiveWorkspaceMode = "FPP";
        viewModel.FacialFpp.UseFppCamera = true;
        WorkspaceSnapshot snapshot = viewModel.CreateSnapshot();

        Assert.Equal(
            ProjectPreviewMode.Raw,
            snapshot.Project?.PreviewMode);
        PreviewProfile savedRaw = viewModel.ActivePreviewProfile;
        Assert.Equal(Dl1PreviewContext.Raw, savedRaw.Context);
        Assert.Equal(PreviewViewMode.Split, savedRaw.ViewMode);
        Assert.Equal(
            Dl1PreviewContract.EyeCameraBoneName,
            savedRaw.CameraBoneName);
        Assert.Empty(savedRaw.ProceduralToggles);

        viewModel.SelectedPreviewMode = "DL1 profile";

        Assert.Equal(
            ProjectPreviewMode.Dl1Profile,
            viewModel.CurrentProject.PreviewMode);
        Assert.Equal(
            Dl1PreviewContext.Dl1Fpp,
            viewModel.CurrentProject.PreviewProfile.Context);
        Assert.Equal(
            PreviewFidelityTier.Dl1Profile,
            viewModel.CurrentProject.PreviewProfile.FidelityTier);
    }

    [Fact]
    public async Task RestoredRawProjectRestoresRawPreviewSelection()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        WorkspaceSnapshot snapshot;
        await using (var firstAssets = new Dl1AssetWorkspace(
                         Path.Combine(
                             _temporaryDirectory,
                             "first-preview-assets.sqlite3"),
                         Path.Combine(
                             _temporaryDirectory,
                             "first-preview-cache")))
        {
            await using var first = new MainWindowViewModel(
                CreateStore(),
                new TestProjectFileDialogs(
                    Path.Combine(
                        _temporaryDirectory,
                        "unused-first.dlraproj")),
                firstAssets);
            first.SelectedPreviewMode = "Raw";
            snapshot = first.CreateSnapshot();
        }

        await using var secondAssets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "second-preview-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "second-preview-cache"));
        await using var second = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-second.dlraproj")),
            secondAssets);

        second.RestoreSnapshot(snapshot);

        Assert.Equal("Raw", second.SelectedPreviewMode);
        Assert.Equal(
            ProjectPreviewMode.Raw,
            second.CurrentProject.PreviewMode);
        Assert.Equal(
            Dl1PreviewContext.Raw,
            second.ActivePreviewProfile.Context);
    }

    [Fact]
    public async Task RestoredProjectRehydratesFppPreviewConfiguration()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        WorkspaceSnapshot snapshot;
        await using (var firstAssets = new Dl1AssetWorkspace(
                         Path.Combine(
                             _temporaryDirectory,
                             "first-fpp-assets.sqlite3"),
                         Path.Combine(
                             _temporaryDirectory,
                             "first-fpp-cache")))
        {
            await using var first = new MainWindowViewModel(
                CreateStore(),
                new TestProjectFileDialogs(
                    Path.Combine(
                        _temporaryDirectory,
                        "unused-first-fpp.dlraproj")),
                firstAssets);
            first.ActiveWorkspaceMode = "FPP";
            first.FacialFpp.UseFppCamera = true;
            first.FacialFpp.ShowHands = false;
            first.FacialFpp.EnableHSpineBasisCorrection = false;
            first.FacialFpp.EnableHeadPositionCorrection = true;
            first.FacialFpp.EnableHandInertia = true;
            first.FacialFpp.ShowCameraRig = false;
            first.FacialFpp.FieldOfView = 83.0f;
            first.FacialFpp.NearPlane = 0.006f;

            snapshot = first.CreateSnapshot();
            PreviewProfile saved =
                Assert.IsType<PreviewProfile>(
                    snapshot.Project?.PreviewProfile);
            Assert.Equal(Dl1PreviewContext.Dl1Fpp, saved.Context);
            Assert.DoesNotContain(
                Dl1PreviewStageIds.FppHandsProjection,
                saved.ProceduralToggles);
            Assert.DoesNotContain(
                Dl1PreviewStageIds.FppHeadSpineCorrection,
                saved.ProceduralToggles);
            Assert.DoesNotContain(
                Dl1PreviewStageIds.FppHSpineBasisCorrection,
                saved.ProceduralToggles);
            Assert.Contains(
                Dl1PreviewStageIds.FppHeadPositionCorrection,
                saved.ProceduralToggles);
            Assert.Contains(
                Dl1PreviewStageIds.FppHandInertia,
                saved.ProceduralToggles);
            Assert.False(
                snapshot.Project!.Dl1Settings.ShowCameraHelpers);
        }

        await using var secondAssets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "second-fpp-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "second-fpp-cache"));
        await using var second = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-second-fpp.dlraproj")),
            secondAssets);

        second.RestoreSnapshot(snapshot);

        Assert.Equal("FPP", second.ActiveWorkspaceMode);
        Assert.True(second.FacialFpp.UseFppCamera);
        Assert.False(second.FacialFpp.ShowHands);
        Assert.False(
            second.FacialFpp.EnableHSpineBasisCorrection);
        Assert.True(
            second.FacialFpp.EnableHeadPositionCorrection);
        Assert.True(second.FacialFpp.EnableHandInertia);
        Assert.False(second.FacialFpp.ShowCameraRig);
        Assert.Equal(83.0f, second.FacialFpp.FieldOfView);
        Assert.Equal(0.006f, second.FacialFpp.NearPlane);
        Assert.Equal(
            snapshot.Project!.PreviewProfile.Context,
            second.ActivePreviewProfile.Context);
        Assert.Equal(
            snapshot.Project.PreviewProfile.CameraLens,
            second.ActivePreviewProfile.CameraLens);
        Assert.True(
            snapshot.Project.PreviewProfile.ProceduralToggles.SequenceEqual(
                second.ActivePreviewProfile.ProceduralToggles,
                StringComparer.Ordinal));
    }

    [Fact]
    public async Task LegacyGroupedHeadCorrectionLoadsBothConcreteStages()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        PreviewProfile baseline =
            PreviewProfile.FirstPersonAuthoring;
        var legacyProfile = new PreviewProfile(
            baseline.Id,
            baseline.ViewMode,
            baseline.Fidelity,
            baseline.VisualStyle,
            baseline.CameraBoneName,
            baseline.CameraLens,
            baseline.CameraOffset,
            baseline.FidelityTier,
            baseline.Context,
            baseline.ProfileVersion,
            baseline.BuildFingerprint,
            [Dl1PreviewStageIds.FppHeadSpineCorrection],
            baseline.MorphActivationThreshold,
            baseline.MaximumActiveMorphTargets,
            baseline.ClampMorphWeightsToRigBounds,
            baseline.CaptureFingerprint);
        DlraProject project =
            DlraProject.Create("Legacy grouped FPP") with
            {
                PreviewMode = ProjectPreviewMode.Dl1Profile,
                PreviewProfile = legacyProfile,
            };
        var snapshot = new WorkspaceSnapshot(
            WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            null,
            string.Empty,
            null,
            null,
            0,
            true,
            60.0f,
            0.02f,
            "FPP",
            project);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "legacy-fpp-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "legacy-fpp-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-legacy-fpp.dlraproj")),
            assets);

        viewModel.RestoreSnapshot(snapshot);

        Assert.True(
            viewModel.FacialFpp.EnableHSpineBasisCorrection);
        Assert.True(
            viewModel.FacialFpp.EnableHeadPositionCorrection);
        PreviewProfile saved = Assert.IsType<PreviewProfile>(
            viewModel.CreateSnapshot().Project?.PreviewProfile);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHeadSpineCorrection,
            saved.ProceduralToggles);
        Assert.Contains(
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            saved.ProceduralToggles);
        Assert.Contains(
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            saved.ProceduralToggles);
    }

    [Fact]
    public async Task SkeletonRoleTogglesPreserveHelperClassification()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "role-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "role-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(_temporaryDirectory, "unused-role.dlraproj")),
            assets);
        viewModel.SkeletonRoots.Add(
            new SkeletonNodeViewModel(
                "offset_helper",
                "offset_helper",
                0,
                -1,
                role: BoneRenderRole.Helper));

        viewModel.RefreshEditableSkeletonPreview();

        SkeletonRenderData initial =
            Assert.IsType<SkeletonRenderData>(
                viewModel.TargetViewport.SceneSource
                    .CaptureFrame()
                    .Skeleton);
        Assert.Equal(
            BoneRenderRole.Helper,
            Assert.Single(initial.Bones).Role);
        Assert.True(initial.ShowHelpers);
        Assert.True(initial.IsVisible(initial.Bones[0]));

        viewModel.ShowHelpers = false;

        SkeletonRenderData hidden =
            Assert.IsType<SkeletonRenderData>(
                viewModel.TargetViewport.SceneSource
                    .CaptureFrame()
                    .Skeleton);
        Assert.False(hidden.ShowHelpers);
        Assert.False(hidden.IsVisible(hidden.Bones[0]));

        viewModel.ShowHelpers = true;

        SkeletonRenderData visible =
            Assert.IsType<SkeletonRenderData>(
                viewModel.TargetViewport.SceneSource
                    .CaptureFrame()
                    .Skeleton);
        Assert.True(visible.ShowHelpers);
        Assert.True(visible.IsVisible(visible.Bones[0]));
    }

    [Fact]
    public async Task SkeletonEditorRefreshPreservesHiddenHierarchyRows()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "hidden-role-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "hidden-role-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused-hidden-role.dlraproj")),
            assets);
        viewModel.SkeletonRoots.Add(
            new SkeletonNodeViewModel(
                "embedded_skin_row",
                "embedded_skin_row",
                0,
                -1,
                role: BoneRenderRole.Prop,
                isHierarchyOverlayVisible: false));

        viewModel.RefreshEditableSkeletonPreview();

        SkeletonRenderData rendered =
            Assert.IsType<SkeletonRenderData>(
                viewModel.TargetViewport.SceneSource
                    .CaptureFrame()
                    .Skeleton);
        BoneRenderData hidden = Assert.Single(rendered.Bones);
        Assert.Equal(BoneRenderRole.Prop, hidden.Role);
        Assert.False(hidden.IsHierarchyOverlayVisible);
        viewModel.ShowPropHelpers = true;
        Assert.False(
            viewModel.TargetViewport.SceneSource
                .CaptureFrame()
                .Skeleton!
                .IsVisible(hidden));
    }

    [Fact]
    public void BoneEditorAppliesAndResetsSelectedBone()
    {
        BoneTransformEditorViewModel editor = new();
        SkeletonNodeViewModel bone = new("Head", "Root/Spine/Head", 8, 7)
        {
            PositionX = 2.0,
            RotationY = 12.0,
            ScaleZ = 0.8,
        };
        int applied = 0;
        editor.TransformApplied += (_, _) => applied++;
        editor.Bone = bone;

        editor.ApplyCommand.Execute(null);
        editor.ResetCommand.Execute(null);

        Assert.Equal(2, applied);
        Assert.Equal(0.0, bone.PositionX);
        Assert.Equal(0.0, bone.RotationY);
        Assert.Equal(1.0, bone.ScaleZ);
    }

    [Fact]
    public void AssetBrowserCombinesTypeProviderAndTextFilters()
    {
        AssetBrowserViewModel browser = new();
        browser.ReplaceAssets(
        [
            new AssetItemViewModel(
                "base-mesh",
                "player_1_tpp",
                AssetKind.Mesh,
                "dl1-rpack:base",
                "common/meshes/player_1_tpp"),
            new AssetItemViewModel(
                "dlc-mesh",
                "volatile",
                AssetKind.Mesh,
                "dl1-rpack:dlc",
                "dlc/meshes/volatile"),
            new AssetItemViewModel(
                "base-animation",
                "idle",
                AssetKind.Animation,
                "dl1-rpack:base",
                "common/anims/idle"),
        ]);

        browser.SelectedKindFilter = nameof(AssetKind.Mesh);
        browser.SelectedProviderFilter = "dl1-rpack:base";
        browser.SearchText = "player";

        AssetItemViewModel visible = Assert.Single(browser.VisibleAssets);
        Assert.Equal("player_1_tpp", visible.Name);
        Assert.Equal(1, browser.FilteredAssetCount);
        Assert.Contains(
            "dl1-rpack:dlc",
            browser.ProviderFilters,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void MimicFilterPreservesAuthoritativeMorphsWeightsAndPresets()
    {
        FacialFppViewModel viewModel = new();
        var smile = new MorphChannelViewModel("Smile")
        {
            Weight = 0.65f,
        };
        var eyeBlink = new MorphChannelViewModel("EyeBlink")
        {
            Weight = 0.25f,
        };
        viewModel.ReplaceMorphs([smile, eyeBlink]);
        viewModel.ReplaceMimicPresets(
            ["Happy", "Eyes closed"]);

        viewModel.MimicFilter = "eye";

        Assert.Equal(2, viewModel.Morphs.Count);
        Assert.Same(
            eyeBlink,
            Assert.Single(viewModel.VisibleMorphs));
        Assert.Equal(0.25f, eyeBlink.Weight);
        Assert.Equal(2, viewModel.MimicPresets.Count);
        Assert.Equal(
            "Eyes closed",
            Assert.Single(viewModel.VisibleMimicPresets));
        Assert.Equal(
            "Eyes closed",
            viewModel.SelectedMimicPreset);

        viewModel.MimicFilter = string.Empty;

        Assert.Equal(2, viewModel.VisibleMorphs.Count);
        Assert.Same(smile, viewModel.VisibleMorphs[0]);
        Assert.Equal(0.65f, viewModel.VisibleMorphs[0].Weight);
        Assert.Equal(2, viewModel.VisibleMimicPresets.Count);
    }

    [Fact]
    public async Task FacialPoseCommandStoresExportableMorphKeys()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "facial.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Facial") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/missing.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "face",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                    MorphEditLayers =
                    [
                        new MorphEditLayer(
                            Guid.NewGuid(),
                            "FED baseline",
                            MorphEditBlendMode.Additive,
                            MorphEditLayerScope.AuthoredExportable,
                            1,
                            [
                                new MorphEditTrack(
                                    "smile",
                                    [
                                        new ScalarKeyframe(0, 0.1),
                                    ]),
                            ]),
                    ],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        var dialogs = new TestProjectFileDialogs(projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "facial-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "facial-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            dialogs,
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var smile = new MorphChannelViewModel("smile")
        {
            Weight = 0.75f,
        };
        viewModel.FacialFpp.ReplaceMorphs([smile]);
        viewModel.Timeline.CurrentFrame = 2;

        viewModel.KeyMorphPoseCommand.Execute(null);

        ImmutableArray<MorphEditLayer> layers =
            viewModel.CurrentProject.Animations[0].MorphEditLayers;
        Assert.Equal(2, layers.Length);
        MorphEditLayer layer = layers[^1];
        Assert.Equal("Editor Facial Adjustments", layer.Name);
        MorphEditTrack track = Assert.Single(layer.Tracks);
        ScalarKeyframe key = Assert.Single(track.Keyframes);
        Assert.Equal(MorphEditLayerScope.AuthoredExportable, layer.Scope);
        Assert.Equal(MorphEditBlendMode.Override, layer.BlendMode);
        Assert.True(layer.Enabled);
        Assert.Equal(1, layer.Weight);
        Assert.Equal(2, key.Frame);
        Assert.Equal(0.75, key.Value, 5);
        var rig = new RigDefinition(
            "face",
            "Face",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "smile",
                    minimumValue: -4,
                    maximumValue: 4),
            ]);
        MorphEvaluationResult evaluated = MorphEvaluator.Evaluate(
            new Dictionary<string, double>
            {
                ["smile"] = 0.25,
            },
            rig,
            2,
            PreviewProfile.ThirdPersonAuthoring,
            EvaluationPurpose.Export,
            layers: layers);
        Assert.Equal(
            0.75,
            evaluated.AuthoredWeights["smile"],
            5);
        Assert.Contains(
            viewModel.Timeline.Tracks,
            item => item.Channel.Contains("Morph", StringComparison.Ordinal));
        TimelineCurveTrackViewModel curve = Assert.Single(
            viewModel.Timeline.Curves,
            item => item.Name.StartsWith(
                "Editor Facial Adjustments / smile",
                StringComparison.Ordinal));
        TimelineCurveKeyViewModel curveKey = Assert.Single(curve.Keys);
        Assert.Equal(2.0, curveKey.Frame);
        Assert.Equal(0.75, curveKey.Value, 5);
    }

    [Fact]
    public async Task ProjectCommandsPersistImmutableBoneLayerAndUndoRedo()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "authoring.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        DlraProject sourceProject = DlraProject.Create("Authoring") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                },
            ],
        };
        ProjectSerializer.SaveAtomic(sourceProject, projectPath);

        JsonWorkspaceStateStore store = CreateStore();
        var dialogs = new TestProjectFileDialogs(projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "cache"));
        await using var viewModel = new MainWindowViewModel(
            store,
            dialogs,
            assets);

        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        Matrix4x4 restLocal = Matrix4x4.CreateTranslation(0.0f, 2.0f, 0.0f);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1,
            restLocal,
            restLocal);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.Timeline.CurrentFrame = 1;
        bone.PositionX = 0.25;
        bone.RotationY = 15.0;

        viewModel.BoneEditor.ApplyCommand.Execute(null);

        BoneEditLayer layer = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        BoneEditTrack track = Assert.Single(layer.Tracks);
        Assert.Equal(BoneEditLayerScope.AuthoredExportable, layer.Scope);
        Assert.Equal(1.0, Assert.Single(track.Keyframes).Frame);
        Assert.Equal(0.25, track.Keyframes[0].Value.Translation.X, 10);
        Assert.Equal(restLocal, bone.RestLocalTransform);
        Assert.True(viewModel.IsDirty);
        Assert.Equal(10, viewModel.Timeline.Curves.Count);
        TimelineCurveTrackViewModel translationCurve = Assert.Single(
            viewModel.Timeline.Curves,
            item => item.Name.EndsWith(
                "Translation X",
                StringComparison.Ordinal));
        Assert.Equal(
            0.25,
            Assert.Single(translationCurve.Keys).Value,
            10);

        viewModel.UndoCommand.Execute(null);
        Assert.Empty(viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.Empty(viewModel.Timeline.Curves);
        Assert.False(viewModel.IsDirty);

        viewModel.RedoCommand.Execute(null);
        Assert.Single(viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.Equal(10, viewModel.Timeline.Curves.Count);
        Assert.True(viewModel.IsDirty);

        viewModel.SelectedRootMotionMode =
            Dl1RootMotionMode.MotionAccumulator;
        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        DlraProject saved = ProjectSerializer.Load(projectPath);
        Assert.Single(saved.Animations[0].EditLayers);
        Assert.Equal(
            Dl1RootMotionMode.MotionAccumulator,
            saved.Animations[0].RootMotionMode);
        Assert.False(viewModel.IsDirty);
        Assert.Contains(projectPath, viewModel.RecentProjectPaths);
        Assert.DoesNotContain('*', viewModel.WindowTitle);
    }

    [Fact]
    public async Task BoneLayerControlsApplyWeightAndEnableStateUndoably()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "layer-controls.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        BoneEditLayer editLayer = new(
            Guid.NewGuid(),
            "Hand correction A",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [new TransformKeyframe(
                        0.0,
                        TransformTRS.Identity)]),
            ],
            boneMask: new Dictionary<int, double>
            {
                [1] = 0.8,
            });
        DlraProject project = DlraProject.Create("Layer controls") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                    EditLayers = [editLayer],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "layer-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "layer-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var selectedBone = new SkeletonNodeViewModel(
            "Hand",
            "Bip01/Hand",
            0,
            -1);
        viewModel.SkeletonRoots.Add(selectedBone);
        viewModel.SelectedBone = selectedBone;
        BoneEditLayerItemViewModel item =
            Assert.Single(viewModel.BoneEditLayers);
        Assert.Same(
            item,
            viewModel.SelectedBoneEditLayer);

        item.Weight = double.NaN;
        Assert.Equal(1.0, item.Weight, 10);
        item.Weight = 2.0;
        Assert.Equal(1.0, item.Weight, 10);
        item.LayerEnabled = false;
        item.Weight = 0.35;
        item.BlendMode = BoneEditBlendMode.Override;
        Assert.True(item.CanEditSelectedBoneInterpolation);
        Assert.Equal(
            BoneEditInterpolation.Linear,
            item.SelectedBoneInterpolation);
        item.SelectedBoneInterpolation =
            BoneEditInterpolation.Step;
        item.HasSelectedBoneMask = true;
        item.SelectedBoneMaskWeight =
            double.PositiveInfinity;
        Assert.Equal(
            1.0,
            item.SelectedBoneMaskWeight,
            10);
        item.SelectedBoneMaskWeight = -1;
        Assert.Equal(
            0.0,
            item.SelectedBoneMaskWeight,
            10);
        item.SelectedBoneMaskWeight = 0.4;
        item.ApplyCommand.Execute(null);

        BoneEditLayer updated = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.False(updated.Enabled);
        Assert.Equal(0.35, updated.Weight, 10);
        Assert.Equal(
            BoneEditBlendMode.Override,
            updated.BlendMode);
        Assert.Equal(
            BoneEditLayerScope.AuthoredExportable,
            updated.Scope);
        Assert.Single(updated.Tracks);
        Assert.Equal(
            BoneEditInterpolation.Step,
            updated.Tracks[0].Interpolation);
        Assert.Equal(0.4, updated.BoneMask[0], 10);
        Assert.Equal(0.8, updated.BoneMask[1], 10);
        Assert.True(viewModel.IsDirty);

        viewModel.UndoCommand.Execute(null);

        BoneEditLayer restored = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.True(restored.Enabled);
        Assert.Equal(1.0, restored.Weight, 10);
        Assert.Equal(
            BoneEditBlendMode.Additive,
            restored.BlendMode);
        Assert.Equal(
            BoneEditInterpolation.Linear,
            restored.Tracks[0].Interpolation);
        Assert.False(restored.BoneMask.ContainsKey(0));
        Assert.Equal(0.8, restored.BoneMask[1], 10);

        viewModel.RedoCommand.Execute(null);
        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);

        BoneEditLayer persisted = Assert.Single(
            ProjectSerializer.Load(projectPath)
                .Animations[0]
                .EditLayers);
        Assert.False(persisted.Enabled);
        Assert.Equal(0.35, persisted.Weight, 10);
        Assert.Equal(
            BoneEditBlendMode.Override,
            persisted.BlendMode);
        Assert.Equal(
            BoneEditInterpolation.Step,
            persisted.Tracks[0].Interpolation);
        Assert.Equal(0.4, persisted.BoneMask[0], 10);
        Assert.Equal(0.8, persisted.BoneMask[1], 10);

        BoneEditLayerItemViewModel persistedItem =
            Assert.IsType<BoneEditLayerItemViewModel>(
                viewModel.SelectedBoneEditLayer);
        Assert.True(
            persistedItem.HasSelectedBoneMask);
        persistedItem.HasSelectedBoneMask = false;
        persistedItem.ApplyCommand.Execute(null);
        BoneEditLayer withoutSelectedBone =
            Assert.Single(
                viewModel.CurrentProject
                    .Animations[0]
                    .EditLayers);
        Assert.False(
            withoutSelectedBone.BoneMask
                .ContainsKey(0));
        Assert.Equal(
            0.8,
            withoutSelectedBone.BoneMask[1],
            10);
        viewModel.UndoCommand.Execute(null);
        Assert.Equal(
            0.4,
            viewModel.CurrentProject
                .Animations[0]
                .EditLayers[0]
                .BoneMask[0],
            10);
    }

    [Fact]
    public async Task SelectedBoneLayerReceivesKeysWithoutChangingItsControls()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "selected-layer-keys.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        BoneEditLayer firstLayer = new(
            Guid.NewGuid(),
            "Layer A",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            TransformTRS.Identity),
                    ]),
            ]);
        BoneEditLayer selectedLayer = new(
            Guid.NewGuid(),
            "Layer B",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.PreviewOnly,
            0.6,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0,
                            new TransformTRS(
                                new Vector3D(
                                    0.5,
                                    0,
                                    0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            enabled: false,
            boneMask: new Dictionary<int, double>
            {
                [0] = 0.25,
            });
        DlraProject project =
            DlraProject.Create("Selected layer") with
            {
                Assets =
                [
                    new ProjectAssetReference
                    {
                        Id = sourceAssetId,
                        Kind =
                            ProjectAssetKind
                                .SourceAnimation,
                        RelativePath =
                            "inputs/source.fbx",
                    },
                ],
                Animations =
                [
                    new ProjectAnimation
                    {
                        Name = "interaction",
                        SourceAssetId = sourceAssetId,
                        TargetRigId =
                            "dl1:test-rig",
                        FrameRate =
                            new FrameRate(30, 1),
                        FrameCount = 2,
                        EditLayers =
                            [firstLayer, selectedLayer],
                    },
                ],
            };
        ProjectSerializer.SaveAtomic(
            project,
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "selected-layer-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "selected-layer-cache"));
        await using var viewModel =
            new MainWindowViewModel(
                CreateStore(),
                new TestProjectFileDialogs(
                    projectPath),
                assets);
        await viewModel.OpenWorkspaceCommand
            .ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Hand",
            "Bip01/Hand",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.SelectedBoneEditLayer =
            Assert.Single(
                viewModel.BoneEditLayers,
                item => item.Id ==
                    selectedLayer.Id);
        Assert.Equal(0.5, bone.PositionX, 10);
        viewModel.Timeline.CurrentFrame = 1;
        bone.PositionX = 0.75;

        viewModel.BoneEditor.ApplyCommand
            .Execute(null);

        ProjectAnimation updatedAnimation =
            Assert.Single(
                viewModel.CurrentProject.Animations);
        BoneEditLayer untouched =
            Assert.Single(
                updatedAnimation.EditLayers,
                layer => layer.Id ==
                    firstLayer.Id);
        Assert.Single(
            Assert.Single(
                untouched.Tracks)
            .Keyframes);
        BoneEditLayer updated =
            Assert.Single(
                updatedAnimation.EditLayers,
                layer => layer.Id ==
                    selectedLayer.Id);
        Assert.Equal(
            BoneEditBlendMode.Override,
            updated.BlendMode);
        Assert.Equal(
            BoneEditLayerScope.PreviewOnly,
            updated.Scope);
        Assert.False(updated.Enabled);
        Assert.Equal(0.6, updated.Weight, 10);
        Assert.Equal(0.25, updated.BoneMask[0], 10);
        Assert.Equal(
            [0.0, 1.0],
            Assert.Single(updated.Tracks)
                .Keyframes
                .Select(static key => key.Frame)
                .ToArray());
        Assert.Equal(
            0.75,
            updated.Tracks[0]
                .Keyframes[1]
                .Value
                .Translation
                .X,
            10);
        Assert.Equal(
            selectedLayer.Id,
            viewModel.SelectedBoneEditLayer?.Id);

        viewModel.UndoCommand.Execute(null);
        BoneEditLayer restored =
            Assert.Single(
                viewModel.CurrentProject
                    .Animations[0]
                    .EditLayers,
                layer => layer.Id ==
                    selectedLayer.Id);
        Assert.Single(
            Assert.Single(
                restored.Tracks)
            .Keyframes);
    }

    [Fact]
    public async Task TranslationGizmoStagesPreviewAndCommitsOneUndoStep()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "gizmo-authoring.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Gizmo authoring") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "gizmo-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "gizmo-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.Timeline.CurrentFrame = 1;
        RenderFrameSnapshot frame =
            viewModel.SourceViewport.SceneSource.CaptureFrame();
        GizmoRenderData xHandle = Assert.Single(
            frame.Gizmos,
            gizmo => gizmo.TranslationBinding is
            {
                Axis: TranslationGizmoAxis.X,
            });
        TranslationGizmoBinding binding =
            xHandle.TranslationBinding!.Value;
        Vector3 axis = Vector3.Normalize(
            xHandle.End - xHandle.Start);

        Assert.True(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding,
                        axis)));
        Assert.True(
            viewModel.SourceViewport.SceneSource
                .UpdateTranslationGizmoDrag(
                    new RenderTranslationGizmoDragUpdate(
                        binding,
                        axis * 0.1f,
                        0.1f)));
        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.False(viewModel.IsDirty);
        Assert.Equal(0.1, bone.PositionX, 5);

        Assert.True(
            viewModel.SourceViewport.SceneSource
                .UpdateTranslationGizmoDrag(
                    new RenderTranslationGizmoDragUpdate(
                        binding,
                        axis * 0.25f,
                        0.25f)));
        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.Equal(0.25, bone.PositionX, 5);

        viewModel.SourceViewport.SceneSource
            .CompleteTranslationGizmoDrag(commit: true);

        BoneEditLayer layer = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        TransformKeyframe key = Assert.Single(
            Assert.Single(layer.Tracks).Keyframes);
        Assert.Equal(1.0, key.Frame);
        Assert.Equal(0.25, key.Value.Translation.X, 5);
        Assert.True(viewModel.IsDirty);

        viewModel.UndoCommand.Execute(null);

        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.False(viewModel.UndoCommand.CanExecute(null));
        Assert.False(viewModel.IsDirty);

        Assert.False(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding with
                        {
                            Axis =
                                (TranslationGizmoAxis)99,
                        },
                        axis)));
        Assert.False(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding with
                        {
                            Space =
                                (RenderGizmoSpace)99,
                        },
                        axis)));

        Assert.True(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding,
                        axis)));
        Assert.True(
            viewModel.SourceViewport.SceneSource
                .UpdateTranslationGizmoDrag(
                    new RenderTranslationGizmoDragUpdate(
                        binding,
                        axis * 0.25f,
                        0.25f)));
        Assert.False(
            viewModel.SourceViewport.SceneSource
                .UpdateTranslationGizmoDrag(
                    new RenderTranslationGizmoDragUpdate(
                        binding with
                        {
                            Axis =
                                (TranslationGizmoAxis)99,
                        },
                        axis * 0.5f,
                        0.5f)));
        viewModel.SourceViewport.SceneSource
            .CompleteTranslationGizmoDrag(commit: true);
        Assert.Empty(
            viewModel.CurrentProject.Animations[0]
                .EditLayers);
        Assert.Equal(0.0, bone.PositionX);

        bone.IsLocked = true;
        Assert.False(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding,
                        axis)));
        bone.IsLocked = false;
        viewModel.SelectedBone = null;
        Assert.False(
            viewModel.SourceViewport.SceneSource
                .TryBeginTranslationGizmoDrag(
                    new RenderTranslationGizmoDragStart(
                        binding,
                        axis)));
    }

    [Fact]
    public async Task TranslationGizmoAxesHonorLocalAndGlobalSpace()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "axes-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "axes-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(
                Path.Combine(
                    _temporaryDirectory,
                    "unused.dlraproj")),
            assets);
        Matrix4x4 localRotation =
            Matrix4x4.CreateRotationZ(MathF.PI / 2.0f);
        var bone = new SkeletonNodeViewModel(
            "Hand",
            "Hand",
            0,
            -1,
            localRotation,
            localRotation);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;

        GizmoRenderData localX = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Gizmos,
            gizmo => gizmo.TranslationBinding is
            {
                Axis: TranslationGizmoAxis.X,
                Space: RenderGizmoSpace.Local,
            });
        Vector3 localAxis = Vector3.Normalize(
            localX.End - localX.Start);
        Assert.InRange(MathF.Abs(localAxis.X), 0.0f, 1.0e-5f);
        Assert.Equal(1.0f, localAxis.Y, 5);

        viewModel.BoneEditor.GizmoSpace =
            RenderGizmoSpace.Global;

        GizmoRenderData globalX = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Gizmos,
            gizmo => gizmo.TranslationBinding is
            {
                Axis: TranslationGizmoAxis.X,
                Space: RenderGizmoSpace.Global,
            });
        Vector3 globalAxis = Vector3.Normalize(
            globalX.End - globalX.Start);
        Assert.Equal(Vector3.UnitX, globalAxis);
    }

    [Fact]
    public async Task RotationGizmoStoresExactQuaternionInSelectedLayer()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "rotation-gizmo.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        Guid layerId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Rotation gizmo") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                    EditLayers =
                    [
                        new BoneEditLayer(
                            layerId,
                            "Selected corrections",
                            BoneEditBlendMode.Override,
                            BoneEditLayerScope.AuthoredExportable,
                            0.75,
                            []),
                    ],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "rotation-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "rotation-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.Timeline.CurrentFrame = 1;
        viewModel.SelectedBoneEditLayer =
            Assert.Single(viewModel.BoneEditLayers);
        viewModel.BoneEditor.GizmoMode =
            RenderTransformGizmoMode.Rotate;

        GizmoRenderData handle = viewModel.SourceViewport.SceneSource
            .CaptureFrame()
            .Gizmos
            .First(gizmo => gizmo.TransformBinding is
            {
                Mode: RenderTransformGizmoMode.Rotate,
                Axis: RenderTransformGizmoAxis.X,
            });
        RenderTransformGizmoBinding binding =
            handle.TransformBinding!.Value;
        Vector3 axis =
            handle.InteractionAxisWorld!.Value;
        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    Vector3.Zero,
                    AxisDistance: 0.0f,
                    RotationRadians: 0.5f,
                    ScaleFactor: 1.0f)));
        Assert.Empty(
            viewModel.CurrentProject.Animations[0]
                .EditLayers[0]
                .Tracks);
        Assert.NotEqual(0.0, bone.RotationX);

        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);

        BoneEditLayer layer = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.Equal(layerId, layer.Id);
        Assert.Equal(BoneEditBlendMode.Override, layer.BlendMode);
        Assert.Equal(0.75, layer.Weight);
        TransformKeyframe key = Assert.Single(
            Assert.Single(layer.Tracks).Keyframes);
        QuaternionD expected = QuaternionD.FromAxisAngle(
            Vector3D.UnitX,
            0.5);
        Assert.Equal(expected.X, key.Value.Rotation.X, 6);
        Assert.Equal(expected.Y, key.Value.Rotation.Y, 6);
        Assert.Equal(expected.Z, key.Value.Rotation.Z, 6);
        Assert.Equal(expected.W, key.Value.Rotation.W, 6);

        viewModel.UndoCommand.Execute(null);
        Assert.Empty(
            viewModel.CurrentProject.Animations[0]
                .EditLayers[0]
                .Tracks);
        Assert.False(viewModel.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task ScaleGizmoIsLocalBoundedAndZeroDragDoesNotMutate()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "scale-gizmo.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Scale gizmo") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "scale-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "scale-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.BoneEditor.GizmoSpace =
            RenderGizmoSpace.Global;
        viewModel.BoneEditor.GizmoMode =
            RenderTransformGizmoMode.Scale;
        Assert.False(viewModel.BoneEditor.IsGizmoSpaceEnabled);

        GizmoRenderData handle = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Gizmos,
            gizmo => gizmo.TransformBinding is
            {
                Mode: RenderTransformGizmoMode.Scale,
                Axis: RenderTransformGizmoAxis.X,
                Space: RenderGizmoSpace.Local,
            });
        RenderTransformGizmoBinding binding =
            handle.TransformBinding!.Value;
        Vector3 axis =
            handle.InteractionAxisWorld!.Value;
        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    Vector3.Zero,
                    AxisDistance: 0.0f,
                    RotationRadians: 0.0f,
                    ScaleFactor: float.MaxValue)));
        Assert.Equal(1000.0, bone.ScaleX);
        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: false);
        Assert.Equal(1.0, bone.ScaleX);
        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);

        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    Vector3.Zero,
                    AxisDistance: 0.0f,
                    RotationRadians: 0.0f,
                    ScaleFactor: 1.0f)));
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);

        Assert.Empty(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.False(viewModel.UndoCommand.CanExecute(null));
        Assert.False(viewModel.IsDirty);

        bone.ScaleX = 0.0;
        Assert.False(
            viewModel.BoneEditor.ApplyCommand
                .CanExecute(null));
        viewModel.BoneEditor.ApplyCommand.Execute(null);
        Assert.Empty(
            viewModel.CurrentProject.Animations[0]
                .EditLayers);
        bone.ScaleX = 1000.001;
        Assert.False(
            viewModel.BoneEditor.ApplyCommand
                .CanExecute(null));
        bone.ScaleX = 0.001;
        Assert.True(
            viewModel.BoneEditor.ApplyCommand
                .CanExecute(null));

        bone.IsLocked = true;
        Assert.False(viewModel.BoneEditor.ApplyCommand.CanExecute(null));
        Assert.False(viewModel.BoneEditor.ResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task TranslationGizmoPreservesUntouchedDoublePrecisionQuaternion()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "translation-quaternion-precision.dlraproj");
        QuaternionD preciseRotation = new QuaternionD(
            0.123456789012345,
            -0.234567890123456,
            0.345678901234567,
            0.891234567890123)
            .Normalized();
        BoneEditLayer layer = new(
            Guid.NewGuid(),
            "Precise rotation",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                Vector3D.Zero,
                                preciseRotation,
                                Vector3D.One)),
                    ]),
            ]);
        ProjectSerializer.SaveAtomic(
            CreateAnimationProject(
                "Translation precision",
                layer),
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "translation-precision-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "translation-precision-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.BoneEditor.GizmoSpace =
            RenderGizmoSpace.Global;
        GizmoRenderData handle = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Gizmos,
            gizmo => gizmo.TransformBinding is
            {
                Mode: RenderTransformGizmoMode.Translate,
                Axis: RenderTransformGizmoAxis.X,
                Space: RenderGizmoSpace.Global,
            });
        RenderTransformGizmoBinding binding =
            handle.TransformBinding!.Value;
        Vector3 axis =
            handle.InteractionAxisWorld!.Value;

        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    Vector3.UnitX * 0.25f,
                    AxisDistance: 0.25f,
                    RotationRadians: 0.0f,
                    ScaleFactor: 1.0f)));
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);

        TransformKeyframe key = Assert.Single(
            Assert.Single(
                viewModel.CurrentProject
                    .Animations[0]
                    .EditLayers[0]
                    .Tracks)
            .Keyframes);
        Assert.Equal(
            preciseRotation.X,
            key.Value.Rotation.X,
            14);
        Assert.Equal(
            preciseRotation.Y,
            key.Value.Rotation.Y,
            14);
        Assert.Equal(
            preciseRotation.Z,
            key.Value.Rotation.Z,
            14);
        Assert.Equal(
            preciseRotation.W,
            key.Value.Rotation.W,
            14);
    }

    [Fact]
    public async Task GlobalRotationUsesEvaluatedSelectedBoneOrientation()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "global-rotation-underlying.dlraproj");
        QuaternionD initialEdit =
            QuaternionD.FromAxisAngle(
                Vector3D.UnitX,
                0.2);
        BoneEditLayer layer = new(
            Guid.NewGuid(),
            "Additive correction",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                Vector3D.Zero,
                                initialEdit,
                                Vector3D.One)),
                    ]),
            ]);
        ProjectSerializer.SaveAtomic(
            CreateAnimationProject(
                "Global rotation",
                layer),
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "global-rotation-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "global-rotation-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        Matrix4x4 restRotation =
            Matrix4x4.CreateRotationZ(MathF.PI / 2.0f);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1,
            restRotation,
            restRotation);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.BoneEditor.GizmoMode =
            RenderTransformGizmoMode.Rotate;
        viewModel.BoneEditor.GizmoSpace =
            RenderGizmoSpace.Global;
        RenderFrameSnapshot frame =
            viewModel.SourceViewport.SceneSource
                .CaptureFrame();
        GizmoRenderData handle = frame.Gizmos.First(
            gizmo => gizmo.TransformBinding is
            {
                Mode: RenderTransformGizmoMode.Rotate,
                Axis: RenderTransformGizmoAxis.X,
                Space: RenderGizmoSpace.Global,
            });
        RenderTransformGizmoBinding binding =
            handle.TransformBinding!.Value;
        Vector3 worldAxis =
            handle.InteractionAxisWorld!.Value;
        Matrix4x4 selectedWorld =
            frame.Skeleton!.Bones[0].WorldTransform *
            frame.Skeleton.RootTransform;
        Assert.True(Matrix4x4.Decompose(
            selectedWorld,
            out _,
            out System.Numerics.Quaternion worldRotation,
            out _));
        Vector3 selectedLocalAxis = Vector3.Normalize(
            Vector3.Transform(
                worldAxis,
                System.Numerics.Quaternion.Inverse(
                    System.Numerics.Quaternion.Normalize(
                        worldRotation))));
        QuaternionD expected = (
            initialEdit *
            QuaternionD.FromAxisAngle(
                new Vector3D(
                    selectedLocalAxis.X,
                    selectedLocalAxis.Y,
                    selectedLocalAxis.Z),
                0.5))
            .Normalized();

        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    worldAxis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    Vector3.Zero,
                    AxisDistance: 0.0f,
                    RotationRadians: 0.5f,
                    ScaleFactor: 1.0f)));
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);

        QuaternionD actual = Assert.Single(
                Assert.Single(
                    viewModel.CurrentProject
                        .Animations[0]
                        .EditLayers[0]
                        .Tracks)
                .Keyframes)
            .Value
            .Rotation;
        Assert.True(
            Math.Abs(
                QuaternionD.Dot(expected, actual)) >
            1.0 - 1.0e-12);
    }

    [Fact]
    public async Task FallbackSkeletonUsesAuthoritativeLayerBlendSemantics()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "fallback-layer-semantics.dlraproj");
        BoneEditLayer layer = new(
            Guid.NewGuid(),
            "Weighted override",
            BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable,
            0.5,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            new TransformTRS(
                                new Vector3D(10.0, 0.0, 0.0),
                                QuaternionD.Identity,
                                Vector3D.One)),
                    ]),
            ],
            boneMask: new Dictionary<int, double>
            {
                [0] = 0.5,
            });
        ProjectSerializer.SaveAtomic(
            CreateAnimationProject(
                "Fallback semantics",
                layer),
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "fallback-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "fallback-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        Matrix4x4 rest =
            Matrix4x4.CreateTranslation(2.0f, 0.0f, 0.0f);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1,
            rest,
            rest);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;

        BoneRenderData rendered = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Skeleton!
                .Bones);
        Assert.Equal(4.0f, rendered.LocalTransform.M41, 5);
        Assert.Equal(4.0f, rendered.WorldTransform.M41, 5);
    }

    [Fact]
    public async Task GizmoDragCancelsWhenFrameOrLayerDestinationChanges()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "gizmo-destination-pinning.dlraproj");
        BoneEditLayer first = new(
            Guid.NewGuid(),
            "Layer A",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            []);
        BoneEditLayer second = new(
            Guid.NewGuid(),
            "Layer B",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            1.0,
            []);
        ProjectSerializer.SaveAtomic(
            CreateAnimationProject(
                "Pinned destination",
                first,
                second),
            projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(
                _temporaryDirectory,
                "pinning-assets.sqlite3"),
            Path.Combine(
                _temporaryDirectory,
                "pinning-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.SelectedBoneEditLayer =
            Assert.Single(
                viewModel.BoneEditLayers,
                item => item.Id == first.Id);
        viewModel.Timeline.CurrentFrame = 1;
        GizmoRenderData handle = Assert.Single(
            viewModel.SourceViewport.SceneSource
                .CaptureFrame()
                .Gizmos,
            gizmo => gizmo.TransformBinding is
            {
                Mode: RenderTransformGizmoMode.Translate,
                Axis: RenderTransformGizmoAxis.X,
            });
        RenderTransformGizmoBinding binding =
            handle.TransformBinding!.Value;
        Vector3 axis =
            handle.InteractionAxisWorld!.Value;

        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    axis * 0.25f,
                    AxisDistance: 0.25f,
                    RotationRadians: 0.0f,
                    ScaleFactor: 1.0f)));
        viewModel.Timeline.CurrentFrame = 2;
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);
        Assert.All(
            viewModel.CurrentProject
                .Animations[0]
                .EditLayers,
            static layer => Assert.Empty(layer.Tracks));

        viewModel.Timeline.CurrentFrame = 1;
        Assert.True(viewModel.SourceViewport.SceneSource
            .TryBeginTransformGizmoDrag(
                new RenderTransformGizmoDragStart(
                    binding,
                    axis)));
        Assert.True(viewModel.SourceViewport.SceneSource
            .UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    axis * 0.25f,
                    AxisDistance: 0.25f,
                    RotationRadians: 0.0f,
                    ScaleFactor: 1.0f)));
        viewModel.SelectedBoneEditLayer =
            Assert.Single(
                viewModel.BoneEditLayers,
                item => item.Id == second.Id);
        viewModel.SourceViewport.SceneSource
            .CompleteTransformGizmoDrag(commit: true);

        Assert.All(
            viewModel.CurrentProject
                .Animations[0]
                .EditLayers,
            static layer => Assert.Empty(layer.Tracks));
        Assert.False(
            viewModel.UndoCommand.CanExecute(null));
    }

    [Fact]
    public async Task BoneKeyUpdatePreservesLayerEnabledStateAndMask()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "layer-metadata.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        Guid layerId = Guid.NewGuid();
        BoneEditLayer editorLayer = new(
            layerId,
            "Editor Bone Adjustments",
            BoneEditBlendMode.Additive,
            BoneEditLayerScope.AuthoredExportable,
            0.8,
            [
                new BoneEditTrack(
                    0,
                    [
                        new TransformKeyframe(
                            0.0,
                            TransformTRS.Identity),
                    ]),
            ],
            enabled: false,
            boneMask: new Dictionary<int, double>
            {
                [0] = 0.35,
            });
        DlraProject project = DlraProject.Create("Layer metadata") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                    EditLayers = [editorLayer],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, projectPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "metadata-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "metadata-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            new TestProjectFileDialogs(projectPath),
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);
        var bone = new SkeletonNodeViewModel(
            "Bip01",
            "Bip01",
            0,
            -1);
        viewModel.SkeletonRoots.Add(bone);
        viewModel.SelectedBone = bone;
        viewModel.Timeline.CurrentFrame = 1;
        bone.PositionY = 0.4;

        viewModel.BoneEditor.ApplyCommand.Execute(null);

        BoneEditLayer updated = Assert.Single(
            viewModel.CurrentProject.Animations[0].EditLayers);
        Assert.Equal(layerId, updated.Id);
        Assert.False(updated.Enabled);
        Assert.Equal(0.35, updated.BoneMask[0], 10);
        Assert.Equal(0.8, updated.Weight, 10);
        Assert.Equal(2, Assert.Single(updated.Tracks).Keyframes.Length);
    }

    [Fact]
    public async Task AdditionalRpackRootIsPortableUndoableAndPersisted()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string projectPath = Path.Combine(
            _temporaryDirectory,
            "retail-roots.dlraproj");
        string rootPath = Path.Combine(
            _temporaryDirectory,
            "packs",
            "authoring");
        Directory.CreateDirectory(rootPath);
        ProjectSerializer.SaveAtomic(
            DlraProject.Create("Retail roots"),
            projectPath);

        var dialogs = new TestProjectFileDialogs(
            projectPath,
            rootPath);
        await using var assets = new Dl1AssetWorkspace(
            Path.Combine(_temporaryDirectory, "root-assets.sqlite3"),
            Path.Combine(_temporaryDirectory, "root-cache"));
        await using var viewModel = new MainWindowViewModel(
            CreateStore(),
            dialogs,
            assets);
        await viewModel.OpenWorkspaceCommand.ExecuteAsync(null);

        viewModel.AddAdditionalRpackRootCommand.Execute(null);

        const string portableRoot = "packs/authoring";
        Assert.Equal(
            portableRoot,
            Assert.Single(
                viewModel.CurrentProject.Dl1Settings
                    .AdditionalRpackRoots));
        Assert.Equal(
            portableRoot,
            Assert.Single(viewModel.AdditionalRpackRoots));
        Assert.True(viewModel.IsDirty);

        viewModel.SelectedAdditionalRpackRoot = portableRoot;
        viewModel.RemoveAdditionalRpackRootCommand.Execute(null);
        Assert.Empty(
            viewModel.CurrentProject.Dl1Settings
                .AdditionalRpackRoots);

        viewModel.UndoCommand.Execute(null);
        Assert.Equal(
            portableRoot,
            Assert.Single(
                viewModel.CurrentProject.Dl1Settings
                    .AdditionalRpackRoots));
        await viewModel.SaveWorkspaceCommand.ExecuteAsync(null);
        Assert.Equal(
            portableRoot,
            Assert.Single(
                ProjectSerializer.Load(projectPath)
                    .Dl1Settings.AdditionalRpackRoots));
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private JsonWorkspaceStateStore CreateStore()
    {
        return new JsonWorkspaceStateStore(
            Path.Combine(_temporaryDirectory, "workspace.json"));
    }

    private static DlraProject CreateAnimationProject(
        string name,
        params BoneEditLayer[] editLayers)
    {
        Guid sourceAssetId = Guid.NewGuid();
        return DlraProject.Create(name) with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "inputs/source.fbx",
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "interaction",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "dl1:test-rig",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 3,
                    EditLayers = [.. editLayers],
                },
            ],
        };
    }

    private sealed class TestProjectFileDialogs(
        string openPath,
        string? additionalRpackRoot = null) :
        IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => openPath;

        public string? ShowSaveProjectDialog(
            string suggestedName,
            string? currentPath) =>
            openPath;

        public string? ShowSelectAdditionalRpackRootDialog(
            string? initialPath) =>
            additionalRpackRoot;
    }
}

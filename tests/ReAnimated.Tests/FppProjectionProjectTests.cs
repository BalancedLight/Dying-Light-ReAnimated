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

public sealed class FppProjectionProjectTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-FppProjection-{Guid.NewGuid():N}");

    [Fact]
    public void FreshProjectRoundTripsExplicitFppProjectionCapture()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "capture.dlraproj");
        DlraProject project = DlraProject.Create("FPP capture") with
        {
            Dl1Settings = new Dl1ProjectSettings
            {
                UseFppProjectionCapture = true,
                FppProjectionCapture = CreateCapture(),
            },
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject loaded = ProjectSerializer.Load(path);

        Assert.True(
            loaded.Dl1Settings.UseFppProjectionCapture);
        Dl1FppProjectionCapture capture = Assert.IsType<
            Dl1FppProjectionCapture>(
            loaded.Dl1Settings.FppProjectionCapture);
        Assert.Equal("win-1.55 test capture", capture.CaptureLabel);
        Assert.Equal(16.0 / 9.0, capture.SceneAspectRatio, 12);
        Assert.Equal(
            Dl1ProjectionFovAxis.Horizontal,
            capture.HandsFieldOfViewAxis);
        string json = File.ReadAllText(path);
        Assert.Contains(
            "\"useFppProjectionCapture\": true",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"handsFieldOfViewAxis\": \"horizontal\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FreshProjectRoundTripsExternalMovieReferenceCameraCapture()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "movie-camera.dlraproj");
        DlraProject project = DlraProject.Create("Movie camera") with
        {
            Dl1Settings = new Dl1ProjectSettings
            {
                UseMovieReferenceCameraCapture = true,
                MovieReferenceCameraCapture = CreateMovieCapture(),
            },
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject loaded = ProjectSerializer.Load(path);

        Assert.True(
            loaded.Dl1Settings.UseMovieReferenceCameraCapture);
        Dl1MovieReferenceCameraCapture capture = Assert.IsType<
            Dl1MovieReferenceCameraCapture>(
            loaded.Dl1Settings.MovieReferenceCameraCapture);
        Assert.Equal(
            new Vector3D(4.0, 2.5, -7.0),
            capture.WorldTransform.Translation);
        Assert.Equal(0.25, capture.WorldTransform.Rotation.Y, 12);
        Assert.Equal(68.0, capture.Lens.VerticalFieldOfViewDegrees);
        Dl1MovieReferenceCameraSnapshot snapshot =
            capture.CreateSnapshot();
        Assert.Equal(
            new Vector3D(4.0, 2.5, -7.0),
            snapshot.WorldTransform.Translation);
        string json = File.ReadAllText(path);
        Assert.Contains(
            "\"useMovieReferenceCameraCapture\": true",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"movieReferenceCameraCapture\"",
            json,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WpfViewModelStoresAndRestoresCaptureWithoutDefaults()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var store = new JsonWorkspaceStateStore(
            Path.Combine(_temporaryDirectory, "recovery.json"));
        await using var first = new MainWindowViewModel(store);
        first.FacialFpp.UseProjectionCapture = true;
        first.FacialFpp.ProjectionCaptureLabel =
            "manual runtime sample";
        first.FacialFpp.SceneCaptureFieldOfView = 74.5;
        first.FacialFpp.SceneCaptureAspectRatio = 2.0;
        first.FacialFpp.SceneCaptureNearPlane = 0.025;
        first.FacialFpp.HandsCaptureFieldOfView = 81.0;
        first.FacialFpp.HandsCaptureFieldOfViewAxis =
            Dl1ProjectionFovAxis.Horizontal;
        first.FacialFpp.HandsCaptureAspectRatio = 2.0;
        first.FacialFpp.HandsCaptureNearPlane = 0.0125;

        first.StoreFppProjectionCaptureCommand.Execute(null);

        Assert.True(
            first.CurrentProject.Dl1Settings
                .UseFppProjectionCapture);
        Assert.NotNull(
            first.CurrentProject.Dl1Settings
                .FppProjectionCapture);
        WorkspaceSnapshot snapshot = first.CreateSnapshot();
        await using var restored = new MainWindowViewModel(store);
        restored.RestoreSnapshot(snapshot);
        Assert.True(restored.FacialFpp.UseProjectionCapture);
        Assert.Equal(
            74.5,
            restored.FacialFpp.SceneCaptureFieldOfView);
        Assert.Equal(
            Dl1ProjectionFovAxis.Horizontal,
            restored.FacialFpp.HandsCaptureFieldOfViewAxis);
        Assert.Contains(
            "authoring evidence",
            restored.FacialFpp.ProjectionCaptureStatus,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompleteCaptureFailsClosedAndIsNotStored()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var store = new JsonWorkspaceStateStore(
            Path.Combine(_temporaryDirectory, "recovery.json"));
        await using var viewModel = new MainWindowViewModel(store);
        viewModel.FacialFpp.UseProjectionCapture = true;
        viewModel.FacialFpp.SceneCaptureFieldOfView = 70.0;

        viewModel.StoreFppProjectionCaptureCommand.Execute(null);

        Assert.False(
            viewModel.CurrentProject.Dl1Settings
                .UseFppProjectionCapture);
        Assert.Null(
            viewModel.CurrentProject.Dl1Settings
                .FppProjectionCapture);
        Assert.Contains(
            "incomplete or invalid",
            viewModel.FacialFpp.ProjectionCaptureStatus,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FppPreviewAlwaysSuppliesExplicitEditorBodyBasis()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var store = new JsonWorkspaceStateStore(
            Path.Combine(_temporaryDirectory, "body-basis-recovery.json"));
        await using var viewModel = new MainWindowViewModel(store);
        viewModel.ActiveWorkspaceMode = "FPP";
        viewModel.FacialFpp.UseProjectionCapture = false;

        Assert.True(
            viewModel.FacialFpp.EnableHSpineBasisCorrection);
        Assert.False(
            viewModel.FacialFpp.EnableHeadPositionCorrection);
        Assert.Contains(
            Dl1PreviewStageIds.FppHSpineBasisCorrection,
            viewModel.ActivePreviewProfile.ProceduralToggles);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHeadPositionCorrection,
            viewModel.ActivePreviewProfile.ProceduralToggles);
        Assert.DoesNotContain(
            Dl1PreviewStageIds.FppHeadSpineCorrection,
            viewModel.ActivePreviewProfile.ProceduralToggles);
        Dl1PreviewInputs inputs = viewModel.CreateDl1PreviewInputs(
            viewModel.ActivePreviewProfile,
            EvaluationPurpose.Preview);

        Assert.Null(inputs.FppProjection);
        Dl1FppBodyCorrectionSnapshot correction = Assert.IsType<
            Dl1FppBodyCorrectionSnapshot>(
            inputs.FppBodyCorrection);
        Assert.Equal(Vector3D.UnitY, correction.WorldUp);
        Assert.Equal(-Vector3D.UnitX, correction.ModelLeft);
        Assert.Equal(-Vector3D.UnitZ, correction.ModelForward);
        Assert.False(correction.VehicleControllerActive);
        Assert.Null(
            viewModel.CreateDl1PreviewInputs(
                    viewModel.ActivePreviewProfile,
                    EvaluationPurpose.Export)
                .FppBodyCorrection);
    }

    [Fact]
    public async Task MovieCameraCaptureIsStoredUsedAndRoutedToTargetViewport()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var store = new JsonWorkspaceStateStore(
            Path.Combine(_temporaryDirectory, "movie-recovery.json"));
        await using var viewModel = new MainWindowViewModel(store);
        viewModel.ActiveWorkspaceMode = "Cutscene";
        viewModel.FacialFpp.UseMovieReferenceCameraCapture = true;
        viewModel.FacialFpp.MovieReferenceCameraCaptureLabel =
            "external IBaseCamera sample";
        viewModel.FacialFpp.MovieCameraPositionX = 4.0;
        viewModel.FacialFpp.MovieCameraPositionY = 2.5;
        viewModel.FacialFpp.MovieCameraPositionZ = -7.0;
        viewModel.FacialFpp.MovieCameraRotationX = 0.0;
        viewModel.FacialFpp.MovieCameraRotationY = 0.0;
        viewModel.FacialFpp.MovieCameraRotationZ = 0.0;
        viewModel.FacialFpp.MovieCameraRotationW = 1.0;
        viewModel.FacialFpp.MovieCameraVerticalFieldOfView = 68.0;
        viewModel.FacialFpp.MovieCameraAspectRatio = 2.0;
        viewModel.FacialFpp.MovieCameraNearPlane = 0.03;
        viewModel.FacialFpp.MovieCameraFarPlane = 750.0;

        viewModel.StoreMovieReferenceCameraCaptureCommand.Execute(null);

        Assert.True(
            viewModel.CurrentProject.Dl1Settings
                .UseMovieReferenceCameraCapture);
        Assert.NotNull(
            viewModel.CurrentProject.Dl1Settings
                .MovieReferenceCameraCapture);
        WorkspaceSnapshot workspaceSnapshot =
            viewModel.CreateSnapshot();
        await using var restored = new MainWindowViewModel(store);
        restored.RestoreSnapshot(workspaceSnapshot);
        Assert.True(
            restored.FacialFpp.UseMovieReferenceCameraCapture);
        Assert.Equal(
            68.0,
            restored.FacialFpp.MovieCameraVerticalFieldOfView);
        Dl1PreviewInputs inputs = restored.CreateDl1PreviewInputs(
            PreviewProfile.MovieAuthoring,
            EvaluationPurpose.Preview);
        Dl1MovieReferenceCameraSnapshot snapshot = Assert.IsType<
            Dl1MovieReferenceCameraSnapshot>(
            inputs.MovieReferenceCamera);
        var rig = new RigDefinition(
            "movie-camera-test",
            "Movie camera test",
            [
                new BoneDefinition(
                    0,
                    "Root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root),
            ]);
        SkeletonPose pose = rig.CreateBindPose();
        var evaluatedCamera = new EvaluatedCamera(
            snapshot.WorldTransform,
            snapshot.Lens,
            false,
            EvaluatedCameraSource.Dl1MovieReferenceCamera);
        var frame = new EvaluationFrame(
            0,
            pose,
            pose,
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            PreviewProfile.MovieAuthoring,
            evaluatedCamera,
            [],
            [],
            null,
            [],
            [
                new Dl1PreviewStageReport(
                    Dl1PreviewStageIds.MovieReferenceCamera,
                    true,
                    Dl1PreviewStageStatus.Applied,
                    "Using external movie camera."),
            ]);

        restored.ApplyEvaluatedPreviewCamera(frame);

        RenderCamera rendered =
            restored.TargetViewport.SceneSource
                .CaptureFrame()
                .Camera;
        Assert.Equal(
            new Vector3(4.0f, 2.5f, -7.0f),
            rendered.Eye);
        Assert.Equal(68.0f, rendered.VerticalFieldOfViewDegrees);
        Assert.Equal(2.0f, rendered.ProjectionAspectRatio);
        Assert.Null(
            restored.TargetViewport.SceneSource
                .CaptureFrame()
                .FppProjectionState);
        Assert.Contains(
            "external DL1 movie IBaseCamera",
            restored.FacialFpp.PreviewStatus,
            StringComparison.Ordinal);
    }

    private static Dl1FppProjectionCapture CreateCapture() =>
        new()
        {
            CaptureLabel = "win-1.55 test capture",
            SceneVerticalFieldOfViewDegrees = 72.0,
            SceneAspectRatio = 16.0 / 9.0,
            SceneNearClipMeters = 0.025,
            HandsFieldOfViewDegrees = 78.0,
            HandsFieldOfViewAxis =
                Dl1ProjectionFovAxis.Horizontal,
            HandsAspectRatio = 16.0 / 9.0,
            HandsNearClipMeters = 0.0125,
        };

    private static Dl1MovieReferenceCameraCapture
        CreateMovieCapture() =>
        new()
        {
            CaptureLabel = "external IBaseCamera capture",
            WorldTransform = new TransformTRS(
                new Vector3D(4.0, 2.5, -7.0),
                new QuaternionD(
                    0.0,
                    0.25,
                    0.0,
                    Math.Sqrt(1.0 - (0.25 * 0.25))),
                Vector3D.One),
            Lens = new CameraLens(
                68.0,
                2.0,
                0.03,
                750.0),
        };

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

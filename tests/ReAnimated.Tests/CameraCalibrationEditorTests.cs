using ReAnimated.App.ViewModels;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class CameraCalibrationEditorTests
{
    private static RigConformanceWizardViewModel Editor() => new(
        (_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("test", "Synthetic camera review")), static _ => { });

    [Fact]
    public async Task PreviewRequiresReviewAndChangingTheDraftInvalidatesApply()
    {
        var model = CameraHelperCalibrationTests.Source();
        var editor = Editor();
        editor.SetModel(model);
        editor.SelectedCameraNode = editor.CameraNodes.Single(n => n.Name == "EyeCamera");
        editor.CameraOffsetZ = .02;
        await editor.PreviewCameraCalibrationCommand.ExecuteAsync(null);
        Assert.True(editor.HasCameraCalibrationPreview, editor.CameraStatus);
        Assert.False(editor.CanApplyCameraCalibration);
        editor.CameraReviewed = true;
        Assert.True(editor.CanApplyCameraCalibration);
        editor.CameraOffsetZ = .03;
        Assert.False(editor.HasCameraCalibrationPreview);
        Assert.False(editor.CanApplyCameraCalibration);
        await editor.PreviewCameraCalibrationCommand.ExecuteAsync(null);
        editor.CameraReviewed = true;
        FbxModelAuthoringImportResult? applied = null;
        editor.CameraModelApplyRequested += (_, e) => { Assert.Same(model, e.Source); applied = e.Result; };
        editor.ApplyCameraCalibrationCommand.Execute(null);
        Assert.NotNull(applied);
        Assert.False(editor.CanApplyCameraCalibration);
    }

    [Fact]
    public async Task EditingWhilePreviewRunsCancelsWithoutLeavingTheEditorBusy()
    {
        var editor = Editor();
        editor.SetModel(CameraHelperCalibrationTests.Source());
        editor.SelectedCameraNode = editor.CameraNodes.Single(n => n.Name == "EyeCamera");
        editor.CameraOffsetZ = .02;
        Task work = editor.PreviewCameraCalibrationCommand.ExecuteAsync(null);
        editor.CameraOffsetZ = .04;
        await work;
        Assert.False(editor.IsBusy);
        Assert.False(editor.HasCameraCalibrationPreview);
        Assert.True(editor.CanPreviewCameraCalibration);
    }
}

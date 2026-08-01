using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.DL1.Assets.Catalog;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private Dl1MeshPreviewPayload? _blenderExportPayload;
    private RetailAssetRecord? _blenderExportRetailAsset;
    private BlenderExecutableResolver? _blenderExecutableResolver;
    private IBlenderFbxExportService? _blenderFbxExportService;
    private AsyncRelayCommand? _exportSelectedMeshToBlenderFbxCommand;
    private RelayCommand? _configureBlenderCommand;
    private JobViewModel? _blenderExportJob;
    private string _blenderExportStatus =
        "Select a decoded skinned retail mesh to create a Blender handoff.";

    public AsyncRelayCommand ExportSelectedMeshToBlenderFbxCommand =>
        _exportSelectedMeshToBlenderFbxCommand ??=
            new AsyncRelayCommand(
                ExportSelectedMeshToBlenderFbxAsync,
                CanExportSelectedMeshToBlenderFbx);

    public RelayCommand ConfigureBlenderCommand =>
        _configureBlenderCommand ??=
            new RelayCommand(
                ConfigureBlender,
                () => _blenderExportJob is null);

    public string BlenderExportStatus
    {
        get => _blenderExportStatus;
        private set => SetProperty(
            ref _blenderExportStatus,
            value);
    }

    private BlenderExecutableResolver BlenderExecutableResolver =>
        _blenderExecutableResolver ??=
            BlenderExecutableResolver.CreateDefault();

    private IBlenderFbxExportService BlenderFbxExportService =>
        _blenderFbxExportService ??=
            new BlenderFbxExportService();

    private bool CanExportSelectedMeshToBlenderFbx() =>
        !IsBusy &&
        _blenderExportJob is null &&
        _blenderExportPayload is
        {
            Source.Rig: not null,
            Skeleton: not null,
        } payload &&
        payload.Meshes.Count > 0 &&
        _blenderExportRetailAsset is not null;

    private void SetBlenderExportTarget(
        Dl1MeshPreviewPayload payload,
        RetailAssetRecord retailAsset)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(retailAsset);
        _blenderExportPayload = payload;
        _blenderExportRetailAsset = retailAsset;
        BlenderExportStatus = payload.Source.Rig is null
            ? "The selected retail mesh is static and has no animation rig."
            : $"Ready: {retailAsset.DisplayName} / {payload.Source.Rig.BoneCount:N0} bones / {payload.Meshes.Count:N0} mesh parts";
        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
    }

    private void ClearBlenderExportTarget()
    {
        _blenderExportPayload = null;
        _blenderExportRetailAsset = null;
        if (_blenderExportJob is null)
        {
            BlenderExportStatus =
                "Select a decoded skinned retail mesh to create a Blender handoff.";
        }

        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
    }

    private void ConfigureBlender()
    {
        string? current =
            BlenderExecutableResolver.LoadConfiguredPath();
        string? selected =
            _fileDialogs.ShowOpenBlenderExecutableDialog(current);
        if (selected is null)
        {
            return;
        }

        try
        {
            BlenderExecutableResolver.SaveConfiguredPath(selected);
            BlenderExportStatus =
                $"Blender configured: {selected}";
            AddDiagnostic(
                "Info",
                "Blender FBX",
                "Optional Blender executable configured",
                selected);
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            BlenderExportStatus =
                "The selected Blender executable could not be saved.";
            AddDiagnostic(
                "Error",
                "Blender FBX",
                "Could not configure Blender",
                exception.Message);
        }
    }

    private async Task ExportSelectedMeshToBlenderFbxAsync()
    {
        if (_blenderExportPayload is not
            {
                Source.Rig: not null,
                Skeleton: not null,
            } payload ||
            _blenderExportRetailAsset is not { } retailAsset)
        {
            return;
        }

        IReadOnlyList<string> anm2Paths =
            _fileDialogs.ShowOpenAnm2ForBlenderDialog(
                _pendingAnm2SourcePath ??
                ProjectPath);
        if (anm2Paths.Count == 0)
        {
            return;
        }

        string? blenderPath =
            BlenderExecutableResolver.Resolve();
        if (blenderPath is null)
        {
            blenderPath =
                _fileDialogs.ShowOpenBlenderExecutableDialog(
                    ProjectPath);
            if (blenderPath is null)
            {
                BlenderExportStatus =
                    "Blender is optional, but blender.exe is required for FBX handoff.";
                return;
            }

            try
            {
                BlenderExecutableResolver.SaveConfiguredPath(
                    blenderPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or IOException
                or UnauthorizedAccessException)
            {
                AddDiagnostic(
                    "Error",
                    "Blender FBX",
                    "The selected Blender executable is invalid",
                    exception.Message);
                return;
            }

            blenderPath =
                BlenderExecutableResolver.LoadConfiguredPath();
        }

        string? outputPath =
            _fileDialogs.ShowSaveBlenderFbxDialog(
                retailAsset.DisplayName,
                ProjectPath);
        if (outputPath is null ||
            blenderPath is null)
        {
            return;
        }

        if (!_fileDialogs.ConfirmRetailFbxExport(
                retailAsset.DisplayName,
                anm2Paths.Count))
        {
            BlenderExportStatus =
                "Local retail-asset export canceled.";
            return;
        }

        JobViewModel job = AddJob(
            $"Blender handoff: {retailAsset.DisplayName}",
            "Preparing",
            "Reading selected ANM2 clips");
        _blenderExportJob = job;
        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
        ConfigureBlenderCommand.NotifyCanExecuteChanged();
        BlenderExportStatus =
            $"Exporting {anm2Paths.Count:N0} Action(s) with decoded base color...";
        try
        {
            var request = new BlenderFbxExportRequest(
                blenderPath,
                outputPath,
                new BlenderFbxAssetIdentity(
                    retailAsset.Id.StableKey,
                    retailAsset.Source.ProviderId,
                    retailAsset.DisplayName,
                    payload.ResourceSha256
                        ?? throw new InvalidDataException(
                            "The decoded retail mesh has no content fingerprint.")),
                payload.Source.Rig,
                payload.Meshes,
                anm2Paths);
            var progress =
                new Progress<BlenderFbxExportProgress>(value =>
                {
                    job.Stage = value.Stage;
                    job.Progress = value.Percent;
                    job.State = value.Detail;
                    BlenderExportStatus =
                        $"{value.Stage}: {value.Detail}";
                });
            BlenderFbxExportResult result =
                await BlenderFbxExportService.ExportAsync(
                    request,
                    progress,
                    job.CancellationToken);
            job.Progress = 100.0;
            job.Complete("Complete");
            BlenderExportStatus =
                $"Created {result.AnimationStacks.Count:N0} Blender Action(s): {result.OutputFbxPath}";
            AddDiagnostic(
                "Info",
                "Blender FBX",
                $"Created a local retail-mesh FBX with {result.AnimationStacks.Count:N0} Action(s)",
                $"{result.OutputFbxPath}. Base-color DDS textures and {result.HandoffManifestPath} were written beside it. Do not redistribute this retail-data bundle.");
            foreach (string warning in result.Warnings)
            {
                AddDiagnostic(
                    "Warning",
                    "Blender FBX",
                    warning,
                    null);
            }
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            BlenderExportStatus =
                "Blender FBX handoff canceled; temporary output was cleaned up.";
        }
        catch (Exception exception)
        {
            job.Complete("Failed");
            BlenderExportStatus =
                "Blender FBX handoff failed. See Diagnostics.";
            AddDiagnostic(
                "Error",
                "Blender FBX",
                "Could not create the local retail-mesh FBX handoff",
                exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_blenderExportJob, job))
            {
                _blenderExportJob = null;
            }

            ExportSelectedMeshToBlenderFbxCommand
                .NotifyCanExecuteChanged();
            ConfigureBlenderCommand.NotifyCanExecuteChanged();
        }
    }
}

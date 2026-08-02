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
    private AsyncRelayCommand? _exportSelectedBrowserMeshToFbxCommand;
    private RelayCommand? _configureBlenderCommand;
    private JobViewModel? _blenderExportJob;
    private string _blenderExportStatus =
        "Select a decoded skinned retail mesh to create a Blender handoff.";

    public AsyncRelayCommand ExportSelectedMeshToBlenderFbxCommand =>
        _exportSelectedMeshToBlenderFbxCommand ??=
            new AsyncRelayCommand(
                ExportSelectedMeshToBlenderFbxAsync,
                CanExportSelectedMeshToBlenderFbx);

    public AsyncRelayCommand ExportSelectedBrowserMeshToFbxCommand =>
        _exportSelectedBrowserMeshToFbxCommand ??=
            new AsyncRelayCommand(
                ExportSelectedBrowserMeshToFbxAsync,
                CanExportSelectedBrowserMeshToFbx);

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

    private bool CanExportSelectedBrowserMeshToFbx() =>
        !IsBusy &&
        _blenderExportJob is null &&
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        };

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
        ExportSelectedBrowserMeshToFbxCommand
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
        ExportSelectedBrowserMeshToFbxCommand
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

    private string? ResolveBlenderForFbxExport()
    {
        string? blenderPath = BlenderExecutableResolver.Resolve();
        if (blenderPath is not null)
        {
            return blenderPath;
        }

        blenderPath = _fileDialogs.ShowOpenBlenderExecutableDialog(
            ProjectPath);
        if (blenderPath is null)
        {
            BlenderExportStatus =
                "Blender is optional, but blender.exe is required for FBX export.";
            return null;
        }

        try
        {
            BlenderExecutableResolver.SaveConfiguredPath(blenderPath);
            return BlenderExecutableResolver.LoadConfiguredPath();
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
            return null;
        }
    }

    private async Task ExportSelectedBrowserMeshToFbxAsync()
    {
        if (AssetBrowser.SelectedAsset is not
            {
                Kind: AssetKind.Mesh,
                RetailAsset: not null,
            } selected)
        {
            return;
        }

        CancelAutomaticAssetPreview();
        string? blenderPath = ResolveBlenderForFbxExport();
        if (blenderPath is null)
        {
            return;
        }

        string? outputPath = _fileDialogs.ShowSaveRetailMeshFbxDialog(
            selected.Name,
            ProjectPath);
        if (outputPath is null)
        {
            return;
        }

        if (!_fileDialogs.ConfirmRetailMeshFbxExport(selected.Name))
        {
            BlenderExportStatus = "Local retail-mesh export canceled.";
            return;
        }

        JobViewModel job = BeginExclusiveAssetDecode(
            $"Export FBX: {selected.Name}",
            "Decoding retail mesh");
        _blenderExportJob = job;
        IsBusy = true;
        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
        ExportSelectedBrowserMeshToFbxCommand
            .NotifyCanExecuteChanged();
        ConfigureBlenderCommand.NotifyCanExecuteChanged();
        BlenderExportStatus = "Decoding retail mesh for FBX export...";
        try
        {
            DecodedRetailModelSession model =
                await DecodeRetailModelAsync(selected, job);
            if (_disposed ||
                !ReferenceEquals(_assetDecodeJob, job))
            {
                job.Complete("Superseded");
                return;
            }

            if (model.Payload.Meshes.Count == 0)
            {
                throw new InvalidDataException(
                    "The selected retail mesh could not be decoded into complete exportable geometry.");
            }

            string rigSummary = model.Payload.Source.Rig is { } rig
                ? $"{rig.BoneCount:N0} bones"
                : "static mesh";
            BlenderExportStatus =
                $"Exporting {model.Payload.Meshes.Count:N0} mesh part(s) / {rigSummary} with embedded textures...";
            var request = new BlenderFbxExportRequest(
                blenderPath,
                outputPath,
                new BlenderFbxAssetIdentity(
                    model.RetailAsset.Id.StableKey,
                    model.RetailAsset.Source.ProviderId,
                    model.RetailAsset.DisplayName,
                    model.Payload.ResourceSha256
                        ?? throw new InvalidDataException(
                            "The decoded retail mesh has no content fingerprint.")),
                model.Payload.Source.Rig,
                model.Payload.Meshes,
                [])
            {
                EmbedTextures = true,
            };
            var progress = new Progress<BlenderFbxExportProgress>(
                value =>
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
            string textureSummary = result.EmbeddedTextureFileNames.Count == 0
                ? "no decoded base-color textures"
                : $"{result.EmbeddedTextureFileNames.Count:N0} embedded base-color texture(s)";
            BlenderExportStatus =
                $"Created self-contained FBX: {result.OutputFbxPath}";
            AddDiagnostic(
                "Info",
                "Blender FBX",
                $"Created local mesh FBX with {result.BoneCount:N0} bone(s), {result.MeshCount:N0} mesh part(s), and {textureSummary}",
                $"{result.OutputFbxPath}. The companion manifest records provenance only; no loose DDS texture dependencies were written. Do not redistribute this retail-data export.");
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
                "Retail-mesh FBX export canceled; temporary output was cleaned up.";
        }
        catch (Exception exception)
        {
            job.Complete("Failed");
            BlenderExportStatus =
                "Retail-mesh FBX export failed. See Diagnostics.";
            AddDiagnostic(
                "Error",
                "Blender FBX",
                "Could not create the local self-contained retail-mesh FBX",
                exception.Message);
        }
        finally
        {
            bool ownsActiveDecode = ReferenceEquals(
                _assetDecodeJob,
                job);
            if (ownsActiveDecode)
            {
                _assetDecodeJob = null;
            }

            if (ReferenceEquals(_blenderExportJob, job))
            {
                _blenderExportJob = null;
            }

            if (ownsActiveDecode)
            {
                IsBusy = false;
            }

            ExportSelectedMeshToBlenderFbxCommand
                .NotifyCanExecuteChanged();
            ExportSelectedBrowserMeshToFbxCommand
                .NotifyCanExecuteChanged();
            ConfigureBlenderCommand.NotifyCanExecuteChanged();
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

        string? blenderPath = ResolveBlenderForFbxExport();

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
        ExportSelectedBrowserMeshToFbxCommand
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
            ExportSelectedBrowserMeshToFbxCommand
                .NotifyCanExecuteChanged();
            ConfigureBlenderCommand.NotifyCanExecuteChanged();
        }
    }
}

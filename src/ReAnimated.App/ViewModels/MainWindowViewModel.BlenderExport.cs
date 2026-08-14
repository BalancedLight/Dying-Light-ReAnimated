using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.Evaluation;

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
        "Load an export-ready animation variant and target model to create a self-contained FBX.";

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
        _targetRig is not null &&
        _targetBaseMeshes.Length > 0 &&
        _targetProjectAsset is not null &&
        GetActiveAnimation() is not null &&
        CanExportAnimation();

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
        RefreshActiveVariantBlenderStatus();
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
            RefreshActiveVariantBlenderStatus();
        }

        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
        ExportSelectedBrowserMeshToFbxCommand
            .NotifyCanExecuteChanged();
    }

    private void RefreshActiveVariantBlenderStatus()
    {
        if (_blenderExportJob is not null)
        {
            return;
        }

        ProjectAnimation? animation = GetActiveAnimation();
        if (animation is not null &&
            _targetRig is { } rig &&
            _targetBaseMeshes.Length > 0 &&
            _targetProjectAsset is not null)
        {
            BlenderExportStatus =
                $"Active variant: {animation.Name} / {ActiveTargetModelLabel} / {rig.BoneCount:N0} rig nodes / {_targetBaseMeshes.Length:N0} mesh parts. Export remains fail-closed until body and facial reviews are current.";
            return;
        }

        BlenderExportStatus =
            "Load an export-ready animation variant and target model to create a self-contained FBX.";
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
        if (_targetRig is not { } targetRig ||
            _targetBaseMeshes.Length == 0 ||
            _targetProjectAsset is not { } targetAsset ||
            GetActiveAnimation() is not { } animation ||
            !CanExportAnimation())
        {
            return;
        }

        if (animation.TargetAssetId != targetAsset.Id ||
            !string.Equals(
                animation.TargetRigSignature,
                RigSignature.Compute(targetRig),
                StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic(
                "Error",
                "Blender FBX",
                "Active variant target identity changed",
                "Reload the exact project target before exporting FBX.");
            return;
        }

        string? blenderPath = ResolveBlenderForFbxExport();
        ProjectModelEntry? model = _project.Models.FirstOrDefault(
            candidate => candidate.AssetId == targetAsset.Id);
        string targetName = model?.Name ??
            targetAsset.RetailIdentity?.ResourceName ??
            targetAsset.ResourceId ??
            Path.GetFileNameWithoutExtension(targetAsset.RelativePath);
        string? outputPath =
            _fileDialogs.ShowSaveBlenderFbxDialog(
                $"{targetName}_{animation.Name}",
                ProjectPath);
        if (outputPath is null ||
            blenderPath is null)
        {
            return;
        }

        bool retailTarget = targetAsset.Kind ==
            ProjectAssetKind.RetailGameResource;
        if (!_fileDialogs.ConfirmActiveVariantFbxExport(
                targetName,
                animation.Name,
                retailTarget))
        {
            BlenderExportStatus =
                "Active-variant FBX export canceled.";
            return;
        }

        JobViewModel job = AddJob(
            $"Blender handoff: {animation.Name}",
            "Preparing",
            "Evaluating the active project variant");
        _blenderExportJob = job;
        ExportSelectedMeshToBlenderFbxCommand
            .NotifyCanExecuteChanged();
        ExportSelectedBrowserMeshToFbxCommand
            .NotifyCanExecuteChanged();
        ConfigureBlenderCommand.NotifyCanExecuteChanged();
        BlenderExportStatus =
            $"Evaluating {animation.FrameCount:N0} authored frame(s) for the exact target rig...";
        try
        {
            string sourceFingerprint = ResolveActiveBlenderSourceFingerprint(
                animation);
            EvaluationRequest template = CreateEvaluationRequest(
                animation,
                0,
                PreviewProfile.RawAuthoring,
                PlaybackMode.Clamp,
                EvaluationPurpose.Export);
            BlenderFbxEvaluatedClip evaluatedClip = await Task.Run(
                () => BlenderFbxActiveVariantEvaluator.Evaluate(
                    animation,
                    ResolveActiveBlenderSourceName(animation),
                    sourceFingerprint,
                    template,
                    job.CancellationToken),
                job.CancellationToken);
            string targetFingerprint = targetAsset.ContentSha256 ??
                throw new InvalidOperationException(
                    "The active target model has no content fingerprint.");
            var request = new BlenderFbxExportRequest(
                blenderPath,
                outputPath,
                new BlenderFbxAssetIdentity(
                    targetAsset.RetailIdentity?.InstallFingerprint ??
                        targetAsset.ResourceId ??
                        targetAsset.Id.ToString("N"),
                    targetAsset.RetailIdentity?.ProviderId ??
                        "project-custom-model",
                    targetName,
                    targetFingerprint),
                targetRig,
                _targetBaseMeshes,
                [])
            {
                EmbedTextures = true,
                EvaluatedClips = [evaluatedClip],
                Provenance = retailTarget
                    ? BlenderFbxExportProvenance.RetailLocal
                    : BlenderFbxExportProvenance.CustomUserOwned,
            };
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
                $"Created a self-contained target-model FBX with the evaluated active variant '{animation.Name}'",
                retailTarget
                    ? $"{result.OutputFbxPath}. The companion manifest is {result.HandoffManifestPath}. This contains retail model bytes; do not redistribute it."
                    : $"{result.OutputFbxPath}. The companion manifest is {result.HandoffManifestPath}.");
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
                "Could not create the active-variant self-contained FBX",
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

    private string ResolveActiveBlenderSourceFingerprint(
        ProjectAnimation animation)
    {
        ProjectAnimationVariant? variant = _project.AnimationVariants
            .FirstOrDefault(candidate => candidate.Id == animation.Id);
        ProjectAnimationSource? source = variant is null
            ? null
            : _project.AnimationSources.FirstOrDefault(candidate =>
                candidate.Id == variant.SourceId);
        string? fingerprint = source?.EmbeddedCustomModelStack
            ?.StackFingerprint ??
            FindProjectAsset(animation.SourceAssetId)?.ContentSha256;
        return fingerprint ?? throw new InvalidOperationException(
            "The active animation source has no immutable content fingerprint.");
    }

    private string ResolveActiveBlenderSourceName(
        ProjectAnimation animation)
    {
        ProjectAnimationVariant? variant = _project.AnimationVariants
            .FirstOrDefault(candidate => candidate.Id == animation.Id);
        ProjectAnimationSource? source = variant is null
            ? null
            : _project.AnimationSources.FirstOrDefault(candidate =>
                candidate.Id == variant.SourceId);
        return source?.Name ??
            Path.GetFileName(
                FindProjectAsset(animation.SourceAssetId)?.RelativePath) ??
            animation.Name;
    }
}

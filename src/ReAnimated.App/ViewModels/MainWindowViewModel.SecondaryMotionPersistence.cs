using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

internal sealed record SecondaryMotionSaveFlushResult(
    DlraProject Project,
    ImmutableArray<PendingSecondaryMotionEdit> PersistedEdits);

public sealed partial class MainWindowViewModel
{
    internal bool HasPendingSecondaryMotionEdits =>
        _secondaryModelStates.HasPendingEdits(SecondaryMotion.Definition);

    internal ImmutableArray<PendingSecondaryMotionEdit> CaptureSecondaryMotionRecoveryEdits() =>
        _secondaryModelStates.CapturePendingEdits(SecondaryMotion.Definition);

    internal void RestoreSecondaryMotionRecoveryEdits(
        IEnumerable<PendingSecondaryMotionEdit> edits)
    {
        _secondaryModelStates.RestorePendingEdits(edits);
        _secondaryDocument = null;
        OnPropertyChanged(nameof(HasAppControlUnsavedChanges));
    }

    internal void NotifySecondaryMotionPendingStateChanged() =>
        OnPropertyChanged(nameof(HasAppControlUnsavedChanges));

    internal async Task<SecondaryMotionSaveFlushResult> PrepareSecondaryMotionProjectSaveAsync(
        DlraProject project,
        string? sourceProjectPath,
        string? materializeProjectPath,
        bool materializeAssets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ImmutableArray<PendingSecondaryMotionEdit> edits =
            _secondaryModelStates.CapturePendingEdits(SecondaryMotion.Definition);
        if (edits.IsEmpty) return new(project, []);

        DlraProject updated = project;
        var persisted = ImmutableArray.CreateBuilder<PendingSecondaryMotionEdit>();
        foreach (PendingSecondaryMotionEdit edit in edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            edit.Validate();
            if (edit.ProjectId != updated.ProjectId)
                throw new InvalidDataException("Pending secondary-motion edits belong to a different project.");

            ProjectModelEntry model = FindCustomModelEntry(updated, edit.ModelId)
                ?? throw new InvalidDataException("A pending secondary-motion model is no longer present in the project.");
            ProjectAssetReference asset = updated.Assets.SingleOrDefault(candidate =>
                    candidate.Id == model.AssetId && candidate.Kind == ProjectAssetKind.CustomModelSource)
                ?? throw new InvalidDataException("A pending secondary-motion model package is missing from the project.");
            string packagePath = await ResolveSecondaryMotionPackagePathAsync(
                asset, sourceProjectPath, cancellationToken).ConfigureAwait(true);
            CustomModelPackage package = await Task.Run(
                () => CustomModelPackageSerializer.Load(packagePath), cancellationToken).ConfigureAwait(true);
            if (package.Document.ModelId != edit.ModelId ||
                !package.Document.Source.ContentSha256.Equals(edit.SourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The pending secondary-motion edit does not match the project's model source.");

            bool alreadyPersisted = DefinitionsEqual(package.Document.SecondaryMotion, edit.Definition);
            if (!string.Equals(asset.ContentSha256, edit.PackageHash, StringComparison.OrdinalIgnoreCase))
            {
                if (alreadyPersisted)
                {
                    persisted.Add(edit);
                    continue;
                }
                if (!CustomModelContractSignatures.ComputeRig(package.Document.CreateEffectiveBones())
                        .Equals(edit.RigSignature, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The model source or effective rig changed after these secondary-motion edits were made. Reopen the current model and review the setup before saving.");
            }
            if (!alreadyPersisted &&
                !PendingSecondaryMotionEdit.ComputeDefinitionSha256(package.Document.SecondaryMotion)
                    .Equals(edit.BaselineDefinitionSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The project's secondary-motion definition changed independently. Reopen the current model and review both setups before saving.");
            if (alreadyPersisted)
            {
                persisted.Add(edit);
                continue;
            }

            edit.Definition.Validate(package.Document.CreateEffectiveBones().Select(static bone => bone.Name));
            CustomModelPackage changed = package with
            {
                Document = package.Document with
                {
                    SecondaryMotion = edit.Definition,
                    LastBuildReceipt = null,
                },
            };
            changed.Document.Validate();
            FbxModelAuthoringImportResult imported = await Task.Run(
                () => FbxModelAuthoringImporter.ImportPackage(changed, cancellationToken), cancellationToken)
                .ConfigureAwait(true);
            ModelsWorkspacePersistencePayload payload = CreateSecondaryMotionPersistencePayload(
                imported,
                model.Name,
                updated.ModelsWorkspace,
                asset.Id);

            ProjectModelsWorkspaceState? priorWorkspace = updated.ModelsWorkspace;
            DlraProject integrated = await IntegrateModelsWorkspaceAsync(
                updated,
                payload,
                materializeProjectPath,
                materializeAssets,
                cancellationToken).ConfigureAwait(true);
            Guid nextAssetId = integrated.Models.Single(row => row.Id == model.Id).AssetId;
            ProjectModelEntry refreshedModel = integrated.Models.Single(row => row.Id == model.Id) with
            {
                Name = model.Name,
            };
            ProjectModelsWorkspaceState? preservedWorkspace = priorWorkspace;
            if (priorWorkspace?.PackageAssetId == asset.Id)
                preservedWorkspace = integrated.ModelsWorkspace;
            updated = integrated with
            {
                Models = integrated.Models.Select(row => row.Id == refreshedModel.Id ? refreshedModel : row).ToImmutableArray(),
                ModelsWorkspace = preservedWorkspace is null
                    ? null
                    : priorWorkspace?.PackageAssetId == asset.Id
                        ? preservedWorkspace with { PackageAssetId = nextAssetId }
                        : preservedWorkspace,
            };
            persisted.Add(edit);
        }

        updated.Validate();
        return new(updated, persisted.ToImmutable());
    }

    internal void CommitSecondaryMotionProjectSave(
        ImmutableArray<PendingSecondaryMotionEdit> completedEdits)
    {
        _secondaryModelStates.MarkPersisted(completedEdits, SecondaryMotion.Definition);
        OnPropertyChanged(nameof(HasAppControlUnsavedChanges));
    }

    private async Task<string> ResolveSecondaryMotionPackagePathAsync(
        ProjectAssetReference asset,
        string? sourceProjectPath,
        CancellationToken cancellationToken)
    {
        if (_pendingProjectAssets.TryGetValue(asset.Id, out PendingProjectAssetReceipt? receipt))
        {
            if (!string.Equals(asset.RelativePath, receipt.RelativePath, StringComparison.Ordinal) ||
                !string.Equals(asset.ContentSha256, receipt.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged model package differs from its project identity.");
            return await _pendingProjectAssetStore.ResolveAsync(receipt, cancellationToken).ConfigureAwait(true);
        }

        if (string.IsNullOrWhiteSpace(sourceProjectPath))
            throw new FileNotFoundException("The project model package has no recovery-staged copy or source project path.");
        string root = Path.GetDirectoryName(Path.GetFullPath(sourceProjectPath))
            ?? throw new InvalidOperationException("The source project has no parent directory.");
        string path = Path.GetFullPath(Path.Combine(root, asset.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The project model package path escapes its project directory.");
        if (!File.Exists(path)) throw new FileNotFoundException("The project model package is missing.", path);
        await using FileStream stream = File.OpenRead(path);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(true));
        if (!string.Equals(hash, asset.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The project model package failed its SHA-256 check.");
        return path;
    }

    private static ModelsWorkspacePersistencePayload CreateSecondaryMotionPersistencePayload(
        FbxModelAuthoringImportResult imported,
        string modelName,
        ProjectModelsWorkspaceState? workspace,
        Guid currentAssetId)
    {
        CustomModelPackage package = imported.Package;
        Dl1PreparedAuthoredRig? output = imported.Rig is null
            ? null
            : Dl1CustomModelRigPreparer.Prepare(imported);
        ImmutableArray<ModelsWorkspaceEmbeddedStackPayload> stacks = package.Document.AnimationClips
            .Select(clip => new ModelsWorkspaceEmbeddedStackPayload(
                clip,
                imported.AnimationClips.ContainsKey(clip.Id),
                imported.AnimationClips.TryGetValue(clip.Id, out AnimationClip? decoded) &&
                    MainWindowViewModel.AnimationContainsTemporalMovement([decoded])))
            .ToImmutableArray();
        return new(
            package.Document.ModelId,
            $"{Dl1SourceModelWriter.SanitizeName(modelName, 55)}.dlrmodel",
            CustomModelPackageSerializer.Serialize(package),
            imported.Rig is null ? null : RigSignature.Compute(imported.Rig),
            package.Document.RigSignature,
            imported.Rig is null ? null : AnimationSkeletonSignature.Compute(imported.Rig),
            output is null ? null : RigSignature.Compute(output.PreviewRig),
            output?.Contract.DescriptorFingerprint,
            package.Document.MorphSignature,
            package.Document.BuildSettings.AnimationScriptAlias,
            package.Document.Camera.ActivePreviewNodeName,
            package.Document.CreateEffectiveBones().Count(static bone =>
                bone.Kind == BoneKind.Camera && !bone.IsWeighted &&
                string.Equals(bone.Name, CustomModelHelperAuthoring.GameCameraName, StringComparison.Ordinal)),
            imported.Rig?.Id,
            stacks,
            workspace?.PackageAssetId == currentAssetId ? workspace.SelectedAnimationClipId : null,
            workspace?.PackageAssetId == currentAssetId ? workspace.PreviewMode : ProjectCustomModelPreviewMode.Dl1Output,
            workspace?.PackageAssetId == currentAssetId ? workspace.ShowMeshes : true,
            workspace?.PackageAssetId == currentAssetId ? workspace.ShowBones : true,
            workspace?.PackageAssetId == currentAssetId ? workspace.ShowHelpers : true,
            workspace?.PackageAssetId == currentAssetId ? workspace.ShowCameraHelpers : false,
            workspace?.PackageAssetId == currentAssetId ? workspace.ShowPropHelpers : true);
    }

    private static bool DefinitionsEqual(SecondaryMotionDefinition left, SecondaryMotionDefinition right) =>
        JsonSerializer.Serialize(left).AsSpan().SequenceEqual(JsonSerializer.Serialize(right));
}

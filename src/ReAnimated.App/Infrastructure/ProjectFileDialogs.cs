using System.Collections.Immutable;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;

namespace ReAnimated.App.Infrastructure;

public enum LocalAnm2SourceBindingDecision
{
    ConfirmSuggested,
    ChooseAnother,
    Cancel,
}

public enum DeveloperToolsDeploymentConflictDecision
{
    BackUpAndReplace,
    SkipSkippable,
    Cancel,
}

public enum CustomModelPreviewCameraDecision
{
    CreateEyeCamera,
    UseEditorOnlySelection,
    UseExistingEyeCamera,
    Cancel,
}

public enum OwningAnimationOpenDecision
{
    PlayOnExistingModel,
    AddAnotherTarget,
    Cancel,
}

public sealed record LocalAnm2ImportPreflight(
    string AnimationName,
    string SourceModelName,
    string SourceModelIdentity,
    string SourceModelFingerprint,
    int BodyDescriptorCount,
    int FacialDescriptorCount,
    int AuxiliaryDescriptorCount,
    int UnresolvedDescriptorCount,
    int AmbiguousDescriptorCount)
{
    public bool IsBlocked => AmbiguousDescriptorCount > 0;
}

public sealed record ExternalFbxAnimationStackSelection(
    ImmutableArray<long> StackObjectIds,
    FbxFacialSourceValueUnit FacialSourceValueUnit)
{
    public const int MaximumSelectedStacks = 256;

    public ExternalFbxAnimationStackSelection Validate()
    {
        if (StackObjectIds.IsDefaultOrEmpty ||
            StackObjectIds.Length > MaximumSelectedStacks ||
            StackObjectIds.Any(static id => id <= 0) ||
            StackObjectIds.Distinct().Count() !=
                StackObjectIds.Length ||
            FacialSourceValueUnit is not (
                FbxFacialSourceValueUnit.Normalized or
                FbxFacialSourceValueUnit.Percent))
        {
            throw new ArgumentException(
                $"An external FBX selection requires 1-{MaximumSelectedStacks} unique positive stack IDs and an explicit facial source unit.");
        }

        return this;
    }
}

public sealed record ExternalFbxTargetModelOption(
    Guid ModelId,
    string Name,
    string Source,
    string Contract,
    bool IsStatic,
    bool IsSelected);

public sealed record AnimationTargetModelOption(
    Guid ModelId,
    string Name,
    string Source,
    string Contract,
    string? UnavailableReason,
    bool IsAlreadyAssigned);

public sealed record AnimationTargetSelection(
    ImmutableArray<Guid> TargetModelIds)
{
    public const int MaximumSelectedTargetModels = 32;

    public AnimationTargetSelection Validate()
    {
        if (TargetModelIds.IsDefaultOrEmpty ||
            TargetModelIds.Length > MaximumSelectedTargetModels ||
            TargetModelIds.Any(static id => id == Guid.Empty) ||
            TargetModelIds.Distinct().Count() != TargetModelIds.Length)
        {
            throw new ArgumentException(
                $"An animation target selection requires 1-{MaximumSelectedTargetModels} unique project-model IDs.");
        }

        return this;
    }
}

public interface IProjectFileDialogService
{
    string? ShowOpenProjectDialog(string? initialPath);

    string? ShowOpenAnimationDialog(string? initialPath) => null;

    ExternalFbxAnimationStackSelection?
        SelectExternalFbxAnimationStacks(
            string sourceName,
            IReadOnlyList<FbxExternalAnimationStackDescriptor> stacks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(stacks);
        ImmutableArray<long> importable = stacks
            .Where(static stack => stack.CanImport)
            .Take(ExternalFbxAnimationStackSelection.MaximumSelectedStacks)
            .Select(static stack => stack.StackObjectId)
            .ToImmutableArray();
        return importable.IsEmpty
            ? null
            : new ExternalFbxAnimationStackSelection(
                importable,
                FbxFacialSourceValueUnit.Percent).Validate();
    }

    AnimationTargetSelection? SelectAnimationTargets(
        string sourceName,
        IReadOnlyList<AnimationTargetModelOption> targetModels) => null;

    AnimationLibraryAssignmentResult? EditAnimationLibraryAssignment(
        AnimationLibraryEditorRequest request) => null;

    OwningAnimationOpenDecision ConfirmOwningAnimationOpen(
        string animationName,
        string modelName) =>
        OwningAnimationOpenDecision.PlayOnExistingModel;

    LocalAnm2SourceBindingDecision ConfirmLocalAnm2SourceBinding(
        LocalAnm2ImportPreflight preflight) =>
        LocalAnm2SourceBindingDecision.Cancel;

    string? ShowOpenMimicAnimationDialog(string? initialPath) => null;

    string? ShowOpenFacialFbxDialog(string? initialPath) => null;

    string? ShowOpenFedDialog(string? initialPath) => null;

    IReadOnlyList<string> ShowOpenAnm2ForBlenderDialog(
        string? initialPath) => [];

    string? ShowOpenBlenderExecutableDialog(
        string? initialPath) => null;

    string? ShowSaveBlenderFbxDialog(
        string suggestedName,
        string? initialPath) => null;

    string? ShowSaveRetailMeshFbxDialog(
        string suggestedName,
        string? initialPath) => null;

    bool ConfirmRetailFbxExport(
        string assetName,
        int clipCount) => false;

    bool ConfirmActiveVariantFbxExport(
        string modelName,
        string animationName,
        bool containsRetailModelBytes) =>
        !containsRetailModelBytes;

    bool ConfirmRetailMeshFbxExport(string assetName) => false;

    /// <summary>
    /// Confirms removing a model from the project, counting what goes with it.
    /// Animation targets belong to the model, so they cannot outlive it.
    /// </summary>
    bool ConfirmProjectModelRemoval(
        string modelName,
        int animationTargetCount,
        int orphanedAnimationScriptCount) => false;

    /// <summary>
    /// Confirms renaming a type-322 animation script that more than one
    /// animation is assigned to. The name lives on the library, so the rename
    /// is not local to the row the author edited.
    /// </summary>
    bool ConfirmAnimationScriptRename(
        string currentResourceName,
        string proposedResourceName,
        int affectedAnimationCount) => false;

    string? ShowSelectExportDirectoryDialog(string? initialPath) => null;

    string? ShowOpenAnimationRpackDialog(string? initialPath) => null;

    /// <summary>
    /// Asks which bone should receive accumulated travel and heading, for a
    /// target rig that carries no 0xCCC3CDDF accumulator track of its own.
    /// Returning null refuses the policy rather than flattening the animation.
    /// </summary>
    string? SelectMotionAccumulatorBone(
        string targetRigId,
        System.Collections.Immutable.ImmutableArray<string> candidateBoneNames)
        => null;

    /// <summary>
    /// Picks the destination file for an animation RPack. The pack is a single
    /// self-contained file - it already carries its compiled type-322 scripts -
    /// so the author chooses the exact path rather than a parent folder.
    /// </summary>
    string? ShowSaveAnimationRpackDialog(
        string suggestedName,
        string? initialPath) => null;

    string? ShowSelectAdditionalRpackRootDialog(string? initialPath) => null;

    string? ShowOpenCustomModelFbxDialog(string? initialPath) => null;

    string? ShowOpenCustomModelPackageDialog(string? initialPath) => null;

    string? ShowSaveCustomModelPackageDialog(
        string suggestedName,
        string? initialPath) => null;

    CustomModelPreviewCameraDecision ConfirmCustomModelPreviewCamera(
        string selectedNodeName,
        bool exactEyeCameraExists) =>
        CustomModelPreviewCameraDecision.Cancel;

    bool ConfirmCustomModelReimport(
        string replacementFileName,
        bool boneAndHelperMappingsBecomeStale,
        bool facialMappingsBecomeStale) => false;

    /// <summary>
    /// Allows a model whose blend shapes are not valid for DL1 to be imported
    /// as geometry, materials, and a rig only.
    /// </summary>
    bool ConfirmCustomModelImportWithoutMorphs(
        string fileName,
        string morphFailure) => false;

    string? ShowSelectCustomModelOutputDirectory(string? initialPath) => null;

    string? ShowSelectDl1DeveloperToolsProjectDialog(string? initialPath) => null;

    DeveloperToolsDeploymentConflictDecision ResolveDeveloperToolsDeploymentConflict(
        string relativePath,
        string conflict,
        bool canSkip) =>
        DeveloperToolsDeploymentConflictDecision.Cancel;

    bool ConfirmLegacyDeveloperToolsOutputBackup(
        string projectRoot,
        IReadOnlyList<string> relativePaths) => false;

    bool ConfirmDeveloperToolsDeployment(
        string projectRoot,
        int artifactCount,
        int animationCount) => false;

    bool ConfirmDeveloperToolsDeploymentRollback(string receiptPath) => false;

    string? ShowOpenCustomModelTextureDialog(string? initialPath) => null;

    string? ShowSaveCustomModelAnimationRpackDialog(
        string suggestedName,
        string? initialPath) => null;

    string? ShowOpenDl1DeveloperToolsCompilerDialog(string? initialPath) => null;

    string? ShowSaveCustomModelRpackDialog(
        string suggestedName,
        string? initialPath) => null;

    string? ShowOpenDeveloperToolsAnimationLoaderLogDialog(
        string? initialPath) => null;

    string? ShowSaveDeveloperToolsAnimationDiagnosticBundleDialog(
        string? initialPath) => null;

    string? ShowSaveProjectDialog(
        string suggestedName,
        string? currentPath);
}

public sealed class WindowsProjectFileDialogService :
    IProjectFileDialogService
{
    private const string ProjectFilter =
        "Dying Light ReAnimated project (*.dlraproj)|*.dlraproj|All files (*.*)|*.*";
    private const string AnimationFilter =
        "Animation sources (*.fbx;*.anm2)|*.fbx;*.anm2|FBX animation (*.fbx)|*.fbx|Dying Light ANM2 (*.anm2)|*.anm2|All files (*.*)|*.*";
    private const string FedFilter =
        "Dying Light facial expressions (*.fed)|*.fed|All files (*.*)|*.*";
    private const string MimicAnimationFilter =
        "Dying Light mimic ANM2 (*.anm2)|*.anm2|All files (*.*)|*.*";
    private const string Anm2Filter =
        "Dying Light ANM2 (*.anm2)|*.anm2|All files (*.*)|*.*";
    private const string BlenderFilter =
        "Blender executable (blender.exe)|blender.exe|Executable files (*.exe)|*.exe";
    private const string FbxFilter =
        "Autodesk FBX (*.fbx)|*.fbx";
    private const string CustomModelFilter =
        "DL ReAnimated model (*.dlrmodel)|*.dlrmodel|All files (*.*)|*.*";
    internal static string CustomModelTextureFilter { get; } =
        BuildCustomModelTextureFilter();
    private const string RpackFilter =
        "Dying Light RPack (*.rpack)|*.rpack|All files (*.*)|*.*";

    private static string BuildCustomModelTextureFilter()
    {
        string patterns = string.Join(
            ';',
            CustomModelTextureDecoder.SupportedExtensions.Select(
                static extension => $"*{extension}"));
        return $"Texture images ({patterns})|{patterns}|All files (*.*)|*.*";
    }

    public string? ShowOpenProjectDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".dlraproj",
            Filter = ProjectFilter,
            Multiselect = false,
            Title = "Open Dying Light ReAnimated project",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowSaveProjectDialog(
        string suggestedName,
        string? currentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".dlraproj",
            FileName = Path.GetFileName(currentPath)
                ?? $"{MakeSafeFileName(suggestedName)}.dlraproj",
            Filter = ProjectFilter,
            OverwritePrompt = true,
            Title = "Save Dying Light ReAnimated project",
        };
        ApplyInitialPath(dialog, currentPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowOpenAnimationDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            Filter = AnimationFilter,
            Multiselect = false,
            Title = "Import FBX or Dying Light 1 ANM2 animation",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public ExternalFbxAnimationStackSelection?
        SelectExternalFbxAnimationStacks(
            string sourceName,
            IReadOnlyList<FbxExternalAnimationStackDescriptor> stacks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(stacks);
        var dialog = new ExternalFbxStackSelectionDialog(
            sourceName,
            stacks);
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true
            ? dialog.Selection
            : null;
    }

    public AnimationTargetSelection? SelectAnimationTargets(
        string sourceName,
        IReadOnlyList<AnimationTargetModelOption> targetModels)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(targetModels);
        var dialog = new AnimationTargetSelectionDialog(
            sourceName,
            targetModels);
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true
            ? dialog.Selection
            : null;
    }

    public string? SelectMotionAccumulatorBone(
        string targetRigId,
        System.Collections.Immutable.ImmutableArray<string> candidateBoneNames)
    {
        var dialog = new MotionAccumulatorSelectionDialog(
            targetRigId,
            candidateBoneNames);
        Window? accumulatorOwner = Application.Current?.MainWindow;
        if (accumulatorOwner is { IsVisible: true })
        {
            dialog.Owner = accumulatorOwner;
        }

        return dialog.ShowDialog() == true
            ? dialog.ChosenBoneName
            : null;
    }

    public AnimationLibraryAssignmentResult? EditAnimationLibraryAssignment(
        AnimationLibraryEditorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new AnimationLibraryEditorDialog(request);
        Window? owner = Application.Current?.MainWindow;
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true
            ? dialog.Result
            : null;
    }

    public OwningAnimationOpenDecision ConfirmOwningAnimationOpen(
        string animationName,
        string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(animationName);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        MessageBoxResult result = MessageBox.Show(
            $"'{animationName}' plays directly on its owning model '{modelName}'. You can still open it for EyeCamera, helper, prop-holder, or other authoring edits.\n\n" +
            "Yes: open and play on the existing model.\n" +
            "No: assign this source to another project model.\n" +
            "Cancel: remain in Animations.",
            "Open animation",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        return result switch
        {
            MessageBoxResult.Yes =>
                OwningAnimationOpenDecision.PlayOnExistingModel,
            MessageBoxResult.No =>
                OwningAnimationOpenDecision.AddAnotherTarget,
            _ => OwningAnimationOpenDecision.Cancel,
        };
    }

    public LocalAnm2SourceBindingDecision ConfirmLocalAnm2SourceBinding(
        LocalAnm2ImportPreflight preflight)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        string blocked = preflight.IsBlocked
            ? $"\n\nThis candidate has {preflight.AmbiguousDescriptorCount:N0} ambiguous descriptor(s), so Yes is not allowed. Choose No to select another model."
            : string.Empty;
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            $"Bind '{preflight.AnimationName}' to this immutable DL1 source model?\n\n" +
            $"Model: {preflight.SourceModelName}\n" +
            $"Retail identity: {preflight.SourceModelIdentity}\n" +
            $"Fingerprint: {preflight.SourceModelFingerprint}\n\n" +
            $"Body: {preflight.BodyDescriptorCount:N0}  |  Facial: {preflight.FacialDescriptorCount:N0}  |  Auxiliary: {preflight.AuxiliaryDescriptorCount:N0}\n" +
            $"Unresolved: {preflight.UnresolvedDescriptorCount:N0}  |  Ambiguous: {preflight.AmbiguousDescriptorCount:N0}\n\n" +
            "Yes confirms this exact model. No returns to the asset browser to choose another. Cancel keeps the current animation unchanged." +
            blocked,
            "Confirm DL1 ANM2 source model",
            System.Windows.MessageBoxButton.YesNoCancel,
            preflight.IsBlocked
                ? System.Windows.MessageBoxImage.Warning
                : System.Windows.MessageBoxImage.Question,
            preflight.IsBlocked
                ? System.Windows.MessageBoxResult.No
                : System.Windows.MessageBoxResult.Yes);
        return result switch
        {
            System.Windows.MessageBoxResult.Yes when !preflight.IsBlocked =>
                LocalAnm2SourceBindingDecision.ConfirmSuggested,
            System.Windows.MessageBoxResult.No =>
                LocalAnm2SourceBindingDecision.ChooseAnother,
            _ => LocalAnm2SourceBindingDecision.Cancel,
        };
    }

    public string? ShowOpenMimicAnimationDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".anm2",
            Filter = MimicAnimationFilter,
            Multiselect = false,
            Title = "Import synchronized Dying Light 1 mimic ANM2",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowOpenFacialFbxDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".fbx",
            Filter = FbxFilter,
            Multiselect = false,
            Title =
                "Import FBX facial animation for DL1 mapping review",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowOpenFedDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".fed",
            Filter = FedFilter,
            Multiselect = false,
            Title = "Open a Dying Light 1 FED expression file",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public IReadOnlyList<string> ShowOpenAnm2ForBlenderDialog(
        string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".anm2",
            Filter = Anm2Filter,
            Multiselect = true,
            Title = "Select one or more DL1 ANM2 animation clips",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileNames
            : [];
    }

    public string? ShowOpenBlenderExecutableDialog(
        string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".exe",
            Filter = BlenderFilter,
            Multiselect = false,
            Title = "Locate Blender",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowSaveBlenderFbxDialog(
        string suggestedName,
        string? initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".fbx",
            FileName = $"{MakeSafeFileName(suggestedName)}.fbx",
            Filter = FbxFilter,
            OverwritePrompt = true,
            Title = "Export active animation variant to self-contained FBX",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowSaveRetailMeshFbxDialog(
        string suggestedName,
        string? initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".fbx",
            FileName = $"{MakeSafeFileName(suggestedName)}.fbx",
            Filter = FbxFilter,
            OverwritePrompt = true,
            Title = "Export DL1 retail mesh to FBX",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public bool ConfirmRetailFbxExport(
        string assetName,
        int clipCount) =>
        System.Windows.MessageBox.Show(
            $"This creates an FBX and colocated DDS textures containing decoded Dying Light 1 retail data for '{assetName}' and {clipCount:N0} animation clip(s).\n\nKeep these files local. Do not upload, publish, bundle, or redistribute them. Only the decoded base-color texture is exported; DL1 shader techniques are not reproduced.\n\nContinue?",
            "Local retail-asset export",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;

    public bool ConfirmActiveVariantFbxExport(
        string modelName,
        string animationName,
        bool containsRetailModelBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentException.ThrowIfNullOrWhiteSpace(animationName);
        string ownership = containsRetailModelBytes
            ? "This FBX contains decoded Dying Light 1 retail mesh and texture data. Keep it local and do not redistribute it."
            : "This FBX contains the project-owned custom model, its materials/textures, and the evaluated active animation variant.";
        return System.Windows.MessageBox.Show(
            $"Export active variant '{animationName}' on '{modelName}' as a self-contained FBX?\n\n{ownership}\n\nOnly the decoded base-color material is reproduced; unsupported DL1 shader maps, cloth, and physics are not fabricated.",
            "Export active variant to FBX",
            System.Windows.MessageBoxButton.YesNo,
            containsRetailModelBytes
                ? System.Windows.MessageBoxImage.Warning
                : System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) ==
            System.Windows.MessageBoxResult.Yes;
    }

    public bool ConfirmAnimationScriptRename(
        string currentResourceName,
        string proposedResourceName,
        int affectedAnimationCount) =>
        System.Windows.MessageBox.Show(
            $"'{currentResourceName}' is the animation script for {affectedAnimationCount} animations in this project.\n\nRenaming it to '{proposedResourceName}' changes the type-322 resource for all of them, not just the row you edited.\n\nContinue?",
            "Rename animation script",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;

    public bool ConfirmProjectModelRemoval(
        string modelName,
        int animationTargetCount,
        int orphanedAnimationScriptCount)
    {
        string targets = animationTargetCount > 0
            ? $"\n\n{animationTargetCount:N0} animation target(s) point at this model and will be removed with it. The immutable animation sources they were made from are kept."
            : string.Empty;
        string scripts = orphanedAnimationScriptCount switch
        {
            <= 0 => string.Empty,
            1 => "\n\nOne animation script is no longer used by anything else and will be removed too.",
            _ => $"\n\n{orphanedAnimationScriptCount:N0} animation scripts are no longer used by anything else and will be removed too.",
        };
        string detail =
            $"Remove '{modelName}' from this project?" +
            targets +
            scripts +
            "\n\nNothing on disk is deleted; this only changes the project.";
        return System.Windows.MessageBox.Show(
            detail,
            "Remove project model",
            System.Windows.MessageBoxButton.YesNo,
            animationTargetCount > 0
                ? System.Windows.MessageBoxImage.Warning
                : System.Windows.MessageBoxImage.Question,
            System.Windows.MessageBoxResult.No) ==
            System.Windows.MessageBoxResult.Yes;
    }

    public bool ConfirmRetailMeshFbxExport(string assetName) =>
        System.Windows.MessageBox.Show(
            $"This creates one self-contained FBX containing decoded Dying Light 1 retail mesh data for '{assetName}'. Decoded base-color textures are embedded in the FBX. Skinned meshes retain their complete bind skeleton and vertex weights.\n\nKeep this local. Do not upload, publish, bundle, or redistribute it. Only the decoded base-color material is exported; DL1 shader techniques and other map types are not reproduced.\n\nContinue?",
            "Local retail-mesh FBX export",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;

    public string? ShowSaveAnimationRpackDialog(
        string suggestedName,
        string? initialPath)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".rpack",
            FileName = MakeSafeFileName(
                Path.GetFileNameWithoutExtension(suggestedName)) + ".rpack",
            Filter = "Dying Light animation RPack (*.rpack)|*.rpack",
            OverwritePrompt = true,
            Title = "Save animation RPack",
        };
        ApplyInitialPath(dialog, initialPath);
        return dialog.ShowDialog() == true
            ? dialog.FileName
            : null;
    }

    public string? ShowSelectExportDirectoryDialog(string? initialPath)
    {
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            // The exporter writes into a generated subfolder that it owns and
            // replaces on the next export, so this picks the parent, never the
            // final file. Saying so stops people hunting for output in the
            // folder they chose.
            Title = "Select the parent folder for the export (a named subfolder is created)",
        };
        string? initialDirectory = Directory.Exists(initialPath)
            ? initialPath
            : Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrWhiteSpace(initialDirectory) &&
            Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true
            ? dialog.FolderName
            : null;
    }

    public string? ShowSelectAdditionalRpackRootDialog(string? initialPath)
    {
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = "Select a project-relative folder containing DL1 RPack files",
        };
        string? initialDirectory = Directory.Exists(initialPath)
            ? initialPath
            : Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrWhiteSpace(initialDirectory) &&
            Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true
            ? dialog.FolderName
            : null;
    }

    public string? ShowOpenCustomModelFbxDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".fbx",
            Filter = FbxFilter,
            Multiselect = false,
            Title = "Import a user-owned binary FBX model",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowOpenCustomModelPackageDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".dlrmodel",
            Filter = CustomModelFilter,
            Multiselect = false,
            Title = "Open a DL ReAnimated custom-model workspace",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSaveCustomModelPackageDialog(
        string suggestedName,
        string? initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".dlrmodel",
            FileName = $"{MakeSafeFileName(suggestedName)}.dlrmodel",
            Filter = CustomModelFilter,
            OverwritePrompt = true,
            Title = "Save custom-model workspace",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public CustomModelPreviewCameraDecision ConfirmCustomModelPreviewCamera(
        string selectedNodeName,
        bool exactEyeCameraExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedNodeName);
        if (exactEyeCameraExists)
        {
            MessageBoxResult collision = MessageBox.Show(
                $"The hierarchy already contains the exact exportable EyeCamera helper. " +
                $"Use that existing helper instead of '{selectedNodeName}'?",
                "Choose preview camera",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);
            return collision == MessageBoxResult.Yes
                ? CustomModelPreviewCameraDecision.UseExistingEyeCamera
                : CustomModelPreviewCameraDecision.Cancel;
        }

        MessageBoxResult result = MessageBox.Show(
            $"Create an exportable child helper named exactly EyeCamera at " +
            $"'{selectedNodeName}'?\n\nYes: create EyeCamera.\n" +
            "No: keep this selection editor-only.\nCancel: leave the camera unchanged.",
            "Choose preview camera",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);
        return result switch
        {
            MessageBoxResult.Yes =>
                CustomModelPreviewCameraDecision.CreateEyeCamera,
            MessageBoxResult.No =>
                CustomModelPreviewCameraDecision.UseEditorOnlySelection,
            _ => CustomModelPreviewCameraDecision.Cancel,
        };
    }

    public bool ConfirmCustomModelReimport(
        string replacementFileName,
        bool boneAndHelperMappingsBecomeStale,
        bool facialMappingsBecomeStale)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replacementFileName);
        var consequences = new List<string>();
        if (boneAndHelperMappingsBecomeStale)
        {
            consequences.Add("Bone/helper mappings will be marked stale for review.");
        }

        if (facialMappingsBecomeStale)
        {
            consequences.Add("Facial mappings will be marked stale for review.");
        }

        if (consequences.Count == 0)
        {
            consequences.Add(
                "Skeleton and morph contracts are unchanged; existing mapping reviews can be preserved.");
        }

        consequences.Add(
            "Authored helpers and preview-camera metadata remain in their separate project layer.");
        return MessageBox.Show(
            $"Replace the current custom-model FBX with '{replacementFileName}'?\n\n" +
            string.Join(Environment.NewLine, consequences),
            "Validate custom-model reimport",
            MessageBoxButton.YesNo,
            boneAndHelperMappingsBecomeStale || facialMappingsBecomeStale
                ? MessageBoxImage.Warning
                : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmCustomModelImportWithoutMorphs(
        string fileName,
        string morphFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(morphFailure);
        return MessageBox.Show(
            $"'{fileName}' contains blend shapes that cannot be represented by DL1.\n\n" +
            $"{morphFailure}\n\n" +
            "Import the mesh, materials, and rig anyway? All blend shapes will be skipped, so facial and morph animation will be unavailable.",
            "Blend shapes will be skipped",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public string? ShowSelectCustomModelOutputDirectory(string? initialPath)
    {
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = "Select parent folder for the complete DL1 model package",
        };
        string? initialDirectory = Directory.Exists(initialPath)
            ? initialPath
            : Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return ShowOwnedDialog(dialog) == true ? dialog.FolderName : null;
    }

    public string? ShowSelectDl1DeveloperToolsProjectDialog(string? initialPath)
    {
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = "Select a Dying Light Developer Tools project",
        };
        string? initialDirectory = Directory.Exists(initialPath)
            ? initialPath
            : Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return ShowOwnedDialog(dialog) == true ? dialog.FolderName : null;
    }

    public DeveloperToolsDeploymentConflictDecision ResolveDeveloperToolsDeploymentConflict(
        string relativePath,
        string conflict,
        bool canSkip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(conflict);
        string choices = canSkip
            ? "Yes: back up and replace this file.\nNo: keep and skip this optional artifact.\nCancel: make no changes."
            : "Yes: back up and replace this required file.\nNo or Cancel: make no changes. Required files cannot be skipped.";
        MessageBoxResult result = MessageBox.Show(
            $"Destination: {relativePath}\n\n{conflict}\n\n{choices}",
            "Resolve Developer Tools deployment conflict",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result switch
        {
            MessageBoxResult.Yes => DeveloperToolsDeploymentConflictDecision.BackUpAndReplace,
            MessageBoxResult.No when canSkip => DeveloperToolsDeploymentConflictDecision.SkipSkippable,
            _ => DeveloperToolsDeploymentConflictDecision.Cancel,
        };
    }

    public bool ConfirmLegacyDeveloperToolsOutputBackup(
        string projectRoot,
        IReadOnlyList<string> relativePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(relativePaths);
        string listed = string.Join(Environment.NewLine, relativePaths.Select(static path => $"  - {path}"));
        return MessageBox.Show(
            $"Move these legacy or unmounted output directories into a recoverable project-local backup?\n\n" +
            $"{listed}\n\nProject: {projectRoot}\n\nNothing is permanently deleted.",
            "Back up legacy Developer Tools output",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmDeveloperToolsDeployment(
        string projectRoot,
        int artifactCount,
        int animationCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return MessageBox.Show(
            $"The preflight passed for {artifactCount:N0} artifact(s) and {animationCount:N0} animation(s).\n\n" +
            $"Project: {projectRoot}\n\n" +
            "Every destination is listed in the Developer Tools export details. Continue with the transactional deployment?",
            "Deploy to Dying Light Developer Tools project",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public bool ConfirmDeveloperToolsDeploymentRollback(string receiptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        return MessageBox.Show(
            $"Roll back the deployment recorded by:\n{receiptPath}\n\n" +
            "Rollback stops if any deployed file was changed after deployment.",
            "Roll back Developer Tools deployment",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public string? ShowOpenCustomModelTextureDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = CustomModelTextureFilter,
            Multiselect = false,
            Title = "Select a user-owned texture",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowOpenAnimationRpackDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            CheckFileExists = true,
            Filter = RpackFilter,
            Multiselect = false,
            Title = "Select an existing animation RPack to append",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSaveCustomModelAnimationRpackDialog(
        string suggestedName,
        string? initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".rpack",
            FileName = $"{MakeSafeFileName(suggestedName)}_animations.rpack",
            Filter = RpackFilter,
            OverwritePrompt = true,
            Title = "Export optional portable animation RPack copy",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowOpenDl1DeveloperToolsCompilerDialog(string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = true,
            CheckFileExists = true,
            DefaultExt = ".exe",
            FileName = "ResPackCompilerConsole_x64_rwdi.exe",
            Filter = "Techland ResPack compiler (ResPackCompilerConsole_x64_rwdi.exe)|ResPackCompilerConsole_x64_rwdi.exe|Executable files (*.exe)|*.exe",
            Multiselect = false,
            Title = "Select the Dying Light Developer Tools ResPack compiler",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSaveCustomModelRpackDialog(
        string suggestedName,
        string? initialPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedName);
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".rpack",
            FileName = $"{MakeSafeFileName(suggestedName)}_pc.rpack",
            Filter = RpackFilter,
            OverwritePrompt = true,
            Title = "Compile custom model to a DL1 model RPack",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowOpenDeveloperToolsAnimationLoaderLogDialog(
        string? initialPath)
    {
        OpenFileDialog dialog = new()
        {
            AddExtension = false,
            CheckFileExists = true,
            FileName = "dl_universal_loader.log",
            Filter =
                "Universal Loader log (dl_universal_loader.log)|dl_universal_loader.log|" +
                "Log files (*.log)|*.log",
            Multiselect = false,
            Title = "Select the existing Universal Loader log",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    public string? ShowSaveDeveloperToolsAnimationDiagnosticBundleDialog(
        string? initialPath)
    {
        SaveFileDialog dialog = new()
        {
            AddExtension = true,
            CheckPathExists = true,
            DefaultExt = ".zip",
            FileName = "dl-reanimated-animation-diagnostics.zip",
            Filter = "ZIP archive (*.zip)|*.zip",
            OverwritePrompt = true,
            Title = "Save offline animation diagnostic bundle",
        };
        ApplyInitialPath(dialog, initialPath);
        return ShowOwnedDialog(dialog) == true ? dialog.FileName : null;
    }

    private static bool? ShowOwnedDialog(CommonDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Window? owner = Application.Current?.MainWindow;
        return owner is { IsVisible: true }
            ? dialog.ShowDialog(owner)
            : dialog.ShowDialog();
    }

    private static void ApplyInitialPath(
        FileDialog dialog,
        string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath))
        {
            return;
        }

        string? directory = Directory.Exists(initialPath)
            ? initialPath
            : Path.GetDirectoryName(initialPath);
        if (!string.IsNullOrEmpty(directory)
            && Directory.Exists(directory))
        {
            dialog.InitialDirectory = directory;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new(name
            .Trim()
            .Select(character => invalid.Contains(character)
                ? '_'
                : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe)
            ? "Untitled"
            : safe;
    }
}

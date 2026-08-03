using System.IO;
using Microsoft.Win32;

namespace ReAnimated.App.Infrastructure;

public enum LocalAnm2SourceBindingDecision
{
    ConfirmSuggested,
    ChooseAnother,
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

public interface IProjectFileDialogService
{
    string? ShowOpenProjectDialog(string? initialPath);

    string? ShowOpenAnimationDialog(string? initialPath) => null;

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

    bool ConfirmRetailMeshFbxExport(string assetName) => false;

    string? ShowSelectExportDirectoryDialog(string? initialPath) => null;

    string? ShowSelectAdditionalRpackRootDialog(string? initialPath) => null;

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
            Title = "Export retail mesh and ANM2 Actions to Blender FBX",
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

    public bool ConfirmRetailMeshFbxExport(string assetName) =>
        System.Windows.MessageBox.Show(
            $"This creates one self-contained FBX containing decoded Dying Light 1 retail mesh data for '{assetName}'. Decoded base-color textures are embedded in the FBX. Skinned meshes retain their complete bind skeleton and vertex weights.\n\nKeep this local. Do not upload, publish, bundle, or redistribute it. Only the decoded base-color material is exported; DL1 shader techniques and other map types are not reproduced.\n\nContinue?",
            "Local retail-mesh FBX export",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No) ==
        System.Windows.MessageBoxResult.Yes;

    public string? ShowSelectExportDirectoryDialog(string? initialPath)
    {
        OpenFolderDialog dialog = new()
        {
            Multiselect = false,
            Title = "Select DL1 ANM2 export folder",
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

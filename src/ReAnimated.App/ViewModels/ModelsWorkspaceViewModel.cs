using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed record CustomModelAnimationHandoff(
    FbxModelAuthoringImportResult Model,
    CustomModelAnimationClip Selection,
    AnimationClip Clip,
    IReadOnlyList<MeshRenderData> PreviewMeshes);

/// <summary>
/// Independent custom-model authoring session. Nothing in this object reads
/// or mutates the animation project's active target, recovery snapshot, or
/// viewport publication.
/// </summary>
public sealed class ModelsWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IProjectFileDialogService _fileDialogs;
    private readonly Action<string> _setStatus;
    private readonly Func<CustomModelAnimationHandoff, Task> _openInAnimate;
    private readonly Func<string?> _getRetailData0PakPath;
    private readonly LinkedViewportCoordinator _cameraCoordinator = new();
    private CancellationTokenSource? _operationCancellation;
    private FbxModelAuthoringImportResult? _model;
    private string? _packagePath;
    private string? _sourcePath;
    private long _operationGeneration;
    private long _previewGeneration;
    private bool _isBusy;
    private string _modelName = "No custom model loaded";
    private CustomModelRigMode _selectedRigMode = CustomModelRigMode.Auto;
    private string _resourceName = "custom_model";
    private string _surfaceName = "default";
    private string _animationScriptAlias = string.Empty;
    private bool _flipTextureCoordinateV = true;
    private string _compilerExecutablePath = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable() ?? string.Empty;
    private string _summary = "Import a binary FBX to inspect its mesh, exact hierarchy, materials, and animation stacks.";
    private string _buildStatus = "Not built";
    private bool _showMeshes = true;
    private bool _showBones = true;
    private bool _showHelpers = true;
    private bool _showCameraHelpers = true;
    private bool _showPropHelpers = true;
    private CustomModelAnimationClipItemViewModel? _selectedAnimation;
    private CustomModelMaterialItemViewModel? _selectedMaterial;
    private CustomModelBoneItemViewModel? _selectedBone;
    private bool _disposed;

    public ModelsWorkspaceViewModel(
        IProjectFileDialogService fileDialogs,
        Action<string> setStatus,
        Func<CustomModelAnimationHandoff, Task> openInAnimate,
        Func<string?> getRetailData0PakPath)
    {
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _openInAnimate = openInAnimate ?? throw new ArgumentNullException(nameof(openInAnimate));
        _getRetailData0PakPath = getRetailData0PakPath ??
            throw new ArgumentNullException(nameof(getRetailData0PakPath));
        _cameraCoordinator.IsLinked = false;
        Viewport = new ViewportPaneViewModel(
            "Custom model preview",
            "Neutral DL1 authoring render | source hierarchy and textures",
            new ViewportSceneSource(
                _cameraCoordinator,
                ViewportSide.Target,
                new System.Numerics.Vector4(0.075f, 0.095f, 0.125f, 1.0f)));
        Timeline = new TimelineViewModel();
        Timeline.CurrentFrameChanged += OnTimelineFrameChanged;

        ImportFbxCommand = new AsyncRelayCommand(ImportFbxAsync, () => !IsBusy);
        OpenPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        SavePackageCommand = new RelayCommand(SavePackage, () => HasModel && !IsBusy);
        SelectTextureCommand = new RelayCommand(SelectTexture, () => SelectedMaterial is not null && !IsBusy);
        BuildLooseFilesCommand = new AsyncRelayCommand(BuildLooseFilesAsync, () => HasModel && !IsBusy);
        SelectModelCompilerCommand = new RelayCommand(SelectModelCompiler, () => !IsBusy);
        BuildModelRpackCommand = new AsyncRelayCommand(BuildModelRpackAsync, () => HasModel && !IsBusy);
        ExportAnimationRpackCommand = new AsyncRelayCommand(
            ExportAnimationRpackAsync,
            () => HasModel && Animations.Any(static clip => clip.Included) && !IsBusy);
        OpenSelectedAnimationInAnimateCommand = new AsyncRelayCommand(
            OpenSelectedAnimationInAnimateAsync,
            () => HasModel && SelectedAnimation?.DecodedClip is not null && !IsBusy);
        FrameModelCommand = new RelayCommand(FrameModel, () => HasModel);
        ResetCameraCommand = new RelayCommand(ResetCamera);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    public ViewportPaneViewModel Viewport { get; }

    public TimelineViewModel Timeline { get; }

    public ObservableCollection<CustomModelBoneItemViewModel> Bones { get; } = [];

    public ObservableCollection<CustomModelMaterialItemViewModel> Materials { get; } = [];

    public ObservableCollection<CustomModelAnimationClipItemViewModel> Animations { get; } = [];

    public ObservableCollection<CustomModelDiagnosticItemViewModel> Diagnostics { get; } = [];

    public IReadOnlyList<CustomModelRigMode> RigModes { get; } = Enum.GetValues<CustomModelRigMode>();

    public IReadOnlyList<Dl1RootMotionMode> RootMotionModes { get; } = Enum.GetValues<Dl1RootMotionMode>();

    public ObservableCollection<string> RootBoneNames { get; } = [];

    public IAsyncRelayCommand ImportFbxCommand { get; }

    public IAsyncRelayCommand OpenPackageCommand { get; }

    public IRelayCommand SavePackageCommand { get; }

    public IRelayCommand SelectTextureCommand { get; }

    public IAsyncRelayCommand BuildLooseFilesCommand { get; }

    public IRelayCommand SelectModelCompilerCommand { get; }

    public IAsyncRelayCommand BuildModelRpackCommand { get; }

    public IAsyncRelayCommand ExportAnimationRpackCommand { get; }

    public IAsyncRelayCommand OpenSelectedAnimationInAnimateCommand { get; }

    public IRelayCommand FrameModelCommand { get; }

    public IRelayCommand ResetCameraCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public bool HasModel => _model is not null;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NotifyCommands();
            }
        }
    }

    public string ModelName
    {
        get => _modelName;
        set => SetProperty(ref _modelName, string.IsNullOrWhiteSpace(value) ? "Untitled model" : value.Trim());
    }

    public CustomModelRigMode SelectedRigMode
    {
        get => _selectedRigMode;
        set => SetProperty(ref _selectedRigMode, value);
    }

    public string ResourceName
    {
        get => _resourceName;
        set => SetProperty(ref _resourceName, value ?? string.Empty);
    }

    public string SurfaceName
    {
        get => _surfaceName;
        set => SetProperty(ref _surfaceName, value ?? string.Empty);
    }

    public string AnimationScriptAlias
    {
        get => _animationScriptAlias;
        set => SetProperty(ref _animationScriptAlias, value ?? string.Empty);
    }

    public bool FlipTextureCoordinateV
    {
        get => _flipTextureCoordinateV;
        set
        {
            if (SetProperty(ref _flipTextureCoordinateV, value) && _model is not null)
            {
                SyncDocument();
                RefreshPreview();
                BuildStatus = value
                    ? "DL1 texture-origin conversion enabled (V is flipped at preview/build boundaries)."
                    : "Raw FBX texture coordinates enabled; use only for sources authored with an upper-left origin.";
            }
        }
    }

    public string CompilerExecutablePath
    {
        get => _compilerExecutablePath;
        private set
        {
            if (SetProperty(ref _compilerExecutablePath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CompilerStatus));
            }
        }
    }

    public string CompilerStatus => File.Exists(CompilerExecutablePath)
        ? $"Developer Tools compiler: {CompilerExecutablePath}"
        : "Developer Tools compiler not selected. Loose sources and animation RPack export remain available.";

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string BuildStatus
    {
        get => _buildStatus;
        private set => SetProperty(ref _buildStatus, value);
    }

    public bool ShowMeshes
    {
        get => _showMeshes;
        set
        {
            if (SetProperty(ref _showMeshes, value))
            {
                Viewport.SceneSource.SetMeshVisibility(value);
            }
        }
    }

    public bool ShowBones
    {
        get => _showBones;
        set
        {
            if (SetProperty(ref _showBones, value))
            {
                RefreshPreview();
            }
        }
    }

    public bool ShowHelpers
    {
        get => _showHelpers;
        set
        {
            if (SetProperty(ref _showHelpers, value))
            {
                ApplySkeletonVisibility();
            }
        }
    }

    public bool ShowCameraHelpers
    {
        get => _showCameraHelpers;
        set
        {
            if (SetProperty(ref _showCameraHelpers, value))
            {
                ApplySkeletonVisibility();
            }
        }
    }

    public bool ShowPropHelpers
    {
        get => _showPropHelpers;
        set
        {
            if (SetProperty(ref _showPropHelpers, value))
            {
                ApplySkeletonVisibility();
            }
        }
    }

    public CustomModelAnimationClipItemViewModel? SelectedAnimation
    {
        get => _selectedAnimation;
        set
        {
            if (SetProperty(ref _selectedAnimation, value))
            {
                RefreshTimeline();
                RefreshPreview();
                OpenSelectedAnimationInAnimateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public CustomModelMaterialItemViewModel? SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (SetProperty(ref _selectedMaterial, value))
            {
                SelectTextureCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public CustomModelBoneItemViewModel? SelectedBone
    {
        get => _selectedBone;
        set
        {
            if (SetProperty(ref _selectedBone, value))
            {
                RefreshPreview();
            }
        }
    }

    public void Tick(DateTimeOffset now) => Timeline.Tick(now);

    public async Task ImportPathAsync(
        string path,
        CustomModelRigMode rigMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        try
        {
            FbxModelAuthoringImportResult imported = await FbxModelAuthoringImporter.ImportFileAsync(
                path,
                new FbxModelAuthoringImportOptions { RigMode = rigMode },
                token);
            EnsureCurrent(generation, token);
            CommitModel(imported, path, packagePath: null);
            _setStatus($"Imported custom model {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException)
        {
            _setStatus("Custom-model import canceled; the previous model was retained");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            NotSupportedException or
            OverflowException)
        {
            BuildStatus = $"Import failed: {exception.Message}";
            _setStatus("Custom-model import failed; the previous model was retained");
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task ImportFbxAsync()
    {
        if (!TryShowModelPicker(
                "Choose a binary FBX model to import",
                () => _fileDialogs.ShowOpenCustomModelFbxDialog(
                    _sourcePath ?? _packagePath),
                out string? path))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            BuildStatus = "FBX selection canceled; current model unchanged";
            _setStatus(BuildStatus);
            return;
        }

        await ImportPathAsync(path, SelectedRigMode);
    }

    private async Task OpenPackageAsync()
    {
        if (!TryShowModelPicker(
                "Choose a DL ReAnimated model workspace to open",
                () => _fileDialogs.ShowOpenCustomModelPackageDialog(
                    _packagePath ?? _sourcePath),
                out string? path))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            BuildStatus = "Model-workspace selection canceled; current model unchanged";
            _setStatus(BuildStatus);
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            CustomModelPackage package = await Task.Run(() => CustomModelPackageSerializer.Load(path), token);
            FbxModelAuthoringImportResult decoded = await Task.Run(
                () => FbxModelAuthoringImporter.ImportPackage(package, token),
                token);
            EnsureCurrent(generation, token);
            CommitModel(decoded, sourcePath: null, packagePath: path);
            _setStatus($"Opened custom model package {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException)
        {
            _setStatus("Custom-model package open canceled; previous model retained");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or NotSupportedException or OverflowException or CustomModelFormatException)
        {
            BuildStatus = $"Open failed: {exception.Message}";
            _setStatus("Custom-model package open failed; previous model retained");
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private void SavePackage()
    {
        if (_model is null)
        {
            return;
        }

        string suggestedName = Dl1SourceModelWriter.SanitizeName(ModelName, 63);
        string? path = _fileDialogs.ShowSaveCustomModelPackageDialog(
            suggestedName,
            _packagePath ?? _sourcePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            SyncDocument();
            _packagePath = CustomModelPackageSerializer.SaveAtomic(_model.Package, path);
            BuildStatus = $"Saved {Path.GetFileName(_packagePath)}";
            _setStatus(BuildStatus);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
            InvalidOperationException or CustomModelFormatException)
        {
            BuildStatus = $"Save failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private void SelectTexture()
    {
        if (_model is null || SelectedMaterial is null)
        {
            return;
        }

        string? path = _fileDialogs.ShowOpenCustomModelTextureDialog(_sourcePath ?? _packagePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            FileInfo info = new(path);
            if (info.Length <= 0 || info.Length > CustomModelPackageSerializer.MaximumTextureBytes)
            {
                throw new InvalidDataException(
                    $"Textures must be between 1 byte and {CustomModelPackageSerializer.MaximumTextureBytes:N0} bytes.");
            }

            byte[] bytes = File.ReadAllBytes(path);
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string extension = NormalizeTextureExtension(path);
            string entryPath = $"textures/user/{hash}{extension}";
            CustomModelMaterial material = SelectedMaterial.Contract;
            CustomModelTextureBinding binding = new()
            {
                Id = Guid.NewGuid(),
                Semantic = CustomModelTextureSemantic.BaseColor,
                SourceKind = CustomModelTextureSourceKind.UserOverride,
                DisplayName = Path.GetFileName(path),
                PackageEntryPath = entryPath,
                OriginalReference = Path.GetFileName(path),
                ContentSha256 = hash,
                MediaType = TextureMediaType(extension),
            };
            material = material with
            {
                Textures = material.Textures
                    .Where(static texture => texture.Semantic != CustomModelTextureSemantic.BaseColor)
                    .Append(binding)
                    .OrderBy(static texture => texture.Semantic)
                    .ToImmutableArray(),
            };
            CustomModelDocument document = _model.Package.Document with
            {
                Materials = _model.Package.Document.Materials
                    .Select(candidate => candidate.Id == material.Id ? material : candidate)
                    .ToImmutableArray(),
            };
            ImmutableDictionary<string, ImmutableArray<byte>> payloads = _model.Package.TexturePayloads
                .Where(pair => document.Materials
                    .SelectMany(static candidate => candidate.Textures)
                    .Any(texture => string.Equals(texture.PackageEntryPath, pair.Key, StringComparison.Ordinal)))
                .ToImmutableDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
                .SetItem(entryPath, bytes.ToImmutableArray());
            _model = _model with
            {
                Package = new CustomModelPackage(document, _model.Package.SourceFbx, payloads),
            };
            PopulateMaterials();
            SelectedMaterial = Materials.First(item => item.Contract.Id == material.Id);
            RefreshPreview();
            BuildStatus = $"Selected base-color texture {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or OverflowException)
        {
            BuildStatus = $"Texture selection failed: {exception.Message}";
        }
    }

    private async Task BuildLooseFilesAsync()
    {
        if (_model is null)
        {
            return;
        }

        string? directory = _fileDialogs.ShowSelectCustomModelOutputDirectory(_packagePath ?? _sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            Dl1SourceModelBuildResult result = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = _model,
                    OutputDirectory = directory,
                    ResourceName = ResourceName,
                    SurfaceName = SurfaceName,
                    AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                        ? null
                        : AnimationScriptAlias.Trim(),
                },
                token);
            EnsureCurrent(generation, token);
            BuildStatus =
                $"Source model ready: {Path.GetFileName(result.SourceMshPath)}, {Path.GetFileName(result.BoneScriptPath)}. Use Compile model RPack to create the validated .msh_obj and RPack; .chr/.skn remain unsupported.";
            _setStatus($"Built custom-model source files in {directory}");
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Model build canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or OverflowException)
        {
            BuildStatus = $"Build failed: {exception.Message}";
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private void SelectModelCompiler()
    {
        string? path = _fileDialogs.ShowOpenDl1DeveloperToolsCompilerDialog(
            File.Exists(CompilerExecutablePath)
                ? CompilerExecutablePath
                : _packagePath ?? _sourcePath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            CompilerExecutablePath = Path.GetFullPath(path);
            BuildStatus = "Selected the Dying Light Developer Tools compiler";
        }
    }

    private async Task BuildModelRpackAsync()
    {
        if (_model is null)
        {
            return;
        }

        if (!File.Exists(CompilerExecutablePath))
        {
            SelectModelCompiler();
            if (!File.Exists(CompilerExecutablePath))
            {
                return;
            }
        }

        string resourceName = Dl1SourceModelWriter.SanitizeName(ResourceName, 55);
        string? path = _fileDialogs.ShowSaveCustomModelRpackDialog(
            resourceName,
            _packagePath ?? _sourcePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            BuildStatus = "Compiling model in an isolated Developer Tools workspace...";
            Dl1OfficialModelCompilerResult result = await Dl1OfficialModelCompiler.CompileAsync(
                new Dl1OfficialModelCompilerRequest
                {
                    Model = _model,
                    CompilerExecutablePath = CompilerExecutablePath,
                    RetailData0PakPath = _getRetailData0PakPath(),
                    OutputRpackPath = path,
                    ResourceName = resourceName,
                    SurfaceName = SurfaceName,
                    AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                        ? null
                        : AnimationScriptAlias.Trim(),
                },
                token);
            EnsureCurrent(generation, token);
            CustomModelDocument document = _model.Package.Document with
            {
                LastBuildReceipt = result.BuildReceipt,
            };
            document.Validate();
            _model = _model with
            {
                Package = new CustomModelPackage(
                    document,
                    _model.Package.SourceFbx,
                    _model.Package.TexturePayloads),
            };
            BuildStatus =
                $"Game-ready model bundle: {Path.GetFileName(result.OutputRpackPath)}" +
                (result.MaterialDatabasePath is null
                    ? string.Empty
                    : $" + {Path.GetFileName(result.MaterialDatabasePath)}") +
                $"; compiled mesh: {Path.GetFileName(result.CompiledMeshObjectPath)}. .chr/.skn remain unsupported.";
            _setStatus($"Compiled custom model RPack {Path.GetFileName(result.OutputRpackPath)}");
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Model RPack compile canceled; no staged output was published";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or OverflowException or TimeoutException or Win32Exception)
        {
            BuildStatus = $"Model RPack compile failed: {exception.Message}";
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task ExportAnimationRpackAsync()
    {
        if (_model is null)
        {
            return;
        }

        string? path = _fileDialogs.ShowSaveCustomModelAnimationRpackDialog(
            $"{Dl1SourceModelWriter.SanitizeName(ResourceName, 55)}_animations",
            _packagePath ?? _sourcePath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            CustomModelAnimationLibraryResult result = await CustomModelAnimationLibraryExporter.ExportAsync(
                new CustomModelAnimationLibraryRequest
                {
                    Model = _model,
                    OutputPath = path,
                    Selections = _model.Package.Document.AnimationClips,
                },
                token);
            EnsureCurrent(generation, token);
            BuildStatus = $"Exported {result.AnimationNames.Length:N0} animation(s) to {Path.GetFileName(result.OutputPath)}";
            _setStatus(BuildStatus);
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Animation RPack export canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or OverflowException)
        {
            BuildStatus = $"Animation RPack export failed: {exception.Message}";
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task OpenSelectedAnimationInAnimateAsync()
    {
        if (_model is null || SelectedAnimation?.DecodedClip is not { } clip)
        {
            return;
        }

        SyncDocument();
        CustomModelAnimationClip selection = _model.Package.Document.AnimationClips
            .First(candidate => candidate.Id == SelectedAnimation.Id);
        CustomModelPreviewPayload preview = CustomModelPreviewAdapter.Create(
            _model,
            clip,
            Timeline.CurrentFrame);
        await _openInAnimate(new CustomModelAnimationHandoff(
            _model,
            selection,
            clip,
            preview.Meshes));
    }

    private void CommitModel(
        FbxModelAuthoringImportResult imported,
        string? sourcePath,
        string? packagePath)
    {
        _model = imported;
        _sourcePath = sourcePath;
        _packagePath = packagePath;
        ModelName = imported.Package.Document.Name;
        SelectedRigMode = imported.Package.Document.RigMode;
        ResourceName = imported.Package.Document.BuildSettings.ResourceName;
        SurfaceName = imported.Package.Document.BuildSettings.SurfaceName;
        AnimationScriptAlias = imported.Package.Document.BuildSettings.AnimationScriptAlias ?? string.Empty;
        _flipTextureCoordinateV = imported.Package.Document.BuildSettings.FlipTextureCoordinateV;
        OnPropertyChanged(nameof(FlipTextureCoordinateV));
        BuildStatus = imported.Package.Document.LastBuildReceipt is { } receipt
            ? $"Last build: {receipt.State} at {receipt.CompletedUtc.LocalDateTime:g}"
            : "Authoring draft";

        Bones.Clear();
        RootBoneNames.Clear();
        foreach (CustomModelBone bone in imported.Package.Document.Bones)
        {
            Bones.Add(new CustomModelBoneItemViewModel(bone));
            RootBoneNames.Add(bone.Name);
        }

        PopulateMaterials();
        Animations.Clear();
        foreach (CustomModelAnimationClip clip in imported.Package.Document.AnimationClips)
        {
            imported.AnimationClips.TryGetValue(clip.Id, out AnimationClip? decoded);
            var item = new CustomModelAnimationClipItemViewModel(clip, decoded);
            item.PropertyChanged += OnAnimationItemPropertyChanged;
            Animations.Add(item);
        }

        Diagnostics.Clear();
        foreach (CustomModelImportDiagnostic diagnostic in imported.Package.Document.Diagnostics)
        {
            Diagnostics.Add(new CustomModelDiagnosticItemViewModel(diagnostic));
        }

        SelectedBone = Bones.FirstOrDefault();
        SelectedMaterial = Materials.FirstOrDefault();
        SelectedAnimation = Animations.FirstOrDefault(static clip => clip.DecodedClip is not null);
        Summary =
            $"{imported.Package.Document.Meshes.Length:N0} mesh part(s) | {imported.Surfaces.Length:N0} draw surface(s) | " +
            $"{imported.Package.Document.Bones.Length:N0} rig node(s) | {Animations.Count:N0} animation stack(s) | " +
            $"{Materials.Count:N0} material(s)";
        OnPropertyChanged(nameof(HasModel));
        NotifyCommands();
        RefreshTimeline();
        RefreshPreview();
        FrameModel();
    }

    private void PopulateMaterials()
    {
        if (_model is null)
        {
            return;
        }

        Materials.Clear();
        foreach (CustomModelMaterial material in _model.Package.Document.Materials)
        {
            Materials.Add(new CustomModelMaterialItemViewModel(material));
        }
    }

    private void SyncDocument()
    {
        if (_model is null)
        {
            return;
        }

        ImmutableArray<CustomModelAnimationClip> selections = Animations
            .Select(static item => item.ToContract())
            .ToImmutableArray();
        CustomModelDocument document = _model.Package.Document with
        {
            Name = ModelName,
            AnimationClips = selections,
            BuildSettings = new CustomModelBuildSettings
            {
                ResourceName = Dl1SourceModelWriter.SanitizeName(ResourceName, 55),
                SurfaceName = Dl1SourceModelWriter.SanitizeName(SurfaceName, 63),
                AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                    ? null
                    : AnimationScriptAlias.Trim(),
                FlipTextureCoordinateV = FlipTextureCoordinateV,
            },
        };
        document.Validate();
        _model = _model with
        {
            Package = new CustomModelPackage(
                document,
                _model.Package.SourceFbx,
                _model.Package.TexturePayloads),
        };
    }

    private void RefreshPreview()
    {
        if (_model is null)
        {
            Viewport.SceneSource.SetScene([], null, []);
            return;
        }

        AnimationClip? clip = SelectedAnimation?.DecodedClip;
        int frame = clip is null
            ? 0
            : Math.Clamp(Timeline.CurrentFrame, 0, checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1)));
        CustomModelPreviewPayload payload = CustomModelPreviewAdapter.Create(
            _model,
            clip,
            frame,
            SelectedBone?.Index);
        SkeletonRenderData? skeleton = ShowBones ? payload.Skeleton : null;
        Viewport.SceneSource.SetScene(
            payload.Meshes,
            skeleton,
            [],
            generation: Interlocked.Increment(ref _previewGeneration));
        Viewport.SceneSource.SetMeshVisibility(ShowMeshes);
        ApplySkeletonVisibility();
        Viewport.SetPresentation(
            $"Model preview — {ModelName}",
            clip is null
                ? "Exact FBX bind hierarchy | neutral DL1 authoring render"
                : $"Animation stack: {SelectedAnimation!.DisplayName} | frame {frame:N0}");
        Viewport.SetDiagnosticOverlay(payload.Diagnostics.IsEmpty
            ? null
            : string.Join(Environment.NewLine, payload.Diagnostics.Take(3)));
    }

    private void ApplySkeletonVisibility() =>
        Viewport.SceneSource.SetSkeletonVisibility(
            ShowBones,
            ShowHelpers,
            ShowCameraHelpers,
            ShowPropHelpers);

    private void RefreshTimeline()
    {
        AnimationClip? clip = SelectedAnimation?.DecodedClip;
        if (clip is null || _model?.Rig is null)
        {
            Timeline.EndFrame = 1;
            Timeline.CurrentFrame = 0;
            Timeline.ReplaceTracks([]);
            Timeline.ReplaceCurves([]);
            return;
        }

        Timeline.FramesPerSecond = clip.FrameRate.FramesPerSecond;
        Timeline.EndFrame = checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1));
        Timeline.CurrentFrame = 0;
        var tracks = new List<TimelineTrackViewModel>();
        var curves = new List<TimelineCurveTrackViewModel>();
        foreach (TransformTrack track in clip.TransformTracks)
        {
            string boneName = (uint)track.BoneIndex < (uint)_model.Rig.BoneCount
                ? _model.Rig.Bones[track.BoneIndex].Name
                : $"Bone {track.BoneIndex}";
            string id = $"custom:{track.BoneIndex}";
            tracks.Add(new TimelineTrackViewModel(
                id,
                boneName,
                "Transform",
                "Source animation",
                isReadOnly: true,
                totalKeyCount: track.Keyframes.Length,
                exactKeyFrames: track.Keyframes.Select(static key => checked((int)Math.Round(key.Frame)))));
            AddTransformCurves(curves, id, boneName, track.Keyframes);
        }

        Timeline.ReplaceTracks(tracks);
        Timeline.ReplaceCurves(curves);
        Timeline.SelectTrack(tracks.FirstOrDefault()?.Id);
        Timeline.FitTimelineCommand.Execute(null);
    }

    private static void AddTransformCurves(
        List<TimelineCurveTrackViewModel> curves,
        string trackId,
        string label,
        IEnumerable<TransformKeyframe> keys)
    {
        TransformKeyframe[] sampled = keys.Take(2048).ToArray();
        Add("Translation X", "#FF6B6B", static value => value.Translation.X);
        Add("Translation Y", "#51CF66", static value => value.Translation.Y);
        Add("Translation Z", "#4DABF7", static value => value.Translation.Z);
        Add("Rotation X", "#FFA94D", static value => value.Rotation.X);
        Add("Rotation Y", "#63E6BE", static value => value.Rotation.Y);
        Add("Rotation Z", "#74C0FC", static value => value.Rotation.Z);
        Add("Rotation W", "#D0BFFF", static value => value.Rotation.W);

        void Add(string name, string color, Func<ReAnimated.Core.Mathematics.TransformTRS, double> select) =>
            curves.Add(new TimelineCurveTrackViewModel(
                name,
                color,
                sampled.Select(key => new TimelineCurveKeyViewModel(key.Frame, select(key.Value))),
                trackId,
                $"{label} / {name}"));
    }

    private void FrameModel()
    {
        RenderFrameSnapshot frame = Viewport.SceneSource.CaptureFrame();
        if (RenderCameraFraming.TryFrame(frame, out RenderCamera camera))
        {
            _cameraCoordinator.UpdateCamera(ViewportSide.Target, camera);
        }
    }

    private void ResetCamera()
    {
        _cameraCoordinator.UpdateCamera(ViewportSide.Target, RenderCamera.Default);
        FrameModel();
    }

    private void OnTimelineFrameChanged(object? sender, EventArgs args) => RefreshPreview();

    private void OnAnimationItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        ExportAnimationRpackCommand.NotifyCanExecuteChanged();
        if (args.PropertyName is nameof(CustomModelAnimationClipItemViewModel.FrameRateNumerator) or
            nameof(CustomModelAnimationClipItemViewModel.FrameRateDenominator))
        {
            SelectedAnimation?.ApplyTimelineCadence(Timeline);
        }
    }

    private long BeginOperation(CancellationToken external, out CancellationToken token)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(external);
        token = _operationCancellation.Token;
        IsBusy = true;
        return Interlocked.Increment(ref _operationGeneration);
    }

    private void EnsureCurrent(long generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _operationGeneration))
        {
            throw new OperationCanceledException("A newer Models-workspace operation superseded this result.");
        }
    }

    private void EndOperation(long generation)
    {
        if (generation == Volatile.Read(ref _operationGeneration))
        {
            IsBusy = false;
        }
    }

    private void Cancel() => _operationCancellation?.Cancel();

    private bool TryShowModelPicker(
        string status,
        Func<string?> showPicker,
        out string? path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(showPicker);
        BuildStatus = status;
        _setStatus(status);
        try
        {
            path = showPicker();
            return true;
        }
        catch (Exception exception)
        {
            path = null;
            BuildStatus = $"File picker failed: {exception.Message}";
            _setStatus(BuildStatus);
            return false;
        }
    }

    private void NotifyCommands()
    {
        ImportFbxCommand.NotifyCanExecuteChanged();
        OpenPackageCommand.NotifyCanExecuteChanged();
        SavePackageCommand.NotifyCanExecuteChanged();
        SelectTextureCommand.NotifyCanExecuteChanged();
        BuildLooseFilesCommand.NotifyCanExecuteChanged();
        SelectModelCompilerCommand.NotifyCanExecuteChanged();
        BuildModelRpackCommand.NotifyCanExecuteChanged();
        ExportAnimationRpackCommand.NotifyCanExecuteChanged();
        OpenSelectedAnimationInAnimateCommand.NotifyCanExecuteChanged();
        FrameModelCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static string NormalizeTextureExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".dds" or ".tga"
            ? extension
            : ".bin";
    }

    private static string TextureMediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".tif" or ".tiff" => "image/tiff",
        ".dds" => "image/vnd-ms.dds",
        ".tga" => "image/x-tga",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Timeline.CurrentFrameChanged -= OnTimelineFrameChanged;
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        foreach (CustomModelAnimationClipItemViewModel animation in Animations)
        {
            animation.PropertyChanged -= OnAnimationItemPropertyChanged;
        }
    }
}

public sealed class CustomModelAnimationClipItemViewModel : ObservableObject
{
    private string _displayName;
    private bool _included;
    private int _frameRateNumerator;
    private int _frameRateDenominator;
    private Dl1RootMotionMode _rootMotionMode;
    private string? _rootBoneName;

    public CustomModelAnimationClipItemViewModel(CustomModelAnimationClip contract, AnimationClip? decodedClip)
    {
        Contract = contract;
        DecodedClip = decodedClip;
        _displayName = contract.DisplayName;
        _included = contract.Included;
        _frameRateNumerator = contract.FrameRate.Numerator;
        _frameRateDenominator = contract.FrameRate.Denominator;
        _rootMotionMode = contract.RootMotionMode;
        _rootBoneName = contract.RootBoneName;
    }

    public CustomModelAnimationClip Contract { get; }

    public AnimationClip? DecodedClip { get; }

    public Guid Id => Contract.Id;

    public string SourceName => Contract.SourceName;

    public long FrameCount => Contract.FrameCount;

    public string DecodeStatus => DecodedClip is null ? "Metadata only — blocked from export" : "Decoded";

    public string DisplayName { get => _displayName; set => SetProperty(ref _displayName, value ?? string.Empty); }

    public bool Included { get => _included; set => SetProperty(ref _included, value); }

    public int FrameRateNumerator { get => _frameRateNumerator; set => SetProperty(ref _frameRateNumerator, Math.Max(1, value)); }

    public int FrameRateDenominator { get => _frameRateDenominator; set => SetProperty(ref _frameRateDenominator, Math.Max(1, value)); }

    public Dl1RootMotionMode RootMotionMode { get => _rootMotionMode; set => SetProperty(ref _rootMotionMode, value); }

    public string? RootBoneName { get => _rootBoneName; set => SetProperty(ref _rootBoneName, string.IsNullOrWhiteSpace(value) ? null : value.Trim()); }

    public CustomModelAnimationClip ToContract() => Contract with
    {
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? Contract.SourceName : DisplayName.Trim(),
        Included = Included,
        FrameRate = new FrameRate(FrameRateNumerator, FrameRateDenominator),
        RootMotionMode = RootMotionMode,
        RootBoneName = RootBoneName,
    };

    public void ApplyTimelineCadence(TimelineViewModel timeline) =>
        timeline.FramesPerSecond = new FrameRate(FrameRateNumerator, FrameRateDenominator).FramesPerSecond;
}

public sealed record CustomModelBoneItemViewModel(CustomModelBone Contract)
{
    public int Index => Contract.Index;
    public string Name => Contract.Name;
    public string Role => Contract.Kind.ToString();
    public int ParentIndex => Contract.ParentIndex;
    public bool IsWeighted => Contract.IsWeighted;
}

public sealed record CustomModelMaterialItemViewModel(CustomModelMaterial Contract)
{
    public string Name => Contract.Name;
    public string BaseColor => Contract.Textures.FirstOrDefault(
        static texture => texture.Semantic == CustomModelTextureSemantic.BaseColor)?.DisplayName ?? "No base color";
    public string TextureSummary => Contract.Textures.IsEmpty
        ? "No textures"
        : string.Join(", ", Contract.Textures.Select(static texture => $"{texture.Semantic}: {texture.DisplayName}"));
}

public sealed record CustomModelDiagnosticItemViewModel(CustomModelImportDiagnostic Contract)
{
    public string Severity => Contract.Severity.ToString();
    public string Code => Contract.Code;
    public string Message => Contract.Message;
    public string? Subject => Contract.Subject;
}

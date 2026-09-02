using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

public sealed record CustomModelPreviewModeChoice(
    CustomModelPreviewMode Mode,
    string Label);

internal sealed record ModelsWorkspaceEmbeddedStackPayload(
    CustomModelAnimationClip Selection,
    bool IsDecoded);

internal sealed record ModelsWorkspacePersistencePayload(
    Guid ModelId,
    string SuggestedFileName,
    ImmutableArray<byte> PackageBytes,
    string? RuntimeRigSignature,
    string AuthoringRigContractSignature,
    string? AnimationSkeletonSignature,
    string? Dl1OutputRigSignature,
    string? Dl1DescriptorInventoryFingerprint,
    string MorphSignature,
    string? AnimationScriptAlias,
    string? PreviewCameraNodeName,
    int ExportableEyeCameraHelperCount,
    string? RigId,
    ImmutableArray<ModelsWorkspaceEmbeddedStackPayload> EmbeddedStacks,
    Guid? SelectedAnimationClipId,
    ProjectCustomModelPreviewMode PreviewMode,
    bool ShowMeshes,
    bool ShowBones,
    bool ShowHelpers,
    bool ShowCameraHelpers,
    bool ShowPropHelpers)
{
    public ProjectModelsWorkspaceState CreateProjectState(Guid packageAssetId) =>
        new()
        {
            PackageAssetId = packageAssetId,
            SelectedAnimationClipId = SelectedAnimationClipId,
            PreviewMode = PreviewMode,
            ShowMeshes = ShowMeshes,
            ShowBones = ShowBones,
            ShowHelpers = ShowHelpers,
            ShowCameraHelpers = ShowCameraHelpers,
            ShowPropHelpers = ShowPropHelpers,
        };
}

internal sealed record PreparedModelsWorkspaceRestore(
    FbxModelAuthoringImportResult Model,
    string PackagePath,
    ProjectModelsWorkspaceState State);

internal sealed record ModelsWorkspaceSessionSnapshot(
    FbxModelAuthoringImportResult? Model,
    string? SourcePath,
    string? PackagePath,
    Guid? SelectedAnimationClipId,
    CustomModelPreviewMode PreviewMode,
    bool ShowMeshes,
    bool ShowBones,
    bool ShowHelpers,
    bool ShowCameraHelpers,
    bool ShowPropHelpers,
    long AuthoringRevision);

/// <summary>
/// Independent custom-model authoring session. Nothing in this object reads
/// or mutates the animation project's active target, recovery snapshot, or
/// viewport publication.
/// </summary>
public sealed partial class ModelsWorkspaceViewModel : ObservableObject, IDisposable
{
    private const string InvalidatedBuildReceiptDiagnosticCode =
        "model_build_receipt_invalidated";

    private static readonly IReadOnlyList<CustomModelPreviewModeChoice> PreviewModeChoicesValue =
    [
        new(CustomModelPreviewMode.Dl1Output, "DL1 output"),
        new(CustomModelPreviewMode.SourceFbx, "Source FBX"),
    ];

    private readonly IProjectFileDialogService _fileDialogs;
    private readonly Action<string> _setStatus;
    private readonly Func<CustomModelAnimationHandoff, Task> _openInAnimate;
    private readonly Func<Task> _synchronizeProject;
    private readonly Action _returnToProjectModels;
    private readonly Func<string?> _getRetailData0PakPath;
    private readonly CustomModelDeveloperToolsSettings _developerToolsSettings;
    private readonly LinkedViewportCoordinator _cameraCoordinator = new();
    private CancellationTokenSource? _operationCancellation;
    private FbxModelAuthoringImportResult? _model;
    private CustomModelPreviewSession? _previewSession;
    private string? _packagePath;
    private string? _sourcePath;
    private long _operationGeneration;
    private long _authoringRevision;
    private long _previewGeneration;
    private bool _suppressPreviewRefresh;
    private bool _suppressPersistenceNotifications;
    private bool _isBusy;
    private string _modelName = "No custom model loaded";
    private CustomModelRigMode _selectedRigMode = CustomModelRigMode.Auto;
    private string _resourceName = "custom_model";
    private string _characterId = "custom_model";
    private bool _characterIdWasExplicitlyEdited;
    private string _surfaceName = "default";
    private string _animationScriptAlias = string.Empty;
    private bool _flipTextureCoordinateV = true;
    private string _compilerExecutablePath = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable() ?? string.Empty;
    private string _developerToolsProjectRoot = string.Empty;
    private bool _installLooseAnm2 = true;
    private bool _exportPortableAnimationRpack = true;
    private string _lastDeploymentManifestPath = string.Empty;
    private string _lastDeployedAnimationScriptPath = string.Empty;
    private bool _canRollbackLastDeployment;
    private ImmutableArray<string> _legacyOutputPaths = [];
    private string _deploymentPreflightSummary =
        "Choose a Developer Tools project, then deploy to run the exact staged preflight.";
    private string _summary = "Import a binary FBX to inspect its mesh, exact hierarchy, materials, and animation stacks.";
    private string _buildStatus = "Not built";
    private bool _showMeshes = true;
    private bool _showBones = true;
    private bool _showHelpers = true;
    private bool _showCameraHelpers = true;
    private bool _showPropHelpers = true;
    private CustomModelPreviewModeChoice _selectedPreviewMode = PreviewModeChoicesValue[0];
    private CustomModelTextureSemantic _selectedTextureSemantic = CustomModelTextureSemantic.BaseColor;
    private CustomModelNormalMapConvention _selectedNormalMapConvention =
        CustomModelNormalMapConvention.RgbOpenGl;
    private CustomModelAnimationClipItemViewModel? _selectedAnimation;
    private CustomModelMaterialItemViewModel? _selectedMaterial;
    private CustomModelBoneItemViewModel? _selectedBone;
    private readonly Stack<CustomModelDocument> _helperUndo = new();
    private CustomModelAuthoredHelperKind _selectedHelperKind =
        CustomModelAuthoredHelperKind.Helper;
    private string _newHelperName = string.Empty;
    private double _helperTranslationX;
    private double _helperTranslationY;
    private double _helperTranslationZ;
    private double _helperRotationX;
    private double _helperRotationY;
    private double _helperRotationZ;
    private bool _disposed;

    public ModelsWorkspaceViewModel(
        IProjectFileDialogService fileDialogs,
        Action<string> setStatus,
        Func<CustomModelAnimationHandoff, Task> openInAnimate,
        Func<string?> getRetailData0PakPath,
        CustomModelDeveloperToolsSettings? developerToolsSettings = null,
        Func<Task>? synchronizeProject = null,
        Action? returnToProjectModels = null,
        Func<string, CancellationToken, Task<Dl1RigTemplateResolution>>? resolveRigTemplate = null,
        Func<CancellationToken, Task<Dl1RetailAnimationPayload?>>? pickRetailAnimation = null)
    {
        _fileDialogs = fileDialogs ?? throw new ArgumentNullException(nameof(fileDialogs));
        _setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        _openInAnimate = openInAnimate ?? throw new ArgumentNullException(nameof(openInAnimate));
        _synchronizeProject = synchronizeProject ?? (() => Task.CompletedTask);
        _returnToProjectModels = returnToProjectModels ?? (() => { });
        _getRetailData0PakPath = getRetailData0PakPath ??
            throw new ArgumentNullException(nameof(getRetailData0PakPath));
        _developerToolsSettings = developerToolsSettings ??
            CustomModelDeveloperToolsSettings.CreateDefault();
        _developerToolsProjectRoot = _developerToolsSettings.LoadProjectRoot() ?? string.Empty;
        _cameraCoordinator.IsLinked = false;
        Viewport = new ViewportPaneViewModel(
            "Custom model preview",
            "DL1 output hierarchy, skinning, and authored textures",
            new ViewportSceneSource(
                _cameraCoordinator,
                ViewportSide.Target,
                new System.Numerics.Vector4(0.075f, 0.095f, 0.125f, 1.0f)));
        Timeline = new TimelineViewModel();
        Timeline.CurrentFrameChanged += OnTimelineFrameChanged;
        Conformance = new RigConformanceWizardViewModel(
            resolveRigTemplate ?? ((profile, _) => Task.FromResult(
                Dl1RigTemplateResolution.Failed(
                    profile,
                    "This workspace was created without access to an indexed Dying Light installation."))),
            value => _setStatus(value));
        Conformance.SetRetailClipPicker(pickRetailAnimation);
        Conformance.FitChanged += OnConformanceFitChanged;
        Conformance.ApplyRequested += OnConformanceApplyRequested;
        Conformance.PropertyChanged += OnConformancePropertyChanged;

        ImportFbxCommand = new AsyncRelayCommand(ImportFbxAsync, () => !IsBusy);
        OpenPackageCommand = new AsyncRelayCommand(OpenPackageAsync, () => !IsBusy);
        ReturnToProjectModelsCommand = new RelayCommand(
            _returnToProjectModels);
        SavePackageCommand = new RelayCommand(SavePackage, () => HasModel && !IsBusy);
        SelectTextureCommand = new RelayCommand(SelectTexture, () => SelectedMaterial is not null && !IsBusy);
        BuildCompletePackageCommand = new AsyncRelayCommand(
            BuildCompletePackageAsync,
            () => HasModel && !IsBusy);
        BuildLooseFilesCommand = new AsyncRelayCommand(BuildLooseFilesAsync, () => HasModel && !IsBusy);
        SelectModelCompilerCommand = new RelayCommand(SelectModelCompiler, () => !IsBusy);
        SelectDeveloperToolsProjectCommand = new RelayCommand(SelectDeveloperToolsProject, () => !IsBusy);
        DeployToDeveloperToolsProjectCommand = new AsyncRelayCommand(
            DeployToDeveloperToolsProjectAsync,
            CanDeployToDeveloperToolsProject);
        RollBackDeploymentCommand = new AsyncRelayCommand(
            RollBackDeploymentAsync,
            () => _canRollbackLastDeployment && File.Exists(_lastDeploymentManifestPath) && !IsBusy);
        BackUpLegacyOutputsCommand = new AsyncRelayCommand(
            BackUpLegacyOutputsAsync,
            () => !_legacyOutputPaths.IsEmpty && Directory.Exists(DeveloperToolsProjectRoot) && !IsBusy);
        BuildModelRpackCommand = new AsyncRelayCommand(BuildModelRpackAsync, () => HasModel && !IsBusy);
        ExportAnimationRpackCommand = new AsyncRelayCommand(
            ExportAnimationRpackAsync,
            () => HasModel && Animations.Any(static clip => clip.Included) && !IsBusy);
        OpenSelectedAnimationInAnimateCommand = new AsyncRelayCommand(
            OpenSelectedAnimationInAnimateAsync,
            () => HasModel && SelectedAnimation?.DecodedClip is not null && !IsBusy);
        OpenDeveloperToolsProjectCommand = new RelayCommand(
            () => OpenPath(DeveloperToolsProjectRoot),
            () => Directory.Exists(DeveloperToolsProjectRoot));
        OpenDeployedAnimationScriptCommand = new RelayCommand(
            () => OpenPath(_lastDeployedAnimationScriptPath),
            () => File.Exists(_lastDeployedAnimationScriptPath));
        OpenDeploymentReceiptCommand = new RelayCommand(
            () => OpenPath(_lastDeploymentManifestPath),
            () => File.Exists(_lastDeploymentManifestPath));
        FrameModelCommand = new RelayCommand(FrameModel, () => HasModel);
        ResetCameraCommand = new RelayCommand(ResetCamera);
        DuplicateSelectedAsHelperCommand = new RelayCommand(
            DuplicateSelectedAsHelper,
            () => HasModel && SelectedBone is not null && !IsBusy);
        SelectPreviewCameraCommand = new RelayCommand(
            SelectPreviewCamera,
            () => HasModel && SelectedBone is not null && !IsBusy);
        ApplyHelperTransformCommand = new RelayCommand(
            ApplySelectedHelperTransform,
            () => HasModel && SelectedBone?.AuthoredHelperId is not null && !IsBusy);
        UndoHelperEditCommand = new RelayCommand(
            UndoHelperEdit,
            () => _helperUndo.Count > 0 && !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        InitializeAnimationRefreshCommands();
        RestoreLatestDeploymentActions();
    }

    public event EventHandler? PersistenceStateChanged;

    public ViewportPaneViewModel Viewport { get; }

    public TimelineViewModel Timeline { get; }

    /// <summary>
    /// The DL1 rig-conformance wizard. It owns only decisions; committing one
    /// runs through this workspace so the change joins the ordinary undo and
    /// persistence flow.
    /// </summary>
    public RigConformanceWizardViewModel Conformance { get; }

    public ObservableCollection<CustomModelBoneItemViewModel> Bones { get; } = [];

    public ObservableCollection<CustomModelMaterialItemViewModel> Materials { get; } = [];

    public ObservableCollection<CustomModelAnimationClipItemViewModel> Animations { get; } = [];

    public ObservableCollection<CustomModelDiagnosticItemViewModel> Diagnostics { get; } = [];

    public ObservableCollection<DeveloperToolsDeploymentArtifactItemViewModel> DeploymentArtifacts { get; } = [];

    public IReadOnlyList<CustomModelRigMode> RigModes { get; } = Enum.GetValues<CustomModelRigMode>();

    public IReadOnlyList<Dl1RootMotionMode> RootMotionModes { get; } = Enum.GetValues<Dl1RootMotionMode>();

    public static IReadOnlyList<CustomModelPreviewModeChoice> PreviewModeChoices => PreviewModeChoicesValue;

    public IReadOnlyList<CustomModelTextureSemantic> TextureSemantics { get; } =
    [
        CustomModelTextureSemantic.BaseColor,
        CustomModelTextureSemantic.Normal,
        CustomModelTextureSemantic.Specular,
        CustomModelTextureSemantic.Mask,
    ];

    public IReadOnlyList<CustomModelNormalMapConvention> NormalMapConventions { get; } =
        Enum.GetValues<CustomModelNormalMapConvention>();

    public ObservableCollection<string> RootBoneNames { get; } = [];

    public IAsyncRelayCommand ImportFbxCommand { get; }

    public IAsyncRelayCommand OpenPackageCommand { get; }

    public IRelayCommand ReturnToProjectModelsCommand { get; }

    public IRelayCommand SavePackageCommand { get; }

    public IRelayCommand SelectTextureCommand { get; }

    public IAsyncRelayCommand BuildCompletePackageCommand { get; }

    public IAsyncRelayCommand BuildLooseFilesCommand { get; }

    public IRelayCommand SelectModelCompilerCommand { get; }

    public IRelayCommand SelectDeveloperToolsProjectCommand { get; }

    public IAsyncRelayCommand BuildModelRpackCommand { get; }

    public IAsyncRelayCommand ExportAnimationRpackCommand { get; }

    public IAsyncRelayCommand OpenSelectedAnimationInAnimateCommand { get; }

    public IRelayCommand OpenDeveloperToolsProjectCommand { get; }

    public IRelayCommand OpenDeployedAnimationScriptCommand { get; }

    public IRelayCommand OpenDeploymentReceiptCommand { get; }

    public IAsyncRelayCommand DeployToDeveloperToolsProjectCommand { get; }

    public IAsyncRelayCommand RollBackDeploymentCommand { get; }

    public IAsyncRelayCommand BackUpLegacyOutputsCommand { get; }

    public IRelayCommand FrameModelCommand { get; }

    public IRelayCommand ResetCameraCommand { get; }

    public IRelayCommand DuplicateSelectedAsHelperCommand { get; }

    public IRelayCommand SelectPreviewCameraCommand { get; }

    public IRelayCommand ApplyHelperTransformCommand { get; }

    public IRelayCommand UndoHelperEditCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public bool HasModel => _model is not null;

    internal long PersistenceRevision => Volatile.Read(ref _authoringRevision);

    public bool CanChangeRigMode => !HasModel && !IsBusy;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanChangeRigMode));
                NotifyCommands();
            }
        }
    }

    public string ModelName
    {
        get => _modelName;
        set
        {
            if (SetProperty(ref _modelName, string.IsNullOrWhiteSpace(value) ? "Untitled model" : value.Trim()))
            {
                MarkAuthoringChanged();
            }
        }
    }

    public CustomModelRigMode SelectedRigMode
    {
        get => _selectedRigMode;
        set
        {
            if (HasModel)
            {
                if (_selectedRigMode != value)
                {
                    OnPropertyChanged();
                    BuildStatus = "Rig treatment is selected before import. Re-import the FBX to use a different treatment.";
                }

                return;
            }

            if (SetProperty(ref _selectedRigMode, value))
            {
                MarkAuthoringChanged();
            }
        }
    }

    public string ResourceName
    {
        get => _resourceName;
        set
        {
            string previousResourceName = _resourceName;
            string nextResourceName = value ?? string.Empty;
            if (SetProperty(ref _resourceName, nextResourceName))
            {
                if (!_characterIdWasExplicitlyEdited ||
                    string.IsNullOrWhiteSpace(_characterId) ||
                    string.Equals(_characterId, previousResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    SetProperty(ref _characterId, nextResourceName, nameof(CharacterId));
                }

                MarkAuthoringChanged();
                InvalidateDeploymentPreflight();
            }
        }
    }

    public string CharacterId
    {
        get => _characterId;
        set
        {
            if (SetProperty(ref _characterId, value ?? string.Empty))
            {
                _characterIdWasExplicitlyEdited = true;
                MarkAuthoringChanged();
                InvalidateDeploymentPreflight();
            }
        }
    }

    public string SurfaceName
    {
        get => _surfaceName;
        set
        {
            if (SetProperty(ref _surfaceName, value ?? string.Empty))
            {
                MarkAuthoringChanged();
                InvalidateDeploymentPreflight();
            }
        }
    }

    public string AnimationScriptAlias
    {
        get => _animationScriptAlias;
        set
        {
            if (SetProperty(ref _animationScriptAlias, value ?? string.Empty))
            {
                MarkAuthoringChanged();
                InvalidateDeploymentPreflight();
                OnPropertyChanged(nameof(AnimationScriptAliasSummary));
            }
        }
    }

    /// <summary>
    /// States, in the operator's terms, which script the deployed character
    /// will redirect to and where that file lands.
    /// </summary>
    public string AnimationScriptAliasSummary =>
        string.IsNullOrWhiteSpace(AnimationScriptAlias)
            ? "No library set. Deploying the character emits an ASCR with no target; set the bank it should drive."
            : $"The deployed ASCR redirects to '{AnimationScriptAlias.Trim()}.scr' " +
                $"(data/characters/animations/animscripts/{AnimationScriptAlias.Trim()}.scr).";

    public string DeveloperToolsProjectRoot
    {
        get => _developerToolsProjectRoot;
        private set
        {
            if (SetProperty(ref _developerToolsProjectRoot, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DeveloperToolsProjectStatus));
                InvalidateDeploymentPreflight();
                ResetAnimationRefreshState(
                    "Choose or complete a schema-2 deployment before requesting an animation refresh.");
                OpenDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
                NotifyCommands();
            }
        }
    }

    public string DeveloperToolsProjectStatus => Directory.Exists(DeveloperToolsProjectRoot)
        ? DeveloperToolsProjectRoot
        : "No Developer Tools project selected. This machine-local choice is not stored in .dlrmodel files.";

    public bool InstallLooseAnm2
    {
        get => _installLooseAnm2;
        set
        {
            if (SetProperty(ref _installLooseAnm2, value))
            {
                InvalidateDeploymentPreflight();
            }
        }
    }

    public bool ExportPortableAnimationRpack
    {
        get => _exportPortableAnimationRpack;
        set
        {
            if (SetProperty(ref _exportPortableAnimationRpack, value))
            {
                InvalidateDeploymentPreflight();
            }
        }
    }

    public string DeploymentPathPreview
    {
        get
        {
            string character = DisplaySafeCharacterId(CharacterId, ResourceName);
            string model = DisplaySafeIdentity(ResourceName, "custom_model", "model");
            string library = DisplaySafeIdentity(AnimationScriptAlias, model, "animation_library");
            int animationCount = Animations.Count(static item => item.Included && item.DecodedClip is not null);
            var lines = new List<string>
            {
                $"data/characters/{character}/{model}.msh, .chr, .bscr, .ascr",
                $"data/characters/animations/animscripts/{library}.scr",
                $"assets_pc/characters/{character}/{model}.msh_obj",
                $"assets_pc/characters/animations/<clip>.anm2_obj ({animationCount:N0})",
            };
            if (InstallLooseAnm2)
            {
                lines.Add("data/characters/animations/<clip>.anm2");
            }

            if (ExportPortableAnimationRpack)
            {
                lines.Add($"out/ReAnimated/{model}/{library}_pc.rpack (optional portable copy)");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    public string DeploymentPreflightSummary
    {
        get => _deploymentPreflightSummary;
        private set => SetProperty(ref _deploymentPreflightSummary, value);
    }

    public CustomModelPreviewModeChoice SelectedPreviewMode
    {
        get => _selectedPreviewMode;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedPreviewMode, value) && HasModel)
            {
                MarkAuthoringChanged();
                RefreshPreview();
                FrameModel();
            }
        }
    }

    public CustomModelTextureSemantic SelectedTextureSemantic
    {
        get => _selectedTextureSemantic;
        set
        {
            if (SetProperty(ref _selectedTextureSemantic, value))
            {
                OnPropertyChanged(nameof(IsNormalTextureSelected));
            }
        }
    }

    public bool IsNormalTextureSelected =>
        SelectedTextureSemantic == CustomModelTextureSemantic.Normal;

    public CustomModelNormalMapConvention SelectedNormalMapConvention
    {
        get => _selectedNormalMapConvention;
        set => SetProperty(ref _selectedNormalMapConvention, value);
    }

    public bool FlipTextureCoordinateV
    {
        get => _flipTextureCoordinateV;
        set
        {
            if (SetProperty(ref _flipTextureCoordinateV, value) && _model is not null)
            {
                MarkAuthoringChanged();
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
                InvalidateDeploymentPreflight();
                DeployToDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string CompilerStatus => File.Exists(CompilerExecutablePath)
        ? $"Developer Tools compiler: {CompilerExecutablePath}"
        : "Developer Tools compiler not selected. Loose-source and portable RPack export remain available; Developer Tools deployment is unavailable.";

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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

                // A skinned mesh still needs its evaluated skeleton palette
                // when the hierarchy overlay is hidden.  ShowBones is a
                // presentation toggle only; rebuilding the preview without a
                // skeleton makes every skinned draw lose its deformation
                // source and disappear.
                ApplySkeletonVisibility();
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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

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
                if (HasModel)
                {
                    MarkAuthoringChanged();
                }

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
                LoadSelectedHelperTransform();
                OnPropertyChanged(nameof(CanEditSelectedHelper));
                DuplicateSelectedAsHelperCommand.NotifyCanExecuteChanged();
                SelectPreviewCameraCommand.NotifyCanExecuteChanged();
                ApplyHelperTransformCommand.NotifyCanExecuteChanged();
                RefreshPreview();
            }
        }
    }

    public IReadOnlyList<CustomModelAuthoredHelperKind> HelperKinds { get; } =
        Enum.GetValues<CustomModelAuthoredHelperKind>();

    public CustomModelAuthoredHelperKind SelectedHelperKind
    {
        get => _selectedHelperKind;
        set => SetProperty(ref _selectedHelperKind, value);
    }

    public string NewHelperName
    {
        get => _newHelperName;
        set => SetProperty(ref _newHelperName, value ?? string.Empty);
    }

    public bool CanEditSelectedHelper => SelectedBone?.AuthoredHelperId is not null;

    public double HelperTranslationX
    {
        get => _helperTranslationX;
        set => SetProperty(ref _helperTranslationX, value);
    }

    public double HelperTranslationY
    {
        get => _helperTranslationY;
        set => SetProperty(ref _helperTranslationY, value);
    }

    public double HelperTranslationZ
    {
        get => _helperTranslationZ;
        set => SetProperty(ref _helperTranslationZ, value);
    }

    public double HelperRotationX
    {
        get => _helperRotationX;
        set => SetProperty(ref _helperRotationX, value);
    }

    public double HelperRotationY
    {
        get => _helperRotationY;
        set => SetProperty(ref _helperRotationY, value);
    }

    public double HelperRotationZ
    {
        get => _helperRotationZ;
        set => SetProperty(ref _helperRotationZ, value);
    }

    public string PreviewCameraStatus => _model?.Package.Document.Camera.ActivePreviewNodeName is { } name
        ? string.Equals(name, CustomModelHelperAuthoring.GameCameraName, StringComparison.Ordinal)
            ? "Preview and export camera: EyeCamera"
            : $"Editor-only preview camera: {name}"
        : "No model-node preview camera selected";

    public void Tick(DateTimeOffset now) => Timeline.Tick(now);

    public async Task ImportPathAsync(
        string path,
        CustomModelRigMode rigMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelsWorkspaceSessionSnapshot previous = CaptureProjectSession();
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        try
        {
            bool isReimport = _model is not null;
            FbxModelAuthoringImportOptions options = new() { RigMode = rigMode };
            FbxModelAuthoringImportResult imported;
            if (_model is null)
            {
                try
                {
                    imported = await FbxModelAuthoringImporter.ImportFileAsync(
                        path,
                        options,
                        token);
                }
                catch (InvalidDataException exception) when (
                    IsRecoverableMorphImportFailure(exception))
                {
                    EnsureCurrent(generation, token);
                    if (!_fileDialogs.ConfirmCustomModelImportWithoutMorphs(
                            Path.GetFileName(path),
                            exception.Message))
                    {
                        BuildStatus = "Custom-model import canceled; invalid blend shapes were not discarded";
                        _setStatus(BuildStatus);
                        return;
                    }

                    imported = await FbxModelAuthoringImporter.ImportFileAsync(
                        path,
                        options with { IgnoreMorphChannels = true },
                        token);
                }
            }
            else
            {
                byte[] replacementBytes = await File.ReadAllBytesAsync(path, token);
                CustomModelReimportPreview preview;
                try
                {
                    preview = await Task.Run(
                        () => FbxModelAuthoringImporter.PreviewReimport(
                            _model.Package,
                            replacementBytes,
                            Path.GetFileName(path),
                            options,
                            token),
                        token);
                }
                catch (InvalidDataException exception) when (
                    IsRecoverableMorphImportFailure(exception))
                {
                    EnsureCurrent(generation, token);
                    if (!_fileDialogs.ConfirmCustomModelImportWithoutMorphs(
                            Path.GetFileName(path),
                            exception.Message))
                    {
                        BuildStatus = "Custom-model reimport canceled; invalid blend shapes were not discarded";
                        _setStatus(BuildStatus);
                        return;
                    }

                    preview = await Task.Run(
                        () => FbxModelAuthoringImporter.PreviewReimport(
                            _model.Package,
                            replacementBytes,
                            Path.GetFileName(path),
                            options with { IgnoreMorphChannels = true },
                            token),
                        token);
                }
                EnsureCurrent(generation, token);
                if (!_fileDialogs.ConfirmCustomModelReimport(
                        Path.GetFileName(path),
                        preview.BoneAndHelperMappingsBecomeStale,
                        preview.FacialMappingsBecomeStale))
                {
                    BuildStatus = "Custom-model reimport canceled after validation; current model unchanged";
                    _setStatus(BuildStatus);
                    return;
                }

                imported = preview.Replacement;
            }

            EnsureCurrent(generation, token);
            CommitModel(
                imported,
                path,
                packagePath: null,
                markAuthoringChanged: false);
            await SynchronizeImportedModelAsync(previous, token);
            _setStatus(isReimport
                ? $"Reimported custom model {Path.GetFileName(path)} after validation"
                : $"Imported custom model {Path.GetFileName(path)}");
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
            UnauthorizedAccessException or
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

    private static bool IsRecoverableMorphImportFailure(
        InvalidDataException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string message = exception.Message;
        return message.Contains("BlendShape", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("morph", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("Shape '", StringComparison.Ordinal);
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

        await OpenPackagePathAsync(path);
    }

    public async Task OpenPackagePathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ModelsWorkspaceSessionSnapshot previous = CaptureProjectSession();
        long generation = BeginOperation(cancellationToken, out CancellationToken token);
        try
        {
            CustomModelPackage package = await Task.Run(() => CustomModelPackageSerializer.Load(path), token);
            FbxModelAuthoringImportResult decoded = await Task.Run(
                () => FbxModelAuthoringImporter.ImportPackage(package, token),
                token);
            EnsureCurrent(generation, token);
            CommitModel(
                decoded,
                sourcePath: null,
                packagePath: path,
                markAuthoringChanged: false);
            await SynchronizeImportedModelAsync(previous, token);
            _setStatus($"Opened custom model package {Path.GetFileName(path)}");
        }
        catch (OperationCanceledException)
        {
            _setStatus("Custom-model package open canceled; previous model retained");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException or OverflowException or
            CustomModelFormatException)
        {
            BuildStatus = $"Open failed: {exception.Message}";
            _setStatus("Custom-model package open failed; previous model retained");
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task SynchronizeImportedModelAsync(
        ModelsWorkspaceSessionSnapshot previous,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _authoringRevision);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _synchronizeProject();
            PersistenceStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            RestoreProjectSession(previous);
            throw;
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
            UnauthorizedAccessException or InvalidOperationException or CustomModelFormatException)
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
            string extension = NormalizeTextureExtension(path);
            if (!CustomModelTextureDecoder.TryDecode(
                    bytes,
                    Path.GetFileName(path),
                    out _,
                    out string failureReason))
            {
                BuildStatus = $"Texture selection failed: {failureReason}";
                _setStatus(BuildStatus);
                return;
            }

            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string entryPath = $"textures/user/{hash}{extension}";
            CustomModelMaterial material = SelectedMaterial.Contract;
            CustomModelTextureBinding binding = new()
            {
                Id = Guid.NewGuid(),
                Semantic = SelectedTextureSemantic,
                SourceKind = CustomModelTextureSourceKind.UserOverride,
                ColorSpace = SelectedTextureSemantic == CustomModelTextureSemantic.BaseColor
                    ? CustomModelTextureColorSpace.Srgb
                    : CustomModelTextureColorSpace.Linear,
                NormalMapConvention = SelectedTextureSemantic == CustomModelTextureSemantic.Normal
                    ? SelectedNormalMapConvention
                    : CustomModelNormalMapConvention.RgbOpenGl,
                DisplayName = Path.GetFileName(path),
                PackageEntryPath = entryPath,
                OriginalReference = Path.GetFileName(path),
                ContentSha256 = hash,
                MediaType = TextureMediaType(extension),
            };
            material = material with
            {
                Textures = material.Textures
                    .Where(texture => texture.Semantic != SelectedTextureSemantic)
                    .Append(binding)
                    .OrderBy(static texture => texture.Semantic)
                    .ToImmutableArray(),
            };
            CustomModelDocument document = _model.Package.Document with
            {
                Materials = _model.Package.Document.Materials
                    .Select(candidate => candidate.Id == material.Id ? material : candidate)
                    .ToImmutableArray(),
                LastBuildReceipt = null,
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
            MarkAuthoringChanged();
            PopulateMaterials();
            SelectedMaterial = Materials.First(item => item.Contract.Id == material.Id);
            RefreshPreview();
            BuildStatus = $"Selected {SelectedTextureSemantic} texture {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or OverflowException)
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
                $"Source model ready: {Path.GetFileName(result.SourceMshPath)}, " +
                $"{Path.GetFileName(result.CharacterDefinitionPath)}, {Path.GetFileName(result.BoneScriptPath)}. " +
                "Use Compile model RPack to create the validated .msh_obj and RPack; .skn remains unsupported.";
            _setStatus($"Built custom-model source files in {directory}");
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Model build canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or
            UnauthorizedAccessException or OverflowException)
        {
            BuildStatus = $"Build failed: {exception.Message}";
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task BuildCompletePackageAsync()
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
                BuildStatus = "Complete package build canceled: select the Dying Light Developer Tools compiler first.";
                return;
            }
        }

        if (!TryShowModelPicker(
                "Choose a parent folder for the complete DL1 model package",
                () => _fileDialogs.ShowSelectCustomModelOutputDirectory(_packagePath ?? _sourcePath),
                out string? parentDirectory))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            FbxModelAuthoringImportResult buildModel = _model ??
                throw new InvalidOperationException("The custom-model authoring document is no longer available.");
            long buildRevision = Volatile.Read(ref _authoringRevision);
            string resourceName = Dl1SourceModelWriter.SanitizeName(ResourceName, 55);
            BuildStatus = "Building and validating the complete DL1 model package in staging...";
            Dl1CustomModelPackageResult result = await Dl1CustomModelPackageBuilder.BuildAsync(
                new Dl1CustomModelPackageRequest
                {
                    Model = buildModel,
                    ParentOutputDirectory = parentDirectory,
                    CompilerExecutablePath = CompilerExecutablePath,
                    RetailData0PakPath = _getRetailData0PakPath(),
                    ResourceName = resourceName,
                    SurfaceName = SurfaceName,
                    AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                        ? null
                        : AnimationScriptAlias.Trim(),
                    AnimationSelections = buildModel.Package.Document.AnimationClips,
                },
                token);
            EnsureCurrentGeneration(generation);
            bool currentDraftMatchesBuild =
                ReferenceEquals(_model, buildModel) &&
                buildRevision == Volatile.Read(ref _authoringRevision);
            CustomModelDocument document = buildModel.Package.Document with
            {
                LastBuildReceipt = currentDraftMatchesBuild
                    ? result.CompiledModel.BuildReceipt
                    : null,
            };
            document.Validate();
            if (currentDraftMatchesBuild)
            {
                _model = buildModel with
                {
                    Package = new CustomModelPackage(
                        document,
                        buildModel.Package.SourceFbx,
                        buildModel.Package.TexturePayloads),
                };
            }
            else
            {
                ClearLastBuildReceipt();
            }

            BuildStatus = result.AnimationLibrary is null
                ? $"Complete DL1 model package built: {result.PackageDirectory}"
                : $"Complete DL1 model + {result.AnimationLibrary.AnimationNames.Length:N0} animation(s): {result.PackageDirectory}";
            if (!currentDraftMatchesBuild)
            {
                BuildStatus += " The authoring draft changed during the build, so its compiler-validation receipt was not attached; rebuild the current draft before publishing it.";
            }

            _setStatus(BuildStatus);
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Complete package build canceled; the previous valid package was retained.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or OverflowException or TimeoutException or Win32Exception)
        {
            BuildStatus = $"Complete package build failed; the previous valid package was retained: {exception.Message}";
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private void SelectModelCompiler()
    {
        try
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
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException)
        {
            BuildStatus = $"Compiler selection failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private void SelectDeveloperToolsProject()
    {
        try
        {
            string? path = _fileDialogs.ShowSelectDl1DeveloperToolsProjectDialog(
                Directory.Exists(DeveloperToolsProjectRoot)
                    ? DeveloperToolsProjectRoot
                    : _packagePath ?? _sourcePath);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            path = Path.GetFullPath(path);
            _developerToolsSettings.SaveProjectRoot(path);
            DeveloperToolsProjectRoot = path;
            RestoreLatestDeploymentActions();
            BuildStatus = "Selected Developer Tools project. Review the derived deployment paths before deploying.";
            _setStatus(BuildStatus);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException)
        {
            BuildStatus = $"Developer Tools project selection failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private bool CanDeployToDeveloperToolsProject() =>
        HasModel &&
        !IsBusy &&
        Directory.Exists(DeveloperToolsProjectRoot) &&
        Animations.Any(static clip => clip.Included && clip.DecodedClip is not null);

    private Dl1DeveloperToolsDeploymentRequest CreateDeveloperToolsDeploymentRequest(
        ImmutableDictionary<string, Dl1DeploymentConflictResolution>? conflictResolutions = null)
    {
        FbxModelAuthoringImportResult model = _model ??
            throw new InvalidOperationException("Import or open a custom model before deployment.");
        string? retailData0PakPath = _getRetailData0PakPath();
        if (string.IsNullOrWhiteSpace(retailData0PakPath))
        {
            throw new FileNotFoundException(
                "Retail DL1 Data0.pak is unavailable. Configure a complete Dying Light 1 installation before deployment.");
        }

        return new Dl1DeveloperToolsDeploymentRequest
        {
            Model = model,
            ProjectRoot = DeveloperToolsProjectRoot,
            CompilerExecutablePath = CompilerExecutablePath,
            RetailData0PakPath = retailData0PakPath,
            CharacterId = CharacterId,
            ModelResourceName = ResourceName,
            SurfaceName = SurfaceName,
            AnimationLibraryName = AnimationScriptAlias,
            AnimationSelections = Animations
                .Select(static animation => animation.ToContract())
                .ToImmutableArray(),
            InstallLooseAnm2 = InstallLooseAnm2,
            ExportPortableAnimationRpack = ExportPortableAnimationRpack,
            ConflictResolutions = conflictResolutions ??
                ImmutableDictionary<string, Dl1DeploymentConflictResolution>.Empty
                    .WithComparers(StringComparer.OrdinalIgnoreCase),
        };
    }

    private async Task DeployToDeveloperToolsProjectAsync()
    {
        if (_model is null || !Directory.Exists(DeveloperToolsProjectRoot))
        {
            return;
        }

        if (!File.Exists(CompilerExecutablePath))
        {
            SelectModelCompiler();
            if (!File.Exists(CompilerExecutablePath))
            {
                BuildStatus = "Developer Tools deployment canceled: select Techland's compiler first.";
                return;
            }
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            BuildStatus = "Preparing canonical Developer Tools deployment preflight...";
            _setStatus(BuildStatus);
            Dl1DeveloperToolsDeploymentRequest request = CreateDeveloperToolsDeploymentRequest();
            Dl1DeveloperToolsDeploymentPlan plan = await Dl1DeveloperToolsProjectDeployer.PreflightAsync(
                request,
                token);
            EnsureCurrent(generation, token);
            PublishDeploymentPlan(plan);

            if (!plan.CanDeploy)
            {
                var resolutionBuilder = ImmutableDictionary.CreateBuilder<string, Dl1DeploymentConflictResolution>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (Dl1DeveloperToolsDeploymentConflict conflict in plan.Conflicts)
                {
                    DeveloperToolsDeploymentConflictDecision decision =
                        _fileDialogs.ResolveDeveloperToolsDeploymentConflict(
                            conflict.RelativePath,
                            conflict.Message,
                            conflict.CanSkip);
                    if (decision == DeveloperToolsDeploymentConflictDecision.Cancel)
                    {
                        BuildStatus =
                            "Developer Tools deployment canceled at conflict review; the project was not changed.";
                        return;
                    }

                    resolutionBuilder[conflict.RelativePath] = decision ==
                        DeveloperToolsDeploymentConflictDecision.BackUpAndReplace
                            ? Dl1DeploymentConflictResolution.BackUpAndReplace
                            : Dl1DeploymentConflictResolution.Skip;
                }

                request = CreateDeveloperToolsDeploymentRequest(resolutionBuilder.ToImmutable());
                plan = await Dl1DeveloperToolsProjectDeployer.PreflightAsync(request, token);
                EnsureCurrent(generation, token);
                PublishDeploymentPlan(plan);
                if (!plan.CanDeploy)
                {
                    throw new Dl1DeveloperToolsDeploymentConflictException(plan);
                }
            }

            if (!_fileDialogs.ConfirmDeveloperToolsDeployment(
                    DeveloperToolsProjectRoot,
                    plan.Artifacts.Length,
                    plan.AnimationNames.Length))
            {
                BuildStatus = "Developer Tools deployment canceled after preflight; the project was not changed.";
                return;
            }

            BuildStatus = "Compiling and validating the staged Developer Tools deployment...";
            _setStatus(BuildStatus);
            Dl1DeveloperToolsDeploymentResult result = await Dl1DeveloperToolsProjectDeployer.DeployAsync(
                request,
                token);
            EnsureCurrent(generation, token);
            PublishDeploymentPlan(result.Plan);
            _lastDeploymentManifestPath = Path.GetFullPath(result.ReceiptPath);
            _lastDeployedAnimationScriptPath = ResolveProjectRelativePath(
                DeveloperToolsProjectRoot,
                result.Receipt.AnimationScriptRelativePath);
            _canRollbackLastDeployment = true;
            QueueAutomaticDeveloperToolsAnimationRefresh(
                DeveloperToolsProjectRoot,
                result.Receipt);
            OpenDeployedAnimationScriptCommand.NotifyCanExecuteChanged();
            OpenDeploymentReceiptCommand.NotifyCanExecuteChanged();
            RollBackDeploymentCommand.NotifyCanExecuteChanged();

            int looseAnimationCount = result.Receipt.Artifacts.Count(static artifact =>
                artifact.Role == Dl1DeploymentArtifactRole.Source &&
                artifact.RelativePath.EndsWith(".anm2", StringComparison.OrdinalIgnoreCase));
            int compiledAnimationCount = result.Receipt.Artifacts.Count(static artifact =>
                artifact.Role == Dl1DeploymentArtifactRole.Compiled &&
                artifact.RelativePath.EndsWith(".anm2_obj", StringComparison.OrdinalIgnoreCase));
            string duplicateNote = result.Plan.StaleDuplicateResources.IsEmpty
                ? "No stale duplicate resources were found."
                : $"{result.Plan.StaleDuplicateResources.Length:N0} stale duplicate resource(s) remain; review the preflight details.";
            BuildStatus =
                $"Developer Tools deployment complete. {result.Receipt.ModelResourceName}.ascr resolves to " +
                $"{result.Receipt.AnimationLibraryName}.scr at {result.Receipt.AnimationScriptRelativePath}; " +
                $"installed {looseAnimationCount:N0} loose and " +
                $"{compiledAnimationCount:N0} compiled animation(s). " +
                $"The project-owned animation runtime RPack is ready for the selected-model loader route and an Editor refresh request was queued. " +
                (result.Plan.ExportPortableAnimationRpack
                    ? "A separate portable export copy was also written. "
                    : "The optional portable export copy was disabled. ") +
                duplicateNote +
                (result.Plan.StaleDuplicateResources.IsEmpty
                    ? string.Empty
                    : " " + string.Join("; ", result.Plan.StaleDuplicateResources));
            _setStatus(BuildStatus);
            if (result.Receipt.Warnings.Contains(
                    Dl1OfficialModelCompiler.MaterialExportWarning,
                    StringComparer.Ordinal))
            {
                _fileDialogs.ShowOperationNotice(
                    "Export",
                    Dl1OfficialModelCompiler.MaterialExportWarning);
            }
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Developer Tools deployment canceled; the previous project state was retained.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or OverflowException or TimeoutException or
            Win32Exception or NotSupportedException)
        {
            BuildStatus =
                $"Developer Tools deployment failed; the previous project state was retained: {exception.Message}";
            _setStatus(BuildStatus);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task RollBackDeploymentAsync()
    {
        string receiptPath = _lastDeploymentManifestPath;
        if (!File.Exists(receiptPath) ||
            !_fileDialogs.ConfirmDeveloperToolsDeploymentRollback(receiptPath))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            BuildStatus = "Validating and rolling back the last Developer Tools deployment...";
            await Dl1DeveloperToolsProjectDeployer.RollbackAsync(receiptPath, token);
            EnsureCurrent(generation, token);
            _canRollbackLastDeployment = false;
            RollBackDeploymentCommand.NotifyCanExecuteChanged();
            BuildStatus = "Developer Tools deployment rolled back. The receipt records the completed rollback.";
            _setStatus(BuildStatus);
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Deployment rollback canceled; the Developer Tools project was retained.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            BuildStatus = $"Deployment rollback failed without replacing modified project files: {exception.Message}";
            _setStatus(BuildStatus);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private async Task BackUpLegacyOutputsAsync()
    {
        ImmutableArray<string> paths = _legacyOutputPaths;
        if (paths.IsEmpty ||
            !_fileDialogs.ConfirmLegacyDeveloperToolsOutputBackup(
                DeveloperToolsProjectRoot,
                paths))
        {
            return;
        }

        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            BuildStatus = "Moving legacy nested output into a recoverable project-local backup...";
            Dl1DeveloperToolsLegacyBackupResult result =
                await Dl1DeveloperToolsProjectDeployer.BackUpLegacyOutputsAsync(
                    DeveloperToolsProjectRoot,
                    token);
            EnsureCurrent(generation, token);
            _legacyOutputPaths = [];
            BackUpLegacyOutputsCommand.NotifyCanExecuteChanged();
            DeploymentPreflightSummary =
                $"Backed up {result.BackedUpRelativePaths.Length:N0} legacy output director{(result.BackedUpRelativePaths.Length == 1 ? "y" : "ies")} " +
                $"under {result.BackupRootRelativePath}. Run deployment again for a fresh preflight.";
            BuildStatus = "Legacy nested output was moved to a recoverable backup; no files were deleted.";
            _setStatus(BuildStatus);
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Legacy-output backup canceled; the project was retained.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            BuildStatus = $"Legacy-output backup failed without deleting project data: {exception.Message}";
            _setStatus(BuildStatus);
        }
        finally
        {
            EndOperation(generation);
        }
    }

    private void RestoreLatestDeploymentActions()
    {
        _lastDeploymentManifestPath = string.Empty;
        _lastDeployedAnimationScriptPath = string.Empty;
        _canRollbackLastDeployment = false;
        Dl1DeveloperToolsDeploymentReceipt? receipt = null;
        if (Directory.Exists(DeveloperToolsProjectRoot))
        {
            receipt =
                Dl1DeveloperToolsProjectDeployer.LoadLatestActiveReceipt(DeveloperToolsProjectRoot);
            if (receipt is not null)
            {
                _lastDeploymentManifestPath = receipt.ManifestPath;
                _lastDeployedAnimationScriptPath = ResolveProjectRelativePath(
                    DeveloperToolsProjectRoot,
                    receipt.AnimationScriptRelativePath);
                _canRollbackLastDeployment = true;
            }
        }

        OpenDeployedAnimationScriptCommand.NotifyCanExecuteChanged();
        OpenDeploymentReceiptCommand.NotifyCanExecuteChanged();
        RollBackDeploymentCommand.NotifyCanExecuteChanged();
        RestoreLatestAnimationRefreshState(receipt);
        NotifyAnimationRefreshCommands();
    }

    private void PublishDeploymentPlan(Dl1DeveloperToolsDeploymentPlan plan)
    {
        DeploymentArtifacts.Clear();
        foreach (Dl1DeveloperToolsDeploymentArtifact artifact in plan.Artifacts)
        {
            DeploymentArtifacts.Add(new DeveloperToolsDeploymentArtifactItemViewModel(artifact));
        }

        var lines = new List<string>
        {
            $"ASCR redirect: {plan.ModelResourceName}.ascr -> {plan.AnimationLibraryName}.scr",
            $"SCR: {plan.AnimationScriptRelativePath}",
            $"Animation mapping ({plan.Animations.Length:N0}):",
            $"Artifacts: {plan.Artifacts.Length:N0}; conflicts: {plan.Conflicts.Length:N0}",
        };
        lines.AddRange(plan.Animations.Select(static animation =>
            $"  {animation.SourceName} -> {animation.Anm2FileName}; {animation.ScrSequence}"));
        lines.Add($"Loader runtime RPack: {plan.AnimationRuntimePackRelativePath}");
        lines.Add(plan.PortableRpackRelativePath is null
            ? "Optional portable animation RPack copy: disabled"
            : $"Optional portable animation RPack copy: {plan.PortableRpackRelativePath}");
        _legacyOutputPaths = plan.LegacyOutputPaths;
        BackUpLegacyOutputsCommand.NotifyCanExecuteChanged();
        if (!plan.LegacyOutputWarnings.IsEmpty)
        {
            lines.Add("Legacy/unmounted output: " + string.Join("; ", plan.LegacyOutputWarnings));
        }

        if (!plan.StaleDuplicateResources.IsEmpty)
        {
            lines.Add("Duplicate resources: " + string.Join("; ", plan.StaleDuplicateResources));
        }

        DeploymentPreflightSummary = string.Join(Environment.NewLine, lines);
    }

    private void InvalidateDeploymentPreflight()
    {
        DeploymentArtifacts.Clear();
        _legacyOutputPaths = [];
        BackUpLegacyOutputsCommand.NotifyCanExecuteChanged();
        DeploymentPreflightSummary =
            "Authoring or deployment settings changed. Deploy will run a fresh staged preflight before writing.";
        OnPropertyChanged(nameof(DeploymentPathPreview));
        DeployToDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
    }

    private static string ResolveProjectRelativePath(string projectRoot, string relativePath) =>
        Path.GetFullPath(
            Path.Combine(
                projectRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

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
            FbxModelAuthoringImportResult buildModel = _model ??
                throw new InvalidOperationException("The custom-model authoring document is no longer available.");
            long buildRevision = Volatile.Read(ref _authoringRevision);
            BuildStatus = "Compiling model in an isolated Developer Tools workspace...";
            Dl1OfficialModelCompilerResult result = await Dl1OfficialModelCompiler.CompileAsync(
                new Dl1OfficialModelCompilerRequest
                {
                    Model = buildModel,
                    CompilerExecutablePath = CompilerExecutablePath,
                    RetailData0PakPath = _getRetailData0PakPath(),
                    OutputRpackPath = path,
                    ResourceName = resourceName,
                    CharacterId = CharacterId,
                    SurfaceName = SurfaceName,
                    AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                        ? null
                        : AnimationScriptAlias.Trim(),
                },
                token);
            EnsureCurrent(generation, token);
            bool currentDraftMatchesBuild =
                ReferenceEquals(_model, buildModel) &&
                buildRevision == Volatile.Read(ref _authoringRevision);
            CustomModelDocument document = buildModel.Package.Document with
            {
                LastBuildReceipt = currentDraftMatchesBuild
                    ? result.BuildReceipt
                    : null,
            };
            document.Validate();
            if (currentDraftMatchesBuild)
            {
                _model = buildModel with
                {
                    Package = new CustomModelPackage(
                        document,
                        buildModel.Package.SourceFbx,
                        buildModel.Package.TexturePayloads),
                };
            }
            else
            {
                ClearLastBuildReceipt();
            }

            BuildStatus =
                $"Compiler-validated model bundle: {Path.GetFileName(result.OutputRpackPath)}" +
                (result.MaterialDatabasePath is null
                    ? string.Empty
                    : $" + {Path.GetFileName(result.MaterialDatabasePath)}") +
                $"; compiled mesh: {Path.GetFileName(result.CompiledMeshObjectPath)}. " +
                "The loose source build supplies CHR v4; .skn remains unsupported.";
            if (!currentDraftMatchesBuild)
            {
                BuildStatus += " The authoring draft changed during compilation, so its validation receipt was not attached; rebuild the current draft before publishing it.";
            }
            _setStatus($"Compiler-validated custom model RPack {Path.GetFileName(result.OutputRpackPath)}");
        }
        catch (OperationCanceledException)
        {
            BuildStatus = "Model RPack compile canceled; no staged output was published";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or OverflowException or TimeoutException or Win32Exception)
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
            exception is ArgumentException or InvalidDataException or InvalidOperationException or IOException or
            UnauthorizedAccessException or OverflowException)
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

        try
        {
            SyncDocument();
            CustomModelAnimationClip selection = _model.Package.Document.AnimationClips
                .First(candidate => candidate.Id == SelectedAnimation.Id);
            CustomModelPreviewPayload preview = CustomModelPreviewAdapter.Create(
                _model,
                clip,
                Timeline.CurrentFrame,
                mode: CustomModelPreviewMode.SourceFbx);
            await _openInAnimate(new CustomModelAnimationHandoff(
                _model,
                selection,
                clip,
                preview.Meshes));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or InvalidOperationException or
            IOException or UnauthorizedAccessException or NotSupportedException or OverflowException or
            CustomModelFormatException)
        {
            BuildStatus = $"Open in Animate failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    internal ModelsWorkspacePersistencePayload? CreatePersistencePayload()
    {
        if (_model is null)
        {
            return null;
        }

        SyncDocument();
        CustomModelPackage package = _model.Package;
        Dl1PreparedAuthoredRig? dl1Output = null;
        if (_model.Rig is not null)
        {
            try
            {
                dl1Output = Dl1CustomModelRigPreparer.Prepare(_model);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
            {
                BuildStatus =
                    $"DL1 output is not ready: {exception.Message}";
            }
        }
        string portableName = Dl1SourceModelWriter.SanitizeName(
            string.IsNullOrWhiteSpace(ResourceName)
                ? package.Document.Name
                : ResourceName,
            55);
        return new ModelsWorkspacePersistencePayload(
            package.Document.ModelId,
            $"{portableName}.dlrmodel",
            CustomModelPackageSerializer.Serialize(package),
            _model.Rig is null
                ? null
                : RigSignature.Compute(_model.Rig),
            package.Document.RigSignature,
            _model.Rig is null
                ? null
                : AnimationSkeletonSignature.Compute(_model.Rig),
            dl1Output is null
                ? null
                : RigSignature.Compute(dl1Output.PreviewRig),
            dl1Output?.Contract.DescriptorFingerprint,
            package.Document.MorphSignature,
            package.Document.BuildSettings.AnimationScriptAlias,
            package.Document.Camera.ActivePreviewNodeName,
            package.Document.CreateEffectiveBones().Count(static bone =>
                bone.Kind == BoneKind.Camera &&
                !bone.IsWeighted &&
                string.Equals(
                    bone.Name,
                    CustomModelHelperAuthoring.GameCameraName,
                    StringComparison.Ordinal)),
            _model.Rig?.Id,
            package.Document.AnimationClips
                .Select(clip => new ModelsWorkspaceEmbeddedStackPayload(
                    clip,
                    _model.AnimationClips.ContainsKey(clip.Id)))
                .ToImmutableArray(),
            SelectedAnimation?.Id,
            SelectedPreviewMode.Mode == CustomModelPreviewMode.SourceFbx
                ? ProjectCustomModelPreviewMode.SourceFbx
                : ProjectCustomModelPreviewMode.Dl1Output,
            ShowMeshes,
            ShowBones,
            ShowHelpers,
            ShowCameraHelpers,
            ShowPropHelpers);
    }

    internal async Task<PreparedModelsWorkspaceRestore> PrepareProjectRestoreAsync(
        string packagePath,
        ProjectModelsWorkspaceState state,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        ArgumentNullException.ThrowIfNull(state);
        string fullPath = Path.GetFullPath(packagePath);
        FbxModelAuthoringImportResult imported = await Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CustomModelPackage package = CustomModelPackageSerializer.Load(fullPath);
                FbxModelAuthoringImportResult decoded =
                    FbxModelAuthoringImporter.ImportPackage(package, cancellationToken);
                if (state.SelectedAnimationClipId is { } selectedClipId &&
                    decoded.Package.Document.AnimationClips.All(clip => clip.Id != selectedClipId))
                {
                    throw new InvalidDataException(
                        "The saved Models-workspace animation is not present in its custom-model package.");
                }

                return decoded;
            },
            cancellationToken).ConfigureAwait(true);
        return new PreparedModelsWorkspaceRestore(imported, fullPath, state);
    }

    internal ModelsWorkspaceSessionSnapshot CaptureProjectSession()
    {
        if (_model is not null)
        {
            SyncDocument();
        }

        return new ModelsWorkspaceSessionSnapshot(
            _model,
            _sourcePath,
            _packagePath,
            SelectedAnimation?.Id,
            SelectedPreviewMode.Mode,
            ShowMeshes,
            ShowBones,
            ShowHelpers,
            ShowCameraHelpers,
            ShowPropHelpers,
            PersistenceRevision);
    }

    internal void CommitProjectRestore(PreparedModelsWorkspaceRestore prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ApplyRestoredSession(
            prepared.Model,
            sourcePath: null,
            prepared.PackagePath,
            prepared.State.SelectedAnimationClipId,
            prepared.State.PreviewMode == ProjectCustomModelPreviewMode.SourceFbx
                ? CustomModelPreviewMode.SourceFbx
                : CustomModelPreviewMode.Dl1Output,
            prepared.State.ShowMeshes,
            prepared.State.ShowBones,
            prepared.State.ShowHelpers,
            prepared.State.ShowCameraHelpers,
            prepared.State.ShowPropHelpers,
            authoringRevision: 0);
    }

    internal void RestoreProjectSession(ModelsWorkspaceSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Model is null)
        {
            ClearProjectSession(snapshot.AuthoringRevision);
            return;
        }

        ApplyRestoredSession(
            snapshot.Model,
            snapshot.SourcePath,
            snapshot.PackagePath,
            snapshot.SelectedAnimationClipId,
            snapshot.PreviewMode,
            snapshot.ShowMeshes,
            snapshot.ShowBones,
            snapshot.ShowHelpers,
            snapshot.ShowCameraHelpers,
            snapshot.ShowPropHelpers,
            snapshot.AuthoringRevision);
    }

    internal void ClearProjectSession(long authoringRevision = 0)
    {
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        Interlocked.Increment(ref _operationGeneration);

        _suppressPersistenceNotifications = true;
        _suppressPreviewRefresh = true;
        try
        {
            foreach (CustomModelAnimationClipItemViewModel animation in Animations)
            {
                animation.PropertyChanged -= OnAnimationItemPropertyChanged;
            }

            _model = null;
            InvalidateConformancePreview();
            Conformance.SetModel(null);
            _previewSession = null;
            _helperUndo.Clear();
            _sourcePath = null;
            _packagePath = null;
            _modelName = "No custom model loaded";
            _selectedRigMode = CustomModelRigMode.Auto;
            _resourceName = "custom_model";
            _characterId = "custom_model";
            _characterIdWasExplicitlyEdited = false;
            _surfaceName = "default";
            _animationScriptAlias = string.Empty;
            _flipTextureCoordinateV = true;
            _selectedPreviewMode = PreviewModeChoicesValue[0];
            _selectedAnimation = null;
            _selectedMaterial = null;
            _selectedBone = null;
            Bones.Clear();
            RootBoneNames.Clear();
            Materials.Clear();
            Animations.Clear();
            Diagnostics.Clear();
            Summary = "Import a binary FBX to inspect its mesh, exact hierarchy, materials, and animation stacks.";
            BuildStatus = "Not built";
            Timeline.EndFrame = 1;
            Timeline.CurrentFrame = 0;
            Timeline.ReplaceTracks([]);
            Timeline.ReplaceCurves([]);
            Viewport.SceneSource.SetScene([], null, []);
            Viewport.SetDiagnosticOverlay(null);
            IsBusy = false;
            OnPropertyChanged(nameof(ModelName));
            OnPropertyChanged(nameof(SelectedRigMode));
            OnPropertyChanged(nameof(ResourceName));
            OnPropertyChanged(nameof(CharacterId));
            OnPropertyChanged(nameof(SurfaceName));
            OnPropertyChanged(nameof(AnimationScriptAlias));
            OnPropertyChanged(nameof(AnimationScriptAliasSummary));
            OnPropertyChanged(nameof(FlipTextureCoordinateV));
            OnPropertyChanged(nameof(SelectedPreviewMode));
            OnPropertyChanged(nameof(SelectedAnimation));
            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(SelectedBone));
            OnPropertyChanged(nameof(CanEditSelectedHelper));
            OnPropertyChanged(nameof(PreviewCameraStatus));
            OnPropertyChanged(nameof(HasModel));
            OnPropertyChanged(nameof(CanChangeRigMode));
        }
        finally
        {
            _suppressPreviewRefresh = false;
            _suppressPersistenceNotifications = false;
            Interlocked.Exchange(ref _authoringRevision, authoringRevision);
        }

        NotifyCommands();
    }

    private void ApplyRestoredSession(
        FbxModelAuthoringImportResult model,
        string? sourcePath,
        string? packagePath,
        Guid? selectedAnimationClipId,
        CustomModelPreviewMode previewMode,
        bool showMeshes,
        bool showBones,
        bool showHelpers,
        bool showCameraHelpers,
        bool showPropHelpers,
        long authoringRevision)
    {
        _suppressPersistenceNotifications = true;
        _suppressPreviewRefresh = true;
        try
        {
            CommitModel(model, sourcePath, packagePath, markAuthoringChanged: false);
            SelectedAnimation = selectedAnimationClipId is { } selectedId
                ? Animations.FirstOrDefault(animation => animation.Id == selectedId)
                : SelectedAnimation;
            SelectedPreviewMode = PreviewModeChoicesValue.First(choice => choice.Mode == previewMode);
            ShowMeshes = showMeshes;
            ShowBones = showBones;
            ShowHelpers = showHelpers;
            ShowCameraHelpers = showCameraHelpers;
            ShowPropHelpers = showPropHelpers;
        }
        finally
        {
            _suppressPreviewRefresh = false;
            _suppressPersistenceNotifications = false;
            Interlocked.Exchange(ref _authoringRevision, authoringRevision);
        }

        RefreshTimeline();
        RefreshPreview();
        FrameModel();
    }

    private void CommitModel(
        FbxModelAuthoringImportResult imported,
        string? sourcePath,
        string? packagePath,
        bool markAuthoringChanged = true)
    {
        imported = NormalizeBuildReceipt(imported, out string buildStatus);
        bool previousPersistenceSuppression = _suppressPersistenceNotifications;
        _suppressPersistenceNotifications = true;
        _suppressPreviewRefresh = true;
        try
        {
            _previewSession = null;
            _helperUndo.Clear();
            _model = imported;
            // A cached conformance preview belongs to the model it was built
            // from; keeping it across a load pairs the new model's meshes with
            // the old model's skeleton.
            InvalidateConformancePreview();
            Conformance.SetModel(imported);
            _sourcePath = sourcePath;
            _packagePath = packagePath;
            ModelName = imported.Package.Document.Name;
            SetProperty(ref _selectedRigMode, imported.Package.Document.RigMode, nameof(SelectedRigMode));
            ResourceName = imported.Package.Document.BuildSettings.ResourceName;
            _characterIdWasExplicitlyEdited =
                !string.IsNullOrWhiteSpace(imported.Package.Document.BuildSettings.CharacterId);
            SetProperty(
                ref _characterId,
                _characterIdWasExplicitlyEdited
                    ? imported.Package.Document.BuildSettings.CharacterId
                    : imported.Package.Document.BuildSettings.ResourceName,
                nameof(CharacterId));
            SurfaceName = imported.Package.Document.BuildSettings.SurfaceName;
            AnimationScriptAlias = imported.Package.Document.BuildSettings.AnimationScriptAlias ?? string.Empty;
            OnPropertyChanged(nameof(AnimationScriptAliasSummary));
            _flipTextureCoordinateV = imported.Package.Document.BuildSettings.FlipTextureCoordinateV;
            OnPropertyChanged(nameof(FlipTextureCoordinateV));
            BuildStatus = buildStatus;
            OnPropertyChanged(nameof(PreviewCameraStatus));

            PopulateHierarchyRows();

            PopulateMaterials();
            foreach (CustomModelAnimationClipItemViewModel animation in Animations)
            {
                animation.PropertyChanged -= OnAnimationItemPropertyChanged;
            }

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

            if (imported.Rig is not null)
            {
                try
                {
                    Dl1PreparedAuthoredRig prepared =
                        Dl1CustomModelRigPreparer.Prepare(imported);
                    foreach (Dl1AuthoredRigDecompositionDiagnostic diagnostic in
                             prepared.Diagnostics)
                    {
                        Diagnostics.Add(new CustomModelDiagnosticItemViewModel(
                            new CustomModelImportDiagnostic
                            {
                                Code = diagnostic.Code,
                                Severity = CustomModelImportSeverity.Warning,
                                Subject = diagnostic.BoneName,
                                Message = diagnostic.Message,
                            }));
                    }
                }
                catch (Exception exception) when (
                    exception is ArgumentException or InvalidDataException or
                    InvalidOperationException or OverflowException)
                {
                    Diagnostics.Add(new CustomModelDiagnosticItemViewModel(
                        new CustomModelImportDiagnostic
                        {
                            Code = "model_dl1_rig_preparation_failed",
                            Severity = CustomModelImportSeverity.Error,
                            Message = exception.Message,
                        }));
                }
            }

            SelectedBone ??= Bones.FirstOrDefault();
            SelectedMaterial = Materials.FirstOrDefault();
            SelectedAnimation = Animations.FirstOrDefault(static clip => clip.DecodedClip is not null);
            Summary =
                $"{imported.Package.Document.Meshes.Length:N0} mesh part(s) | {imported.Surfaces.Length:N0} draw surface(s) | " +
                $"{imported.Package.Document.CreateEffectiveBones().Length:N0} rig/helper node(s) | {Animations.Count:N0} animation stack(s) | " +
                $"{Materials.Count:N0} material(s)";
            OnPropertyChanged(nameof(HasModel));
            OnPropertyChanged(nameof(CanChangeRigMode));
            NotifyCommands();
            RefreshTimeline();
        }
        finally
        {
            _suppressPreviewRefresh = false;
            _suppressPersistenceNotifications = previousPersistenceSuppression;
        }

        RefreshPreview();
        FrameModel();
        if (markAuthoringChanged)
        {
            MarkAuthoringChanged();
        }
    }

    private void PopulateHierarchyRows(string? selectedName = null)
    {
        if (_model is null)
        {
            Bones.Clear();
            RootBoneNames.Clear();
            SelectedBone = null;
            return;
        }

        selectedName ??= SelectedBone?.Name;
        CustomModelDocument document = _model.Package.Document;
        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        Bones.Clear();
        RootBoneNames.Clear();
        for (int index = 0; index < effective.Length; index++)
        {
            Guid? helperId = index >= document.Bones.Length
                ? document.AuthoredHelpers[index - document.Bones.Length].Id
                : null;
            Bones.Add(new CustomModelBoneItemViewModel(effective[index], helperId));
            RootBoneNames.Add(effective[index].Name);
        }

        SelectedBone = selectedName is null
            ? Bones.FirstOrDefault()
            : Bones.FirstOrDefault(row => string.Equals(
                row.Name,
                selectedName,
                StringComparison.OrdinalIgnoreCase)) ?? Bones.FirstOrDefault();
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
            Materials.Add(new CustomModelMaterialItemViewModel(
                material,
                _model.Package.TexturePayloads));
        }
    }

    internal static FbxModelAuthoringImportResult NormalizeBuildReceipt(
        FbxModelAuthoringImportResult imported,
        out string buildStatus)
    {
        ArgumentNullException.ThrowIfNull(imported);
        CustomModelBuildReceipt? receipt = imported.Package.Document.LastBuildReceipt;
        if (receipt is null)
        {
            buildStatus = "Authoring draft";
            return imported;
        }

        if (Dl1OfficialModelCompiler.IsCurrentBuildReceipt(receipt, imported))
        {
            buildStatus =
                $"Last compiler-validated build: {receipt.CompletedUtc.LocalDateTime:g}";
            return imported;
        }

        string reason = receipt.State == CustomModelBuildState.GameReady
            ? "The saved schema-1 game-ready receipt predates the current evidence boundary."
            : string.Equals(
                receipt.ToolFingerprint,
                Dl1OfficialModelCompiler.CurrentToolFingerprint,
                StringComparison.OrdinalIgnoreCase)
                ? "The saved compiler receipt does not match the current model package or build settings."
                : "The saved compiler receipt was produced by an older model-output contract.";
        string message =
            $"{reason} It was invalidated; rebuild this model before publishing it.";
        ImmutableArray<CustomModelImportDiagnostic> diagnostics = imported.Package.Document.Diagnostics
            .Where(static diagnostic =>
                !string.Equals(
                    diagnostic.Code,
                    InvalidatedBuildReceiptDiagnosticCode,
                    StringComparison.Ordinal))
            .Append(new CustomModelImportDiagnostic
            {
                Code = InvalidatedBuildReceiptDiagnosticCode,
                Severity = CustomModelImportSeverity.Warning,
                Message = message,
                Subject = imported.Package.Document.Name,
            })
            .ToImmutableArray();
        CustomModelDocument document = imported.Package.Document with
        {
            Diagnostics = diagnostics,
            LastBuildReceipt = null,
        };
        document.Validate();
        buildStatus = message;
        return imported with
        {
            Package = new CustomModelPackage(
                document,
                imported.Package.SourceFbx,
                imported.Package.TexturePayloads),
        };
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
        CustomModelBuildSettings buildSettings = new()
        {
            CharacterId = string.IsNullOrWhiteSpace(CharacterId)
                ? Dl1SourceModelWriter.SanitizeName(ResourceName, 55)
                : CharacterId.Trim(),
            ResourceName = Dl1SourceModelWriter.SanitizeName(ResourceName, 55),
            SurfaceName = Dl1SourceModelWriter.SanitizeName(SurfaceName, 63),
            AnimationScriptAlias = string.IsNullOrWhiteSpace(AnimationScriptAlias)
                ? null
                : AnimationScriptAlias.Trim(),
            FlipTextureCoordinateV = FlipTextureCoordinateV,
        };
        CustomModelDocument current = _model.Package.Document;
        bool invalidatesBuildReceipt =
            !string.Equals(current.Name, ModelName, StringComparison.Ordinal) ||
            !current.AnimationClips.SequenceEqual(selections) ||
            current.BuildSettings != buildSettings;
        CustomModelDocument document = current with
        {
            Name = ModelName,
            AnimationClips = selections,
            BuildSettings = buildSettings,
            LastBuildReceipt = invalidatesBuildReceipt ? null : current.LastBuildReceipt,
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
        if (_suppressPreviewRefresh)
        {
            return;
        }

        if (_model is null)
        {
            _previewSession = null;
            Viewport.SceneSource.SetScene([], null, []);
            return;
        }

        AnimationClip? clip = SelectedAnimation?.DecodedClip;
        int frame = clip is null
            ? 0
            : Math.Clamp(Timeline.CurrentFrame, 0, checked((int)Math.Min(int.MaxValue, clip.FrameCount - 1)));
        bool replacePreparedScene;
        CustomModelPreviewSession session;
        if (_previewSession is null ||
            !_previewSession.Matches(_model, SelectedPreviewMode.Mode))
        {
            session = CustomModelPreviewAdapter.CreateSession(
                _model,
                SelectedPreviewMode.Mode);
            _previewSession = session;
            replacePreparedScene = true;
        }
        else
        {
            session = _previewSession;
            replacePreparedScene = false;
        }

        // Always retain the evaluated skeleton for skinning. Overlay
        // visibility is carried separately by SkeletonRenderData's role
        // flags and applied below.
        SkeletonRenderData? skeleton =
            session.CreateSkeleton(clip, frame, SelectedBone?.Index);
        if (replacePreparedScene)
        {
            Viewport.SceneSource.SetScene(
                session.Meshes,
                skeleton,
                [],
                generation: Interlocked.Increment(ref _previewGeneration));
        }
        else
        {
            Viewport.SceneSource.SetSkeleton(skeleton);
        }

        _cameraCoordinator.SetTargetPreviewCameraOverride(
            session.CreatePreviewCamera(
                clip,
                frame,
                _model.Package.Document.Camera.ActivePreviewNodeName));

        Viewport.SceneSource.SetMeshVisibility(ShowMeshes);
        ApplySkeletonVisibility();
        string previewLabel = session.IsSourceFallback
            ? "Source FBX fallback"
            : SelectedPreviewMode.Label;
        Viewport.SetPresentation(
            $"{previewLabel} preview - {ModelName}",
            clip is null
                ? session.EffectiveMode == CustomModelPreviewMode.Dl1Output
                    ? "Emitted Chrome hierarchy, +X authored bone frames, and DL1 texture semantics"
                    : session.IsSourceFallback
                        ? "DL1 preparation failed; showing the unmodified source FBX hierarchy and texture coordinates"
                        : "Unmodified FBX bind hierarchy, skin palettes, and source textures"
                : $"Animation stack: {SelectedAnimation!.DisplayName} | frame {frame:N0}" +
                    (session.IsSourceFallback ? " | Source FBX fallback" : string.Empty));
        Viewport.SetDiagnosticOverlay(session.Diagnostics.IsEmpty
            ? null
            : string.Join(Environment.NewLine, session.Diagnostics.Take(3)));
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

    private void DuplicateSelectedAsHelper()
    {
        if (_model is null || SelectedBone is null)
        {
            return;
        }

        try
        {
            string? requestedName = string.IsNullOrWhiteSpace(NewHelperName)
                ? null
                : NewHelperName.Trim();
            CustomModelDocument updated = CustomModelHelperAuthoring.DuplicateAsHelper(
                _model.Package.Document,
                SelectedBone.Index,
                SelectedHelperKind,
                requestedName);
            ApplyHelperMutation(
                updated,
                updated.AuthoredHelpers[^1].Name,
                $"Created {SelectedHelperKind} helper '{updated.AuthoredHelpers[^1].Name}'");
            NewHelperName = string.Empty;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            BuildStatus = $"Helper creation failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private void SelectPreviewCamera()
    {
        if (_model is null || SelectedBone is null)
        {
            return;
        }

        try
        {
            CustomModelDocument current = _model.Package.Document;
            string selectedName = SelectedBone.Name;
            CustomModelDocument updated;
            string selectedAfter;
            if (string.Equals(
                    selectedName,
                    CustomModelHelperAuthoring.GameCameraName,
                    StringComparison.Ordinal))
            {
                updated = CustomModelHelperAuthoring.SelectPreviewCamera(
                    current,
                    selectedName);
                selectedAfter = selectedName;
            }
            else
            {
                bool exactExists = current.CreateEffectiveBones().Any(static bone =>
                    string.Equals(
                        bone.Name,
                        CustomModelHelperAuthoring.GameCameraName,
                        StringComparison.Ordinal));
                CustomModelPreviewCameraDecision decision =
                    _fileDialogs.ConfirmCustomModelPreviewCamera(
                        selectedName,
                        exactExists);
                switch (decision)
                {
                    case CustomModelPreviewCameraDecision.CreateEyeCamera:
                        updated = CustomModelHelperAuthoring.CreateEyeCameraHelper(
                            current,
                            SelectedBone.Index);
                        selectedAfter = CustomModelHelperAuthoring.GameCameraName;
                        break;
                    case CustomModelPreviewCameraDecision.UseEditorOnlySelection:
                        updated = CustomModelHelperAuthoring.SelectPreviewCamera(
                            current,
                            selectedName);
                        selectedAfter = selectedName;
                        break;
                    case CustomModelPreviewCameraDecision.UseExistingEyeCamera:
                        updated = CustomModelHelperAuthoring.SelectPreviewCamera(
                            current,
                            CustomModelHelperAuthoring.GameCameraName);
                        selectedAfter = CustomModelHelperAuthoring.GameCameraName;
                        break;
                    default:
                        return;
                }
            }

            ApplyHelperMutation(
                updated,
                selectedAfter,
                string.Equals(
                    updated.Camera.ActivePreviewNodeName,
                    CustomModelHelperAuthoring.GameCameraName,
                    StringComparison.Ordinal)
                    ? "Selected exact EyeCamera for preview and game-ready FPP export"
                    : $"Selected '{selectedAfter}' as an editor-only preview camera");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            KeyNotFoundException)
        {
            BuildStatus = $"Preview camera selection failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private void ApplySelectedHelperTransform()
    {
        if (_model is null || SelectedBone?.AuthoredHelperId is not { } helperId)
        {
            return;
        }

        try
        {
            double radians = Math.PI / 180.0;
            System.Numerics.Quaternion rotation =
                System.Numerics.Quaternion.CreateFromYawPitchRoll(
                    checked((float)(HelperRotationY * radians)),
                    checked((float)(HelperRotationX * radians)),
                    checked((float)(HelperRotationZ * radians)));
            var transform = new ReAnimated.Core.Mathematics.TransformTRS(
                new ReAnimated.Core.Mathematics.Vector3D(
                    HelperTranslationX,
                    HelperTranslationY,
                    HelperTranslationZ),
                new ReAnimated.Core.Mathematics.QuaternionD(
                    rotation.X,
                    rotation.Y,
                    rotation.Z,
                    rotation.W),
                ReAnimated.Core.Mathematics.Vector3D.One);
            CustomModelDocument updated = CustomModelHelperAuthoring.SetLocalTransform(
                _model.Package.Document,
                helperId,
                transform);
            ApplyHelperMutation(
                updated,
                SelectedBone.Name,
                $"Updated local transform for helper '{SelectedBone.Name}'");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            KeyNotFoundException or
            OverflowException)
        {
            BuildStatus = $"Helper transform failed: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    private void UndoHelperEdit()
    {
        if (_model is null || _helperUndo.Count == 0)
        {
            return;
        }

        string? selectedName = SelectedBone?.Name;
        CustomModelDocument restored = _helperUndo.Pop();
        ReplaceAuthoredDocument(restored, selectedName);
        BuildStatus = "Undid the last helper/camera authoring edit";
        _setStatus(BuildStatus);
        UndoHelperEditCommand.NotifyCanExecuteChanged();
        MarkAuthoringChanged();
    }

    private void ApplyHelperMutation(
        CustomModelDocument updated,
        string? selectedName,
        string status)
    {
        if (_model is null)
        {
            return;
        }

        _helperUndo.Push(_model.Package.Document);
        ReplaceAuthoredDocument(updated, selectedName);
        BuildStatus = status;
        _setStatus(status);
        UndoHelperEditCommand.NotifyCanExecuteChanged();
        MarkAuthoringChanged();
    }

    private void ReplaceAuthoredDocument(
        CustomModelDocument document,
        string? selectedName)
    {
        if (_model is null)
        {
            return;
        }

        document.Validate();
        _model = _model with
        {
            Package = new CustomModelPackage(
                document,
                _model.Package.SourceFbx,
                _model.Package.TexturePayloads),
            Rig = document.Bones.IsEmpty
                ? null
                : document.CreateRigDefinition(),
        };
        _previewSession = null;
        PopulateHierarchyRows(selectedName);
        OnPropertyChanged(nameof(PreviewCameraStatus));
        NotifyCommands();
        RefreshTimeline();
        RefreshPreview();
    }

    private void LoadSelectedHelperTransform()
    {
        if (_model is null || SelectedBone?.AuthoredHelperId is not { } helperId)
        {
            HelperTranslationX = 0;
            HelperTranslationY = 0;
            HelperTranslationZ = 0;
            HelperRotationX = 0;
            HelperRotationY = 0;
            HelperRotationZ = 0;
            return;
        }

        CustomModelAuthoredHelper helper = _model.Package.Document.AuthoredHelpers
            .Single(row => row.Id == helperId);
        HelperTranslationX = helper.LocalTransform.Translation.X;
        HelperTranslationY = helper.LocalTransform.Translation.Y;
        HelperTranslationZ = helper.LocalTransform.Translation.Z;
        ReAnimated.Core.Mathematics.QuaternionD q =
            helper.LocalTransform.Rotation.Normalized();
        HelperRotationX = Math.Asin(Math.Clamp(
                2.0 * ((q.W * q.X) - (q.Y * q.Z)),
                -1.0,
                1.0)) * 180.0 / Math.PI;
        HelperRotationY = Math.Atan2(
                2.0 * ((q.W * q.Y) + (q.X * q.Z)),
                1.0 - (2.0 * ((q.X * q.X) + (q.Y * q.Y)))) * 180.0 / Math.PI;
        HelperRotationZ = Math.Atan2(
                2.0 * ((q.W * q.Z) + (q.X * q.Y)),
                1.0 - (2.0 * ((q.X * q.X) + (q.Z * q.Z)))) * 180.0 / Math.PI;
    }

    private void OnTimelineFrameChanged(object? sender, EventArgs args) => RefreshPreview();

    private void OnAnimationItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        MarkAuthoringChanged();
        ExportAnimationRpackCommand.NotifyCanExecuteChanged();
        InvalidateDeploymentPreflight();
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
        EnsureCurrentGeneration(generation);
    }

    private void EnsureCurrentGeneration(long generation)
    {
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

    private void MarkAuthoringChanged()
    {
        if (_suppressPersistenceNotifications)
        {
            return;
        }

        Interlocked.Increment(ref _authoringRevision);
        PersistenceStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearLastBuildReceipt()
    {
        if (_model is null || _model.Package.Document.LastBuildReceipt is null)
        {
            return;
        }

        CustomModelDocument document = _model.Package.Document with { LastBuildReceipt = null };
        document.Validate();
        _model = _model with
        {
            Package = new CustomModelPackage(
                document,
                _model.Package.SourceFbx,
                _model.Package.TexturePayloads),
        };
    }

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
        BuildCompletePackageCommand.NotifyCanExecuteChanged();
        BuildLooseFilesCommand.NotifyCanExecuteChanged();
        SelectModelCompilerCommand.NotifyCanExecuteChanged();
        SelectDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
        BuildModelRpackCommand.NotifyCanExecuteChanged();
        ExportAnimationRpackCommand.NotifyCanExecuteChanged();
        OpenSelectedAnimationInAnimateCommand.NotifyCanExecuteChanged();
        OpenDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
        OpenDeployedAnimationScriptCommand.NotifyCanExecuteChanged();
        OpenDeploymentReceiptCommand.NotifyCanExecuteChanged();
        DeployToDeveloperToolsProjectCommand.NotifyCanExecuteChanged();
        RollBackDeploymentCommand.NotifyCanExecuteChanged();
        BackUpLegacyOutputsCommand.NotifyCanExecuteChanged();
        FrameModelCommand.NotifyCanExecuteChanged();
        DuplicateSelectedAsHelperCommand.NotifyCanExecuteChanged();
        SelectPreviewCameraCommand.NotifyCanExecuteChanged();
        ApplyHelperTransformCommand.NotifyCanExecuteChanged();
        UndoHelperEditCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        NotifyAnimationRefreshCommands();
    }

    private static string NormalizeTextureExtension(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (!CustomModelTextureDecoder.SupportedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Texture extension '{extension}' is not supported.");
        }

        return extension;
    }

    private static string TextureMediaType(string extension) => extension switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".bmp" => "image/bmp",
        ".dds" => "image/vnd-ms.dds",
        ".tga" => "image/x-tga",
        _ => "application/octet-stream",
    };

    private static string DisplaySafeIdentity(string? value, string? fallback, string finalFallback)
    {
        string candidate = string.IsNullOrWhiteSpace(value) ? fallback ?? string.Empty : value;
        candidate = candidate.Trim();
        return string.IsNullOrWhiteSpace(candidate) ? finalFallback : candidate;
    }

    private static string DisplaySafeCharacterId(string? value, string? fallback)
    {
        try
        {
            return Dl1DeveloperToolsProjectDeployer.NormalizeCharacterId(
                DisplaySafeIdentity(value, fallback, "character"));
        }
        catch (ArgumentException)
        {
            return "<invalid-character-id>";
        }
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
        {
            BuildStatus = "The selected deployment path is no longer available.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or IOException)
        {
            BuildStatus = $"Could not open deployment path: {exception.Message}";
            _setStatus(BuildStatus);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAnimationRefreshMonitor();
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

public sealed record CustomModelBoneItemViewModel(
    CustomModelBone Contract,
    Guid? AuthoredHelperId = null)
{
    public int Index => Contract.Index;
    public string Name => Contract.Name;
    public string Role => Contract.Kind.ToString();
    public int ParentIndex => Contract.ParentIndex;
    public bool IsWeighted => Contract.IsWeighted;
    public bool IsAuthored => AuthoredHelperId is not null;
}

public sealed class CustomModelMaterialItemViewModel
{
    private static readonly Dictionary<string, (bool Success, string Verdict)>
        DecodeVerdicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, ImmutableArray<byte>> _payloads;

    public CustomModelMaterialItemViewModel(
        CustomModelMaterial contract,
        IReadOnlyDictionary<string, ImmutableArray<byte>> payloads)
    {
        Contract = contract;
        _payloads = payloads;
    }

    public CustomModelMaterial Contract { get; }

    public string Name => Contract.Name;

    public string BaseColor => Contract.Textures.FirstOrDefault(
        static texture => texture.Semantic == CustomModelTextureSemantic.BaseColor)?.DisplayName ?? "No base color";

    public string TextureSummary => Contract.Textures.IsEmpty
        ? "No textures"
        : string.Join(Environment.NewLine, Contract.Textures.Select(TextureStatus));

    private string TextureStatus(CustomModelTextureBinding texture)
    {
        if (texture.PackageEntryPath is not { } entryPath ||
            !_payloads.TryGetValue(entryPath, out ImmutableArray<byte> payload) ||
            payload.IsDefaultOrEmpty)
        {
            return $"{texture.Semantic}: {texture.DisplayName} ? {texture.SourceKind}, no bytes";
        }

        if (!DecodeVerdicts.TryGetValue(texture.ContentSha256, out var verdict))
        {
            bool success = CustomModelTextureDecoder.TryDecode(
                payload.AsSpan(),
                texture.DisplayName,
                out CustomModelDecodedTexture? decoded,
                out string failureReason);
            verdict = success && decoded is not null
                ? (true, $"{decoded.MediaType}, preview OK")
                : (false, $"preview failed: {failureReason}");
            DecodeVerdicts[texture.ContentSha256] = verdict;
        }

        double kibibytes = Math.Max(0.1, payload.Length / 1024.0);
        return $"{texture.Semantic}: {texture.DisplayName} ? {texture.SourceKind}, " +
            $"{kibibytes:N1} KB, {verdict.Verdict}";
    }
}

public sealed record CustomModelDiagnosticItemViewModel(CustomModelImportDiagnostic Contract)
{
    public string Severity => Contract.Severity.ToString();
    public string Code => Contract.Code;
    public string Message => Contract.Message;
    public string? Subject => Contract.Subject;
}

public sealed record DeveloperToolsDeploymentArtifactItemViewModel(
    Dl1DeveloperToolsDeploymentArtifact Contract)
{
    public string RelativePath => Contract.RelativePath;

    public string Role => Contract.Role.ToString();

    public string Disposition => Contract.Disposition.ToString();

    public string Required => Contract.Required ? "Required" : "Optional";

    public string Description => Contract.Description;
}

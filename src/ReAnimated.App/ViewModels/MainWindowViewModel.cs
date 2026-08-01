using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Discovery;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting;
using ReAnimated.Retargeting.Ik;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel :
    ObservableObject,
    IWorkspaceSnapshotProvider,
    IAsyncDisposable
{
    private sealed record ImportedAnimationSession(
        RigDefinition Rig,
        AnimationClip Clip,
        string SourcePath,
        string SourceKind)
    {
        public AnimationSourceKind SourceKindContract { get; init; } =
            AnimationSourceKind.LocalFbx;

        public Guid? RetailSourceModelAssetId { get; init; }

        public Anm2TrackPartition? Partition { get; init; }

        public AnimationTimingProvenance TimingProvenance { get; init; } =
            AnimationTimingProvenance.EmbeddedFbx;

        public double? SourceRangeStartFrame { get; init; }

        public double? SourceRangeEndFrame { get; init; }

        public string? TimingDetail { get; init; }

        public AnimationClip? FacialClip { get; init; }
    }

    private sealed record ImportedMimicSession(
        Guid AssetId,
        AnimationClip Clip,
        string SourcePath);

    private sealed record ImportedFacialFbxSession(
        Guid AssetId,
        AnimationClip Clip,
        string SourcePath,
        ProjectMorphSourceValueUnit SourceValueUnit);

    private sealed record DecodedRetailModelSession(
        Dl1MeshPreviewPayload Payload,
        RetailAssetRecord RetailAsset,
        ProjectAssetReference ProjectAsset,
        MeshRenderData[] PreviewMeshes);

    private sealed record RootMotionTrailCacheKey(
        Guid? ActiveAnimationId,
        ImportedAnimationSession Source,
        RigDefinition Target,
        RetargetMap? Mapping,
        ProjectAnimation Animation,
        AnimationClip EvaluationClip,
        int SampleCount);

    private sealed record RootMotionTrailBuildSnapshot(
        RootMotionTrailCacheKey CacheKey,
        ImportedAnimationSession Source,
        RigDefinition Target,
        RetargetMap? Mapping,
        ProjectAnimation Animation,
        AnimationClip EvaluationClip,
        Dl1AuthoringPolicy AuthoringPolicy,
        IkConstraintLayer[] IkLayers,
        int SampleCount);

    private sealed record RootMotionTrailCache(
        RootMotionTrailCacheKey Key,
        long FrameCount,
        ImmutableArray<Vector3> WorldPositions);

    private sealed class BoneGizmoDragContext
    {
        public BoneGizmoDragContext(
            ViewportSide side,
            SkeletonNodeViewModel bone,
            RenderTransformGizmoBinding binding,
            TransformTRS initialTransform,
            Matrix4x4 worldToParentLocal,
            System.Numerics.Quaternion worldToSelectedRotation,
            Vector3 axisDirectionWorld,
            DlraProject project,
            Guid animationId,
            double frame,
            Guid? preferredLayerId,
            Guid previewLayerId)
        {
            Side = side;
            Bone = bone;
            Binding = binding;
            InitialTransform = initialTransform;
            CurrentTransform = initialTransform;
            WorldToParentLocal = worldToParentLocal;
            WorldToSelectedRotation = worldToSelectedRotation;
            AxisDirectionWorld = axisDirectionWorld;
            Project = project;
            AnimationId = animationId;
            Frame = frame;
            PreferredLayerId = preferredLayerId;
            PreviewLayerId = previewLayerId;
        }

        public ViewportSide Side { get; }

        public SkeletonNodeViewModel Bone { get; }

        public RenderTransformGizmoBinding Binding { get; }

        public TransformTRS InitialTransform { get; }

        public TransformTRS CurrentTransform { get; set; }

        public Matrix4x4 WorldToParentLocal { get; }

        public System.Numerics.Quaternion WorldToSelectedRotation { get; }

        public Vector3 AxisDirectionWorld { get; }

        public DlraProject Project { get; }

        public Guid AnimationId { get; }

        public double Frame { get; }

        public Guid? PreferredLayerId { get; }

        public Guid PreviewLayerId { get; }

        public bool HasMeaningfulMovement { get; set; }
    }

    private sealed class ViewportBoneGizmoTarget :
        IRenderTransformGizmoTarget,
        IRenderTranslationGizmoTarget
    {
        private readonly MainWindowViewModel _owner;
        private readonly ViewportSide _side;

        public ViewportBoneGizmoTarget(
            MainWindowViewModel owner,
            ViewportSide side)
        {
            _owner = owner;
            _side = side;
        }

        public bool TryBeginTransformGizmoDrag(
            RenderTransformGizmoDragStart start) =>
            _owner.TryBeginBoneGizmoDrag(
                _side,
                start);

        public bool UpdateTransformGizmoDrag(
            RenderTransformGizmoDragUpdate update) =>
            _owner.UpdateBoneGizmoDrag(
                _side,
                update);

        public void CompleteTransformGizmoDrag(bool commit) =>
            _owner.CompleteBoneGizmoDrag(
                _side,
                commit);

        public bool TryBeginTranslationGizmoDrag(
            RenderTranslationGizmoDragStart start)
        {
            return TryConvertBinding(
                    start.Binding,
                    out RenderTransformGizmoBinding binding) &&
                TryBeginTransformGizmoDrag(
                    new RenderTransformGizmoDragStart(
                        binding,
                        start.AxisDirectionWorld));
        }

        public bool UpdateTranslationGizmoDrag(
            RenderTranslationGizmoDragUpdate update)
        {
            if (!TryConvertBinding(
                    update.Binding,
                    out RenderTransformGizmoBinding binding))
            {
                CompleteTransformGizmoDrag(commit: false);
                return false;
            }

            return UpdateTransformGizmoDrag(
                new RenderTransformGizmoDragUpdate(
                    binding,
                    update.WorldDelta,
                    update.AxisDistance,
                    RotationRadians: 0.0f,
                    ScaleFactor: 1.0f));
        }

        public void CompleteTranslationGizmoDrag(bool commit) =>
            CompleteTransformGizmoDrag(commit);

        private static bool TryConvertBinding(
            TranslationGizmoBinding binding,
            out RenderTransformGizmoBinding converted)
        {
            converted = default;
            if (!Enum.IsDefined(binding.Axis) ||
                !Enum.IsDefined(binding.Space))
            {
                return false;
            }

            converted = new(
                binding.BoneIndex,
                RenderTransformGizmoMode.Translate,
                binding.Axis switch
                {
                    TranslationGizmoAxis.X =>
                        RenderTransformGizmoAxis.X,
                    TranslationGizmoAxis.Y =>
                        RenderTransformGizmoAxis.Y,
                    TranslationGizmoAxis.Z =>
                        RenderTransformGizmoAxis.Z,
                    _ => default,
                },
                binding.Space);
            return true;
        }
    }

    private const string EditorLayerName = "Editor Bone Adjustments";
    private const string FacialEditorLayerName = "Editor Facial Adjustments";
    private const double RetailRenderBindDecompositionTolerance = 1.0e-4;
    private const int MaximumDecodedAttachmentAssetCacheEntries = 64;
    private const string RawPreviewModeLabel = "Raw";
    private const string Dl1ProfilePreviewModeLabel = "DL1 profile";
    private const string PreviewFidelityBadgeLabel = "Preview fidelity";
    private const string InstalledBuildBadgeLabel = "Installed DL1 build";
    private const string AuthoredSourcePaneTitle = "Source / Authored";
    private const string AuthoredSourcePaneFidelity =
        "GPU-skinned authoring preview";
    private const string TargetPaneTitle = "DL1 Target";
    private const string TargetPaneFidelity =
        "Decoded retail mesh and skeleton";
    private readonly JsonWorkspaceStateStore _recoveryStore;
    private readonly IProjectFileDialogService _fileDialogs;
    private readonly Dl1AssetWorkspace _assetWorkspace;
    private readonly IRetailMeshDecodeService _retailMeshDecodeService;
    private readonly IDl1InstalledBuildFingerprintService
        _installedBuildFingerprintService;
    private readonly IFacialFbxProjectReviewImporter
        _facialFbxProjectReviewImporter;
    private readonly CancellationTokenSource _lifetimeSource = new();
    private readonly object _rootMotionTrailWorkerGate = new();
    private readonly HashSet<Task<Vector3[]>>
        _rootMotionTrailWorkers = [];
    private readonly LinkedViewportCoordinator _viewportCoordinator = new();
    private readonly EditorSessionCoordinator _editorSessionCoordinator = new();
    private readonly Stack<DlraProject> _undoProjects = new();
    private readonly Stack<DlraProject> _redoProjects = new();
    private readonly Dictionary<Guid, AttachmentRenderAsset>
        _attachmentRenderAssets = [];
    private readonly Dictionary<Guid, string>
        _attachmentStatuses = [];
    private SkeletonNodeViewModel? _selectedBone;
    private BoneMappingViewModel? _selectedBoneMapping;
    private BoneEditLayerItemViewModel?
        _selectedBoneEditLayer;
    private AnimationLibraryItemViewModel?
        _selectedAnimationLibraryItem;
    private AssetItemViewModel? _pendingExplorerAnimationSourceChoice;
    private AssetItemViewModel? _pendingExplorerAnimationTimingChoice;
    private Dl1RetailAnimationTiming? _selectedExplorerAnimationTiming;
    private JobViewModel? _assetDecodeJob;
    private CancellationTokenSource? _automaticAssetPreviewSource;
    private Task? _automaticAssetPreviewTask;
    private JobViewModel? _assetProfileScanJob;
    private JobViewModel? _ikBakeJob;
    private Task? _assetCatalogLoadTask;
    private bool _isViewportsLinked = true;
    private bool _isSourceViewportVisible = true;
    private bool _isTargetSwitching;
    private bool _isDiagnosticsDrawerOpen;
    private bool _showMeshes = true;
    private bool _showSkeletonOverlay = true;
    private bool _showDeformBones = true;
    private bool _showHelpers;
    private bool _showCameraHelpers = true;
    private bool _showPropHelpers;
    private bool _showRootMotionTrail;
    private bool _showDeformedBounds;
    private bool _showBoneLocalAxes;
    private bool _highlightSelectedMeshes;
    private bool _hasRecoverySnapshot;
    private bool _isBusy;
    private bool _isDirty;
    private bool _disposed;
    private WorkspaceSnapshot? _recoverySnapshot;
    private string _statusText =
        "Ready - loading the saved Dying Light 1 asset catalog";
    private string _activeWorkspaceMode = "Browse";
    private EditorWorkspaceMode _activeWorkspace =
        EditorWorkspaceMode.Browse;
    private PreviewLayoutMode _previewLayout =
        PreviewLayoutMode.IsolatedBrowse;
    private string _selectedPreviewMode = Dl1ProfilePreviewModeLabel;
    private string? _projectPath;
    private Guid? _activeAnimationId;
    private DlraProject _project = DlraProject.Create("Untitled");
    private DlraProject? _savedProject;
    private ImportedAnimationSession? _sourceAnimation;
    private ImportedMimicSession? _mimicAnimation;
    private ImportedFacialFbxSession? _facialFbxAnimation;
    private AnimationClip? _synchronizedAnimation;
    private RigDefinition? _targetRig;
    private RetargetMap? _activeRetargetMap;
    private string _mappingReviewStatus =
        "Load a source animation and retail target to review mapping.";
    private string? _lastPreviewDiagnostic;
    private ProjectAssetReference? _targetProjectAsset;
    private DecodedRetailModelSession? _sourceModelContext;
    private TargetBindingStatus _targetBindingStatus =
        TargetBindingStatus.Invalid;
    private string? _pendingAnm2SourcePath;
    private string? _pendingMimicSourcePath;
    private Guid? _pendingMimicAssetId;
    private string? _pendingFacialFbxSourcePath;
    private Guid? _pendingFacialFbxAssetId;
    private FedDocument? _fedDocument;
    private bool _synchronizingMorphWeights;
    private bool _synchronizingProjectBindings;
    private bool _synchronizingPreviewMode;
    private bool _synchronizingFppProjectionCapture;
    private bool _synchronizingMovieReferenceCameraCapture;
    private bool _synchronizingPreviewConfiguration;
    private string? _facialFbxUnmappedFingerprint;
    private ImmutableArray<string> _facialFbxUnmappedChannels = [];
    private Dl1RootMotionMode _selectedRootMotionMode =
        Dl1RootMotionMode.Recorded;
    private Dl1InstalledBuildFingerprint? _installedBuildFingerprint;
    private string? _installedBuildFingerprintError;
    private bool _isReadingInstalledBuildFingerprint;
    private bool _hasReadInstalledBuildFingerprint;
    private string? _selectedAdditionalRpackRoot;
    private string? _selectedHelperOverrideSourceBone;
    private string? _selectedHelperOverrideTargetBone;
    private MeshRenderData[] _targetBaseMeshes = [];
    private MeshRenderData[] _sourceBaseMeshes = [];
    private AssetItemViewModel[] _indexedAssetItems = [];
    private string? _lastAttachmentDiagnosticSignature;
    private BoneGizmoDragContext? _boneGizmoDrag;
    private JobViewModel? _rootMotionTrailJob;
    private RootMotionTrailBuildSnapshot?
        _rootMotionTrailBuildSnapshot;
    private RootMotionTrailCache? _rootMotionTrailCache;
    private Task<Vector3[]>? _rootMotionTrailWorkerTask;
    private int _rootMotionTrailGeneration;
    private long _previewGeneration;
    private PreviewFramePair? _lastPreviewFramePair;
    private RenderFppProjectionState? _suspendedTargetProjection;
    private RenderFrameSnapshot? _isolatedBrowsePreviewFrame;
    private string? _isolatedBrowsePreviewTitle;
    private string? _isolatedBrowsePreviewFidelity;
    private ViewportOrbitCameraPair? _authoringOrbitCameras;
    private ViewportOrbitCameraPair? _browseOrbitCameras;

    public MainWindowViewModel(JsonWorkspaceStateStore recoveryStore)
        : this(
            recoveryStore,
            new WindowsProjectFileDialogService(),
            CreateDefaultAssetWorkspace(),
            new Dl1InstalledBuildFingerprintService())
    {
    }

    public MainWindowViewModel(
        JsonWorkspaceStateStore recoveryStore,
        IProjectFileDialogService fileDialogs,
        Dl1AssetWorkspace assetWorkspace)
        : this(
            recoveryStore,
            fileDialogs,
            assetWorkspace,
            new Dl1InstalledBuildFingerprintService())
    {
    }

    public MainWindowViewModel(
        JsonWorkspaceStateStore recoveryStore,
        IProjectFileDialogService fileDialogs,
        Dl1AssetWorkspace assetWorkspace,
        IDl1InstalledBuildFingerprintService installedBuildFingerprintService,
        IFacialFbxProjectReviewImporter?
            facialFbxProjectReviewImporter = null,
        IRetailMeshDecodeService? retailMeshDecodeService = null)
    {
        _recoveryStore = recoveryStore
            ?? throw new ArgumentNullException(nameof(recoveryStore));
        _fileDialogs = fileDialogs
            ?? throw new ArgumentNullException(nameof(fileDialogs));
        _assetWorkspace = assetWorkspace
            ?? throw new ArgumentNullException(nameof(assetWorkspace));
        _retailMeshDecodeService = retailMeshDecodeService ??
            new RetailMeshDecodeService(assetWorkspace);
        _installedBuildFingerprintService =
            installedBuildFingerprintService
            ?? throw new ArgumentNullException(
                nameof(installedBuildFingerprintService));
        _facialFbxProjectReviewImporter =
            facialFbxProjectReviewImporter ??
            new FacialFbxProjectReviewImporter();
        _savedProject = _project;

        SourceViewport = new ViewportPaneViewModel(
            AuthoredSourcePaneTitle,
            AuthoredSourcePaneFidelity,
            new ViewportSceneSource(
                _viewportCoordinator,
                ViewportSide.Source,
                // Inspection lighting must remain readable even on dark or
                // untextured retail surfaces. Keep the panes distinct without
                // using the near-black gameplay palette.
                new Vector4(0.115f, 0.155f, 0.215f, 1.0f)));
        TargetViewport = new ViewportPaneViewModel(
            TargetPaneTitle,
            TargetPaneFidelity,
            new ViewportSceneSource(
                _viewportCoordinator,
                ViewportSide.Target,
                new Vector4(0.205f, 0.115f, 0.135f, 1.0f)));
        SourceViewport.SceneSource.SetTransformGizmoTarget(
            new ViewportBoneGizmoTarget(
                this,
                ViewportSide.Source));
        TargetViewport.SceneSource.SetTransformGizmoTarget(
            new ViewportBoneGizmoTarget(
                this,
                ViewportSide.Target));
        SourceViewport.SceneSource.SetTranslationGizmoTarget(
            new ViewportBoneGizmoTarget(
                this,
                ViewportSide.Source));
        TargetViewport.SceneSource.SetTranslationGizmoTarget(
            new ViewportBoneGizmoTarget(
                this,
                ViewportSide.Target));

        NewWorkspaceCommand = new RelayCommand(
            NewWorkspace,
            () => !IsBusy);
        OpenWorkspaceCommand = new AsyncRelayCommand(
            OpenWorkspaceAsync,
            () => !IsBusy);
        SaveWorkspaceCommand = new AsyncRelayCommand(
            SaveWorkspaceAsync,
            () => !IsBusy);
        ImportAnimationCommand = new AsyncRelayCommand(
            ImportAnimationAsync,
            () => !IsBusy);
        PreviewSelectedAssetCommand = new AsyncRelayCommand(
            PreviewSelectedAssetAsync,
            CanUseSelectedMeshAsset);
        UseSelectedAssetAsSourceCommand = new AsyncRelayCommand(
            UseSelectedAssetAsSourceAsync,
            CanUseSelectedMeshAsset);
        UseSelectedAssetAsTargetCommand = new AsyncRelayCommand(
            UseSelectedAssetAsTargetAsync,
            CanUseSelectedMeshAssetAsTarget,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlaySelectedExplorerAnimationCommand = new AsyncRelayCommand(
            PlaySelectedExplorerAnimationAsync,
            CanPlaySelectedExplorerAnimation);
        SelectWorkspaceCommand = new RelayCommand<string>(
            SelectWorkspace);
        ToggleDiagnosticsDrawerCommand = new RelayCommand(
            () => IsDiagnosticsDrawerOpen =
                !IsDiagnosticsDrawerOpen);
        CancelExplorerSourceModelPickerCommand = new RelayCommand(
            CancelExplorerSourceModelPicker,
            () => IsExplorerSourceModelPickerActive);
        ConfirmExplorerAnimationTimingCommand = new AsyncRelayCommand(
            ConfirmExplorerAnimationTimingAsync,
            CanConfirmExplorerAnimationTiming);
        CancelExplorerAnimationTimingCommand = new RelayCommand(
            CancelExplorerAnimationTiming,
            () => IsExplorerAnimationTimingPickerActive);
        ActivateSelectedAnimationCommand = new AsyncRelayCommand(
            ActivateSelectedAnimationAsync,
            CanActivateSelectedAnimation);
        RenameSelectedAnimationCommand = new RelayCommand(
            RenameSelectedAnimation,
            CanRenameSelectedAnimation);
        DuplicateSelectedAnimationCommand = new RelayCommand(
            DuplicateSelectedAnimation,
            CanUseSelectedAnimationLibraryItem);
        RebindSelectedAnimationSourceCommand = new AsyncRelayCommand(
            RebindSelectedAnimationSourceAsync,
            CanRebindSelectedAnimationSource);
        RemoveSelectedAnimationCommand = new RelayCommand(
            RemoveSelectedAnimation,
            CanUseSelectedAnimationLibraryItem);
        RevealSelectedAnimationSourceCommand = new RelayCommand(
            RevealSelectedAnimationSource,
            CanUseSelectedAnimationLibraryItem);
        AttachSelectedAnimationAsFacialCommand = new AsyncRelayCommand(
            AttachSelectedAnimationAsFacialAsync,
            CanAttachSelectedAnimationAsFacial);
        ImportMimicAnimationCommand = new AsyncRelayCommand(
            ImportMimicAnimationAsync,
            CanImportMimicAnimation);
        ImportFacialFbxCommand = new AsyncRelayCommand(
            ImportFacialFbxAsync,
            CanImportFacialFbx);
        ApplyFacialMappingReviewCommand = new RelayCommand(
            ApplyFacialMappingReview,
            CanApplyFacialMappingReview);
        ReviewAndLockAllFacialMappingsCommand = new RelayCommand(
            ReviewAndLockAllFacialMappings,
            CanReviewAndLockAllFacialMappings);
        ExportBodyCommand = new AsyncRelayCommand(
            () => ExportAnimationAsync(Dl1AnimationExportParts.Body),
            CanExportAnimation);
        ExportMimicCommand = new AsyncRelayCommand(
            () => ExportAnimationAsync(Dl1AnimationExportParts.Mimic),
            CanExportAnimation);
        ExportBodyAndMimicCommand = new AsyncRelayCommand(
            () => ExportAnimationAsync(
                Dl1AnimationExportParts.BodyAndMimic),
            CanExportAnimation);
        ImportFedCommand = new AsyncRelayCommand(
            ImportFedAsync,
            () => !IsBusy);
        ApplyFedExpressionCommand = new RelayCommand(
            ApplyFedExpression,
            CanApplyFedExpression);
        KeyMorphPoseCommand = new RelayCommand(
            KeyMorphPose,
            CanKeyMorphPose);
        KeyIkConstraintCommand = new RelayCommand(
            KeyIkConstraint,
            CanKeyIkConstraint);
        BakeIkConstraintCommand = new AsyncRelayCommand(
            BakeSelectedIkConstraintAsync,
            CanBakeSelectedIkConstraint);
        AddAttachmentCommand = new AsyncRelayCommand(
            AddAttachmentAsync,
            CanAddAttachment);
        ApplyAttachmentCommand = new RelayCommand(
            ApplyAttachment,
            CanApplyAttachment);
        RemoveAttachmentCommand = new RelayCommand(
            RemoveAttachment,
            CanRemoveAttachment);
        ResetAttachmentOffsetCommand = new RelayCommand(
            AttachmentEditor.ResetOffset);
        UndoCommand = new RelayCommand(Undo, () => _undoProjects.Count > 0);
        RedoCommand = new RelayCommand(Redo, () => _redoProjects.Count > 0);
        RestoreRecoveryCommand = new RelayCommand(
            RestoreRecovery,
            () => HasRecoverySnapshot);
        DismissRecoveryCommand = new RelayCommand(
            DismissRecovery,
            () => HasRecoverySnapshot);
        ResetCameraCommand = new RelayCommand(ResetCamera);
        FrameSelectionCommand = new RelayCommand(FrameSelection);
        FrameAttachmentCommand = new RelayCommand(
            FrameSelectedAttachment,
            CanFrameSelectedAttachment);
        StoreFppProjectionCaptureCommand = new RelayCommand(
            StoreFppProjectionCapture);
        StoreMovieReferenceCameraCaptureCommand = new RelayCommand(
            StoreMovieReferenceCameraCapture);
        AddAdditionalRpackRootCommand = new RelayCommand(
            AddAdditionalRpackRoot,
            () => !IsBusy);
        RemoveAdditionalRpackRootCommand = new RelayCommand(
            RemoveAdditionalRpackRoot,
            () => !IsBusy &&
                  SelectedAdditionalRpackRoot is not null);
        AutoMapCommand = new RelayCommand(
            AutoMap,
            () => !IsBusy &&
                  _sourceAnimation is not null &&
                  _targetRig is not null);
        ValidateMappingCommand = new RelayCommand(
            ValidateMapping,
            CanReviewMapping);
        SaveMappingProfileCommand = new RelayCommand(
            SaveReviewedMapping,
            CanReviewMapping);
        AddHelperOverrideCommand = new RelayCommand(
            AddHelperOverride,
            CanAddHelperOverride);
        RemoveHelperOverrideCommand = new RelayCommand(
            RemoveSelectedHelperOverride,
            CanRemoveSelectedHelperOverride);

        AssetBrowser.IndexGameRequested += OnIndexGameRequested;
        AssetBrowser.SelectedAssetChanged += OnSelectedAssetChanged;
        AssetBrowser.ProfileScanRequested += OnProfileScanRequested;
        AssetBrowser.ProfileScanCancellationRequested +=
            OnProfileScanCancellationRequested;
        BoneEditor.TransformApplied += OnBoneTransformApplied;
        BoneEditor.GizmoModeChanged += OnBoneGizmoModeChanged;
        BoneEditor.GizmoSpaceChanged += OnBoneGizmoSpaceChanged;
        Timeline.CurrentFrameChanged += OnTimelineFrameChanged;
        Timeline.PropertyChanged += OnTimelinePropertyChanged;
        Timeline.KeyframeRequested += OnTimelineKeyframeRequested;
        FacialFpp.LensChanged += OnLensChanged;
        FacialFpp.MorphWeightsChanged += OnMorphWeightsChanged;
        FacialFpp.PropertyChanged += OnFacialFppPropertyChanged;
        IkEditor.PropertyChanged += OnIkEditorPropertyChanged;
        AttachmentEditor.PropertyChanged +=
            OnAttachmentEditorPropertyChanged;

        BuildFidelityBadges();
        TryLoadRecoveryMetadata();
        AddDiagnostic(
            "Info",
            "Renderer",
            "D3D11 editor preview pipeline is available",
            "Static and skinned meshes, evidence-backed retail SHORT4 position deltas, bounded ABDM/type-8480 BC1/BC2/BC3 base-color previews, skeletons, bone selection, and gizmos render now. Broader shader/material interpretation, unknown morph layouts, normal-delta shading, and game-validated facial captures remain explicit fidelity gaps.");
    }

    public AssetBrowserViewModel AssetBrowser { get; } = new();

    public ObservableCollection<SkeletonNodeViewModel> SkeletonRoots { get; } = [];

    public ObservableCollection<BoneMappingViewModel> BoneMappings { get; } = [];

    public ObservableCollection<TargetBindReviewViewModel>
        RequiredTargetBindReviews
    { get; } = [];

    public ObservableCollection<string> MappingSourceBoneOptions { get; } = [];

    public ObservableCollection<string> MappingHelperTargetOptions { get; } = [];

    public BoneMappingViewModel? SelectedBoneMapping
    {
        get => _selectedBoneMapping;
        set
        {
            if (SetProperty(ref _selectedBoneMapping, value))
            {
                if (value is not null &&
                    MappingSourceBoneOptions.Contains(
                        value.SourceBone,
                        StringComparer.OrdinalIgnoreCase))
                {
                    SelectedHelperOverrideSourceBone =
                        MappingSourceBoneOptions.First(option =>
                            string.Equals(
                                option,
                                value.SourceBone,
                                StringComparison.OrdinalIgnoreCase));
                }

                RemoveHelperOverrideCommand
                    .NotifyCanExecuteChanged();
            }
        }
    }

    public string? SelectedHelperOverrideSourceBone
    {
        get => _selectedHelperOverrideSourceBone;
        set
        {
            if (SetProperty(
                    ref _selectedHelperOverrideSourceBone,
                    value))
            {
                AddHelperOverrideCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string? SelectedHelperOverrideTargetBone
    {
        get => _selectedHelperOverrideTargetBone;
        set
        {
            if (SetProperty(
                    ref _selectedHelperOverrideTargetBone,
                    value))
            {
                AddHelperOverrideCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<BoneEditLayerItemViewModel> BoneEditLayers { get; } = [];

    public ObservableCollection<AnimationLibraryItemViewModel>
        AnimationLibrary
    { get; } = [];

    public AnimationLibraryItemViewModel? SelectedAnimationLibraryItem
    {
        get => _selectedAnimationLibraryItem;
        set
        {
            if (SetProperty(
                    ref _selectedAnimationLibraryItem,
                    value))
            {
                NotifyAnimationLibraryCommands();
            }
        }
    }

    public BoneEditLayerItemViewModel? SelectedBoneEditLayer
    {
        get => _selectedBoneEditLayer;
        set
        {
            if (_boneGizmoDrag is not null &&
                _selectedBoneEditLayer?.Id != value?.Id)
            {
                CancelBoneGizmoDrag(refreshPreview: true);
            }

            if (SetProperty(
                    ref _selectedBoneEditLayer,
                    value))
            {
                SyncBoneEditorFromProject();
                RefreshEditableSkeletonPreview();
            }
        }
    }

    public BoneTransformEditorViewModel BoneEditor { get; } = new();

    public TimelineViewModel Timeline { get; } = new();

    public FacialFppViewModel FacialFpp { get; } = new();

    public IkConstraintEditorViewModel IkEditor { get; } = new();

    public AttachmentEditorViewModel AttachmentEditor { get; } = new();

    public ObservableCollection<JobViewModel> Jobs { get; } = [];

    public ObservableCollection<DiagnosticEntryViewModel> Diagnostics { get; } = [];

    public ObservableCollection<FidelityBadgeViewModel> FidelityBadges { get; } = [];

    public Dl1InstalledBuildFingerprint? InstalledBuildFingerprint =>
        _installedBuildFingerprint;

    public ObservableCollection<string> RecentProjectPaths { get; } = [];

    public ObservableCollection<string> AdditionalRpackRoots { get; } = [];

    public ObservableCollection<string> WorkspaceModes { get; } =
        ["Browse", "Animate", "Retarget/Edit", "Face", "FPP"];

    public IReadOnlyList<string> PreviewModes { get; } =
        [RawPreviewModeLabel, Dl1ProfilePreviewModeLabel];

    public IReadOnlyList<Dl1RootMotionMode> RootMotionModes { get; } =
        Enum.GetValues<Dl1RootMotionMode>();

    public ViewportPaneViewModel SourceViewport { get; }

    public ViewportPaneViewModel TargetViewport { get; }

    public RelayCommand NewWorkspaceCommand { get; }

    public AsyncRelayCommand OpenWorkspaceCommand { get; }

    public AsyncRelayCommand SaveWorkspaceCommand { get; }

    public AsyncRelayCommand ImportAnimationCommand { get; }

    public AsyncRelayCommand PreviewSelectedAssetCommand { get; }

    public AsyncRelayCommand UseSelectedAssetAsSourceCommand { get; }

    public AsyncRelayCommand UseSelectedAssetAsTargetCommand { get; }

    public AsyncRelayCommand PlaySelectedExplorerAnimationCommand { get; }

    public RelayCommand<string> SelectWorkspaceCommand { get; }

    public RelayCommand ToggleDiagnosticsDrawerCommand { get; }

    public RelayCommand CancelExplorerSourceModelPickerCommand { get; }

    public bool IsExplorerSourceModelPickerActive =>
        _pendingExplorerAnimationSourceChoice is not null;

    public string ExplorerSourceModelPickerPrompt =>
        _pendingExplorerAnimationSourceChoice is { } animation
            ? $"Choose the exact retail source model for '{animation.Name}'. Selecting a skinned mesh decodes it, binds this clip to its fingerprint, and starts playback."
            : string.Empty;

    public ObservableCollection<Dl1RetailAnimationTiming>
        ExplorerAnimationTimingChoices { get; } = [];

    public Dl1RetailAnimationTiming? SelectedExplorerAnimationTiming
    {
        get => _selectedExplorerAnimationTiming;
        set
        {
            if (SetProperty(
                    ref _selectedExplorerAnimationTiming,
                    value))
            {
                ConfirmExplorerAnimationTimingCommand
                    .NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsExplorerAnimationTimingPickerActive =>
        _pendingExplorerAnimationTimingChoice is not null &&
        ExplorerAnimationTimingChoices.Count > 1;

    public string ExplorerAnimationTimingPrompt =>
        _pendingExplorerAnimationTimingChoice is { } animation
            ? $"'{animation.Name}' has multiple exact AnimationScr timing entries in this provider/pack. Choose the intended cadence and range."
            : string.Empty;

    public AsyncRelayCommand ConfirmExplorerAnimationTimingCommand
    {
        get;
    }

    public RelayCommand CancelExplorerAnimationTimingCommand { get; }

    public AsyncRelayCommand ActivateSelectedAnimationCommand { get; }

    public RelayCommand RenameSelectedAnimationCommand { get; }

    public RelayCommand DuplicateSelectedAnimationCommand { get; }

    public AsyncRelayCommand RebindSelectedAnimationSourceCommand { get; }

    public RelayCommand RemoveSelectedAnimationCommand { get; }

    public RelayCommand RevealSelectedAnimationSourceCommand { get; }

    public AsyncRelayCommand AttachSelectedAnimationAsFacialCommand { get; }

    public AsyncRelayCommand ImportMimicAnimationCommand { get; }

    public AsyncRelayCommand ImportFacialFbxCommand { get; }

    public RelayCommand ApplyFacialMappingReviewCommand { get; }

    public RelayCommand ReviewAndLockAllFacialMappingsCommand { get; }

    public AsyncRelayCommand ExportBodyCommand { get; }

    public AsyncRelayCommand ExportMimicCommand { get; }

    public AsyncRelayCommand ExportBodyAndMimicCommand { get; }

    public AsyncRelayCommand ImportFedCommand { get; }

    public RelayCommand ApplyFedExpressionCommand { get; }

    public RelayCommand KeyMorphPoseCommand { get; }

    public RelayCommand KeyIkConstraintCommand { get; }

    public AsyncRelayCommand BakeIkConstraintCommand { get; }

    public AsyncRelayCommand AddAttachmentCommand { get; }

    public RelayCommand ApplyAttachmentCommand { get; }

    public RelayCommand RemoveAttachmentCommand { get; }

    public RelayCommand ResetAttachmentOffsetCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand RedoCommand { get; }

    public RelayCommand RestoreRecoveryCommand { get; }

    public RelayCommand DismissRecoveryCommand { get; }

    public RelayCommand ResetCameraCommand { get; }

    public RelayCommand FrameSelectionCommand { get; }

    public RelayCommand FrameAttachmentCommand { get; }

    public RelayCommand StoreFppProjectionCaptureCommand { get; }

    public RelayCommand StoreMovieReferenceCameraCaptureCommand { get; }

    public RelayCommand AddAdditionalRpackRootCommand { get; }

    public RelayCommand RemoveAdditionalRpackRootCommand { get; }

    public RelayCommand AutoMapCommand { get; }

    public RelayCommand ValidateMappingCommand { get; }

    public RelayCommand SaveMappingProfileCommand { get; }

    public RelayCommand AddHelperOverrideCommand { get; }

    public RelayCommand RemoveHelperOverrideCommand { get; }

    public string MappingReviewStatus
    {
        get => _mappingReviewStatus;
        private set => SetProperty(
            ref _mappingReviewStatus,
            value);
    }

    public DlraProject CurrentProject => _project;

    public PreviewProfile ActivePreviewProfile =>
        ResolvePreviewProfile();

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public Dl1RootMotionMode SelectedRootMotionMode
    {
        get => _selectedRootMotionMode;
        set
        {
            if (SetProperty(ref _selectedRootMotionMode, value) &&
                !_synchronizingProjectBindings)
            {
                UpdateRootMotionMode(value);
            }
        }
    }

    public bool PreviewMotionAccumulationEnabled
    {
        get => GetActiveAnimation()?.PreviewMotionAccumulationEnabled == true;
        set
        {
            if (!TryGetActiveAnimation(
                    out ProjectAnimation animation,
                    out int animationIndex) ||
                animation.PreviewMotionAccumulationEnabled == value)
            {
                return;
            }

            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    animation with
                    {
                        PreviewMotionAccumulationEnabled = value,
                    }),
            });
            OnPropertyChanged();
            AddDiagnostic(
                "Info",
                "Motion accumulation",
                value
                    ? "Preview actor/world accumulation enabled"
                    : "Preview actor/world accumulation disabled",
                "Recorded skeletal local transforms and actor-relative skinned vertices remain unchanged; only actor/world placement, attachments, and the root trail move.");
            RefreshAnimationPreview();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                NewWorkspaceCommand.NotifyCanExecuteChanged();
                OpenWorkspaceCommand.NotifyCanExecuteChanged();
                SaveWorkspaceCommand.NotifyCanExecuteChanged();
                ImportAnimationCommand.NotifyCanExecuteChanged();
                PreviewSelectedAssetCommand.NotifyCanExecuteChanged();
                UseSelectedAssetAsSourceCommand.NotifyCanExecuteChanged();
                UseSelectedAssetAsTargetCommand.NotifyCanExecuteChanged();
                ImportMimicAnimationCommand.NotifyCanExecuteChanged();
                ImportFacialFbxCommand.NotifyCanExecuteChanged();
                ApplyFacialMappingReviewCommand
                    .NotifyCanExecuteChanged();
                ReviewAndLockAllFacialMappingsCommand
                    .NotifyCanExecuteChanged();
                ImportFedCommand.NotifyCanExecuteChanged();
                AutoMapCommand.NotifyCanExecuteChanged();
                ValidateMappingCommand.NotifyCanExecuteChanged();
                SaveMappingProfileCommand.NotifyCanExecuteChanged();
                AddHelperOverrideCommand.NotifyCanExecuteChanged();
                RemoveHelperOverrideCommand.NotifyCanExecuteChanged();
                NotifyExportCommands();
                ApplyFedExpressionCommand.NotifyCanExecuteChanged();
                KeyMorphPoseCommand.NotifyCanExecuteChanged();
                KeyIkConstraintCommand.NotifyCanExecuteChanged();
                BakeIkConstraintCommand.NotifyCanExecuteChanged();
                AddAttachmentCommand.NotifyCanExecuteChanged();
                ApplyAttachmentCommand.NotifyCanExecuteChanged();
                RemoveAttachmentCommand.NotifyCanExecuteChanged();
                FrameAttachmentCommand.NotifyCanExecuteChanged();
                AddAdditionalRpackRootCommand.NotifyCanExecuteChanged();
                RemoveAdditionalRpackRootCommand.NotifyCanExecuteChanged();
                ExportSelectedMeshToBlenderFbxCommand
                    .NotifyCanExecuteChanged();
                PlaySelectedExplorerAnimationCommand
                    .NotifyCanExecuteChanged();
                ConfirmExplorerAnimationTimingCommand
                    .NotifyCanExecuteChanged();
                CancelExplorerAnimationTimingCommand
                    .NotifyCanExecuteChanged();
                NotifyAnimationLibraryCommands();
            }
        }
    }

    public string? SelectedAdditionalRpackRoot
    {
        get => _selectedAdditionalRpackRoot;
        set
        {
            if (SetProperty(
                    ref _selectedAdditionalRpackRoot,
                    value))
            {
                RemoveAdditionalRpackRootCommand
                    .NotifyCanExecuteChanged();
            }
        }
    }

    public SkeletonNodeViewModel? SelectedBone
    {
        get => _selectedBone;
        set
        {
            if (_boneGizmoDrag is not null &&
                !ReferenceEquals(_selectedBone, value))
            {
                CancelBoneGizmoDrag(refreshPreview: true);
            }

            if (SetProperty(ref _selectedBone, value))
            {
                BoneEditor.Bone = value;
                UpdateBoneLayerSelectionContext();
                SyncBoneEditorFromProject();
                RefreshTimelineTracks();
                SourceViewport.SceneSource.SelectBone(value?.Index);
                TargetViewport.SceneSource.SelectBone(value?.Index);
                RefreshEditableSkeletonPreview();
                OnPropertyChanged(nameof(SelectedBoneLabel));
            }
        }
    }

    public string SelectedBoneLabel =>
        SelectedBone?.Path ?? "No bone selected";

    public bool IsViewportsLinked
    {
        get => _isViewportsLinked;
        set
        {
            if (SetProperty(ref _isViewportsLinked, value))
            {
                _viewportCoordinator.IsLinked = value;
                StatusText = value
                    ? "Viewport cameras linked"
                    : "Viewport cameras unlinked";
            }
        }
    }

    public EditorWorkspaceMode ActiveWorkspace
    {
        get => _activeWorkspace;
        set
        {
            SetWorkspace(value, preserveLegacyCutscene: false);
        }
    }

    public PreviewLayoutMode PreviewLayout => _previewLayout;

    public bool IsBrowseWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Browse;

    public bool IsAnimateWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Animate;

    public bool IsRetargetWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.RetargetEdit;

    public bool IsFaceWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Face;

    public bool IsFppWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Fpp;

    public bool IsAnimationAuthoringWorkspace =>
        ActiveWorkspace != EditorWorkspaceMode.Browse;

    public bool IsFaceOrFppWorkspace =>
        IsFaceWorkspace || IsFppWorkspace;

    public bool IsInspectorPanelVisible =>
        !IsDiagnosticsDrawerOpen &&
        (IsRetargetWorkspace || IsFaceOrFppWorkspace);

    public bool IsLinkedCameraControlVisible =>
        PreviewLayout is PreviewLayoutMode.RetargetComparison or
            PreviewLayoutMode.FacialComparison;

    public bool IsTargetSwitching
    {
        get => _isTargetSwitching;
        private set
        {
            if (SetProperty(ref _isTargetSwitching, value))
            {
                OnPropertyChanged(nameof(TargetBindingStatusText));
                OnPropertyChanged(nameof(TargetPlaybackMessage));
            }
        }
    }

    public bool IsDiagnosticsDrawerOpen
    {
        get => _isDiagnosticsDrawerOpen;
        set
        {
            if (SetProperty(ref _isDiagnosticsDrawerOpen, value))
            {
                OnPropertyChanged(nameof(IsInspectorPanelVisible));
            }
        }
    }

    public TargetBindingStatus ActiveTargetBindingStatus =>
        _targetBindingStatus;

    public string TargetBindingStatusText => IsTargetSwitching
        ? "Loading target"
        : ActiveTargetBindingStatus switch
        {
            TargetBindingStatus.Direct => "Direct same-rig playback",
            TargetBindingStatus.Ready => "Reviewed retarget ready",
            TargetBindingStatus.NeedsReview => "Retarget review required",
            _ => "No playable target",
        };

    public string TargetPlaybackMessage => IsTargetSwitching
        ? "The last valid frame is frozen while the selected target is decoded."
        : ActiveTargetBindingStatus == TargetBindingStatus.NeedsReview
            ? "Target animation is locked. Review the proposed mapping; the target remains in bind pose."
            : ActiveTargetBindingStatus == TargetBindingStatus.Invalid
                ? "Choose an explicit source and target before target playback."
                : "The target is admitted to the authoritative preview pipeline.";

    public bool IsTargetPlaybackBlocked =>
        ActiveTargetBindingStatus is
            TargetBindingStatus.NeedsReview or
            TargetBindingStatus.Invalid;

    public string ActiveAnimationLabel =>
        GetActiveAnimation()?.Name ?? "No animation";

    public bool CanOpenAnimateWorkspace =>
        GetActiveAnimation() is not null;

    public string AnimateWorkspaceHint => CanOpenAnimateWorkspace
        ? "Open the active animation and its authoritative target preview."
        : "Play or import an animation before opening Animate.";

    public string ActiveSourceModelLabel
    {
        get
        {
            Guid? sourceModelId = GetActiveAnimation()?.SourceBinding?
                .RetailSourceModelAssetId;
            ProjectAssetReference? source = sourceModelId is { } id
                ? FindProjectAsset(id)
                : _sourceModelContext?.ProjectAsset;
            return source?.RetailIdentity?.ResourceName ??
                (_sourceAnimation?.SourceKind ?? "No source model");
        }
    }

    public string ActiveTargetModelLabel =>
        _targetProjectAsset?.RetailIdentity?.ResourceName ??
        "No target model";

    public bool IsSourceViewportVisible
    {
        get => _isSourceViewportVisible;
        private set => SetProperty(
            ref _isSourceViewportVisible,
            value);
    }

    public bool ShowDeformBones
    {
        get => _showDeformBones;
        set
        {
            if (SetProperty(ref _showDeformBones, value))
            {
                ApplySkeletonVisibility();
            }
        }
    }

    public bool ShowMeshes
    {
        get => _showMeshes;
        set
        {
            if (!SetProperty(ref _showMeshes, value))
            {
                return;
            }

            SourceViewport.SceneSource.SetMeshVisibility(value);
            TargetViewport.SceneSource.SetMeshVisibility(value);
            StatusText = value
                ? "Mesh surfaces visible"
                : "Mesh surfaces hidden; decoded scene retained";
        }
    }

    public bool ShowSkeletonOverlay
    {
        get => _showSkeletonOverlay;
        set
        {
            if (SetProperty(
                    ref _showSkeletonOverlay,
                    value))
            {
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

    public bool ShowRootMotionTrail
    {
        get => _showRootMotionTrail;
        set
        {
            if (!SetProperty(ref _showRootMotionTrail, value))
            {
                return;
            }

            ApplyAuthoringOverlays();
            if (value)
            {
                EnsureRootMotionTrail();
            }
            else
            {
                CancelRootMotionTrailJob("Disabled");
            }
        }
    }

    public bool ShowDeformedBounds
    {
        get => _showDeformedBounds;
        set
        {
            if (SetProperty(ref _showDeformedBounds, value))
            {
                ApplyAuthoringOverlays();
            }
        }
    }

    public bool ShowBoneLocalAxes
    {
        get => _showBoneLocalAxes;
        set
        {
            if (SetProperty(ref _showBoneLocalAxes, value))
            {
                ApplyAuthoringOverlays();
            }
        }
    }

    public bool HighlightSelectedMeshes
    {
        get => _highlightSelectedMeshes;
        set
        {
            if (SetProperty(
                    ref _highlightSelectedMeshes,
                    value))
            {
                ApplyAuthoringOverlays();
            }
        }
    }

    public bool HasRecoverySnapshot
    {
        get => _hasRecoverySnapshot;
        private set
        {
            if (SetProperty(ref _hasRecoverySnapshot, value))
            {
                RestoreRecoveryCommand.NotifyCanExecuteChanged();
                DismissRecoveryCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ActiveWorkspaceMode
    {
        get => _activeWorkspaceMode;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value)
                ? "Browse"
                : value.Trim();
            EditorWorkspaceMode workspace = normalized switch
            {
                "Animate" => EditorWorkspaceMode.Animate,
                "Retarget" or "Retarget/Edit" or "Bone Edit" =>
                    EditorWorkspaceMode.RetargetEdit,
                "Facial" or "Face" => EditorWorkspaceMode.Face,
                "FPP" or "Cutscene" => EditorWorkspaceMode.Fpp,
                _ => EditorWorkspaceMode.Browse,
            };
            bool preserveCutscene = string.Equals(
                normalized,
                "Cutscene",
                StringComparison.Ordinal);
            SetWorkspace(workspace, preserveCutscene);
        }
    }

    private void SelectWorkspace(string? value)
    {
        if (string.Equals(
                value,
                "Animate",
                StringComparison.Ordinal) &&
            !CanOpenAnimateWorkspace)
        {
            StatusText =
                "Animate needs an active animation. The Browse preview remains unchanged.";
            return;
        }

        ActiveWorkspaceMode = value ?? "Browse";
    }

    private void SetWorkspace(
        EditorWorkspaceMode workspace,
        bool preserveLegacyCutscene)
    {
        EditorWorkspaceMode previousWorkspace = _activeWorkspace;
        string previousLegacyName = _activeWorkspaceMode;
        bool usedLinkedTargetView = UsesLinkedTargetExternalView();
        string legacyName = preserveLegacyCutscene
            ? "Cutscene"
            : workspace switch
            {
                EditorWorkspaceMode.Browse => "Browse",
                EditorWorkspaceMode.Animate => "Animate",
                EditorWorkspaceMode.RetargetEdit => "Retarget",
                EditorWorkspaceMode.Face => "Facial",
                EditorWorkspaceMode.Fpp => "FPP",
                _ => "Browse",
            };
        bool workspaceChanged = SetProperty(
            ref _activeWorkspace,
            workspace,
            nameof(ActiveWorkspace));
        bool legacyChanged = SetProperty(
            ref _activeWorkspaceMode,
            legacyName,
            nameof(ActiveWorkspaceMode));
        PreviewLayoutMode layout = workspace switch
        {
            EditorWorkspaceMode.Browse =>
                PreviewLayoutMode.IsolatedBrowse,
            EditorWorkspaceMode.Animate =>
                PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.RetargetEdit =>
                PreviewLayoutMode.RetargetComparison,
            EditorWorkspaceMode.Face =>
                PreviewLayoutMode.FacialComparison,
            EditorWorkspaceMode.Fpp =>
                PreviewLayoutMode.FppDualView,
            _ => PreviewLayoutMode.IsolatedBrowse,
        };
        if (_previewLayout != layout)
        {
            _previewLayout = layout;
            OnPropertyChanged(nameof(PreviewLayout));
        }

        IsSourceViewportVisible = layout is
            PreviewLayoutMode.RetargetComparison or
            PreviewLayoutMode.FacialComparison or
            PreviewLayoutMode.FppDualView;
        if (workspaceChanged || legacyChanged)
        {
            if (previousWorkspace == EditorWorkspaceMode.Browse &&
                workspace != EditorWorkspaceMode.Browse)
            {
                SuspendIsolatedBrowsePreview();
            }

            bool usesLinkedTargetView = UsesLinkedTargetExternalView();
            if (!usesLinkedTargetView)
            {
                // Layout changes consume the last authoritative frame pair.
                // Leaving FPP/movie comparison must restore the authored
                // source scene without evaluating or mutating the project.
                if (usedLinkedTargetView)
                {
                    _suspendedTargetProjection = TargetViewport.SceneSource
                        .CaptureFrame()
                        .FppProjectionState;
                }

                _viewportCoordinator
                    .SetTargetPreviewCameraOverrideActive(false);
                TargetViewport.SceneSource.SetFppProjectionState(null);
                ClearLinkedTargetExternalView();
            }
            else if (!usedLinkedTargetView)
            {
                bool restoredPreviewCamera = _viewportCoordinator
                    .SetTargetPreviewCameraOverrideActive(true);
                TargetViewport.SceneSource.SetFppProjectionState(
                    restoredPreviewCamera
                        ? _suspendedTargetProjection
                        : null);
                _suspendedTargetProjection = null;
                RestoreLinkedTargetExternalViewFromCurrentScene();
            }
            else if (!string.Equals(
                         previousLegacyName,
                         legacyName,
                         StringComparison.Ordinal))
            {
                // FPP and movie/cutscene cameras are different contracts.
                // A context switch waits for its own evaluated camera rather
                // than reusing the other context's override.
                _viewportCoordinator.SetTargetPreviewCameraOverride(null);
                _suspendedTargetProjection = null;
                TargetViewport.SceneSource.SetFppProjectionState(null);
                RestoreLinkedTargetExternalViewFromCurrentScene();
            }

            if (workspace == EditorWorkspaceMode.Browse &&
                previousWorkspace != EditorWorkspaceMode.Browse)
            {
                RestoreIsolatedBrowsePreview();
            }
            OnPropertyChanged(nameof(IsBrowseWorkspace));
            OnPropertyChanged(nameof(IsAnimateWorkspace));
            OnPropertyChanged(nameof(IsRetargetWorkspace));
            OnPropertyChanged(nameof(IsFaceWorkspace));
            OnPropertyChanged(nameof(IsFppWorkspace));
            OnPropertyChanged(nameof(IsAnimationAuthoringWorkspace));
            OnPropertyChanged(nameof(IsFaceOrFppWorkspace));
            OnPropertyChanged(nameof(IsInspectorPanelVisible));
            OnPropertyChanged(nameof(IsLinkedCameraControlVisible));
            OnPropertyChanged(nameof(ActivePreviewProfile));
            UpdateFidelityStatusBadges();
            if (_sourceAnimation is null)
            {
                UpdateUnevaluatedPreviewStatus(
                    "Load or activate an animation to evaluate this workspace.");
            }
        }
    }

    private void SuspendIsolatedBrowsePreview()
    {
        if (_isolatedBrowsePreviewFrame is null)
        {
            return;
        }

        _browseOrbitCameras = _viewportCoordinator
            .CaptureOrbitCameras();
        TargetViewport.SceneSource.SetExternalPreviewScene(null);
        if (_authoringOrbitCameras is not null)
        {
            _viewportCoordinator.RestoreOrbitCameras(
                _authoringOrbitCameras);
        }
    }

    private void RestoreIsolatedBrowsePreview()
    {
        if (_isolatedBrowsePreviewFrame is null)
        {
            return;
        }

        _authoringOrbitCameras = _viewportCoordinator
            .CaptureOrbitCameras();
        if (_browseOrbitCameras is not null)
        {
            _viewportCoordinator.RestoreOrbitCameras(
                _browseOrbitCameras);
        }

        TargetViewport.SceneSource.SetExternalPreviewScene(
            _isolatedBrowsePreviewFrame);
        TargetViewport.SetPresentation(
            _isolatedBrowsePreviewTitle ?? "Asset Preview",
            _isolatedBrowsePreviewFidelity ??
                "Isolated retail asset; project state unchanged");
    }

    private void ClearIsolatedBrowsePreview()
    {
        TargetViewport.SceneSource.SetExternalPreviewScene(null);
        _isolatedBrowsePreviewFrame = null;
        _isolatedBrowsePreviewTitle = null;
        _isolatedBrowsePreviewFidelity = null;
        _authoringOrbitCameras = null;
        _browseOrbitCameras = null;
    }

    private void SetTargetBindingStatus(TargetBindingStatus status)
    {
        _editorSessionCoordinator.UpdateTargetStatus(status);
        if (_targetBindingStatus == status)
        {
            return;
        }

        _targetBindingStatus = status;
        OnPropertyChanged(nameof(ActiveTargetBindingStatus));
        OnPropertyChanged(nameof(TargetBindingStatusText));
        OnPropertyChanged(nameof(TargetPlaybackMessage));
        OnPropertyChanged(nameof(IsTargetPlaybackBlocked));
    }

    public string SelectedPreviewMode
    {
        get => _selectedPreviewMode;
        set
        {
            string normalized = string.Equals(
                    value,
                    RawPreviewModeLabel,
                    StringComparison.Ordinal)
                ? RawPreviewModeLabel
                : Dl1ProfilePreviewModeLabel;
            if (!SetProperty(ref _selectedPreviewMode, normalized) ||
                _synchronizingPreviewMode)
            {
                return;
            }

            PreviewProfile profile = ResolvePreviewProfile();
            DlraProject updated = normalized == RawPreviewModeLabel
                ? _project with
                {
                    PreviewMode = ProjectPreviewMode.Raw,
                }
                : _project with
                {
                    PreviewMode = ProjectPreviewMode.Dl1Profile,
                    PreviewProfile = profile,
                };
            updated.Validate();
            CommitProject(updated);
            OnPropertyChanged(nameof(ActivePreviewProfile));
            StatusText = normalized == RawPreviewModeLabel
                ? "Raw preview active: decoded/authored data with no DL1 runtime-emulation claim"
                : "DL1 profile preview active: versioned authoring emulation with visible fidelity status";
        }
    }

    public string? ProjectPath
    {
        get => _projectPath;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : Path.GetFullPath(value);
            if (SetProperty(ref _projectPath, normalized))
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    public string WindowTitle
    {
        get
        {
            string name = ProjectPath is null
                ? _project.Name
                : Path.GetFileNameWithoutExtension(ProjectPath);
            string dirtyMarker = IsDirty ? "*" : string.Empty;
            return $"Dying Light ReAnimated — {name}{dirtyMarker} — DL1";
        }
    }

    public WorkspaceSnapshot CreateSnapshot()
    {
        return new WorkspaceSnapshot(
            WorkspaceSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            ProjectPath,
            AssetBrowser.SearchText,
            AssetBrowser.SelectedAsset?.Id,
            SelectedBone?.Path,
            Timeline.CurrentFrame,
            IsViewportsLinked,
            FacialFpp.FieldOfView,
            FacialFpp.NearPlane,
            ActiveWorkspaceMode,
            CreateProjectWithCurrentPreviewConfiguration(),
            IsDirty,
            ShowMeshes,
            ShowSkeletonOverlay);
    }

    public void RestoreSnapshot(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Project is not null)
        {
            try
            {
                snapshot.Project.Validate();
                SetProject(
                    snapshot.Project,
                    markSaved: !snapshot.IsProjectDirty,
                    clearHistory: true,
                    clearPreview: true);
            }
            catch (Exception exception) when (
                exception is ProjectFormatException
                or ArgumentException
                or InvalidOperationException)
            {
                AddDiagnostic(
                    "Error",
                    "Recovery",
                    "The recovery project is invalid",
                    exception.Message);
            }
        }

        ProjectPath = snapshot.ProjectPath;
        AssetBrowser.SearchText = snapshot.AssetSearch;
        Timeline.CurrentFrame = snapshot.CurrentFrame;
        IsViewportsLinked = snapshot.ViewportsLinked;
        FacialFpp.FieldOfView = snapshot.FppFieldOfView;
        FacialFpp.NearPlane = snapshot.FppNearPlane;
        if (_project.Animations.IsEmpty)
        {
            ActiveWorkspaceMode = snapshot.ActiveWorkspaceMode;
        }
        else
        {
            SetWorkspace(
                ResolveStartupWorkspace(_project),
                preserveLegacyCutscene: false);
        }
        ShowMeshes = snapshot.MeshesVisible ?? true;
        ShowSkeletonOverlay =
            snapshot.SkeletonOverlayVisible ?? true;
        SelectedBone = FindBone(snapshot.SelectedBonePath);
        StatusText = $"Recovered workspace from {snapshot.SavedAt.LocalDateTime:g}";
    }

    public void NotifyAutosave(AutosaveCompletedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Succeeded)
        {
            StatusText = IsDirty
                ? $"Recovery autosaved {args.Timestamp.LocalDateTime:T}; project still has unsaved edits"
                : $"Recovery autosaved {args.Timestamp.LocalDateTime:T}";
        }
        else
        {
            AddDiagnostic(
                "Error",
                "Autosave",
                "Workspace autosave failed",
                args.Error);
        }
    }

    public void TickPlayback(DateTimeOffset now)
    {
        Timeline.Tick(now);
    }

    /// <summary>
    /// Opens the validated LocalAppData catalog on application startup. The
    /// underlying catalog checks bounded pack fingerprints and only scans the
    /// retail sources again when that saved snapshot is absent or stale.
    /// Concurrent startup/manual requests share the same operation.
    /// </summary>
    public Task InitializeAssetCatalogAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (_assetCatalogLoadTask is { IsCompleted: false } activeLoad)
        {
            return activeLoad;
        }

        _assetCatalogLoadTask = LoadAssetCatalogAsync();
        return _assetCatalogLoadTask;
    }

    public async Task InitializeInstalledBuildStatusAsync(
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        _isReadingInstalledBuildFingerprint = true;
        _installedBuildFingerprintError = null;
        UpdateFidelityStatusBadges();
        using CancellationTokenSource linkedSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeSource.Token);
        try
        {
            Dl1InstalledBuildFingerprint? fingerprint =
                await _installedBuildFingerprintService
                    .TryReadDiscoveredAsync(linkedSource.Token);
            if (_disposed)
            {
                return;
            }

            _installedBuildFingerprint = fingerprint;
            _hasReadInstalledBuildFingerprint = true;
            OnPropertyChanged(nameof(InstalledBuildFingerprint));
            if (fingerprint is null)
            {
                AddDiagnostic(
                    "Info",
                    "Fidelity",
                    "No complete Dying Light 1 installation was detected",
                    "Game-validated preview profiles remain downgraded until a Windows DL1 executable fingerprint is available.");
            }
            else
            {
                AddDiagnostic(
                    "Info",
                    "Fidelity",
                    "Installed Dying Light 1 build fingerprinted",
                    $"DyingLightGame.exe {fingerprint.FileVersion}; {fingerprint.ExecutableSize:N0} bytes; build {ShortFingerprint(fingerprint.BuildFingerprint)}.");
            }
        }
        catch (OperationCanceledException) when (
            linkedSource.IsCancellationRequested)
        {
            if (!_disposed && !cancellationToken.IsCancellationRequested)
            {
                _hasReadInstalledBuildFingerprint = true;
                _installedBuildFingerprintError =
                    "Installed-build fingerprinting was canceled.";
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            InvalidOperationException)
        {
            if (!_disposed)
            {
                _hasReadInstalledBuildFingerprint = true;
                _installedBuildFingerprintError = exception.Message;
                AddDiagnostic(
                    "Warning",
                    "Fidelity",
                    "Installed Dying Light 1 build could not be fingerprinted",
                    exception.Message);
            }
        }
        finally
        {
            _isReadingInstalledBuildFingerprint = false;
            if (!_disposed)
            {
                UpdateFidelityStatusBadges();
            }
        }
    }

    public void SetSourcePreviewScene(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData? skeleton,
        IReadOnlyList<GizmoRenderData>? gizmos = null,
        IReadOnlyList<MorphWeight>? morphWeights = null,
        long? generation = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        SourceViewport.SceneSource.SetScene(
            meshes,
            skeleton,
            gizmos ?? Array.Empty<GizmoRenderData>(),
            morphWeights,
            generation);
        SourceViewport.SceneSource.SelectBone(SelectedBone?.Index);
    }

    public void SetTargetPreviewScene(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData? skeleton,
        IReadOnlyList<GizmoRenderData>? gizmos = null,
        IReadOnlyList<MorphWeight>? morphWeights = null,
        long? generation = null)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        _targetBaseMeshes = meshes
            .Select(static mesh => mesh with
            {
                IsSelected = true,
            })
            .ToArray();
        TargetViewport.SceneSource.SetScene(
            _targetBaseMeshes,
            skeleton,
            gizmos ?? Array.Empty<GizmoRenderData>(),
            morphWeights,
            generation);
        TargetViewport.SceneSource.SelectBone(SelectedBone?.Index);
        ApplyAuthoringOverlays();
    }

    public void RefreshEditableSkeletonPreview()
    {
        if (_sourceAnimation is not null &&
            _targetRig is not null &&
            (HasSameRigContract(
                 _sourceAnimation.Rig,
                 _targetRig) ||
             _activeRetargetMap is not null))
        {
            RefreshAnimationPreview();
            return;
        }

        SkeletonRenderData? skeleton = BuildEditableSkeleton();
        if (skeleton is null && SkeletonRoots.Count > 0)
        {
            // A decoded raw bind skeleton can remain renderable even when an
            // unweighted helper has a deliberately non-TRS local matrix. Do
            // not turn that local editor limitation into a missing-skeleton
            // failure for every skinned draw.
            SourceViewport.SceneSource.SetGizmos([]);
            TargetViewport.SceneSource.SetGizmos([]);
            ApplyAuthoringOverlays();
            return;
        }

        GizmoRenderData[] gizmos = BuildBoneEditGizmos(skeleton);
        SourceViewport.SceneSource.SetSkeleton(skeleton);
        SourceViewport.SceneSource.SetGizmos(gizmos);
        TargetViewport.SceneSource.SetSkeleton(skeleton);
        TargetViewport.SceneSource.SetGizmos(gizmos);
        ApplyAuthoringOverlays();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelAutomaticAssetPreview();
        _lifetimeSource.Cancel();
        if (_assetDecodeJob is { IsCancellable: true } activeAssetDecode)
        {
            activeAssetDecode.Cancel();
        }
        if (_automaticAssetPreviewTask is { } automaticPreviewTask)
        {
            await automaticPreviewTask;
        }
        Task<Vector3[]>[] rootMotionTrailWorkers;
        lock (_rootMotionTrailWorkerGate)
        {
            rootMotionTrailWorkers =
                _rootMotionTrailWorkers.ToArray();
        }

        CancelRootMotionTrailJob("Canceled");
        AssetBrowser.IndexGameRequested -= OnIndexGameRequested;
        AssetBrowser.SelectedAssetChanged -= OnSelectedAssetChanged;
        AssetBrowser.ProfileScanRequested -= OnProfileScanRequested;
        AssetBrowser.ProfileScanCancellationRequested -=
            OnProfileScanCancellationRequested;
        BoneEditor.TransformApplied -= OnBoneTransformApplied;
        BoneEditor.GizmoModeChanged -= OnBoneGizmoModeChanged;
        BoneEditor.GizmoSpaceChanged -= OnBoneGizmoSpaceChanged;
        CancelBoneGizmoDrag(refreshPreview: false);
        SourceViewport.SceneSource.SetTransformGizmoTarget(null);
        TargetViewport.SceneSource.SetTransformGizmoTarget(null);
        SourceViewport.SceneSource.SetTranslationGizmoTarget(null);
        TargetViewport.SceneSource.SetTranslationGizmoTarget(null);
        Timeline.CurrentFrameChanged -= OnTimelineFrameChanged;
        Timeline.PropertyChanged -= OnTimelinePropertyChanged;
        Timeline.KeyframeRequested -= OnTimelineKeyframeRequested;
        FacialFpp.LensChanged -= OnLensChanged;
        FacialFpp.MorphWeightsChanged -= OnMorphWeightsChanged;
        FacialFpp.PropertyChanged -= OnFacialFppPropertyChanged;
        IkEditor.PropertyChanged -= OnIkEditorPropertyChanged;
        AttachmentEditor.PropertyChanged -=
            OnAttachmentEditorPropertyChanged;

        foreach (JobViewModel job in Jobs)
        {
            job.Dispose();
        }

        _assetDecodeJob = null;
        _assetProfileScanJob = null;
        if (rootMotionTrailWorkers.Length > 0)
        {
            try
            {
                await Task.WhenAll(rootMotionTrailWorkers)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
            {
            }
        }

        lock (_rootMotionTrailWorkerGate)
        {
            _rootMotionTrailWorkers.Clear();
            _rootMotionTrailWorkerTask = null;
        }

        await _assetWorkspace.DisposeAsync().ConfigureAwait(false);
        _lifetimeSource.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Dl1AssetWorkspace CreateDefaultAssetWorkspace()
    {
        AppPaths paths = AppPaths.CreateDefault();
        return new Dl1AssetWorkspace(
            paths.AssetIndexFile,
            paths.RpackCacheDirectory);
    }

    private void BuildFidelityBadges()
    {
        FidelityBadges.Add(new FidelityBadgeViewModel(
            PreviewFidelityBadgeLabel,
            "DL1 profile",
            "No captured Windows build profile is active."));
        FidelityBadges.Add(new FidelityBadgeViewModel(
            InstalledBuildBadgeLabel,
            "Not checked",
            "Installed-build detection has not run."));
        FidelityBadges.Add(new FidelityBadgeViewModel(
            "DL1 scope",
            "Verified target",
            "This first application pass is deliberately DL1-only."));
        foreach (RenderFeatureStatus feature in RenderFeatureManifest.Features)
        {
            FidelityBadges.Add(new FidelityBadgeViewModel(
                Humanize(feature.Feature.ToString()),
                feature.Availability switch
                {
                    RenderFeatureAvailability.Available => "Available",
                    RenderFeatureAvailability.HookOnly => "Hook only",
                    _ => "Not implemented",
                },
                feature.Detail));
        }

        UpdateFidelityStatusBadges();
    }

    private void UpdateFidelityStatusBadges()
    {
        if (FidelityBadges.Count == 0)
        {
            return;
        }

        PreviewProfile profile = ResolvePreviewProfile();
        PreviewFidelityTier effectiveTier =
            profile.GetEffectiveFidelityTier(
                _installedBuildFingerprint?.BuildFingerprint);
        string state = effectiveTier switch
        {
            PreviewFidelityTier.Raw => "Raw",
            PreviewFidelityTier.Dl1Profile => "DL1 profile",
            PreviewFidelityTier.GameValidated => "Game validated",
            _ => throw new InvalidOperationException(
                $"Unsupported preview fidelity tier '{effectiveTier}'."),
        };
        string detail;
        if (profile.FidelityTier != PreviewFidelityTier.GameValidated)
        {
            detail = effectiveTier == PreviewFidelityTier.Raw
                ? "The active preview shows decoded/authored data without a DL1 runtime-emulation validation claim."
                : "The active preview uses versioned DL1 authoring emulation. No captured Windows build validation profile is active.";
        }
        else if (_isReadingInstalledBuildFingerprint)
        {
            detail =
                $"The saved Game validated profile requires build {ShortFingerprint(profile.BuildFingerprint)}. Installed-build detection is still running, so the visible tier is downgraded.";
        }
        else if (_installedBuildFingerprint is null)
        {
            detail =
                !_hasReadInstalledBuildFingerprint
                    ? $"The saved Game validated profile requires build {ShortFingerprint(profile.BuildFingerprint)}. Installed-build detection has not run, so the visible tier is downgraded."
                    : $"The saved Game validated profile requires build {ShortFingerprint(profile.BuildFingerprint)}. No installed build fingerprint is available, so the visible tier is downgraded.";
        }
        else if (!string.Equals(
                     profile.BuildFingerprint,
                     _installedBuildFingerprint.BuildFingerprint,
                     StringComparison.OrdinalIgnoreCase))
        {
            detail =
                $"The saved Game validated profile requires build {ShortFingerprint(profile.BuildFingerprint)}, but the installed executable is {_installedBuildFingerprint.FileVersion} / {ShortFingerprint(_installedBuildFingerprint.BuildFingerprint)}. The visible tier is downgraded.";
        }
        else if (effectiveTier != PreviewFidelityTier.GameValidated)
        {
            detail =
                $"The installed build matches {ShortFingerprint(profile.BuildFingerprint)}, but no independently trusted capture registry entry matches validation capture {ShortFingerprint(profile.CaptureFingerprint)}. Project metadata is not trusted by itself, so the visible tier is downgraded.";
        }
        else
        {
            detail =
                $"The installed build and independently trusted validation capture both match DyingLightGame.exe {_installedBuildFingerprint.FileVersion} ({ShortFingerprint(_installedBuildFingerprint.BuildFingerprint)}).";
        }

        ReplaceFidelityBadge(
            PreviewFidelityBadgeLabel,
            new FidelityBadgeViewModel(
                PreviewFidelityBadgeLabel,
                state,
                detail));

        FidelityBadgeViewModel installedBadge;
        if (_isReadingInstalledBuildFingerprint)
        {
            installedBadge = new FidelityBadgeViewModel(
                InstalledBuildBadgeLabel,
                "Detecting",
                "Streaming DyingLightGame.exe through the bounded SHA-256 build-identity reader. The game is not launched.");
        }
        else if (_installedBuildFingerprint is { } installed)
        {
            installedBadge = new FidelityBadgeViewModel(
                InstalledBuildBadgeLabel,
                "Detected",
                $"File {installed.FileVersion}; product {installed.ProductVersion}; {installed.ExecutableSize:N0} bytes; executable SHA-256 {installed.ExecutableSha256}; build fingerprint {installed.BuildFingerprint}.");
        }
        else if (!string.IsNullOrWhiteSpace(_installedBuildFingerprintError))
        {
            installedBadge = new FidelityBadgeViewModel(
                InstalledBuildBadgeLabel,
                "Unavailable",
                _installedBuildFingerprintError);
        }
        else if (!_hasReadInstalledBuildFingerprint)
        {
            installedBadge = new FidelityBadgeViewModel(
                InstalledBuildBadgeLabel,
                "Not checked",
                "Installed-build detection has not run. The application performs this read-only check at startup.");
        }
        else
        {
            installedBadge = new FidelityBadgeViewModel(
                InstalledBuildBadgeLabel,
                "Not found",
                "No complete Steam installation was detected. This does not affect Raw or DL1 profile authoring, but Game validated profiles remain downgraded.");
        }

        ReplaceFidelityBadge(InstalledBuildBadgeLabel, installedBadge);
    }

    private void ReplaceFidelityBadge(
        string label,
        FidelityBadgeViewModel replacement)
    {
        int index = FidelityBadges
            .Select((badge, badgeIndex) => (badge, badgeIndex))
            .Where(item => string.Equals(
                item.badge.Label,
                label,
                StringComparison.Ordinal))
            .Select(static item => item.badgeIndex)
            .DefaultIfEmpty(-1)
            .First();
        if (index >= 0)
        {
            FidelityBadges[index] = replacement;
        }
    }

    private static string ShortFingerprint(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint)
            ? "unavailable"
            : fingerprint.Length <= 12
                ? fingerprint
                : $"{fingerprint[..12]}…";

    private void TryLoadRecoveryMetadata()
    {
        try
        {
            _recoverySnapshot = _recoveryStore.Load();
            HasRecoverySnapshot = _recoverySnapshot is not null;
        }
        catch (Exception exception)
        {
            AddDiagnostic(
                "Warning",
                "Recovery",
                "Recovery snapshot could not be read",
                exception.Message);
        }
    }

    private void RestoreRecovery()
    {
        if (_recoverySnapshot is null)
        {
            return;
        }

        try
        {
            WorkspaceSnapshot snapshot = _recoverySnapshot;
            ProjectVariantRecoveryNormalizationResult? normalization =
                snapshot.Project is null
                    ? null
                    : ProjectVariantRecoveryNormalizer.Normalize(
                        snapshot.Project);
            string? backupPath = null;
            if (normalization is { WasRepaired: true })
            {
                backupPath = _recoveryStore.BackupCurrent();
                snapshot = snapshot with
                {
                    Project = normalization.Project,
                    IsProjectDirty = true,
                    ActiveWorkspaceMode = "Animate",
                };
            }

            RestoreSnapshot(snapshot);
            if (normalization is { WasRepaired: true })
            {
                foreach (ProjectVariantRecoveryRepair repair in
                         normalization.Repairs)
                {
                    AddDiagnostic(
                        "Warning",
                        "Recovery repair",
                        $"Paused unsafe target variant for {repair.AnimationName}",
                        $"Retained variant {repair.RetainedVariantId:N}; created or reused safe direct-source variant {repair.SafeVariantId:N} on {repair.SourceModelName}; original recovery backed up to {backupPath}.");
                }

                StatusText =
                    $"Recovery repaired: activated {normalization.Repairs.Length:N0} safe direct-source variant(s); original backed up";
            }

            HasRecoverySnapshot = false;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            AddDiagnostic(
                "Error",
                "Recovery",
                "Recovery could not be restored transactionally",
                exception.Message);
            StatusText = "Recovery restore failed; the original snapshot was retained";
        }
    }

    private void DismissRecovery()
    {
        try
        {
            _recoveryStore.Delete();
            _recoverySnapshot = null;
            HasRecoverySnapshot = false;
            StatusText = "Recovery snapshot dismissed";
        }
        catch (Exception exception)
        {
            AddDiagnostic(
                "Error",
                "Recovery",
                "Unable to dismiss recovery snapshot",
                exception.Message);
        }
    }

    private void NewWorkspace()
    {
        SetProject(
            DlraProject.Create("Untitled"),
            markSaved: true,
            clearHistory: true,
            clearPreview: true);
        ProjectPath = null;
        StatusText = "New empty DL1 project";
    }

    private void AddAdditionalRpackRoot()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            AddDiagnostic(
                "Warning",
                "Assets",
                "Save the project before adding an RPack root",
                "Additional pack roots are stored as portable project-relative paths.");
            StatusText = "Save the project before adding an RPack root";
            return;
        }

        string projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(ProjectPath))
            ?? throw new InvalidOperationException(
                "The project path has no parent directory.");
        string? selected = _fileDialogs
            .ShowSelectAdditionalRpackRootDialog(projectDirectory);
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        string fullRoot = Path.GetFullPath(selected);
        string relative = Path.GetRelativePath(
                projectDirectory,
                fullRoot)
            .Replace(
                Path.DirectorySeparatorChar,
                '/');
        if (Path.IsPathRooted(relative) ||
            relative is "." or ".." ||
            relative.StartsWith("../", StringComparison.Ordinal))
        {
            AddDiagnostic(
                "Error",
                "Assets",
                "The additional RPack root is outside the project",
                "Choose a subfolder beside the project so the path remains portable and no retail content is embedded.");
            StatusText = "RPack root must be inside the project directory";
            return;
        }

        if (_project.Dl1Settings.AdditionalRpackRoots.Contains(
                relative,
                StringComparer.OrdinalIgnoreCase))
        {
            SelectedAdditionalRpackRoot =
                AdditionalRpackRoots.FirstOrDefault(root =>
                    string.Equals(
                        root,
                        relative,
                        StringComparison.OrdinalIgnoreCase));
            StatusText = $"RPack root '{relative}' is already configured";
            return;
        }

        DlraProject updated = _project with
        {
            Dl1Settings = _project.Dl1Settings with
            {
                AdditionalRpackRoots =
                    _project.Dl1Settings.AdditionalRpackRoots.Add(
                        relative),
            },
        };
        updated.Validate();
        CommitProject(updated);
        SelectedAdditionalRpackRoot = relative;
        StatusText =
            $"Added project RPack root '{relative}'; re-index DL1 to apply it";
    }

    private void RemoveAdditionalRpackRoot()
    {
        if (SelectedAdditionalRpackRoot is not { } selected)
        {
            return;
        }

        ImmutableArray<string> roots =
            _project.Dl1Settings.AdditionalRpackRoots
                .Where(root => !string.Equals(
                    root,
                    selected,
                    StringComparison.OrdinalIgnoreCase))
                .ToImmutableArray();
        if (roots.Length ==
            _project.Dl1Settings.AdditionalRpackRoots.Length)
        {
            return;
        }

        DlraProject updated = _project with
        {
            Dl1Settings = _project.Dl1Settings with
            {
                AdditionalRpackRoots = roots,
            },
        };
        updated.Validate();
        CommitProject(updated);
        StatusText =
            $"Removed project RPack root '{selected}'; re-index DL1 to apply it";
    }

    private async Task OpenWorkspaceAsync()
    {
        string? path = _fileDialogs.ShowOpenProjectDialog(ProjectPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        StatusText = $"Opening {Path.GetFileName(path)}…";
        try
        {
            DlraProject loaded = await Task.Run(
                () => ProjectSerializer.Load(path));
            SetProject(
                loaded,
                markSaved: true,
                clearHistory: true,
                clearPreview: true);
            ProjectPath = path;
            AddRecentProjectPath(path);
            await LoadActiveSourceAsync(path);
            StatusText = $"Opened DL1 project {loaded.Name}";
        }
        catch (LegacyProjectFormatException exception)
        {
            AddDiagnostic(
                "Error",
                "Project",
                "Legacy Python project was not opened",
                exception.Message);
            StatusText = "Project open failed";
        }
        catch (Exception exception) when (
            exception is ProjectFormatException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            AddDiagnostic(
                "Error",
                "Project",
                "Project could not be opened",
                exception.Message);
            StatusText = "Project open failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadActiveSourceAsync(string projectPath)
    {
        ProjectAnimation? animation = GetActiveAnimation();
        if (animation is null)
        {
            return;
        }

        ProjectAssetReference? sourceAsset =
            _project.Assets.FirstOrDefault(asset =>
                asset.Id == animation.SourceAssetId);
        if (sourceAsset is null)
        {
            throw new ProjectFormatException(
                "The active animation source asset is missing.");
        }

        if (animation.SourceBinding is not { } sourceBinding)
        {
            AddDiagnostic(
                "Error",
                "ANM2 source binding",
                "The active document has no provable immutable source binding",
                "Playback is blocked. Select an exact-signature retail model and use Rebind Source; the existing authored document will not be mutated.");
            StatusText = "Animation source needs an explicit rebind";
            return;
        }

        if (sourceBinding.Kind is
            AnimationSourceKind.LocalAnm2 or
            AnimationSourceKind.RetailAnm2)
        {
            if (sourceBinding.RetailSourceModelAssetId is null)
            {
                AddDiagnostic(
                    "Error",
                    "ANM2 source binding",
                    "The saved ANM2 does not identify an exact retail source model",
                    "Playback is blocked until Rebind Source creates a new animation document.");
                StatusText = "ANM2 source model is unproven";
                return;
            }

            if (sourceBinding.Kind == AnimationSourceKind.LocalAnm2)
            {
                if (sourceAsset.Kind != ProjectAssetKind.SourceAnimation)
                {
                    throw new ProjectFormatException(
                        "The local ANM2 binding does not refer to a project animation source.");
                }

                string sourceProjectDirectory = Path.GetDirectoryName(
                        Path.GetFullPath(projectPath))
                    ?? throw new ProjectFormatException(
                        "The project path has no parent directory.");
                string localPath = ResolveProjectAssetPath(
                    sourceProjectDirectory,
                    sourceAsset,
                    "animation source");
                string actualHash =
                    await ProjectSourceImporter.ComputeSha256Async(
                        localPath,
                        CancellationToken.None);
                if (!string.Equals(
                        actualHash,
                        sourceAsset.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Animation source hash mismatch for '{sourceAsset.RelativePath}'.");
                }
            }

            AddDiagnostic(
                "Info",
                "ANM2 source binding",
                "The saved ANM2 is waiting for the indexed retail catalog",
                "After indexing, the editor will resolve the exact saved source-model fingerprint and activate the clip without using the currently selected target as a fallback.");
            StatusText = "Load the asset catalog to resolve the saved ANM2 source model";
            return;
        }

        if (sourceAsset.Kind != ProjectAssetKind.SourceAnimation)
        {
            throw new ProjectFormatException(
                "The local FBX binding does not refer to a project animation source.");
        }

        string projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new ProjectFormatException(
                "The project path has no parent directory.");
        string sourcePath = ResolveProjectAssetPath(
            projectDirectory,
            sourceAsset,
            "animation source");

        if (sourceAsset.ContentSha256 is { } expectedHash)
        {
            string actualHash =
                await ProjectSourceImporter.ComputeSha256Async(
                    sourcePath,
                    CancellationToken.None);
            if (!string.Equals(
                    actualHash,
                    expectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Animation source hash mismatch for '{sourceAsset.RelativePath}'.");
            }
        }

        _mimicAnimation = null;
        _facialFbxAnimation = null;
        _synchronizedAnimation = null;
        _pendingMimicSourcePath = null;
        _pendingMimicAssetId = null;
        _pendingFacialFbxSourcePath = null;
        _pendingFacialFbxAssetId = null;
        if (animation.MimicAssetId is { } mimicAssetId)
        {
            ProjectAssetReference mimicAsset =
                _project.Assets.FirstOrDefault(asset =>
                    asset.Id == mimicAssetId &&
                    asset.Kind == ProjectAssetKind.SourceAnimation)
                ?? throw new ProjectFormatException(
                    "The active animation mimic asset is missing or is not a source animation.");
            if (mimicAsset.ContentSha256 is not { } expectedMimicHash)
            {
                throw new ProjectFormatException(
                    "A saved mimic asset requires an exact SHA-256 fingerprint.");
            }

            string mimicPath = ResolveProjectAssetPath(
                projectDirectory,
                mimicAsset,
                "mimic ANM2");
            if (!string.Equals(
                    Path.GetExtension(mimicPath),
                    ".anm2",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The saved mimic project asset is not an ANM2 file.");
            }

            string actualMimicHash =
                await ProjectSourceImporter.ComputeSha256Async(
                    mimicPath,
                    CancellationToken.None);
            if (!string.Equals(
                    actualMimicHash,
                    expectedMimicHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Mimic source hash mismatch for '{mimicAsset.RelativePath}'.");
            }

            _pendingMimicSourcePath = mimicPath;
            _pendingMimicAssetId = mimicAsset.Id;
        }
        else if (animation.FacialSourceAssetId is
        { } facialSourceAssetId)
        {
            ProjectAssetReference facialSourceAsset =
                _project.Assets.FirstOrDefault(asset =>
                    asset.Id == facialSourceAssetId &&
                    asset.Kind == ProjectAssetKind.SourceAnimation)
                ?? throw new ProjectFormatException(
                    "The active animation facial FBX asset is missing or is not a source animation.");
            if (facialSourceAsset.ContentSha256 is not
                { } expectedFacialSourceHash)
            {
                throw new ProjectFormatException(
                    "A saved facial FBX asset requires an exact SHA-256 fingerprint.");
            }

            string facialSourcePath = ResolveProjectAssetPath(
                projectDirectory,
                facialSourceAsset,
                "facial FBX");
            if (!string.Equals(
                    Path.GetExtension(facialSourcePath),
                    ".fbx",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The saved facial source project asset is not an FBX file.");
            }

            string actualFacialSourceHash =
                await ProjectSourceImporter.ComputeSha256Async(
                    facialSourcePath,
                    CancellationToken.None);
            if (!string.Equals(
                    actualFacialSourceHash,
                    expectedFacialSourceHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Facial FBX source hash mismatch for '{facialSourceAsset.RelativePath}'.");
            }

            _pendingFacialFbxSourcePath = facialSourcePath;
            _pendingFacialFbxAssetId = facialSourceAsset.Id;
        }

        string extension = Path.GetExtension(sourcePath)
            .ToLowerInvariant();
        if (extension == ".fbx")
        {
            FbxCoreAnimationImportResult imported =
                await new FbxAnimationDecoder().DecodeFileAsync(
                    sourcePath);
            ReportFbxAnimationDomainImport(imported);
            _pendingAnm2SourcePath = null;
            _sourceAnimation = new ImportedAnimationSession(
                imported.Rig,
                imported.Clip,
                sourcePath,
                "FBX")
            {
                SourceKindContract = AnimationSourceKind.LocalFbx,
                TimingProvenance =
                    AnimationTimingProvenance.EmbeddedFbx,
            };
            _sourceBaseMeshes = [];
            _synchronizedAnimation = imported.Clip;
            SourceViewport.SceneSource.SetScene(
                [],
                CorePreviewAdapter.ToRenderSkeleton(
                    imported.Rig.CreateBindPose()),
                []);
            RefreshProjectBindings();
            AutoMapCommand.NotifyCanExecuteChanged();
            return;
        }

        throw new InvalidDataException(
            "The saved local animation source is not a binary FBX file.");
    }

    private static string ResolveProjectAssetPath(
        string projectDirectory,
        ProjectAssetReference asset,
        string description)
    {
        string sourcePath = Path.GetFullPath(
            Path.Combine(
                projectDirectory,
                asset.RelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
        string requiredPrefix = projectDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!sourcePath.StartsWith(
                requiredPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"The project-relative {description} is missing or escaped the project directory.",
                sourcePath);
        }

        return sourcePath;
    }

    private string ResolveLocalProjectAssetPath(
        ProjectAssetReference asset)
    {
        string projectPath = ProjectPath
            ?? throw new InvalidOperationException(
                "Save the project before resolving local animation sources.");
        string projectDirectory = Path.GetDirectoryName(
                Path.GetFullPath(projectPath))
            ?? throw new InvalidOperationException(
                "The project path has no parent directory.");
        return ResolveProjectAssetPath(
            projectDirectory,
            asset,
            "animation source");
    }

    private async Task<(Dl1MeshPreviewPayload Payload, RetailAssetRecord Asset)>
        DecodeProjectModelAsync(
            ProjectAssetReference modelAsset,
            CancellationToken cancellationToken)
    {
        AssetItemViewModel? row = FindRetailCatalogAsset(
            modelAsset,
            _indexedAssetItems);
        if (row is not
            {
                Kind: AssetKind.Mesh,
                RetailAsset: { } retail,
            })
        {
            throw new InvalidOperationException(
                "The immutable retail source model is not available in the indexed DL1 installation.");
        }

        Dl1MeshPreviewPayload payload =
            await _retailMeshDecodeService.DecodeAsync(
                retail,
                cancellationToken);
        if (!string.Equals(
                payload.ResourceSha256,
                modelAsset.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Retail model '{row.Name}' changed since this animation was bound.");
        }

        return (payload, retail);
    }

    private async Task<(Anm2Clip Clip,
        AnimationTimingProvenance Provenance,
        double? StartFrame,
        double? EndFrame,
        string? Detail)> DecodeProjectAnm2Async(
            ProjectAssetReference sourceAsset,
            ProjectAnimation animation,
            CancellationToken cancellationToken)
    {
        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new InvalidDataException(
                "The animation has no immutable source binding.");
        if (binding.Kind == AnimationSourceKind.RetailAnm2)
        {
            AssetItemViewModel? row = FindRetailCatalogAsset(
                sourceAsset,
                _indexedAssetItems);
            if (row is not
                {
                    Kind: AssetKind.Animation,
                    RetailAsset: { } retail,
                })
            {
                throw new InvalidOperationException(
                    "The saved retail ANM2 is not available in the indexed DL1 installation.");
            }

            Dl1RetailAnimationTiming? savedTiming =
                binding.TimingProvenance ==
                    AnimationTimingProvenance.ExactRetailAnimationScript &&
                binding.SourceRangeStartFrame is { } savedStart &&
                binding.SourceRangeEndFrame is { } savedEnd
                    ? new Dl1RetailAnimationTiming(
                        animation.FrameRate,
                        checked((float)savedStart),
                        checked((float)savedEnd),
                        binding.TimingProvenance,
                        binding.TimingDetail ??
                            "Saved exact AnimationScr selection")
                    : null;
            Dl1RetailAnimationPayload payload =
                await _assetWorkspace.DecodeAnimationAsync(
                    retail,
                    savedTiming,
                    cancellationToken);
            if (!string.Equals(
                    payload.ResourceSha256,
                    sourceAsset.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Retail animation '{row.Name}' changed since it was added to the project.");
            }

            return (
                payload.Clip,
                binding.TimingProvenance,
                binding.SourceRangeStartFrame ??
                    payload.Timing.StartFrame,
                binding.SourceRangeEndFrame ??
                    payload.Timing.EndFrame,
                binding.TimingDetail ?? payload.Timing.Detail);
        }

        if (binding.Kind != AnimationSourceKind.LocalAnm2)
        {
            throw new InvalidOperationException(
                "The selected source is not an ANM2 animation.");
        }

        string path = ResolveLocalProjectAssetPath(sourceAsset);
        string actualHash =
            await ProjectSourceImporter.ComputeSha256Async(
                path,
                cancellationToken);
        if (!string.Equals(
                actualHash,
                sourceAsset.ContentSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The local ANM2 differs from its saved project fingerprint.");
        }

        Anm2Clip clip = await new Anm2Decoder().DecodeFileAsync(
            path,
            cancellationToken: cancellationToken);
        return (
            clip,
            binding.TimingProvenance,
            binding.SourceRangeStartFrame,
            binding.SourceRangeEndFrame,
            binding.TimingDetail);
    }

    private async Task<AnimationClip> LoadFacialClipAsync(
        ProjectAnimation animation,
        CancellationToken cancellationToken)
    {
        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new InvalidDataException(
                "The facial animation has no immutable source binding.");
        if (binding.Kind == AnimationSourceKind.LocalFbx)
        {
            throw new InvalidOperationException(
                "Attach-as-facial currently requires a partitioned DL1 ANM2 source. Use Import facial FBX for reviewed FBX morph curves.");
        }

        ProjectAssetReference sourceAsset = FindProjectAsset(
                animation.SourceAssetId)
            ?? throw new InvalidDataException(
                "The facial animation source asset is missing.");
        ProjectAssetReference modelAsset = binding.RetailSourceModelAssetId is
                { } modelId
            ? FindProjectAsset(modelId)
                ?? throw new InvalidDataException(
                    "The facial animation source model is missing.")
            : throw new InvalidDataException(
                "The facial animation has no exact source model.");
        (Dl1MeshPreviewPayload model, _) =
            await DecodeProjectModelAsync(
                modelAsset,
                cancellationToken);
        (Anm2Clip raw, _, _, _, _) =
            await DecodeProjectAnm2Async(
                sourceAsset,
                animation,
                cancellationToken);
        Anm2PartitionedImportResult partitioned =
            Anm2TrackPartitioner.Partition(
                raw,
                model.Source.Rig ??
                    throw new InvalidDataException(
                        "The exact facial source model has no skeleton."),
                animation.FrameRate,
                cancellationToken);
        if (partitioned.Partition.RequiresReview ||
            !string.Equals(
                partitioned.Partition.Fingerprint,
                binding.Partition?.Fingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The facial ANM2 partition no longer matches its immutable source binding.");
        }

        return partitioned.FacialClip;
    }

    private void ReportFbxAnimationDomainImport(
        FbxCoreAnimationImportResult imported)
    {
        foreach (FbxAnimationImportNotice notice in
                 imported.DomainNotices)
        {
            AddDiagnostic(
                "Information",
                "FBX",
                notice.Summary,
                notice.Detail);
        }

        if (imported.AnimationStackActivities.Length <= 1)
        {
            return;
        }

        FbxAnimationStackActivity selected = imported
            .AnimationStackActivities
            .Single(activity =>
                activity.Stack.ObjectId ==
                imported.AnimationStack.ObjectId);
        AddDiagnostic(
            "Information",
            "FBX",
            $"Automatically selected animation stack '{selected.Stack.Name}'",
            $"It is the only unambiguous skeletal take: {selected.SkeletalBindingCount:N0} limb channels, {selected.ChangingSkeletalBindingCount:N0} changing. Other authored takes remain available only through explicit stack selection.");
    }

    private async Task SaveWorkspaceAsync()
    {
        string? path = ProjectPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = _fileDialogs.ShowSaveProjectDialog(
                _project.Name,
                RecentProjectPaths.FirstOrDefault());
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        StatusText = $"Saving {Path.GetFileName(path)}…";
        try
        {
            DlraProject projectToSave =
                CreateProjectWithCurrentPreviewConfiguration();
            projectToSave.Validate();
            string savedPath = await Task.Run(
                () => ProjectSerializer.SaveAtomic(projectToSave, path));
            ProjectPath = savedPath;
            _project = projectToSave;
            _savedProject = projectToSave;
            UpdateDirtyState();
            AddRecentProjectPath(savedPath);
            StatusText = $"Saved {Path.GetFileName(savedPath)}";
        }
        catch (Exception exception) when (
            exception is ProjectFormatException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Project",
                "Project could not be saved",
                exception.Message);
            StatusText = "Project save failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAnimationAsync()
    {
        if (string.IsNullOrWhiteSpace(ProjectPath))
        {
            await SaveWorkspaceAsync();
            if (string.IsNullOrWhiteSpace(ProjectPath))
            {
                StatusText = "Save the project before importing animation sources";
                return;
            }
        }

        string? selectedPath = _fileDialogs.ShowOpenAnimationDialog(ProjectPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Import {Path.GetFileName(selectedPath)}",
            "Decode",
            "Validating animation source");
        try
        {
            ImportedAnimationSession session;
            string extension = Path.GetExtension(selectedPath)
                .ToLowerInvariant();
            if (extension == ".fbx")
            {
                var decoder = new FbxAnimationDecoder();
                FbxCoreAnimationImportResult imported =
                    await decoder.DecodeFileAsync(
                        selectedPath,
                        cancellationToken: job.CancellationToken);
                ReportFbxAnimationDomainImport(imported);
                session = new ImportedAnimationSession(
                    imported.Rig,
                    imported.Clip,
                    selectedPath,
                    "FBX")
                {
                    SourceKindContract = AnimationSourceKind.LocalFbx,
                    TimingProvenance =
                        AnimationTimingProvenance.EmbeddedFbx,
                };
            }
            else if (extension == ".anm2")
            {
                RigDefinition rig = _sourceModelContext?.Payload.Source.Rig
                    ?? throw new InvalidOperationException(
                        "Use a matching retail DL1 model as Source before importing an ANM2 file.");
                var decoder = new Anm2Decoder();
                Anm2Clip source = await decoder.DecodeFileAsync(
                    selectedPath,
                    cancellationToken: job.CancellationToken);
                Anm2PartitionedImportResult imported =
                    Anm2TrackPartitioner.Partition(
                        source,
                        rig,
                        new FrameRate(30, 1),
                        job.CancellationToken);
                if (imported.Partition.RequiresReview)
                {
                    throw new InvalidDataException(
                        "ANM2 contains duplicated or bone/morph-colliding descriptors that require review before playback: " +
                        string.Join(", ", imported.Partition.AmbiguousDescriptors.Select(
                            static descriptor => $"0x{descriptor:X8}")) +
                        ".");
                }

                Guid sourceModelAssetId = _sourceModelContext?.ProjectAsset.Id
                    ?? throw new InvalidOperationException(
                        "The explicit retail source model has no physical identity.");
                session = new ImportedAnimationSession(
                    rig,
                    imported.CombinedClip,
                    selectedPath,
                    "DL1 ANM2")
                {
                    SourceKindContract = AnimationSourceKind.LocalAnm2,
                    RetailSourceModelAssetId = sourceModelAssetId,
                    Partition = imported.Partition,
                    TimingProvenance =
                        AnimationTimingProvenance.Manual30FpsFallback,
                    FacialClip = imported.FacialClip,
                };
                _sourceBaseMeshes =
                    _sourceModelContext?.PreviewMeshes ?? [];
                if (imported.Partition.UnresolvedDescriptors.Length > 0)
                {
                    AddDiagnostic(
                        "Warning",
                        "ANM2",
                        $"{imported.Partition.UnresolvedDescriptors.Length:N0} descriptors do not exist in the bound retail source rig",
                        string.Join(
                            ", ",
                            imported.Partition.UnresolvedDescriptors
                                .Take(12)
                                .Select(static value => $"0x{value:X8}")));
                }

                AddDiagnostic(
                    "Warning",
                    "ANM2",
                    "ANM2 has no embedded playback cadence",
                    "The import uses 30/1 fps until an animation.scr cadence is selected.");
            }
            else
            {
                throw new InvalidDataException(
                    "Only binary FBX and Dying Light 1 ANM2 animation sources are supported.");
            }

            job.Stage = "Project source";
            job.Progress = 55.0;
            ImportedProjectSource projectSource =
                await ProjectSourceImporter.ImportAsync(
                    selectedPath,
                    ProjectPath,
                    job.CancellationToken);
            ProjectAssetReference asset = new()
            {
                Kind = ProjectAssetKind.SourceAnimation,
                RelativePath = projectSource.ProjectRelativePath,
                ContentSha256 = projectSource.Sha256,
            };

            RigDefinition? initialTargetRig =
                session.SourceKindContract == AnimationSourceKind.LocalAnm2
                    ? _sourceModelContext?.Payload.Source.Rig
                    : _targetRig;
            ProjectAssetReference? initialTargetAsset =
                session.SourceKindContract == AnimationSourceKind.LocalAnm2
                    ? _sourceModelContext?.ProjectAsset
                    : _targetProjectAsset;
            RetargetMap? proposal = initialTargetRig is null ||
                HasSameRigContract(session.Rig, initialTargetRig)
                ? null
                : RetargetMapBuilder.CreateSuggested(
                    session.Rig,
                    initialTargetRig);
            ProjectAnimation animation = CreateProjectAnimation(
                session,
                asset,
                initialTargetRig,
                initialTargetAsset?.Id,
                initialTargetAsset?.ContentSha256,
                proposal);
            _sourceAnimation = session with
            {
                SourcePath = projectSource.AbsolutePath,
            };
            if (session.SourceKindContract == AnimationSourceKind.LocalFbx)
            {
                _sourceBaseMeshes = [];
            }
            _mimicAnimation = null;
            _facialFbxAnimation = null;
            _synchronizedAnimation = session.Clip;
            _pendingAnm2SourcePath = null;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;
            _activeRetargetMap = proposal;
            _activeAnimationId = animation.Id;
            ImmutableArray<ProjectAssetReference> importedAssets =
                _project.Assets.Add(asset);
            if (initialTargetAsset is not null &&
                !importedAssets.Any(candidate =>
                    candidate.Id == initialTargetAsset.Id))
            {
                importedAssets = importedAssets.Add(
                    initialTargetAsset);
            }
            CommitProject(_project with
            {
                Assets = importedAssets,
                Animations = _project.Animations.Add(animation),
            });

            if (session.SourceKindContract == AnimationSourceKind.LocalAnm2 &&
                _sourceModelContext is { } localAnm2Model)
            {
                PublishDecodedMesh(
                    localAnm2Model.Payload,
                    localAnm2Model.RetailAsset,
                    localAnm2Model.ProjectAsset,
                    restoreRetargetMap: false);
                _activeRetargetMap = null;
                SetTargetBindingStatus(TargetBindingStatus.Direct);
            }

            Timeline.CurrentFrame = 0;
            _editorSessionCoordinator.Reset(
                animation.Id,
                frame: 0);
            SetTargetBindingStatus(
                initialTargetRig is null
                    ? TargetBindingStatus.Invalid
                    : ResolveTargetBindingStatus(
                        session.Rig,
                        initialTargetRig,
                        proposal));
            RefreshAnimationPreview();
            if (proposal is not null)
            {
                PublishMappingProposal(proposal);
            }

            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Imported {Path.GetFileName(selectedPath)} ({session.Clip.FrameCount:N0} frames at {session.Clip.FrameRate.Numerator}/{session.Clip.FrameRate.Denominator} fps)";
            AddDiagnostic(
                "Info",
                "Animation",
                $"{session.SourceKind} animation imported",
                $"Source rig {session.Rig.BoneCount:N0} bones; SHA-256 {projectSource.Sha256}.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Animation import canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Animation",
                "Animation import failed",
                exception.Message);
            StatusText = "Animation import failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPlaySelectedExplorerAnimation() =>
        !IsBusy &&
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Animation,
            RetailAsset: not null,
        };

    private Task PlaySelectedExplorerAnimationAsync()
    {
        if (AssetBrowser.SelectedAsset is not
            {
                Kind: AssetKind.Animation,
                RetailAsset: not null,
            } selected)
        {
            return Task.CompletedTask;
        }

        return PlayExplorerAnimationAsync(
            selected,
            selectedTiming: null);
    }

    private async Task PlayExplorerAnimationAsync(
        AssetItemViewModel selected,
        Dl1RetailAnimationTiming? selectedTiming)
    {
        if (selected is not
            {
                Kind: AssetKind.Animation,
                RetailAsset: { } retailAnimation,
            })
        {
            return;
        }

        if (selectedTiming is null)
        {
            ClearExplorerAnimationTimingPicker();
        }

        if (_sourceModelContext?.Payload.Source.Rig is not
                { } sourceRig ||
            _sourceModelContext.ProjectAsset is not
                { } sourceModelAsset)
        {
            BeginExplorerSourceModelPicker(selected);
            return;
        }

        ProjectAnimation? reusable = FindReusableRetailAnimation(
            _project,
            retailAnimation,
            sourceModelAsset);
        if (reusable is not null)
        {
            if (reusable.Id != _activeAnimationId ||
                _sourceAnimation is null)
            {
                await ActivateAnimationAsync(
                    reusable.Id,
                    beginPlayback: true);
            }
            else
            {
                Timeline.CurrentFrame = 0;
                Timeline.IsPlaying = true;
                RefreshAnimationPreview();
            }
            StatusText =
                $"Playing {selected.Name} (reused project clip)";
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Play {selected.Name}",
            "Retail ANM2",
            "Decoding selected animation once");
        try
        {
            job.Progress = 25.0;
            Dl1RetailAnimationPayload payload =
                await _assetWorkspace.DecodeAnimationAsync(
                    retailAnimation,
                    selectedTiming,
                    job.CancellationToken);
            job.Stage = "Source partition";
            job.Progress = 55.0;
            Anm2PartitionedImportResult partitioned =
                Anm2TrackPartitioner.Partition(
                    payload.Clip,
                    sourceRig,
                    payload.Timing.FrameRate,
                    job.CancellationToken);
            if (partitioned.Partition.RequiresReview)
            {
                throw new InvalidDataException(
                    "The retail ANM2 has duplicated or bone/morph-colliding descriptors that require review: " +
                    string.Join(
                        ", ",
                        partitioned.Partition.AmbiguousDescriptors.Select(
                            static descriptor => $"0x{descriptor:X8}")));
            }

            ProjectAssetReference candidate =
                CreateRetailProjectAsset(
                    retailAnimation,
                    payload.ResourceSha256);
            ProjectAssetReference sourceAsset =
                FindMatchingProjectRetailAsset(candidate) ?? candidate;
            ProjectAnimation? existing = _project.Animations
                .FirstOrDefault(animation =>
                    animation.SourceBinding is
                    {
                        Kind: AnimationSourceKind.RetailAnm2,
                        RetailSourceModelAssetId: { } modelId,
                    } binding &&
                    ProjectRetailAssetsMatch(
                        FindProjectAsset(binding.AssetId),
                        sourceAsset) &&
                    ProjectRetailAssetsMatch(
                        FindProjectAsset(modelId),
                        sourceModelAsset));
            var session = new ImportedAnimationSession(
                sourceRig,
                partitioned.CombinedClip,
                $"retail://{retailAnimation.Id.StableKey}",
                "Retail DL1 ANM2")
            {
                SourceKindContract = AnimationSourceKind.RetailAnm2,
                RetailSourceModelAssetId = sourceModelAsset.Id,
                Partition = partitioned.Partition,
                TimingProvenance = payload.Timing.Provenance,
                SourceRangeStartFrame = payload.Timing.StartFrame,
                SourceRangeEndFrame = payload.Timing.EndFrame,
                TimingDetail = payload.Timing.Detail,
                FacialClip = partitioned.FacialClip,
            };

            ProjectAnimation animation;
            ImmutableArray<ProjectAssetReference> assets = _project.Assets;
            ImmutableArray<ProjectAnimation> animations = _project.Animations;
            if (existing is null)
            {
                if (sourceAsset.Id == candidate.Id)
                {
                    assets = assets.Add(sourceAsset);
                }

                if (!assets.Any(asset =>
                        asset.Id == sourceModelAsset.Id))
                {
                    assets = assets.Add(sourceModelAsset);
                }

                animation = CreateProjectAnimation(
                    session,
                    sourceAsset,
                    sourceRig,
                    sourceModelAsset.Id,
                    sourceModelAsset.ContentSha256,
                    proposal: null);
                animations = animations.Add(animation);
            }
            else
            {
                animation = existing;
            }

            _sourceAnimation = session;
            _sourceBaseMeshes = _sourceModelContext.PreviewMeshes;
            _mimicAnimation = null;
            _facialFbxAnimation = null;
            _synchronizedAnimation = partitioned.CombinedClip;
            _activeRetargetMap = null;
            _activeAnimationId = animation.Id;
            CommitProject(_project with
            {
                Assets = assets,
                Animations = animations,
                ActiveAnimationId = animation.Id,
            });
            PublishDecodedMesh(
                _sourceModelContext.Payload,
                _sourceModelContext.RetailAsset,
                sourceModelAsset,
                restoreRetargetMap: false);
            _activeRetargetMap = null;
            SetTargetBindingStatus(TargetBindingStatus.Direct);
            _editorSessionCoordinator.Reset(
                animation.Id,
                frame: 0);
            Timeline.CurrentFrame = 0;
            Timeline.IsPlaying = true;
            RefreshAnimationPreview();
            job.Progress = 100.0;
            job.Complete(existing is null ? "Added and playing" : "Reused and playing");
            string cadenceBadge = payload.Timing.Provenance ==
                AnimationTimingProvenance.Manual30FpsFallback
                    ? "manual 30 FPS"
                    : "exact AnimationScr timing";
            StatusText =
                $"Playing {selected.Name} ({cadenceBadge})";
            AddDiagnostic(
                payload.Timing.Provenance ==
                    AnimationTimingProvenance.Manual30FpsFallback
                        ? "Warning"
                        : "Info",
                "Animation explorer",
                existing is null
                    ? "Retail clip added once to the project animation library"
                    : "Reused the existing retail clip/source-model document",
                $"{payload.Timing.Detail} Source model fingerprint {sourceModelAsset.ContentSha256}; partition {partitioned.Partition.Fingerprint}.");
        }
        catch (Dl1AnimationTimingConflictException conflict)
        {
            job.Complete("Timing selection required");
            BeginExplorerAnimationTimingPicker(
                selected,
                conflict.Choices);
            AddDiagnostic(
                "Warning",
                "Animation explorer",
                $"Choose timing for {selected.Name}",
                conflict.Message);
            StatusText = "Animation timing selection required";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Retail animation playback canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Animation explorer",
                $"Could not play {selected.Name}",
                exception.Message);
            StatusText = "Retail animation playback failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void BeginExplorerAnimationTimingPicker(
        AssetItemViewModel animation,
        IReadOnlyList<Dl1RetailAnimationTiming> choices)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count < 2)
        {
            throw new ArgumentException(
                "A timing picker requires at least two exact choices.",
                nameof(choices));
        }

        _pendingExplorerAnimationTimingChoice = animation;
        ExplorerAnimationTimingChoices.Clear();
        foreach (Dl1RetailAnimationTiming choice in choices)
        {
            ExplorerAnimationTimingChoices.Add(choice);
        }
        SelectedExplorerAnimationTiming =
            ExplorerAnimationTimingChoices[0];
        OnPropertyChanged(
            nameof(IsExplorerAnimationTimingPickerActive));
        OnPropertyChanged(nameof(ExplorerAnimationTimingPrompt));
        ConfirmExplorerAnimationTimingCommand.NotifyCanExecuteChanged();
        CancelExplorerAnimationTimingCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmExplorerAnimationTiming() =>
        !IsBusy &&
        _pendingExplorerAnimationTimingChoice is not null &&
        SelectedExplorerAnimationTiming is not null;

    private async Task ConfirmExplorerAnimationTimingAsync()
    {
        if (_pendingExplorerAnimationTimingChoice is not { } animation ||
            SelectedExplorerAnimationTiming is not { } timing)
        {
            return;
        }

        ClearExplorerAnimationTimingPicker();
        AssetBrowser.SelectedAsset = animation;
        await PlayExplorerAnimationAsync(animation, timing);
    }

    private void CancelExplorerAnimationTiming()
    {
        ClearExplorerAnimationTimingPicker();
        StatusText = "Animation timing selection canceled";
    }

    private void ClearExplorerAnimationTimingPicker()
    {
        _pendingExplorerAnimationTimingChoice = null;
        ExplorerAnimationTimingChoices.Clear();
        SelectedExplorerAnimationTiming = null;
        OnPropertyChanged(
            nameof(IsExplorerAnimationTimingPickerActive));
        OnPropertyChanged(nameof(ExplorerAnimationTimingPrompt));
        ConfirmExplorerAnimationTimingCommand.NotifyCanExecuteChanged();
        CancelExplorerAnimationTimingCommand.NotifyCanExecuteChanged();
    }

    private void BeginExplorerSourceModelPicker(
        AssetItemViewModel animation)
    {
        _pendingExplorerAnimationSourceChoice = animation;
        OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
        OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
        CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();
        AssetBrowser.SearchText = string.Empty;
        AssetBrowser.SelectedKindFilter = nameof(AssetKind.Mesh);
        AddDiagnostic(
            "Info",
            "Animation explorer",
            $"Choose the exact source model for {animation.Name}",
            "The explorer is now filtered to retail meshes. Single-click remains metadata-only; selecting a mesh decodes it and, if it has a skeleton, immediately plays the pending animation against that immutable model fingerprint.");
        StatusText = $"Choose the source model for {animation.Name}";
    }

    private void CancelExplorerSourceModelPicker()
    {
        _pendingExplorerAnimationSourceChoice = null;
        OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
        OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
        CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();
        StatusText = "Source-model selection canceled";
    }

    private bool CanUseSelectedAnimationLibraryItem() =>
        !IsBusy && SelectedAnimationLibraryItem is not null;

    private bool CanActivateSelectedAnimation() =>
        CanUseSelectedAnimationLibraryItem() &&
        SelectedAnimationLibraryItem?.Id != _activeAnimationId;

    private async Task ActivateSelectedAnimationAsync()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        await ActivateAnimationAsync(
            selected.Id,
            beginPlayback: false);
    }

    private async Task ActivateAnimationAsync(
        Guid animationId,
        bool beginPlayback,
        bool persistActivation = true)
    {
        ProjectAnimation animation = _project.Animations.FirstOrDefault(
                candidate => candidate.Id == animationId)
            ?? throw new ArgumentException(
                "The requested animation is not in the project library.",
                nameof(animationId));
        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new InvalidDataException(
                "This animation has no provable source model. Use Rebind Source to create a clean document.");
        ProjectAssetReference sourceAsset = FindProjectAsset(
                animation.SourceAssetId)
            ?? throw new InvalidDataException(
                "The active animation source asset is missing.");

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Activate {animation.Name}",
            "Animation library",
            "Resolving immutable source binding");
        try
        {
            _activeAnimationId = animation.Id;
            if (persistActivation)
            {
                CommitProject(_project with
                {
                    ActiveAnimationId = animation.Id,
                });
            }
            else
            {
                RefreshAnimationLibrary();
            }
            _mimicAnimation = null;
            _facialFbxAnimation = null;
            _synchronizedAnimation = null;
            _activeRetargetMap = null;
            _pendingAnm2SourcePath = null;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;

            Dl1MeshPreviewPayload? sourceModelPayload = null;
            RetailAssetRecord? sourceModelRetail = null;
            ProjectAssetReference? sourceModelProjectAsset = null;
            ImportedAnimationSession session;
            if (binding.Kind == AnimationSourceKind.LocalFbx)
            {
                string sourcePath = ResolveLocalProjectAssetPath(
                    sourceAsset);
                string actualHash =
                    await ProjectSourceImporter.ComputeSha256Async(
                        sourcePath,
                        job.CancellationToken);
                if (!string.Equals(
                        actualHash,
                        sourceAsset.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The local FBX differs from its saved project fingerprint.");
                }

                FbxCoreAnimationImportResult imported =
                    await new FbxAnimationDecoder().DecodeFileAsync(
                        sourcePath,
                        cancellationToken: job.CancellationToken);
                if (!string.Equals(
                        RigSignature.Compute(imported.Rig),
                        binding.SourceRigSignature,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The local FBX rig differs from its immutable saved source signature.");
                }

                session = new ImportedAnimationSession(
                    imported.Rig,
                    imported.Clip,
                    sourcePath,
                    "FBX")
                {
                    SourceKindContract = AnimationSourceKind.LocalFbx,
                    TimingProvenance = binding.TimingProvenance,
                    TimingDetail = binding.TimingDetail,
                };
                _sourceBaseMeshes = [];
                _sourceModelContext = null;
                OnPropertyChanged(nameof(ActiveSourceModelLabel));
            }
            else
            {
                ProjectAssetReference sourceModelAsset =
                    binding.RetailSourceModelAssetId is { } modelId
                        ? FindProjectAsset(modelId)
                            ?? throw new InvalidDataException(
                                "The immutable ANM2 source-model asset is missing.")
                        : throw new InvalidDataException(
                            "The ANM2 source binding does not identify an exact retail model.");
                sourceModelProjectAsset = sourceModelAsset;
                (sourceModelPayload, sourceModelRetail) =
                    await DecodeProjectModelAsync(
                        sourceModelAsset,
                        job.CancellationToken);
                (Anm2Clip raw, AnimationTimingProvenance provenance,
                    double? start, double? end, string? detail) =
                    await DecodeProjectAnm2Async(
                        sourceAsset,
                        animation,
                        job.CancellationToken);
                Anm2PartitionedImportResult partitioned =
                    Anm2TrackPartitioner.Partition(
                        raw,
                        sourceModelPayload.Source.Rig ??
                            throw new InvalidDataException(
                                "The immutable ANM2 source model has no skeleton."),
                        animation.FrameRate,
                        job.CancellationToken);
                if (partitioned.Partition.RequiresReview ||
                    !string.Equals(
                        partitioned.Partition.Fingerprint,
                        binding.Partition?.Fingerprint,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The ANM2 descriptor partition differs from its immutable saved source binding.");
                }

                if (partitioned.CombinedClip.FrameCount !=
                    animation.FrameCount)
                {
                    throw new InvalidDataException(
                        "The ANM2 frame count differs from the saved animation document.");
                }

                session = new ImportedAnimationSession(
                    sourceModelPayload.Source.Rig,
                    partitioned.CombinedClip,
                    binding.Kind == AnimationSourceKind.RetailAnm2
                        ? $"retail://{sourceAsset.ResourceId}"
                        : ResolveLocalProjectAssetPath(sourceAsset),
                    binding.Kind == AnimationSourceKind.RetailAnm2
                        ? "Retail DL1 ANM2"
                        : "DL1 ANM2")
                {
                    SourceKindContract = binding.Kind,
                    RetailSourceModelAssetId =
                        binding.RetailSourceModelAssetId,
                    Partition = partitioned.Partition,
                    TimingProvenance = provenance,
                    SourceRangeStartFrame = start,
                    SourceRangeEndFrame = end,
                    TimingDetail = detail,
                    FacialClip = partitioned.FacialClip,
                };
                _sourceBaseMeshes = CreatePreviewMeshes(
                    sourceModelPayload);
                _sourceModelContext = new DecodedRetailModelSession(
                    sourceModelPayload,
                    sourceModelRetail!,
                    sourceModelAsset,
                    _sourceBaseMeshes);
                OnPropertyChanged(nameof(ActiveSourceModelLabel));
            }

            _sourceAnimation = session;
            _synchronizedAnimation = session.Clip;
            job.Stage = "Target model";
            job.Progress = 65.0;
            if (animation.TargetAssetId is { } targetAssetId)
            {
                ProjectAssetReference targetAsset = FindProjectAsset(
                        targetAssetId)
                    ?? throw new InvalidDataException(
                        "The animation target asset is missing.");
                if (sourceModelPayload is not null &&
                    ProjectRetailAssetsMatch(
                        FindProjectAsset(
                            binding.RetailSourceModelAssetId!.Value),
                        targetAsset))
                {
                    PublishDecodedMesh(
                        sourceModelPayload,
                        sourceModelRetail!,
                        sourceModelProjectAsset,
                        restoreRetargetMap: false);
                }
                else
                {
                    (Dl1MeshPreviewPayload targetPayload,
                        RetailAssetRecord targetRetail) =
                        await DecodeProjectModelAsync(
                            targetAsset,
                            job.CancellationToken);
                    PublishDecodedMesh(
                        targetPayload,
                        targetRetail,
                        targetAsset,
                        restoreRetargetMap: false);
                }
            }
            else
            {
                _targetRig = null;
                _targetProjectAsset = null;
                _targetBaseMeshes = [];
                TargetViewport.SceneSource.SetScene([], null, []);
            }

            if (animation.MimicAssetId is { } mimicAssetId)
            {
                AnimationClip facialClip;
                FacialClipTiming timing;
                if (animation.FacialAnimationSourceBinding is
                    { } facialBinding)
                {
                    var facialDocument = animation with
                    {
                        SourceAssetId = mimicAssetId,
                        SourceBinding = facialBinding,
                        FrameRate = animation.FacialTiming?.NativeFrameRate ??
                            animation.FrameRate,
                    };
                    facialClip = await LoadFacialClipAsync(
                        facialDocument,
                        job.CancellationToken);
                    timing = animation.FacialTiming ??
                        FacialClipTiming.ForClip(facialClip);
                }
                else
                {
                    if (_targetRig is null)
                    {
                        throw new InvalidOperationException(
                            "A separate mimic source requires its exact decoded target model.");
                    }

                    ProjectAssetReference mimicAsset = FindProjectAsset(
                            mimicAssetId)
                        ?? throw new InvalidDataException(
                            "The separate mimic asset is missing.");
                    string mimicPath = ResolveLocalProjectAssetPath(
                        mimicAsset);
                    SynchronizedMimicAnimation loaded =
                        await SynchronizedMimicAnm2Loader.LoadAsync(
                            mimicPath,
                            mimicAsset.ContentSha256 ??
                                throw new InvalidDataException(
                                    "The mimic asset has no fingerprint."),
                            _targetRig,
                            session.Clip,
                            animation.FrameRate,
                            animation.FrameCount,
                            job.CancellationToken);
                    facialClip = loaded.Mimic;
                    timing = animation.FacialTiming ?? loaded.Timing;
                }

                _mimicAnimation = new ImportedMimicSession(
                    mimicAssetId,
                    facialClip,
                    FormatProjectAssetLabel(
                        FindProjectAsset(mimicAssetId)));
                _synchronizedAnimation =
                    AnimationClipSynchronization.Synchronize(
                        session.Clip,
                        facialClip,
                        timing);
            }

            RestoreOrCreateRetargetMap();
            TargetBindingStatus bindingStatus = _targetRig is null
                ? TargetBindingStatus.Invalid
                : ResolveTargetBindingStatus(
                    session.Rig,
                    _targetRig,
                    _activeRetargetMap);
            SetTargetBindingStatus(bindingStatus);
            _editorSessionCoordinator.Reset(
                animation.Id,
                frame: 0);
            SetWorkspace(
                bindingStatus == TargetBindingStatus.NeedsReview
                    ? EditorWorkspaceMode.RetargetEdit
                    : EditorWorkspaceMode.Animate,
                preserveLegacyCutscene: false);
            Timeline.CurrentFrame = 0;
            Timeline.IsPlaying = beginPlayback &&
                bindingStatus is TargetBindingStatus.Direct or
                    TargetBindingStatus.Ready;
            RefreshAnimationPreview();
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText = $"Activated {animation.Name}";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            job.Complete("Failed");
            _sourceAnimation = null;
            _sourceBaseMeshes = [];
            _synchronizedAnimation = null;
            _activeRetargetMap = null;
            AddDiagnostic(
                "Error",
                "Animation library",
                $"Could not activate {animation.Name}",
                exception.Message);
            StatusText = "Animation activation failed";
            RefreshAnimationPreview();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRenameSelectedAnimation() =>
        CanUseSelectedAnimationLibraryItem() &&
        !string.IsNullOrWhiteSpace(
            SelectedAnimationLibraryItem?.Name);

    private void RenameSelectedAnimation()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        int index = -1;
        for (int candidateIndex = 0;
             candidateIndex < _project.Animations.Length;
             candidateIndex++)
        {
            if (_project.Animations[candidateIndex].Id == selected.Id)
            {
                index = candidateIndex;
                break;
            }
        }
        if (index < 0 || string.Equals(
                _project.Animations[index].Name,
                selected.Name,
                StringComparison.Ordinal))
        {
            return;
        }

        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                index,
                _project.Animations[index] with
                {
                    Name = selected.Name.Trim(),
                }),
        });
        StatusText = $"Renamed animation to {selected.Name.Trim()}";
    }

    private void DuplicateSelectedAnimation()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        ProjectAnimation? source = _project.Animations.FirstOrDefault(
            animation => animation.Id == selected.Id);
        if (source is null)
        {
            return;
        }

        ProjectAnimation duplicate = source with
        {
            Id = Guid.NewGuid(),
            Name = source.Name + " Copy",
            VariantGroupId = Guid.NewGuid(),
        };
        _activeAnimationId = duplicate.Id;
        CommitProject(_project with
        {
            Animations = _project.Animations.Add(duplicate),
            ActiveAnimationId = duplicate.Id,
        });
        StatusText = $"Duplicated {source.Name}";
    }

    private bool CanRebindSelectedAnimationSource() =>
        CanUseSelectedAnimationLibraryItem() &&
        _targetRig is not null &&
        _targetProjectAsset is not null &&
        _project.Animations.FirstOrDefault(animation =>
            animation.Id == SelectedAnimationLibraryItem!.Id)
            ?.SourceBinding?.Kind is
                AnimationSourceKind.LocalAnm2 or
                AnimationSourceKind.RetailAnm2;

    private async Task RebindSelectedAnimationSourceAsync()
    {
        if (SelectedAnimationLibraryItem is not { } selected ||
            _targetRig is not { } sourceRig ||
            _targetProjectAsset is not { } sourceModelAsset)
        {
            return;
        }

        ProjectAnimation original = _project.Animations.First(
            animation => animation.Id == selected.Id);
        ProjectAssetReference sourceAsset = FindProjectAsset(
                original.SourceAssetId)
            ?? throw new InvalidOperationException(
                "The selected animation source asset is missing.");
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Rebind {original.Name}",
            "Source model",
            "Decoding a clean immutable source document");
        try
        {
            (Anm2Clip raw, AnimationTimingProvenance provenance,
                double? start, double? end, string? detail) =
                await DecodeProjectAnm2Async(
                    sourceAsset,
                    original,
                    job.CancellationToken);
            Anm2PartitionedImportResult partitioned =
                Anm2TrackPartitioner.Partition(
                    raw,
                    sourceRig,
                    original.FrameRate,
                    job.CancellationToken);
            if (partitioned.Partition.RequiresReview)
            {
                throw new InvalidDataException(
                    "Rebinding produced ambiguous bone/morph descriptors that require review.");
            }

            var session = new ImportedAnimationSession(
                sourceRig,
                partitioned.CombinedClip,
                original.SourceBinding?.Kind ==
                    AnimationSourceKind.RetailAnm2
                        ? $"retail://{sourceAsset.ResourceId}"
                        : ResolveLocalProjectAssetPath(sourceAsset),
                original.SourceBinding?.Kind ==
                    AnimationSourceKind.RetailAnm2
                        ? "Retail DL1 ANM2"
                        : "DL1 ANM2")
            {
                SourceKindContract = original.SourceBinding!.Kind,
                RetailSourceModelAssetId = sourceModelAsset.Id,
                Partition = partitioned.Partition,
                TimingProvenance = provenance,
                SourceRangeStartFrame = start,
                SourceRangeEndFrame = end,
                TimingDetail = detail,
                FacialClip = partitioned.FacialClip,
            };
            ProjectAnimation rebound = CreateProjectAnimation(
                session,
                sourceAsset,
                sourceRig,
                sourceModelAsset.Id,
                sourceModelAsset.ContentSha256,
                proposal: null) with
            {
                Name = original.Name + " (rebound)",
            };
            _sourceAnimation = session;
            _sourceBaseMeshes = _targetBaseMeshes;
            _synchronizedAnimation = partitioned.CombinedClip;
            _mimicAnimation = null;
            _facialFbxAnimation = null;
            _activeRetargetMap = null;
            _activeAnimationId = rebound.Id;
            CommitProject(_project with
            {
                Animations = _project.Animations.Add(rebound),
                ActiveAnimationId = rebound.Id,
            });
            Timeline.CurrentFrame = 0;
            RefreshAnimationPreview();
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText = $"Created clean source rebind for {original.Name}";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Source rebind",
                "A clean animation document could not be created",
                exception.Message);
            StatusText = "Source rebind failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveSelectedAnimation()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        bool removedActive = selected.Id == _activeAnimationId;
        ImmutableArray<ProjectAnimation> animations = _project.Animations
            .Where(animation => animation.Id != selected.Id)
            .ToImmutableArray();
        Guid? next = removedActive
            ? animations.FirstOrDefault()?.Id
            : _activeAnimationId;
        _activeAnimationId = next;
        if (removedActive)
        {
            _sourceAnimation = null;
            _sourceBaseMeshes = [];
            _synchronizedAnimation = null;
            _activeRetargetMap = null;
        }

        CommitProject(_project with
        {
            Animations = animations,
            ActiveAnimationId = next,
        });
        Timeline.IsPlaying = false;
        RefreshAnimationPreview();
        StatusText = $"Removed {selected.Name}";
    }

    private void RevealSelectedAnimationSource()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        ProjectAnimation? animation = _project.Animations.FirstOrDefault(
            candidate => candidate.Id == selected.Id);
        ProjectAssetReference? asset = animation is null
            ? null
            : FindProjectAsset(animation.SourceAssetId);
        if (asset?.RetailIdentity is not null)
        {
            AssetItemViewModel? row = FindRetailCatalogAsset(
                asset,
                _indexedAssetItems);
            if (row is null)
            {
                StatusText = "The retail source is not present in the indexed installation";
                return;
            }

            AssetBrowser.SelectedKindFilter = AssetKind.Animation.ToString();
            AssetBrowser.SearchText = row.Name;
            AssetBrowser.SelectedAsset = row;
            StatusText = $"Revealed retail source {row.Name}";
            return;
        }

        if (asset is null || ProjectPath is null)
        {
            return;
        }

        string path = ResolveLocalProjectAssetPath(asset);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{path}\"",
            UseShellExecute = true,
        });
    }

    private bool CanAttachSelectedAnimationAsFacial() =>
        CanUseSelectedAnimationLibraryItem() &&
        _activeAnimationId is { } activeId &&
        SelectedAnimationLibraryItem?.Id != activeId &&
        GetActiveAnimation() is
        {
            MimicAssetId: null,
            FacialSourceAssetId: null,
        };

    private async Task AttachSelectedAnimationAsFacialAsync()
    {
        if (SelectedAnimationLibraryItem is not { } selected ||
            !TryGetActiveAnimation(
                out ProjectAnimation body,
                out int bodyIndex))
        {
            return;
        }

        ProjectAnimation facial = _project.Animations.First(
            animation => animation.Id == selected.Id);
        if (facial.SourceBinding is not { } facialBinding ||
            (facialBinding.Roles & AnimationSourceRoles.Facial) == 0)
        {
            AddDiagnostic(
                "Error",
                "Facial attachment",
                $"{facial.Name} has no exact facial descriptor partition",
                "Mixed retail files are supported, but the selected source must contain exact morph descriptors for its immutable source model.");
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Attach facial {facial.Name}",
            "Facial source",
            "Resolving native facial timing");
        try
        {
            AnimationClip facialClip = await LoadFacialClipAsync(
                facial,
                job.CancellationToken);
            FacialClipTiming timing = FacialClipTiming.ForClip(facialClip);
            AnimationClip bodyClip = _sourceAnimation?.Clip
                ?? throw new InvalidOperationException(
                    "Activate the body animation before attaching a facial source.");
            _synchronizedAnimation =
                AnimationClipSynchronization.Synchronize(
                    bodyClip,
                    facialClip,
                    timing);
            _mimicAnimation = new ImportedMimicSession(
                facial.SourceAssetId,
                facialClip,
                FormatProjectAssetLabel(
                    FindProjectAsset(facial.SourceAssetId)));
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    bodyIndex,
                    body with
                    {
                        MimicAssetId = facial.SourceAssetId,
                        FacialAnimationSourceBinding =
                            facialBinding with
                            {
                                Roles = AnimationSourceRoles.Facial,
                            },
                        FacialTiming = timing,
                    }),
            });
            RefreshAnimationPreview();
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText = $"Attached {facial.Name} as facial animation";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Facial attachment",
                $"Could not attach {facial.Name}",
                exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImportMimicAnimation() =>
        !IsBusy &&
        _sourceAnimation is not null &&
        _targetRig is not null &&
        _targetProjectAsset is not null &&
        GetActiveAnimation() is
        {
            MimicAssetId: null,
            FacialSourceAssetId: null,
            MimicProfileId: null,
        } animation &&
        animation.MorphBindings.IsEmpty;

    private async Task ImportMimicAnimationAsync()
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            _targetProjectAsset is not { } targetAsset ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            AddDiagnostic(
                "Warning",
                "Mimic",
                "Mimic import needs an active body animation and its exact decoded retail target rig",
                null);
            return;
        }

        try
        {
            EnsureExactMimicTarget(
                animation,
                target,
                targetAsset);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Mimic",
                "The selected retail target is not the animation's exact saved target",
                exception.Message);
            StatusText = "Mimic import requires the exact saved target";
            return;
        }

        string? selectedPath =
            _fileDialogs.ShowOpenMimicAnimationDialog(ProjectPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Import mimic {Path.GetFileName(selectedPath)}",
            "Decode",
            "Validating exact target descriptors and synchronized cadence");
        try
        {
            if (!string.Equals(
                    Path.GetExtension(selectedPath),
                    ".anm2",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Mimic animation sources must be DL1 ANM2 files.");
            }

            job.Stage = "Project asset";
            job.Progress = 35.0;
            ImportedProjectSource projectSource =
                await ProjectSourceImporter.ImportAsync(
                    selectedPath,
                    ProjectPath ??
                    throw new InvalidOperationException(
                        "Save the project before importing a mimic animation."),
                    job.CancellationToken);
            job.Stage = "Exact target synchronization";
            job.Progress = 60.0;
            SynchronizedMimicAnimation loaded =
                await SynchronizedMimicAnm2Loader.LoadAsync(
                    projectSource.AbsolutePath,
                    projectSource.Sha256,
                    target,
                    source.Clip,
                    animation.FrameRate,
                    animation.FrameCount,
                    job.CancellationToken);
            ProjectAssetReference asset = new()
            {
                Kind = ProjectAssetKind.SourceAnimation,
                RelativePath = projectSource.ProjectRelativePath,
                ContentSha256 = projectSource.Sha256,
            };
            ProjectAnimation updatedAnimation = animation with
            {
                MimicAssetId = asset.Id,
                FacialAnimationSourceBinding =
                    loaded.Partition is { } partition
                        ? new ProjectAnimationSourceBinding
                        {
                            Kind = AnimationSourceKind.LocalAnm2,
                            AssetId = asset.Id,
                            Roles = AnimationSourceRoles.Facial,
                            SourceRigSignature =
                                RigSignature.Compute(target),
                            RetailSourceModelAssetId =
                                targetAsset.Id,
                            TimingProvenance =
                                AnimationTimingProvenance.UserSpecified,
                            Partition = partition,
                        }
                        : null,
                FacialTiming = loaded.Timing,
            };
            DlraProject updatedProject = _project with
            {
                Assets = _project.Assets.Add(asset),
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updatedAnimation),
            };
            updatedProject.Validate();

            _mimicAnimation = new ImportedMimicSession(
                asset.Id,
                loaded.Mimic,
                projectSource.AbsolutePath);
            _facialFbxAnimation = null;
            _synchronizedAnimation = loaded.Synchronized;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;
            CommitProject(updatedProject);
            RefreshAnimationPreview();
            NotifyExportCommands();

            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Imported synchronized mimic {Path.GetFileName(selectedPath)}";
            AddDiagnostic(
                "Info",
                "Mimic",
                $"Imported {loaded.Mimic.ScalarTracks.Length:N0} exact retail morph tracks",
                $"{loaded.Mimic.FrameCount:N0} native frames at {loaded.Mimic.FrameRate.Numerator}/{loaded.Mimic.FrameRate.Denominator} fps; neutral outside its own range; SHA-256 {projectSource.Sha256}. " +
                (loaded.Partition is { } loadedPartition
                    ? $"The mixed-file partition retained {loadedPartition.BodyDescriptors.Length:N0} body, {loadedPartition.AuxiliaryDescriptors.Length:N0} auxiliary, and {loadedPartition.UnresolvedDescriptors.Length:N0} unresolved descriptor(s) without rejecting the facial import."
                    : string.Empty));
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Mimic import canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Mimic",
                "Mimic ANM2 import failed",
                exception.Message);
            StatusText = "Mimic import failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImportFacialFbx()
    {
        if (IsBusy ||
            FacialFpp.SelectedFacialSourceValueUnit is null ||
            _sourceAnimation is null ||
            _targetRig is null ||
            _targetProjectAsset is null ||
            string.IsNullOrWhiteSpace(ProjectPath) ||
            GetActiveAnimation() is not { } animation)
        {
            return false;
        }

        return animation.MimicAssetId is null &&
               animation.FacialSourceAssetId is null &&
               animation.MorphBindings.IsEmpty &&
               animation.MimicProfileId is null &&
               animation.MimicMappingFingerprint is null;
    }

    private async Task ImportFacialFbxAsync()
    {
        if (FacialFpp.SelectedFacialSourceValueUnit is not
            { } sourceValueUnit ||
            _sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            _targetProjectAsset is not { } targetAsset ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            AddDiagnostic(
                "Warning",
                "Facial FBX",
                "Facial FBX review needs an explicit source unit, an active body timeline, and its exact decoded retail target",
                null);
            return;
        }

        try
        {
            EnsureExactMimicTarget(
                animation,
                target,
                targetAsset);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Facial FBX",
                "The selected retail target is not the animation's exact saved target",
                exception.Message);
            StatusText =
                "Facial FBX import requires the exact saved target";
            return;
        }

        string? selectedPath =
            _fileDialogs.ShowOpenFacialFbxDialog(ProjectPath);
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Review facial FBX {Path.GetFileName(selectedPath)}",
            "Facial FBX",
            "Decoding explicit-unit morph curves on the body timeline");
        try
        {
            if (!string.Equals(
                    Path.GetExtension(selectedPath),
                    ".fbx",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Facial animation review accepts binary FBX files only.");
            }

            job.Stage = "Project source";
            job.Progress = 20.0;
            ImportedProjectSource projectSource =
                await ProjectSourceImporter.ImportAsync(
                    selectedPath,
                    ProjectPath ??
                    throw new InvalidOperationException(
                        "Save the project before importing a facial FBX."),
                    job.CancellationToken);
            job.Stage = "Facial curves";
            job.Progress = 45.0;
            FacialFbxProjectReviewImportResult result =
                await _facialFbxProjectReviewImporter.ImportAsync(
                    projectSource.AbsolutePath,
                    sourceValueUnit,
                    animation,
                    target,
                    job.CancellationToken);
            if (result.UpdatedAnimation.Id != animation.Id ||
                result.UpdatedAnimation.FrameRate !=
                    animation.FrameRate ||
                result.UpdatedAnimation.FrameCount !=
                    animation.FrameCount)
            {
                throw new InvalidDataException(
                    "Facial FBX review changed the authoritative body identity or timeline.");
            }

            if (!result.SourceClip.TransformTracks.IsEmpty ||
                result.SourceClip.FrameRate != animation.FrameRate ||
                result.SourceClip.FrameCount != animation.FrameCount)
            {
                throw new InvalidDataException(
                    "Facial FBX source curves must be scalar-only and use the exact body timeline.");
            }

            AnimationClip synchronized =
                AnimationClipSynchronization.Synchronize(
                    source.Clip,
                    result.SourceClip);
            ProjectAssetReference asset = new()
            {
                Kind = ProjectAssetKind.SourceAnimation,
                RelativePath = projectSource.ProjectRelativePath,
                ContentSha256 = projectSource.Sha256,
            };
            ProjectAnimation updatedAnimation =
                result.UpdatedAnimation with
                {
                    FacialSourceAssetId = asset.Id,
                    FacialSourceValueUnit = sourceValueUnit,
                };
            DlraProject updatedProject = _project with
            {
                Assets = _project.Assets.Add(asset),
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updatedAnimation),
            };
            updatedProject.Validate();

            _facialFbxUnmappedFingerprint =
                updatedAnimation.MimicMappingFingerprint;
            _facialFbxUnmappedChannels =
                result.UnmappedAnimatedChannels;
            _mimicAnimation = null;
            _facialFbxAnimation =
                new ImportedFacialFbxSession(
                    asset.Id,
                    result.SourceClip,
                    projectSource.AbsolutePath,
                    sourceValueUnit);
            _synchronizedAnimation = synchronized;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;
            CommitProject(updatedProject);
            RefreshAnimationPreview();
            NotifyExportCommands();

            foreach (string unmapped in
                     result.UnmappedAnimatedChannels)
            {
                AddDiagnostic(
                    "Warning",
                    "Facial FBX",
                    "Animated facial channel has no DL1 mapping suggestion",
                    unmapped);
            }

            job.Progress = 100.0;
            job.Complete("Review ready");
            StatusText =
                $"Imported {result.SourceChannelCount:N0} retained facial FBX channel(s); review and lock {result.SuggestedBindingCount:N0} suggestion(s) before mimic export";
            AddDiagnostic(
                "Info",
                "Facial FBX",
                "Facial curves and mapping suggestions were added to the authoritative preview/export pipeline",
                $"{sourceValueUnit} source values; {result.UnmappedAnimatedChannels.Length:N0} animated channel(s) unmapped; project-relative SHA-256 {projectSource.Sha256}. Mimic ANM2 is generated only at export after enabled mappings are reviewed and locked.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Facial FBX import canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Facial FBX",
                "Facial FBX mapping review import failed",
                exception.Message);
            StatusText = "Facial FBX import failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyFacialMappingReview() =>
        !IsBusy &&
        HasExactActiveFacialTarget() &&
        GetActiveAnimation()?.MimicProfileId is not null &&
        FacialFpp.FacialMappingReviews.Count > 0 &&
        HasPendingFacialMappingReviewChanges();

    private bool CanReviewAndLockAllFacialMappings() =>
        !IsBusy &&
        HasExactActiveFacialTarget() &&
        GetActiveAnimation()?.MimicProfileId is not null &&
        FacialFpp.FacialMappingReviews.Any(
            static row =>
                !row.IsReviewed ||
                !row.IsLocked);

    private bool HasExactActiveFacialTarget()
    {
        if (_targetRig is not { } target ||
            _targetProjectAsset is not { } targetAsset ||
            GetActiveAnimation() is not { } animation)
        {
            return false;
        }

        try
        {
            EnsureExactMimicTarget(
                animation,
                target,
                targetAsset);
            return true;
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException)
        {
            return false;
        }
    }

    private bool HasPendingFacialMappingReviewChanges()
    {
        ProjectAnimation? animation = GetActiveAnimation();
        return animation is not null &&
               !animation.MorphBindings.SequenceEqual(
                   FacialFpp.FacialMappingReviews.Select(
                       static row => row.BuildBinding()));
    }

    private void ApplyFacialMappingReview()
    {
        if (_targetRig is not { } target ||
            _targetProjectAsset is not { } targetAsset ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        try
        {
            EnsureExactMimicTarget(
                animation,
                target,
                targetAsset);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
            InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Facial FBX",
                "Facial mapping review requires the exact saved retail target",
                exception.Message);
            StatusText =
                "Facial review changes were not applied";
            return;
        }

        ImmutableArray<ProjectMorphBinding> bindings =
            FacialFpp.FacialMappingReviews
                .Select(static row => row.BuildBinding())
                .ToImmutableArray();
        if (bindings.SequenceEqual(animation.MorphBindings))
        {
            return;
        }

        try
        {
            string profileId = animation.MimicProfileId ??
                throw new InvalidDataException(
                    "The facial mapping review has no DL1 mimic profile.");
            string fingerprint =
                FbxFacialProjectReviewService
                    .ComputeMappingFingerprint(
                        profileId,
                        target,
                        new AnimationTiming(
                            animation.FrameRate,
                            animation.FrameCount),
                        bindings);
            if (string.Equals(
                    _facialFbxUnmappedFingerprint,
                    animation.MimicMappingFingerprint,
                    StringComparison.Ordinal))
            {
                _facialFbxUnmappedFingerprint = fingerprint;
            }

            ProjectAnimation updated = animation with
            {
                MorphBindings = bindings,
                MimicMappingFingerprint = fingerprint,
            };
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updated),
            });
            NotifyExportCommands();
            StatusText =
                $"Stored facial review: {bindings.Count(static binding => binding.IsReviewed && binding.IsLocked):N0}/{bindings.Length:N0} mappings reviewed and locked";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            OverflowException)
        {
            AddDiagnostic(
                "Error",
                "Facial FBX",
                "Facial mapping review changes were not stored",
                exception.Message);
            StatusText =
                "Facial review changes were not applied";
        }
    }

    private void ReviewAndLockAllFacialMappings()
    {
        foreach (FacialMorphBindingReviewViewModel row in
                 FacialFpp.FacialMappingReviews)
        {
            row.IsReviewed = true;
            row.IsLocked = true;
        }

        ApplyFacialMappingReview();
    }

    private bool CanReviewMapping() =>
        !IsBusy &&
        _sourceAnimation is not null &&
        _targetRig is not null &&
        _activeRetargetMap is not null &&
        GetActiveAnimation() is not null;

    private bool CanExportAnimation() =>
        CanReviewMapping() &&
        TryAnalyzeActiveMapping(out RetargetMappingReviewReport? review) &&
        review is { IsReady: true };

    private bool TryAnalyzeActiveMapping(
        out RetargetMappingReviewReport? review)
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            _activeRetargetMap is not { } mapping)
        {
            review = null;
            return false;
        }

        try
        {
            review = RetargetMappingReview.Analyze(
                source.Rig,
                target,
                mapping);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException)
        {
            review = null;
            MappingReviewStatus =
                $"Mapping validation failed: {exception.Message}";
            return false;
        }
    }

    private void NotifyExportCommands()
    {
        ImportMimicAnimationCommand.NotifyCanExecuteChanged();
        NotifyFacialMappingReviewCommands();
        ExportBodyCommand.NotifyCanExecuteChanged();
        ExportMimicCommand.NotifyCanExecuteChanged();
        ExportBodyAndMimicCommand.NotifyCanExecuteChanged();
    }

    private static void EnsureExactMimicTarget(
        ProjectAnimation animation,
        RigDefinition targetRig,
        ProjectAssetReference targetAsset)
    {
        if (animation.TargetAssetId != targetAsset.Id ||
            targetAsset.Kind !=
                ProjectAssetKind.RetailGameResource ||
            targetAsset.RetailIdentity is null ||
            string.IsNullOrWhiteSpace(
                targetAsset.ContentSha256))
        {
            throw new InvalidOperationException(
                "The active animation is not bound to the selected retail asset identity.");
        }

        string signature = RigSignature.Compute(targetRig);
        if (!string.Equals(
                signature,
                animation.TargetRigSignature,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected retail target rig signature differs from the saved animation target.");
        }
    }

    private async Task ExportAnimationAsync(
        Dl1AnimationExportParts parts)
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            _activeRetargetMap is not { } mapping ||
            GetActiveAnimation() is not { } animation)
        {
            AddDiagnostic(
                "Warning",
                "Export",
                "Export needs a loaded source, decoded retail target, and reviewed mapping",
                null);
            return;
        }

        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(
                source.Rig,
                target,
                mapping);
        if (!review.IsReady)
        {
            PublishMappingReviewDiagnostics(review);
            MappingReviewStatus = FormatMappingReviewStatus(review);
            StatusText =
                "Export blocked: review and save the retarget mapping";
            return;
        }

        string? outputDirectory =
            _fileDialogs.ShowSelectExportDirectoryDialog(
                ProjectPath is null
                    ? null
                    : Path.GetDirectoryName(ProjectPath));
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Export {animation.Name}",
            "Authoritative pipeline",
            "Validating source, target, and mapping identities");
        try
        {
            string sourceSignature = RigSignature.Compute(source.Rig);
            string targetSignature = RigSignature.Compute(target);
            string targetFingerprint =
                _targetProjectAsset?.ContentSha256
                ?? throw new InvalidOperationException(
                    "The selected retail target has no content fingerprint.");
            string mappingFingerprint =
                RetargetMapFingerprint.Compute(
                    sourceSignature,
                    targetSignature,
                    targetFingerprint,
                    mapping);
            if (!string.Equals(
                    sourceSignature,
                    animation.SourceRigSignature,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    targetSignature,
                    animation.TargetRigSignature,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    mappingFingerprint,
                    animation.MappingFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The active source, retail target, or mapping no longer matches the saved project identities. Review and save the mapping before export.");
            }

            job.Progress = 20.0;
            job.Stage = "Sampling authored frames";
            EvaluationRequest evaluation = CreateEvaluationRequest(
                animation,
                0,
                PreviewProfile.RawAuthoring,
                PlaybackMode.Clamp,
                EvaluationPurpose.Export);
            var exporter = new Dl1AnimationExporter(
                new Anm2EvaluationAdapter(
                    new AnimationEvaluator()));
            Dl1AnimationExportResult result = await Task.Run(
                () => exporter.Export(
                    new Dl1AnimationExportRequest
                    {
                        Evaluation = evaluation,
                        Parts = parts,
                    },
                    job.CancellationToken),
                job.CancellationToken);

            job.Progress = 82.0;
            job.Stage = "Atomic ANM2 write";
            Directory.CreateDirectory(outputDirectory);
            string safeName = MakeSafeFileName(animation.Name);
            List<string> outputs = [];
            if (result.BodyAnm2 is not null)
            {
                string bodyPath = Path.Combine(
                    outputDirectory,
                    safeName + ".anm2");
                await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                    bodyPath,
                    result.BodyAnm2,
                    job.CancellationToken);
                outputs.Add(bodyPath);
            }

            if (result.MimicAnm2 is not null)
            {
                string mimicPath = Path.Combine(
                    outputDirectory,
                    safeName + "_mimic.anm2");
                await Rp6lAnimationLibraryCodec.WriteAtomicAsync(
                    mimicPath,
                    result.MimicAnm2,
                    job.CancellationToken);
                outputs.Add(mimicPath);
            }

            job.Progress = 100.0;
            job.Complete("Complete");
            AddDiagnostic(
                "Info",
                "Export",
                $"Exported {outputs.Count:N0} DL1 ANM2 file(s) through the authoritative authored pipeline",
                string.Join(Environment.NewLine, outputs));
            StatusText =
                $"Exported {string.Join(", ", outputs.Select(Path.GetFileName))}";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Animation export canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Export",
                "DL1 ANM2 export failed",
                exception.Message);
            StatusText = "Animation export failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string MakeSafeFileName(string value)
    {
        HashSet<char> invalid =
            Path.GetInvalidFileNameChars().ToHashSet();
        string safe = new(value
            .Trim()
            .Select(character => invalid.Contains(character)
                ? '_'
                : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(safe)
            ? "animation"
            : safe;
    }

    private async Task ImportFedAsync()
    {
        string? path = _fileDialogs.ShowOpenFedDialog(ProjectPath);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            $"Inspect {Path.GetFileName(path)}",
            "FED",
            "Reading bounded facial-expression data");
        try
        {
            FedDocument document = await Task.Run(
                () => FedReader.Read(path),
                job.CancellationToken);
            _fedDocument = document;
            FacialFpp.ReplaceMimicPresets(
                document.Expressions.Select(
                    static expression => expression.Name));
            foreach (FedDiagnostic diagnostic in document.Diagnostics)
            {
                AddDiagnostic(
                    diagnostic.Severity.ToString(),
                    "FED",
                    diagnostic.Message,
                    diagnostic.Code);
            }

            job.Progress = 100.0;
            job.Complete("Complete");
            ApplyFedExpressionCommand.NotifyCanExecuteChanged();
            StatusText =
                $"Loaded {document.Expressions.Count:N0} FED expressions from {Path.GetFileName(path)}";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "FED load canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidDataException
            or IOException
            or UnauthorizedAccessException
            or EndOfStreamException
            or OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "FED",
                "FED expression file could not be loaded",
                exception.Message);
            StatusText = "FED load failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyFedExpression() =>
        !IsBusy &&
        _fedDocument is not null &&
        !string.IsNullOrWhiteSpace(
            FacialFpp.SelectedMimicPreset) &&
        _targetRig is not null &&
        GetActiveAnimation() is not null;

    private void ApplyFedExpression()
    {
        if (_fedDocument is not { } document ||
            FacialFpp.SelectedMimicPreset is not { } expressionName ||
            _targetRig is not { } target ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        try
        {
            FedLayerBuildResult result =
                FedDomainAdapter.CreateLayer(
                    document,
                    expressionName,
                    target,
                    compatibilityPolicy:
                        FedLayerCompatibilityPolicy
                            .RequireComplete);
            ProjectAnimation updated = animation with
            {
                MorphEditLayers =
                    animation.MorphEditLayers.Add(result.Layer),
            };
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updated),
            });
            foreach (FedDiagnostic diagnostic in result.Diagnostics)
            {
                AddDiagnostic(
                    diagnostic.Severity.ToString(),
                    "FED",
                    diagnostic.Message,
                    diagnostic.Code);
            }

            AddDiagnostic(
                "Info",
                "FED",
                $"Applied '{expressionName}' as an authored, non-destructive facial layer",
                $"All {result.Compatibility.SourceWeightCount:N0} FED rows resolved against the selected mesh's exact morph inventory.");
            RefreshAnimationPreview();
            StatusText = $"Applied FED expression {expressionName}";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or KeyNotFoundException)
        {
            AddDiagnostic(
                "Error",
                "FED",
                $"Could not apply FED expression '{expressionName}'",
                exception.Message);
        }
    }

    private bool CanKeyMorphPose() =>
        !IsBusy &&
        FacialFpp.Morphs.Count > 0 &&
        GetActiveAnimation() is not null;

    private void KeyMorphPose()
    {
        if (!TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        double frame = Timeline.CurrentFrame;
        int layerIndex = FindFacialEditorLayerIndex(
            animation.MorphEditLayers);
        MorphEditLayer layer = layerIndex >= 0
            ? animation.MorphEditLayers[layerIndex]
            : new MorphEditLayer(
                Guid.NewGuid(),
                FacialEditorLayerName,
                MorphEditBlendMode.Override,
                MorphEditLayerScope.AuthoredExportable,
                1,
                []);
        ImmutableArray<MorphEditTrack> tracks = layer.Tracks;
        foreach (MorphChannelViewModel morph in FacialFpp.Morphs)
        {
            int trackIndex = FindMorphTrackIndex(
                tracks,
                morph.Name);
            ImmutableArray<ScalarKeyframe> keys =
                trackIndex >= 0
                    ? tracks[trackIndex].Keyframes
                    : [];
            int keyIndex = FindScalarKeyIndex(keys, frame);
            ScalarKeyframe key = new(frame, morph.Weight);
            keys = keyIndex >= 0
                ? keys.SetItem(keyIndex, key)
                : keys.Add(key)
                    .OrderBy(static item => item.Frame)
                    .ToImmutableArray();
            MorphEditTrack updatedTrack = new(
                morph.Name,
                keys);
            tracks = trackIndex >= 0
                ? tracks.SetItem(trackIndex, updatedTrack)
                : tracks.Add(updatedTrack);
        }

        MorphEditLayer updatedLayer = new(
            layer.Id,
            layer.Name,
            MorphEditBlendMode.Override,
            MorphEditLayerScope.AuthoredExportable,
            1,
            tracks,
            enabled: true);
        ImmutableArray<MorphEditLayer> layers =
            layerIndex >= 0
                ? animation.MorphEditLayers
                    .RemoveAt(layerIndex)
                    .Add(updatedLayer)
                : animation.MorphEditLayers.Add(updatedLayer);
        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                animation with
                {
                    MorphEditLayers = layers,
                }),
        });
        AddDiagnostic(
            "Info",
            "Facial editor",
            $"Stored {FacialFpp.Morphs.Count:N0} authored morph values at frame {frame:N0}",
            $"The final immutable '{FacialEditorLayerName}' override layer stores absolute authored totals and is included in mimic export.");
        RefreshAnimationPreview();
        StatusText = $"Keyed facial pose at frame {frame:N0}";
    }

    private void InitializeIkEditorFromBindPose()
    {
        if (_targetRig is not { } rig ||
            IkEditor.SelectedChain is not { } selectedName)
        {
            return;
        }

        TwoBoneIkChainDefinition? chain = rig.IkChains.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                selectedName,
                StringComparison.OrdinalIgnoreCase));
        if (chain is null)
        {
            return;
        }

        SkeletonPose bind = rig.CreateBindPose();
        Vector3D end =
            bind.GlobalMatrices[chain.EndBoneIndex].Translation;
        Vector3D joint =
            bind.GlobalMatrices[chain.JointBoneIndex].Translation;
        Vector3D root =
            bind.GlobalMatrices[chain.RootBoneIndex].Translation;
        double offset = Math.Max(
            0.25,
            Vector3D.Distance(root, end) * 0.5);
        Vector3D pole = joint + (Vector3D.UnitZ * offset);
        IkEditor.EffectorX = end.X;
        IkEditor.EffectorY = end.Y;
        IkEditor.EffectorZ = end.Z;
        IkEditor.PoleX = pole.X;
        IkEditor.PoleY = pole.Y;
        IkEditor.PoleZ = pole.Z;
    }

    private bool CanKeyIkConstraint() =>
        !IsBusy &&
        _targetRig is not null &&
        !string.IsNullOrWhiteSpace(IkEditor.SelectedChain) &&
        GetActiveAnimation() is not null;

    private void KeyIkConstraint()
    {
        if (_targetRig is not { } rig ||
            IkEditor.SelectedChain is not { } chainName ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        if (rig.IkChains.All(chain => !string.Equals(
                chain.Name,
                chainName,
                StringComparison.OrdinalIgnoreCase)))
        {
            AddDiagnostic(
                "Error",
                "IK",
                $"IK chain '{chainName}' is not validated for the selected retail rig",
                "FK bone editing remains available.");
            return;
        }

        try
        {
            double frame = Timeline.CurrentFrame;
            QuaternionD? orientation =
                IkEditor.UseEndOrientation
                    ? CreateEditorQuaternion(
                        IkEditor.EndRotationX,
                        IkEditor.EndRotationY,
                        IkEditor.EndRotationZ)
                    : null;
            ProjectIkKeyframe key = new()
            {
                Frame = frame,
                Effector = new Vector3D(
                    IkEditor.EffectorX,
                    IkEditor.EffectorY,
                    IkEditor.EffectorZ),
                Pole = new Vector3D(
                    IkEditor.PoleX,
                    IkEditor.PoleY,
                    IkEditor.PoleZ),
                EndOrientation = orientation,
            };
            if (!key.Effector.IsFinite ||
                !key.Pole.IsFinite ||
                (key.EndOrientation.HasValue &&
                 !key.EndOrientation.Value.IsFinite))
            {
                throw new ArgumentException(
                    "IK effector, pole, and orientation values must be finite.");
            }

            int layerIndex = FindProjectIkLayerIndex(
                animation.IkLayers,
                chainName);
            ProjectIkLayer layer = layerIndex >= 0
                ? animation.IkLayers[layerIndex]
                : new ProjectIkLayer
                {
                    Name = $"Editor IK: {chainName}",
                    ChainName = chainName,
                    Weight = IkEditor.Weight,
                    BakeToEditLayer =
                        IkEditor.BakeToEditLayer,
                    Keyframes = [key],
                };
            ImmutableArray<ProjectIkKeyframe> keys =
                layer.Keyframes;
            int keyIndex = FindProjectIkKeyIndex(
                keys,
                frame);
            keys = keyIndex >= 0
                ? keys.SetItem(keyIndex, key)
                : keys.Add(key)
                    .OrderBy(static item => item.Frame)
                    .ToImmutableArray();
            bool hasOrientation =
                keys[0].EndOrientation.HasValue;
            if (keys.Any(candidate =>
                    candidate.EndOrientation.HasValue !=
                    hasOrientation))
            {
                throw new InvalidOperationException(
                    "A keyed IK layer must either orient every end-effector key or none of them. Keep the orientation toggle consistent for this chain.");
            }

            ProjectIkLayer updatedLayer = layer with
            {
                Enabled = true,
                Weight = IkEditor.Weight,
                BakeToEditLayer =
                    IkEditor.BakeToEditLayer,
                Keyframes = keys,
            };
            ImmutableArray<ProjectIkLayer> layers =
                layerIndex >= 0
                    ? animation.IkLayers.SetItem(
                        layerIndex,
                        updatedLayer)
                    : animation.IkLayers.Add(updatedLayer);
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    animation with
                    {
                        IkLayers = layers,
                    }),
            });
            AddDiagnostic(
                "Info",
                "IK",
                $"Stored {chainName} effector and pole at frame {frame:N0}",
                "This validated two-bone IK layer is authored/exportable; no rest skeleton was modified.");
            RefreshAnimationPreview();
            StatusText = $"Keyed {chainName} IK at frame {frame:N0}";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            AddDiagnostic(
                "Error",
                "IK",
                $"Could not key {chainName}",
                exception.Message);
        }
    }

    private bool CanBakeSelectedIkConstraint()
    {
        if (IsBusy ||
            _sourceAnimation is null ||
            _targetRig is null ||
            _activeRetargetMap is null ||
            IkEditor.SelectedChain is not { } chainName ||
            GetActiveAnimation() is not { } animation)
        {
            return false;
        }

        return animation.IkLayers.Any(layer =>
            layer.Enabled &&
            layer.BakeToEditLayer &&
            string.Equals(
                layer.ChainName,
                chainName,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task BakeSelectedIkConstraintAsync()
    {
        if (_targetRig is null ||
            IkEditor.SelectedChain is not { } chainName ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        ProjectIkLayer? projectLayer =
            animation.IkLayers.FirstOrDefault(layer =>
                layer.Enabled &&
                layer.BakeToEditLayer &&
                string.Equals(
                    layer.ChainName,
                    chainName,
                    StringComparison.OrdinalIgnoreCase));
        if (projectLayer is null)
        {
            return;
        }

        DlraProject projectAtStart = _project;
        Guid bakedLayerId = Guid.NewGuid();
        if (_ikBakeJob is { IsCancellable: true } previousJob)
        {
            previousJob.Cancel();
            previousJob.Complete("Superseded");
        }

        JobViewModel job = AddJob(
            $"Bake {chainName} IK",
            "Authoritative evaluation",
            $"Sampling {animation.FrameCount:N0} export poses");
        _ikBakeJob = job;
        IsBusy = true;
        StatusText =
            $"Baking {chainName} IK across {animation.FrameCount:N0} frames\u2026";
        try
        {
            EvaluationRequest request = CreateEvaluationRequest(
                animation,
                0,
                PreviewProfile.RawAuthoring,
                PlaybackMode.Clamp,
                EvaluationPurpose.Export);
            BoneEditLayer baked = await Task.Run(
                () => IkConstraintLayerBaker
                    .BakeToOverrideLayer(
                        new AnimationEvaluator(),
                        request,
                        projectLayer.Id,
                        bakedLayerId,
                        $"Baked IK: {chainName}",
                        job.CancellationToken),
                job.CancellationToken);
            if (!ReferenceEquals(_project, projectAtStart))
            {
                job.Complete("Superseded");
                AddDiagnostic(
                    "Warning",
                    "IK",
                    $"Discarded stale {chainName} bake",
                    "The project changed while IK baking was running; no baked layer was applied.");
                StatusText = "IK bake superseded by a project change";
                return;
            }

            ProjectAnimation updatedAnimation = animation with
            {
                EditLayers = animation.EditLayers.Add(baked),
                IkLayers = animation.IkLayers.Remove(projectLayer),
            };
            DlraProject updated = _project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updatedAnimation),
            };
            updated.Validate();
            CommitProject(updated);
            job.Progress = 100.0;
            job.Complete("Complete");
            IkEditor.BakeToEditLayer = false;
            AddDiagnostic(
                "Info",
                "IK",
                $"Baked {chainName} into '{baked.Name}'",
                $"Generated {baked.Tracks.Length:N0} authored FK tracks with {animation.FrameCount:N0} deterministic samples each; the keyed IK layer was removed in the same undoable transaction.");
            RefreshAnimationPreview();
            StatusText = $"Baked {chainName} IK to bone layer";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            AddDiagnostic(
                "Info",
                "IK",
                $"Canceled {chainName} bake",
                "No partial FK layer was applied.");
            StatusText = "IK bake canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "IK",
                $"Could not bake {chainName}",
                exception.Message);
            StatusText = "IK bake failed";
        }
        finally
        {
            if (ReferenceEquals(_ikBakeJob, job))
            {
                _ikBakeJob = null;
            }

            IsBusy = false;
        }
    }

    private static QuaternionD CreateEditorQuaternion(
        double rotationX,
        double rotationY,
        double rotationZ)
    {
        const double degreesToRadians = Math.PI / 180.0;
        System.Numerics.Quaternion rotation =
            System.Numerics.Quaternion.CreateFromYawPitchRoll(
                checked((float)(rotationY * degreesToRadians)),
                checked((float)(rotationX * degreesToRadians)),
                checked((float)(rotationZ * degreesToRadians)));
        return new QuaternionD(
                rotation.X,
                rotation.Y,
                rotation.Z,
                rotation.W)
            .Normalized();
    }

    private static ProjectAnimation CreateProjectAnimation(
        ImportedAnimationSession session,
        ProjectAssetReference asset,
        RigDefinition? targetRig,
        Guid? targetAssetId,
        string? targetAssetFingerprint,
        RetargetMap? proposal)
    {
        string sourceSignature = RigSignature.Compute(session.Rig);
        string? targetSignature = targetRig is null
            ? null
            : RigSignature.Compute(targetRig);
        return new ProjectAnimation
        {
            Name = session.Clip.Name,
            SourceAssetId = asset.Id,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = session.SourceKindContract,
                AssetId = asset.Id,
                Roles = ResolveSourceRoles(session),
                SourceRigSignature = sourceSignature,
                RetailSourceModelAssetId =
                    session.RetailSourceModelAssetId,
                TimingProvenance = session.TimingProvenance,
                SourceRangeStartFrame = session.SourceRangeStartFrame,
                SourceRangeEndFrame = session.SourceRangeEndFrame,
                TimingDetail = session.TimingDetail,
                Partition = session.Partition,
            },
            TargetAssetId = targetAssetId,
            TargetRigId = targetRig?.Id ?? "dl1-retail:target-not-selected",
            SourceRigSignature = sourceSignature,
            TargetRigSignature = targetSignature,
            MappingFingerprint =
                proposal is null || targetSignature is null
                    ? null
                    : RetargetMapFingerprint.Compute(
                        sourceSignature,
                        targetSignature,
                        targetAssetFingerprint,
                        proposal),
            FrameRate = session.Clip.FrameRate,
            FrameCount = session.Clip.FrameCount,
            RootMotionMode = session.SourceKindContract ==
                    AnimationSourceKind.LocalFbx
                ? Dl1RootMotionMode.InPlace
                : Dl1RootMotionMode.Recorded,
            BoneMappings = proposal is null
                ? []
                : ToProjectMappings(
                    session.Rig,
                    targetRig!,
                    proposal),
            TargetBindReviews = proposal is null
                ? []
                : ToProjectTargetBindReviews(
                    targetRig!,
                    proposal),
        };
    }

    private static AnimationSourceRoles ResolveSourceRoles(
        ImportedAnimationSession session)
    {
        AnimationSourceRoles roles = session.Partition?.Roles ??
            AnimationSourceRoles.Body;
        if (!session.Clip.ScalarTracks.IsEmpty)
        {
            roles |= AnimationSourceRoles.Facial;
        }

        if (!session.Clip.AuxiliaryTransformTracks.IsEmpty)
        {
            roles |= AnimationSourceRoles.Auxiliary;
        }

        return roles;
    }

    private void UpdateRootMotionMode(
        Dl1RootMotionMode mode)
    {
        if (!Enum.IsDefined(mode) ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex) ||
            animation.RootMotionMode == mode)
        {
            return;
        }

        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                animation with
                {
                    RootMotionMode = mode,
                }),
        });
        AddDiagnostic(
            "Info",
            "Root motion",
            $"Root policy changed to {mode}",
            "Preview and export use the same authored root/helper policy.");
        RefreshAnimationPreview();
    }

    private bool CanAddHelperOverride()
    {
        if (IsBusy ||
            _sourceAnimation is null ||
            _targetRig is null ||
            GetActiveAnimation() is null ||
            string.IsNullOrWhiteSpace(
                SelectedHelperOverrideSourceBone) ||
            string.IsNullOrWhiteSpace(
                SelectedHelperOverrideTargetBone))
        {
            return false;
        }

        string sourceName =
            SelectedHelperOverrideSourceBone;
        string targetName =
            SelectedHelperOverrideTargetBone;
        int sourceIndex =
            _sourceAnimation.Rig.GetBoneIndex(sourceName);
        int targetIndex =
            _targetRig.GetBoneIndex(targetName);
        if (sourceIndex < 0 ||
            targetIndex < 0 ||
            _targetRig.Bones[targetIndex].Kind is not (
                BoneKind.Helper or
                BoneKind.Camera or
                BoneKind.Prop))
        {
            return false;
        }

        return BoneMappings.All(row =>
            string.IsNullOrWhiteSpace(row.TargetBone) ||
            !string.Equals(
                row.TargetBone,
                targetName,
                StringComparison.OrdinalIgnoreCase));
    }

    private void AddHelperOverride()
    {
        if (!CanAddHelperOverride())
        {
            return;
        }

        string sourceName =
            SelectedHelperOverrideSourceBone!;
        string targetName =
            SelectedHelperOverrideTargetBone!;
        var row = new BoneMappingViewModel(
            sourceName,
            targetName,
            GetMappingConfidence(
                BoneMappingMethod.Manual.ToString()),
            BoneMappingMethod.Manual.ToString(),
            mappingKind:
                RetargetMappingKind.HelperOverride,
            transferPolicy:
                RetargetTransferPolicy.RestRelative,
            componentPolicy:
                RetargetMapBuilder.GetDefaultHelperComponentPolicy(
                    targetName));
        row.PropertyChanged += OnBoneMappingChanged;
        BoneMappings.Add(row);
        SelectedBoneMapping = row;
        if (TryPersistBoneMappings(
                row,
                mappingIdentityChanged: true,
                policyChanged: false))
        {
            StatusText =
                $"Added helper override {targetName} <- {sourceName}; the source's ordinary body row was preserved";
        }
    }

    private bool CanRemoveSelectedHelperOverride() =>
        !IsBusy &&
        GetActiveAnimation() is not null &&
        SelectedBoneMapping is
        {
            MappingKind:
                RetargetMappingKind.HelperOverride,
            HasTarget: true,
        };

    private void RemoveSelectedHelperOverride()
    {
        if (!CanRemoveSelectedHelperOverride() ||
            SelectedBoneMapping is not { } row)
        {
            return;
        }

        string sourceName = row.SourceBone;
        string targetName = row.TargetBone!;
        row.PropertyChanged -= OnBoneMappingChanged;
        BoneMappings.Remove(row);
        SelectedBoneMapping = null;
        if (TryPersistBoneMappings(
                changedRow: null,
                mappingIdentityChanged: true,
                policyChanged: false))
        {
            StatusText =
                $"Removed helper override {targetName} <- {sourceName}; ordinary body mappings were unchanged";
        }
    }

    internal static RetargetComponentPolicy
        DefaultHelperComponentPolicy(
            string targetName) =>
        RetargetMapBuilder.GetDefaultHelperComponentPolicy(
            targetName);

    private void AutoMap()
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            AddDiagnostic(
                "Warning",
                "Retargeting",
                "Auto-map needs a loaded source animation and retail target rig",
                null);
            return;
        }

        RetargetMap proposal = MergeAutoMapWithLockedRows(
            RetargetMapBuilder.CreateSuggested(
                source.Rig,
                target),
            _activeRetargetMap);
        _activeRetargetMap = proposal;
        ProjectAnimation updated = animation with
        {
            TargetRigId = target.Id,
            SourceRigSignature = RigSignature.Compute(source.Rig),
            TargetRigSignature = RigSignature.Compute(target),
            MappingFingerprint =
                RetargetMapFingerprint.Compute(
                    RigSignature.Compute(source.Rig),
                    RigSignature.Compute(target),
                    _targetProjectAsset?.ContentSha256,
                    proposal),
            BoneMappings = ToProjectMappings(
                source.Rig,
                target,
                proposal),
            TargetBindReviews =
                ToProjectTargetBindReviews(
                    target,
                    proposal),
        };
        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                updated),
        });
        PublishMappingProposal(proposal);
        RefreshAnimationPreview();
    }

    private void PublishMappingProposal(RetargetMap proposal)
    {
        RigDefinition source = _sourceAnimation!.Rig;
        RigDefinition target = _targetRig!;
        RetargetMappingReviewReport review =
            RetargetMappingReview.Analyze(
                source,
                target,
                proposal);
        int targetBindRowCount = target.Bones.Count(
            bone => proposal.Entries.All(
                entry => entry.TargetBoneIndex != bone.Index));
        foreach (CompatibilityDiagnostic diagnostic in
                 review.Diagnostics)
        {
            AddDiagnostic(
                diagnostic.Severity switch
                {
                    CompatibilityDiagnosticSeverity.Error => "Error",
                    CompatibilityDiagnosticSeverity.Warning => "Warning",
                    _ => "Info",
                },
                "Retargeting",
                diagnostic.Message,
                diagnostic.Code);
        }

        MappingReviewStatus = FormatMappingReviewStatus(review);
        StatusText =
            $"Auto-map proposed {proposal.Entries.Length:N0}/{target.BoneCount:N0} target rows; {targetBindRowCount:N0} target-only rows stay at bind";
        NotifyMappingCommands();
    }

    private void ValidateMapping()
    {
        if (!TryAnalyzeActiveMapping(
                out RetargetMappingReviewReport? review) ||
            review is null)
        {
            MappingReviewStatus =
                "Mapping validation is unavailable until source, target, and proposal are loaded.";
            return;
        }

        PublishMappingReviewDiagnostics(review);
        MappingReviewStatus = FormatMappingReviewStatus(review);
        StatusText = review.IsReady
            ? "Mapping validation passed"
            : "Mapping validation requires explicit review";
        NotifyMappingCommands();
    }

    private void SaveReviewedMapping()
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            _activeRetargetMap is not { } mapping ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        RetargetMap reviewed;
        try
        {
            reviewed = ApplyExplicitReviewSelections(
                source.Rig,
                target,
                mapping,
                BoneMappings,
                RequiredTargetBindReviews);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Mapping review",
                "Explicit mapping review selections were rejected",
                exception.Message);
            MappingReviewStatus =
                $"Mapping review selections are invalid: {exception.Message}";
            return;
        }

        _activeRetargetMap = reviewed;
        string sourceSignature = RigSignature.Compute(source.Rig);
        string targetSignature = RigSignature.Compute(target);
        ProjectAnimation updated = animation with
        {
            SourceRigSignature = sourceSignature,
            TargetRigSignature = targetSignature,
            BoneMappings = ToProjectMappings(
                source.Rig,
                target,
                reviewed),
            TargetBindReviews =
                ToProjectTargetBindReviews(
                    target,
                    reviewed),
            MappingFingerprint =
                RetargetMapFingerprint.Compute(
                    sourceSignature,
                    targetSignature,
                    _targetProjectAsset?.ContentSha256,
                    reviewed),
        };
        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                updated),
        });
        RetargetMappingReviewReport report =
            RetargetMappingReview.Analyze(
                source.Rig,
                target,
                reviewed);
        PublishMappingReviewDiagnostics(report);
        MappingReviewStatus = FormatMappingReviewStatus(report);
        StatusText = report.IsReady
            ? "Reviewed mapping saved in project"
            : "Mapping saved, but validation still reports blockers";
        NotifyMappingCommands();
    }

    internal static RetargetMap ApplyExplicitReviewSelections(
        RigDefinition source,
        RigDefinition target,
        RetargetMap mapping,
        IEnumerable<BoneMappingViewModel> mappingRows,
        IEnumerable<TargetBindReviewViewModel>
            requiredTargetBindRows)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(mappingRows);
        ArgumentNullException.ThrowIfNull(
            requiredTargetBindRows);
        if (!string.Equals(
                mapping.SourceRigId,
                source.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                mapping.TargetRigId,
                target.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The active review mapping does not belong to the loaded rig pair.");
        }

        if (mapping.Entries.Any(entry =>
                (uint)entry.SourceBoneIndex >=
                    (uint)source.BoneCount ||
                (uint)entry.TargetBoneIndex >=
                    (uint)target.BoneCount))
        {
            throw new InvalidOperationException(
                "The active review mapping contains a bone index outside the loaded rigs.");
        }
        if (mapping.ReviewedTargetBindBoneIndices.Any(index =>
                (uint)index >= (uint)target.BoneCount))
        {
            throw new InvalidOperationException(
                "The active review mapping contains a target-bind decision outside the loaded target rig.");
        }

        Dictionary<int, BoneMappingViewModel> rowsByTarget = [];
        foreach (BoneMappingViewModel row in mappingRows.Where(
                     static row => row.HasTarget))
        {
            int sourceIndex =
                source.GetBoneIndex(row.SourceBone);
            int targetIndex =
                target.GetBoneIndex(row.TargetBone!);
            if (sourceIndex < 0 || targetIndex < 0)
            {
                throw new InvalidOperationException(
                    $"Review row '{row.SourceBone}' -> '{row.TargetBone}' does not identify unique bones in the loaded rigs.");
            }

            if (!rowsByTarget.TryAdd(targetIndex, row))
            {
                throw new InvalidOperationException(
                    $"Target bone '{target.Bones[targetIndex].Name}' has more than one visible review row.");
            }
        }

        var entries =
            ImmutableArray.CreateBuilder<BoneMapEntry>(
                mapping.Entries.Length);
        foreach (BoneMapEntry entry in mapping.Entries)
        {
            if (!rowsByTarget.TryGetValue(
                    entry.TargetBoneIndex,
                    out BoneMappingViewModel? row) ||
                source.GetBoneIndex(row.SourceBone) !=
                    entry.SourceBoneIndex ||
                row.MappingKind != entry.MappingKind)
            {
                throw new InvalidOperationException(
                    $"The visible review row for target '{target.Bones[entry.TargetBoneIndex].Name}' does not match the active mapping.");
            }

            entries.Add(
                new BoneMapEntry(
                    entry.SourceBoneIndex,
                    entry.TargetBoneIndex,
                    entry.Method,
                    entry.Confidence,
                    entry.IsLocked,
                    row.IsReviewed,
                    entry.MappingKind,
                    entry.TransferPolicy,
                    entry.ComponentPolicy));
        }

        if (rowsByTarget.Count != mapping.Entries.Length)
        {
            throw new InvalidOperationException(
                "The visible mapping review contains rows that are not part of the active mapping.");
        }

        HashSet<int> mappedTargets = mapping.Entries
            .Select(static entry => entry.TargetBoneIndex)
            .ToHashSet();
        TargetBindReviewViewModel[] bindRows =
            requiredTargetBindRows.ToArray();
        HashSet<int> representedRequiredTargets = [];
        foreach (TargetBindReviewViewModel row in bindRows)
        {
            if ((uint)row.TargetBoneIndex >=
                    (uint)target.BoneCount ||
                !string.Equals(
                    target.Bones[row.TargetBoneIndex].Name,
                    row.TargetBone,
                    StringComparison.Ordinal) ||
                !target.Bones[row.TargetBoneIndex]
                    .RequiredForExport ||
                mappedTargets.Contains(row.TargetBoneIndex) ||
                !representedRequiredTargets.Add(
                    row.TargetBoneIndex))
            {
                throw new InvalidOperationException(
                    $"Target-bind review row '{row.TargetBone}' does not represent one unique, required, unmapped target bone.");
            }
        }

        int[] expectedRequiredTargets = target.Bones
            .Where(bone =>
                bone.RequiredForExport &&
                !mappedTargets.Contains(bone.Index))
            .Select(static bone => bone.Index)
            .ToArray();
        if (expectedRequiredTargets.Length !=
                representedRequiredTargets.Count ||
            expectedRequiredTargets.Any(index =>
                !representedRequiredTargets.Contains(index)))
        {
            throw new InvalidOperationException(
                "Every required unmapped target bone must have a visible target-bind review row.");
        }

        HashSet<int> reviewedTargetBindBones = mapping
            .ReviewedTargetBindBoneIndices
            .Where(index =>
                !representedRequiredTargets.Contains(index))
            .ToHashSet();
        foreach (TargetBindReviewViewModel row in bindRows.Where(
                     static row => row.IsReviewed))
        {
            reviewedTargetBindBones.Add(
                row.TargetBoneIndex);
        }

        return new RetargetMap(
            mapping.SourceRigId,
            mapping.TargetRigId,
            entries,
            reviewedTargetBindBones.Order());
    }

    private static string FormatMappingReviewStatus(
        RetargetMappingReviewReport review)
    {
        if (review.IsReady)
        {
            return "Ready for export: deterministic identities and explicit review decisions are current.";
        }

        return
            $"{review.ExplicitReviewRequiredCount:N0} mapped row(s) and {review.RequiredTargetBindReviewCount:N0} required target-bind row(s) need explicit review before export.";
    }

    private void PublishMappingReviewDiagnostics(
        RetargetMappingReviewReport review)
    {
        foreach (CompatibilityDiagnostic diagnostic in
                 review.Diagnostics)
        {
            AddDiagnostic(
                diagnostic.Severity switch
                {
                    CompatibilityDiagnosticSeverity.Error => "Error",
                    CompatibilityDiagnosticSeverity.Warning => "Warning",
                    _ => "Info",
                },
                "Mapping review",
                diagnostic.Message,
                diagnostic.Code);
        }
    }

    private void NotifyMappingCommands()
    {
        AutoMapCommand.NotifyCanExecuteChanged();
        ValidateMappingCommand.NotifyCanExecuteChanged();
        SaveMappingProfileCommand.NotifyCanExecuteChanged();
        AddHelperOverrideCommand.NotifyCanExecuteChanged();
        RemoveHelperOverrideCommand.NotifyCanExecuteChanged();
        NotifyExportCommands();
    }

    internal static RetargetMap MergeAutoMapWithLockedRows(
        RetargetMap proposal,
        RetargetMap? current)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (current is null)
        {
            return proposal;
        }

        if (!string.Equals(
                proposal.SourceRigId,
                current.SourceRigId,
                StringComparison.Ordinal) ||
            !string.Equals(
                proposal.TargetRigId,
                current.TargetRigId,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A locked mapping can only be preserved for the same source and target rigs.",
                nameof(current));
        }

        BoneMapEntry[] preserved = current.Entries
            .Where(static entry =>
                entry.IsLocked ||
                entry.MappingKind ==
                    RetargetMappingKind.HelperOverride)
            .ToArray();
        HashSet<int> lockedBodySources = preserved
            .Where(static entry =>
                entry.MappingKind ==
                    RetargetMappingKind.Bone)
            .Select(static entry => entry.SourceBoneIndex)
            .ToHashSet();
        HashSet<int> preservedTargets = preserved
            .Select(static entry => entry.TargetBoneIndex)
            .ToHashSet();
        Dictionary<int, BoneMapEntry> currentBodyByTarget =
            current.Entries
                .Where(static entry =>
                    entry.MappingKind ==
                        RetargetMappingKind.Bone)
                .ToDictionary(
                    static entry => entry.TargetBoneIndex);
        IEnumerable<BoneMapEntry> refreshed =
            proposal.Entries
                .Where(entry =>
                    (entry.MappingKind !=
                         RetargetMappingKind.Bone ||
                     entry.Method ==
                         BoneMappingMethod.Distributed ||
                     !lockedBodySources.Contains(
                         entry.SourceBoneIndex)) &&
                    !preservedTargets.Contains(
                        entry.TargetBoneIndex))
                .Select(entry =>
                {
                    if (entry.MappingKind !=
                            RetargetMappingKind.Bone ||
                        !currentBodyByTarget.TryGetValue(
                            entry.TargetBoneIndex,
                            out BoneMapEntry? existing) ||
                        existing.SourceBoneIndex !=
                            entry.SourceBoneIndex)
                    {
                        return entry;
                    }

                    bool upgradeLegacyRotation =
                        ShouldUpgradeLegacyAutomaticRotationRow(
                            entry,
                            existing);
                    return new BoneMapEntry(
                        entry.SourceBoneIndex,
                        entry.TargetBoneIndex,
                        entry.Method,
                        entry.Confidence,
                        entry.IsLocked,
                        entry.IsReviewed,
                        RetargetMappingKind.Bone,
                        upgradeLegacyRotation
                            ? entry.TransferPolicy
                            : existing.TransferPolicy,
                        upgradeLegacyRotation
                            ? entry.ComponentPolicy
                            : existing.ComponentPolicy);
                });
        return new RetargetMap(
            proposal.SourceRigId,
            proposal.TargetRigId,
            refreshed
                .Concat(preserved)
                .OrderBy(static entry =>
                    entry.TargetBoneIndex));
    }

    private static bool ShouldUpgradeLegacyAutomaticRotationRow(
        BoneMapEntry proposal,
        BoneMapEntry existing) =>
        proposal.MappingKind == RetargetMappingKind.Bone &&
        proposal.TransferPolicy ==
            RetargetTransferPolicy.AnatomicalDirection &&
        proposal.ComponentPolicy ==
            RetargetComponentPolicy.Rotation &&
        existing.MappingKind == RetargetMappingKind.Bone &&
        (existing.TransferPolicy is
            RetargetTransferPolicy.RotationDelta or
            RetargetTransferPolicy.GlobalRotationDelta) &&
        existing.ComponentPolicy ==
            RetargetComponentPolicy.Rotation &&
        existing.Method != BoneMappingMethod.Manual &&
        !existing.IsLocked &&
        !existing.IsReviewed &&
        proposal.SourceBoneIndex == existing.SourceBoneIndex &&
        proposal.TargetBoneIndex == existing.TargetBoneIndex;

    private static ImmutableArray<ProjectTargetBindReview>
        ToProjectTargetBindReviews(
            RigDefinition target,
            RetargetMap mapping) =>
        mapping.ReviewedTargetBindBoneIndices
            .Order()
            .Select(targetBoneIndex =>
            {
                if ((uint)targetBoneIndex >=
                    (uint)target.BoneCount)
                {
                    throw new InvalidOperationException(
                        "A reviewed target-bind row is outside the loaded target rig.");
                }

                return new ProjectTargetBindReview
                {
                    TargetBoneIndex = targetBoneIndex,
                    TargetBoneName =
                        target.Bones[targetBoneIndex].Name,
                };
            })
            .ToImmutableArray();

    private static ImmutableArray<ProjectBoneMapping> ToProjectMappings(
        RigDefinition source,
        RigDefinition target,
        RetargetMap proposal) =>
        proposal.Entries
            .OrderBy(static entry => entry.TargetBoneIndex)
            .Select(entry => new ProjectBoneMapping
            {
                SourceBoneName =
                    source.Bones[entry.SourceBoneIndex].Name,
                TargetBoneName =
                    target.Bones[entry.TargetBoneIndex].Name,
                Method = entry.Method.ToString(),
                IsLocked = entry.IsLocked,
                IsReviewed = entry.IsReviewed,
                MappingKind = entry.MappingKind,
                TransferPolicy = entry.TransferPolicy,
                ComponentPolicy = entry.ComponentPolicy,
            })
            .ToImmutableArray();

    private void SetProject(
        DlraProject project,
        bool markSaved,
        bool clearHistory,
        bool clearPreview)
    {
        ArgumentNullException.ThrowIfNull(project);
        CancelRootMotionTrailJob("Project changed");
        _rootMotionTrailCache = null;
        _project = project;
        _activeAnimationId = project.ActiveAnimationId ??
            project.Animations.FirstOrDefault()?.Id;
        if (markSaved)
        {
            _savedProject = project;
        }
        else if (_savedProject is not null
                 && ReferenceEquals(_savedProject, project))
        {
            _savedProject = null;
        }

        if (clearHistory)
        {
            _undoProjects.Clear();
            _redoProjects.Clear();
        }

        if (clearPreview)
        {
            ClearIsolatedBrowsePreview();
            ClearLinkedTargetExternalView();
            _pendingExplorerAnimationSourceChoice = null;
            OnPropertyChanged(
                nameof(IsExplorerSourceModelPickerActive));
            OnPropertyChanged(
                nameof(ExplorerSourceModelPickerPrompt));
            CancelExplorerSourceModelPickerCommand
                .NotifyCanExecuteChanged();
            ClearExplorerAnimationTimingPicker();
            _sourceAnimation = null;
            _mimicAnimation = null;
            _facialFbxAnimation = null;
            _synchronizedAnimation = null;
            _targetRig = null;
            _activeRetargetMap = null;
            _targetProjectAsset = null;
            _pendingAnm2SourcePath = null;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;
            _fedDocument = null;
            _targetBaseMeshes = [];
            _sourceBaseMeshes = [];
            _indexedAssetItems = [];
            _attachmentRenderAssets.Clear();
            _attachmentStatuses.Clear();
            _lastAttachmentDiagnosticSignature = null;
            AssetBrowser.SelectedAsset = null;
            AttachmentEditor.ReplaceCatalogAssets([]);
            AttachmentEditor.ReplaceParentBones(null);
            AttachmentEditor.ReplaceBindings(
                [],
                new Dictionary<Guid, ProjectAssetReference>(),
                null);
            SkeletonRoots.Clear();
            SelectedBone = null;
            SourceViewport.SceneSource.SetScene([], null, []);
            TargetViewport.SceneSource.SetScene([], null, []);
            FacialFpp.ReplaceMorphs([]);
            FacialFpp.ReplaceMimicPresets([]);
            IkEditor.ReplaceChains([]);
            AutoMapCommand.NotifyCanExecuteChanged();
            ImportMimicAnimationCommand.NotifyCanExecuteChanged();
            ApplyFedExpressionCommand.NotifyCanExecuteChanged();
            KeyMorphPoseCommand.NotifyCanExecuteChanged();
            KeyIkConstraintCommand.NotifyCanExecuteChanged();
            AddAttachmentCommand.NotifyCanExecuteChanged();
            ApplyAttachmentCommand.NotifyCanExecuteChanged();
            RemoveAttachmentCommand.NotifyCanExecuteChanged();
        }

        LoadPreviewConfigurationFromProject(project);
        SetWorkspace(
            ResolveStartupWorkspace(project),
            preserveLegacyCutscene: false);
        RefreshProjectBindings();
        UpdateDirtyState();
        NotifyProjectChanged();
        ApplyAuthoringOverlays();
    }

    internal static EditorWorkspaceMode ResolveStartupWorkspace(
        DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        ProjectAnimation? active = project.ActiveAnimationId is { } id
            ? project.Animations.FirstOrDefault(animation =>
                animation.Id == id)
            : project.Animations.FirstOrDefault();
        if (active is null)
        {
            return EditorWorkspaceMode.Browse;
        }

        bool crossRig = !string.Equals(
            active.SourceRigSignature,
            active.TargetRigSignature,
            StringComparison.OrdinalIgnoreCase);
        bool pendingReview = crossRig &&
            (active.MappingFingerprint is null ||
             active.BoneMappings.IsEmpty ||
             active.BoneMappings.Any(static mapping =>
                 !mapping.IsReviewed));
        return pendingReview
            ? EditorWorkspaceMode.RetargetEdit
            : EditorWorkspaceMode.Animate;
    }

    private void CommitProject(DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project = NormalizeAnimationVariantGroups(project);
        Guid? activeAnimationId =
            _activeAnimationId is { } activeId &&
            project.Animations.Any(animation => animation.Id == activeId)
                ? activeId
                : project.ActiveAnimationId is { } savedActiveId &&
                  project.Animations.Any(animation =>
                      animation.Id == savedActiveId)
                    ? savedActiveId
                    : project.Animations.FirstOrDefault()?.Id;
        project = project with
        {
            ActiveAnimationId = activeAnimationId,
        };
        _activeAnimationId = activeAnimationId;
        _undoProjects.Push(_project);
        _redoProjects.Clear();
        _project = project;
        RefreshProjectBindings();
        UpdateDirtyState();
        NotifyProjectChanged();
        SyncBoneEditorFromProject();
        RefreshEditableSkeletonPreview();
    }

    internal static DlraProject NormalizeAnimationVariantGroups(
        DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets =
            project.Assets.ToDictionary(static asset => asset.Id);
        ImmutableArray<ProjectAnimation>.Builder animations =
            project.Animations.ToBuilder();
        bool changed = false;
        for (var index = 0; index < animations.Count; index++)
        {
            ProjectAnimation animation = animations[index];
            if (animation.VariantGroupId is not null ||
                animation.SourceBinding is null)
            {
                continue;
            }

            try
            {
                animations[index] = animation with
                {
                    VariantGroupId = AnimationVariantKey.CreateGroupId(
                        animation,
                        assets),
                };
                changed = true;
            }
            catch (ArgumentException)
            {
                // A legacy local document with incomplete identities remains
                // valid but intentionally cannot participate in variant reuse.
            }
        }

        return changed
            ? project with
            {
                Animations = animations.MoveToImmutable(),
            }
            : project;
    }

    private void Undo()
    {
        if (_undoProjects.Count == 0)
        {
            return;
        }

        _redoProjects.Push(_project);
        _project = _undoProjects.Pop();
        RefreshAfterHistoryMove("Undo");
    }

    private void Redo()
    {
        if (_redoProjects.Count == 0)
        {
            return;
        }

        _undoProjects.Push(_project);
        _project = _redoProjects.Pop();
        RefreshAfterHistoryMove("Redo");
    }

    private void RefreshAfterHistoryMove(string action)
    {
        _activeAnimationId = _project.ActiveAnimationId ??
            _project.Animations.FirstOrDefault()?.Id;

        LoadPreviewConfigurationFromProject(_project);
        RefreshProjectBindings();
        UpdateDirtyState();
        NotifyProjectChanged();
        SyncBoneEditorFromProject();
        RefreshEditableSkeletonPreview();
        StatusText = $"{action}: {_project.Name}";
    }

    private void RefreshProjectBindings()
    {
        _synchronizingPreviewMode = true;
        try
        {
            SelectedPreviewMode =
                _project.PreviewMode == ProjectPreviewMode.Raw
                    ? RawPreviewModeLabel
                    : Dl1ProfilePreviewModeLabel;
        }
        finally
        {
            _synchronizingPreviewMode = false;
        }
        OnPropertyChanged(nameof(ActivePreviewProfile));

        AdditionalRpackRoots.Clear();
        foreach (string root in
                 _project.Dl1Settings.AdditionalRpackRoots)
        {
            AdditionalRpackRoots.Add(root);
        }

        SelectedAdditionalRpackRoot = null;
        _synchronizingFppProjectionCapture = true;
        try
        {
            FacialFpp.LoadProjectionCapture(
                _project.Dl1Settings.UseFppProjectionCapture,
                _project.Dl1Settings.FppProjectionCapture);
            FacialFpp.ProjectionCaptureStatus =
                _project.Dl1Settings switch
                {
                    {
                        UseFppProjectionCapture: true,
                        FppProjectionCapture: not null,
                    } =>
                        "Stored user/runtime-capture inputs are enabled. They remain authoring evidence, not game validation.",
                    {
                        UseFppProjectionCapture: true,
                    } =>
                        "Capture is enabled but no complete stored input exists; FPP projection stages fail closed.",
                    _ =>
                        "No runtime-capture projection is enabled. Editor fallback values are not game validated.",
                };
        }
        finally
        {
            _synchronizingFppProjectionCapture = false;
        }

        _synchronizingMovieReferenceCameraCapture = true;
        try
        {
            FacialFpp.LoadMovieReferenceCameraCapture(
                _project.Dl1Settings.UseMovieReferenceCameraCapture,
                _project.Dl1Settings.MovieReferenceCameraCapture);
            FacialFpp.MovieReferenceCameraStatus =
                _project.Dl1Settings switch
                {
                    {
                        UseMovieReferenceCameraCapture: true,
                        MovieReferenceCameraCapture: not null,
                    } =>
                        "Stored external IBaseCamera transform and lens are enabled as movie-authoring input. They are not trusted game-validation evidence.",
                    {
                        UseMovieReferenceCameraCapture: true,
                    } =>
                        "Movie camera capture is enabled but incomplete; the DL1 movie camera stage fails closed.",
                    _ =>
                        "No external movie reference-camera snapshot is enabled.",
                };
        }
        finally
        {
            _synchronizingMovieReferenceCameraCapture = false;
        }

        ProjectAnimation? animation = GetActiveAnimation();
        OnPropertyChanged(nameof(PreviewMotionAccumulationEnabled));
        _synchronizingProjectBindings = true;
        try
        {
            SelectedRootMotionMode =
                animation?.RootMotionMode ??
                Dl1RootMotionMode.Recorded;
        }
        finally
        {
            _synchronizingProjectBindings = false;
        }

        string? selectedMappingSource =
            SelectedBoneMapping?.SourceBone;
        string? selectedMappingTarget =
            SelectedBoneMapping?.TargetBone;
        RetargetMappingKind? selectedMappingKind =
            SelectedBoneMapping?.MappingKind;
        foreach (BoneMappingViewModel row in BoneMappings)
        {
            row.PropertyChanged -= OnBoneMappingChanged;
        }
        foreach (TargetBindReviewViewModel row in
                 RequiredTargetBindReviews)
        {
            row.PropertyChanged -=
                OnTargetBindReviewChanged;
        }

        SelectedBoneMapping = null;
        BoneMappings.Clear();
        RequiredTargetBindReviews.Clear();
        if (animation is null)
        {
            Timeline.FramesPerSecond = 30.0;
            Timeline.EndFrame = 120;
            Timeline.ReplaceTracks([]);
            Timeline.ReplaceCurves([]);
        }
        else
        {
            Timeline.FramesPerSecond = animation.FrameRate.FramesPerSecond;
            Timeline.EndFrame = checked((int)Math.Min(
                int.MaxValue,
                Math.Max(1, animation.FrameCount - 1)));
            HashSet<string> representedSources =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectBoneMapping mapping in animation.BoneMappings)
            {
                BoneMappingViewModel row = new(
                    mapping.SourceBoneName,
                    mapping.TargetBoneName,
                    GetMappingConfidence(mapping.Method),
                    mapping.Method,
                    mapping.IsLocked,
                    mapping.IsReviewed,
                    mapping.MappingKind,
                    mapping.TransferPolicy,
                    mapping.ComponentPolicy);
                row.PropertyChanged += OnBoneMappingChanged;
                BoneMappings.Add(row);
                if (mapping.MappingKind ==
                    RetargetMappingKind.Bone)
                {
                    representedSources.Add(
                        mapping.SourceBoneName);
                }
            }

            if (_sourceAnimation is { } source)
            {
                foreach (BoneDefinition bone in source.Rig.Bones.Where(
                             bone => !representedSources.Contains(
                                 bone.Name)))
                {
                    BoneMappingViewModel row = new(
                        bone.Name,
                        null,
                        0.0,
                        "Unmapped");
                    row.PropertyChanged += OnBoneMappingChanged;
                    BoneMappings.Add(row);
                }
            }

            RefreshTimelineTracks();
        }

        if (animation is not null &&
            _targetRig is { } targetRig)
        {
            HashSet<int> mappedTargetBoneIndices =
                (_activeRetargetMap?.Entries ?? [])
                .Select(static entry =>
                    entry.TargetBoneIndex)
                .ToHashSet();
            HashSet<int> reviewedTargetBindBoneIndices =
                animation.TargetBindReviews
                    .Where(review =>
                        (uint)review.TargetBoneIndex <
                            (uint)targetRig.BoneCount &&
                        string.Equals(
                            targetRig.Bones[
                                review.TargetBoneIndex]
                                .Name,
                            review.TargetBoneName,
                            StringComparison.Ordinal))
                    .Select(static review =>
                        review.TargetBoneIndex)
                    .ToHashSet();
            foreach (BoneDefinition bone in targetRig.Bones.Where(
                         bone =>
                             bone.RequiredForExport &&
                             !mappedTargetBoneIndices.Contains(
                                 bone.Index)))
            {
                TargetBindReviewViewModel row = new(
                    bone.Index,
                    bone.Name,
                    bone.Kind,
                    reviewedTargetBindBoneIndices.Contains(
                        bone.Index));
                row.PropertyChanged +=
                    OnTargetBindReviewChanged;
                RequiredTargetBindReviews.Add(row);
            }
        }

        RefreshMappingAuthoringOptions();
        SelectedBoneMapping = BoneMappings.FirstOrDefault(row =>
            selectedMappingKind.HasValue &&
            string.Equals(
                row.SourceBone,
                selectedMappingSource,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                row.TargetBone,
                selectedMappingTarget,
                StringComparison.OrdinalIgnoreCase) &&
            row.MappingKind ==
                selectedMappingKind.Value);
        SelectedBoneMapping ??=
            BoneMappings.FirstOrDefault();

        if (TryAnalyzeActiveMapping(
                out RetargetMappingReviewReport? mappingReview) &&
            mappingReview is not null)
        {
            MappingReviewStatus =
                FormatMappingReviewStatus(mappingReview);
        }
        else if (_activeRetargetMap is null)
        {
            MappingReviewStatus =
                "Load a source animation and retail target to review mapping.";
        }

        RefreshBoneEditLayerItems(animation);
        RefreshFacialMappingReviews(animation);
        HashSet<string> mappedTargets = BoneMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.TargetBone))
            .Select(mapping => mapping.TargetBone!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SkeletonNodeViewModel node in EnumerateSkeletonNodes())
        {
            node.IsMapped = mappedTargets.Contains(node.Name);
        }

        AttachmentEditor.ReplaceParentBones(_targetRig);
        AttachmentEditor.ReplaceBindings(
            animation?.Attachments ?? [],
            _project.Assets.ToDictionary(
                static asset => asset.Id),
            _targetRig,
            _attachmentStatuses);
        NotifyAttachmentCommands();
        SynchronizeIkEditorLayerSettings(animation);
        BakeIkConstraintCommand.NotifyCanExecuteChanged();
        RefreshAnimationLibrary();
    }

    private void RefreshFacialMappingReviews(
        ProjectAnimation? animation)
    {
        foreach (FacialMorphBindingReviewViewModel row in
                 FacialFpp.FacialMappingReviews)
        {
            row.PropertyChanged -=
                OnFacialMappingReviewRowChanged;
        }

        FacialFpp.FacialMappingReviews.Clear();
        FacialFpp.UnmappedFacialChannels.Clear();
        if (animation is null)
        {
            _facialFbxUnmappedFingerprint = null;
            _facialFbxUnmappedChannels = [];
            FacialFpp.SelectedFacialSourceValueUnit = null;
            FacialFpp.FacialMappingReviewStatus =
                "Choose Normalized or Percent, then import a facial FBX against the active body timeline and exact retail target.";
            NotifyFacialMappingReviewCommands();
            return;
        }

        if (!string.Equals(
                _facialFbxUnmappedFingerprint,
                animation.MimicMappingFingerprint,
                StringComparison.Ordinal))
        {
            _facialFbxUnmappedFingerprint = null;
            _facialFbxUnmappedChannels = [];
        }

        foreach (ProjectMorphBinding binding in
                 animation.MorphBindings)
        {
            FacialMorphBindingReviewViewModel row =
                new(binding);
            row.PropertyChanged +=
                OnFacialMappingReviewRowChanged;
            FacialFpp.FacialMappingReviews.Add(row);
        }

        foreach (string channel in _facialFbxUnmappedChannels)
        {
            FacialFpp.UnmappedFacialChannels.Add(channel);
        }

        if (animation.FacialSourceValueUnit is
            { } facialSourceValueUnit)
        {
            FacialFpp.SelectedFacialSourceValueUnit =
                facialSourceValueUnit;
        }
        else
        {
            ProjectMorphSourceValueUnit[] persistedUnits =
                animation.MorphBindings
                    .Select(static binding =>
                        binding.SourceValueUnit)
                    .Distinct()
                    .ToArray();
            if (persistedUnits.Length == 1)
            {
                FacialFpp.SelectedFacialSourceValueUnit =
                    persistedUnits[0];
            }
        }

        if (animation.MorphBindings.IsEmpty)
        {
            FacialFpp.FacialMappingReviewStatus =
                animation.MimicProfileId is null
                    ? "No facial FBX review is stored. Choose the source value unit explicitly before import."
                    : "The facial FBX contained no mapped DL1 suggestions. Review the unmapped diagnostics or undo the import.";
        }
        else
        {
            int reviewedAndLocked =
                animation.MorphBindings.Count(
                    static binding =>
                        !binding.Enabled ||
                        binding.IsReviewed &&
                        binding.IsLocked);
            int enabled =
                animation.MorphBindings.Count(
                    static binding => binding.Enabled);
            int exportReady =
                animation.MorphBindings.Count(
                    static binding =>
                        binding.Enabled &&
                        binding.IsReviewed &&
                        binding.IsLocked);
            FacialFpp.FacialMappingReviewStatus =
                $"{animation.MorphBindings.Length:N0} suggestion(s), " +
                $"{exportReady:N0}/{enabled:N0} enabled mapping(s) " +
                "reviewed and locked. The retained FBX curves drive preview; apply row changes before mimic export." +
                (reviewedAndLocked ==
                 animation.MorphBindings.Length
                    ? " Review is complete."
                    : string.Empty);
        }

        NotifyFacialMappingReviewCommands();
    }

    private void RefreshAnimationLibrary()
    {
        Guid? selectedId = SelectedAnimationLibraryItem?.Id ??
            _activeAnimationId;
        Dictionary<Guid, ProjectAssetReference> assets = _project.Assets
            .ToDictionary(static asset => asset.Id);
        AnimationLibrary.Clear();
        Guid? previousGroupId = null;
        IEnumerable<IGrouping<Guid, ProjectAnimation>> animationGroups =
            _project.Animations
                .GroupBy(static animation =>
                    animation.VariantGroupId ?? animation.Id)
                .OrderBy(static group =>
                    group.First().Name,
                    StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<Guid, ProjectAnimation> group in animationGroups)
        foreach (ProjectAnimation animation in group.OrderBy(candidate =>
                     candidate.TargetAssetId is { } targetId &&
                     assets.TryGetValue(targetId, out ProjectAssetReference? target)
                         ? target.RetailIdentity?.ResourceName ??
                           target.ResourceId ?? string.Empty
                         : string.Empty,
                     StringComparer.OrdinalIgnoreCase))
        {
            assets.TryGetValue(
                animation.SourceAssetId,
                out ProjectAssetReference? sourceAsset);
            ProjectAssetReference? sourceModel =
                animation.SourceBinding?.RetailSourceModelAssetId is
                    { } sourceModelId &&
                assets.TryGetValue(sourceModelId, out ProjectAssetReference? model)
                    ? model
                    : null;
            ProjectAssetReference? targetModel =
                animation.TargetAssetId is { } targetId &&
                assets.TryGetValue(targetId, out ProjectAssetReference? target)
                    ? target
                    : null;
            TargetBindingStatus bindingStatus = string.Equals(
                    animation.SourceRigSignature,
                    animation.TargetRigSignature,
                    StringComparison.OrdinalIgnoreCase)
                ? TargetBindingStatus.Direct
                : animation.MappingFingerprint is not null &&
                  !animation.BoneMappings.IsEmpty &&
                  animation.BoneMappings.All(static mapping =>
                      mapping.IsReviewed)
                    ? TargetBindingStatus.Ready
                    : TargetBindingStatus.NeedsReview;
            string mappingState = bindingStatus switch
            {
                TargetBindingStatus.Direct =>
                    "Direct same-rig playback",
                TargetBindingStatus.Ready =>
                    "Reviewed cross-rig mapping",
                TargetBindingStatus.NeedsReview =>
                    "Retarget setup required",
                _ => "Target unavailable",
            };
            string diagnostics = BuildAnimationLibraryDiagnostics(animation);
            double durationSeconds = animation.FrameCount <= 1
                ? 0.0
                : animation.FrameRate.SecondsForFrame(
                    animation.FrameCount - 1);
            AnimationLibrary.Add(new AnimationLibraryItemViewModel(
                animation.Id,
                animation.Name,
                FormatProjectAssetLabel(sourceAsset),
                FormatProjectAssetLabel(sourceModel),
                FormatProjectAssetLabel(targetModel),
                animation.SourceBinding?.Roles.ToString() ??
                    "Unproven legacy source",
                $"{animation.FrameRate.Numerator}/{animation.FrameRate.Denominator} fps",
                $"{animation.FrameCount:N0} frames / {durationSeconds:0.###} s",
                mappingState,
                diagnostics,
                animation.Id == _activeAnimationId,
                animation.VariantGroupId,
                animation.Name,
                bindingStatus,
                showVariantGroupHeader:
                    previousGroupId != group.Key));
            previousGroupId = group.Key;
        }

        SelectedAnimationLibraryItem = AnimationLibrary.FirstOrDefault(item =>
            item.Id == selectedId) ??
            AnimationLibrary.FirstOrDefault(item => item.IsActive) ??
            AnimationLibrary.FirstOrDefault();
        NotifyAnimationLibraryCommands();
    }

    private static string BuildAnimationLibraryDiagnostics(
        ProjectAnimation animation)
    {
        var diagnostics = new List<string>();
        if (animation.SourceBinding is null)
        {
            diagnostics.Add("Source model is unproven; rebind required");
        }
        else
        {
            if (animation.SourceBinding.TimingProvenance ==
                AnimationTimingProvenance.Manual30FpsFallback)
            {
                diagnostics.Add("Manual 30 FPS");
            }

            if (animation.SourceBinding.Partition is { } partition)
            {
                if (!partition.AmbiguousDescriptors.IsEmpty)
                {
                    diagnostics.Add(
                        $"{partition.AmbiguousDescriptors.Length:N0} ambiguous descriptor(s)");
                }

                if (!partition.UnresolvedDescriptors.IsEmpty)
                {
                    diagnostics.Add(
                        $"{partition.UnresolvedDescriptors.Length:N0} unresolved descriptor(s)");
                }
            }
        }

        if (animation.MimicAssetId is not null)
        {
            diagnostics.Add("Separate facial source attached");
        }

        return diagnostics.Count == 0
            ? "Ready"
            : string.Join(" | ", diagnostics);
    }

    private static string FormatProjectAssetLabel(
        ProjectAssetReference? asset) =>
        asset switch
        {
            null => "Not selected",
            { RetailIdentity: { } identity } =>
                $"{identity.ResourceName} ({identity.ProviderPack})",
            _ => asset.RelativePath,
        };

    private void NotifyAnimationLibraryCommands()
    {
        ActivateSelectedAnimationCommand.NotifyCanExecuteChanged();
        RenameSelectedAnimationCommand.NotifyCanExecuteChanged();
        DuplicateSelectedAnimationCommand.NotifyCanExecuteChanged();
        RebindSelectedAnimationSourceCommand.NotifyCanExecuteChanged();
        RemoveSelectedAnimationCommand.NotifyCanExecuteChanged();
        RevealSelectedAnimationSourceCommand.NotifyCanExecuteChanged();
        AttachSelectedAnimationAsFacialCommand.NotifyCanExecuteChanged();
    }

    private void OnFacialMappingReviewRowChanged(
        object? sender,
        PropertyChangedEventArgs args) =>
        NotifyFacialMappingReviewCommands();

    private void NotifyFacialMappingReviewCommands()
    {
        ImportFacialFbxCommand.NotifyCanExecuteChanged();
        ApplyFacialMappingReviewCommand.NotifyCanExecuteChanged();
        ReviewAndLockAllFacialMappingsCommand
            .NotifyCanExecuteChanged();
    }

    private void RefreshMappingAuthoringOptions()
    {
        string? previousSource =
            SelectedHelperOverrideSourceBone;
        string? previousTarget =
            SelectedHelperOverrideTargetBone;
        MappingSourceBoneOptions.Clear();
        MappingHelperTargetOptions.Clear();
        if (_sourceAnimation is { } source)
        {
            foreach (string name in source.Rig.Bones
                         .Select(static bone => bone.Name)
                         .OrderBy(
                             static name => name,
                             StringComparer.OrdinalIgnoreCase))
            {
                MappingSourceBoneOptions.Add(name);
            }
        }

        if (_targetRig is { } target)
        {
            foreach (string name in target.Bones
                         .Where(static bone =>
                             bone.Kind is
                                 BoneKind.Helper or
                                 BoneKind.Camera or
                                 BoneKind.Prop)
                         .Select(static bone => bone.Name)
                         .OrderBy(
                             static name => name,
                             StringComparer.OrdinalIgnoreCase))
            {
                MappingHelperTargetOptions.Add(name);
            }
        }

        SelectedHelperOverrideSourceBone =
            MappingSourceBoneOptions.FirstOrDefault(name =>
                string.Equals(
                    name,
                    previousSource,
                    StringComparison.OrdinalIgnoreCase)) ??
            MappingSourceBoneOptions.FirstOrDefault();
        SelectedHelperOverrideTargetBone =
            MappingHelperTargetOptions.FirstOrDefault(name =>
                string.Equals(
                    name,
                    previousTarget,
                    StringComparison.OrdinalIgnoreCase) &&
                BoneMappings.All(row =>
                    string.IsNullOrWhiteSpace(
                        row.TargetBone) ||
                    !string.Equals(
                        row.TargetBone,
                        name,
                        StringComparison.OrdinalIgnoreCase))) ??
            MappingHelperTargetOptions.FirstOrDefault(name =>
                BoneMappings.All(row =>
                    string.IsNullOrWhiteSpace(
                        row.TargetBone) ||
                    !string.Equals(
                        row.TargetBone,
                        name,
                        StringComparison.OrdinalIgnoreCase))) ??
            MappingHelperTargetOptions.FirstOrDefault();
        AddHelperOverrideCommand.NotifyCanExecuteChanged();
        RemoveHelperOverrideCommand.NotifyCanExecuteChanged();
    }

    private void SynchronizeIkEditorLayerSettings(
        ProjectAnimation? animation)
    {
        ProjectIkLayer? layer =
            animation?.IkLayers.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.ChainName,
                    IkEditor.SelectedChain,
                    StringComparison.OrdinalIgnoreCase));
        IkEditor.BakeToEditLayer =
            layer?.BakeToEditLayer ?? false;
        if (layer is not null)
        {
            IkEditor.Weight = layer.Weight;
        }
    }

    private void RefreshBoneEditLayerItems(ProjectAnimation? animation)
    {
        Guid? selectedLayerId =
            SelectedBoneEditLayer?.Id;
        foreach (BoneEditLayerItemViewModel item in BoneEditLayers)
        {
            item.ApplyRequested -= OnBoneEditLayerApplyRequested;
        }

        SelectedBoneEditLayer = null;
        BoneEditLayers.Clear();
        if (animation is null)
        {
            return;
        }

        foreach (BoneEditLayer layer in animation.EditLayers)
        {
            BoneEditLayerItemViewModel item = new(layer);
            item.ApplyRequested += OnBoneEditLayerApplyRequested;
            item.SetSelectedBone(
                SelectedBone?.Index,
                SelectedBone?.Path);
            BoneEditLayers.Add(item);
        }

        SelectedBoneEditLayer =
            selectedLayerId.HasValue
                ? BoneEditLayers.FirstOrDefault(
                    item => item.Id ==
                        selectedLayerId.Value)
                : null;
        SelectedBoneEditLayer ??=
            BoneEditLayers.FirstOrDefault();
    }

    private void UpdateBoneLayerSelectionContext()
    {
        foreach (BoneEditLayerItemViewModel item in BoneEditLayers)
        {
            item.SetSelectedBone(
                SelectedBone?.Index,
                SelectedBone?.Path);
        }
    }

    private void OnBoneEditLayerApplyRequested(
        object? sender,
        EventArgs args)
    {
        if (sender is not BoneEditLayerItemViewModel item ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        int layerIndex = -1;
        for (int index = 0;
             index < animation.EditLayers.Length;
             index++)
        {
            if (animation.EditLayers[index].Id == item.Id)
            {
                layerIndex = index;
                break;
            }
        }
        if (layerIndex < 0)
        {
            AddDiagnostic(
                "Warning",
                "Bone layers",
                "The selected edit layer no longer exists",
                item.Name);
            return;
        }

        BoneEditLayer layer = animation.EditLayers[layerIndex];
        ImmutableDictionary<int, double> boneMask =
            item.BuildBoneMask();
        ImmutableArray<BoneEditTrack> tracks =
            item.BuildTracks(layer.Tracks);
        if (layer.Enabled == item.LayerEnabled &&
            layer.BlendMode == item.BlendMode &&
            Math.Abs(layer.Weight - item.Weight) <= 1.0e-12 &&
            TrackInterpolationsEqual(
                layer.Tracks,
                tracks) &&
            BoneMasksEqual(
                layer.BoneMask,
                boneMask))
        {
            StatusText = $"Bone layer '{item.Name}' is unchanged";
            return;
        }

        BoneEditLayer updated = new(
            layer.Id,
            layer.Name,
            item.BlendMode,
            layer.Scope,
            item.Weight,
            tracks,
            item.LayerEnabled,
            boneMask);
        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                animation with
                {
                    EditLayers = animation.EditLayers.SetItem(
                        layerIndex,
                        updated),
                }),
        });
        StatusText =
            $"Applied bone layer '{item.Name}' ({(item.LayerEnabled ? "on" : "off")}, {item.BlendMode}, {item.Weight:P0}, {boneMask.Count:N0} explicit bone mask entries)";
    }

    private static bool TrackInterpolationsEqual(
        ImmutableArray<BoneEditTrack> left,
        ImmutableArray<BoneEditTrack> right) =>
        left.Length == right.Length &&
        left.Zip(
                right,
                static (leftTrack, rightTrack) =>
                    leftTrack.BoneIndex ==
                    rightTrack.BoneIndex &&
                    leftTrack.Interpolation ==
                    rightTrack.Interpolation)
            .All(static equal => equal);

    private static bool BoneMasksEqual(
        ImmutableDictionary<int, double> left,
        ImmutableDictionary<int, double> right) =>
        left.Count == right.Count &&
        left.All(pair =>
            right.TryGetValue(
                pair.Key,
                out double value) &&
            Math.Abs(pair.Value - value) <=
                1.0e-12);

    private void OnBoneMappingChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        bool targetChanged = args.PropertyName ==
            nameof(BoneMappingViewModel.TargetBone);
        bool policyChanged = args.PropertyName is
            nameof(BoneMappingViewModel.TransferPolicy) or
            nameof(BoneMappingViewModel.ComponentPolicy);
        if (!targetChanged &&
            !policyChanged &&
            args.PropertyName is not
                nameof(BoneMappingViewModel.IsLocked) and not
                nameof(BoneMappingViewModel.IsReviewed))
        {
            return;
        }

        _ = TryPersistBoneMappings(
            sender as BoneMappingViewModel,
            targetChanged,
            policyChanged);
    }

    private void OnTargetBindReviewChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (sender is not TargetBindReviewViewModel ||
            args.PropertyName !=
                nameof(TargetBindReviewViewModel.IsReviewed))
        {
            return;
        }

        SaveReviewedMapping();
    }

    private bool TryPersistBoneMappings(
        BoneMappingViewModel? changedRow,
        bool mappingIdentityChanged,
        bool policyChanged)
    {
        if (
            _sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return false;
        }

        try
        {
            List<ProjectBoneMapping> rows = [];
            HashSet<string> targets =
                new(StringComparer.OrdinalIgnoreCase);
            HashSet<int> mappedTargetBoneIndices = [];
            foreach (BoneMappingViewModel row in BoneMappings)
            {
                if (string.IsNullOrWhiteSpace(row.TargetBone))
                {
                    continue;
                }

                int sourceIndex =
                    source.Rig.GetBoneIndex(row.SourceBone);
                int targetIndex =
                    target.GetBoneIndex(row.TargetBone);
                if (sourceIndex < 0 || targetIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Mapping '{row.SourceBone}' -> '{row.TargetBone}' does not name bones in the loaded rigs.");
                }

                if (row.MappingKind ==
                        RetargetMappingKind.HelperOverride &&
                    target.Bones[targetIndex].Kind is not (
                        BoneKind.Helper or
                        BoneKind.Camera or
                        BoneKind.Prop))
                {
                    throw new InvalidOperationException(
                        $"Helper override '{row.SourceBone}' -> '{row.TargetBone}' must target a helper, camera, or prop node.");
                }

                if (!targets.Add(
                        target.Bones[targetIndex].Name))
                {
                    throw new InvalidOperationException(
                        $"Target bone '{target.Bones[targetIndex].Name}' is mapped more than once.");
                }

                mappedTargetBoneIndices.Add(targetIndex);
                rows.Add(new ProjectBoneMapping
                {
                    SourceBoneName =
                        source.Rig.Bones[sourceIndex].Name,
                    TargetBoneName =
                        target.Bones[targetIndex].Name,
                    Method = ReferenceEquals(
                                 row,
                                 changedRow) &&
                             (mappingIdentityChanged ||
                              policyChanged)
                        ? BoneMappingMethod.Manual.ToString()
                        : row.Status,
                    IsLocked = row.IsLocked,
                    IsReviewed =
                        ReferenceEquals(
                            row,
                            changedRow) &&
                        (mappingIdentityChanged ||
                         policyChanged)
                            ? false
                            : row.IsReviewed,
                    MappingKind = row.MappingKind,
                    TransferPolicy =
                        row.TransferPolicy,
                    ComponentPolicy =
                        row.ComponentPolicy,
                });
            }

            ImmutableArray<ProjectTargetBindReview>
                targetBindReviews =
                    RetainUnmappedTargetBindReviews(
                        animation.TargetBindReviews,
                        mappedTargetBoneIndices);
            RetargetMap map = ToRetargetMap(
                source.Rig,
                target,
                rows,
                targetBindReviews);
            _activeRetargetMap = map;
            ProjectAnimation updated = animation with
            {
                BoneMappings = rows.ToImmutableArray(),
                TargetBindReviews = targetBindReviews,
                SourceRigSignature =
                    RigSignature.Compute(source.Rig),
                TargetRigSignature =
                    RigSignature.Compute(target),
                MappingFingerprint =
                    RetargetMapFingerprint.Compute(
                        RigSignature.Compute(source.Rig),
                        RigSignature.Compute(target),
                        _targetProjectAsset?.ContentSha256,
                        map),
            };
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updated),
            });
            PublishMappingProposal(map);
            RefreshAnimationPreview();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            AddDiagnostic(
                "Error",
                "Retargeting",
                "Manual mapping was rejected",
                exception.Message);
            RefreshProjectBindings();
            return false;
        }
    }

    internal static ImmutableArray<ProjectTargetBindReview>
        RetainUnmappedTargetBindReviews(
            IEnumerable<ProjectTargetBindReview>
                existingReviews,
            IEnumerable<int> mappedTargetBoneIndices)
    {
        ArgumentNullException.ThrowIfNull(existingReviews);
        ArgumentNullException.ThrowIfNull(
            mappedTargetBoneIndices);
        HashSet<int> mapped =
            mappedTargetBoneIndices.ToHashSet();
        return existingReviews
            .Where(review =>
                !mapped.Contains(
                    review.TargetBoneIndex))
            .ToImmutableArray();
    }

    private static RetargetMap ToRetargetMap(
        RigDefinition source,
        RigDefinition target,
        IEnumerable<ProjectBoneMapping> mappings,
        IEnumerable<ProjectTargetBindReview>? targetBindReviews = null)
    {
        RetargetMap automaticProposal =
            RetargetMapBuilder.CreateSuggested(
                source,
                target);
        Dictionary<int, BoneMapEntry> proposedByTarget =
            automaticProposal.Entries.ToDictionary(
                static entry => entry.TargetBoneIndex);
        return new RetargetMap(
            source.Id,
            target.Id,
            mappings.Select(mapping =>
            {
                int sourceIndex =
                    source.GetBoneIndex(mapping.SourceBoneName);
                int targetIndex =
                    target.GetBoneIndex(mapping.TargetBoneName);
                if (sourceIndex < 0 || targetIndex < 0)
                {
                    throw new InvalidOperationException(
                        "A saved mapping row does not exist in the loaded rig pair.");
                }

                BoneMappingMethod method = Enum.TryParse(
                    mapping.Method,
                    ignoreCase: true,
                    out BoneMappingMethod parsed)
                    ? parsed
                    : BoneMappingMethod.Manual;
                BoneMapEntry saved = new(
                    sourceIndex,
                    targetIndex,
                    method,
                    GetMappingConfidence(method.ToString()),
                    mapping.IsLocked,
                    mapping.IsReviewed,
                    mapping.MappingKind,
                    mapping.TransferPolicy,
                    mapping.ComponentPolicy);
                if (proposedByTarget.TryGetValue(
                        targetIndex,
                        out BoneMapEntry? proposed) &&
                    ShouldUpgradeLegacyAutomaticRotationRow(
                        proposed,
                        saved))
                {
                    return new BoneMapEntry(
                        saved.SourceBoneIndex,
                        saved.TargetBoneIndex,
                        saved.Method,
                        saved.Confidence,
                        saved.IsLocked,
                        saved.IsReviewed,
                        saved.MappingKind,
                        proposed.TransferPolicy,
                        proposed.ComponentPolicy);
                }

                return saved;
            }),
            (targetBindReviews ?? [])
                .Select(review =>
                {
                    if ((uint)review.TargetBoneIndex >=
                            (uint)target.BoneCount ||
                        !string.Equals(
                            target.Bones[review.TargetBoneIndex].Name,
                            review.TargetBoneName,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            "A saved target-bind review does not match the loaded target rig.");
                    }

                    return review.TargetBoneIndex;
                }));
    }

    private void NotifyProjectChanged()
    {
        OnPropertyChanged(nameof(CurrentProject));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ActiveAnimationLabel));
        OnPropertyChanged(nameof(CanOpenAnimateWorkspace));
        OnPropertyChanged(nameof(AnimateWorkspaceHint));
        UpdateFidelityStatusBadges();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyMappingCommands();
        ApplyFedExpressionCommand.NotifyCanExecuteChanged();
        KeyMorphPoseCommand.NotifyCanExecuteChanged();
        KeyIkConstraintCommand.NotifyCanExecuteChanged();
        NotifyAttachmentCommands();
    }

    private static double GetMappingConfidence(string method) =>
        Enum.TryParse(
            method,
            ignoreCase: true,
            out BoneMappingMethod parsed)
            ? parsed switch
            {
                BoneMappingMethod.DescriptorHash => 1.0,
                BoneMappingMethod.ExactName => 1.0,
                BoneMappingMethod.NormalizedName => 0.95,
                BoneMappingMethod.Semantic => 0.9,
                BoneMappingMethod.Structural => 0.7,
                BoneMappingMethod.Manual => 1.0,
                BoneMappingMethod.Composed => 0.75,
                BoneMappingMethod.Distributed => 0.75,
                _ => 0.0,
            }
            : 0.0;

    private void UpdateDirtyState()
    {
        IsDirty = _savedProject is null
            || !ReferenceEquals(_project, _savedProject);
    }

    private void AddRecentProjectPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? existing = RecentProjectPaths.FirstOrDefault(candidate =>
            string.Equals(
                candidate,
                fullPath,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            RecentProjectPaths.Remove(existing);
        }

        RecentProjectPaths.Insert(0, fullPath);
        while (RecentProjectPaths.Count > 12)
        {
            RecentProjectPaths.RemoveAt(RecentProjectPaths.Count - 1);
        }
    }

    private void ApplySkeletonVisibility()
    {
        bool showSkeleton = ShowSkeletonOverlay;
        SourceViewport.SceneSource.SetSkeletonVisibility(
            showSkeleton && ShowDeformBones,
            showSkeleton && ShowHelpers,
            showSkeleton && ShowCameraHelpers,
            showSkeleton && ShowPropHelpers);
        TargetViewport.SceneSource.SetSkeletonVisibility(
            showSkeleton && ShowDeformBones,
            showSkeleton && ShowHelpers,
            showSkeleton && ShowCameraHelpers,
            showSkeleton && ShowPropHelpers);
        StatusText = showSkeleton
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Skeleton overlay: bones {(ShowDeformBones ? "on" : "off")}, helpers {(ShowHelpers ? "on" : "off")}, cameras {(ShowCameraHelpers ? "on" : "off")}, props/pivots {(ShowPropHelpers ? "on" : "off")}")
            : "Skeleton overlay hidden; per-role visibility retained";
    }

    private void ApplyAuthoringOverlays()
    {
        var options = new RenderAuthoringOverlayOptions(
            ShowRootMotionTrail,
            ShowDeformedBounds,
            ShowBoneLocalAxes,
            HighlightSelectedMeshes);
        RootMotionTrailRenderData? targetTrail = null;
        if (ShowRootMotionTrail &&
            _rootMotionTrailCache is { } cache &&
            IsRootMotionTrailCacheCurrent(cache))
        {
            int currentSampleIndex =
                ResolveRootMotionTrailSampleIndex(
                    Timeline.CurrentFrame,
                    cache.FrameCount,
                    cache.WorldPositions.Length);
            targetTrail = new RootMotionTrailRenderData(
                cache.WorldPositions,
                currentSampleIndex);
        }

        SourceViewport.SceneSource.SetAuthoringOverlays(
            new RenderAuthoringOverlayState(
                options with
                {
                    ShowRootMotionTrail = false,
                },
                null));
        TargetViewport.SceneSource.SetAuthoringOverlays(
            new RenderAuthoringOverlayState(
                options,
                targetTrail));
    }

    private void EnsureRootMotionTrail()
    {
        if (!ShowRootMotionTrail || _disposed)
        {
            return;
        }

        RootMotionTrailBuildSnapshot? snapshot;
        try
        {
            snapshot = CreateRootMotionTrailBuildSnapshot();
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            OverflowException)
        {
            CancelRootMotionTrailJob("Unavailable");
            _ = SetProperty(
                ref _showRootMotionTrail,
                false,
                nameof(ShowRootMotionTrail));
            StatusText =
                $"Root-motion trail unavailable: {exception.Message}";
            ApplyAuthoringOverlays();
            return;
        }

        if (snapshot is null)
        {
            CancelRootMotionTrailJob("Waiting for animation");
            ApplyAuthoringOverlays();
            return;
        }

        if (_rootMotionTrailCache is { } cache &&
            IsRootMotionTrailCacheCurrent(cache))
        {
            return;
        }

        if (_rootMotionTrailJob is { IsCancellable: true } &&
            _rootMotionTrailBuildSnapshot is { } active &&
            IsSameRootMotionTrailSource(active, snapshot))
        {
            return;
        }

        CancelRootMotionTrailJob("Superseded");
        int generation = ++_rootMotionTrailGeneration;
        JobViewModel job = AddJob(
            "Build root-motion trail",
            "Authoritative evaluation",
            $"Sampling {snapshot.SampleCount:N0} poses");
        _rootMotionTrailJob = job;
        _rootMotionTrailBuildSnapshot = snapshot;
        IProgress<double> progress = new Progress<double>(
            value =>
            {
                if (ReferenceEquals(
                        _rootMotionTrailJob,
                        job))
                {
                    job.Progress = value;
                }
            });
        CancellationToken cancellationToken =
            job.CancellationToken;
        Task<Vector3[]> worker = Task.Run(
            () => EvaluateRootMotionTrail(
                snapshot,
                cancellationToken,
                progress),
            cancellationToken);
        lock (_rootMotionTrailWorkerGate)
        {
            _rootMotionTrailWorkers.Add(worker);
            _rootMotionTrailWorkerTask = worker;
        }

        _ = CompleteRootMotionTrailBuildAsync(
            snapshot,
            generation,
            job,
            worker);
    }

    private RootMotionTrailBuildSnapshot?
        CreateRootMotionTrailBuildSnapshot()
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            GetActiveAnimation() is not { } animation)
        {
            return null;
        }

        bool directSameRig = HasSameRigContract(source.Rig, target);
        RetargetMap? mapping = directSameRig
            ? null
            : _activeRetargetMap;
        if (!directSameRig && mapping is null)
        {
            return null;
        }

        AnimationClip evaluationClip =
            ResolveSynchronizedAnimation(animation, source);
        int sampleCount = checked((int)Math.Min(
            evaluationClip.FrameCount,
            AuthoritativeRootMotionTrailSampler.MaximumSampleCount));
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source.Rig,
            target,
            mapping,
            animation.RootMotionMode switch
            {
                Dl1RootMotionMode.Recorded =>
                    AnimationRootMode.Recorded,
                Dl1RootMotionMode.InPlace =>
                    AnimationRootMode.InPlace,
                Dl1RootMotionMode.Bip01 =>
                    AnimationRootMode.Bip01,
                Dl1RootMotionMode.MotionAccumulator =>
                    AnimationRootMode.MotionAccumulator,
                _ => throw new InvalidDataException(
                    "The project contains an unknown DL1 root-motion mode."),
            });
        var cacheKey = new RootMotionTrailCacheKey(
            _activeAnimationId,
            source,
            target,
            mapping,
            animation,
            evaluationClip,
            sampleCount);
        return new RootMotionTrailBuildSnapshot(
            cacheKey,
            source,
            target,
            mapping,
            animation,
            evaluationClip,
            policy,
            BuildIkLayers(animation, target),
            sampleCount);
    }

    private async Task CompleteRootMotionTrailBuildAsync(
        RootMotionTrailBuildSnapshot snapshot,
        int generation,
        JobViewModel job,
        Task<Vector3[]> worker)
    {
        try
        {
            Vector3[] positions = await worker;
            if (_disposed ||
                generation != _rootMotionTrailGeneration ||
                !ShowRootMotionTrail ||
                !IsRootMotionTrailBuildCurrent(snapshot))
            {
                job.Complete("Superseded");
                return;
            }

            _rootMotionTrailCache = new RootMotionTrailCache(
                snapshot.CacheKey,
                snapshot.EvaluationClip.FrameCount,
                positions.ToImmutableArray());
            job.Progress = 100.0;
            job.Complete("Complete");
            ApplyAuthoringOverlays();
            StatusText =
                $"Root-motion trail ready ({positions.Length:N0} authoritative samples)";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            OverflowException)
        {
            job.Complete("Failed");
            if (!_disposed &&
                generation == _rootMotionTrailGeneration)
            {
                _ = SetProperty(
                    ref _showRootMotionTrail,
                    false,
                    nameof(ShowRootMotionTrail));
                ApplyAuthoringOverlays();
                AddDiagnostic(
                    "Error",
                    "Root motion",
                    "The root-motion trail could not be evaluated",
                    exception.Message);
                StatusText = "Root-motion trail evaluation failed";
            }
        }
        finally
        {
            lock (_rootMotionTrailWorkerGate)
            {
                _rootMotionTrailWorkers.Remove(worker);
                if (ReferenceEquals(
                        _rootMotionTrailWorkerTask,
                        worker))
                {
                    _rootMotionTrailWorkerTask = null;
                }
            }

            if (ReferenceEquals(_rootMotionTrailJob, job))
            {
                _rootMotionTrailJob = null;
                _rootMotionTrailBuildSnapshot = null;
            }
        }
    }

    private static Vector3[] EvaluateRootMotionTrail(
        RootMotionTrailBuildSnapshot snapshot,
        CancellationToken cancellationToken,
        IProgress<double> progress)
    {
        ImmutableArray<MorphChannelBinding> morphBindings =
            ProjectMorphBindingResolver.Resolve(
                snapshot.Animation.MorphBindings,
                snapshot.Target,
                ProjectMorphBindingResolutionMode.Preview);
        return AuthoritativeRootMotionTrailSampler.Evaluate(
            new AuthoritativeRootMotionTrailRequest(
                snapshot.Source.Rig,
                snapshot.Target,
                snapshot.EvaluationClip,
                snapshot.Mapping,
                snapshot.Animation.EditLayers,
                snapshot.Animation.Attachments,
                snapshot.AuthoringPolicy,
                morphBindings,
                snapshot.Animation.MorphEditLayers,
                snapshot.IkLayers,
                snapshot.SampleCount,
                snapshot.Animation.PreviewMotionAccumulationEnabled),
            progress,
            cancellationToken);
    }

    private void CancelRootMotionTrailJob(string state)
    {
        _rootMotionTrailGeneration++;
        JobViewModel? job = _rootMotionTrailJob;
        _rootMotionTrailJob = null;
        _rootMotionTrailBuildSnapshot = null;
        if (job is null)
        {
            return;
        }

        if (job.IsCancellable)
        {
            job.Cancel();
        }

        job.Complete(state);
    }

    private bool IsRootMotionTrailCacheCurrent(
        RootMotionTrailCache cache) =>
        IsRootMotionTrailCacheKeyCurrent(cache.Key);

    private bool IsRootMotionTrailBuildCurrent(
        RootMotionTrailBuildSnapshot snapshot) =>
        IsRootMotionTrailCacheKeyCurrent(snapshot.CacheKey);

    private static bool IsSameRootMotionTrailSource(
        RootMotionTrailBuildSnapshot first,
        RootMotionTrailBuildSnapshot second) =>
        IsSameRootMotionTrailCacheKey(
            first.CacheKey,
            second.CacheKey);

    private bool IsRootMotionTrailCacheKeyCurrent(
        RootMotionTrailCacheKey key)
    {
        RetargetMap? expectedMapping =
            HasSameRigContract(key.Source.Rig, key.Target)
                ? null
                : _activeRetargetMap;
        if (key.ActiveAnimationId != _activeAnimationId ||
            !ReferenceEquals(key.Source, _sourceAnimation) ||
            !ReferenceEquals(key.Target, _targetRig) ||
            !ReferenceEquals(key.Mapping, expectedMapping) ||
            GetActiveAnimation() is not { } animation ||
            !ReferenceEquals(key.Animation, animation))
        {
            return false;
        }

        AnimationClip? evaluationClip;
        if (animation.MimicAssetId is { } mimicAssetId)
        {
            evaluationClip =
                _mimicAnimation is { } mimic &&
                mimic.AssetId == mimicAssetId
                    ? _synchronizedAnimation
                    : null;
        }
        else if (animation.FacialSourceAssetId is
        { } facialSourceAssetId)
        {
            evaluationClip =
                _facialFbxAnimation is { } facial &&
                facial.AssetId == facialSourceAssetId &&
                facial.SourceValueUnit ==
                    animation.FacialSourceValueUnit
                    ? _synchronizedAnimation
                    : null;
        }
        else
        {
            evaluationClip = key.Source.Clip;
        }
        int sampleCount = evaluationClip is null
            ? 0
            : checked((int)Math.Min(
                evaluationClip.FrameCount,
                AuthoritativeRootMotionTrailSampler.MaximumSampleCount));
        return ReferenceEquals(
                key.EvaluationClip,
                evaluationClip) &&
            key.SampleCount == sampleCount;
    }

    private static bool IsSameRootMotionTrailCacheKey(
        RootMotionTrailCacheKey first,
        RootMotionTrailCacheKey second) =>
        first.ActiveAnimationId == second.ActiveAnimationId &&
        ReferenceEquals(first.Source, second.Source) &&
        ReferenceEquals(first.Target, second.Target) &&
        ReferenceEquals(first.Mapping, second.Mapping) &&
        ReferenceEquals(first.Animation, second.Animation) &&
        ReferenceEquals(
            first.EvaluationClip,
            second.EvaluationClip) &&
        first.SampleCount == second.SampleCount;

    private static int ResolveRootMotionTrailSampleIndex(
        double currentFrame,
        long frameCount,
        int sampleCount)
    {
        if (sampleCount <= 0)
        {
            return -1;
        }

        if (sampleCount == 1 || frameCount <= 1)
        {
            return 0;
        }

        double normalized = Math.Clamp(
            currentFrame / (frameCount - 1),
            0.0,
            1.0);
        return Math.Clamp(
            checked((int)Math.Round(
                normalized * (sampleCount - 1),
                MidpointRounding.AwayFromZero)),
            0,
            sampleCount - 1);
    }

    private void ResetCamera()
    {
        _viewportCoordinator.UpdateCamera(
            ViewportSide.Source,
            RenderCamera.Default with
            {
                VerticalFieldOfViewDegrees = FacialFpp.FieldOfView,
                NearPlane = FacialFpp.NearPlane,
            });
        if (!IsViewportsLinked)
        {
            _viewportCoordinator.UpdateCamera(
                ViewportSide.Target,
                RenderCamera.Default with
                {
                    VerticalFieldOfViewDegrees = FacialFpp.FieldOfView,
                    NearPlane = FacialFpp.NearPlane,
                });
        }

        StatusText = "Viewport camera reset";
    }

    private void StoreFppProjectionCapture()
    {
        Dl1FppProjectionCapture? capture =
            _project.Dl1Settings.FppProjectionCapture;
        if (FacialFpp.UseProjectionCapture &&
            !FacialFpp.TryCreateProjectionCapture(
                out capture,
                out string? error))
        {
            FacialFpp.ProjectionCaptureStatus =
                $"Capture inputs are incomplete or invalid: {error}";
            StatusText =
                "FPP runtime-capture projection was not stored";
            return;
        }

        DlraProject updated = _project with
        {
            Dl1Settings = _project.Dl1Settings with
            {
                UseFppProjectionCapture =
                    FacialFpp.UseProjectionCapture,
                FppProjectionCapture = capture,
            },
        };
        updated.Validate();
        CommitProject(updated);
        FacialFpp.ProjectionCaptureStatus =
            FacialFpp.UseProjectionCapture
                ? "Stored runtime-capture inputs are enabled for FPP preview. This remains a DL1 profile until a matching game capture is validated."
                : "Stored runtime-capture inputs are disabled; the editor fallback lens is not game validated.";
        StatusText = "FPP projection capture settings stored in project";
        RefreshAnimationPreview();
    }

    private void StoreMovieReferenceCameraCapture()
    {
        Dl1MovieReferenceCameraCapture? capture =
            _project.Dl1Settings.MovieReferenceCameraCapture;
        if (FacialFpp.UseMovieReferenceCameraCapture &&
            !FacialFpp.TryCreateMovieReferenceCameraCapture(
                out capture,
                out string? error))
        {
            FacialFpp.MovieReferenceCameraStatus =
                $"Movie camera inputs are incomplete or invalid: {error}";
            StatusText =
                "Movie reference-camera capture was not stored";
            return;
        }

        DlraProject updated = _project with
        {
            Dl1Settings = _project.Dl1Settings with
            {
                UseMovieReferenceCameraCapture =
                    FacialFpp.UseMovieReferenceCameraCapture,
                MovieReferenceCameraCapture = capture,
            },
        };
        updated.Validate();
        CommitProject(updated);
        FacialFpp.MovieReferenceCameraStatus =
            FacialFpp.UseMovieReferenceCameraCapture
                ? "Stored external IBaseCamera transform and lens are enabled for movie preview. This is authoring input, not trusted game-validation evidence."
                : "Stored external movie reference-camera input is disabled.";
        StatusText =
            "Movie reference-camera settings stored in project";
        RefreshAnimationPreview();
    }

    private void FrameSelection()
    {
        bool targetCameraLocked =
            _viewportCoordinator.HasTargetPreviewCameraOverride;
        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot sourceFrame =
            SourceViewport.SceneSource.CaptureFrame();
        ViewportSide side =
            !targetCameraLocked &&
            HasFrameableContent(targetFrame)
                ? ViewportSide.Target
                : ViewportSide.Source;
        RenderFrameSnapshot frame =
            side == ViewportSide.Target
                ? targetFrame
                : sourceFrame;
        if (!RenderCameraFraming.TryFrame(
                frame,
                out RenderCamera camera))
        {
            AddDiagnostic(
                "Info",
                "Viewport",
                "Nothing is available to frame",
                "Load a retail mesh or animation skeleton before framing the viewport.");
            StatusText = "Nothing is available to frame";
            return;
        }

        _viewportCoordinator.UpdateCamera(side, camera);
        StatusText = side switch
        {
            ViewportSide.Target =>
                "Framed the decoded DL1 target",
            _ when SourceViewport.SceneSource
                .HasExternalPreviewScene =>
                "Framed the external view of the evaluated DL1 target",
            _ => "Framed the authored source",
        };
    }

    private void FrameSelectedAttachment()
    {
        TryFrameSelectedAttachment(reportFailure: true);
    }

    private bool TryFrameSelectedAttachment(bool reportFailure)
    {
        AttachmentItemViewModel? selected =
            AttachmentEditor.SelectedAttachment;
        if (selected is null)
        {
            if (reportFailure)
            {
                StatusText =
                    "Select a document attachment before framing it";
            }

            return false;
        }

        RenderFrameSnapshot frame =
            TargetViewport.SceneSource.CaptureFrame();
        MeshRenderData[] attachmentMeshes = frame.Meshes
            .Where(mesh =>
                IsAttachmentMeshForBinding(
                    mesh,
                    selected.Id))
            .ToArray();
        if (attachmentMeshes.Length == 0)
        {
            if (reportFailure)
            {
                AddDiagnostic(
                    "Info",
                    "Attachments",
                    $"'{selected.Name}' has no visible decoded surfaces to frame",
                    "Check its per-resource status in the attachment list. Missing or unsupported retail data is never substituted.");
                StatusText =
                    $"Attachment {selected.Name} is not renderable";
            }

            return false;
        }

        RenderFrameSnapshot attachmentFrame = frame with
        {
            Meshes = attachmentMeshes,
            Skeleton = null,
            Gizmos = [],
        };
        if (!RenderCameraFraming.TryFrame(
                attachmentFrame,
                out RenderCamera camera))
        {
            if (reportFailure)
            {
                StatusText =
                    $"Attachment {selected.Name} has no finite bounds";
            }

            return false;
        }

        _viewportCoordinator.UpdateCamera(
            ViewportSide.Target,
            camera);
        StatusText =
            _viewportCoordinator.HasTargetPreviewCameraOverride
                ? $"Framed {selected.Name} in the orbit camera; disable the active FPP/movie camera to see that view"
                : $"Framed attachment {selected.Name}";
        return true;
    }

    private bool CanFrameSelectedAttachment() =>
        !IsBusy &&
        AttachmentEditor.SelectedAttachment is { } selected &&
        TargetViewport.SceneSource
            .CaptureFrame()
            .Meshes
            .Any(mesh =>
                IsAttachmentMeshForBinding(
                    mesh,
                    selected.Id));

    private static bool IsAttachmentMeshForBinding(
        MeshRenderData mesh,
        Guid bindingId) =>
        mesh.Id.StartsWith(
            $"attachment/{bindingId:N}/",
            StringComparison.Ordinal);

    private static bool HasFrameableContent(
        RenderFrameSnapshot frame) =>
        frame.Meshes.Count > 0 ||
        frame.Skeleton is { Bones.Count: > 0 } ||
        frame.Gizmos.Count > 0;

    private async void OnIndexGameRequested(object? sender, EventArgs args)
    {
        await InitializeAssetCatalogAsync();
    }

    private async Task LoadAssetCatalogAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_assetProfileScanJob is { IsCancellable: true } profileJob)
        {
            profileJob.Cancel();
        }

        AssetBrowser.SetCatalogLoading(true);
        JobViewModel job = AddJob(
            "Load Dying Light 1 asset catalog",
            "Discovery",
            "Opening saved catalog");
        var progress = new Progress<Dl1AssetIndexProgress>(update =>
        {
            job.Stage = update.Stage;
            job.Progress = update.Percent;
            job.State = update.Detail;
            StatusText = update.Detail;
        });

        try
        {
            IReadOnlyList<string> additionalRpackRoots =
                ProjectRetailRootResolver.ResolveAdditionalRpackRoots(
                    _project,
                    ProjectPath);
            Dl1AssetIndexResult result =
                await _assetWorkspace.IndexSteamInstallAsync(
                    progress,
                    additionalRpackRoots,
                    job.CancellationToken);
            job.Stage = "Publish";
            job.State = "Building asset browser rows";
            AssetItemViewModel[] assets = await Task.Run(
                () => result.Catalog.Assets
                    .Select(CreateAssetItem)
                    .ToArray(),
                job.CancellationToken);
            if (_disposed)
            {
                return;
            }

            _indexedAssetItems = assets;
            AssetBrowser.ReplaceAssets(assets);
            AttachmentEditor.ReplaceCatalogAssets(assets);
            ProjectAnimation? activeAnimation = GetActiveAnimation();
            bool hasBoundAnm2 = activeAnimation?.SourceBinding?.Kind is
                AnimationSourceKind.LocalAnm2 or
                AnimationSourceKind.RetailAnm2;
            if (hasBoundAnm2)
            {
                job.Stage = "Animation source";
                job.State =
                    "Resolving the active ANM2's immutable source model";
                await ActivateAnimationAsync(
                    activeAnimation!.Id,
                    beginPlayback: false,
                    persistActivation: false);
            }
            else
            {
                RestoreSavedRetailTargetSelection(assets);
            }
            job.Stage = "Attachments";
            job.State = "Resolving saved prop and weapon bindings";
            await RestoreProjectAttachmentsAsync(
                assets,
                job.CancellationToken);
            job.Progress = 100.0;
            job.Complete("Complete");
            string catalogSource =
                result.Catalog.WasRestoredFromPersistentIndex
                    ? "the validated local cache"
                    : "a fresh retail scan";
            StatusText =
                $"Loaded {assets.Length:N0} Dying Light 1 assets from {catalogSource}";
            AddDiagnostic(
                "Info",
                "Assets",
                result.Catalog.WasRestoredFromPersistentIndex
                    ? "Saved Dying Light 1 asset catalog loaded"
                    : "Dying Light 1 asset catalog refreshed",
                $"{assets.Length:N0} resolved assets from {catalogSource}; {result.Catalog.Conflicts.Count:N0} precedence conflicts retained in the catalog.");
            foreach (Dl1RetailProviderDiagnostic diagnostic in
                     result.ProviderDiagnostics)
            {
                AddDiagnostic(
                    "Warning",
                    "Assets",
                    diagnostic.Message,
                    $"{diagnostic.Code}: {diagnostic.Path}");
            }

            foreach (RpackProviderError sourceError in
                     result.RpackSourceErrors)
            {
                string resource = sourceError.ResourceIndex is { } index
                    ? $" resource {index:N0} ({sourceError.ResourceName})"
                    : string.Empty;
                AddDiagnostic(
                    "Error",
                    "Assets",
                    $"RPack source failed locally:{resource}",
                    $"{sourceError.Path}: {sourceError.ErrorType}: {sourceError.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Dying Light 1 asset catalog loading canceled";
        }
        catch (Exception exception)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Assets",
                "Dying Light 1 asset catalog could not be loaded",
                exception.Message);
            StatusText = "Dying Light 1 asset catalog loading failed";
        }
        finally
        {
            AssetBrowser.SetCatalogLoading(false);
        }
    }

    private async void OnProfileScanRequested(
        object? sender,
        AssetProfileScanRequestedEventArgs args)
    {
        if (_disposed || _assetProfileScanJob is not null)
        {
            AssetBrowser.SetProfileScanRunning(
                false,
                _disposed
                    ? "Profile classification stopped because the workspace closed."
                    : "A profile classification batch is already running.");
            return;
        }

        JobViewModel job = AddJob(
            "Classify filtered DL1 retail meshes",
            "Mesh profiles",
            $"0 of {args.Assets.Count:N0}");
        _assetProfileScanJob = job;
        AssetBrowser.SetProfileScanRunning(
            true,
            $"Classifying {args.Assets.Count:N0} filtered mesh rows in the background...");
        int classified = 0;
        int failed = 0;
        try
        {
            for (int index = 0; index < args.Assets.Count; index++)
            {
                job.CancellationToken.ThrowIfCancellationRequested();
                AssetItemViewModel item = args.Assets[index];
                if (item.RetailAsset is not { } retailAsset ||
                    item.Kind != AssetKind.Mesh ||
                    item.MeshProfile is not null)
                {
                    continue;
                }

                item.MarkProfileClassifying();
                job.State =
                    $"{index + 1:N0} of {args.Assets.Count:N0}: {item.Name}";
                job.Progress =
                    100.0 * index / Math.Max(1, args.Assets.Count);
                AssetBrowser.SetProfileScanRunning(
                    true,
                    $"Classifying {item.Name} ({index + 1:N0} of {args.Assets.Count:N0})");
                try
                {
                    Dl1RetailMeshProfile profile =
                        await _assetWorkspace.ClassifyMeshAsync(
                            retailAsset,
                            job.CancellationToken);
                    if (_disposed)
                    {
                        return;
                    }

                    item.ApplyMeshProfile(profile);
                    AssetBrowser.NotifyProfileChanged(item);
                    classified++;
                }
                catch (OperationCanceledException)
                    when (job.CancellationToken.IsCancellationRequested)
                {
                    item.ResetProfileClassifying();
                    throw;
                }
                catch (Exception exception)
                {
                    item.MarkProfileFailed(exception.Message);
                    AssetBrowser.NotifyProfileChanged(item);
                    failed++;
                    AddDiagnostic(
                        "Error",
                        "Mesh profiles",
                        $"Could not classify {item.Name}",
                        $"{retailAsset.Id.StableKey}: {exception.Message}");
                }
            }

            job.Progress = 100.0;
            job.Complete(
                failed == 0
                    ? "Complete"
                    : $"Complete with {failed:N0} local failure(s)");
            string status =
                $"Classified {classified:N0} mesh profile(s)";
            if (failed > 0)
            {
                status += $"; {failed:N0} remained unknown after local failures";
            }

            AssetBrowser.SetProfileScanRunning(false, status);
            StatusText = status;
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            string status =
                $"Mesh profile classification canceled after {classified:N0} completed row(s)";
            AssetBrowser.SetProfileScanRunning(false, status);
            StatusText = status;
        }
        finally
        {
            if (ReferenceEquals(_assetProfileScanJob, job))
            {
                _assetProfileScanJob = null;
            }

            if (AssetBrowser.IsProfileScanRunning)
            {
                AssetBrowser.SetProfileScanRunning(
                    false,
                    $"Profile classification stopped after {classified:N0} completed row(s).");
            }
        }
    }

    private void OnProfileScanCancellationRequested(
        object? sender,
        EventArgs args)
    {
        if (_assetProfileScanJob is { IsCancellable: true } job)
        {
            job.Cancel();
            AssetBrowser.SetProfileScanRunning(
                true,
                "Canceling mesh profile classification...");
        }
    }

    private bool CanUseSelectedMeshAsset() =>
        !IsBusy &&
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        };

    private bool CanUseSelectedMeshAssetAsTarget() =>
        (!IsBusy || IsTargetSwitching) &&
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        };

    private async Task<DecodedRetailModelSession> DecodeRetailModelAsync(
        AssetItemViewModel item,
        JobViewModel job)
    {
        RetailAssetRecord retailAsset = item.RetailAsset
            ?? throw new InvalidOperationException(
                "The selected row is not a retail asset.");
        job.Progress = 15.0;
        Dl1MeshPreviewPayload payload =
            await _retailMeshDecodeService.DecodeAsync(
                retailAsset,
                job.CancellationToken);
        job.CancellationToken.ThrowIfCancellationRequested();
        string fingerprint = payload.ResourceSha256
            ?? throw new InvalidDataException(
                "The decoded retail resource has no content fingerprint.");
        ProjectAssetReference projectAsset =
            CreateRetailProjectAsset(retailAsset, fingerprint);
        MeshRenderData[] previewMeshes = CreatePreviewMeshes(payload);
        if (payload.Profile is { } profile)
        {
            item.ApplyMeshProfile(profile);
            AssetBrowser.NotifyProfileChanged(item);
        }

        return new DecodedRetailModelSession(
            payload,
            retailAsset,
            projectAsset,
            previewMeshes);
    }

    private static MeshRenderData[] CreatePreviewMeshes(
        Dl1MeshPreviewPayload payload)
    {
        bool evidenceClassifiedFppHands =
            payload.Profile is
            {
                Perspective: Dl1MeshPerspective.FirstPerson,
                PerspectiveConfidence:
                    Dl1ClassificationConfidence.High,
            };
        return payload.Meshes
            .Select(mesh => evidenceClassifiedFppHands
                ? mesh with
                {
                    ProjectionRole = MeshProjectionRole.FppHands,
                }
                : mesh)
            .ToArray();
    }

    private JobViewModel BeginExclusiveAssetDecode(
        string name,
        string stage)
    {
        if (_assetProfileScanJob is { IsCancellable: true } profileJob)
        {
            profileJob.Cancel();
            AssetBrowser.SetProfileScanRunning(
                true,
                "Pausing background classification for the explicit asset action...");
        }

        if (_assetDecodeJob is { IsCancellable: true } previous)
        {
            previous.Cancel();
            previous.Complete("Superseded");
        }

        JobViewModel job = AddJob(name, "RP6L mesh", stage);
        _assetDecodeJob = job;
        return job;
    }

    private async Task PreviewSelectedAssetAsync()
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
        await PreviewAssetAsync(selected, requireCurrentSelection: false);
    }

    private async Task PreviewAssetAsync(
        AssetItemViewModel selected,
        bool requireCurrentSelection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selected);

        IsBusy = true;
        JobViewModel job = BeginExclusiveAssetDecode(
            $"Preview {selected.Name}",
            "Reading isolated asset preview");
        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.CanBeCanceled
                ? cancellationToken.Register(job.Cancel)
                : default;
        try
        {
            DecodedRetailModelSession model =
                await DecodeRetailModelAsync(selected, job);
            if (_disposed || !ReferenceEquals(_assetDecodeJob, job))
            {
                return;
            }
            if (requireCurrentSelection &&
                !string.Equals(
                    AssetBrowser.SelectedAsset?.Id,
                    selected.Id,
                    StringComparison.Ordinal))
            {
                job.Complete("Superseded");
                return;
            }

            job.Stage = "Isolated GPU preview";
            job.Progress = 85.0;
            long generation = Interlocked.Increment(
                ref _previewGeneration);
            SetWorkspace(
                EditorWorkspaceMode.Browse,
                preserveLegacyCutscene: false);
            if (_isolatedBrowsePreviewFrame is null)
            {
                _authoringOrbitCameras = _viewportCoordinator
                    .CaptureOrbitCameras();
            }

            RenderFrameSnapshot authored = TargetViewport.SceneSource
                .CaptureFrame();
            _isolatedBrowsePreviewFrame = authored with
            {
                Meshes = model.PreviewMeshes,
                Skeleton = model.Payload.Skeleton,
                Gizmos = [],
                MorphWeights = [],
                Generation = generation,
                FppProjectionState = null,
                AuthoringOverlays =
                    RenderAuthoringOverlayState.Disabled,
            };
            _isolatedBrowsePreviewTitle =
                $"Asset Preview - {selected.Name}";
            _isolatedBrowsePreviewFidelity =
                $"Isolated retail asset; {model.PreviewMeshes.Length:N0} draw surface(s); active animation unchanged";
            TargetViewport.SceneSource.SetExternalPreviewScene(
                _isolatedBrowsePreviewFrame);
            TargetViewport.SetPresentation(
                _isolatedBrowsePreviewTitle,
                _isolatedBrowsePreviewFidelity);
            FrameIsolatedBrowsePreview();
            SetBlenderExportTarget(
                model.Payload,
                model.RetailAsset);
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Previewing {selected.Name} in isolation; project target unchanged";
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Asset preview",
                $"Could not preview {selected.Name}",
                exception.Message);
            StatusText = "Isolated asset preview failed";
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

            if (ownsActiveDecode)
            {
                IsBusy = false;
            }
        }
    }

    private void FrameIsolatedBrowsePreview()
    {
        RenderFrameSnapshot frame = TargetViewport.SceneSource
            .CaptureFrame();
        if (!RenderCameraFraming.TryFrame(
                frame,
                out RenderCamera camera))
        {
            return;
        }

        _viewportCoordinator.UpdateCamera(
            ViewportSide.Target,
            camera);
        _browseOrbitCameras = _viewportCoordinator
            .CaptureOrbitCameras();
    }

    private void ScheduleAutomaticAssetPreview(
        AssetItemViewModel selected)
    {
        CancelAutomaticAssetPreview();
        CancellationTokenSource source =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeSource.Token);
        _automaticAssetPreviewSource = source;
        _automaticAssetPreviewTask = PreviewSelectedAssetAfterDelayAsync(
            selected,
            source);
    }

    private async Task PreviewSelectedAssetAfterDelayAsync(
        AssetItemViewModel selected,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(300, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (_disposed ||
                ActiveWorkspace != EditorWorkspaceMode.Browse ||
                !string.Equals(
                    AssetBrowser.SelectedAsset?.Id,
                    selected.Id,
                    StringComparison.Ordinal))
            {
                return;
            }

            await PreviewAssetAsync(
                selected,
                requireCurrentSelection: true,
                cancellationToken: source.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer selection or explicit Use action superseded the
            // debounced Browse preview.
        }
        finally
        {
            if (ReferenceEquals(
                    _automaticAssetPreviewSource,
                    source))
            {
                _automaticAssetPreviewSource = null;
            }
            source.Dispose();
        }
    }

    private void CancelAutomaticAssetPreview()
    {
        CancellationTokenSource? source =
            Interlocked.Exchange(
                ref _automaticAssetPreviewSource,
                null);
        if (source is null)
        {
            return;
        }

        source.Cancel();
    }

    private async Task UseSelectedAssetAsSourceAsync()
    {
        CancelAutomaticAssetPreview();
        if (AssetBrowser.SelectedAsset is not
            {
                Kind: AssetKind.Mesh,
                RetailAsset: not null,
            } selected)
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = BeginExclusiveAssetDecode(
            $"Use {selected.Name} as source",
            "Decoding immutable source-model candidate");
        try
        {
            DecodedRetailModelSession model =
                await DecodeRetailModelAsync(selected, job);
            if (_disposed || !ReferenceEquals(_assetDecodeJob, job))
            {
                return;
            }

            if (model.Payload.Source.Rig is null)
            {
                throw new InvalidOperationException(
                    "This retail resource has no decoded skeleton and cannot be an animation source model.");
            }

            ProjectAssetReference sourceAsset =
                FindMatchingProjectRetailAsset(model.ProjectAsset) ??
                model.ProjectAsset;
            model = model with
            {
                ProjectAsset = sourceAsset,
            };
            _sourceModelContext = model;
            OnPropertyChanged(nameof(ActiveSourceModelLabel));
            SetBlenderExportTarget(
                model.Payload,
                model.RetailAsset);
            SetSourcePreviewScene(
                model.PreviewMeshes,
                model.Payload.Skeleton);
            SourceViewport.SetPresentation(
                "Source Model",
                "Explicit immutable source-model candidate; no animation or target was changed");
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"{selected.Name} is the explicit source model for the next ANM2 clip";

            if (_pendingExplorerAnimationSourceChoice is { } pending)
            {
                _pendingExplorerAnimationSourceChoice = null;
                OnPropertyChanged(
                    nameof(IsExplorerSourceModelPickerActive));
                OnPropertyChanged(
                    nameof(ExplorerSourceModelPickerPrompt));
                CancelExplorerSourceModelPickerCommand
                    .NotifyCanExecuteChanged();
                AssetBrowser.SelectedAsset = pending;
                IsBusy = false;
                await PlaySelectedExplorerAnimationAsync();
            }
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
        }
        catch (Exception exception)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Animation source",
                $"Could not use {selected.Name} as the source model",
                exception.Message);
            StatusText = "Source-model selection failed";
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

            if (ownsActiveDecode)
            {
                IsBusy = false;
            }
        }
    }

    private async Task UseSelectedAssetAsTargetAsync()
    {
        CancelAutomaticAssetPreview();
        if (AssetBrowser.SelectedAsset is not
            {
                Kind: AssetKind.Mesh,
                RetailAsset: not null,
            } selected)
        {
            return;
        }

        ProjectAnimation? previousAnimation = GetActiveAnimation();
        bool wasPlaying = Timeline.IsPlaying;
        Timeline.IsPlaying = false;
        int frozenFrame = Math.Max(
            0,
            Timeline.CurrentFrame);
        TargetTransitionToken transition =
            _editorSessionCoordinator.BeginTargetTransition(
                previousAnimation?.Id,
                frozenFrame);
        IsTargetSwitching = true;
        IsBusy = true;
        JobViewModel job = BeginExclusiveAssetDecode(
            $"Use {selected.Name} as target",
            "Decoding target without changing the project");
        try
        {
            DecodedRetailModelSession decoded =
                await DecodeRetailModelAsync(selected, job);
            if (_disposed ||
                !ReferenceEquals(_assetDecodeJob, job) ||
                decoded.Payload.Source.Rig is not { } targetRig)
            {
                if (decoded.Payload.Source.Rig is null)
                {
                    throw new InvalidOperationException(
                        "This retail resource has no decoded skeleton and cannot be an animation target.");
                }

                return;
            }

            ProjectAssetReference targetAsset =
                FindMatchingProjectRetailAsset(decoded.ProjectAsset) ??
                decoded.ProjectAsset;
            decoded = decoded with
            {
                ProjectAsset = targetAsset,
            };

            if (previousAnimation is null)
            {
                if (!_editorSessionCoordinator.TryCommitTargetTransition(
                        transition,
                        animationId: null,
                        variant: null,
                        TargetBindingStatus.Invalid,
                        () =>
                        {
                            PublishDecodedMesh(
                                decoded.Payload,
                                decoded.RetailAsset,
                                targetAsset,
                                restoreRetargetMap: false);
                            _activeRetargetMap = null;
                            SetTargetBindingStatus(
                                TargetBindingStatus.Invalid);
                        }))
                {
                    job.Complete("Superseded");
                    return;
                }

                job.Progress = 100.0;
                job.Complete("Complete");
                StatusText =
                    $"{selected.Name} is ready as a target; load or activate an animation to bind it";
                return;
            }

            ProjectAnimationSourceBinding sourceBinding =
                previousAnimation.SourceBinding ??
                throw new InvalidOperationException(
                    "The active animation has no immutable source binding. Rebind its source before choosing a target.");
            RigDefinition sourceRig = _sourceAnimation?.Rig ??
                _sourceModelContext?.Payload.Source.Rig ??
                throw new InvalidOperationException(
                    "The active animation's immutable source rig is not loaded.");
            string sourceSignature = RigSignature.Compute(sourceRig);
            if (!string.Equals(
                    sourceSignature,
                    sourceBinding.SourceRigSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The loaded source rig differs from the active animation's immutable source binding.");
            }

            ImmutableArray<ProjectAssetReference> assets =
                _project.Assets;
            if (!assets.Any(asset => asset.Id == targetAsset.Id))
            {
                assets = assets.Add(targetAsset);
            }

            IReadOnlyDictionary<Guid, ProjectAssetReference> assetMap =
                assets.ToDictionary(static asset => asset.Id);
            Guid variantGroupId =
                previousAnimation.VariantGroupId ??
                AnimationVariantKey.CreateGroupId(
                    previousAnimation,
                    assetMap);
            ProjectAnimation desired = previousAnimation with
            {
                VariantGroupId = variantGroupId,
                TargetAssetId = targetAsset.Id,
                TargetRigId = targetRig.Id,
                TargetRigSignature = RigSignature.Compute(targetRig),
            };
            AnimationVariantKey desiredKey = AnimationVariantKey.Create(
                desired,
                assetMap);
            ProjectAnimation? exactVariant = _project.Animations
                .Where(animation =>
                    (animation.VariantGroupId ?? variantGroupId) ==
                        variantGroupId &&
                    animation.SourceBinding is not null &&
                    animation.TargetAssetId is not null)
                .FirstOrDefault(animation =>
                {
                    try
                    {
                        return AnimationVariantKey.Create(
                                animation with
                                {
                                    VariantGroupId = variantGroupId,
                                },
                                assetMap) == desiredKey;
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                });

            bool direct = HasSameRigContract(sourceRig, targetRig);
            RetargetMap? mapping;
            ProjectAnimation activated;
            ImmutableArray<ProjectAnimation> animations =
                _project.Animations;
            if (exactVariant is not null)
            {
                activated = exactVariant with
                {
                    VariantGroupId = variantGroupId,
                };
                int exactIndex = animations.IndexOf(exactVariant);
                if (exactIndex >= 0 &&
                    exactVariant.VariantGroupId is null)
                {
                    animations = animations.SetItem(
                        exactIndex,
                        activated);
                }

                mapping = direct
                    ? null
                    : activated.BoneMappings.IsEmpty
                        ? null
                        : ToRetargetMap(
                            sourceRig,
                            targetRig,
                            activated.BoneMappings,
                            activated.TargetBindReviews);
            }
            else
            {
                mapping = direct
                    ? null
                    : RetargetMapBuilder.CreateSuggested(
                        sourceRig,
                        targetRig);
                activated = CreateCleanTargetVariant(
                    previousAnimation,
                    variantGroupId,
                    targetAsset,
                    targetRig,
                    sourceRig,
                    mapping);
                animations = animations.Add(activated);
            }

            TargetBindingStatus bindingStatus = direct
                ? TargetBindingStatus.Direct
                : exactVariant is null
                    ? TargetBindingStatus.NeedsReview
                    : ResolveTargetBindingStatus(
                        sourceRig,
                        targetRig,
                        mapping);
            DlraProject candidateProject = _project with
            {
                Assets = assets,
                Animations = animations,
                ActiveAnimationId = activated.Id,
            };
            candidateProject.Validate();
            if (!_editorSessionCoordinator.TryCommitTargetTransition(
                    transition,
                    activated.Id,
                    desiredKey,
                    bindingStatus,
                    () =>
                    {
                        PublishDecodedMesh(
                            decoded.Payload,
                            decoded.RetailAsset,
                            targetAsset,
                            restoreRetargetMap: false,
                            animationContext: activated);
                        _activeAnimationId = activated.Id;
                        CommitProject(candidateProject);
                        _activeRetargetMap = mapping;
                        SetTargetBindingStatus(bindingStatus);
                        RefreshProjectBindings();
                        if (mapping is not null)
                        {
                            PublishMappingProposal(mapping);
                        }
                        else
                        {
                            MappingReviewStatus = direct
                                ? "Exact source/target rig signature: direct local-transform playback; PoseRetargeter is bypassed."
                                : "Retarget setup required.";
                        }
                    },
                    binding: new EditorSessionBinding(
                        assets.First(asset =>
                            asset.Id == sourceBinding.AssetId)
                            .ContentSha256 ?? sourceSignature,
                        targetAsset.ContentSha256 ??
                            RigSignature.Compute(targetRig),
                        activated.MappingFingerprint),
                    isPlaying: false))
            {
                job.Complete("Superseded");
                return;
            }

            SetWorkspace(
                bindingStatus == TargetBindingStatus.NeedsReview
                    ? EditorWorkspaceMode.RetargetEdit
                    : EditorWorkspaceMode.Animate,
                preserveLegacyCutscene: false);
            Timeline.IsPlaying = wasPlaying &&
                bindingStatus is TargetBindingStatus.Direct or
                    TargetBindingStatus.Ready;
            RefreshAnimationPreview();
            job.Progress = 100.0;
            job.Complete(
                exactVariant is null
                    ? "Created target variant"
                    : "Reused target variant");
            StatusText = bindingStatus == TargetBindingStatus.NeedsReview
                ? $"{selected.Name} created as a clean target variant; retarget setup is required before target playback"
                : $"{selected.Name} activated using its exact saved target variant";
        }
        catch (OperationCanceledException)
        {
            if (_editorSessionCoordinator.TryCancelTargetTransition(
                    transition))
            {
                job.Complete("Canceled");
                Timeline.IsPlaying = wasPlaying;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            if (_editorSessionCoordinator.TryCancelTargetTransition(
                    transition))
            {
                job.Complete("Failed");
                AddDiagnostic(
                    "Error",
                    "Target transaction",
                    $"Could not use {selected.Name} as the target",
                    exception.Message);
                StatusText =
                    "Target change failed; the previous animation variant and frame were retained";
                Timeline.IsPlaying = wasPlaying;
            }
        }
        finally
        {
            bool ownsActiveTargetDecode = ReferenceEquals(
                _assetDecodeJob,
                job);
            if (ownsActiveTargetDecode)
            {
                _assetDecodeJob = null;
            }

            if (!_editorSessionCoordinator.Current.IsTargetTransitioning)
            {
                IsTargetSwitching = false;
            }

            if (ownsActiveTargetDecode)
            {
                IsBusy = false;
            }
        }
    }

    internal static ProjectAnimation CreateCleanTargetVariant(
        ProjectAnimation sourceVariant,
        Guid variantGroupId,
        ProjectAssetReference targetAsset,
        RigDefinition targetRig,
        RigDefinition sourceRig,
        RetargetMap? proposal)
    {
        ArgumentNullException.ThrowIfNull(sourceVariant);
        ArgumentNullException.ThrowIfNull(targetAsset);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(sourceRig);
        string sourceSignature = RigSignature.Compute(sourceRig);
        string targetSignature = RigSignature.Compute(targetRig);
        if (!string.Equals(
                sourceVariant.SourceRigSignature,
                sourceSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The variant source rig differs from its immutable source signature.",
                nameof(sourceRig));
        }

        return sourceVariant with
        {
            Id = Guid.NewGuid(),
            VariantGroupId = variantGroupId,
            TargetAssetId = targetAsset.Id,
            TargetRigId = targetRig.Id,
            TargetRigSignature = targetSignature,
            MappingFingerprint = proposal is null
                ? null
                : RetargetMapFingerprint.Compute(
                    sourceSignature,
                    targetSignature,
                    targetAsset.ContentSha256,
                    proposal),
            BoneMappings = proposal is null
                ? []
                : ToProjectMappings(
                    sourceRig,
                    targetRig,
                    proposal),
            TargetBindReviews = [],
            EditLayers = [],
            MorphBindings = [],
            MorphEditLayers = [],
            IkLayers = [],
            Attachments = [],
        };
    }

    internal static TargetBindingStatus ResolveTargetBindingStatus(
        RigDefinition source,
        RigDefinition target,
        RetargetMap? mapping)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (HasSameRigContract(source, target))
        {
            return TargetBindingStatus.Direct;
        }

        if (mapping is null)
        {
            return TargetBindingStatus.Invalid;
        }

        return RetargetMappingReview.Analyze(
                source,
                target,
                mapping).IsReady
            ? TargetBindingStatus.Ready
            : TargetBindingStatus.NeedsReview;
    }

    private void RestoreSavedRetailTargetSelection(
        IReadOnlyList<AssetItemViewModel> assets)
    {
        ProjectAnimation? animation = GetActiveAnimation();
        if (animation?.TargetAssetId is not { } targetAssetId)
        {
            return;
        }

        ProjectAssetReference? saved =
            _project.Assets.FirstOrDefault(asset =>
                asset.Id == targetAssetId);
        ProjectRetailAssetIdentity? identity =
            saved?.RetailIdentity;
        if (identity is null)
        {
            return;
        }

        AssetItemViewModel? match = assets.FirstOrDefault(item =>
            item.RetailAsset is { } retail &&
            retail.Id.Namespace ==
                RetailAssetNamespace.RpackResource &&
            string.Equals(
                retail.Id.InstallId,
                identity.InstallFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                retail.Id.ProviderId,
                identity.ProviderId,
                StringComparison.Ordinal) &&
            retail.Id.ResourceType == identity.ResourceType &&
            retail.Source.ResourceIndex == identity.ResourceIndex &&
            string.Equals(
                retail.DisplayName,
                identity.ResourceName,
                StringComparison.OrdinalIgnoreCase) &&
            retail.Id.Precedence == identity.Precedence);
        if (match is null)
        {
            AddDiagnostic(
                "Error",
                "Assets",
                "The saved retail target identity was not found in this DL1 installation",
                $"{identity.ProviderPack} / type {identity.ResourceType} / {identity.ResourceName}.");
            return;
        }

        AssetBrowser.SelectedAsset = match;
    }

    private void OnSelectedAssetChanged(
        object? sender,
        AssetItemViewModel? selected)
    {
        CancelAutomaticAssetPreview();
        PlaySelectedExplorerAnimationCommand
            .NotifyCanExecuteChanged();
        PreviewSelectedAssetCommand.NotifyCanExecuteChanged();
        UseSelectedAssetAsSourceCommand.NotifyCanExecuteChanged();
        UseSelectedAssetAsTargetCommand.NotifyCanExecuteChanged();
        if (_disposed || selected is null)
        {
            return;
        }

        if (selected.Kind == AssetKind.Mesh &&
            selected.RetailAsset is not null &&
            ActiveWorkspace == EditorWorkspaceMode.Browse)
        {
            StatusText =
                $"Selected {selected.Name}; loading an isolated Browse preview";
            ScheduleAutomaticAssetPreview(selected);
            return;
        }

        StatusText =
            $"Selected {selected.Name}; selection is metadata-only";
    }

    private void PublishDecodedMesh(
        Dl1MeshPreviewPayload payload,
        RetailAssetRecord retailAsset,
        ProjectAssetReference? exactProjectAsset = null,
        bool restoreRetargetMap = true,
        ProjectAnimation? animationContext = null)
    {
        bool evidenceClassifiedFppHands = payload.Profile is
        {
            Perspective: Dl1MeshPerspective.FirstPerson,
            PerspectiveConfidence: Dl1ClassificationConfidence.High,
        };
        MeshRenderData[] previewMeshes = CreatePreviewMeshes(payload);
        ProjectAssetReference targetCandidate = exactProjectAsset ??
            CreateRetailProjectAsset(
                retailAsset,
                payload.ResourceSha256
                    ?? throw new InvalidDataException(
                        "The decoded retail resource has no content fingerprint."));
        targetCandidate = FindMatchingProjectRetailAsset(
                targetCandidate) ??
            targetCandidate;
        ProjectAnimation? activeAnimation = animationContext ??
            GetActiveAnimation();
        if (activeAnimation is
        {
            MimicAssetId: not null,
        } or
        {
            FacialSourceAssetId: not null,
        })
        {
            EnsureDecodedTargetMatchesSavedFacialTarget(
                activeAnimation,
                payload.Source.Rig,
                targetCandidate);
        }

        _targetRig = payload.Source.Rig;
        _targetProjectAsset = targetCandidate;
        OnPropertyChanged(nameof(ActiveTargetModelLabel));
        SetBlenderExportTarget(payload, retailAsset);
        if (_sourceAnimation is null)
        {
            SetSourcePreviewScene(previewMeshes, payload.Skeleton);
        }
        else
        {
            SkeletonPose sourcePose = SampleSourcePose();
            SetSourcePreviewScene(
                _sourceBaseMeshes,
                CorePreviewAdapter.ToRenderSkeleton(
                    sourcePose,
                    SelectedBone?.Index));
        }

        SetTargetPreviewScene(previewMeshes, payload.Skeleton);
        ReplaceSkeleton(payload.Skeleton);
        FacialFpp.ReplaceMorphs(payload.MorphChannelNames.Select(
            static name => new MorphChannelViewModel(name)));
        IkEditor.ReplaceChains(
            _targetRig?.IkChains.Select(static chain => chain.Name)
            ?? []);
        InitializeIkEditorFromBindPose();
        ApplyFedExpressionCommand.NotifyCanExecuteChanged();
        KeyMorphPoseCommand.NotifyCanExecuteChanged();
        KeyIkConstraintCommand.NotifyCanExecuteChanged();
        foreach (string diagnostic in payload.Diagnostics)
        {
            AddDiagnostic(
                "Warning",
                "DL1 mesh",
                diagnostic,
                null);
        }

        if (payload.MorphChannelNames.Count > 0)
        {
            int decodedMorphCount = payload.Source.MorphTargets.Count(
                static target =>
                    target.PayloadStatus ==
                        Dl1MorphPayloadStatus.VertexDeltasDecoded);
            int nameOnlyMorphCount =
                payload.Source.MorphTargets.Count - decodedMorphCount;
            AddDiagnostic(
                decodedMorphCount > 0 ? "Info" : "Warning",
                "Facial",
                $"{payload.MorphChannelNames.Count:N0} morph names; {decodedMorphCount:N0} position-delta targets decoded",
                decodedMorphCount > 0
                    ? $"Mapped entity/LOD targets use evidence-backed target-major SHORT4 XYZ deltas at 1/16384 before skinning. {nameOnlyMorphCount:N0} inventory channels are name-only for this resource. Facial preview remains DL1 profile until matching Windows 1.55 visual captures are approved."
                    : "This resource has no mapped, supported SHORT4 position-delta target. Named inventory remains available for authoring, while unknown layouts fail locally and are never replaced with fabricated deformation.");
        }

        if (evidenceClassifiedFppHands)
        {
            AddDiagnostic(
                "Info",
                "FPP",
                "Retail mesh uses the explicit FPP-hands projection role",
                "The resource identity has a high-confidence explicit FPP token. In FPP camera mode it renders only when a valid separate hands projection is available; orbit and non-FPP views retain the scene projection.");
        }

        if (_targetRig is null)
        {
            AddDiagnostic(
                "Warning",
                "Retargeting",
                "The selected retail resource has no decoded animation rig",
                "Static meshes remain previewable but cannot be animation targets.");
        }
        else
        {
            AddDiagnostic(
                "Info",
                "Retargeting",
                $"Retail rig derived dynamically: {_targetRig.BoneCount:N0} bones, {_targetRig.MorphChannels.Length:N0} morphs",
                $"Rig signature {RigSignature.Compute(_targetRig)}; {_targetRig.IkChains.Length:N0} validated two-bone IK chains.");
        }

        if (restoreRetargetMap)
        {
            RestoreOrCreateRetargetMap();
        }
    }

    private void EnsureDecodedTargetMatchesSavedFacialTarget(
        ProjectAnimation animation,
        RigDefinition? targetRig,
        ProjectAssetReference candidate)
    {
        ProjectAssetReference saved =
            animation.TargetAssetId is { } targetAssetId
                ? _project.Assets.FirstOrDefault(asset =>
                    asset.Id == targetAssetId &&
                    asset.Kind ==
                        ProjectAssetKind.RetailGameResource)
                    ?? throw new InvalidDataException(
                        "The animation with saved facial data has no exact retail target asset.")
                : throw new InvalidDataException(
                    "The animation with saved facial data has no retail target identity.");
        ProjectRetailAssetIdentity expected =
            saved.RetailIdentity
            ?? throw new InvalidDataException(
                "The saved retail target has no physical identity.");
        ProjectRetailAssetIdentity actual =
            candidate.RetailIdentity
            ?? throw new InvalidDataException(
                "The decoded retail target has no physical identity.");
        bool sameIdentity =
            string.Equals(
                saved.ContentSha256,
                candidate.ContentSha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                expected.InstallFingerprint,
                actual.InstallFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                expected.ProviderId,
                actual.ProviderId,
                StringComparison.Ordinal) &&
            string.Equals(
                expected.ProviderPack,
                actual.ProviderPack,
                StringComparison.OrdinalIgnoreCase) &&
            expected.ResourceType == actual.ResourceType &&
            expected.ResourceIndex == actual.ResourceIndex &&
            string.Equals(
                expected.ResourceName,
                actual.ResourceName,
                StringComparison.OrdinalIgnoreCase) &&
            expected.Precedence == actual.Precedence;
        if (!sameIdentity)
        {
            throw new InvalidDataException(
                "Saved facial data can only be decoded against the animation's exact retail target identity.");
        }

        if (targetRig is null ||
            !string.Equals(
                RigSignature.Compute(targetRig),
                animation.TargetRigSignature,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The decoded retail target rig differs from the rig used to bind the saved facial data.");
        }
    }

    private async Task LoadPendingAnm2SourceAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingAnm2SourcePath is not { } sourcePath ||
            _targetRig is not { } targetRig ||
            GetActiveAnimation() is not { } animation)
        {
            return;
        }

        if (animation.SourceBinding is not
            {
                Kind: AnimationSourceKind.LocalAnm2,
                RetailSourceModelAssetId: { } sourceModelAssetId,
                Partition: { } savedPartition,
            } sourceBinding)
        {
            AddDiagnostic(
                "Error",
                "ANM2 source binding",
                "This older C# schema-1 ANM2 document has no provable source model",
                "Playback is blocked. Select an exact-signature retail model and use Rebind Source; the existing authored document will not be mutated.");
            StatusText = "ANM2 source needs an explicit source-model rebind";
            return;
        }

        if (_targetProjectAsset?.Id != sourceModelAssetId)
        {
            if (string.Equals(
                    RigSignature.Compute(targetRig),
                    sourceBinding.SourceRigSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                AddDiagnostic(
                    "Warning",
                    "ANM2 source binding",
                    "The selected model is an exact-signature source candidate, but its retail fingerprint differs",
                    "Use Rebind Source to create a new clean animation document. The existing document remains bound to its saved retail model identity.");
            }

            StatusText = "Select the animation's exact saved source model before ANM2 playback";
            return;
        }

        Anm2Clip source = await new Anm2Decoder().DecodeFileAsync(
            sourcePath,
            cancellationToken: cancellationToken);
        Anm2PartitionedImportResult imported =
            Anm2TrackPartitioner.Partition(
                source,
                targetRig,
                animation.FrameRate,
                cancellationToken);
        if (imported.Partition.RequiresReview)
        {
            throw new InvalidDataException(
                "The saved ANM2 contains ambiguous descriptors and cannot be played until it is rebound and reviewed.");
        }

        if (!string.Equals(
                imported.Partition.Fingerprint,
                savedPartition.Fingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The decoded ANM2 partition differs from the immutable saved source binding.");
        }

        if (imported.CombinedClip.FrameCount != animation.FrameCount)
        {
            throw new InvalidDataException(
                "The saved ANM2 source frame count no longer matches the project.");
        }

        _sourceAnimation = new ImportedAnimationSession(
            targetRig,
            imported.CombinedClip,
            sourcePath,
            "DL1 ANM2")
        {
            SourceKindContract = AnimationSourceKind.LocalAnm2,
            RetailSourceModelAssetId = sourceModelAssetId,
            Partition = imported.Partition,
            TimingProvenance = sourceBinding.TimingProvenance,
            FacialClip = imported.FacialClip,
        };
        _sourceBaseMeshes = _targetBaseMeshes;
        _synchronizedAnimation = imported.CombinedClip;
        _pendingAnm2SourcePath = null;
        SourceViewport.SceneSource.SetScene(
            [],
            CorePreviewAdapter.ToRenderSkeleton(
                targetRig.CreateBindPose()),
            []);
        if (imported.Partition.UnresolvedDescriptors.Length > 0)
        {
            AddDiagnostic(
                "Warning",
                "ANM2",
                $"{imported.Partition.UnresolvedDescriptors.Length:N0} saved descriptors do not exist in the exact retail source rig",
                string.Join(
                    ", ",
                    imported.Partition.UnresolvedDescriptors
                        .Take(12)
                        .Select(static value => $"0x{value:X8}")));
        }

        RefreshProjectBindings();
        RestoreOrCreateRetargetMap();
        StatusText =
            $"Loaded saved ANM2 source {Path.GetFileName(sourcePath)} against its exact retail rig";
    }

    private async Task LoadPendingMimicSourceAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingMimicSourcePath is not { } sourcePath ||
            _pendingMimicAssetId is not { } mimicAssetId ||
            _sourceAnimation is not { } source ||
            _targetRig is not { } targetRig ||
            _targetProjectAsset is not { } targetAsset ||
            GetActiveAnimation() is not { } animation)
        {
            return;
        }

        if (animation.MimicAssetId != mimicAssetId)
        {
            throw new InvalidDataException(
                "The pending mimic project asset is not the active animation's saved mimic.");
        }

        EnsureExactMimicTarget(
            animation,
            targetRig,
            targetAsset);
        ProjectAssetReference mimicAsset =
            _project.Assets.FirstOrDefault(asset =>
                asset.Id == mimicAssetId &&
                asset.Kind == ProjectAssetKind.SourceAnimation)
            ?? throw new InvalidDataException(
                "The saved mimic project asset is unavailable.");
        string expectedHash = mimicAsset.ContentSha256
            ?? throw new InvalidDataException(
                "The saved mimic project asset has no SHA-256 fingerprint.");
        SynchronizedMimicAnimation loaded =
            await SynchronizedMimicAnm2Loader.LoadAsync(
                sourcePath,
                expectedHash,
                targetRig,
                source.Clip,
                animation.FrameRate,
                animation.FrameCount,
                cancellationToken);

        _mimicAnimation = new ImportedMimicSession(
            mimicAssetId,
            loaded.Mimic,
            sourcePath);
        FacialClipTiming timing = animation.FacialTiming ??
            loaded.Timing;
        _synchronizedAnimation =
            AnimationClipSynchronization.Synchronize(
                source.Clip,
                loaded.Mimic,
                timing);
        _pendingMimicSourcePath = null;
        _pendingMimicAssetId = null;
        RefreshAnimationPreview();
        NotifyExportCommands();
        AddDiagnostic(
            "Info",
            "Mimic",
            $"Reopened {loaded.Mimic.ScalarTracks.Length:N0} synchronized mimic tracks",
            $"{loaded.Mimic.FrameCount:N0} native frames at {loaded.Mimic.FrameRate.Numerator}/{loaded.Mimic.FrameRate.Denominator} fps; neutral outside the saved facial range; the project-relative SHA-256 and descriptor partition were verified before decode.");
    }

    private async Task LoadPendingFacialFbxSourceAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingFacialFbxSourcePath is not { } sourcePath ||
            _pendingFacialFbxAssetId is not { } facialSourceAssetId ||
            _sourceAnimation is not { } source ||
            _targetRig is not { } targetRig ||
            _targetProjectAsset is not { } targetAsset ||
            GetActiveAnimation() is not { } animation)
        {
            return;
        }

        if (animation.FacialSourceAssetId != facialSourceAssetId ||
            animation.FacialSourceValueUnit is not
            { } sourceValueUnit)
        {
            throw new InvalidDataException(
                "The pending facial FBX project asset is not the active animation's saved facial source.");
        }

        EnsureExactMimicTarget(
            animation,
            targetRig,
            targetAsset);
        ProjectAssetReference facialSourceAsset =
            _project.Assets.FirstOrDefault(asset =>
                asset.Id == facialSourceAssetId &&
                asset.Kind == ProjectAssetKind.SourceAnimation)
            ?? throw new InvalidDataException(
                "The saved facial FBX project asset is unavailable.");
        _ = facialSourceAsset.ContentSha256
            ?? throw new InvalidDataException(
                "The saved facial FBX project asset has no SHA-256 fingerprint.");

        string profileId = animation.MimicProfileId ??
            throw new InvalidDataException(
                "The saved facial FBX has no versioned DL1 mimic profile.");
        Dl1MimicProfile profile =
            Dl1MimicProfileCodec.ReadBuiltInCommon46();
        if (!string.Equals(
                profile.ProfileId,
                profileId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The saved facial FBX uses unsupported mimic profile '{profileId}'.");
        }

        string expectedMappingFingerprint =
            animation.MimicMappingFingerprint ??
            throw new InvalidDataException(
                "The saved facial FBX has no mapping fingerprint.");
        string actualMappingFingerprint =
            FbxFacialProjectReviewService.ComputeMappingFingerprint(
                profileId,
                targetRig,
                new AnimationTiming(
                    animation.FrameRate,
                    animation.FrameCount),
                animation.MorphBindings);
        if (!string.Equals(
                expectedMappingFingerprint,
                actualMappingFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The saved facial FBX mapping fingerprint no longer matches its exact retail rig, body timing, and reviewed rows.");
        }

        _ = ProjectMorphBindingResolver.Resolve(
            animation.MorphBindings,
            targetRig,
            ProjectMorphBindingResolutionMode.Preview);
        AnimationClip facialClip =
            await _facialFbxProjectReviewImporter.DecodeSourceAsync(
                sourcePath,
                sourceValueUnit,
                animation,
                cancellationToken);
        AnimationClip synchronized =
            AnimationClipSynchronization.Synchronize(
                source.Clip,
                facialClip);

        _mimicAnimation = null;
        _facialFbxAnimation =
            new ImportedFacialFbxSession(
                facialSourceAssetId,
                facialClip,
                sourcePath,
                sourceValueUnit);
        _synchronizedAnimation = synchronized;
        _pendingFacialFbxSourcePath = null;
        _pendingFacialFbxAssetId = null;
        RefreshAnimationPreview();
        NotifyExportCommands();
        AddDiagnostic(
            "Info",
            "Facial FBX",
            $"Reopened {facialClip.ScalarTracks.Length:N0} retained facial curve(s)",
            $"{facialClip.FrameCount:N0} frames at {facialClip.FrameRate.Numerator}/{facialClip.FrameRate.Denominator} fps; project-relative SHA-256, exact retail target, profile, and mapping fingerprint were verified before preview.");
    }

    private void RestoreOrCreateRetargetMap()
    {
        AutoMapCommand.NotifyCanExecuteChanged();
        if (_sourceAnimation is not null &&
            _targetRig is not null)
        {
            if (HasSameRigContract(
                    _sourceAnimation.Rig,
                    _targetRig))
            {
                _activeRetargetMap = null;
                RefreshProjectBindings();
                MappingReviewStatus =
                    "Exact source/target rig signature: direct local-transform playback; PoseRetargeter is bypassed.";
                RefreshAnimationPreview();
                NotifyMappingCommands();
                return;
            }

            ProjectAnimation? animation = GetActiveAnimation();
            string sourceSignature =
                RigSignature.Compute(_sourceAnimation.Rig);
            string targetSignature =
                RigSignature.Compute(_targetRig);
            if (animation is not null &&
                animation.BoneMappings.Length > 0 &&
                string.Equals(
                    animation.SourceRigSignature,
                    sourceSignature,
                    StringComparison.Ordinal) &&
                string.Equals(
                    animation.TargetRigSignature,
                    targetSignature,
                    StringComparison.Ordinal))
            {
                _activeRetargetMap = ToRetargetMap(
                    _sourceAnimation.Rig,
                    _targetRig,
                    animation.BoneMappings,
                    animation.TargetBindReviews);
                RefreshProjectBindings();
                PublishMappingProposal(_activeRetargetMap);
                RefreshAnimationPreview();
            }
            else
            {
                AutoMap();
            }
        }

        NotifyMappingCommands();
    }

    private ProjectAssetReference CreateRetailProjectAsset(
        RetailAssetRecord asset,
        string contentSha256)
    {
        Dl1InstallLocation install = _assetWorkspace.Install
            ?? throw new InvalidOperationException(
                "The retail install identity is unavailable.");
        string installPath = Path.GetFullPath(install.InstallPath);
        string containerPath = Path.GetFullPath(
            asset.Source.ContainerPath);
        string providerPack =
            ResolvePortableProviderPackPath(
                installPath,
                containerPath);

        return new ProjectAssetReference
        {
            Kind = ProjectAssetKind.RetailGameResource,
            RelativePath = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"retail/{asset.Id.ResourceType}/{asset.Id.SourceIndex:D8}"),
            ResourceId = asset.Id.LogicalId.StableKey,
            ContentSha256 = contentSha256,
            RetailIdentity = new ProjectRetailAssetIdentity
            {
                InstallFingerprint = asset.Id.InstallId,
                ProviderId = asset.Id.ProviderId,
                ProviderPack = providerPack,
                ResourceType = asset.Id.ResourceType,
                ResourceIndex = asset.Source.ResourceIndex,
                ResourceName = asset.DisplayName,
                Precedence = asset.Id.Precedence,
                ContentSha256 = contentSha256,
            },
        };
    }

    private string ResolvePortableProviderPackPath(
        string installPath,
        string containerPath)
    {
        if (TryCreateContainedRelativePath(
                installPath,
                containerPath,
                out string installRelative))
        {
            return installRelative;
        }

        if (ProjectPath is { } projectPath)
        {
            string projectDirectory =
                Path.GetDirectoryName(projectPath)
                ?? throw new InvalidOperationException(
                    "The project has no parent directory.");
            foreach (string configuredRoot in
                     _project.Dl1Settings
                         .AdditionalRpackRoots)
            {
                string rootPath = Path.GetFullPath(
                    Path.Combine(
                        projectDirectory,
                        configuredRoot.Replace(
                            '/',
                            Path.DirectorySeparatorChar)));
                if (TryCreateContainedRelativePath(
                        rootPath,
                        containerPath,
                        out _)
                    && TryCreateContainedRelativePath(
                        projectDirectory,
                        containerPath,
                        out string projectRelative))
                {
                    return projectRelative;
                }
            }
        }

        throw new InvalidOperationException(
            "The selected retail provider is outside both the indexed DL1 installation and the configured project RPack roots.");
    }

    private static bool TryCreateContainedRelativePath(
        string rootPath,
        string candidatePath,
        out string relativePath)
    {
        string relative = Path.GetRelativePath(
                Path.GetFullPath(rootPath),
                Path.GetFullPath(candidatePath))
            .Replace('\\', '/');
        if (Path.IsPathRooted(relative) ||
            relative.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == ".."))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = relative;
        return true;
    }

    private async Task AddAttachmentAsync()
    {
        AssetItemViewModel? selected =
            AttachmentEditor.SelectedCatalogAsset;
        AttachmentBoneOptionViewModel? parent =
            AttachmentEditor.SelectedParentBone;
        if (selected?.RetailAsset is not { } retailAsset ||
            selected.Kind != AssetKind.Mesh ||
            parent is null ||
            _targetRig is not { } targetRig ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            AddDiagnostic(
                "Error",
                "Attachments",
                "A retail mesh, target rig, parent bone, and animation are required",
                "Load the asset catalog, choose a mesh in the attachment picker, load the animated target, and choose a decoded bone or helper.");
            return;
        }

        if (animation.Attachments.Length >=
            AttachmentBinding.MaximumPerAnimation)
        {
            AddDiagnostic(
                "Error",
                "Attachments",
                "The bounded attachment limit has been reached",
                $"A DL1 animation document supports at most {AttachmentBinding.MaximumPerAnimation} rigid prop/weapon attachments.");
            return;
        }

        string targetSignature = RigSignature.Compute(targetRig);
        Guid animationId = animation.Id;
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Attach {selected.Name}",
            "Retail prop",
            "Decoding");
        try
        {
            Dl1MeshPreviewPayload payload =
                await _retailMeshDecodeService.DecodeAsync(
                    retailAsset,
                    job.CancellationToken);
            job.Progress = 65;
            job.Stage = "Validate";
            if (payload.Meshes.Count == 0)
            {
                throw new InvalidDataException(
                    "The selected retail resource has no decoded renderable surfaces.");
            }

            if (_targetRig is null ||
                !string.Equals(
                    targetSignature,
                    RigSignature.Compute(_targetRig),
                    StringComparison.Ordinal) ||
                !TryGetActiveAnimation(
                    out animation,
                    out animationIndex) ||
                animation.Id != animationId)
            {
                throw new InvalidOperationException(
                    "The target rig or active animation changed while the attachment was decoding.");
            }

            string contentSha256 = payload.ResourceSha256
                ?? throw new InvalidDataException(
                    "The decoded attachment resource has no content fingerprint.");
            ProjectAssetReference candidate =
                CreateRetailProjectAsset(
                    retailAsset,
                    contentSha256);
            ProjectAssetReference projectAsset =
                FindEquivalentRetailProjectAsset(candidate)
                ?? candidate;
            ImmutableArray<ProjectAssetReference> assets =
                ReferenceEquals(projectAsset, candidate)
                    ? _project.Assets.Add(projectAsset)
                    : _project.Assets;
            AttachmentBinding binding =
                CreateEditedAttachmentBinding(
                    Guid.NewGuid(),
                    projectAsset.Id,
                    parent);
            ProjectAnimation updated = animation with
            {
                Attachments =
                    animation.Attachments.Add(binding),
            };
            job.Stage = "Publish";
            CommitProject(_project with
            {
                Assets = assets,
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updated),
            });
            _attachmentRenderAssets[projectAsset.Id] =
                new AttachmentRenderAsset(
                    projectAsset.Id,
                    binding.Name,
                    payload.Meshes,
                    payload.Skeleton);
            TrimAttachmentRenderAssetCache();
            _attachmentStatuses[binding.Id] =
                $"Ready: {payload.Meshes.Count:N0} decoded surface(s)";
            RefreshProjectBindings();
            AttachmentEditor.SelectedAttachment =
                AttachmentEditor.Attachments.FirstOrDefault(
                    item => item.Id == binding.Id);
            HighlightSelectedMeshes = true;
            RefreshAnimationPreview();
            bool framed =
                TryFrameSelectedAttachment(reportFailure: false);
            job.Progress = 100;
            job.Complete("Complete");
            StatusText =
                $"Attached {binding.Name} to {binding.ParentBoneName}" +
                (framed
                    ? " and framed its decoded surfaces"
                    : "; select Frame attachment to locate it");
            AddDiagnostic(
                "Info",
                "Attachments",
                $"Attached retail asset '{selected.Name}'",
                $"{binding.ParentBoneName}; project asset {projectAsset.Id}; SHA-256 {contentSha256}.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Attachment decode canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            IOException or
            InvalidOperationException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Attachments",
                $"Could not attach '{selected.Name}'",
                exception.Message);
            StatusText = "Attachment authoring failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAddAttachment() =>
        !IsBusy &&
        AttachmentEditor.SelectedCatalogAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        } &&
        AttachmentEditor.SelectedParentBone is not null &&
        _targetRig is not null &&
        GetActiveAnimation() is { } animation &&
        animation.Attachments.Length <
            AttachmentBinding.MaximumPerAnimation;

    private void ApplyAttachment()
    {
        AttachmentItemViewModel? selected =
            AttachmentEditor.SelectedAttachment;
        AttachmentBoneOptionViewModel? parent =
            AttachmentEditor.SelectedParentBone;
        if (selected is null ||
            parent is null ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        int bindingIndex = FindAttachmentIndex(
            animation.Attachments,
            selected.Id);
        if (bindingIndex < 0)
        {
            AddDiagnostic(
                "Error",
                "Attachments",
                "The selected attachment is no longer in the active animation",
                selected.Id.ToString());
            RefreshProjectBindings();
            return;
        }

        try
        {
            AttachmentBinding existing =
                animation.Attachments[bindingIndex];
            AttachmentBinding updated =
                CreateEditedAttachmentBinding(
                    existing.Id,
                    existing.AssetId,
                    parent);
            ProjectAnimation updatedAnimation = animation with
            {
                Attachments = animation.Attachments.SetItem(
                    bindingIndex,
                    updated),
            };
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updatedAnimation),
            });
            _attachmentStatuses[updated.Id] =
                _attachmentRenderAssets.ContainsKey(
                    updated.AssetId)
                    ? "Ready: local offset updated"
                    : "Waiting for retail asset decode";
            RefreshProjectBindings();
            AttachmentEditor.SelectedAttachment =
                AttachmentEditor.Attachments.FirstOrDefault(
                    item => item.Id == updated.Id);
            RefreshAnimationPreview();
            StatusText =
                $"Updated attachment {updated.Name}";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            OverflowException)
        {
            AddDiagnostic(
                "Error",
                "Attachments",
                "The attachment edit was rejected",
                exception.Message);
        }
    }

    private bool CanApplyAttachment() =>
        !IsBusy &&
        AttachmentEditor.SelectedAttachment is not null &&
        AttachmentEditor.SelectedParentBone is not null &&
        GetActiveAnimation() is not null;

    private void RemoveAttachment()
    {
        AttachmentItemViewModel? selected =
            AttachmentEditor.SelectedAttachment;
        if (selected is null ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        int bindingIndex = FindAttachmentIndex(
            animation.Attachments,
            selected.Id);
        if (bindingIndex < 0)
        {
            return;
        }

        ProjectAnimation updated = animation with
        {
            Attachments =
                animation.Attachments.RemoveAt(bindingIndex),
        };
        CommitProject(_project with
        {
            Animations = _project.Animations.SetItem(
                animationIndex,
                updated),
        });
        _attachmentStatuses.Remove(selected.Id);
        RefreshProjectBindings();
        RefreshAnimationPreview();
        StatusText =
            $"Removed attachment {selected.Name}";
    }

    private bool CanRemoveAttachment() =>
        !IsBusy &&
        AttachmentEditor.SelectedAttachment is not null &&
        GetActiveAnimation() is not null;

    private AttachmentBinding CreateEditedAttachmentBinding(
        Guid id,
        Guid assetId,
        AttachmentBoneOptionViewModel parent)
    {
        string name = AttachmentEditor.Name.Trim();
        return new AttachmentBinding(
            id,
            assetId,
            name,
            parent.Index,
            AttachmentEditor.CreateLocalOffset(),
            AttachmentEditor.IsPreviewOnly
                ? AttachmentScope.PreviewOnly
                : AttachmentScope.AuthoredExportable,
            parent.Name);
    }

    private ProjectAssetReference?
        FindEquivalentRetailProjectAsset(
            ProjectAssetReference candidate) =>
        _project.Assets.FirstOrDefault(asset =>
            asset.Kind == ProjectAssetKind.RetailGameResource &&
            string.Equals(
                asset.ResourceId,
                candidate.ResourceId,
                StringComparison.Ordinal) &&
            string.Equals(
                asset.ContentSha256,
                candidate.ContentSha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                asset.RetailIdentity?.ProviderId,
                candidate.RetailIdentity?.ProviderId,
                StringComparison.Ordinal) &&
            asset.RetailIdentity?.ResourceIndex ==
                candidate.RetailIdentity?.ResourceIndex);

    private async Task RestoreProjectAttachmentsAsync(
        IReadOnlyList<AssetItemViewModel> catalogAssets,
        CancellationToken cancellationToken)
    {
        ProjectAnimation? animation = GetActiveAnimation();
        _attachmentRenderAssets.Clear();
        if (animation is null ||
            animation.Attachments.IsDefaultOrEmpty)
        {
            RefreshProjectBindings();
            return;
        }

        Dictionary<Guid, ProjectAssetReference> projectAssets =
            _project.Assets.ToDictionary(static asset => asset.Id);
        foreach (IGrouping<Guid, AttachmentBinding> group in
                 animation.Attachments.GroupBy(
                     static binding => binding.AssetId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttachmentBinding[] bindings = group.ToArray();
            if (!projectAssets.TryGetValue(
                    group.Key,
                    out ProjectAssetReference? projectAsset) ||
                projectAsset.RetailIdentity is null)
            {
                SetAttachmentGroupFailure(
                    bindings,
                    "Error: project retail asset identity is missing",
                    "A saved attachment does not refer to a valid retail project asset.");
                continue;
            }

            AssetItemViewModel? catalogAsset =
                FindRetailCatalogAsset(
                    projectAsset,
                    catalogAssets);
            if (catalogAsset?.RetailAsset is not
                { } retailAsset)
            {
                SetAttachmentGroupFailure(
                    bindings,
                    "Error: retail asset not found",
                    $"Saved attachment asset '{projectAsset.RetailIdentity.ResourceName}' was not found in the indexed DL1 providers.");
                continue;
            }

            try
            {
                Dl1MeshPreviewPayload payload =
                    await _retailMeshDecodeService.DecodeAsync(
                        retailAsset,
                        cancellationToken);
                string actualHash = payload.ResourceSha256
                    ?? throw new InvalidDataException(
                        "The decoded attachment has no content fingerprint.");
                if (!string.Equals(
                        actualHash,
                        projectAsset.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Retail attachment content changed: project {projectAsset.ContentSha256}, installed {actualHash}.");
                }

                if (payload.Meshes.Count == 0)
                {
                    throw new InvalidDataException(
                        "The saved retail attachment has no decoded renderable surfaces.");
                }

                _attachmentRenderAssets[group.Key] =
                    new AttachmentRenderAsset(
                        group.Key,
                        projectAsset.RetailIdentity.ResourceName,
                        payload.Meshes,
                        payload.Skeleton);
                foreach (AttachmentBinding binding in bindings)
                {
                    _attachmentStatuses[binding.Id] =
                        $"Ready: {payload.Meshes.Count:N0} decoded surface(s)";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                IOException or
                InvalidOperationException or
                OverflowException)
            {
                SetAttachmentGroupFailure(
                    bindings,
                    $"Error: {exception.Message}",
                    $"Could not restore retail attachment '{projectAsset.RetailIdentity.ResourceName}': {exception.Message}");
            }
        }

        RefreshProjectBindings();
        RefreshAnimationPreview();
    }

    private void SetAttachmentGroupFailure(
        IEnumerable<AttachmentBinding> bindings,
        string status,
        string diagnostic)
    {
        foreach (AttachmentBinding binding in bindings)
        {
            _attachmentStatuses[binding.Id] = status;
        }

        AddDiagnostic(
            "Error",
            "Attachments",
            "A saved attachment could not be resolved",
            diagnostic);
    }

    private void TrimAttachmentRenderAssetCache()
    {
        if (_attachmentRenderAssets.Count <=
            MaximumDecodedAttachmentAssetCacheEntries)
        {
            return;
        }

        HashSet<Guid> inUse = _project.Animations
            .SelectMany(static animation =>
                animation.Attachments)
            .Select(static attachment =>
                attachment.AssetId)
            .ToHashSet();
        foreach (Guid candidate in
                 _attachmentRenderAssets.Keys.ToArray())
        {
            if (_attachmentRenderAssets.Count <=
                MaximumDecodedAttachmentAssetCacheEntries)
            {
                break;
            }

            if (!inUse.Contains(candidate))
            {
                _attachmentRenderAssets.Remove(candidate);
            }
        }
    }

    private static AssetItemViewModel? FindRetailCatalogAsset(
        ProjectAssetReference projectAsset,
        IReadOnlyList<AssetItemViewModel> catalogAssets)
    {
        ProjectRetailAssetIdentity? identity =
            projectAsset.RetailIdentity;
        if (identity is null)
        {
            return null;
        }

        return catalogAssets.FirstOrDefault(item =>
            item.RetailAsset is { } retail &&
            retail.Id.Namespace ==
                RetailAssetNamespace.RpackResource &&
            string.Equals(
                retail.Id.InstallId,
                identity.InstallFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                retail.Id.ProviderId,
                identity.ProviderId,
                StringComparison.Ordinal) &&
            retail.Id.ResourceType ==
                identity.ResourceType &&
            retail.Source.ResourceIndex ==
                identity.ResourceIndex &&
            string.Equals(
                retail.DisplayName,
                identity.ResourceName,
                StringComparison.OrdinalIgnoreCase) &&
            retail.Id.Precedence ==
                identity.Precedence);
    }

    private ProjectAssetReference? FindProjectAsset(Guid assetId) =>
        _project.Assets.FirstOrDefault(asset => asset.Id == assetId);

    private ProjectAssetReference? FindMatchingProjectRetailAsset(
        ProjectAssetReference candidate) =>
        _project.Assets.FirstOrDefault(asset =>
            ProjectRetailAssetsMatch(asset, candidate));

    internal static ProjectAnimation? FindReusableRetailAnimation(
        DlraProject project,
        RetailAssetRecord retailAnimation,
        ProjectAssetReference sourceModelAsset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(retailAnimation);
        ArgumentNullException.ThrowIfNull(sourceModelAsset);
        return project.Animations.FirstOrDefault(animation =>
            animation.SourceBinding is
            {
                Kind: AnimationSourceKind.RetailAnm2,
                RetailSourceModelAssetId: { } modelId,
            } binding &&
            ProjectRetailAssetMatchesRecord(
                project.Assets.FirstOrDefault(
                    asset => asset.Id == binding.AssetId),
                retailAnimation) &&
            ProjectRetailAssetsMatch(
                project.Assets.FirstOrDefault(
                    asset => asset.Id == modelId),
                sourceModelAsset));
    }

    private static bool ProjectRetailAssetMatchesRecord(
        ProjectAssetReference? projectAsset,
        RetailAssetRecord retailAsset)
    {
        if (projectAsset?.RetailIdentity is not { } identity)
        {
            return false;
        }

        return string.Equals(
                   identity.InstallFingerprint,
                   retailAsset.Id.InstallId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   identity.ProviderId,
                   retailAsset.Id.ProviderId,
                   StringComparison.Ordinal) &&
               identity.ResourceType == retailAsset.Id.ResourceType &&
               identity.ResourceIndex ==
                   retailAsset.Source.ResourceIndex &&
               string.Equals(
                   identity.ResourceName,
                   retailAsset.DisplayName,
                   StringComparison.OrdinalIgnoreCase) &&
               identity.Precedence == retailAsset.Id.Precedence &&
               ProviderPackMatchesContainer(
                   identity.ProviderPack,
                   retailAsset.Source.ContainerPath);
    }

    private static bool ProviderPackMatchesContainer(
        string providerPack,
        string containerPath)
    {
        string portable = providerPack
            .Replace('\\', '/')
            .Trim('/');
        string container = containerPath
            .Replace('\\', '/')
            .TrimEnd('/');
        return portable.Length > 0 &&
            (string.Equals(
                 container,
                 portable,
                 StringComparison.OrdinalIgnoreCase) ||
             container.EndsWith(
                 "/" + portable,
                 StringComparison.OrdinalIgnoreCase));
    }

    private static bool ProjectRetailAssetsMatch(
        ProjectAssetReference? left,
        ProjectAssetReference? right)
    {
        if (left?.RetailIdentity is not { } first ||
            right?.RetailIdentity is not { } second)
        {
            return false;
        }

        return string.Equals(
                   left.ContentSha256,
                   right.ContentSha256,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   first.InstallFingerprint,
                   second.InstallFingerprint,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.ProviderId,
                   second.ProviderId,
                   StringComparison.Ordinal) &&
               first.ResourceType == second.ResourceType &&
               first.ResourceIndex == second.ResourceIndex &&
               string.Equals(
                   first.ResourceName,
                   second.ResourceName,
                   StringComparison.OrdinalIgnoreCase) &&
               first.Precedence == second.Precedence;
    }

    internal static bool ShouldPreserveTargetMapping(
        ProjectAnimation animation,
        ProjectAssetReference? previousTargetAsset,
        ProjectAssetReference selectedTargetAsset,
        string selectedRigSignature)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(selectedTargetAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedRigSignature);
        return animation.TargetAssetId.HasValue &&
            string.Equals(
                animation.TargetRigSignature,
                selectedRigSignature,
                StringComparison.Ordinal) &&
            ProjectRetailAssetsMatch(
                previousTargetAsset,
                selectedTargetAsset);
    }

    private void ReplaceSkeleton(SkeletonRenderData? skeleton)
    {
        SkeletonRoots.Clear();
        SelectedBone = null;
        if (skeleton is null)
        {
            return;
        }

        string[] paths = BuildBonePaths(skeleton.Bones);
        SkeletonNodeViewModel[] nodes = new SkeletonNodeViewModel[
            skeleton.Bones.Count];
        for (int index = 0; index < nodes.Length; index++)
        {
            BoneRenderData bone = skeleton.Bones[index];
            nodes[index] = new SkeletonNodeViewModel(
                bone.Name,
                paths[index],
                index,
                bone.ParentIndex,
                bone.LocalTransform,
                bone.WorldTransform,
                bone.Role,
                bone.IsHierarchyOverlayVisible);
        }

        for (int index = 0; index < nodes.Length; index++)
        {
            int parentIndex = nodes[index].ParentIndex;
            if (parentIndex >= 0 && parentIndex < nodes.Length)
            {
                nodes[parentIndex].Children.Add(nodes[index]);
            }
            else
            {
                SkeletonRoots.Add(nodes[index]);
            }
        }

        RefreshProjectBindings();
        RefreshEditableSkeletonPreview();
    }

    private static string[] BuildBonePaths(
        IReadOnlyList<BoneRenderData> bones)
    {
        string[] paths = new string[bones.Count];
        bool[] visiting = new bool[bones.Count];
        for (int index = 0; index < bones.Count; index++)
        {
            _ = ResolveBonePath(index, bones, paths, visiting);
        }

        return paths;
    }

    private static string ResolveBonePath(
        int index,
        IReadOnlyList<BoneRenderData> bones,
        string[] paths,
        bool[] visiting)
    {
        if (paths[index] is { Length: > 0 } cached)
        {
            return cached;
        }

        BoneRenderData bone = bones[index];
        if (visiting[index]
            || bone.ParentIndex < 0
            || bone.ParentIndex >= bones.Count)
        {
            return paths[index] = bone.Name;
        }

        visiting[index] = true;
        string parentPath = ResolveBonePath(
            bone.ParentIndex,
            bones,
            paths,
            visiting);
        visiting[index] = false;
        return paths[index] = $"{parentPath}/{bone.Name}";
    }

    private AssetItemViewModel CreateAssetItem(
        RetailAssetRecord asset)
    {
        AssetKind kind;
        if (asset.Id.Namespace == RetailAssetNamespace.RpackResource)
        {
            kind = asset.Id.ResourceType switch
            {
                Rp6lResourceTypes.Mesh => AssetKind.Mesh,
                Rp6lResourceTypes.Skin => AssetKind.CharacterPreset,
                Rp6lResourceTypes.Animation => AssetKind.Animation,
                Rp6lResourceTypes.AnimationScript =>
                    AssetKind.AnimationScript,
                _ => AssetKind.Unknown,
            };
        }
        else
        {
            kind = Path.GetExtension(asset.Id.Name)
                .ToLowerInvariant() switch
            {
                ".fed" => AssetKind.FacialDefinition,
                ".dds" => AssetKind.Texture,
                _ => AssetKind.Unknown,
            };
        }

        _assetWorkspace.TryGetCachedMeshProfile(
            asset.Id,
            out Dl1RetailMeshProfile? profile);
        return new AssetItemViewModel(
            asset.Id.StableKey,
            asset.DisplayName,
            kind,
            asset.Source.ProviderId,
            asset.Id.LogicalId.StableKey,
            asset,
            profile);
    }

    private JobViewModel AddJob(
        string name,
        string stage,
        string state)
    {
        JobViewModel job = new(name, stage, state);
        Jobs.Insert(0, job);
        while (Jobs.Count > 24)
        {
            JobViewModel oldest = Jobs[^1];
            Jobs.RemoveAt(Jobs.Count - 1);
            oldest.Dispose();
        }

        return job;
    }

    private void OnBoneTransformApplied(
        object? sender,
        SkeletonNodeViewModel bone)
    {
        ArgumentNullException.ThrowIfNull(bone);
        string? validationError = null;
        if (ReferenceEquals(BoneEditor.Bone, bone) &&
            BoneEditor.TryGetTransform(
                out TransformTRS transform,
                out validationError))
        {
            PersistBoneKeyframe(
                bone,
                transform);
            return;
        }

        AddDiagnostic(
            "Error",
            "Bone editor",
            $"Could not store {bone.Path}",
            validationError ??
                "The numeric transform is invalid.");
    }

    private void OnBoneGizmoModeChanged(
        object? sender,
        EventArgs args)
    {
        OnBoneGizmoConfigurationChanged("mode");
    }

    private void OnBoneGizmoSpaceChanged(
        object? sender,
        EventArgs args)
    {
        OnBoneGizmoConfigurationChanged("space");
    }

    private void OnBoneGizmoConfigurationChanged(string setting)
    {
        if (_boneGizmoDrag is not null)
        {
            CancelBoneGizmoDrag(refreshPreview: true);
            StatusText =
                $"Canceled the active transform drag because gizmo {setting} changed";
            return;
        }

        RefreshEditableSkeletonPreview();
    }

    private bool TryBeginBoneGizmoDrag(
        ViewportSide side,
        RenderTransformGizmoDragStart start)
    {
        if (_boneGizmoDrag is not null ||
            IsBusy ||
            SelectedBone is not { IsLocked: false } bone ||
            start.Binding.BoneIndex != bone.Index ||
            !Enum.IsDefined(start.Binding.Mode) ||
            !Enum.IsDefined(start.Binding.Axis) ||
            !Enum.IsDefined(start.Binding.Space) ||
            start.Binding.Mode != BoneEditor.GizmoMode ||
            start.Binding.Space != BoneEditor.EffectiveGizmoSpace ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out _) ||
            (side == ViewportSide.Target &&
             _viewportCoordinator.HasTargetPreviewCameraOverride) ||
            !IsFinite(start.AxisDirectionWorld) ||
            start.AxisDirectionWorld.LengthSquared() < 1.0e-8f)
        {
            return false;
        }

        RenderFrameSnapshot frame = side == ViewportSide.Source
            ? SourceViewport.SceneSource.CaptureFrame()
            : TargetViewport.SceneSource.CaptureFrame();
        if (frame.Skeleton is not { } skeleton ||
            bone.Index < 0 ||
            bone.Index >= skeleton.Bones.Count)
        {
            return false;
        }

        Matrix4x4 selectedWorld =
            skeleton.Bones[bone.Index].WorldTransform *
            skeleton.RootTransform;
        if (!IsFinite(selectedWorld) ||
            !Matrix4x4.Decompose(
                selectedWorld,
                out _,
                out System.Numerics.Quaternion selectedWorldRotation,
                out _) ||
            !float.IsFinite(selectedWorldRotation.X) ||
            !float.IsFinite(selectedWorldRotation.Y) ||
            !float.IsFinite(selectedWorldRotation.Z) ||
            !float.IsFinite(selectedWorldRotation.W) ||
            selectedWorldRotation.LengthSquared() < 1.0e-8f)
        {
            return false;
        }

        selectedWorldRotation =
            System.Numerics.Quaternion.Normalize(
                selectedWorldRotation);
        System.Numerics.Quaternion worldToSelectedRotation =
            System.Numerics.Quaternion.Inverse(
                selectedWorldRotation);
        Vector3 axisDirectionWorld =
            Vector3.Normalize(start.AxisDirectionWorld);
        if (!IsFinite(axisDirectionWorld) ||
            !float.IsFinite(worldToSelectedRotation.X) ||
            !float.IsFinite(worldToSelectedRotation.Y) ||
            !float.IsFinite(worldToSelectedRotation.Z) ||
            !float.IsFinite(worldToSelectedRotation.W))
        {
            return false;
        }

        int parentIndex =
            skeleton.Bones[bone.Index].ParentIndex;
        Matrix4x4 parentWorld = skeleton.RootTransform;
        if (parentIndex >= 0)
        {
            if (parentIndex >= skeleton.Bones.Count)
            {
                return false;
            }

            parentWorld =
                skeleton.Bones[parentIndex].WorldTransform *
                skeleton.RootTransform;
        }

        if (!IsFinite(parentWorld) ||
            !Matrix4x4.Invert(
                parentWorld,
                out Matrix4x4 worldToParentLocal) ||
            !IsFinite(worldToParentLocal))
        {
            return false;
        }

        if (!BoneEditor.TryGetTransform(
                out TransformTRS initial,
                out _) ||
            !IsValidBoneGizmoTransform(initial))
        {
            return false;
        }

        double frameNumber = Math.Min(
            Timeline.CurrentFrame,
            animation.FrameCount - 1);
        _boneGizmoDrag = new BoneGizmoDragContext(
            side,
            bone,
            start.Binding,
            initial,
            worldToParentLocal,
            worldToSelectedRotation,
            axisDirectionWorld,
            _project,
            animation.Id,
            frameNumber,
            SelectedBoneEditLayer?.Id,
            Guid.NewGuid());
        StatusText =
            $"Dragging {start.Binding.Axis} {start.Binding.Mode.ToString().ToLowerInvariant()} in {start.Binding.Space} space";
        return true;
    }

    private bool UpdateBoneGizmoDrag(
        ViewportSide side,
        RenderTransformGizmoDragUpdate update)
    {
        if (_boneGizmoDrag is not { } drag ||
            drag.Side != side ||
            drag.Binding != update.Binding ||
            !ReferenceEquals(SelectedBone, drag.Bone) ||
            drag.Bone.IsLocked ||
            !IsBoneGizmoDestinationCurrent(drag) ||
            (side == ViewportSide.Target &&
             _viewportCoordinator.HasTargetPreviewCameraOverride) ||
            !IsFinite(update.WorldDelta) ||
            !float.IsFinite(update.AxisDistance) ||
            !float.IsFinite(update.RotationRadians) ||
            !float.IsFinite(update.ScaleFactor) ||
            update.ScaleFactor <= 0.0f)
        {
            return false;
        }

        if (!TryApplyBoneGizmoUpdate(
                drag,
                update,
                out TransformTRS transformed,
                out bool meaningful))
        {
            return false;
        }

        drag.CurrentTransform = transformed;
        ApplyTransformToBone(drag.Bone, transformed);
        drag.HasMeaningfulMovement = meaningful;
        RefreshEditableSkeletonPreview();
        return true;
    }

    private void CompleteBoneGizmoDrag(
        ViewportSide side,
        bool commit)
    {
        if (_boneGizmoDrag is not { } drag)
        {
            return;
        }

        _boneGizmoDrag = null;
        bool shouldCommit =
            drag.Side == side &&
            commit &&
            drag.HasMeaningfulMovement &&
            ReferenceEquals(SelectedBone, drag.Bone) &&
            !drag.Bone.IsLocked &&
            IsBoneGizmoDestinationCurrent(drag) &&
            (side != ViewportSide.Target ||
             !_viewportCoordinator.HasTargetPreviewCameraOverride);
        if (shouldCommit)
        {
            PersistBoneKeyframe(
                drag.Bone,
                drag.CurrentTransform,
                drag);
            return;
        }

        RestoreBoneGizmoTransform(drag);
        RefreshEditableSkeletonPreview();
        StatusText = "Transform drag canceled";
    }

    private void CancelBoneGizmoDrag(bool refreshPreview)
    {
        if (_boneGizmoDrag is not { } drag)
        {
            return;
        }

        _boneGizmoDrag = null;
        RestoreBoneGizmoTransform(drag);
        if (refreshPreview)
        {
            RefreshEditableSkeletonPreview();
        }
    }

    private void RestoreBoneGizmoTransform(
        BoneGizmoDragContext drag)
    {
        ApplyTransformToBone(
            drag.Bone,
            drag.InitialTransform);
    }

    private static bool TryApplyBoneGizmoUpdate(
        BoneGizmoDragContext drag,
        RenderTransformGizmoDragUpdate update,
        out TransformTRS transformed,
        out bool meaningful)
    {
        transformed = drag.InitialTransform;
        meaningful = false;
        switch (drag.Binding.Mode)
        {
            case RenderTransformGizmoMode.Translate:
                {
                    Vector3 localDelta = Vector3.TransformNormal(
                        update.WorldDelta,
                        drag.WorldToParentLocal);
                    if (!IsFinite(localDelta))
                    {
                        return false;
                    }

                    transformed = drag.InitialTransform with
                    {
                        Translation =
                            drag.InitialTransform.Translation +
                            new Vector3D(
                                localDelta.X,
                                localDelta.Y,
                                localDelta.Z),
                    };
                    meaningful =
                        MathF.Abs(update.AxisDistance) > 1.0e-6f &&
                        localDelta.LengthSquared() > 1.0e-12f;
                    break;
                }

            case RenderTransformGizmoMode.Rotate:
                {
                    if (!TryCreateGizmoRotation(
                            drag,
                            update.RotationRadians,
                            out QuaternionD rotation))
                    {
                        return false;
                    }

                    transformed = drag.InitialTransform with
                    {
                        Rotation = rotation,
                    };
                    QuaternionD initial =
                        drag.InitialTransform.Rotation.Normalized();
                    meaningful =
                        MathF.Abs(update.RotationRadians) > 1.0e-5f &&
                        1.0 - Math.Abs(
                            QuaternionD.Dot(initial, rotation)) >
                        1.0e-12;
                    break;
                }

            case RenderTransformGizmoMode.Scale:
                {
                    Vector3D initial = drag.InitialTransform.Scale;
                    double selected = drag.Binding.Axis switch
                    {
                        RenderTransformGizmoAxis.X => initial.X,
                        RenderTransformGizmoAxis.Y => initial.Y,
                        RenderTransformGizmoAxis.Z => initial.Z,
                        _ => double.NaN,
                    };
                    double scaled = Math.Clamp(
                        selected * update.ScaleFactor,
                        BoneTransformAuthoringPolicy.MinimumScale,
                        BoneTransformAuthoringPolicy.MaximumScale);
                    if (!double.IsFinite(scaled) || scaled <= 0.0)
                    {
                        return false;
                    }

                    Vector3D scale = drag.Binding.Axis switch
                    {
                        RenderTransformGizmoAxis.X =>
                            new Vector3D(scaled, initial.Y, initial.Z),
                        RenderTransformGizmoAxis.Y =>
                            new Vector3D(initial.X, scaled, initial.Z),
                        RenderTransformGizmoAxis.Z =>
                            new Vector3D(initial.X, initial.Y, scaled),
                        _ => default,
                    };
                    transformed = drag.InitialTransform with
                    {
                        Scale = scale,
                    };
                    meaningful =
                        Math.Abs(scaled - selected) > 1.0e-9;
                    break;
                }

            default:
                return false;
        }

        return IsValidBoneGizmoTransform(transformed);
    }

    private static bool TryCreateGizmoRotation(
        BoneGizmoDragContext drag,
        float rotationRadians,
        out QuaternionD rotation)
    {
        rotation = default;
        if (!float.IsFinite(rotationRadians))
        {
            return false;
        }

        if (drag.Binding.Space is not
            (RenderGizmoSpace.Local or RenderGizmoSpace.Global))
        {
            return false;
        }

        Vector3 localAxis = Vector3.Transform(
            drag.AxisDirectionWorld,
            drag.WorldToSelectedRotation);
        if (!TryNormalizeAxis(ref localAxis))
        {
            return false;
        }

        try
        {
            var axis = new Vector3D(
                localAxis.X,
                localAxis.Y,
                localAxis.Z);
            QuaternionD initial =
                drag.InitialTransform.Rotation.Normalized();
            QuaternionD delta = QuaternionD.FromAxisAngle(
                axis,
                rotationRadians);
            rotation = (initial * delta).Normalized();
            return rotation.IsFinite;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsValidBoneGizmoTransform(
        TransformTRS transform) =>
        transform.IsFinite &&
        transform.Rotation.LengthSquared > 1.0e-12 &&
        IsValidBoneGizmoScale(transform.Scale);

    private static bool IsValidBoneGizmoScale(Vector3D scale) =>
        BoneTransformAuthoringPolicy.IsValidScale(scale);

    private void ApplyTransformToBone(
        SkeletonNodeViewModel bone,
        TransformTRS transform)
    {
        if (ReferenceEquals(BoneEditor.Bone, bone))
        {
            BoneEditor.SetTransform(transform);
            return;
        }

        bone.PositionX = transform.Translation.X;
        bone.PositionY = transform.Translation.Y;
        bone.PositionZ = transform.Translation.Z;
        bone.ScaleX = transform.Scale.X;
        bone.ScaleY = transform.Scale.Y;
        bone.ScaleZ = transform.Scale.Z;
    }

    private void OnTimelineKeyframeRequested(
        object? sender,
        EventArgs args)
    {
        if (SelectedBone is { } bone)
        {
            PersistBoneKeyframe(bone);
        }
        else
        {
            AddDiagnostic(
                "Warning",
                "Bone editor",
                "Select a bone before adding a transform key",
                null);
        }
    }

    private void PersistBoneKeyframe(
        SkeletonNodeViewModel bone,
        TransformTRS? exactTransform = null,
        BoneGizmoDragContext? destination = null)
    {
        if (!TryResolveBoneEditDestination(
                destination,
                out ProjectAnimation animation,
                out int animationIndex,
                out double frame,
                out Guid? preferredLayerId))
        {
            AddDiagnostic(
                "Warning",
                "Bone editor",
                destination is null
                    ? "No project animation is active"
                    : "The transform drag destination changed before commit",
                destination is null
                    ? "Open a .dlraproj containing a real source animation before authoring bone keys. No synthetic project asset was created."
                    : "The project, animation, frame, or selected edit layer no longer matches the immutable destination captured when the drag began.");
            StatusText = destination is null
                ? "Bone edit not stored: no active project animation"
                : "Transform drag canceled: authoring destination changed";
            return;
        }

        if (bone.Index < 0)
        {
            AddDiagnostic(
                "Error",
                "Bone editor",
                "The selected bone has an invalid index",
                bone.Path);
            return;
        }

        try
        {
            TransformTRS value;
            string? validationError = null;
            if (exactTransform is { } supplied)
            {
                value = supplied;
            }
            else if (ReferenceEquals(BoneEditor.Bone, bone) &&
                     BoneEditor.TryGetTransform(
                         out TransformTRS editorTransform,
                         out validationError))
            {
                value = editorTransform;
            }
            else
            {
                throw new InvalidOperationException(
                    validationError ??
                    "The selected bone transform is not available from the numeric editor.");
            }

            if (!value.IsFinite ||
                value.Rotation.LengthSquared <= 1.0e-12 ||
                !BoneTransformAuthoringPolicy.IsValidScale(
                    value.Scale))
            {
                throw new InvalidOperationException(
                    $"The authored bone transform must contain finite values, a non-zero quaternion, and scale axes from {BoneTransformAuthoringPolicy.MinimumScale:G} through {BoneTransformAuthoringPolicy.MaximumScale:G}.");
            }

            ImmutableArray<BoneEditLayer> layers =
                UpsertBoneKeyframe(
                    animation.EditLayers,
                    bone.Index,
                    frame,
                    value,
                    Guid.NewGuid(),
                    preferredLayerId);
            string targetLayerName =
                preferredLayerId is { } layerId
                    ? animation.EditLayers
                        .FirstOrDefault(layer =>
                            layer.Id == layerId)
                        ?.Name ??
                        EditorLayerName
                    : EditorLayerName;
            ProjectAnimation updatedAnimation = animation with
            {
                EditLayers = layers,
            };
            CommitProject(_project with
            {
                Animations = _project.Animations.SetItem(
                    animationIndex,
                    updatedAnimation),
            });
            AddDiagnostic(
                "Info",
                "Bone editor",
                $"Stored {bone.Path} at frame {frame:N0}",
                $"Authored in immutable '{targetLayerName}' layer; the decoded rest hierarchy was not mutated.");
            StatusText = $"Keyed {bone.Name} at frame {frame:N0}";
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            AddDiagnostic(
                "Error",
                "Bone editor",
                $"Could not store {bone.Path}",
                exception.Message);
        }
    }

    private bool TryResolveBoneEditDestination(
        BoneGizmoDragContext? destination,
        out ProjectAnimation animation,
        out int animationIndex,
        out double frame,
        out Guid? preferredLayerId)
    {
        if (destination is null)
        {
            if (!TryGetActiveAnimation(
                    out animation,
                    out animationIndex))
            {
                frame = 0.0;
                preferredLayerId = null;
                return false;
            }

            frame = Math.Min(
                Timeline.CurrentFrame,
                animation.FrameCount - 1);
            preferredLayerId =
                SelectedBoneEditLayer?.Id;
            return true;
        }

        if (!IsBoneGizmoDestinationCurrent(destination))
        {
            animation = null!;
            animationIndex = -1;
            frame = 0.0;
            preferredLayerId = null;
            return false;
        }

        for (int index = 0;
             index < _project.Animations.Length;
             index++)
        {
            if (_project.Animations[index].Id !=
                destination.AnimationId)
            {
                continue;
            }

            animation = _project.Animations[index];
            animationIndex = index;
            frame = destination.Frame;
            preferredLayerId =
                destination.PreferredLayerId;
            return true;
        }

        animation = null!;
        animationIndex = -1;
        frame = 0.0;
        preferredLayerId = null;
        return false;
    }

    private bool IsBoneGizmoDestinationCurrent(
        BoneGizmoDragContext drag)
    {
        if (!ReferenceEquals(_project, drag.Project) ||
            _activeAnimationId != drag.AnimationId ||
            SelectedBoneEditLayer?.Id !=
                drag.PreferredLayerId)
        {
            return false;
        }

        ProjectAnimation? animation = GetActiveAnimation();
        if (animation is null ||
            animation.Id != drag.AnimationId)
        {
            return false;
        }

        double currentFrame = Math.Min(
            Timeline.CurrentFrame,
            animation.FrameCount - 1);
        return Math.Abs(currentFrame - drag.Frame) <= 1.0e-9;
    }

    private static ImmutableArray<BoneEditLayer>
        UpsertBoneKeyframe(
            ImmutableArray<BoneEditLayer> layers,
            int boneIndex,
            double frame,
            TransformTRS value,
            Guid newLayerId,
            Guid? preferredLayerId)
    {
        int layerIndex = FindEditorLayerIndex(
            layers,
            preferredLayerId);
        BoneEditLayer layer = layerIndex >= 0
            ? layers[layerIndex]
            : new BoneEditLayer(
                newLayerId,
                EditorLayerName,
                BoneEditBlendMode.Additive,
                BoneEditLayerScope.AuthoredExportable,
                1.0,
                []);
        ImmutableArray<BoneEditTrack> tracks =
            layer.Tracks;
        int trackIndex = FindTrackIndex(
            tracks,
            boneIndex);
        IEnumerable<TransformKeyframe> existingKeys =
            trackIndex >= 0
                ? tracks[trackIndex].Keyframes
                : [];
        ImmutableArray<TransformKeyframe> keys =
            existingKeys
                .Where(key =>
                    Math.Abs(key.Frame - frame) >
                    1.0e-9)
                .Append(new TransformKeyframe(
                    frame,
                    value))
                .OrderBy(static key => key.Frame)
                .ToImmutableArray();
        BoneEditTrack track = new(
            boneIndex,
            keys,
            trackIndex >= 0
                ? tracks[trackIndex].Interpolation
                : BoneEditInterpolation.Linear);
        tracks = trackIndex >= 0
            ? tracks.SetItem(trackIndex, track)
            : tracks.Add(track);
        BoneEditLayer updatedLayer = new(
            layer.Id,
            layer.Name,
            layer.BlendMode,
            layer.Scope,
            layer.Weight,
            tracks,
            layer.Enabled,
            layer.BoneMask);
        return layerIndex >= 0
            ? layers.SetItem(layerIndex, updatedLayer)
            : layers.Add(updatedLayer);
    }

    private void OnTimelineFrameChanged(object? sender, EventArgs args)
    {
        _editorSessionCoordinator.SynchronizeTimeline(
            _activeAnimationId,
            Math.Max(0, Timeline.CurrentFrame),
            Timeline.IsPlaying);
        if (_boneGizmoDrag is not null)
        {
            CancelBoneGizmoDrag(refreshPreview: false);
            StatusText =
                "Transform drag canceled because the timeline frame changed";
        }

        SyncBoneEditorFromProject();
        RefreshEditableSkeletonPreview();
    }

    private void OnTimelinePropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TimelineViewModel.IsPlaying))
        {
            _editorSessionCoordinator.SynchronizeTimeline(
                _activeAnimationId,
                Math.Max(0, Timeline.CurrentFrame),
                Timeline.IsPlaying);
        }
    }

    private SkeletonPose SampleSourcePose()
    {
        ImportedAnimationSession source = _sourceAnimation
            ?? throw new InvalidOperationException(
                "No animation source is loaded.");
        double seconds = source.Clip.FrameRate.SecondsForFrame(
            Timeline.CurrentFrame);
        return source.Clip.SamplePose(
            source.Rig,
            seconds,
            Timeline.IsLooping
                ? PlaybackMode.Loop
                : PlaybackMode.Clamp);
    }

    private void RefreshAnimationPreview()
    {
        if (!UsesLinkedTargetExternalView())
        {
            ClearLinkedTargetExternalView();
        }

        if (_sourceAnimation is not { } source)
        {
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            ClearLinkedTargetExternalView(
                evaluationUnavailable: true);
            UpdateUnevaluatedPreviewStatus(
                "Load an FBX or ANM2 source to evaluate the shared timeline.");
            return;
        }

        SkeletonPose sourcePose = SampleSourcePose();
        RigDefinition? target = _targetRig;
        if (target is null ||
            (!HasSameRigContract(source.Rig, target) &&
             _activeRetargetMap is null))
        {
            if (target is not null)
            {
                PublishBlockedTargetPreview(
                    source,
                    sourcePose,
                    target,
                    "Retarget setup required");
                SetTargetBindingStatus(
                    TargetBindingStatus.NeedsReview);
            }
            else
            {
                PublishSourceSkeletonFallback(
                    source,
                    sourcePose);
                SetTargetBindingStatus(
                    TargetBindingStatus.Invalid);
            }
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            ClearLinkedTargetExternalView(
                evaluationUnavailable: true);
            UpdateUnevaluatedPreviewStatus(
                target is null
                    ? "Use a decoded retail model as Target to evaluate the DL1 preview."
                    : "Retarget setup required. The raw source may play, while the target remains in bind pose.");
            return;
        }

        ProjectAnimation? projectAnimation = GetActiveAnimation();
        if (projectAnimation is null)
        {
            PublishSourceSkeletonFallback(
                source,
                sourcePose);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            ClearLinkedTargetExternalView(
                evaluationUnavailable: true);
            UpdateUnevaluatedPreviewStatus(
                "Create or open an animation document to evaluate the DL1 preview.");
            return;
        }

        if (HasSavedFacialSource(projectAnimation) &&
            !IsActiveFacialSourceResolved(projectAnimation))
        {
            PublishSourceSkeletonFallback(
                source,
                sourcePose);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            ClearLinkedTargetExternalView(
                evaluationUnavailable: true);
            UpdateUnevaluatedPreviewStatus(
                "The saved facial source is waiting for hash verification and exact-target decode.");
            return;
        }

        try
        {
            long generation = Interlocked.Increment(
                ref _previewGeneration);
            double seconds = source.Clip.FrameRate.SecondsForFrame(
                Timeline.CurrentFrame);
            EvaluationRequest request = CreateEvaluationRequest(
                projectAnimation,
                seconds,
                ResolvePreviewProfile(),
                Timeline.IsLooping
                    ? PlaybackMode.Loop
                    : PlaybackMode.Clamp,
                EvaluationPurpose.Preview);
            EvaluationFrame frame =
                new AnimationEvaluator().Evaluate(request);
            SkeletonRenderData rendered =
                CorePreviewAdapter.ToRenderSkeleton(
                    frame.DisplayPose,
                    SelectedBone?.Index,
                    frame.ActorWorldTransform);
            GizmoRenderData[] boneGizmos =
                BuildBoneEditGizmos(rendered);
            GizmoRenderData[] cameraGizmos =
                ActiveWorkspaceMode == "FPP" &&
                FacialFpp.ShowCameraRig
                    ? BuildCameraHelperGizmos(frame.CameraHelpers)
                    : [];
            GizmoRenderData[] targetGizmos =
                boneGizmos.Concat(cameraGizmos).ToArray();
            MorphWeight[] targetMorphs =
                frame.DisplayMorphWeights.Select(static pair =>
                    new MorphWeight(
                        pair.Key,
                        checked((float)pair.Value)))
                    .ToArray();
            MeshRenderData[] targetMeshes =
                PublishEvaluatedAttachments(
                projectAnimation,
                frame);
            ProjectAssetReference? sourceProjectAsset =
                FindProjectAsset(projectAnimation.SourceAssetId);
            string sourceFingerprint =
                sourceProjectAsset?.ContentSha256 ??
                RigSignature.Compute(source.Rig);
            string targetFingerprint =
                _targetProjectAsset?.ContentSha256 ??
                RigSignature.Compute(target);
            int publicationFrame = Math.Max(
                0,
                Timeline.CurrentFrame);
            AnimationVariantKey? activeVariant = null;
            try
            {
                activeVariant = AnimationVariantKey.Create(
                    projectAnimation,
                    _project.Assets.ToDictionary(
                        static asset => asset.Id));
            }
            catch (ArgumentException)
            {
                // Incomplete legacy local projects remain previewable only
                // through their explicit source-binding checks; they do not
                // receive a reusable target-variant identity.
            }

            _editorSessionCoordinator.SynchronizeSession(
                projectAnimation.Id,
                activeVariant,
                new EditorSessionBinding(
                    sourceFingerprint,
                    targetFingerprint,
                    projectAnimation.MappingFingerprint),
                _targetBindingStatus,
                publicationFrame,
                Timeline.IsPlaying);
            PreviewPublicationToken publicationToken =
                _editorSessionCoordinator.CreatePublicationToken(
                    projectAnimation.Id,
                    sourceFingerprint,
                    targetFingerprint,
                    projectAnimation.MappingFingerprint,
                    publicationFrame);
            var framePair = new PreviewFramePair(
                publicationToken,
                generation,
                generation);
            if (!_editorSessionCoordinator.TryPublishFrame(framePair))
            {
                return;
            }

            _viewportCoordinator.PublishScenePair(() =>
            {
                PublishAuthoredSourcePreview(
                    frame,
                    generation);
                TargetViewport.SceneSource.SetScene(
                    targetMeshes,
                    rendered,
                    targetGizmos,
                    targetMorphs,
                    generation);
            });
            _lastPreviewFramePair = framePair;
            UpdateAdaptiveViewport(
                source,
                projectAnimation,
                frame);
            SetTargetBindingStatus(
                HasSameRigContract(source.Rig, target)
                    ? TargetBindingStatus.Direct
                    : TargetBindingStatus.Ready);
            SynchronizeMorphControls(
                frame.AuthoredMorphWeights);
            ApplyEvaluatedPreviewCamera(frame);
            ApplyAuthoringOverlays();
            EnsureRootMotionTrail();
            _lastPreviewDiagnostic = null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            OverflowException)
        {
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            PublishBlockedTargetPreview(
                source,
                sourcePose,
                target,
                "Retarget setup required");
            SetTargetBindingStatus(
                HasSameRigContract(source.Rig, target)
                    ? TargetBindingStatus.Invalid
                    : TargetBindingStatus.NeedsReview);
            ApplyAuthoringOverlays();
            ClearLinkedTargetExternalView(
                evaluationUnavailable: true);
            StatusText =
                $"Preview failed at frame {Timeline.CurrentFrame:0.###} — see Diagnostics";
            if (!string.Equals(
                    _lastPreviewDiagnostic,
                    exception.Message,
                    StringComparison.Ordinal))
            {
                _lastPreviewDiagnostic = exception.Message;
                AddDiagnostic(
                    "Error",
                    "Preview",
                    "The authoritative animation preview could not be evaluated",
                    exception.Message);
            }
        }
    }

    private void PublishBlockedTargetPreview(
        ImportedAnimationSession source,
        SkeletonPose sourcePose,
        RigDefinition target,
        string message)
    {
        long generation = Interlocked.Increment(
            ref _previewGeneration);
        PreviewFramePair? framePair = null;
        if (GetActiveAnimation() is { } projectAnimation)
        {
            ProjectAssetReference? sourceProjectAsset =
                FindProjectAsset(projectAnimation.SourceAssetId);
            string sourceFingerprint =
                sourceProjectAsset?.ContentSha256 ??
                RigSignature.Compute(source.Rig);
            string targetFingerprint =
                _targetProjectAsset?.ContentSha256 ??
                RigSignature.Compute(target);
            int publicationFrame = Math.Max(
                0,
                Timeline.CurrentFrame);
            AnimationVariantKey? activeVariant = null;
            try
            {
                activeVariant = AnimationVariantKey.Create(
                    projectAnimation,
                    _project.Assets.ToDictionary(
                        static asset => asset.Id));
            }
            catch (ArgumentException)
            {
                // Unnormalized local projects can still show their safe
                // source/bind-pose pair, but cannot claim reusable identity.
            }

            TargetBindingStatus blockedStatus =
                HasSameRigContract(source.Rig, target)
                    ? TargetBindingStatus.Invalid
                    : TargetBindingStatus.NeedsReview;
            _editorSessionCoordinator.SynchronizeSession(
                projectAnimation.Id,
                activeVariant,
                new EditorSessionBinding(
                    sourceFingerprint,
                    targetFingerprint,
                    projectAnimation.MappingFingerprint),
                blockedStatus,
                publicationFrame,
                Timeline.IsPlaying);
            PreviewPublicationToken publicationToken =
                _editorSessionCoordinator.CreatePublicationToken(
                    projectAnimation.Id,
                    sourceFingerprint,
                    targetFingerprint,
                    projectAnimation.MappingFingerprint,
                    publicationFrame);
            framePair = new PreviewFramePair(
                publicationToken,
                generation,
                generation);
            if (!_editorSessionCoordinator.TryPublishFrame(framePair))
            {
                return;
            }
        }

        _viewportCoordinator.PublishScenePair(() =>
        {
            SetSourcePreviewScene(
                _sourceBaseMeshes,
                CorePreviewAdapter.ToRenderSkeleton(
                    sourcePose,
                    SelectedBone?.Index),
                generation: generation);
            TargetViewport.SceneSource.SetScene(
                _targetBaseMeshes,
                CorePreviewAdapter.ToRenderSkeleton(
                    target.CreateBindPose(),
                    SelectedBone?.Index),
                [],
                generation: generation);
        });
        _lastPreviewFramePair = framePair;
        SourceViewport.SetPresentation(
            "Raw Source",
            _sourceBaseMeshes.Length == 0
                ? $"{source.SourceKind} | skeleton only"
                : "Exact immutable source model and decoded local pose");
        TargetViewport.SetPresentation(
            "DL1 Target",
            $"{message}; bind pose is held to prevent unsafe deformation");
    }

    private void PublishSourceSkeletonFallback(
        ImportedAnimationSession source,
        SkeletonPose sourcePose)
    {
        // Imported animation files currently contribute a rig and tracks, not
        // renderable source geometry. Publish this fallback only when target
        // evaluation is unavailable. Publishing it before every target solve
        // lets the render thread observe a one-frame FBX skeleton flash while
        // the timeline is being scrubbed.
        SetSourcePreviewScene(
            _sourceBaseMeshes,
            CorePreviewAdapter.ToRenderSkeleton(
                sourcePose,
                SelectedBone?.Index),
            generation: Interlocked.Increment(
                ref _previewGeneration));
        if (!UsesLinkedTargetExternalView())
        {
            SourceViewport.SetPresentation(
                "Raw Source",
                _sourceBaseMeshes.Length == 0
                    ? $"{source.SourceKind} | skeleton only (source file has no geometry)"
                    : $"{source.SourceKind} | exact bound retail source mesh and pose");
        }
        IsSourceViewportVisible = true;
    }

    internal void PublishAuthoredSourcePreview(
        EvaluationFrame frame,
        long? generation = null)
    {
        MorphWeight[] rawMorphs =
            frame.RawSourceMorphWeights.Select(static pair =>
                new MorphWeight(
                    pair.Key,
                    checked((float)pair.Value)))
                .ToArray();
        SetSourcePreviewScene(
            _sourceBaseMeshes,
            CorePreviewAdapter.ToRenderSkeleton(
                frame.RawSourcePose,
                SelectedBone?.Index),
            morphWeights: rawMorphs,
            generation: generation);
        if (!UsesLinkedTargetExternalView())
        {
            SourceViewport.SetPresentation(
                "Raw Source",
                _sourceBaseMeshes.Length == 0
                    ? "Source file has no geometry | skeleton-only exact decoded pose"
                    : "Exact source rig, retail mesh, ANM2 pose, and authored morph channels");
        }
    }

    private void UpdateAdaptiveViewport(
        ImportedAnimationSession source,
        ProjectAnimation animation,
        EvaluationFrame frame)
    {
        _ = source;
        _ = animation;
        _ = frame;
        // Layout is presentation-only. It consumes the last authoritative
        // frame pair and must never trigger evaluation or project repair.
        IsSourceViewportVisible = PreviewLayout is
            PreviewLayoutMode.RetargetComparison or
            PreviewLayoutMode.FacialComparison or
            PreviewLayoutMode.FppDualView;
    }

    internal static bool ShouldShowSourceViewport(
        bool forceCompare,
        string workspaceMode,
        bool sourceHasNoGeometry,
        bool rigsDiffer,
        bool modelsDiffer,
        bool meshesDiffer,
        bool poseDiffers,
        bool morphsDiffer,
        bool authoringLayersDiffer,
        bool accumulated) =>
        workspaceMode is
            "Retarget" or
            "Retarget/Edit" or
            "Bone Edit" or
            "Facial" or
            "Face" or
            "FPP" or
            "Cutscene";

    private static bool MorphWeightsNearlyEqual(
        ImmutableDictionary<string, double> first,
        ImmutableDictionary<string, double> second)
    {
        HashSet<string> names = first.Keys
            .Concat(second.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            first.TryGetValue(name, out double left);
            second.TryGetValue(name, out double right);
            if (Math.Abs(left - right) > 1.0e-7)
            {
                return false;
            }
        }

        return true;
    }

    private MeshRenderData[] PublishEvaluatedAttachments(
        ProjectAnimation animation,
        EvaluationFrame frame)
    {
        AttachmentSceneComposition scene =
            AttachmentSceneComposer.Compose(
                _targetBaseMeshes,
                frame.DisplayAttachments,
                _attachmentRenderAssets,
                frame.ActorWorldTransform);
        Guid? selectedBindingId =
            AttachmentEditor.SelectedAttachment?.Id;
        MeshRenderData[] presentedMeshes = scene.Meshes
            .Select(mesh =>
            {
                bool isAttachment =
                    mesh.Id.StartsWith(
                        "attachment/",
                        StringComparison.Ordinal);
                bool isSelected = selectedBindingId.HasValue
                    ? IsAttachmentMeshForBinding(
                        mesh,
                        selectedBindingId.Value)
                    : !isAttachment;
                return mesh with
                {
                    IsSelected = isSelected,
                };
            })
            .ToArray();
        FrameAttachmentCommand.NotifyCanExecuteChanged();

        Dictionary<Guid, AttachmentRenderDiagnostic> renderErrors =
            scene.Diagnostics
                .Where(static diagnostic =>
                    diagnostic.BindingId != Guid.Empty)
                .GroupBy(static diagnostic =>
                    diagnostic.BindingId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First());
        foreach (AttachmentBinding binding in
                 animation.Attachments)
        {
            string status;
            if (binding.ParentBoneIndex >=
                frame.DisplayPose.Rig.BoneCount)
            {
                status =
                    "Error: parent bone index is not in the active rig";
            }
            else
            {
                string actual =
                    frame.DisplayPose.Rig
                        .Bones[binding.ParentBoneIndex]
                        .Name;
                if (!string.IsNullOrWhiteSpace(
                        binding.ParentBoneName) &&
                    !string.Equals(
                        binding.ParentBoneName,
                        actual,
                        StringComparison.OrdinalIgnoreCase))
                {
                    status =
                        $"Error: parent is '{actual}', expected '{binding.ParentBoneName}'";
                }
                else if (!_attachmentRenderAssets.ContainsKey(
                             binding.AssetId))
                {
                    status =
                        _attachmentStatuses.TryGetValue(
                            binding.Id,
                            out string? existing)
                            ? existing
                            : "Error: retail asset is unresolved";
                }
                else if (renderErrors.TryGetValue(
                             binding.Id,
                             out AttachmentRenderDiagnostic?
                                 renderError))
                {
                    status =
                        $"Error: {renderError.Message}";
                }
                else
                {
                    int surfaceCount = presentedMeshes.Count(mesh =>
                        IsAttachmentMeshForBinding(
                            mesh,
                            binding.Id));
                    status =
                        $"{surfaceCount:N0} surface(s) visible at frame {frame.SampleFrame:0.###}" +
                        (selectedBindingId == binding.Id
                            ? " — highlighted; use Frame attachment"
                            : string.Empty);
                }
            }

            _attachmentStatuses[binding.Id] = status;
            AttachmentEditor.SetBindingStatus(
                binding.Id,
                status);
        }

        string[] messages = frame.Diagnostics
            .Where(static diagnostic =>
                diagnostic.Code.StartsWith(
                    "attachment_",
                    StringComparison.Ordinal))
            .Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")
            .Concat(scene.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static message =>
                message,
                StringComparer.Ordinal)
            .ToArray();
        string signature = string.Join('\n', messages);
        if (!string.Equals(
                signature,
                _lastAttachmentDiagnosticSignature,
                StringComparison.Ordinal))
        {
            _lastAttachmentDiagnosticSignature = signature;
            foreach (string message in messages)
            {
                AddDiagnostic(
                    "Error",
                    "Attachments",
                    "Attachment preview failed locally",
                    message);
            }
        }

        return presentedMeshes;
    }

    private EvaluationRequest CreateEvaluationRequest(
        ProjectAnimation animation,
        double seconds,
        PreviewProfile previewProfile,
        PlaybackMode playbackMode,
        EvaluationPurpose purpose)
    {
        ImportedAnimationSession source = _sourceAnimation
            ?? throw new InvalidOperationException(
                "No animation source is loaded.");
        RigDefinition target = _targetRig
            ?? throw new InvalidOperationException(
                "No retail target rig is loaded.");
        ValidateActiveSourceBinding(animation, source);
        bool directSameRig = HasSameRigContract(source.Rig, target);
        RetargetMap? mapping = directSameRig
            ? null
            : _activeRetargetMap ??
              throw new InvalidOperationException(
                  "Cross-rig playback is unavailable until a valid source-to-target mapping exists.");
        if (!directSameRig)
        {
            RetargetMappingReviewReport review =
                RetargetMappingReview.Analyze(
                    source.Rig,
                    target,
                    mapping!);
            if (!review.IsReady)
            {
                throw new InvalidOperationException(
                    "Retarget setup required. Target playback is blocked until every proposed mapping row and required target-bind fallback has been reviewed. " +
                    $"{review.ExplicitReviewRequiredCount:N0} mapping row(s) and {review.RequiredTargetBindReviewCount:N0} target-bind fallback(s) still require review.");
            }
        }
        Dl1AuthoringPolicy policy = Dl1AuthoringPolicy.Create(
            source.Rig,
            target,
            mapping,
            animation.RootMotionMode switch
            {
                Dl1RootMotionMode.Recorded =>
                    AnimationRootMode.Recorded,
                Dl1RootMotionMode.InPlace =>
                    AnimationRootMode.InPlace,
                Dl1RootMotionMode.Bip01 =>
                    AnimationRootMode.Bip01,
                Dl1RootMotionMode.MotionAccumulator =>
                    AnimationRootMode.MotionAccumulator,
                _ => throw new InvalidDataException(
                    "The project contains an unknown DL1 root-motion mode."),
            });
        ImmutableArray<MorphChannelBinding> morphBindings =
            ProjectMorphBindingResolver.Resolve(
                animation.MorphBindings,
                target,
                purpose == EvaluationPurpose.Export
                    ? ProjectMorphBindingResolutionMode.Export
                    : ProjectMorphBindingResolutionMode.Preview);
        AnimationClip evaluationClip =
            ResolveSynchronizedAnimation(
                animation,
                source);
        return new EvaluationRequest(
            source.Rig,
            target,
            evaluationClip,
            seconds,
            previewProfile,
            mapping,
            GetEvaluationEditLayers(
                animation,
                purpose),
            playbackMode: playbackMode,
            purpose: purpose,
            attachments: animation.Attachments,
            dl1AuthoringPolicy: policy,
            morphBindings: morphBindings,
            morphEditLayers: animation.MorphEditLayers,
            ikLayers: BuildIkLayers(animation, target),
            dl1PreviewInputs: CreateDl1PreviewInputs(
                previewProfile,
                purpose),
            previewMotionAccumulationEnabled:
                animation.PreviewMotionAccumulationEnabled);
    }

    private static bool HasSameRigContract(
        RigDefinition source,
        RigDefinition target) =>
        string.Equals(
            RigSignature.Compute(source),
            RigSignature.Compute(target),
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateActiveSourceBinding(
        ProjectAnimation animation,
        ImportedAnimationSession source)
    {
        if (animation.SourceBinding is not { } binding)
        {
            if (source.SourceKindContract is
                AnimationSourceKind.LocalAnm2 or
                AnimationSourceKind.RetailAnm2)
            {
                throw new InvalidOperationException(
                    "ANM2 playback is blocked because the existing document has no provable immutable source-model binding. Use Rebind Source to create a new document.");
            }

            return;
        }

        string currentSignature = RigSignature.Compute(source.Rig);
        if (!string.Equals(
                currentSignature,
                binding.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase) ||
            binding.Kind != source.SourceKindContract ||
            binding.RetailSourceModelAssetId !=
                source.RetailSourceModelAssetId ||
            (binding.Partition is { } savedPartition &&
             !string.Equals(
                 savedPartition.Fingerprint,
                 source.Partition?.Fingerprint,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "The loaded source rig or ANM2 partition differs from this animation document's immutable source binding. Playback was stopped before deformation.");
        }
    }

    internal Dl1PreviewInputs CreateDl1PreviewInputs(
        PreviewProfile previewProfile,
        EvaluationPurpose purpose)
    {
        if (purpose != EvaluationPurpose.Preview)
        {
            return Dl1PreviewInputs.Empty;
        }

        if (previewProfile.Context == Dl1PreviewContext.Dl1Movie)
        {
            if (!FacialFpp.UseMovieReferenceCameraCapture)
            {
                FacialFpp.MovieReferenceCameraStatus =
                    "No external movie reference-camera snapshot is enabled; movie preview remains on the orbit camera.";
                return Dl1PreviewInputs.Empty;
            }

            if (!FacialFpp.TryCreateMovieReferenceCameraCapture(
                    out Dl1MovieReferenceCameraCapture? movieCapture,
                    out string? movieError) ||
                movieCapture is null)
            {
                FacialFpp.MovieReferenceCameraStatus =
                    $"Movie camera requested but unavailable: {movieError}";
                return Dl1PreviewInputs.Empty;
            }

            try
            {
                Dl1MovieReferenceCameraSnapshot snapshot =
                    movieCapture.CreateSnapshot();
                FacialFpp.MovieReferenceCameraStatus =
                    "Using the explicit external IBaseCamera transform and lens. A rig RefCamera helper is not substituted, and this input is not trusted game validation.";
                return new Dl1PreviewInputs(
                    movieReferenceCamera: snapshot);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidOperationException)
            {
                FacialFpp.MovieReferenceCameraStatus =
                    $"Movie camera requested but unavailable: {exception.Message}";
                return Dl1PreviewInputs.Empty;
            }
        }

        if (previewProfile.Context != Dl1PreviewContext.Dl1Fpp)
        {
            FacialFpp.ProjectionCaptureStatus =
                FacialFpp.UseProjectionCapture
                    ? "Runtime-capture projection applies only to the DL1 FPP preview context."
                    : "No runtime-capture projection is enabled. Editor fallback values are not game validated.";
            return Dl1PreviewInputs.Empty;
        }

        // The authoring target is rendered in a fixed right-handed,
        // Y-up/-Z-forward identity model space and this editor does not
        // simulate a vehicle controller. Supplying that state explicitly
        // enables only the decompile-matched, stateless HSpine subset.
        var bodyCorrection = new Dl1FppBodyCorrectionSnapshot(
            Vector3D.UnitY,
            -Vector3D.UnitX,
            -Vector3D.UnitZ,
            vehicleControllerActive: false);
        if (!FacialFpp.UseProjectionCapture)
        {
            FacialFpp.ProjectionCaptureStatus =
                "No runtime-capture projection is enabled. Editor fallback values are not game validated.";
            return new Dl1PreviewInputs(
                fppBodyCorrection: bodyCorrection);
        }

        if (!FacialFpp.TryCreateProjectionCapture(
                out Dl1FppProjectionCapture? capture,
                out string? error) ||
            capture is null)
        {
            FacialFpp.ProjectionCaptureStatus =
                $"Capture requested but unavailable: {error}";
            return new Dl1PreviewInputs(
                fppBodyCorrection: bodyCorrection);
        }

        try
        {
            Dl1FppProjectionSnapshot snapshot =
                capture.CreateSnapshot(
                    previewProfile.CameraLens.FarClipMeters);
            FacialFpp.ProjectionCaptureStatus =
                "Using explicit user/runtime-capture scene and hands projection values. This input is not itself game validation.";
            return new Dl1PreviewInputs(
                fppProjection: snapshot,
                fppBodyCorrection: bodyCorrection);
        }
        catch (ArgumentException exception)
        {
            FacialFpp.ProjectionCaptureStatus =
                $"Capture requested but unavailable: {exception.Message}";
            return new Dl1PreviewInputs(
                fppBodyCorrection: bodyCorrection);
        }
    }

    private AnimationClip ResolveSynchronizedAnimation(
        ProjectAnimation animation,
        ImportedAnimationSession source)
    {
        if (animation.MimicAssetId is { } mimicAssetId)
        {
            if (_mimicAnimation is not { } mimic ||
                mimic.AssetId != mimicAssetId ||
                _synchronizedAnimation is not { } synchronizedMimic)
            {
                throw new InvalidOperationException(
                    "The saved mimic project asset has not been hash-checked, decoded, and synchronized against the exact retail target rig.");
            }

            return ValidateSynchronizedCadence(
                synchronizedMimic,
                animation);
        }

        if (animation.FacialSourceAssetId is { } facialSourceAssetId)
        {
            if (_facialFbxAnimation is not { } facial ||
                facial.AssetId != facialSourceAssetId ||
                facial.SourceValueUnit !=
                    animation.FacialSourceValueUnit ||
                _synchronizedAnimation is not { } synchronizedFacial)
            {
                throw new InvalidOperationException(
                    "The saved facial FBX has not been hash-checked, decoded, and synchronized against the exact body timeline and retail target rig.");
            }

            return ValidateSynchronizedCadence(
                synchronizedFacial,
                animation);
        }

        return source.Clip;
    }

    private static AnimationClip ValidateSynchronizedCadence(
        AnimationClip synchronized,
        ProjectAnimation animation)
    {
        if (synchronized.FrameRate != animation.FrameRate ||
            synchronized.FrameCount != animation.FrameCount)
        {
            throw new InvalidDataException(
                "The active synchronized body and mimic cadence differs from the saved animation.");
        }

        return synchronized;
    }

    private static bool HasSavedFacialSource(
        ProjectAnimation animation) =>
        animation.MimicAssetId is not null ||
        animation.FacialSourceAssetId is not null;

    private bool IsActiveFacialSourceResolved(
        ProjectAnimation animation)
    {
        if (_synchronizedAnimation is null)
        {
            return false;
        }

        if (animation.MimicAssetId is { } mimicAssetId)
        {
            return _mimicAnimation?.AssetId == mimicAssetId;
        }

        if (animation.FacialSourceAssetId is
            { } facialSourceAssetId)
        {
            return _facialFbxAnimation is { } facial &&
                   facial.AssetId == facialSourceAssetId &&
                   facial.SourceValueUnit ==
                       animation.FacialSourceValueUnit;
        }

        return true;
    }

    private ImmutableArray<BoneEditLayer>
        GetEvaluationEditLayers(
            ProjectAnimation animation,
            EvaluationPurpose purpose)
    {
        if (purpose != EvaluationPurpose.Preview ||
            _boneGizmoDrag is not { } drag ||
            !ReferenceEquals(SelectedBone, drag.Bone) ||
            animation.Id != drag.AnimationId ||
            !IsBoneGizmoDestinationCurrent(drag))
        {
            return animation.EditLayers;
        }

        return UpsertBoneKeyframe(
            animation.EditLayers,
            drag.Bone.Index,
            drag.Frame,
            drag.CurrentTransform,
            drag.PreviewLayerId,
            drag.PreferredLayerId);
    }

    private PreviewProfile ResolvePreviewProfile()
    {
        if (SelectedPreviewMode == RawPreviewModeLabel)
        {
            return ResolveRawPreviewProfile();
        }

        PreviewProfile baseline;
        if (ActiveWorkspaceMode == "Cutscene")
        {
            baseline = PreviewProfile.MovieAuthoring;
            return ApplySavedGameValidationEvidence(baseline);
        }

        if (ActiveWorkspaceMode != "FPP")
        {
            baseline = PreviewProfile.ThirdPersonAuthoring;
            return ApplySavedGameValidationEvidence(baseline);
        }

        baseline = PreviewProfile.FirstPersonAuthoring;
        AuthoringPreviewFidelity fidelity = FacialFpp.UseFppCamera
            ? baseline.Fidelity
            : baseline.Fidelity & ~AuthoringPreviewFidelity.Camera;
        var toggles = ImmutableArray.CreateBuilder<string>();
        if (FacialFpp.ShowHands)
        {
            toggles.Add(Dl1PreviewStageIds.FppHandsProjection);
        }

        if (FacialFpp.EnableHSpineBasisCorrection)
        {
            toggles.Add(
                Dl1PreviewStageIds.FppHSpineBasisCorrection);
        }

        if (FacialFpp.EnableHeadPositionCorrection)
        {
            toggles.Add(
                Dl1PreviewStageIds.FppHeadPositionCorrection);
        }

        if (FacialFpp.EnableHandInertia)
        {
            toggles.Add(Dl1PreviewStageIds.FppHandInertia);
        }

        if (toggles.Count == 0)
        {
            toggles.Add(Dl1PreviewStageIds.NoProceduralStages);
        }

        PreviewProfile activeProfile = new(
            baseline.Id,
            baseline.ViewMode,
            fidelity,
            baseline.VisualStyle,
            baseline.CameraBoneName,
            new CameraLens(
                FacialFpp.FieldOfView,
                baseline.CameraLens.AspectRatio,
                FacialFpp.NearPlane,
                baseline.CameraLens.FarClipMeters),
            baseline.CameraOffset,
            baseline.FidelityTier,
            baseline.Context,
            baseline.ProfileVersion,
            baseline.BuildFingerprint,
            toggles.ToImmutable(),
            baseline.MorphActivationThreshold,
            baseline.MaximumActiveMorphTargets,
            baseline.ClampMorphWeightsToRigBounds,
            baseline.CaptureFingerprint);
        return ApplySavedGameValidationEvidence(activeProfile);
    }

    private DlraProject CreateProjectWithCurrentPreviewConfiguration() =>
        SelectedPreviewMode == RawPreviewModeLabel
            ? _project with
            {
                PreviewMode = ProjectPreviewMode.Raw,
                PreviewProfile = ResolveRawPreviewProfile(),
                Dl1Settings = _project.Dl1Settings with
                {
                    ShowCameraHelpers = FacialFpp.ShowCameraRig,
                },
            }
            : _project with
            {
                PreviewMode = ProjectPreviewMode.Dl1Profile,
                PreviewProfile = ResolvePreviewProfile(),
                Dl1Settings = _project.Dl1Settings with
                {
                    ShowCameraHelpers = FacialFpp.ShowCameraRig,
                },
            };

    private void LoadPreviewConfigurationFromProject(
        DlraProject project)
    {
        PreviewProfile profile = project.PreviewProfile;
        bool isFpp =
            profile.ViewMode is
                PreviewViewMode.FirstPerson or
                PreviewViewMode.Split &&
            string.Equals(
                profile.CameraBoneName,
                Dl1PreviewContract.EyeCameraBoneName,
                StringComparison.OrdinalIgnoreCase);
        string workspaceMode = isFpp
            ? "FPP"
            : profile.Context == Dl1PreviewContext.Dl1Movie
                ? "Cutscene"
                : "Retarget";

        _synchronizingPreviewConfiguration = true;
        try
        {
            bool legacyGroupedHeadCorrection =
                profile.ProceduralToggles.Contains(
                    Dl1PreviewStageIds.FppHeadSpineCorrection,
                    StringComparer.Ordinal);
            FacialFpp.UseFppCamera =
                isFpp &&
                profile.Fidelity.HasFlag(
                    AuthoringPreviewFidelity.Camera);
            FacialFpp.ShowHands =
                !isFpp ||
                profile.ProceduralToggles.Contains(
                    Dl1PreviewStageIds.FppHandsProjection,
                    StringComparer.Ordinal);
            FacialFpp.EnableHSpineBasisCorrection =
                !isFpp ||
                legacyGroupedHeadCorrection ||
                profile.ProceduralToggles.Contains(
                    Dl1PreviewStageIds.FppHSpineBasisCorrection,
                    StringComparer.Ordinal);
            FacialFpp.EnableHeadPositionCorrection =
                isFpp &&
                (legacyGroupedHeadCorrection ||
                 profile.ProceduralToggles.Contains(
                     Dl1PreviewStageIds.FppHeadPositionCorrection,
                     StringComparer.Ordinal));
            FacialFpp.EnableHandInertia =
                isFpp &&
                profile.ProceduralToggles.Contains(
                    Dl1PreviewStageIds.FppHandInertia,
                    StringComparer.Ordinal);
            FacialFpp.ShowCameraRig =
                project.Dl1Settings.ShowCameraHelpers;
            FacialFpp.FieldOfView = checked(
                (float)profile.CameraLens
                    .VerticalFieldOfViewDegrees);
            FacialFpp.NearPlane = checked(
                (float)profile.CameraLens.NearClipMeters);
            ActiveWorkspaceMode = workspaceMode;
        }
        finally
        {
            _synchronizingPreviewConfiguration = false;
        }
    }

    private PreviewProfile ResolveRawPreviewProfile()
    {
        if (ActiveWorkspaceMode != "FPP")
        {
            return PreviewProfile.RawAuthoring;
        }

        AuthoringPreviewFidelity fidelity =
            PreviewProfile.RawAuthoring.Fidelity;
        if (!FacialFpp.UseFppCamera)
        {
            fidelity &= ~AuthoringPreviewFidelity.Camera;
        }

        return new PreviewProfile(
            "raw_fpp_authoring",
            PreviewViewMode.Split,
            fidelity,
            PreviewVisualStyle.UnlitDiagnostic,
            Dl1PreviewContract.EyeCameraBoneName,
            new CameraLens(
                FacialFpp.FieldOfView,
                PreviewProfile.RawAuthoring.CameraLens.AspectRatio,
                FacialFpp.NearPlane,
                PreviewProfile.RawAuthoring.CameraLens.FarClipMeters),
            TransformTRS.Identity,
            PreviewFidelityTier.Raw,
            Dl1PreviewContext.Raw);
    }

    private PreviewProfile ApplySavedGameValidationEvidence(
        PreviewProfile activeProfile)
    {
        PreviewProfile saved = _project.PreviewProfile;
        if (saved.FidelityTier != PreviewFidelityTier.GameValidated ||
            !HasEquivalentPreviewBehavior(saved, activeProfile))
        {
            return activeProfile;
        }

        return new PreviewProfile(
            activeProfile.Id,
            activeProfile.ViewMode,
            activeProfile.Fidelity,
            activeProfile.VisualStyle,
            activeProfile.CameraBoneName,
            activeProfile.CameraLens,
            activeProfile.CameraOffset,
            PreviewFidelityTier.GameValidated,
            activeProfile.Context,
            activeProfile.ProfileVersion,
            saved.BuildFingerprint,
            activeProfile.ProceduralToggles,
            activeProfile.MorphActivationThreshold,
            activeProfile.MaximumActiveMorphTargets,
            activeProfile.ClampMorphWeightsToRigBounds,
            saved.CaptureFingerprint);
    }

    private static bool HasEquivalentPreviewBehavior(
        PreviewProfile saved,
        PreviewProfile active) =>
        saved.ViewMode == active.ViewMode &&
        saved.Fidelity == active.Fidelity &&
        saved.VisualStyle == active.VisualStyle &&
        string.Equals(
            saved.CameraBoneName,
            active.CameraBoneName,
            StringComparison.Ordinal) &&
        saved.CameraLens == active.CameraLens &&
        saved.CameraOffset == active.CameraOffset &&
        saved.Context == active.Context &&
        saved.ProfileVersion == active.ProfileVersion &&
        saved.ProceduralToggles.SequenceEqual(
            active.ProceduralToggles,
            StringComparer.Ordinal) &&
        saved.MorphActivationThreshold ==
            active.MorphActivationThreshold &&
        saved.MaximumActiveMorphTargets ==
            active.MaximumActiveMorphTargets &&
        saved.ClampMorphWeightsToRigBounds ==
            active.ClampMorphWeightsToRigBounds;

    internal void ApplyEvaluatedPreviewCamera(EvaluationFrame frame)
    {
        if (ActiveWorkspaceMode == "Cutscene")
        {
            TargetViewport.SceneSource.SetFppProjectionState(null);
            if (frame.Camera?.Source ==
                EvaluatedCameraSource.Dl1MovieReferenceCamera)
            {
                _viewportCoordinator.SetTargetPreviewCameraOverride(
                    Dl1PreviewCameraAdapter.ToRenderCamera(
                        frame.Camera,
                        preserveLensAspectRatio: true));
                FacialFpp.PreviewStatus =
                    "Target viewport follows the explicit external DL1 movie IBaseCamera snapshot. A rig RefCamera helper is not used as a substitute.";
                PublishLinkedTargetExternalView(frame);
                return;
            }

            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            string unavailable = frame.Dl1PreviewStages
                .FirstOrDefault(static stage =>
                    stage.StageId ==
                        Dl1PreviewStageIds.MovieReferenceCamera)
                ?.Message
                ?? "No external movie reference-camera snapshot was evaluated.";
            FacialFpp.PreviewStatus =
                $"Movie camera unavailable: {unavailable}";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        bool useFppCamera =
            ActiveWorkspaceMode == "FPP" &&
            FacialFpp.UseFppCamera;
        if (!useFppCamera)
        {
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            FacialFpp.PreviewStatus = ActiveWorkspaceMode == "FPP"
                ? SelectedPreviewMode == RawPreviewModeLabel
                    ? "Raw preview is active on the shared timeline; the target viewport remains on its orbit camera."
                    : "DL1 FPP profile is active on the shared timeline; the target viewport remains on its orbit camera."
                : "Orbit preview active. Select FPP mode and enable the FPP camera to use the evaluated EyeCamera helper.";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        if (frame.Camera is null)
        {
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            string unavailable = frame.Dl1PreviewStages
                .FirstOrDefault(static stage =>
                    stage.StageId == Dl1PreviewStageIds.FppViewTransform)
                ?.Message
                ?? "The selected rig does not provide an evaluated FPP camera.";
            FacialFpp.PreviewStatus =
                $"FPP camera unavailable: {unavailable}";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        if (frame.Camera.Source !=
            EvaluatedCameraSource.Dl1FppEyeCamera)
        {
            TargetViewport.SceneSource.SetFppProjectionState(null);
            _viewportCoordinator.SetTargetPreviewCameraOverride(
                Dl1PreviewCameraAdapter.ToRenderCamera(
                    frame.Camera,
                    preserveLensAspectRatio: false));
            FacialFpp.PreviewStatus =
                "Raw FPP preview follows the evaluated EyeCamera bone with the ordinary scene lens. DL1 hands projection and procedural stages are disabled.";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        bool capturedSceneProjection =
            frame.Dl1PreviewStages.Any(static stage =>
                stage.StageId ==
                    Dl1PreviewStageIds.FppSceneProjection &&
                stage.Status == Dl1PreviewStageStatus.Applied);
        RenderProjectionParameters? handsProjection =
            frame.Camera.HandsProjection is { } evaluatedHands
                ? Dl1PreviewCameraAdapter.ToRenderProjection(
                    evaluatedHands)
                : null;
        TargetViewport.SceneSource.SetFppProjectionState(
            new RenderFppProjectionState(
                RouteHandsMeshes: true,
                SceneAspectRatio: capturedSceneProjection
                    ? checked((float)frame.Camera.Lens.AspectRatio)
                    : null,
                HandsProjection: handsProjection));
        _viewportCoordinator.SetTargetPreviewCameraOverride(
            Dl1PreviewCameraAdapter.ToRenderCamera(
                frame.Camera,
                preserveLensAspectRatio:
                    capturedSceneProjection));
        string stageSummary = string.Join(
            " | ",
            frame.Dl1PreviewStages
                .Where(static stage =>
                    stage.StageId is
                        Dl1PreviewStageIds.FppViewTransform or
                        Dl1PreviewStageIds.FppSceneProjection or
                        Dl1PreviewStageIds.FppHandsProjection or
                        Dl1PreviewStageIds.FppHSpineBasisCorrection or
                        Dl1PreviewStageIds.FppHeadPositionCorrection or
                        Dl1PreviewStageIds.FppHandInertia)
                .Select(stage =>
                    $"{Humanize(stage.StageId)}: {stage.Status}"));
        FacialFpp.PreviewStatus =
            "Target viewport follows the evaluated EyeCamera authoring fallback. " +
            (string.IsNullOrWhiteSpace(stageSummary)
                ? string.Empty
                : stageSummary);
        PublishLinkedTargetExternalView(frame);
    }

    private bool UsesLinkedTargetExternalView() =>
        ActiveWorkspaceMode is "FPP" or "Cutscene";

    private void PublishLinkedTargetExternalView(
        EvaluationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!UsesLinkedTargetExternalView())
        {
            ClearLinkedTargetExternalView();
            return;
        }

        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();
        SourceViewport.SceneSource.SetExternalPreviewScene(
            targetFrame);
        SourceViewport.SetPresentation(
            "DL1 Target / External",
            ActiveWorkspaceMode == "FPP"
                ? "Same evaluated target | free orbit | FPP hands projection disabled"
                : "Same evaluated target | free orbit | movie camera override disabled");

        if (ActiveWorkspaceMode == "Cutscene")
        {
            bool hasMovieCamera =
                frame.Camera?.Source ==
                    EvaluatedCameraSource.Dl1MovieReferenceCamera &&
                _viewportCoordinator.HasTargetPreviewCameraOverride;
            TargetViewport.SetPresentation(
                hasMovieCamera
                    ? "DL1 Target / Movie Camera"
                    : "DL1 Target / Orbit",
                hasMovieCamera
                    ? "Explicit external DL1 movie IBaseCamera | captured scene aspect"
                    : "Evaluated target | external movie camera unavailable");
            return;
        }

        bool hasFppCamera =
            frame.Camera is not null &&
            _viewportCoordinator.HasTargetPreviewCameraOverride;
        RenderFppProjectionState? projection =
            targetFrame.FppProjectionState;
        string fidelity = hasFppCamera
            ? frame.Camera!.Source ==
                EvaluatedCameraSource.Dl1FppEyeCamera
                ? projection?.HandsProjection is not null
                    ? "Evaluated EyeCamera | captured scene and separate hands projections"
                    : "Evaluated EyeCamera | hands projection unavailable or disabled"
                : "Evaluated EyeCamera profile bone | ordinary scene projection"
            : "Evaluated target | EyeCamera override disabled or unavailable";
        TargetViewport.SetPresentation(
            hasFppCamera
                ? "DL1 Target / EyeCamera"
                : "DL1 Target / Orbit",
            fidelity);
    }

    private void ClearLinkedTargetExternalView(
        bool evaluationUnavailable = false)
    {
        SourceViewport.SceneSource.SetExternalPreviewScene(null);
        if (!UsesLinkedTargetExternalView())
        {
            SourceViewport.SetPresentation(
                AuthoredSourcePaneTitle,
                AuthoredSourcePaneFidelity);
            TargetViewport.SetPresentation(
                TargetPaneTitle,
                TargetPaneFidelity);
            return;
        }

        SourceViewport.SetPresentation(
            AuthoredSourcePaneTitle,
            evaluationUnavailable
                ? "Authored scene restored | evaluated DL1 target unavailable"
                : AuthoredSourcePaneFidelity);
        TargetViewport.SetPresentation(
            ActiveWorkspaceMode == "Cutscene"
                ? "DL1 Target / Movie Camera"
                : "DL1 Target / EyeCamera",
            evaluationUnavailable
                ? "Waiting for an evaluated DL1 target scene"
                : TargetPaneFidelity);
    }

    private void RestoreLinkedTargetExternalViewFromCurrentScene()
    {
        if (!UsesLinkedTargetExternalView())
        {
            return;
        }

        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();
        SourceViewport.SceneSource.SetExternalPreviewScene(targetFrame);
        SourceViewport.SetPresentation(
            "DL1 Target / External",
            ActiveWorkspaceMode == "Cutscene"
                ? "Same evaluated target | free orbit | movie camera override disabled"
                : "Same evaluated target | free orbit | FPP hands projection disabled");
        bool hasPreviewCamera =
            _viewportCoordinator.HasTargetPreviewCameraOverride;
        TargetViewport.SetPresentation(
            ActiveWorkspaceMode == "Cutscene"
                ? hasPreviewCamera
                    ? "DL1 Target / Movie Camera"
                    : "DL1 Target / Orbit"
                : hasPreviewCamera
                    ? "DL1 Target / EyeCamera"
                    : "DL1 Target / Orbit",
            hasPreviewCamera
                ? ActiveWorkspaceMode == "Cutscene"
                    ? "Restored evaluated movie camera | current published frame"
                    : "Restored evaluated EyeCamera | current published frame"
                : "Evaluated target | free orbit until a camera is available");
    }

    private void UpdateUnevaluatedPreviewStatus(string nextStep)
    {
        FacialFpp.PreviewStatus = ActiveWorkspaceMode switch
        {
            "FPP" when
                SelectedPreviewMode == RawPreviewModeLabel &&
                FacialFpp.UseFppCamera =>
                $"Raw EyeCamera preview is requested without DL1 procedural projection. {nextStep}",
            "FPP" when SelectedPreviewMode == RawPreviewModeLabel =>
                $"Raw preview is active on the shared timeline; the target viewport remains on its orbit camera. {nextStep}",
            "FPP" when FacialFpp.UseFppCamera =>
                $"DL1 FPP camera is requested. {nextStep}",
            "FPP" =>
                $"DL1 FPP profile is active on the shared timeline; the target viewport remains on its orbit camera. {nextStep}",
            "Cutscene" when
                FacialFpp.UseMovieReferenceCameraCapture =>
                $"The explicit external DL1 movie reference camera is requested. {nextStep}",
            "Cutscene" =>
                $"DL1 movie context is active, but no external reference-camera snapshot is loaded. {nextStep}",
            _ =>
                "Orbit preview active. Select FPP mode and enable the FPP camera to use the evaluated EyeCamera helper.",
        };
    }

    private static GizmoRenderData[] BuildCameraHelperGizmos(
        ImmutableArray<EvaluatedCameraHelper> helpers)
    {
        const float axisLength = 0.16f;
        var gizmos = new List<GizmoRenderData>(
            helpers.Length * 3);
        foreach (EvaluatedCameraHelper helper in helpers)
        {
            Vector3 origin = ToRenderVector(
                helper.WorldTransform.Translation);
            Vector3 right = Vector3.Normalize(ToRenderVector(
                helper.WorldTransform.TransformDirection(
                    new Vector3D(1.0, 0.0, 0.0))));
            Vector3 up = Vector3.Normalize(ToRenderVector(
                helper.WorldTransform.TransformDirection(
                    new Vector3D(0.0, -1.0, 0.0))));
            Vector3 forward = Vector3.Normalize(ToRenderVector(
                helper.WorldTransform.TransformDirection(
                    new Vector3D(0.0, 0.0, 1.0))));
            gizmos.Add(
                new(
                    GizmoKind.Axis,
                    origin,
                    origin + (right * axisLength),
                    new Vector4(0.95f, 0.25f, 0.22f, 1.0f),
                    2.0f));
            gizmos.Add(
                new(
                    GizmoKind.Axis,
                    origin,
                    origin + (up * axisLength),
                    new Vector4(0.25f, 0.95f, 0.35f, 1.0f),
                    2.0f));
            gizmos.Add(
                new(
                    GizmoKind.Axis,
                    origin,
                    origin + (forward * axisLength),
                    new Vector4(0.25f, 0.55f, 1.0f, 1.0f),
                    2.0f));
        }

        return gizmos.ToArray();
    }

    private static Vector3 ToRenderVector(Vector3D value) =>
        new(
            checked((float)value.X),
            checked((float)value.Y),
            checked((float)value.Z));

    private static IkConstraintLayer[] BuildIkLayers(
        ProjectAnimation animation,
        RigDefinition rig)
    {
        Dictionary<string, TwoBoneIkChainDefinition> chains =
            rig.IkChains.ToDictionary(
                static chain => chain.Name,
                StringComparer.OrdinalIgnoreCase);
        List<IkConstraintLayer> layers = [];
        foreach (ProjectIkLayer layer in animation.IkLayers)
        {
            if (!chains.TryGetValue(
                    layer.ChainName,
                    out TwoBoneIkChainDefinition? chain))
            {
                continue;
            }

            layers.Add(
                new IkConstraintLayer(
                    layer.Id,
                    layer.Name,
                    chain.RootBoneIndex,
                    chain.JointBoneIndex,
                    chain.EndBoneIndex,
                    layer.Weight,
                    layer.Keyframes.Select(static key =>
                        new IkConstraintKeyframe(
                            key.Frame,
                            key.Effector,
                            key.Pole,
                            key.EndOrientation)),
                    layer.Enabled,
                    layer.BakeToEditLayer));
        }

        return layers.ToArray();
    }

    private void SyncBoneEditorFromProject()
    {
        SkeletonNodeViewModel? bone = SelectedBone;
        if (bone is null)
        {
            return;
        }

        TransformTRS edit = TrySampleEditorTransform(
            bone.Index,
            Timeline.CurrentFrame,
            out TransformTRS sampled)
                ? sampled
                : TransformTRS.Identity;
        BoneEditor.SetTransform(edit);
    }

    private void RefreshTimelineTracks()
    {
        ProjectAnimation? animation = GetActiveAnimation();
        if (animation is null)
        {
            Timeline.ReplaceTracks([]);
            Timeline.ReplaceCurves([]);
            return;
        }

        List<TimelineTrackViewModel> viewModels = [];
        List<TimelineCurveTrackViewModel> curveModels = [];
        foreach (BoneEditLayer layer in animation.EditLayers)
        {
            foreach (BoneEditTrack track in layer.Tracks)
            {
                SkeletonNodeViewModel? bone = FindBone(track.BoneIndex);
                var viewModel = new TimelineTrackViewModel(
                    bone?.Path ?? $"Bone {track.BoneIndex}",
                    $"{layer.Name} / Transform");
                foreach (TransformKeyframe keyframe in track.Keyframes)
                {
                    int frame = checked((int)Math.Round(keyframe.Frame));
                    viewModel.Keyframes.Add(
                        new TimelineKeyframeViewModel(
                            viewModel.Name,
                            frame,
                            frame * 6.0,
                            12.0));
                }

                viewModels.Add(viewModel);
                AddTransformCurves(
                    curveModels,
                    $"{layer.Name} / {viewModel.Name}",
                    track.Keyframes);
            }
        }

        foreach (MorphEditLayer layer in animation.MorphEditLayers)
        {
            foreach (MorphEditTrack track in layer.Tracks)
            {
                var viewModel = new TimelineTrackViewModel(
                    track.MorphName,
                    $"{layer.Name} / Morph");
                foreach (ScalarKeyframe keyframe in track.Keyframes)
                {
                    int frame = checked((int)Math.Round(keyframe.Frame));
                    viewModel.Keyframes.Add(
                        new TimelineKeyframeViewModel(
                            viewModel.Name,
                            frame,
                            frame * 6.0,
                            12.0));
                }

                viewModels.Add(viewModel);
                curveModels.Add(new TimelineCurveTrackViewModel(
                    $"{layer.Name} / {track.MorphName}",
                    "#E599F7",
                    track.Keyframes.Select(static key =>
                        new TimelineCurveKeyViewModel(
                            key.Frame,
                            key.Value))));
            }
        }

        foreach (ProjectIkLayer layer in animation.IkLayers)
        {
            var viewModel = new TimelineTrackViewModel(
                layer.ChainName,
                $"{layer.Name} / IK");
            foreach (ProjectIkKeyframe keyframe in layer.Keyframes)
            {
                int frame = checked((int)Math.Round(keyframe.Frame));
                viewModel.Keyframes.Add(
                    new TimelineKeyframeViewModel(
                        viewModel.Name,
                        frame,
                        frame * 6.0,
                        12.0));
            }

            viewModels.Add(viewModel);
            AddVectorCurves(
                curveModels,
                $"{layer.Name} / {layer.ChainName} / Effector",
                layer.Keyframes.Select(static key =>
                    (key.Frame, key.Effector)));
            AddVectorCurves(
                curveModels,
                $"{layer.Name} / {layer.ChainName} / Pole",
                layer.Keyframes.Select(static key =>
                    (key.Frame, key.Pole)));
        }

        foreach (AttachmentBinding attachment in
                 animation.Attachments)
        {
            string parent =
                attachment.ParentBoneName
                ?? $"Bone {attachment.ParentBoneIndex}";
            var viewModel = new TimelineTrackViewModel(
                attachment.Name,
                $"Attachment / {parent}");
            viewModel.Keyframes.Add(
                new TimelineKeyframeViewModel(
                    viewModel.Name,
                    0,
                    0,
                    12.0));
            viewModels.Add(viewModel);
            AddTransformCurves(
                curveModels,
                $"Attachment / {attachment.Name}",
                [new TransformKeyframe(
                    0.0,
                    attachment.LocalOffset)]);
        }

        Timeline.ReplaceTracks(viewModels);
        Timeline.ReplaceCurves(curveModels);
    }

    private static void AddTransformCurves(
        List<TimelineCurveTrackViewModel> curves,
        string prefix,
        ImmutableArray<TransformKeyframe> keyframes)
    {
        AddCurve(
            curves,
            $"{prefix} / Translation X",
            "#F26C6C",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.X)));
        AddCurve(
            curves,
            $"{prefix} / Translation Y",
            "#6BCB77",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.Y)));
        AddCurve(
            curves,
            $"{prefix} / Translation Z",
            "#5C7CFA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.Z)));
        AddCurve(
            curves,
            $"{prefix} / Rotation X",
            "#FFA94D",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.X)));
        AddCurve(
            curves,
            $"{prefix} / Rotation Y",
            "#38D9A9",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.Y)));
        AddCurve(
            curves,
            $"{prefix} / Rotation Z",
            "#9775FA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.Z)));
        AddCurve(
            curves,
            $"{prefix} / Rotation W",
            "#CED4DA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.W)));
        AddCurve(
            curves,
            $"{prefix} / Scale X",
            "#FF8787",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.X)));
        AddCurve(
            curves,
            $"{prefix} / Scale Y",
            "#8CE99A",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.Y)));
        AddCurve(
            curves,
            $"{prefix} / Scale Z",
            "#91A7FF",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.Z)));
    }

    private static void AddVectorCurves(
        List<TimelineCurveTrackViewModel> curves,
        string prefix,
        IEnumerable<(double Frame, Vector3D Value)> keys)
    {
        (double Frame, Vector3D Value)[] rows = keys.ToArray();
        AddCurve(
            curves,
            $"{prefix} X",
            "#F26C6C",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.X)));
        AddCurve(
            curves,
            $"{prefix} Y",
            "#6BCB77",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Y)));
        AddCurve(
            curves,
            $"{prefix} Z",
            "#5C7CFA",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Z)));
    }

    private static void AddCurve(
        List<TimelineCurveTrackViewModel> curves,
        string name,
        string color,
        IEnumerable<TimelineCurveKeyViewModel> keys) =>
        curves.Add(new TimelineCurveTrackViewModel(
            name,
            color,
            keys));

    private ProjectAnimation? GetActiveAnimation()
    {
        if (_activeAnimationId is not { } id)
        {
            return null;
        }

        return _project.Animations.FirstOrDefault(
            animation => animation.Id == id);
    }

    private bool TryGetActiveAnimation(
        out ProjectAnimation animation,
        out int animationIndex)
    {
        if (_activeAnimationId is { } id)
        {
            for (int index = 0; index < _project.Animations.Length; index++)
            {
                if (_project.Animations[index].Id == id)
                {
                    animation = _project.Animations[index];
                    animationIndex = index;
                    return true;
                }
            }
        }

        animation = null!;
        animationIndex = -1;
        return false;
    }

    private static int FindEditorLayerIndex(
        ImmutableArray<BoneEditLayer> layers,
        Guid? preferredLayerId = null)
    {
        if (preferredLayerId.HasValue)
        {
            for (int index = 0; index < layers.Length; index++)
            {
                if (layers[index].Id ==
                    preferredLayerId.Value)
                {
                    return index;
                }
            }
        }

        for (int index = 0; index < layers.Length; index++)
        {
            if (string.Equals(
                    layers[index].Name,
                    EditorLayerName,
                    StringComparison.Ordinal)
                && layers[index].Scope ==
                BoneEditLayerScope.AuthoredExportable)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindFacialEditorLayerIndex(
        ImmutableArray<MorphEditLayer> layers)
    {
        for (int index = 0; index < layers.Length; index++)
        {
            if (string.Equals(
                layers[index].Name,
                    FacialEditorLayerName,
                    StringComparison.Ordinal) &&
                layers[index].Scope ==
                MorphEditLayerScope.AuthoredExportable)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMorphTrackIndex(
        ImmutableArray<MorphEditTrack> tracks,
        string morphName)
    {
        for (int index = 0; index < tracks.Length; index++)
        {
            if (string.Equals(
                    tracks[index].MorphName,
                    morphName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindScalarKeyIndex(
        ImmutableArray<ScalarKeyframe> keys,
        double frame)
    {
        for (int index = 0; index < keys.Length; index++)
        {
            if (Math.Abs(keys[index].Frame - frame) <= 1e-9)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindProjectIkLayerIndex(
        ImmutableArray<ProjectIkLayer> layers,
        string chainName)
    {
        for (int index = 0; index < layers.Length; index++)
        {
            if (string.Equals(
                    layers[index].ChainName,
                    chainName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindProjectIkKeyIndex(
        ImmutableArray<ProjectIkKeyframe> keys,
        double frame)
    {
        for (int index = 0; index < keys.Length; index++)
        {
            if (Math.Abs(keys[index].Frame - frame) <= 1e-9)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindAttachmentIndex(
        ImmutableArray<AttachmentBinding> bindings,
        Guid bindingId)
    {
        for (int index = 0; index < bindings.Length; index++)
        {
            if (bindings[index].Id == bindingId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindTrackIndex(
        ImmutableArray<BoneEditTrack> tracks,
        int boneIndex)
    {
        for (int index = 0; index < tracks.Length; index++)
        {
            if (tracks[index].BoneIndex == boneIndex)
            {
                return index;
            }
        }

        return -1;
    }

    private bool TrySampleEditorTransform(
        int boneIndex,
        double frame,
        out TransformTRS transform)
    {
        ProjectAnimation? animation = GetActiveAnimation();
        if (animation is not null)
        {
            int layerIndex = FindEditorLayerIndex(
                animation.EditLayers,
                SelectedBoneEditLayer?.Id);
            if (layerIndex >= 0)
            {
                BoneEditLayer layer = animation.EditLayers[layerIndex];
                int trackIndex = FindTrackIndex(layer.Tracks, boneIndex);
                if (trackIndex >= 0)
                {
                    transform = layer.Tracks[trackIndex].Sample(frame);
                    return true;
                }
            }
        }

        transform = TransformTRS.Identity;
        return false;
    }

    private void OnLensChanged(object? sender, EventArgs args)
    {
        if (_synchronizingPreviewConfiguration)
        {
            return;
        }

        _viewportCoordinator.UpdateLens(
            FacialFpp.FieldOfView,
            FacialFpp.NearPlane);
        RefreshAnimationPreview();
        UpdateFidelityStatusBadges();
    }

    private void OnFacialFppPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_synchronizingFppProjectionCapture ||
            _synchronizingMovieReferenceCameraCapture ||
            _synchronizingPreviewConfiguration)
        {
            return;
        }

        if (string.Equals(
                args.PropertyName,
                nameof(
                    FacialFppViewModel
                        .SelectedFacialSourceValueUnit),
                StringComparison.Ordinal))
        {
            ImportFacialFbxCommand.NotifyCanExecuteChanged();
        }

        if (args.PropertyName is
                nameof(FacialFppViewModel.UseFppCamera) or
                nameof(FacialFppViewModel.ShowHands) or
                nameof(FacialFppViewModel.ShowCameraRig) or
                nameof(
                    FacialFppViewModel
                        .EnableHSpineBasisCorrection) or
                nameof(
                    FacialFppViewModel
                        .EnableHeadPositionCorrection) or
                nameof(FacialFppViewModel.EnableHandInertia) or
                nameof(FacialFppViewModel.UseProjectionCapture) or
                nameof(FacialFppViewModel.SceneCaptureFieldOfView) or
                nameof(FacialFppViewModel.SceneCaptureAspectRatio) or
                nameof(FacialFppViewModel.SceneCaptureNearPlane) or
                nameof(FacialFppViewModel.HandsCaptureFieldOfView) or
                nameof(FacialFppViewModel.HandsCaptureFieldOfViewAxis) or
                nameof(FacialFppViewModel.HandsCaptureAspectRatio) or
                nameof(FacialFppViewModel.HandsCaptureNearPlane) or
                nameof(FacialFppViewModel.UseMovieReferenceCameraCapture) or
                nameof(FacialFppViewModel.MovieCameraPositionX) or
                nameof(FacialFppViewModel.MovieCameraPositionY) or
                nameof(FacialFppViewModel.MovieCameraPositionZ) or
                nameof(FacialFppViewModel.MovieCameraRotationX) or
                nameof(FacialFppViewModel.MovieCameraRotationY) or
                nameof(FacialFppViewModel.MovieCameraRotationZ) or
                nameof(FacialFppViewModel.MovieCameraRotationW) or
                nameof(FacialFppViewModel.MovieCameraVerticalFieldOfView) or
                nameof(FacialFppViewModel.MovieCameraAspectRatio) or
                nameof(FacialFppViewModel.MovieCameraNearPlane) or
                nameof(FacialFppViewModel.MovieCameraFarPlane))
        {
            RefreshAnimationPreview();
            UpdateFidelityStatusBadges();
        }
    }

    private void OnIkEditorPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (string.Equals(
                args.PropertyName,
                nameof(IkConstraintEditorViewModel.SelectedChain),
                StringComparison.Ordinal))
        {
            InitializeIkEditorFromBindPose();
            SynchronizeIkEditorLayerSettings(
                GetActiveAnimation());
            KeyIkConstraintCommand.NotifyCanExecuteChanged();
            BakeIkConstraintCommand.NotifyCanExecuteChanged();
        }
        else if (string.Equals(
                     args.PropertyName,
                     nameof(IkConstraintEditorViewModel.BakeToEditLayer),
                     StringComparison.Ordinal))
        {
            BakeIkConstraintCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnAttachmentEditorPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName is
                nameof(AttachmentEditorViewModel.SelectedCatalogAsset) or
                nameof(AttachmentEditorViewModel.SelectedParentBone) or
                nameof(AttachmentEditorViewModel.SelectedAttachment))
        {
            NotifyAttachmentCommands();
            if (string.Equals(
                    args.PropertyName,
                    nameof(AttachmentEditorViewModel.SelectedAttachment),
                    StringComparison.Ordinal) &&
                !_synchronizingProjectBindings)
            {
                if (AttachmentEditor.SelectedAttachment is not null)
                {
                    HighlightSelectedMeshes = true;
                }

                RefreshAnimationPreview();
            }
        }
    }

    private void NotifyAttachmentCommands()
    {
        AddAttachmentCommand.NotifyCanExecuteChanged();
        ApplyAttachmentCommand.NotifyCanExecuteChanged();
        RemoveAttachmentCommand.NotifyCanExecuteChanged();
        FrameAttachmentCommand.NotifyCanExecuteChanged();
    }

    private void OnMorphWeightsChanged(object? sender, EventArgs args)
    {
        if (_synchronizingMorphWeights)
        {
            return;
        }

        MorphWeight[] weights = CreatePreviewMorphWeights(
            FacialFpp.Morphs,
            _targetRig,
            ResolvePreviewProfile(),
            Timeline.CurrentFrame);
        bool showingExternalTarget =
            SourceViewport.SceneSource.HasExternalPreviewScene;
        if (!showingExternalTarget)
        {
            SourceViewport.SceneSource.SetMorphWeights(weights);
        }

        TargetViewport.SceneSource.SetMorphWeights(weights);
        if (showingExternalTarget)
        {
            SourceViewport.SceneSource.SetExternalPreviewScene(
                TargetViewport.SceneSource.CaptureFrame());
            ApplyAuthoringOverlays();
        }
    }

    internal static MorphWeight[] CreatePreviewMorphWeights(
        IEnumerable<MorphChannelViewModel> morphs,
        RigDefinition? targetRig,
        PreviewProfile profile,
        double frame)
    {
        ArgumentNullException.ThrowIfNull(morphs);
        ArgumentNullException.ThrowIfNull(profile);
        Dictionary<string, double> authored = morphs
            .ToDictionary(
                static morph => morph.Name,
                static morph => (double)morph.Weight,
                StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, double> display = targetRig is null
            ? authored
            : MorphEvaluator.Evaluate(
                    authored,
                    targetRig,
                    frame,
                    profile,
                    EvaluationPurpose.Preview)
                .DisplayWeights;
        return display.Select(static pair =>
                new MorphWeight(
                    pair.Key,
                    checked((float)pair.Value)))
            .ToArray();
    }

    private void SynchronizeMorphControls(
        ImmutableDictionary<string, double> values)
    {
        _synchronizingMorphWeights = true;
        try
        {
            foreach (MorphChannelViewModel morph in FacialFpp.Morphs)
            {
                morph.Weight = values.TryGetValue(
                    morph.Name,
                    out double value)
                        ? checked((float)value)
                        : 0;
            }
        }
        finally
        {
            _synchronizingMorphWeights = false;
        }
    }

    private SkeletonNodeViewModel? FindBone(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return EnumerateSkeletonNodes().FirstOrDefault(node =>
            string.Equals(node.Path, path, StringComparison.Ordinal));
    }

    private SkeletonNodeViewModel? FindBone(int index)
    {
        return EnumerateSkeletonNodes().FirstOrDefault(
            node => node.Index == index);
    }

    private IEnumerable<SkeletonNodeViewModel> EnumerateSkeletonNodes()
    {
        Stack<SkeletonNodeViewModel> stack =
            new(SkeletonRoots.Reverse());
        while (stack.Count > 0)
        {
            SkeletonNodeViewModel current = stack.Pop();
            yield return current;
            for (int index = current.Children.Count - 1; index >= 0; index--)
            {
                stack.Push(current.Children[index]);
            }
        }
    }

    private SkeletonRenderData? BuildEditableSkeleton()
    {
        SkeletonNodeViewModel[] ordered =
            EnumerateSkeletonNodes()
                .OrderBy(static node => node.Index)
                .ToArray();
        if (ordered.Length == 0)
        {
            return null;
        }

        for (int index = 0; index < ordered.Length; index++)
        {
            SkeletonNodeViewModel node = ordered[index];
            if (node.Index != index ||
                node.ParentIndex >= index)
            {
                AddDiagnostic(
                    "Error",
                    "Bone editor",
                    "Skeleton indexes must be contiguous and topologically ordered for preview",
                    $"Expected bone index {index} with an earlier parent, received index {node.Index} and parent {node.ParentIndex} ({node.Path}).");
                return null;
            }
        }

        try
        {
            RigDefinition rig = new(
                "editor-fallback-rig",
                "Editor fallback rig",
                ordered.Select(node =>
                    new BoneDefinition(
                        node.Index,
                        node.Name,
                        node.ParentIndex,
                        CorePreviewAdapter.ToCoreMatrix(
                                node.RestLocalTransform)
                            // Retail compact matrices originate as floats and
                            // legitimately carry small orthogonality drift.
                            // Use the same tolerance as the DL1 rig decoder so
                            // rebuilding the editor pose cannot discard an
                            // otherwise validated retail skeleton.
                            .Decompose(
                                RetailRenderBindDecompositionTolerance),
                        ToCoreBoneKind(
                            node.Role,
                            node.ParentIndex))));
            SkeletonPose pose = rig.CreateBindPose();
            if (GetActiveAnimation() is { } animation)
            {
                double frame = Math.Min(
                    Timeline.CurrentFrame,
                    animation.FrameCount - 1);
                ImmutableArray<BoneEditLayer> layers =
                    GetEvaluationEditLayers(
                        animation,
                        EvaluationPurpose.Preview);
                pose = BoneEditLayerEvaluator.ApplyLayers(
                    pose,
                    frame,
                    layers,
                    BoneEditLayerScope.AuthoredExportable);
                pose = BoneEditLayerEvaluator.ApplyLayers(
                    pose,
                    frame,
                    layers,
                    BoneEditLayerScope.PreviewOnly);
            }

            SkeletonRenderData rendered =
                CorePreviewAdapter.ToRenderSkeleton(
                pose,
                SelectedBone?.Index);
            return rendered with
            {
                Bones = rendered.Bones
                    .Select((bone, index) => bone with
                    {
                        Role = ordered[index].Role,
                        IsHierarchyOverlayVisible =
                            ordered[index]
                                .IsHierarchyOverlayVisible,
                    })
                    .ToArray(),
            };
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            AddDiagnostic(
                "Error",
                "Bone editor",
                "The fallback skeleton could not be evaluated through the authoritative edit-layer pipeline",
                exception.Message);
            return null;
        }
    }

    private static BoneKind ToCoreBoneKind(
        BoneRenderRole role,
        int parentIndex) =>
        role switch
        {
            BoneRenderRole.Deform when parentIndex < 0 =>
                BoneKind.Root,
            BoneRenderRole.Deform =>
                BoneKind.Deform,
            BoneRenderRole.Helper =>
                BoneKind.Helper,
            BoneRenderRole.Camera =>
                BoneKind.Camera,
            BoneRenderRole.Prop =>
                BoneKind.Prop,
            _ => throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "The renderer bone role is unknown."),
        };

    private GizmoRenderData[] BuildBoneEditGizmos(
        SkeletonRenderData? skeleton)
    {
        int? selectedIndex = SelectedBone?.Index;
        if (skeleton is null
            || selectedIndex is null
            || selectedIndex < 0
            || selectedIndex >= skeleton.Bones.Count)
        {
            return [];
        }

        Matrix4x4 selectedWorld =
            skeleton.Bones[selectedIndex.Value].WorldTransform
            * skeleton.RootTransform;
        RenderGizmoSpace space =
            BoneEditor.EffectiveGizmoSpace;
        if (!IsFinite(selectedWorld) ||
            !TryGetTranslationGizmoAxes(
                selectedWorld,
                space,
                out Vector3 xAxis,
                out Vector3 yAxis,
                out Vector3 zAxis))
        {
            return [];
        }

        Vector3 origin = new(
            selectedWorld.M41,
            selectedWorld.M42,
            selectedWorld.M43);
        if (!IsFinite(origin))
        {
            return [];
        }

        const float axisLength = 0.18f;
        var xColor = new Vector4(
            0.95f,
            0.20f,
            0.18f,
            1.0f);
        var yColor = new Vector4(
            0.24f,
            0.90f,
            0.34f,
            1.0f);
        var zColor = new Vector4(
            0.24f,
            0.52f,
            1.0f,
            1.0f);
        RenderTransformGizmoMode mode =
            BoneEditor.GizmoMode;
        if (mode == RenderTransformGizmoMode.Rotate)
        {
            var arcs = new List<GizmoRenderData>(48);
            AddRotationGizmoArc(
                arcs,
                selectedIndex.Value,
                space,
                RenderTransformGizmoAxis.X,
                origin,
                xAxis,
                yAxis,
                zAxis,
                xColor);
            AddRotationGizmoArc(
                arcs,
                selectedIndex.Value,
                space,
                RenderTransformGizmoAxis.Y,
                origin,
                yAxis,
                zAxis,
                xAxis,
                yColor);
            AddRotationGizmoArc(
                arcs,
                selectedIndex.Value,
                space,
                RenderTransformGizmoAxis.Z,
                origin,
                zAxis,
                xAxis,
                yAxis,
                zColor);
            return [.. arcs];
        }

        GizmoKind kind = mode switch
        {
            RenderTransformGizmoMode.Translate =>
                GizmoKind.TranslationHandle,
            RenderTransformGizmoMode.Scale =>
                GizmoKind.ScaleHandle,
            _ => throw new InvalidOperationException(
                $"Unsupported transform gizmo mode '{mode}'."),
        };
        return
        [
            CreateAxisTransformGizmo(
                kind,
                mode,
                selectedIndex.Value,
                RenderTransformGizmoAxis.X,
                space,
                origin,
                xAxis,
                axisLength,
                xColor),
            CreateAxisTransformGizmo(
                kind,
                mode,
                selectedIndex.Value,
                RenderTransformGizmoAxis.Y,
                space,
                origin,
                yAxis,
                axisLength,
                yColor),
            CreateAxisTransformGizmo(
                kind,
                mode,
                selectedIndex.Value,
                RenderTransformGizmoAxis.Z,
                space,
                origin,
                zAxis,
                axisLength,
                zColor),
        ];
    }

    private static GizmoRenderData CreateAxisTransformGizmo(
        GizmoKind kind,
        RenderTransformGizmoMode mode,
        int boneIndex,
        RenderTransformGizmoAxis axis,
        RenderGizmoSpace space,
        Vector3 origin,
        Vector3 direction,
        float length,
        Vector4 color) =>
        new(
            kind,
            origin,
            origin + (direction * length),
            color,
            1.5f,
            TranslationBinding:
                mode == RenderTransformGizmoMode.Translate
                    ? new TranslationGizmoBinding(
                        boneIndex,
                        axis switch
                        {
                            RenderTransformGizmoAxis.X =>
                                TranslationGizmoAxis.X,
                            RenderTransformGizmoAxis.Y =>
                                TranslationGizmoAxis.Y,
                            RenderTransformGizmoAxis.Z =>
                                TranslationGizmoAxis.Z,
                            _ => throw new InvalidOperationException(
                                $"Unsupported transform axis '{axis}'."),
                        },
                        space)
                    : null,
            TransformBinding: new RenderTransformGizmoBinding(
                boneIndex,
                mode,
                axis,
                space),
            InteractionAxisWorld: direction);

    private static void AddRotationGizmoArc(
        List<GizmoRenderData> destination,
        int boneIndex,
        RenderGizmoSpace space,
        RenderTransformGizmoAxis axis,
        Vector3 origin,
        Vector3 axisDirection,
        Vector3 firstPlaneAxis,
        Vector3 secondPlaneAxis,
        Vector4 color)
    {
        const int segmentCount = 16;
        const float radius = 0.16f;
        var binding = new RenderTransformGizmoBinding(
            boneIndex,
            RenderTransformGizmoMode.Rotate,
            axis,
            space);
        for (int segment = 0; segment < segmentCount; segment++)
        {
            float firstAngle =
                MathF.Tau * segment / segmentCount;
            float secondAngle =
                MathF.Tau * (segment + 1) / segmentCount;
            Vector3 start = origin + radius *
                ((firstPlaneAxis * MathF.Cos(firstAngle)) +
                 (secondPlaneAxis * MathF.Sin(firstAngle)));
            Vector3 end = origin + radius *
                ((firstPlaneAxis * MathF.Cos(secondAngle)) +
                 (secondPlaneAxis * MathF.Sin(secondAngle)));
            destination.Add(
                new GizmoRenderData(
                    GizmoKind.RotationHandle,
                    start,
                    end,
                    color,
                    1.5f,
                    TransformBinding: binding,
                    InteractionAxisWorld: axisDirection));
        }
    }

    private static bool TryGetTranslationGizmoAxes(
        Matrix4x4 selectedWorld,
        RenderGizmoSpace space,
        out Vector3 xAxis,
        out Vector3 yAxis,
        out Vector3 zAxis)
    {
        xAxis = Vector3.UnitX;
        yAxis = Vector3.UnitY;
        zAxis = Vector3.UnitZ;
        if (space == RenderGizmoSpace.Global)
        {
            return true;
        }

        if (space != RenderGizmoSpace.Local ||
            !Matrix4x4.Decompose(
                selectedWorld,
                out _,
                out System.Numerics.Quaternion rotation,
                out _) ||
            !float.IsFinite(rotation.X) ||
            !float.IsFinite(rotation.Y) ||
            !float.IsFinite(rotation.Z) ||
            !float.IsFinite(rotation.W) ||
            rotation.LengthSquared() < 1.0e-8f)
        {
            return false;
        }

        rotation = System.Numerics.Quaternion.Normalize(rotation);
        Matrix4x4 orientation =
            Matrix4x4.CreateFromQuaternion(rotation);
        xAxis = Vector3.TransformNormal(
            Vector3.UnitX,
            orientation);
        yAxis = Vector3.TransformNormal(
            Vector3.UnitY,
            orientation);
        zAxis = Vector3.TransformNormal(
            Vector3.UnitZ,
            orientation);
        return TryNormalizeAxis(ref xAxis) &&
               TryNormalizeAxis(ref yAxis) &&
               TryNormalizeAxis(ref zAxis);
    }

    private static bool TryNormalizeAxis(ref Vector3 axis)
    {
        if (!IsFinite(axis) ||
            axis.LengthSquared() < 1.0e-8f)
        {
            return false;
        }

        axis = Vector3.Normalize(axis);
        return IsFinite(axis);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) &&
        float.IsFinite(value.M12) &&
        float.IsFinite(value.M13) &&
        float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) &&
        float.IsFinite(value.M22) &&
        float.IsFinite(value.M23) &&
        float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) &&
        float.IsFinite(value.M32) &&
        float.IsFinite(value.M33) &&
        float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) &&
        float.IsFinite(value.M42) &&
        float.IsFinite(value.M43) &&
        float.IsFinite(value.M44);

    private void AddDiagnostic(
        string severity,
        string area,
        string message,
        string? detail)
    {
        Diagnostics.Insert(
            0,
            new DiagnosticEntryViewModel(
                DateTimeOffset.Now,
                severity,
                area,
                message,
                detail));
        while (Diagnostics.Count > 500)
        {
            Diagnostics.RemoveAt(Diagnostics.Count - 1);
        }
    }

    private static string Humanize(string text)
    {
        return string.Concat(
            text.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $" {character}"
                    : character.ToString()));
    }
}

public sealed class ViewportPaneViewModel : ObservableObject
{
    private string _title;
    private string _fidelityLabel;

    public ViewportPaneViewModel(
        string title,
        string fidelityLabel,
        ViewportSceneSource sceneSource)
    {
        _title = title ?? throw new ArgumentNullException(nameof(title));
        _fidelityLabel = fidelityLabel ??
            throw new ArgumentNullException(nameof(fidelityLabel));
        SceneSource = sceneSource ??
            throw new ArgumentNullException(nameof(sceneSource));
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string FidelityLabel
    {
        get => _fidelityLabel;
        private set => SetProperty(ref _fidelityLabel, value);
    }

    public ViewportSceneSource SceneSource { get; }

    internal void SetPresentation(
        string title,
        string fidelityLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(fidelityLabel);
        Title = title;
        FidelityLabel = fidelityLabel;
    }
}

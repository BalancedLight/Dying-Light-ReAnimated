using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Fed;
using ReAnimated.Codecs.Models;
using ReAnimated.Codecs.ProjectArtifacts;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
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

public enum DeveloperToolsExportMode
{
    AnimationsOnly,
    CharactersOnly,
    Anm2Only,
    Full,
}

public sealed partial class MainWindowViewModel :
    ObservableObject,
    IWorkspaceSnapshotProvider,
    IAsyncDisposable
{
    private enum StandaloneExportMode
    {
        Anm2Files,
        AnimationRpack,
        CharacterFiles,
    }

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

        public AnimationSourceRoles? DeclaredRoles { get; init; }

        public ProjectMorphSourceValueUnit? FacialSourceValueUnit
        { get; init; }
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

    /// <summary>
    /// A fingerprint-validated project model selected explicitly as the
    /// skeleton used to interpret ANM2 descriptors. Retail and project-owned
    /// custom models deliberately share this contract so selecting a source
    /// never implies that retail bytes are available.
    /// </summary>
    private sealed record DecodedProjectModelSession(
        RigDefinition Rig,
        ProjectAssetReference ProjectAsset,
        MeshRenderData[] PreviewMeshes,
        SkeletonRenderData Skeleton,
        Dl1MeshPreviewPayload? RetailPayload = null,
        RetailAssetRecord? RetailAsset = null,
        bool UsesSourcePreviewFallback = false,
        ImmutableArray<string> PreviewDiagnostics = default,
        CustomModelPreviewSession? CustomPreviewSession = null);

    private sealed record PreparedRetailTarget(
        Dl1MeshPreviewPayload Payload,
        RetailAssetRecord RetailAsset,
        ProjectAssetReference ProjectAsset);

    private sealed record PreparedCustomTarget(
        RigDefinition Rig,
        MeshRenderData[] Meshes,
        SkeletonRenderData Skeleton,
        ProjectAssetReference ProjectAsset,
        CustomModelPreviewSession PreviewSession,
        CustomModelPreviewMode RequestedPreviewMode =
            CustomModelPreviewMode.Dl1Output,
        CustomModelPreviewMode EffectivePreviewMode =
            CustomModelPreviewMode.Dl1Output,
        ImmutableArray<string> PreviewDiagnostics = default)
    {
        public bool UsesSourceFallback =>
            RequestedPreviewMode != EffectivePreviewMode;
    }

    private sealed record PreparedExternalFbxTarget(
        ProjectModelEntry Model,
        ProjectAssetReference Asset,
        RigDefinition Rig,
        PreparedRetailTarget? Retail,
        PreparedCustomTarget? Custom);

    internal sealed record ExternalFbxVariantTarget(
        ProjectAssetReference Asset,
        RigDefinition Rig);

    internal sealed record ExternalFbxTargetVariantBatch(
        RigDefinition SourceRig,
        ImmutableArray<ProjectAnimation> Animations,
        ImmutableArray<RetargetMap?> Mappings);

    private sealed record PreparedCustomModelSource(
        FbxModelAuthoringImportResult Imported,
        CustomModelAnimationClip Selection,
        AnimationClip Clip,
        CustomModelPreviewPayload Preview,
        string PackagePath);

    private sealed record LoadedProjectCustomModel(
        FbxModelAuthoringImportResult Imported,
        CustomModelPackage Package);

    private sealed record CompiledCheckedCustomModel(
        byte[] Rpack,
        Dl1OfficialModelCompilerEvidence Evidence);

    private sealed record PreparedCheckedExportModel(
        ProjectModelEntry Model,
        ProjectAssetReference Asset,
        ImmutableArray<Dl1PortableAnimationResource> Animations,
        LoadedProjectCustomModel? CustomModel,
        byte[]? CompiledCustomModelRpack,
        Dl1OfficialModelCompilerEvidence? OfficialCompilerEvidence,
        ImmutableArray<Dl1PortableMorphResource> CompiledMorphResources,
        string AnimationLibraryName);

    internal sealed record ProjectAnimationPackTarget(
        ProjectModelEntry Model,
        bool IsCustomModel,
        ImmutableArray<Dl1PortableAnimationResource> Animations);

    internal sealed record BuiltProjectAnimationPack(
        byte[] Rpack,
        ImmutableDictionary<string, byte[]> Animations,
        ImmutableDictionary<string, Rp6lAnimationScript> Scripts,
        ImmutableDictionary<string, string> LooseScripts);

    private sealed record StandaloneExportArtifact(
        string RelativePath,
        byte[] Payload);

    private sealed record PreparedAnimationTransition(
        long Generation,
        ProjectAnimation Animation,
        ImportedAnimationSession Source,
        MeshRenderData[] SourceMeshes,
        DecodedProjectModelSession? SourceModel,
        PreparedRetailTarget? Target,
        PreparedCustomTarget? CustomTarget,
        ImportedMimicSession? Mimic,
        AnimationClip SynchronizedClip,
        RetargetMap? Mapping,
        TargetBindingStatus BindingStatus,
        DirectRigBinding? DirectBinding = null);

    private sealed record AnimationRuntimeSnapshot(
        DlraProject Project,
        DlraProject? SavedProject,
        Guid? ActiveAnimationId,
        ImportedAnimationSession? SourceAnimation,
        ImportedMimicSession? MimicAnimation,
        ImportedFacialFbxSession? FacialFbxAnimation,
        AnimationClip? SynchronizedAnimation,
        RigDefinition? TargetRig,
        RetargetMap? ActiveRetargetMap,
        DirectRigBinding? ActiveDirectRigBinding,
        ProjectAssetReference? TargetProjectAsset,
        bool CustomTargetUsesSourcePreviewFallback,
        string? CustomTargetPreviewDiagnostic,
        CustomModelPreviewSession? CustomTargetPreviewSession,
        DecodedProjectModelSession? SourceModelContext,
        MeshRenderData[] SourceMeshes,
        MeshRenderData[] TargetMeshes,
        TargetBindingStatus TargetBindingStatus,
        int Frame,
        bool IsPlaying,
        bool IsPlaybackEnabled,
        EditorWorkspaceMode Workspace,
        string LegacyWorkspace,
        ViewportOrbitCameraPair OrbitCameras,
        PreviewFramePair? LastPreviewFramePair,
        DlraProject[] UndoProjects,
        DlraProject[] RedoProjects);

    private sealed record RootMotionTrailCacheKey(
        Guid? ActiveAnimationId,
        ImportedAnimationSession Source,
        RigDefinition Target,
        RetargetMap? Mapping,
        DirectRigBinding? DirectBinding,
        ProjectAnimation Animation,
        AnimationClip EvaluationClip,
        int SampleCount);

    private sealed record RootMotionTrailBuildSnapshot(
        RootMotionTrailCacheKey CacheKey,
        ImportedAnimationSession Source,
        RigDefinition Target,
        RetargetMap? Mapping,
        DirectRigBinding? DirectBinding,
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
    private static readonly JsonSerializerOptions IndentedJsonOptions =
        new()
        {
            WriteIndented = true,
        };
    private readonly JsonWorkspaceStateStore _recoveryStore;
    private readonly PendingProjectAssetStore _pendingProjectAssetStore;
    private readonly Dictionary<Guid, PendingProjectAssetReceipt>
        _pendingProjectAssets = [];
    private readonly SemaphoreSlim _modelsIntegrationGate = new(1, 1);
    private readonly IProjectFileDialogService _fileDialogs;
    private readonly Dl1AssetWorkspace _assetWorkspace;
    private readonly IRetailMeshDecodeService _retailMeshDecodeService;
    private readonly IDl1InstalledBuildFingerprintService
        _installedBuildFingerprintService;
    private readonly IFacialFbxProjectReviewImporter
        _facialFbxProjectReviewImporter;
    private readonly AssistedReviewSettingsStore?
        _assistedReviewSettingsStore;
    private readonly StructuredFileLogger? _structuredLogger;
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
    private ProjectModelItemViewModel? _selectedProjectModel;
    private AssetItemViewModel? _pendingExplorerAnimationSourceChoice;
    private string? _pendingLocalAnm2ImportPath;
    private AssetItemViewModel? _pendingExplorerAnimationTimingChoice;
    private Dl1RetailAnimationTiming? _selectedExplorerAnimationTiming;
    private JobViewModel? _assetDecodeJob;
    private CancellationTokenSource? _automaticAssetPreviewSource;
    private Task? _automaticAssetPreviewTask;
    private JobViewModel? _assetProfileScanJob;
    private JobViewModel? _ikBakeJob;
    private Task? _assetCatalogLoadTask;
    private bool _isViewportsLinked = true;
    private bool _isSourceViewportVisible;
    private bool _isTargetSwitching;
    private bool _isDiagnosticsDrawerOpen;
    private int _selectedDiagnosticsTabIndex;
    private int _selectedExplorerTabIndex;
    private int _selectedInspectorTabIndex;
    private bool _showMeshes = true;
    private bool _showSkeletonOverlay = true;
    private bool _showDeformBones = true;
    private bool _showHelpers = true;
    private bool _showCameraHelpers = true;
    private bool _showPropHelpers = true;
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
    private string? _lastDeveloperToolsBatchReceiptPath;
    private string? _lastProjectArtifactReceiptRelativePath;
    private string? _lastProjectArtifactProjectRoot;
    private string _activeWorkspaceMode = "Models";
    private EditorWorkspaceMode _activeWorkspace =
        EditorWorkspaceMode.Models;
    private bool _isCustomModelAuthoringSurfaceVisible;
    private bool _isAssistedReviewEnabled;
    private PreviewLayoutMode _previewLayout =
        PreviewLayoutMode.IsolatedBrowse;
    private string _selectedPreviewMode = Dl1ProfilePreviewModeLabel;
    private string? _projectPath;
    private Guid? _activeAnimationId;
    private DlraProject _project = DlraProject.Create("Untitled");
    private DlraProject? _savedProject;
    private long _savedModelsRevision;
    private long _integratedModelsRevision;
    private CancellationTokenSource? _modelsIntegrationSource;
    private ImportedAnimationSession? _sourceAnimation;
    private ImportedMimicSession? _mimicAnimation;
    private ImportedFacialFbxSession? _facialFbxAnimation;
    private AnimationClip? _synchronizedAnimation;
    private RigDefinition? _targetRig;
    private RetargetMap? _activeRetargetMap;
    private DirectRigBinding? _activeDirectRigBinding;
    private string _mappingReviewStatus =
        "Load a source animation and fingerprinted target to review mapping.";
    private string? _lastPreviewDiagnostic;
    private ProjectAssetReference? _targetProjectAsset;
    private bool _customTargetUsesSourcePreviewFallback;
    private string? _customTargetPreviewDiagnostic;
    private CustomModelPreviewSession? _customTargetPreviewSession;
    private DecodedProjectModelSession? _sourceModelContext;
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
    private bool _batchReviewingMapping;
    private bool _synchronizingPreviewMode;
    private bool _synchronizingFppProjectionCapture;
    private bool _synchronizingMovieReferenceCameraCapture;
    private bool _synchronizingPreviewConfiguration;
    private string? _facialFbxUnmappedFingerprint;
    private ImmutableArray<string> _facialFbxUnmappedChannels = [];
    private Dl1RootMotionMode _selectedRootMotionMode =
        Dl1RootMotionMode.Recorded;
    private string? _selectedRootBoneName;
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
    private long _animationTransitionGeneration;
    private long _animationSourcePreviewGeneration;
    private PreviewFramePair? _lastPreviewFramePair;
    private string? _animationOperationFailureMessage;
    private RenderFppProjectionState? _suspendedTargetProjection;
    private RenderFrameSnapshot? _lastFppExternalOrbitFrame;
    private RenderFrameSnapshot? _isolatedBrowsePreviewFrame;
    private DecodedRetailModelSession? _isolatedBrowsePreviewModel;
    private string? _isolatedBrowsePreviewTitle;
    private string? _isolatedBrowsePreviewFidelity;
    private ViewportOrbitCameraPair? _authoringOrbitCameras;
    private ViewportOrbitCameraPair? _browseOrbitCameras;
    private string? _lastComparisonFramingKey;
    private DeveloperToolsExportMode _developerToolsExportMode =
        DeveloperToolsExportMode.AnimationsOnly;

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
        StructuredFileLogger? structuredLogger)
        : this(
            recoveryStore,
            new WindowsProjectFileDialogService(),
            CreateDefaultAssetWorkspace(),
            new Dl1InstalledBuildFingerprintService(),
            structuredLogger: structuredLogger)
    {
    }

    public MainWindowViewModel(
        JsonWorkspaceStateStore recoveryStore,
        StructuredFileLogger? structuredLogger,
        AssistedReviewSettingsStore assistedReviewSettingsStore)
        : this(
            recoveryStore,
            new WindowsProjectFileDialogService(),
            CreateDefaultAssetWorkspace(),
            new Dl1InstalledBuildFingerprintService(),
            structuredLogger: structuredLogger,
            assistedReviewSettingsStore: assistedReviewSettingsStore)
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
        IRetailMeshDecodeService? retailMeshDecodeService = null,
        StructuredFileLogger? structuredLogger = null,
        AssistedReviewSettingsStore?
            assistedReviewSettingsStore = null)
    {
        _recoveryStore = recoveryStore
            ?? throw new ArgumentNullException(nameof(recoveryStore));
        _pendingProjectAssetStore = new PendingProjectAssetStore(
            _recoveryStore.FilePath);
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
        _structuredLogger = structuredLogger;
        _assistedReviewSettingsStore =
            assistedReviewSettingsStore;
        _isAssistedReviewEnabled =
            assistedReviewSettingsStore?.LoadEnabled() ?? false;
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

        Models = new ModelsWorkspaceViewModel(
            _fileDialogs,
            value => StatusText = value,
            OpenCustomModelAnimationInAnimateAsync,
            ResolveRetailData0PakPath,
            synchronizeProject: SynchronizeModelsWorkspaceProjectAsync,
            returnToProjectModels: OpenRetailModelBrowser);
        Models.PersistenceStateChanged += OnModelsPersistenceStateChanged;
        _savedModelsRevision = Models.PersistenceRevision;
        _integratedModelsRevision = Models.PersistenceRevision;
        if (Directory.Exists(Models.DeveloperToolsProjectRoot))
        {
            _lastDeveloperToolsBatchReceiptPath =
                Dl1DeveloperToolsProjectDeployer
                    .LoadLatestActiveBatchReceipt(
                        Models.DeveloperToolsProjectRoot)
                    ?.ReceiptPath;
        }

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
        UseSelectedProjectModelAsSourceCommand = new AsyncRelayCommand(
            UseSelectedProjectModelAsSourceAsync,
            CanUseSelectedProjectModelAsSource);
        UseSelectedAssetAsTargetCommand = new AsyncRelayCommand(
            UseSelectedAssetAsTargetAsync,
            CanUseSelectedMeshAssetAsTarget,
            AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PlaySelectedExplorerAnimationCommand = new AsyncRelayCommand(
            PlaySelectedExplorerAnimationAsync,
            CanPlaySelectedExplorerAnimation);
        AddSelectedModelToProjectCommand = new AsyncRelayCommand(
            AddSelectedModelToProjectAsync,
            CanUseSelectedMeshAsset);
        SelectWorkspaceCommand = new RelayCommand<string>(
            SelectWorkspace);
        OpenCustomModelAuthoringCommand = new RelayCommand(
            OpenCustomModelAuthoring);
        ImportCustomModelCommand = new AsyncRelayCommand(
            ImportCustomModelAsync,
            () => !IsBusy);
        OpenCustomModelPackageCommand = new AsyncRelayCommand(
            OpenCustomModelPackageAsync,
            () => !IsBusy);
        EditSelectedProjectModelCommand = new AsyncRelayCommand(
            EditSelectedProjectModelAsync,
            CanEditSelectedProjectModel);
        OpenRetailModelBrowserCommand = new RelayCommand(
            OpenRetailModelBrowser);
        ToggleDiagnosticsDrawerCommand = new RelayCommand(
            () => IsDiagnosticsDrawerOpen =
                !IsDiagnosticsDrawerOpen);
        ShowAnimationOperationDiagnosticsCommand = new RelayCommand(() =>
        {
            SelectedDiagnosticsTabIndex = 1;
            IsDiagnosticsDrawerOpen = true;
        });
        ShowFidelityDetailsCommand = new RelayCommand(() =>
        {
            SelectedDiagnosticsTabIndex = 2;
            IsDiagnosticsDrawerOpen = true;
        });
        OpenBoneEditorCommand = new RelayCommand(OpenBoneEditor);
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
        OpenSelectedAnimationCommand = new AsyncRelayCommand(
            OpenSelectedAnimationAsync,
            () => !IsBusy && SelectedAnimationLibraryItem is not null);
        AddAnimationTargetsCommand = new AsyncRelayCommand(
            AddAnimationTargetsAsync,
            CanAddAnimationTargets);
        AssignAnimationLibraryCommand = new RelayCommand(
            AssignSelectedAnimationLibrary,
            CanAssignSelectedAnimationLibrary);
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
        ApplyExportSelectionCommand = new RelayCommand(
            ApplyExportSelection,
            () => !IsBusy && !_project.AnimationVariants.IsEmpty);
        ExportCheckedPortableCommand = new AsyncRelayCommand(
            ExportCheckedPortableAsync,
            CanRunCheckedExportWorkflow);
        ExportAnm2FilesCommand = new AsyncRelayCommand(
            ExportSelectedAnm2FilesAsync,
            CanRunCheckedExportWorkflow);
        ExportAnimationRPackCommand = new AsyncRelayCommand(
            ExportSelectedAnimationRPackAsync,
            CanRunCheckedExportWorkflow);
        ExportCharacterFilesCommand = new AsyncRelayCommand(
            ExportSelectedCharacterFilesAsync,
            CanRunCheckedExportWorkflow);
        SelectDeveloperToolsExportModeCommand =
            new RelayCommand<string>(SelectDeveloperToolsExportMode);
        DeployCurrentSelectionCommand = new AsyncRelayCommand(
            DeployCurrentSelectionAsync,
            CanRunCheckedExportWorkflow);
        DeployCheckedToDeveloperToolsCommand = new AsyncRelayCommand(
            DeployCheckedToDeveloperToolsAsync,
            CanRunCheckedExportWorkflow);
        RollBackDeveloperToolsBatchCommand = new AsyncRelayCommand(
            RollBackDeveloperToolsBatchAsync,
            () => !IsBusy &&
                  (File.Exists(_lastDeveloperToolsBatchReceiptPath) ||
                   !string.IsNullOrWhiteSpace(
                       _lastProjectArtifactReceiptRelativePath) &&
                   Directory.Exists(_lastProjectArtifactProjectRoot)));
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
        RestoreRecoveryCommand = new AsyncRelayCommand(
            RestoreRecoveryAsync,
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
        AcceptMappingProposalCommand = new RelayCommand(
            AcceptMappingProposalAndPlay,
            CanReviewMapping);
        ApplyAssistedReviewCommand = new RelayCommand(
            ApplyAssistedReview,
            CanApplyAssistedReview);
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

        // Make holder, camera, and prop helper roles visible on the first
        // decoded model. ViewportSceneSource otherwise inherits the renderer
        // record defaults, which intentionally hide ordinary helpers/props.
        ApplySkeletonVisibility();
        BuildFidelityBadges();
        TryLoadRecoveryMetadata();
        AddDiagnostic(
            "Info",
            "Renderer",
            "D3D11 editor preview pipeline is available",
            "Static and skinned meshes, evidence-backed retail PC HALF4 position deltas, bounded ABDM/type-8480 BC1/BC2/BC3 base-color previews, skeletons, bone selection, and gizmos render now. Broader shader/material interpretation, non-PC morph layouts, normal-delta shading, and game-validated facial captures remain explicit fidelity gaps.");
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

    public ObservableCollection<ProjectModelItemViewModel>
        ProjectModelLibrary
    { get; } = [];

    public ObservableCollection<ExportModelSelectionViewModel>
        ExportModelSelections
    { get; } = [];

    /// <summary>
    /// Flat animation-centric export rows used by the Files and Developer
    /// Tools workspaces.  ExportModelSelections remains as a compatibility
    /// grouping for the transactional exporters while the UI no longer has
    /// to render nested model expanders.
    /// </summary>
    public ObservableCollection<ExportVariantSelectionViewModel>
        ExportVariants
    { get; } = [];

    public ObservableCollection<ExportReadinessItemViewModel>
        ExportReadiness
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
                ScheduleSelectedAnimationSourcePreview();
            }
        }
    }

    public ProjectModelItemViewModel? SelectedProjectModel
    {
        get => _selectedProjectModel;
        set
        {
            Guid? previousId = _selectedProjectModel?.ModelId;
            if (!SetProperty(ref _selectedProjectModel, value))
            {
                return;
            }

            UseSelectedProjectModelAsSourceCommand.NotifyCanExecuteChanged();
            EditSelectedProjectModelCommand.NotifyCanExecuteChanged();
            if (value is null || previousId == value.ModelId)
            {
                return;
            }

            if (_project.Workflow.SelectedModelId != value.ModelId)
            {
                _project = _project with
                {
                    Workflow = _project.Workflow with
                    {
                        SelectedModelId = value.ModelId,
                    },
                };
                UpdateDirtyState();
                OnPropertyChanged(nameof(CurrentProject));
            }

            ScheduleProjectModelPreview(value);
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
        ["Models", "Animations", "Playback", "Retarget/Edit", "Export"];

    public IReadOnlyList<string> PreviewModes { get; } =
        [RawPreviewModeLabel, Dl1ProfilePreviewModeLabel];

    public IReadOnlyList<Dl1RootMotionMode> RootMotionModes { get; } =
        Enum.GetValues<Dl1RootMotionMode>();

    public ObservableCollection<string> RootBoneCandidates { get; } = [];

    public ViewportPaneViewModel SourceViewport { get; }

    public ViewportPaneViewModel TargetViewport { get; }

    public ModelsWorkspaceViewModel Models { get; }

    public RelayCommand NewWorkspaceCommand { get; }

    public AsyncRelayCommand OpenWorkspaceCommand { get; }

    public AsyncRelayCommand SaveWorkspaceCommand { get; }

    public AsyncRelayCommand ImportAnimationCommand { get; }

    public AsyncRelayCommand PreviewSelectedAssetCommand { get; }

    public AsyncRelayCommand UseSelectedAssetAsSourceCommand { get; }

    public AsyncRelayCommand UseSelectedProjectModelAsSourceCommand { get; }

    public AsyncRelayCommand UseSelectedAssetAsTargetCommand { get; }

    public AsyncRelayCommand PlaySelectedExplorerAnimationCommand { get; }

    public AsyncRelayCommand AddSelectedModelToProjectCommand { get; }

    public RelayCommand<string> SelectWorkspaceCommand { get; }

    public RelayCommand OpenCustomModelAuthoringCommand { get; }

    public AsyncRelayCommand ImportCustomModelCommand { get; }

    public AsyncRelayCommand OpenCustomModelPackageCommand { get; }

    public AsyncRelayCommand EditSelectedProjectModelCommand { get; }

    public RelayCommand OpenRetailModelBrowserCommand { get; }

    public RelayCommand ToggleDiagnosticsDrawerCommand { get; }

    public RelayCommand ShowAnimationOperationDiagnosticsCommand { get; }

    public bool HasAnimationOperationFailure =>
        !string.IsNullOrWhiteSpace(_animationOperationFailureMessage);

    public string AnimationOperationFailureMessage =>
        _animationOperationFailureMessage ?? string.Empty;

    public RelayCommand ShowFidelityDetailsCommand { get; }

    public RelayCommand OpenBoneEditorCommand { get; }

    public RelayCommand CancelExplorerSourceModelPickerCommand { get; }

    public bool IsExplorerSourceModelPickerActive =>
        _pendingExplorerAnimationSourceChoice is not null ||
        _pendingLocalAnm2ImportPath is not null;

    public string ExplorerSourceModelPickerPrompt =>
        _pendingExplorerAnimationSourceChoice is { } animation
            ? $"Choose the exact fingerprinted source model for '{animation.Name}'. Use a base-game mesh or a rigged project model; the clip is bound to that immutable model before playback."
            : _pendingLocalAnm2ImportPath is { } localPath
                ? $"Choose the exact fingerprinted source model for '{Path.GetFileName(localPath)}'. Use a base-game mesh or rigged project model; descriptor coverage is shown before anything changes."
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

    public AsyncRelayCommand OpenSelectedAnimationCommand { get; }

    public AsyncRelayCommand AddAnimationTargetsCommand { get; }

    public RelayCommand AssignAnimationLibraryCommand { get; }

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

    public RelayCommand ApplyExportSelectionCommand { get; }

    public AsyncRelayCommand ExportCheckedPortableCommand { get; }

    public AsyncRelayCommand ExportAnm2FilesCommand { get; }

    public AsyncRelayCommand ExportAnimationRPackCommand { get; }

    public AsyncRelayCommand ExportCharacterFilesCommand { get; }

    public AsyncRelayCommand ExportFullProjectCommand =>
        ExportCheckedPortableCommand;

    public RelayCommand<string> SelectDeveloperToolsExportModeCommand
    { get; }

    public AsyncRelayCommand DeployCurrentSelectionCommand { get; }

    public bool IsDeveloperToolsAnimationsOnlySelected =>
        _developerToolsExportMode ==
        DeveloperToolsExportMode.AnimationsOnly;

    public bool IsDeveloperToolsCharactersOnlySelected =>
        _developerToolsExportMode ==
        DeveloperToolsExportMode.CharactersOnly;

    public bool IsDeveloperToolsAnm2OnlySelected =>
        _developerToolsExportMode == DeveloperToolsExportMode.Anm2Only;

    public bool IsDeveloperToolsFullProjectSelected =>
        _developerToolsExportMode == DeveloperToolsExportMode.Full;

    public bool IsCharacterCompilerRequired =>
        _developerToolsExportMode is
            DeveloperToolsExportMode.CharactersOnly or
            DeveloperToolsExportMode.Full;

    public AsyncRelayCommand DeployCheckedToDeveloperToolsCommand { get; }

    public AsyncRelayCommand RollBackDeveloperToolsBatchCommand { get; }

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

    public AsyncRelayCommand RestoreRecoveryCommand { get; }

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

    public RelayCommand AcceptMappingProposalCommand { get; }

    public RelayCommand ApplyAssistedReviewCommand { get; }

    public RelayCommand AddHelperOverrideCommand { get; }

    public RelayCommand RemoveHelperOverrideCommand { get; }

    public bool IsAssistedReviewEnabled
    {
        get => _isAssistedReviewEnabled;
        set
        {
            if (!SetProperty(
                    ref _isAssistedReviewEnabled,
                    value))
            {
                return;
            }

            try
            {
                _assistedReviewSettingsStore?.SaveEnabled(value);
                StatusText = value
                    ? "Assisted review enabled; use Apply assisted review to change eligible rows"
                    : "Assisted review disabled; existing project review decisions were not changed";
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException)
            {
                AddDiagnostic(
                    "Warning",
                    "Settings",
                    "The assisted-review preference could not be saved",
                    exception.Message);
            }

            ApplyAssistedReviewCommand.NotifyCanExecuteChanged();
        }
    }

    public static string AssistedReviewPolicySummary =>
        $"{RetargetSuggestionScorer.PolicyVersion}: eligible deterministic rows at {RetargetSuggestionScorer.AssistedReviewThreshold:P0}; structural, fan-out, helper fallback, bind fallback, ambiguous, and manual rows remain review-required.";

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

    public string? SelectedRootBoneName
    {
        get => _selectedRootBoneName;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();
            if (SetProperty(ref _selectedRootBoneName, normalized) &&
                !_synchronizingProjectBindings)
            {
                UpdateRootBoneName(normalized);
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

            CommitProject(WithUpdatedActiveAnimation(
                _project,
                animation with
                {
                    PreviewMotionAccumulationEnabled = value,
                },
                animationIndex));
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
                AddSelectedModelToProjectCommand.NotifyCanExecuteChanged();
                UseSelectedAssetAsSourceCommand.NotifyCanExecuteChanged();
                UseSelectedProjectModelAsSourceCommand
                    .NotifyCanExecuteChanged();
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
                ApplyExportSelectionCommand.NotifyCanExecuteChanged();
                ExportCheckedPortableCommand.NotifyCanExecuteChanged();
                ExportAnm2FilesCommand.NotifyCanExecuteChanged();
                ExportAnimationRPackCommand.NotifyCanExecuteChanged();
                ExportCharacterFilesCommand.NotifyCanExecuteChanged();
                DeployCurrentSelectionCommand.NotifyCanExecuteChanged();
                DeployCheckedToDeveloperToolsCommand.NotifyCanExecuteChanged();
                RollBackDeveloperToolsBatchCommand.NotifyCanExecuteChanged();
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
                ExportSelectedBrowserMeshToFbxCommand
                    .NotifyCanExecuteChanged();
                PlaySelectedExplorerAnimationCommand
                    .NotifyCanExecuteChanged();
                ConfirmExplorerAnimationTimingCommand
                    .NotifyCanExecuteChanged();
                CancelExplorerAnimationTimingCommand
                    .NotifyCanExecuteChanged();
                AssignAnimationLibraryCommand.NotifyCanExecuteChanged();
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

    public bool IsModelsWorkspace =>
        ActiveWorkspace is
            EditorWorkspaceMode.Models or
            EditorWorkspaceMode.Browse;

    public bool IsCustomModelAuthoringSurfaceVisible =>
        IsModelsWorkspace && _isCustomModelAuthoringSurfaceVisible;

    public bool IsRetailModelBrowserSurfaceVisible =>
        IsModelsWorkspace && !IsCustomModelAuthoringSurfaceVisible;

    public bool IsAnimationWorkspaceSurfaceVisible =>
        !IsCustomModelAuthoringSurfaceVisible;

    public bool IsAnimationsWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Animations;

    public bool IsAnimateWorkspace =>
        IsPlaybackWorkspace;

    public bool IsPlaybackWorkspace =>
        ActiveWorkspace is
            EditorWorkspaceMode.Animate or
            EditorWorkspaceMode.Fpp;

    public bool IsRetargetWorkspace =>
        ActiveWorkspace is
            EditorWorkspaceMode.RetargetEdit or
            EditorWorkspaceMode.Face;

    public bool IsExportWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Export;

    public bool IsFaceWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Face;

    public bool IsFppWorkspace =>
        ActiveWorkspace == EditorWorkspaceMode.Fpp;

    public bool IsAnimationAuthoringWorkspace =>
        ActiveWorkspace is
            EditorWorkspaceMode.Animate or
            EditorWorkspaceMode.RetargetEdit or
            EditorWorkspaceMode.Face or
            EditorWorkspaceMode.Fpp or
            EditorWorkspaceMode.Export;

    public bool IsFaceOrFppWorkspace =>
        IsRetargetWorkspace;

    public bool IsInspectorPanelVisible =>
        !IsDiagnosticsDrawerOpen &&
        (IsRetargetWorkspace || IsExportWorkspace);

    public bool IsFppPlaybackEnabled
    {
        get => ActiveWorkspace == EditorWorkspaceMode.Fpp;
        set
        {
            if (value == IsFppPlaybackEnabled)
            {
                return;
            }

            SetWorkspace(
                value
                    ? EditorWorkspaceMode.Fpp
                    : EditorWorkspaceMode.Playback,
                preserveLegacyCutscene: false);
            RefreshAnimationPreview();
        }
    }

    public bool IsRetargetSetupVisible =>
        IsRetargetWorkspace && GetActiveAnimation() is null;

    public string RetargetSetupSelectionLabel =>
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        } selected
            ? $"Selected model: {selected.Name}"
            : "No retail model selected";

    public string RetargetSetupInstructions =>
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Mesh,
            RetailAsset: not null,
        }
            ? "The selected model is shown in bind pose without changing the project. Use it as the source for the next retail ANM2, or load an animation first and then choose its target."
            : "Select a skinned model in Assets. Then preview it, bind it as the source for a retail ANM2, or import an animation. Retarget mapping needs both a source animation and target rig.";

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
                OnPropertyChanged(nameof(IsAnimationDiagnosticsDrawerVisible));
            }
        }
    }

    public bool IsAnimationDiagnosticsDrawerVisible =>
        IsDiagnosticsDrawerOpen && IsAnimationWorkspaceSurfaceVisible;

    public int SelectedDiagnosticsTabIndex
    {
        get => _selectedDiagnosticsTabIndex;
        set => SetProperty(
            ref _selectedDiagnosticsTabIndex,
            Math.Clamp(value, 0, 2));
    }

    public int SelectedExplorerTabIndex
    {
        get => _selectedExplorerTabIndex;
        set => SetProperty(
            ref _selectedExplorerTabIndex,
            Math.Clamp(value, 0, 2));
    }

    public int SelectedInspectorTabIndex
    {
        get => _selectedInspectorTabIndex;
        set => SetProperty(
            ref _selectedInspectorTabIndex,
            Math.Clamp(value, 0, 4));
    }

    public TargetBindingStatus ActiveTargetBindingStatus =>
        _targetBindingStatus;

    public string TargetBindingStatusText => IsTargetSwitching
        ? "Loading target"
        : ActiveTargetBindingStatus switch
        {
            TargetBindingStatus.Direct =>
                GetActiveAnimation()?.BindingMode ==
                    ProjectAnimationBindingMode.CompatibleDirect
                    ? "Direct \u2014 compatible skeleton"
                    : "Direct \u2014 owning model",
            TargetBindingStatus.Ready => "Reviewed retarget ready",
            TargetBindingStatus.NeedsReview => "Retarget review required",
            _ => GetActiveAnimation()?.TargetAssetId is not null
                ? "Saved target unavailable"
                : "No playable target",
        };

    public string TargetPlaybackMessage => IsTargetSwitching
        ? "The last valid frame is frozen while the selected target is decoded."
        : ActiveTargetBindingStatus == TargetBindingStatus.NeedsReview
            ? "Draft preview uses the proposed map. Unmapped target nodes remain at bind pose, and export stays blocked until every required row is reviewed."
            : ActiveTargetBindingStatus == TargetBindingStatus.Invalid
                ? GetActiveAnimation()?.TargetAssetId is not null
                    ? "The project already identifies an exact target. It is being restored from the saved catalog, or its failure is recorded in Diagnostics. Do not select a replacement mesh."
                    : "Choose an explicit source and target before target playback."
                : "The target is admitted to the authoritative preview pipeline.";

    public bool IsTargetPlaybackBlocked =>
        ActiveTargetBindingStatus == TargetBindingStatus.Invalid;

    public bool IsDraftTargetPreview =>
        ActiveTargetBindingStatus == TargetBindingStatus.NeedsReview;

    public string ExportSelectionSummary
    {
        get
        {
            int models = ExportModelSelections.Count(static model =>
                model.IsSelected);
            int variants = ExportModelSelections.Sum(static model =>
                model.Variants.Count(static variant =>
                    variant.IsSelected));
            return $"{models:N0} model(s), {variants:N0} ready variant(s) selected";
        }
    }

    public string ActiveAnimationLabel =>
        GetActiveAnimation()?.Name ?? "No animation";

    public string AnimateWorkspaceHint => GetActiveAnimation() is not null
        ? "Open the active animation and its authoritative target preview."
        : "Open animation setup. An isolated retail preview remains visible until a clip is imported or played.";

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
                (source?.Kind == ProjectAssetKind.CustomModelSource
                    ? Path.GetFileNameWithoutExtension(source.RelativePath)
                    : null) ??
                (_sourceAnimation is { } animationSource
                    ? $"{animationSource.SourceKind} rig ({animationSource.Rig.BoneCount:N0} bones)"
                    : "No source model");
        }
    }

    public string ActiveTargetModelLabel =>
        _targetProjectAsset?.RetailIdentity?.ResourceName ??
        (_targetProjectAsset?.Kind == ProjectAssetKind.CustomModelSource
            ? Path.GetFileNameWithoutExtension(
                _targetProjectAsset.RelativePath)
            : null) ??
        "No target model";

    public bool IsSourceViewportVisible
    {
        get => _isSourceViewportVisible;
        private set => SetProperty(
            ref _isSourceViewportVisible,
            value);
    }

    /// <summary>
    /// Materializes both native viewport hosts for the opt-in packaged WPF
    /// startup smoke. Normal empty Browse, Animate, Face, and FPP workspaces
    /// remain genuinely single-pane until an authoring comparison exists.
    /// </summary>
    internal void ConfigureStartupSmokeDualViewport()
    {
        if (_previewLayout != PreviewLayoutMode.FppDualView)
        {
            _previewLayout = PreviewLayoutMode.FppDualView;
            OnPropertyChanged(nameof(PreviewLayout));
            OnPropertyChanged(nameof(IsLinkedCameraControlVisible));
        }

        IsSourceViewportVisible = true;
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
                ? "Models"
                : value.Trim();
            EditorWorkspaceMode workspace = normalized switch
            {
                "Browse" or "Models" or "Model Import" =>
                    EditorWorkspaceMode.Models,
                "Animations" or "Animation Browser" =>
                    EditorWorkspaceMode.Animations,
                "Animate" or "Playback" =>
                    EditorWorkspaceMode.Playback,
                "Retarget" or "Retarget/Edit" or "Bone Edit" =>
                    EditorWorkspaceMode.RetargetEdit,
                "Facial" or "Face" => EditorWorkspaceMode.Face,
                "FPP" or "Cutscene" => EditorWorkspaceMode.Fpp,
                "Export" => EditorWorkspaceMode.Export,
                _ => EditorWorkspaceMode.Models,
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
        ActiveWorkspaceMode = value ?? "Models";
    }

    private void OpenCustomModelAuthoring()
    {
        if (!IsModelsWorkspace)
        {
            SetWorkspace(
                EditorWorkspaceMode.Models,
                preserveLegacyCutscene: false);
        }

        if (SetProperty(
                ref _isCustomModelAuthoringSurfaceVisible,
                true,
                nameof(IsCustomModelAuthoringSurfaceVisible)))
        {
            OnPropertyChanged(nameof(IsRetailModelBrowserSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationWorkspaceSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationDiagnosticsDrawerVisible));
            StatusText =
                "Custom model authoring opened; animation targets remain unchanged";
        }
    }

    private async Task ImportCustomModelAsync()
    {
        OpenCustomModelAuthoring();
        await Models.ImportFbxCommand.ExecuteAsync(null);
    }

    private async Task OpenCustomModelPackageAsync()
    {
        OpenCustomModelAuthoring();
        await Models.OpenPackageCommand.ExecuteAsync(null);
    }

    private bool CanEditSelectedProjectModel()
    {
        if (IsBusy || SelectedProjectModel is not { } selected)
        {
            return false;
        }

        ProjectModelEntry? model = _project.Models.FirstOrDefault(
            candidate => candidate.Id == selected.ModelId);
        ProjectAssetReference? asset = model is null
            ? null
            : _project.Assets.FirstOrDefault(candidate =>
                candidate.Id == model.AssetId);
        return asset?.Kind == ProjectAssetKind.CustomModelSource;
    }

    private async Task EditSelectedProjectModelAsync()
    {
        if (!CanEditSelectedProjectModel() ||
            SelectedProjectModel is not { } selected)
        {
            return;
        }

        ProjectModelEntry model = _project.Models.Single(candidate =>
            candidate.Id == selected.ModelId);
        ProjectAssetReference asset = _project.Assets.Single(candidate =>
            candidate.Id == model.AssetId);
        try
        {
            string packagePath = await ResolveProjectAssetPathAsync(
                asset,
                "custom-model package",
                CancellationToken.None);
            OpenCustomModelAuthoring();
            await Models.OpenPackagePathAsync(packagePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AddDiagnostic(
                "Error",
                "Models",
                "The selected custom model could not be opened",
                exception.Message);
            StatusText = "Selected custom model could not be opened";
        }
    }

    private void OpenRetailModelBrowser()
    {
        if (SetProperty(
                ref _isCustomModelAuthoringSurfaceVisible,
                false,
                nameof(IsCustomModelAuthoringSurfaceVisible)))
        {
            OnPropertyChanged(nameof(IsRetailModelBrowserSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationWorkspaceSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationDiagnosticsDrawerVisible));
            SelectedExplorerTabIndex = 0;
            StatusText =
                "Base-game model browser opened; selection previews only";
        }
    }

    private void SetWorkspace(
        EditorWorkspaceMode workspace,
        bool preserveLegacyCutscene)
    {
        string previousLegacyName = _activeWorkspaceMode;
        if (workspace is not (
                EditorWorkspaceMode.Models or
                EditorWorkspaceMode.Browse))
        {
            _isCustomModelAuthoringSurfaceVisible = false;
        }

        bool usedLinkedTargetView = UsesLinkedTargetExternalView();
        bool hasActiveAnimation = GetActiveAnimation() is not null;
        bool hasLoadedComparison = hasActiveAnimation &&
            _sourceAnimation is not null &&
            _targetRig is not null;
        bool hasPlayableComparison = hasLoadedComparison &&
            ActiveTargetBindingStatus is
                TargetBindingStatus.Direct or
                TargetBindingStatus.Ready;
        bool wasShowingIsolatedPreview =
            _isolatedBrowsePreviewFrame is not null &&
            TargetViewport.SceneSource.HasExternalPreviewScene;
        bool shouldShowIsolatedPreview =
            workspace is
                EditorWorkspaceMode.Browse or
                EditorWorkspaceMode.Models or
                EditorWorkspaceMode.Animations ||
            (!hasActiveAnimation &&
             (workspace == EditorWorkspaceMode.Animate ||
              workspace == EditorWorkspaceMode.RetargetEdit));
        string legacyName = preserveLegacyCutscene
            ? "Cutscene"
            : workspace switch
            {
                EditorWorkspaceMode.Browse => "Browse",
                EditorWorkspaceMode.Models => "Models",
                EditorWorkspaceMode.Animate => "Playback",
                EditorWorkspaceMode.RetargetEdit => "Retarget",
                EditorWorkspaceMode.Face => "Facial",
                EditorWorkspaceMode.Fpp => "FPP",
                EditorWorkspaceMode.Animations => "Animations",
                EditorWorkspaceMode.Export => "Export",
                _ => "Models",
            };
        bool workspaceChanged = SetProperty(
            ref _activeWorkspace,
            workspace,
            nameof(ActiveWorkspace));
        bool legacyChanged = SetProperty(
            ref _activeWorkspaceMode,
            legacyName,
            nameof(ActiveWorkspaceMode));
        if ((workspaceChanged || legacyChanged) &&
            !_synchronizingPreviewConfiguration &&
            _project.SchemaVersion >= 2)
        {
            ProjectWorkflowTab activeTab = workspace switch
            {
                EditorWorkspaceMode.Models or
                EditorWorkspaceMode.Browse =>
                    ProjectWorkflowTab.Models,
                EditorWorkspaceMode.Animations =>
                    ProjectWorkflowTab.Animations,
                EditorWorkspaceMode.Animate or
                EditorWorkspaceMode.Fpp =>
                    ProjectWorkflowTab.Playback,
                EditorWorkspaceMode.RetargetEdit or
                EditorWorkspaceMode.Face =>
                    ProjectWorkflowTab.RetargetEdit,
                EditorWorkspaceMode.Export =>
                    ProjectWorkflowTab.Export,
                _ => ProjectWorkflowTab.Models,
            };
            if (_project.Workflow.ActiveTab != activeTab)
            {
                _project = _project with
                {
                    Workflow = _project.Workflow with
                    {
                        ActiveTab = activeTab,
                    },
                };
                UpdateDirtyState();
                OnPropertyChanged(nameof(CurrentProject));
            }
        }
        if (workspace == EditorWorkspaceMode.Face)
        {
            SelectedInspectorTabIndex = 4;
        }
        else if (workspace == EditorWorkspaceMode.RetargetEdit &&
                 SelectedInspectorTabIndex == 4)
        {
            SelectedInspectorTabIndex = 0;
        }
        PreviewLayoutMode layout = workspace switch
        {
            EditorWorkspaceMode.Browse =>
                PreviewLayoutMode.IsolatedBrowse,
            EditorWorkspaceMode.Models =>
                PreviewLayoutMode.IsolatedBrowse,
            EditorWorkspaceMode.Animations =>
                PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.Animate =>
                PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.RetargetEdit =>
                hasLoadedComparison
                    ? PreviewLayoutMode.RetargetComparison
                    : PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.Face =>
                hasPlayableComparison
                    ? PreviewLayoutMode.FacialComparison
                    : PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.Fpp =>
                hasPlayableComparison
                    ? PreviewLayoutMode.FppDualView
                    : PreviewLayoutMode.SingleAuthoritative,
            EditorWorkspaceMode.Export =>
                PreviewLayoutMode.SingleAuthoritative,
            _ => PreviewLayoutMode.IsolatedBrowse,
        };
        bool layoutChanged = _previewLayout != layout;
        if (layoutChanged)
        {
            _previewLayout = layout;
            OnPropertyChanged(nameof(PreviewLayout));
            OnPropertyChanged(nameof(IsLinkedCameraControlVisible));
        }

        IsSourceViewportVisible = layout is
            PreviewLayoutMode.RetargetComparison or
            PreviewLayoutMode.FacialComparison or
            PreviewLayoutMode.FppDualView;
        if (workspaceChanged || legacyChanged || layoutChanged)
        {
            if (wasShowingIsolatedPreview &&
                !shouldShowIsolatedPreview)
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
                    _suspendedTargetProjection =
                        previousLegacyName == "FPP"
                            ? SourceViewport.SceneSource
                                .CaptureFrame()
                                .FppProjectionState
                            : TargetViewport.SceneSource
                                .CaptureFrame()
                                .FppProjectionState;
                }

                _viewportCoordinator
                    .SetTargetPreviewCameraOverrideActive(false);
                _viewportCoordinator.SetPreviewCameraOverrideActive(
                    ViewportSide.Source,
                    false);
                TargetViewport.SceneSource.SetFppProjectionState(null);
                ClearLinkedTargetExternalView();
            }
            else if (!usedLinkedTargetView)
            {
                bool restoredPreviewCamera = legacyName == "FPP"
                    ? _viewportCoordinator.SetPreviewCameraOverrideActive(
                        ViewportSide.Source,
                        true)
                    : _viewportCoordinator
                        .SetTargetPreviewCameraOverrideActive(true);
                TargetViewport.SceneSource.SetFppProjectionState(null);
                if (!restoredPreviewCamera)
                {
                    _suspendedTargetProjection = null;
                }
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
                _viewportCoordinator.SetPreviewCameraOverride(
                    ViewportSide.Source,
                    null);
                _suspendedTargetProjection = null;
                TargetViewport.SceneSource.SetFppProjectionState(null);
                RestoreLinkedTargetExternalViewFromCurrentScene();
            }

            if (!wasShowingIsolatedPreview &&
                shouldShowIsolatedPreview)
            {
                RestoreIsolatedBrowsePreview();
            }
            else if (wasShowingIsolatedPreview &&
                     shouldShowIsolatedPreview)
            {
                UpdateIsolatedPreviewPresentation();
            }
            OnPropertyChanged(nameof(IsBrowseWorkspace));
            OnPropertyChanged(nameof(IsModelsWorkspace));
            OnPropertyChanged(nameof(IsCustomModelAuthoringSurfaceVisible));
            OnPropertyChanged(nameof(IsRetailModelBrowserSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationWorkspaceSurfaceVisible));
            OnPropertyChanged(nameof(IsAnimationDiagnosticsDrawerVisible));
            OnPropertyChanged(nameof(IsAnimationsWorkspace));
            OnPropertyChanged(nameof(IsAnimateWorkspace));
            OnPropertyChanged(nameof(IsPlaybackWorkspace));
            OnPropertyChanged(nameof(IsRetargetWorkspace));
            OnPropertyChanged(nameof(IsExportWorkspace));
            OnPropertyChanged(nameof(IsFaceWorkspace));
            OnPropertyChanged(nameof(IsFppWorkspace));
            OnPropertyChanged(nameof(IsFppPlaybackEnabled));
            OnPropertyChanged(nameof(IsAnimationAuthoringWorkspace));
            OnPropertyChanged(nameof(IsFaceOrFppWorkspace));
            OnPropertyChanged(nameof(IsInspectorPanelVisible));
            OnPropertyChanged(nameof(IsRetargetSetupVisible));
            OnPropertyChanged(nameof(RetargetSetupSelectionLabel));
            OnPropertyChanged(nameof(RetargetSetupInstructions));
            OnPropertyChanged(nameof(IsLinkedCameraControlVisible));
            OnPropertyChanged(nameof(ActivePreviewProfile));
            if (workspace is
                EditorWorkspaceMode.Models or
                EditorWorkspaceMode.Browse)
            {
                SelectedExplorerTabIndex = 0;
            }
            else if (workspace == EditorWorkspaceMode.Animations)
            {
                SelectedExplorerTabIndex = 1;
                ScheduleSelectedAnimationSourcePreview();
            }
            UpdateFidelityStatusBadges();
            if (workspace == EditorWorkspaceMode.RetargetEdit)
            {
                // Retarget/Edit owns two differently scaled scenes. Reframe
                // both when the workspace opens so a single-pane target
                // camera can never leave the raw FBX skeleton off-screen.
                FrameComparisonPanes(force: true);
            }
            if (_sourceAnimation is null)
            {
                UpdateUnevaluatedPreviewStatus(
                    workspace == EditorWorkspaceMode.RetargetEdit
                        ? "Retarget setup is waiting for an animation; the selected model preview remains isolated."
                        : "Load or activate an animation to evaluate this workspace.");
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
        UpdateIsolatedPreviewPresentation();
    }

    private void UpdateIsolatedPreviewPresentation()
    {
        if (_isolatedBrowsePreviewFrame is null)
        {
            return;
        }

        bool isAnimationSetup =
            ActiveWorkspace == EditorWorkspaceMode.Animate &&
            GetActiveAnimation() is null;
        bool isRetargetSetup =
            ActiveWorkspace == EditorWorkspaceMode.RetargetEdit &&
            GetActiveAnimation() is null;
        string setupTitle = isRetargetSetup
            ? "Retarget Setup"
            : "Animation Setup";
        TargetViewport.SetPresentation(
            isAnimationSetup || isRetargetSetup
                ? (_isolatedBrowsePreviewTitle ?? "Asset Preview")
                    .Replace(
                        "Asset Preview",
                        setupTitle,
                        StringComparison.Ordinal)
                : _isolatedBrowsePreviewTitle ?? "Asset Preview",
            isRetargetSetup
                ? "Bind-pose retail model selected; load an animation to create source and target bindings"
                : isAnimationSetup
                    ? "Bind-pose retail model ready; import or play an animation to begin authoring"
                : _isolatedBrowsePreviewFidelity ??
                    "Isolated retail asset; project state unchanged");
    }

    private void ClearIsolatedBrowsePreview()
    {
        TargetViewport.SceneSource.SetExternalPreviewScene(null);
        _isolatedBrowsePreviewFrame = null;
        _isolatedBrowsePreviewModel = null;
        _isolatedBrowsePreviewTitle = null;
        _isolatedBrowsePreviewFidelity = null;
        _authoringOrbitCameras = null;
        _browseOrbitCameras = null;
    }

    private void SetTargetBindingStatus(TargetBindingStatus status)
    {
        _editorSessionCoordinator.UpdateTargetStatus(status);
        Timeline.IsPlaybackEnabled =
            status != TargetBindingStatus.Invalid;
        if (_targetBindingStatus == status)
        {
            return;
        }

        _targetBindingStatus = status;
        OnPropertyChanged(nameof(ActiveTargetBindingStatus));
        OnPropertyChanged(nameof(TargetBindingStatusText));
        OnPropertyChanged(nameof(TargetPlaybackMessage));
        OnPropertyChanged(nameof(IsTargetPlaybackBlocked));
        OnPropertyChanged(nameof(IsDraftTargetPreview));
        RefreshActiveExportReadiness();
        if (ActiveWorkspace is
            EditorWorkspaceMode.RetargetEdit or
            EditorWorkspaceMode.Face or
            EditorWorkspaceMode.Fpp)
        {
            SetWorkspace(
                ActiveWorkspace,
                preserveLegacyCutscene: false);
        }
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
            ShowSkeletonOverlay,
            _pendingProjectAssets.Values
                .OrderBy(static receipt => receipt.RelativePath,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray());
    }

    public void RestoreSnapshot(WorkspaceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        AdoptPendingProjectAssetReceipts(snapshot.PendingAssets);
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

    private void AdoptPendingProjectAssetReceipts(
        ImmutableArray<PendingProjectAssetReceipt> receipts)
    {
        ImmutableArray<PendingProjectAssetReceipt> validated =
            PendingProjectAssetStore.ValidateSnapshotReceipts(
                receipts.IsDefault ? [] : receipts);
        _pendingProjectAssets.Clear();
        foreach (PendingProjectAssetReceipt receipt in validated)
        {
            _pendingProjectAssets.Add(receipt.AssetId, receipt);
        }
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
        Models.Tick(now);
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
             _activeDirectRigBinding is not null ||
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
        Models.PersistenceStateChanged -= OnModelsPersistenceStateChanged;
        Models.Dispose();
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

    private string? ResolveRetailData0PakPath()
    {
        Dl1InstallLocation? install = _assetWorkspace.Install ??
            SteamInstallDiscovery.Discover()
                .FirstOrDefault(static candidate => candidate.IsValid);
        if (install is null)
        {
            return null;
        }

        string path = Path.Combine(install.InstallPath, "DW", "Data0.pak");
        return File.Exists(path) ? path : null;
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

    private async Task RestoreRecoveryAsync()
    {
        if (_recoverySnapshot is null)
        {
            return;
        }

        AnimationRuntimeSnapshot previous =
            CaptureAnimationRuntimeSnapshot();
        ModelsWorkspaceSessionSnapshot previousModels =
            Models.CaptureProjectSession();
        Dictionary<Guid, PendingProjectAssetReceipt> previousPendingAssets =
            new(_pendingProjectAssets);
        long previousSavedModelsRevision = _savedModelsRevision;
        long previousIntegratedModelsRevision = _integratedModelsRevision;
        string? previousProjectPath = ProjectPath;
        try
        {
            WorkspaceSnapshot snapshot = _recoverySnapshot;
            AdoptPendingProjectAssetReceipts(snapshot.PendingAssets);
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

            PreparedModelsWorkspaceRestore? preparedModels =
                snapshot.Project is null
                    ? null
                    : await PrepareModelsWorkspaceRestoreAsync(
                        snapshot.Project,
                        snapshot.ProjectPath,
                        CancellationToken.None);

            RestoreSnapshot(snapshot);
            CommitModelsWorkspaceRestore(preparedModels);
            _savedModelsRevision = snapshot.IsProjectDirty
                ? -1
                : Models.PersistenceRevision;
            _integratedModelsRevision = Models.PersistenceRevision;
            UpdateDirtyState();
            ProjectAnimation? activeAnimation = GetActiveAnimation();
            if (activeAnimation is not null)
            {
                if (CanRestoreAnimationFromLoadedCatalog(
                        activeAnimation,
                        _project.Assets,
                        _indexedAssetItems))
                {
                    await ActivateAnimationAsync(
                        activeAnimation.Id,
                        beginPlayback: false,
                        persistActivation: false);
                }
                else
                {
                    await LoadActiveSourceAsync(
                        snapshot.ProjectPath ?? string.Empty);
                }

                if (_sourceAnimation is null ||
                    (activeAnimation.TargetAssetId is not null &&
                     (_targetRig is null ||
                      _targetProjectAsset is null)))
                {
                    throw new InvalidOperationException(
                        "The saved animation and its exact fingerprinted target could not be restored as a playable session. The recovery snapshot was retained.");
                }
            }

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
            CustomModelFormatException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            RestorePendingProjectAssets(previousPendingAssets);
            ProjectPath = previousProjectPath;
            RestoreAnimationRuntimeSnapshot(previous);
            Models.RestoreProjectSession(previousModels);
            _savedModelsRevision = previousSavedModelsRevision;
            _integratedModelsRevision = previousIntegratedModelsRevision;
            UpdateDirtyState();
            NotifyProjectChanged();
            AddDiagnostic(
                "Error",
                "Recovery",
                "Recovery could not be restored transactionally; previous session retained",
                exception.ToString());
            StatusText =
                "Recovery restore failed; the previous session and original snapshot were retained";
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
        _pendingProjectAssets.Clear();
        Models.ClearProjectSession();
        _savedModelsRevision = Models.PersistenceRevision;
        _integratedModelsRevision = Models.PersistenceRevision;
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

        AnimationRuntimeSnapshot previous =
            CaptureAnimationRuntimeSnapshot();
        ModelsWorkspaceSessionSnapshot previousModels =
            Models.CaptureProjectSession();
        Dictionary<Guid, PendingProjectAssetReceipt> previousPendingAssets =
            new(_pendingProjectAssets);
        long previousSavedModelsRevision = _savedModelsRevision;
        long previousIntegratedModelsRevision = _integratedModelsRevision;
        string? previousProjectPath = ProjectPath;
        IsBusy = true;
        StatusText = $"Opening {Path.GetFileName(path)}…";
        try
        {
            _pendingProjectAssets.Clear();
            DlraProject loaded = await Task.Run(
                () => ProjectSerializer.Load(path));
            PreparedModelsWorkspaceRestore? preparedModels =
                await PrepareModelsWorkspaceRestoreAsync(
                    loaded,
                    path,
                    CancellationToken.None);
            SetProject(
                loaded,
                markSaved: true,
                clearHistory: true,
                clearPreview: true);
            CommitModelsWorkspaceRestore(preparedModels);
            _savedModelsRevision = Models.PersistenceRevision;
            _integratedModelsRevision = Models.PersistenceRevision;
            UpdateDirtyState();
            ProjectPath = path;
            AddRecentProjectPath(path);
            ProjectAnimation? activeAnimation = GetActiveAnimation();
            if (activeAnimation is not null &&
                CanRestoreAnimationFromLoadedCatalog(
                    activeAnimation,
                    _project.Assets,
                    _indexedAssetItems))
            {
                await ActivateAnimationAsync(
                    activeAnimation.Id,
                    beginPlayback: false,
                    persistActivation: false);
                if (_sourceAnimation is null)
                {
                    throw new InvalidOperationException(
                        "The saved animation could not be prepared as a playable session.");
                }
            }
            else
            {
                // A local FBX can be restored before the retail catalog is
                // available. If the document also has a saved retail target,
                // LoadAssetCatalogAsync completes the exact target restore as
                // soon as its fingerprint is available.
                await LoadActiveSourceAsync(path);
            }
            if (activeAnimation is null || _sourceAnimation is not null)
            {
                StatusText = $"Opened DL1 project {loaded.Name}";
            }
        }
        catch (LegacyProjectFormatException exception)
        {
            RestorePendingProjectAssets(previousPendingAssets);
            ProjectPath = previousProjectPath;
            RestoreAnimationRuntimeSnapshot(previous);
            Models.RestoreProjectSession(previousModels);
            _savedModelsRevision = previousSavedModelsRevision;
            _integratedModelsRevision = previousIntegratedModelsRevision;
            UpdateDirtyState();
            NotifyProjectChanged();
            AddDiagnostic(
                "Error",
                "Project",
                "Legacy Python project was not opened",
                exception.Message);
            StatusText = "Project open failed";
        }
        catch (Exception exception) when (
            exception is ProjectFormatException
            or CustomModelFormatException
            or InvalidDataException
            or InvalidOperationException
            or IOException
            or UnauthorizedAccessException)
        {
            RestorePendingProjectAssets(previousPendingAssets);
            ProjectPath = previousProjectPath;
            RestoreAnimationRuntimeSnapshot(previous);
            Models.RestoreProjectSession(previousModels);
            _savedModelsRevision = previousSavedModelsRevision;
            _integratedModelsRevision = previousIntegratedModelsRevision;
            UpdateDirtyState();
            NotifyProjectChanged();
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

    private void RestorePendingProjectAssets(
        IReadOnlyDictionary<Guid, PendingProjectAssetReceipt> receipts)
    {
        _pendingProjectAssets.Clear();
        foreach ((Guid assetId, PendingProjectAssetReceipt receipt) in receipts)
        {
            _pendingProjectAssets.Add(assetId, receipt);
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
                "Playback is blocked. Select an exact-signature fingerprinted model and use Rebind Source; the existing authored document will not be mutated.");
            StatusText = "Animation source needs an explicit rebind";
            return;
        }

        bool isEmbeddedCustomModelStack =
            _activeAnimationId is { } activeId &&
            _project.AnimationVariants.FirstOrDefault(variant =>
                variant.Id == activeId) is { } activeVariant &&
            _project.AnimationSources.FirstOrDefault(source =>
                source.Id == activeVariant.SourceId)?
                .EmbeddedCustomModelStack is not null;
        if (sourceBinding.Kind == AnimationSourceKind.LocalFbx &&
            (isEmbeddedCustomModelStack ||
             TryParseCustomModelStackResourceId(
                 sourceAsset.ResourceId,
                 out _,
                 out _)))
        {
            await ActivateAnimationAsync(
                animation.Id,
                beginPlayback: false,
                persistActivation: false);
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
                    "The saved ANM2 does not identify an exact fingerprinted source model",
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
            RigDefinition importedRig;
            AnimationClip importedClip;
            AnimationClip? facialClip = null;
            if (TryParseExternalFbxStackTimingDetail(
                    sourceBinding.TimingDetail,
                    out long stackObjectId,
                    out string stackFingerprint,
                    out FbxFacialSourceValueUnit sourceValueUnit,
                    out bool usesSelectedModelRig,
                    out Guid? selectedSourceModelId))
            {
                ImmutableArray<FbxExternalAnimationImportResult> rows =
                    await FbxExternalAnimationImportService
                        .ImportFileSelectedAsync(
                            sourcePath,
                            [stackObjectId],
                            new FbxExternalAnimationImportOptions
                            {
                                FacialSourceValueUnit =
                                    sourceValueUnit,
                            });
                FbxExternalAnimationImportResult imported =
                    AssertSingleExternalStack(
                        rows,
                        stackObjectId,
                        stackFingerprint);
                importedRig = imported.SourceRig ??
                    (usesSelectedModelRig
                        ? await DecodeFacialOnlySourceRigAsync(
                            animation,
                            selectedSourceModelId,
                            CancellationToken.None)
                        : throw new InvalidDataException(
                            "The external FBX stack lost its embedded source skeleton."));
                importedClip = imported.Clip;
                facialClip = imported.Facial?.Clip;
            }
            else
            {
                FbxCoreAnimationImportResult imported =
                    await new FbxAnimationDecoder().DecodeFileAsync(
                        sourcePath);
                ReportFbxAnimationDomainImport(imported);
                importedRig = imported.Rig;
                importedClip = imported.Clip;
            }

            if (!string.Equals(
                    RigSignature.Compute(importedRig),
                    sourceBinding.SourceRigSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The saved external FBX stack source rig no longer matches its immutable binding.");
            }

            _pendingAnm2SourcePath = null;
            _sourceAnimation = new ImportedAnimationSession(
                importedRig,
                importedClip,
                sourcePath,
                "FBX")
            {
                SourceKindContract = AnimationSourceKind.LocalFbx,
                TimingProvenance =
                    AnimationTimingProvenance.EmbeddedFbx,
                TimingDetail = sourceBinding.TimingDetail,
                FacialClip = facialClip,
                DeclaredRoles = sourceBinding.Roles,
            };
            _sourceBaseMeshes = [];
            _synchronizedAnimation = importedClip;
            SourceViewport.SceneSource.SetScene(
                [],
                CorePreviewAdapter.ToRenderSkeleton(
                    importedRig.CreateBindPose()),
                []);
            RefreshProjectBindings();
            OnPropertyChanged(nameof(ActiveSourceModelLabel));
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
        if (!string.IsNullOrWhiteSpace(ProjectPath))
        {
            string projectDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(ProjectPath)) ??
                throw new InvalidOperationException(
                    "The project path has no parent directory.");
            try
            {
                return ResolveProjectAssetPath(
                    projectDirectory,
                    asset,
                    "animation source");
            }
            catch (FileNotFoundException) when (
                _pendingProjectAssets.ContainsKey(asset.Id))
            {
            }
        }

        if (_pendingProjectAssets.TryGetValue(
                asset.Id,
                out PendingProjectAssetReceipt? receipt))
        {
            return _pendingProjectAssetStore.ResolveAsync(
                    receipt,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        throw new FileNotFoundException(
            "The project-relative animation source is neither saved nor present in recovery staging.",
            asset.RelativePath);
    }

    private async Task<string> ResolveProjectAssetPathAsync(
        ProjectAssetReference asset,
        string description,
        CancellationToken cancellationToken,
        string? projectPath = null)
    {
        string? effectiveProjectPath = projectPath ?? ProjectPath;
        string? path = null;
        if (!string.IsNullOrWhiteSpace(effectiveProjectPath))
        {
            string projectDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(effectiveProjectPath)) ??
                throw new InvalidOperationException(
                    "The project path has no parent directory.");
            try
            {
                path = ResolveProjectAssetPath(
                    projectDirectory,
                    asset,
                    description);
            }
            catch (FileNotFoundException) when (
                _pendingProjectAssets.ContainsKey(asset.Id))
            {
            }
        }

        if (path is null && _pendingProjectAssets.TryGetValue(
                asset.Id,
                out PendingProjectAssetReceipt? receipt))
        {
            path = await _pendingProjectAssetStore.ResolveAsync(
                receipt,
                cancellationToken);
        }

        if (path is null)
        {
            throw new FileNotFoundException(
                $"The project-relative {description} is neither saved nor present in recovery staging.",
                asset.RelativePath);
        }

        await VerifyLocalProjectAssetHashAsync(
            asset,
            path,
            description,
            cancellationToken);
        return path;
    }

    private async Task<PreparedCustomModelSource> DecodeCustomModelSourceAsync(
        ProjectAssetReference sourceAsset,
        ProjectAnimation animation,
        ProjectAnimationSourceBinding binding,
        ProjectEmbeddedAnimationStackIdentity? embeddedStack,
        CancellationToken cancellationToken)
    {
        Guid expectedModelId = Guid.Empty;
        Guid clipId = Guid.Empty;
        bool legacyStackReference =
            sourceAsset.Kind == ProjectAssetKind.SourceAnimation &&
            TryParseCustomModelStackResourceId(
                sourceAsset.ResourceId,
                out expectedModelId,
                out clipId);
        bool embeddedStackReference =
            embeddedStack is not null &&
            sourceAsset.Kind == ProjectAssetKind.CustomModelSource &&
            TryParseCustomModelResourceId(
                sourceAsset.ResourceId,
                out expectedModelId);
        if (!legacyStackReference && !embeddedStackReference)
        {
            throw new InvalidDataException(
                "The custom-model animation source identity is invalid.");
        }

        if (embeddedStack is not null)
        {
            clipId = embeddedStack.ClipId;
        }

        string packagePath = await ResolveProjectAssetPathAsync(
            sourceAsset,
            "custom-model package",
            cancellationToken);
        CustomModelPackage package = await Task.Run(
            () => CustomModelPackageSerializer.Load(packagePath),
            cancellationToken);
        if (package.Document.ModelId != expectedModelId)
        {
            throw new InvalidDataException(
                "The .dlrmodel identity differs from the saved animation source.");
        }

        FbxModelAuthoringImportResult imported = await Task.Run(
            () => FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken),
            cancellationToken);
        RigDefinition rig = imported.Rig ??
            throw new InvalidDataException(
                "The saved custom model no longer provides a rig.");
        string signature = RigSignature.Compute(rig);
        if (!string.Equals(
                signature,
                binding.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The custom-model rig differs from its immutable saved source signature.");
        }

        CustomModelAnimationClip selection = package.Document.AnimationClips
            .FirstOrDefault(candidate => candidate.Id == clipId) ??
            throw new InvalidDataException(
                "The selected animation stack is missing from the saved .dlrmodel.");
        if (embeddedStack is not null &&
            (selection.FbxObjectId != embeddedStack.FbxObjectId ||
             !string.Equals(
                 selection.SourceFingerprint,
                 embeddedStack.StackFingerprint,
                 StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The selected custom-model animation stack differs from its persisted object identity or fingerprint.");
        }
        if (!imported.AnimationClips.TryGetValue(
                clipId,
                out AnimationClip? decoded))
        {
            throw new InvalidDataException(
                $"Animation stack '{selection.DisplayName}' can no longer be decoded.");
        }

        AnimationClip clip = ApplyCustomModelClipSettings(
            decoded,
            selection with
            {
                DisplayName = animation.Name,
                FrameRate = animation.FrameRate,
            });
        if (clip.FrameCount != animation.FrameCount)
        {
            throw new InvalidDataException(
                "The custom-model animation frame count differs from the saved project document.");
        }

        CustomModelPreviewPayload preview = CustomModelPreviewAdapter.Create(
            imported,
            clip,
            frame: 0);
        if (preview.Skeleton is null)
        {
            throw new InvalidDataException(
                "The custom-model animation has no renderable source skeleton.");
        }

        return new PreparedCustomModelSource(
            imported,
            selection,
            clip,
            preview,
            packagePath);
    }

    private async Task<(ProjectAnimationSource Source,
        ProjectAnimationVariant Variant)> RepairCustomModelIdentityAsync(
        ProjectAnimationSource source,
        ProjectAnimationVariant variant,
        CancellationToken cancellationToken)
    {
        if (source.EmbeddedCustomModelStack is null)
        {
            return (source, variant);
        }

        ProjectAssetReference packageAsset = FindProjectAsset(
                source.SourceAssetId)
            ?? throw new InvalidDataException(
                "The embedded custom-model source asset is missing.");
        string packagePath = await ResolveProjectAssetPathAsync(
            packageAsset,
            "embedded custom-model package",
            cancellationToken);
        CustomModelPackage package = await Task.Run(
            () => CustomModelPackageSerializer.Load(packagePath),
            cancellationToken);
        if (!TryParseCustomModelResourceId(
                packageAsset.ResourceId,
                out Guid expectedModelId) ||
            package.Document.ModelId != expectedModelId)
        {
            throw new InvalidDataException(
                "The embedded .dlrmodel identity differs from its project asset.");
        }

        FbxModelAuthoringImportResult imported = await Task.Run(
            () => FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken),
            cancellationToken);
        CustomModelProjectIdentityRepairResult repaired =
            CustomModelProjectIdentityRepair.Repair(
                _project,
                packageAsset,
                imported);
        if (repaired.WasRepaired)
        {
            CommitProject(repaired.Project);
            AddDiagnostic(
                "Information",
                "Project migration",
                "Repaired custom-model runtime rig identity",
                "The project had stored the .dlrmodel reimport contract where exact runtime animation identity was required. Model, source, and variant identifiers were preserved.");
        }

        ProjectAnimationSource repairedSource = repaired.Project
            .AnimationSources.Single(candidate => candidate.Id == source.Id);
        ProjectAnimationVariant repairedVariant = repaired.Project
            .AnimationVariants.Single(candidate => candidate.Id == variant.Id);
        string activeSignature = repairedSource.SourceRigSignature;
        if (!string.Equals(
                activeSignature,
                repaired.RuntimeRigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The embedded animation source has a rig signature that matches neither its saved authoring contract nor its reconstructed runtime rig.");
        }

        return (repairedSource, repairedVariant);
    }

    private async Task<PreparedCustomTarget> DecodeCustomModelTargetAsync(
        ProjectAssetReference targetAsset,
        CancellationToken cancellationToken)
    {
        if (targetAsset.Kind != ProjectAssetKind.CustomModelSource)
        {
            throw new InvalidDataException(
                "The requested project asset is not a custom-model target.");
        }

        string packagePath = await ResolveProjectAssetPathAsync(
            targetAsset,
            "custom-model target",
            cancellationToken);
        CustomModelPackage package = await Task.Run(
            () => CustomModelPackageSerializer.Load(packagePath),
            cancellationToken);
        string[] identity = targetAsset.ResourceId?.Split(':') ?? [];
        if (identity.Length < 2 ||
            !string.Equals(identity[0], "custom-model", StringComparison.Ordinal) ||
            !Guid.TryParseExact(identity[1], "N", out Guid modelId) ||
            package.Document.ModelId != modelId)
        {
            throw new InvalidDataException(
                "The .dlrmodel identity differs from the saved custom target.");
        }

        FbxModelAuthoringImportResult imported = await Task.Run(
            () => FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken),
            cancellationToken);
        RigDefinition rig = imported.Rig ??
            throw new InvalidDataException(
                "The saved custom target no longer provides a rig.");
        RepairDecodedCustomModelTargetIdentity(
            targetAsset,
            imported);
        CustomModelPreviewSession previewSession =
            CreateCustomModelAnimationTargetPreview(imported);
        CustomModelPreviewPayload preview = previewSession.CreatePayload(
            clip: null,
            frame: 0);
        return new PreparedCustomTarget(
            rig,
            preview.Meshes.ToArray(),
            preview.Skeleton ??
                throw new InvalidDataException(
                    "The custom target has no renderable skeleton."),
            targetAsset,
            previewSession,
            previewSession.RequestedMode,
            previewSession.EffectiveMode,
            previewSession.Diagnostics);
    }

    private void RepairDecodedCustomModelTargetIdentity(
        ProjectAssetReference targetAsset,
        FbxModelAuthoringImportResult imported)
    {
        int ownerCount = _project.Models.Count(model =>
            model.AssetId == targetAsset.Id);
        if (ownerCount == 0)
        {
            // Immutable historical packages can remain as source-only assets
            // after a model reimport. They have no current target-model record
            // to normalize.
            return;
        }

        CustomModelProjectIdentityRepairResult repaired =
            CustomModelProjectIdentityRepair.Repair(
                _project,
                targetAsset,
                imported);
        if (!repaired.WasRepaired)
        {
            return;
        }

        CommitProject(repaired.Project);
        AddDiagnostic(
            "Information",
            "Project migration",
            "Repaired custom-model target runtime identity",
            "The hash-verified .dlrmodel target had stored its authoring reimport contract where runtime animation identity was required. Stable model, source, and variant IDs were retained.");
    }

    internal static CustomModelPreviewSession
        CreateCustomModelAnimationTargetPreview(
            FbxModelAuthoringImportResult imported)
    {
        ArgumentNullException.ThrowIfNull(imported);
        return CustomModelPreviewAdapter.CreateSession(
            imported,
            CustomModelPreviewMode.Dl1Output);
    }

    private static async Task VerifyLocalProjectAssetHashAsync(
        ProjectAssetReference asset,
        string path,
        string description,
        CancellationToken cancellationToken)
    {
        string expected = asset.ContentSha256 ??
            throw new InvalidDataException(
                $"The saved {description} has no content fingerprint.");
        string actual = await ProjectSourceImporter.ComputeSha256Async(
            path,
            cancellationToken);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {description} differs from its saved project fingerprint.");
        }
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
            projectToSave = await PersistModelsWorkspaceAsync(
                projectToSave,
                path,
                CancellationToken.None);
            projectToSave = NormalizeAnimationVariantGroups(
                projectToSave);
            projectToSave =
                SynchronizeSchema2FromCompatibilityAnimations(
                    _project,
                    projectToSave);
            projectToSave = ProjectAnimationOutputNormalizer.Normalize(
                projectToSave);
            projectToSave.Validate();
            await MaterializePendingProjectAssetsAsync(
                projectToSave,
                path,
                CancellationToken.None);
            string savedPath = await Task.Run(
                () => ProjectSerializer.SaveAtomic(projectToSave, path));
            ProjectPath = savedPath;
            _project = projectToSave;
            _savedProject = projectToSave;
            _savedModelsRevision = Models.PersistenceRevision;
            _integratedModelsRevision = Models.PersistenceRevision;
            ClearMaterializedPendingProjectAssets(projectToSave);
            UpdateDirtyState();
            AddRecentProjectPath(savedPath);
            StatusText = $"Saved {Path.GetFileName(savedPath)}";
        }
        catch (Exception exception) when (
            exception is ProjectFormatException
            or CustomModelFormatException
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

    private async Task MaterializePendingProjectAssetsAsync(
        DlraProject project,
        string projectPath,
        CancellationToken cancellationToken)
    {
        HashSet<Guid> referenced = project.Assets
            .Select(static asset => asset.Id)
            .ToHashSet();
        foreach (PendingProjectAssetReceipt receipt in _pendingProjectAssets
                     .Values
                     .Where(receipt => referenced.Contains(receipt.AssetId))
                     .OrderBy(static receipt => receipt.RelativePath,
                         StringComparer.OrdinalIgnoreCase))
        {
            ProjectAssetReference asset = project.Assets.Single(candidate =>
                candidate.Id == receipt.AssetId);
            if (!string.Equals(
                    asset.RelativePath,
                    receipt.RelativePath,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    asset.ContentSha256,
                    receipt.ContentSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "A recovery staging receipt disagrees with its project asset identity.");
            }

            _ = await _pendingProjectAssetStore.MaterializeAsync(
                receipt,
                projectPath,
                cancellationToken);
        }
    }

    private void ClearMaterializedPendingProjectAssets(DlraProject project)
    {
        foreach (Guid assetId in project.Assets
                     .Select(static asset => asset.Id)
                     .Where(_pendingProjectAssets.ContainsKey)
                     .ToArray())
        {
            PendingProjectAssetReceipt receipt = _pendingProjectAssets[assetId];
            try
            {
                _pendingProjectAssetStore.Delete(receipt);
                _pendingProjectAssets.Remove(assetId);
            }
            catch (IOException)
            {
                // The project is already durable. A stale recovery copy is
                // harmless and can be cleaned by a later successful save.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void OnModelsPersistenceStateChanged(
        object? sender,
        EventArgs args)
    {
        UpdateDirtyState();
        QueueModelsWorkspaceProjectSynchronization();
    }

    private void QueueModelsWorkspaceProjectSynchronization()
    {
        _modelsIntegrationSource?.Cancel();
        _modelsIntegrationSource?.Dispose();
        _modelsIntegrationSource = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeSource.Token);
        CancellationToken token = _modelsIntegrationSource.Token;
        _ = SynchronizeModelsWorkspaceProjectInBackgroundAsync(token);
    }

    private async Task SynchronizeModelsWorkspaceProjectInBackgroundAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            await SynchronizeModelsWorkspaceProjectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidDataException or
            InvalidOperationException or IOException or UnauthorizedAccessException or
            CustomModelFormatException)
        {
            AddDiagnostic(
                "Error",
                "Models",
                "Custom-model project synchronization failed",
                exception.Message);
            StatusText =
                "Custom-model edits remain in recovery staging; project synchronization failed";
        }
    }

    private Task SynchronizeModelsWorkspaceProjectAsync() =>
        SynchronizeModelsWorkspaceProjectAsync(CancellationToken.None);

    private async Task SynchronizeModelsWorkspaceProjectAsync(
        CancellationToken cancellationToken)
    {
        await _modelsIntegrationGate.WaitAsync(cancellationToken);
        try
        {
            long revision = Models.PersistenceRevision;
            if (revision == _integratedModelsRevision)
            {
                return;
            }

            ModelsWorkspacePersistencePayload? payload =
                Models.CreatePersistencePayload();
            DlraProject updated = await IntegrateModelsWorkspaceAsync(
                _project,
                payload,
                ProjectPath,
                cancellationToken);
            if (!updated.Equals(_project))
            {
                CommitProject(updated);
            }

            _integratedModelsRevision = revision;
        }
        finally
        {
            _modelsIntegrationGate.Release();
        }
    }

    private async Task<DlraProject> PersistModelsWorkspaceAsync(
        DlraProject project,
        string projectPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        return await IntegrateModelsWorkspaceAsync(
            project,
            Models.CreatePersistencePayload(),
            projectPath,
            cancellationToken);
    }

    private async Task<DlraProject> IntegrateModelsWorkspaceAsync(
        DlraProject project,
        ModelsWorkspacePersistencePayload? payload,
        string? materializeProjectPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (payload is null)
        {
            if (project.ModelsWorkspace is null)
            {
                return project;
            }

            DlraProject cleared = project with { ModelsWorkspace = null };
            cleared.Validate();
            return cleared;
        }

        string sha256 = Convert.ToHexString(
                SHA256.HashData(payload.PackageBytes.AsSpan()))
            .ToLowerInvariant();
        string resourceId = CreateCustomModelResourceId(
            payload.ModelId,
            Path.GetFileNameWithoutExtension(payload.SuggestedFileName));
        ProjectAssetReference? exactAsset = project.Assets.FirstOrDefault(asset =>
            asset.Kind == ProjectAssetKind.CustomModelSource &&
            string.Equals(asset.ResourceId, resourceId, StringComparison.Ordinal) &&
            string.Equals(asset.ContentSha256, sha256, StringComparison.OrdinalIgnoreCase));
        ProjectModelEntry? previousModel = FindCustomModelEntry(
            project,
            payload.ModelId);
        ProjectAssetReference? previousAsset = previousModel is null
            ? null
            : project.Assets.FirstOrDefault(asset =>
                asset.Id == previousModel.AssetId);
        bool previousAssetMayBeReplaced = previousAsset is not null &&
            !IsAssetReferencedAsImmutableAnimationInput(project, previousAsset.Id);
        Guid packageAssetId = exactAsset?.Id ??
            (previousAssetMayBeReplaced ? previousAsset!.Id : Guid.NewGuid());
        string relativePath = exactAsset?.RelativePath ??
            (previousAssetMayBeReplaced
                ? previousAsset!.RelativePath
                : CreatePendingCustomModelRelativePath(
                    project,
                    payload,
                    sha256));
        PendingProjectAssetReceipt staged =
            await _pendingProjectAssetStore.StageAsync(
                packageAssetId,
                relativePath,
                payload.PackageBytes.ToArray(),
                cancellationToken);
        _pendingProjectAssets[packageAssetId] = staged;
        if (!string.IsNullOrWhiteSpace(materializeProjectPath))
        {
            _ = await _pendingProjectAssetStore.MaterializeAsync(
                staged,
                materializeProjectPath,
                cancellationToken);
        }

        var packageAsset = new ProjectAssetReference
        {
            Id = packageAssetId,
            Kind = ProjectAssetKind.CustomModelSource,
            RelativePath = relativePath,
            ResourceId = resourceId,
            ContentSha256 = sha256,
        };
        ImmutableArray<ProjectAssetReference> assets = project.Assets
            .Where(asset => asset.Id != packageAssetId)
            .Where(asset =>
                previousAsset is null ||
                asset.Id != previousAsset.Id ||
                IsAssetReferencedAsImmutableAnimationInput(project, asset.Id))
            .Append(packageAsset)
            .ToImmutableArray();
        ImmutableArray<ProjectAnimationLibrary> animationLibraries =
            project.AnimationLibraries;
        Guid? rootAnimationLibraryId =
            previousModel?.RootAnimationLibraryId;
        if (!string.IsNullOrWhiteSpace(payload.AnimationScriptAlias))
        {
            string alias = Dl1SourceModelWriter.SanitizeName(
                payload.AnimationScriptAlias.Trim(),
                63);
            if (!string.Equals(
                    alias,
                    payload.AnimationScriptAlias.Trim(),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The custom model animation-script alias must already be an exact DL1 resource name.");
            }

            ProjectAnimationLibrary? matchingLibrary = animationLibraries
                .FirstOrDefault(library => string.Equals(
                    library.ResourceName,
                    alias,
                    StringComparison.OrdinalIgnoreCase));
            if (matchingLibrary is null)
            {
                matchingLibrary = new ProjectAnimationLibrary
                {
                    Id = Guid.NewGuid(),
                    ResourceName = alias,
                    DisplayName = $"{alias} animations",
                    Mode = ProjectAnimationLibraryMode.CustomAdditive,
                    CollisionPolicy =
                        ProjectAnimationSequenceCollisionPolicy.Reject,
                };
                animationLibraries = animationLibraries.Add(
                    matchingLibrary);
            }

            rootAnimationLibraryId = matchingLibrary.Id;
        }
        var modelEntry = new ProjectModelEntry
        {
            Id = previousModel?.Id ?? Guid.NewGuid(),
            AssetId = packageAssetId,
            Name = string.IsNullOrWhiteSpace(Models.ModelName)
                ? Path.GetFileNameWithoutExtension(payload.SuggestedFileName)
                : Models.ModelName,
            RigSignature = payload.RuntimeRigSignature,
            AuthoringRigContractSignature =
                payload.AuthoringRigContractSignature,
            AnimationSkeletonSignature =
                payload.AnimationSkeletonSignature,
            Dl1OutputRigSignature = payload.Dl1OutputRigSignature,
            Dl1DescriptorInventoryFingerprint =
                payload.Dl1DescriptorInventoryFingerprint,
            RootAnimationLibraryId = rootAnimationLibraryId,
            MorphSignature = payload.MorphSignature,
            IsStatic = Models.SelectedRigMode == CustomModelRigMode.StaticProp,
            ExportableEyeCameraHelperCount =
                payload.ExportableEyeCameraHelperCount,
            PreviewCameraNodeName = payload.PreviewCameraNodeName,
        };
        DlraProject reconciled = project with
        {
            Assets = assets,
            AnimationLibraries = animationLibraries,
        };
        reconciled = previousModel is null
            ? reconciled with
            {
                Models = reconciled.Models.Add(modelEntry),
            }
            : ProjectModelReimportReconciler.Apply(reconciled, modelEntry);
        reconciled = ReconcileEmbeddedCustomModelStacks(
            reconciled,
            packageAsset,
            modelEntry,
            payload);
        DlraProject updated = reconciled with
        {
            ModelsWorkspace = payload.CreateProjectState(packageAssetId),
            // Model authoring is selection-only. In particular, importing a
            // model must not silently replace the active source or target.
            Workflow = project.Workflow,
            ActiveAnimationId = project.ActiveAnimationId,
        };
        updated.Validate();
        return updated;
    }

    private static string CreatePendingCustomModelRelativePath(
        DlraProject project,
        ModelsWorkspacePersistencePayload payload,
        string sha256)
    {
        string stem = Dl1SourceModelWriter.SanitizeName(
            Path.GetFileNameWithoutExtension(payload.SuggestedFileName),
            40);
        string candidate =
            $"Sources/{stem}-{payload.ModelId:N}.dlrmodel";
        if (project.Assets.All(asset => !string.Equals(
                asset.RelativePath,
                candidate,
                StringComparison.OrdinalIgnoreCase)))
        {
            return candidate;
        }

        return $"Sources/{stem}-{sha256[..12]}.dlrmodel";
    }

    private static DlraProject ReconcileEmbeddedCustomModelStacks(
        DlraProject project,
        ProjectAssetReference packageAsset,
        ProjectModelEntry modelEntry,
        ModelsWorkspacePersistencePayload payload)
    {
        var sources = project.AnimationSources.ToBuilder();
        var variants = project.AnimationVariants.ToBuilder();
        foreach (ModelsWorkspaceEmbeddedStackPayload stack in
                 payload.EmbeddedStacks)
        {
            CustomModelAnimationClip selection = stack.Selection;
            ProjectAnimationSource? source = sources.FirstOrDefault(candidate =>
                candidate.SourceAssetId == packageAsset.Id &&
                candidate.EmbeddedCustomModelStack is { } embedded &&
                embedded.ClipId == selection.Id &&
                embedded.FbxObjectId == selection.FbxObjectId &&
                string.Equals(
                    embedded.StackFingerprint,
                    selection.SourceFingerprint,
                    StringComparison.OrdinalIgnoreCase));
            if (source is null && selection.Included && stack.IsDecoded)
            {
                source = new ProjectAnimationSource
                {
                    Id = Guid.NewGuid(),
                    Name = selection.DisplayName,
                    SourceAssetId = packageAsset.Id,
                    EmbeddedCustomModelStack =
                        new ProjectEmbeddedAnimationStackIdentity
                        {
                            ClipId = selection.Id,
                            FbxObjectId = selection.FbxObjectId,
                            StackFingerprint = selection.SourceFingerprint,
                            SourceRigSignature = payload.RuntimeRigSignature ??
                                throw new InvalidDataException(
                                    "A decoded embedded animation stack requires a runtime rig signature."),
                            SourceAnimationSkeletonSignature =
                                payload.AnimationSkeletonSignature,
                            Roles = ResolveEmbeddedCustomModelRoles(selection),
                            FacialSourceValueUnit =
                                ResolveEmbeddedFacialSourceUnit(selection),
                        },
                    FrameRate = selection.FrameRate,
                    FrameCount = selection.FrameCount,
                    SourceAnimationSkeletonSignature =
                        payload.AnimationSkeletonSignature,
                    Presentation = new ProjectAnimationSourcePresentation
                    {
                        OriginKind =
                            ProjectAnimationSourceOriginKind.OwningCustomModel,
                        OriginName = modelEntry.Name,
                        OwningModelId = modelEntry.Id,
                        ProjectAssetId = packageAsset.Id,
                        SourceRigIdentity = payload.RigId,
                    },
                };
                sources.Add(source);
            }

            if (source is null || modelEntry.IsStatic)
            {
                continue;
            }

            ProjectAnimationVariant? variant = variants.FirstOrDefault(candidate =>
                candidate.SourceId == source.Id &&
                candidate.TargetModelId == modelEntry.Id);
            if (variant is null)
            {
                if (string.IsNullOrWhiteSpace(payload.RigId) ||
                    string.IsNullOrWhiteSpace(payload.RuntimeRigSignature) ||
                    string.IsNullOrWhiteSpace(
                        payload.AnimationSkeletonSignature))
                {
                    throw new InvalidDataException(
                        "A non-static custom model requires a decoded rig before embedded stacks can target it.");
                }

                variants.Add(new ProjectAnimationVariant
                {
                    Id = Guid.NewGuid(),
                    SourceId = source.Id,
                    Name = selection.DisplayName,
                    TargetModelId = modelEntry.Id,
                    TargetRigId = payload.RigId,
                    TargetRigSignature = payload.RuntimeRigSignature,
                    TargetAnimationSkeletonSignature =
                        payload.AnimationSkeletonSignature,
                    BindingMode =
                        ProjectAnimationBindingMode.ExactDirect,
                    RootMotionMode = selection.RootMotionMode,
                    RootBoneName = selection.RootBoneName,
                    IncludeInPackage = selection.Included,
                });
            }
            else if (variant.IncludeInPackage != selection.Included)
            {
                int index = variants.IndexOf(variant);
                variants[index] = variant with
                {
                    IncludeInPackage = selection.Included,
                };
            }
        }

        return project with
        {
            AnimationSources = sources.ToImmutable(),
            AnimationVariants = variants.ToImmutable(),
        };
    }

    private async Task<PreparedModelsWorkspaceRestore?>
        PrepareModelsWorkspaceRestoreAsync(
            DlraProject project,
            string? projectPath,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.ModelsWorkspace is not { } state)
        {
            return null;
        }

        ProjectAssetReference packageAsset = project.Assets
            .SingleOrDefault(asset => asset.Id == state.PackageAssetId) ??
            throw new InvalidDataException(
                "The saved Models workspace custom-model asset is missing.");
        if (packageAsset.Kind != ProjectAssetKind.CustomModelSource ||
            !TryParseCustomModelResourceId(
                packageAsset.ResourceId,
                out Guid expectedModelId))
        {
            throw new InvalidDataException(
                "The saved Models workspace custom-model identity is invalid.");
        }

        string packagePath = await ResolveProjectAssetPathAsync(
            packageAsset,
            "Models-workspace custom-model package",
            cancellationToken,
            projectPath ?? string.Empty);
        PreparedModelsWorkspaceRestore prepared =
            await Models.PrepareProjectRestoreAsync(
                packagePath,
                state,
                cancellationToken);
        if (prepared.Model.Package.Document.ModelId != expectedModelId)
        {
            throw new InvalidDataException(
                "The Models-workspace .dlrmodel identity differs from its saved project asset.");
        }

        return prepared;
    }

    private void CommitModelsWorkspaceRestore(
        PreparedModelsWorkspaceRestore? prepared)
    {
        if (prepared is null)
        {
            Models.ClearProjectSession();
            return;
        }

        Models.CommitProjectRestore(prepared);
    }

    private static ProjectModelEntry? FindCustomModelEntry(
        DlraProject project,
        Guid stableModelId)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (stableModelId == Guid.Empty)
        {
            throw new ArgumentException(
                "A custom-model identity cannot be empty.",
                nameof(stableModelId));
        }

        Dictionary<Guid, ProjectAssetReference> assets = project.Assets
            .ToDictionary(static asset => asset.Id);
        ProjectModelEntry[] matches = project.Models
            .Where(model =>
                assets.TryGetValue(
                    model.AssetId,
                    out ProjectAssetReference? asset) &&
                asset.Kind == ProjectAssetKind.CustomModelSource &&
                TryParseCustomModelResourceId(
                    asset.ResourceId,
                    out Guid modelId) &&
                modelId == stableModelId)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException(
                $"The project model library contains more than one entry for custom-model identity '{stableModelId:D}'."),
        };
    }

    private static bool IsAssetReferencedAsImmutableAnimationInput(
        DlraProject project,
        Guid assetId)
    {
        if (project.Animations.Any(animation =>
            animation.SourceAssetId == assetId ||
            animation.MimicAssetId == assetId ||
            animation.FacialSourceAssetId == assetId ||
            animation.SourceBinding?.RetailSourceModelAssetId ==
                assetId ||
            animation.FacialAnimationSourceBinding?
                .RetailSourceModelAssetId == assetId ||
            animation.Attachments.Any(attachment =>
                attachment.AssetId == assetId)) ||
            project.AnimationSources.Any(source =>
                source.SourceAssetId == assetId ||
                source.MimicAssetId == assetId ||
                source.FacialSourceAssetId == assetId ||
                source.SourceBinding?.RetailSourceModelAssetId ==
                    assetId ||
                source.FacialAnimationSourceBinding?
                    .RetailSourceModelAssetId == assetId) ||
            project.AnimationVariants.Any(variant =>
                variant.Attachments.Any(attachment =>
                    attachment.AssetId == assetId)))
        {
            return true;
        }

        return false;
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

        await ImportAnimationPathAsync(
            selectedPath,
            confirmedAnm2SourceModel: null);
    }

    private async Task OpenCustomModelAnimationInAnimateAsync(
        CustomModelAnimationHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        if (!handoff.Model.AnimationClips.ContainsKey(handoff.Selection.Id))
        {
            throw new InvalidDataException(
                "The selected animation stack is not decoded in the current model package.");
        }

        await SynchronizeModelsWorkspaceProjectAsync(
            CancellationToken.None);

        string packageSha256 = Convert.ToHexString(SHA256.HashData(
                CustomModelPackageSerializer.Serialize(
                    handoff.Model.Package).AsSpan()))
            .ToLowerInvariant();
        ProjectAssetReference packageAsset = _project.Assets
            .Single(asset =>
                asset.Kind == ProjectAssetKind.CustomModelSource &&
                TryParseCustomModelResourceId(
                    asset.ResourceId,
                    out Guid modelId) &&
                modelId == handoff.Model.Package.Document.ModelId &&
                string.Equals(
                    asset.ContentSha256,
                    packageSha256,
                    StringComparison.OrdinalIgnoreCase));
        ProjectAnimationSource source = _project.AnimationSources
            .Single(candidate =>
                candidate.SourceAssetId == packageAsset.Id &&
                candidate.EmbeddedCustomModelStack is { } embedded &&
                embedded.ClipId == handoff.Selection.Id &&
                embedded.FbxObjectId == handoff.Selection.FbxObjectId &&
                string.Equals(
                    embedded.StackFingerprint,
                    handoff.Selection.SourceFingerprint,
                    StringComparison.OrdinalIgnoreCase));
        ProjectAnimationVariant variant = _project.AnimationVariants
            .Single(candidate =>
                candidate.SourceId == source.Id &&
                _project.Models.Any(model =>
                    model.Id == candidate.TargetModelId &&
                    model.AssetId == packageAsset.Id));

        await ActivateAnimationAsync(
            variant.Id,
            beginPlayback: true);
        SetWorkspace(
            EditorWorkspaceMode.Animate,
            preserveLegacyCutscene: false);
        StatusText =
            $"Playing {variant.Name} in Playback; its immutable source and direct target already existed in the project";
    }
    private static AnimationClip ApplyCustomModelClipSettings(
        AnimationClip source,
        CustomModelAnimationClip selection) =>
        new(
            selection.DisplayName,
            selection.FrameRate,
            source.FrameCount,
            source.TransformTracks,
            source.ScalarTracks,
            source.AuxiliaryTransformTracks);

    private static AnimationSourceRoles ResolveEmbeddedCustomModelRoles(
        CustomModelAnimationClip selection)
    {
        AnimationSourceRoles roles = AnimationSourceRoles.None;
        if (selection.HasSkeletalTracks)
        {
            roles |= AnimationSourceRoles.Body;
        }

        if (selection.HasMorphTracks)
        {
            roles |= AnimationSourceRoles.Facial;
        }

        return roles == AnimationSourceRoles.None
            ? AnimationSourceRoles.Auxiliary
            : roles;
    }

    private static ProjectMorphSourceValueUnit ResolveEmbeddedFacialSourceUnit(
        CustomModelAnimationClip selection) =>
        string.Equals(
            selection.FacialSourceValueUnit,
            "percent",
            StringComparison.Ordinal)
            ? ProjectMorphSourceValueUnit.Percent
            : ProjectMorphSourceValueUnit.Normalized;

    private static string CreateCustomModelResourceId(
        Guid modelId,
        string modelName) =>
        $"custom-model:{modelId:N}:{SanitizeProjectSourceName(modelName)}";

    private static string CreateCustomModelStackResourceId(
        Guid modelId,
        Guid clipId) =>
        $"custom-model:{modelId:N}:stack:{clipId:N}";

    private static bool TryParseCustomModelResourceId(
        string? resourceId,
        out Guid modelId)
    {
        modelId = Guid.Empty;
        string[] parts = resourceId?.Split(':') ?? [];
        return parts.Length == 3 &&
               string.Equals(
                   parts[0],
                   "custom-model",
                   StringComparison.Ordinal) &&
               Guid.TryParseExact(parts[1], "N", out modelId) &&
               !string.IsNullOrWhiteSpace(parts[2]);
    }

    private static bool TryParseCustomModelStackResourceId(
        string? resourceId,
        out Guid modelId,
        out Guid clipId)
    {
        modelId = Guid.Empty;
        clipId = Guid.Empty;
        string[] parts = resourceId?.Split(':') ?? [];
        return parts.Length == 4 &&
               string.Equals(parts[0], "custom-model", StringComparison.Ordinal) &&
               string.Equals(parts[2], "stack", StringComparison.Ordinal) &&
               Guid.TryParseExact(parts[1], "N", out modelId) &&
               Guid.TryParseExact(parts[3], "N", out clipId);
    }

    private static string SanitizeProjectSourceName(string value)
    {
        string trimmed = string.IsNullOrWhiteSpace(value)
            ? "custom-model"
            : value.Trim();
        HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
        char[] sanitized = trimmed
            .Select(character => invalid.Contains(character) ||
                                 character is '/' or '\\'
                ? '_'
                : character)
            .Take(80)
            .ToArray();
        string result = new(sanitized);
        return string.IsNullOrWhiteSpace(result)
            ? "custom-model"
            : result;
    }

    private async Task ImportAnimationPathAsync(
        string selectedPath,
        DecodedProjectModelSession? confirmedAnm2SourceModel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);

        long generation = Interlocked.Increment(
            ref _animationTransitionGeneration);
        AnimationRuntimeSnapshot previous =
            CaptureAnimationRuntimeSnapshot();
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Import {Path.GetFileName(selectedPath)}",
            "Decode",
            "Validating animation source");
        try
        {
            string extension = Path.GetExtension(selectedPath)
                .ToLowerInvariant();
            if (extension == ".fbx")
            {
                await ImportExternalFbxStacksAsync(
                    selectedPath,
                    generation,
                    job);
                return;
            }

            ImportedAnimationSession session;
            DecodedProjectModelSession? sourceModel = null;
            if (extension == ".anm2")
            {
                job.Stage = "Source-model preflight";
                job.Progress = 15.0;
                sourceModel = confirmedAnm2SourceModel ??
                    await ResolveSuggestedLocalAnm2SourceModelAsync(
                        job.CancellationToken);
                if (sourceModel?.Rig is not { } rig)
                {
                    BeginLocalAnm2SourceModelPicker(selectedPath);
                    job.Complete("Source model required");
                    return;
                }

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

                LocalAnm2ImportPreflight preflight =
                    CreateLocalAnm2ImportPreflight(
                        selectedPath,
                        sourceModel,
                        imported.Partition);
                LocalAnm2SourceBindingDecision decision =
                    _fileDialogs.ConfirmLocalAnm2SourceBinding(
                        preflight);
                if (decision ==
                    LocalAnm2SourceBindingDecision.ChooseAnother)
                {
                    BeginLocalAnm2SourceModelPicker(selectedPath);
                    job.Complete("Choose another source model");
                    return;
                }

                if (decision == LocalAnm2SourceBindingDecision.Cancel)
                {
                    job.Complete("Canceled; previous session retained");
                    StatusText =
                        "ANM2 import canceled; previous animation retained";
                    return;
                }

                if (preflight.IsBlocked ||
                    imported.Partition.RequiresReview)
                {
                    throw new InvalidDataException(
                        "ANM2 contains duplicated or bone/morph-colliding descriptors that require review before playback: " +
                        string.Join(", ", imported.Partition.AmbiguousDescriptors.Select(
                            static descriptor => $"0x{descriptor:X8}")) +
                        ".");
                }

                Guid sourceModelAssetId = sourceModel.ProjectAsset.Id;
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
                if (imported.Partition.UnresolvedDescriptors.Length > 0)
                {
                    AddDiagnostic(
                        "Warning",
                        "ANM2",
                        $"{imported.Partition.UnresolvedDescriptors.Length:N0} descriptors do not exist in the bound source rig",
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


            EnsureCurrentAnimationTransition(
                generation,
                job.CancellationToken);

            job.Stage = "Project source";
            job.Progress = 55.0;
            ImportedProjectSource projectSource =
                await ProjectSourceImporter.ImportAsync(
                    selectedPath,
                    ProjectPath!,
                    job.CancellationToken);
            ProjectAssetReference asset = new()
            {
                Kind = ProjectAssetKind.SourceAnimation,
                RelativePath = projectSource.ProjectRelativePath,
                ContentSha256 = projectSource.Sha256,
            };

            DecodedProjectModelSession? selectedPreviewTarget =
                session.SourceKindContract == AnimationSourceKind.LocalAnm2
                    ? sourceModel
                    : _isolatedBrowsePreviewModel is
                        {
                            Payload.Source.Rig: not null,
                        } previewModel
                        ? CreateProjectModelSession(previewModel)
                        : null;
            RigDefinition? initialTargetRig =
                selectedPreviewTarget?.Rig ?? _targetRig;
            ProjectAssetReference? initialTargetAsset =
                selectedPreviewTarget?.ProjectAsset ?? _targetProjectAsset;
            DirectRigCompatibilityResult? directCompatibility =
                initialTargetRig is null
                    ? null
                    : DirectRigBindingAnalyzer.Analyze(
                        session.Rig,
                        initialTargetRig,
                        session.Clip);
            RetargetMap? proposal = initialTargetRig is null ||
                directCompatibility!.IsDirect
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
            ImmutableArray<ProjectAssetReference> importedAssets =
                _project.Assets.Add(asset);
            if (initialTargetAsset is not null &&
                !importedAssets.Any(candidate =>
                    candidate.Id == initialTargetAsset.Id))
            {
                importedAssets = importedAssets.Add(
                    initialTargetAsset);
            }
            DlraProject preparedProject = _project with
            {
                Assets = importedAssets,
                Animations = _project.Animations.Add(animation),
                ActiveAnimationId = animation.Id,
            };
            TargetBindingStatus initialBindingStatus =
                initialTargetRig is null
                    ? TargetBindingStatus.Invalid
                    : ResolveTargetBindingStatus(
                        session.Rig,
                        initialTargetRig,
                        proposal,
                        directCompatibility?.Binding);
            var prepared = new PreparedAnimationTransition(
                generation,
                animation,
                session with
                {
                    SourcePath = projectSource.AbsolutePath,
                },
                session.SourceKindContract == AnimationSourceKind.LocalFbx
                    ? []
                    : sourceModel?.PreviewMeshes ?? [],
                sourceModel,
                selectedPreviewTarget?.RetailPayload is not { } retailPayload ||
                    selectedPreviewTarget.RetailAsset is not { } retailAsset
                    ? null
                    : new PreparedRetailTarget(
                        retailPayload,
                        retailAsset,
                        selectedPreviewTarget.ProjectAsset),
                CustomTarget:
                    selectedPreviewTarget is not null &&
                    selectedPreviewTarget.RetailPayload is null
                        ? new PreparedCustomTarget(
                            selectedPreviewTarget.Rig,
                            selectedPreviewTarget.PreviewMeshes,
                            selectedPreviewTarget.Skeleton,
                            selectedPreviewTarget.ProjectAsset,
                            selectedPreviewTarget.CustomPreviewSession ??
                                throw new InvalidDataException(
                                    "The custom target has no prepared DL1-output preview session."),
                            CustomModelPreviewMode.Dl1Output,
                            selectedPreviewTarget.UsesSourcePreviewFallback
                                ? CustomModelPreviewMode.SourceFbx
                                : CustomModelPreviewMode.Dl1Output,
                            selectedPreviewTarget.PreviewDiagnostics)
                        : null,
                Mimic: null,
                SynchronizedClip: session.Clip,
                Mapping: proposal,
                BindingStatus: initialBindingStatus,
                DirectBinding: directCompatibility?.Binding);
            EnsureCurrentAnimationTransition(
                generation,
                job.CancellationToken);
            CommitPreparedAnimationTransition(
                prepared,
                preparedProject,
                beginPlayback: false,
                persistProject: true);

            job.Progress = 100.0;
            job.Complete("Complete");
            ClearAnimationOperationFailure();
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
            RestoreAnimationRuntimeSnapshot(previous);
            job.Complete("Canceled");
            StatusText =
                "Animation import canceled; previous animation retained";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException)
        {
            RestoreAnimationRuntimeSnapshot(previous);
            job.Complete("Failed");
            ReportAnimationOperationFailure(
                "Import",
                job.Stage,
                selectedPath,
                generation,
                exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportExternalFbxStacksAsync(
        string selectedPath,
        long generation,
        JobViewModel job)
    {
        job.Stage = "Scanning every FBX animation stack";
        job.Progress = 10.0;
        var initialOptions = new FbxExternalAnimationImportOptions
        {
            FacialSourceValueUnit =
                FbxFacialSourceValueUnit.Percent,
        };
        FbxExternalAnimationScanResult scan =
            await FbxExternalAnimationImportService.ScanFileAsync(
                selectedPath,
                initialOptions,
                cancellationToken: job.CancellationToken);
        foreach (FbxExternalAnimationStackDescriptor stack in scan.Stacks)
        foreach (FbxExternalAnimationDiagnostic diagnostic in
                 stack.Diagnostics)
        {
            AddDiagnostic(
                diagnostic.Severity.ToString(),
                "FBX stack browser",
                $"{stack.Name}: {diagnostic.Message}",
                $"{diagnostic.Code}; object {stack.StackObjectId}; roles {stack.Roles}; layers {stack.LayerNames.Length}");
        }

        if (scan.Stacks.IsEmpty)
        {
            throw new InvalidDataException(
                "The FBX contains no animation stacks.");
        }

        ExternalFbxAnimationStackSelection? selection =
            _fileDialogs.SelectExternalFbxAnimationStacks(
                Path.GetFileName(selectedPath),
                scan.Stacks);
        if (selection is null)
        {
            job.Complete("Canceled; previous session retained");
            StatusText =
                "FBX stack selection canceled; previous animation retained";
            return;
        }

        selection = selection.Validate();
        var options = initialOptions with
        {
            FacialSourceValueUnit =
                selection.FacialSourceValueUnit,
        };
        job.Stage = "Importing checked FBX stacks";
        job.Progress = 30.0;
        ImmutableArray<FbxExternalAnimationImportResult> imported =
            await FbxExternalAnimationImportService
                .ImportFileSelectedAsync(
                    selectedPath,
                    selection.StackObjectIds,
                    options,
                    cancellationToken: job.CancellationToken);
        if (imported.IsEmpty)
        {
            throw new InvalidDataException(
                "No checked FBX animation stack was imported.");
        }

        EnsureCurrentAnimationTransition(
            generation,
            job.CancellationToken);
        job.Stage = "Copying one portable FBX source";
        job.Progress = 55.0;
        ImportedProjectSource projectSource =
            await ProjectSourceImporter.ImportAsync(
                selectedPath,
                ProjectPath!,
                job.CancellationToken);
        ProjectAssetReference? existingAsset = _project.Assets
            .FirstOrDefault(candidate =>
                candidate.Kind == ProjectAssetKind.SourceAnimation &&
                string.Equals(
                    candidate.ContentSha256,
                    projectSource.Sha256,
                    StringComparison.OrdinalIgnoreCase));
        ProjectAssetReference asset = existingAsset ?? new ProjectAssetReference
        {
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = projectSource.ProjectRelativePath,
            ContentSha256 = projectSource.Sha256,
        };

        var sources = _project.AnimationSources.ToBuilder();
        int addedCount = 0;
        Guid? firstSourceId = null;
        foreach (FbxExternalAnimationImportResult take in imported)
        {
            Guid sourceId = CreateExternalFbxStackGroupId(
                projectSource.Sha256,
                take.Stack.StackObjectId,
                take.Stack.StackFingerprint);
            firstSourceId ??= sourceId;
            if (sources.Any(source => source.Id == sourceId))
            {
                continue;
            }

            string sourceRigSignature = take.SourceRig is null
                ? string.Empty
                : RigSignature.Compute(take.SourceRig);
            string? skeletonSignature = take.SourceRig is null
                ? null
                : AnimationSkeletonSignature.Compute(take.SourceRig);
            ProjectMorphSourceValueUnit? facialUnit =
                (take.Stack.Roles & AnimationSourceRoles.Facial) != 0
                    ? ToProjectMorphSourceValueUnit(
                        take.FacialSourceValueUnit)
                    : null;
            string timingDetail = CreateExternalFbxStackTimingDetail(
                take.Stack,
                take.FacialSourceValueUnit,
                usesSelectedModelRig: false);
            string originName = take.SourceRig?.Id ??
                take.Stack.SourceRigId ??
                $"{Path.GetFileNameWithoutExtension(selectedPath)} / {take.Stack.Name}";
            sources.Add(new ProjectAnimationSource
            {
                Id = sourceId,
                Name = take.Stack.Name,
                SourceAssetId = asset.Id,
                SourceBinding = new ProjectAnimationSourceBinding
                {
                    Kind = AnimationSourceKind.LocalFbx,
                    AssetId = asset.Id,
                    Roles = take.Stack.Roles,
                    SourceRigSignature = sourceRigSignature,
                    TimingProvenance =
                        AnimationTimingProvenance.EmbeddedFbx,
                    SourceRangeStartFrame = 0,
                    SourceRangeEndFrame = take.Clip.FrameCount - 1,
                    TimingDetail = timingDetail,
                },
                SourceAnimationSkeletonSignature = skeletonSignature,
                Presentation = new ProjectAnimationSourcePresentation
                {
                    OriginKind =
                        ProjectAnimationSourceOriginKind.ImportedFbxRig,
                    OriginName = originName,
                    ProjectAssetId = asset.Id,
                    SourceRigIdentity = take.SourceRig is null
                        ? "Skeleton-free facial source"
                        : take.SourceRig.Id,
                },
                FacialSourceValueUnit = facialUnit,
                FrameRate = take.Clip.FrameRate,
                FrameCount = take.Clip.FrameCount,
            });
            addedCount++;
        }

        DlraProject preparedProject = _project with
        {
            Assets = existingAsset is null
                ? _project.Assets.Add(asset)
                : _project.Assets,
            AnimationSources = sources.ToImmutable(),
            Workflow = _project.Workflow with
            {
                ActiveTab = ProjectWorkflowTab.Animations,
                SelectedAnimationSourceId = firstSourceId,
                SelectedAnimationVariantId = null,
            },
        };
        EnsureCurrentAnimationTransition(
            generation,
            job.CancellationToken);
        preparedProject.Validate();
        CommitProject(preparedProject);
        SetWorkspace(
            EditorWorkspaceMode.Animations,
            preserveLegacyCutscene: false);
        if (firstSourceId is { } selectedSourceId)
        {
            SelectedAnimationLibraryItem = AnimationLibrary
                .FirstOrDefault(item =>
                    item.VariantGroupId == selectedSourceId);
        }

        job.Progress = 100.0;
        job.Complete("Complete");
        ClearAnimationOperationFailure();
        StatusText =
            addedCount == 0
                ? $"The {imported.Length:N0} checked FBX stack(s) already exist in Animations"
                : $"Added {addedCount:N0} immutable FBX animation source(s); assign project models from Animations";
        AddDiagnostic(
            "Info",
            "Animation",
            $"Imported {addedCount:N0} source-only FBX stack(s)",
            $"The checked takes share one project source SHA-256 {projectSource.Sha256}; DeformPercent unit {selection.FacialSourceValueUnit}. No target variants or duplicated source samples were created.");
    }

    private ImmutableArray<ExternalFbxTargetModelOption>
        CreateExternalFbxTargetModelOptions()
    {
        Dictionary<Guid, ProjectAssetReference> assets = _project.Assets
            .ToDictionary(static asset => asset.Id);
        Guid? preferredModelId = _project.Workflow.SelectedModelId;
        if (preferredModelId is null &&
            _targetProjectAsset is { } activeTargetAsset)
        {
            preferredModelId = _project.Models.FirstOrDefault(model =>
                model.AssetId == activeTargetAsset.Id)?.Id;
        }

        ProjectModelEntry[] riggedModels = _project.Models
            .Where(static model =>
                !model.IsStatic &&
                !string.IsNullOrWhiteSpace(model.RigSignature))
            .ToArray();
        if (preferredModelId is null && riggedModels.Length == 1)
        {
            preferredModelId = riggedModels[0].Id;
        }

        return _project.Models
            .OrderByDescending(model => model.Id == preferredModelId)
            .ThenBy(static model => model.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(model =>
            {
                assets.TryGetValue(
                    model.AssetId,
                    out ProjectAssetReference? asset);
                string source = asset?.Kind switch
                {
                    ProjectAssetKind.RetailGameResource =>
                        "Base game reference",
                    ProjectAssetKind.CustomModelSource =>
                        "Project custom model",
                    _ => "Unavailable model asset",
                };
                string contract = model.IsStatic
                    ? "Static model - preview/export only"
                    : string.IsNullOrWhiteSpace(model.RigSignature)
                        ? "Rig identity pending validation"
                        : $"Rig {model.RigSignature[..Math.Min(12, model.RigSignature.Length)]}...";
                return new ExternalFbxTargetModelOption(
                    model.Id,
                    model.Name,
                    source,
                    contract,
                    model.IsStatic ||
                    string.IsNullOrWhiteSpace(model.RigSignature),
                    model.Id == preferredModelId &&
                    !model.IsStatic &&
                    !string.IsNullOrWhiteSpace(model.RigSignature));
            })
            .ToImmutableArray();
    }

    private async Task<ImmutableArray<PreparedExternalFbxTarget>>
        DecodeExternalFbxTargetsAsync(
            IReadOnlyList<Guid> targetModelIds,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetModelIds);
        if (targetModelIds.Count == 0 ||
            targetModelIds.Count >
                AnimationTargetSelection
                    .MaximumSelectedTargetModels ||
            targetModelIds.Distinct().Count() != targetModelIds.Count)
        {
            throw new ArgumentException(
                "The FBX target selection is empty, duplicated, or exceeds the supported target-model limit.",
                nameof(targetModelIds));
        }

        var prepared = ImmutableArray.CreateBuilder<
            PreparedExternalFbxTarget>(targetModelIds.Count);
        foreach (Guid modelId in targetModelIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectModelEntry model = _project.Models.FirstOrDefault(
                    candidate => candidate.Id == modelId)
                ?? throw new InvalidDataException(
                    "A checked FBX target model is no longer in the project model library.");
            if (model.IsStatic)
            {
                throw new InvalidDataException(
                    $"Static project model '{model.Name}' cannot be an animation target.");
            }

            if (string.IsNullOrWhiteSpace(model.RigSignature))
            {
                throw new InvalidDataException(
                    $"Project model '{model.Name}' has no persisted rig contract and cannot be an animation target.");
            }

            ProjectAssetReference asset = FindProjectAsset(model.AssetId)
                ?? throw new InvalidDataException(
                    $"Project model '{model.Name}' refers to a missing model asset.");
            RigDefinition rig;
            PreparedRetailTarget? retail = null;
            PreparedCustomTarget? custom = null;
            if (asset.Kind == ProjectAssetKind.CustomModelSource)
            {
                custom = await DecodeCustomModelTargetAsync(
                    asset,
                    cancellationToken);
                rig = custom.Rig;
                model = _project.Models.FirstOrDefault(candidate =>
                        candidate.Id == modelId)
                    ?? throw new InvalidDataException(
                        "The checked FBX target model disappeared during identity repair.");
            }
            else if (asset.Kind == ProjectAssetKind.RetailGameResource)
            {
                (Dl1MeshPreviewPayload payload,
                    RetailAssetRecord retailAsset) =
                    await DecodeProjectModelAsync(
                        asset,
                        cancellationToken);
                rig = payload.Source.Rig ??
                    throw new InvalidDataException(
                        $"Project model '{model.Name}' is static and cannot be an animation target.");
                retail = new PreparedRetailTarget(
                    payload,
                    retailAsset,
                    asset);
            }
            else
            {
                throw new InvalidDataException(
                    $"Project model '{model.Name}' does not reference a supported model asset.");
            }

            string decodedSignature = RigSignature.Compute(rig);
            if (!string.Equals(
                    model.RigSignature,
                    decodedSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Project model '{model.Name}' has a stale rig contract. Reimport or review the model before assigning animation variants.");
            }

            prepared.Add(new PreparedExternalFbxTarget(
                model,
                asset,
                rig,
                retail,
                custom));
        }

        return prepared.MoveToImmutable();
    }

    internal static ExternalFbxTargetVariantBatch
        CreateExternalFbxTargetVariants(
            FbxExternalAnimationImportResult take,
            ProjectAssetReference sourceAsset,
            string sourcePath,
            IReadOnlyList<ExternalFbxVariantTarget> targets,
            Guid? selectedSourceModelId = null)
    {
        ArgumentNullException.ThrowIfNull(take);
        ArgumentNullException.ThrowIfNull(sourceAsset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(targets);
        if (sourceAsset.Kind != ProjectAssetKind.SourceAnimation ||
            targets.Count == 0 ||
            targets.Count >
                AnimationTargetSelection
                    .MaximumSelectedTargetModels ||
            targets.Select(static target => target.Asset.Id)
                .Distinct()
                .Count() != targets.Count)
        {
            throw new ArgumentException(
                "External FBX variants require one portable source and a bounded, unique target-model list.",
                nameof(targets));
        }

        if (take.SourceRig is null &&
            (!selectedSourceModelId.HasValue ||
             selectedSourceModelId.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "A facial-only FBX source requires the stable project-model ID used as its source rig.",
                nameof(selectedSourceModelId));
        }

        RigDefinition sourceRig = take.SourceRig ?? targets[0].Rig;
        ImportedAnimationSession session =
            CreateExternalFbxAnimationSession(
                take,
                sourcePath,
                sourceRig,
                take.SourceRig is null
                    ? selectedSourceModelId
                    : null);
        string sourceFingerprint = sourceAsset.ContentSha256 ??
            throw new ArgumentException(
                "The portable external FBX source requires a content fingerprint.",
                nameof(sourceAsset));
        Guid sourceGroupId = CreateExternalFbxStackGroupId(
            sourceFingerprint,
            take.Stack.StackObjectId,
            take.Stack.StackFingerprint);
        var animations = ImmutableArray.CreateBuilder<ProjectAnimation>(
            targets.Count);
        var mappings = ImmutableArray.CreateBuilder<RetargetMap?>(
            targets.Count);
        foreach (ExternalFbxVariantTarget target in targets)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target.Asset.Kind is not (
                    ProjectAssetKind.RetailGameResource or
                    ProjectAssetKind.CustomModelSource))
            {
                throw new ArgumentException(
                    "An external FBX target must be a retail or custom-model asset.",
                    nameof(targets));
            }

            DirectRigCompatibilityResult compatibility =
                DirectRigBindingAnalyzer.Analyze(
                    sourceRig,
                    target.Rig,
                    session.Clip);
            RetargetMap? proposal = compatibility.IsDirect
                ? null
                : RetargetMapBuilder.CreateSuggested(
                    sourceRig,
                    target.Rig);
            ProjectAnimation animation = CreateProjectAnimation(
                    session,
                    sourceAsset,
                    target.Rig,
                    target.Asset.Id,
                    target.Asset.ContentSha256,
                    proposal) with
                {
                    VariantGroupId = sourceGroupId,
                };
            if (take.Facial is { HasFacialAnimation: true } facial &&
                !target.Rig.MorphChannels.IsEmpty)
            {
                Dl1MimicProfile profile =
                    FbxFacialProjectReviewService
                        .CreateTargetInventoryProfile(target.Rig);
                FbxFacialProjectReview facialReview =
                    FbxFacialProjectReviewService.Create(
                        new FbxFacialProjectReviewRequest
                        {
                            SourcePath = sourcePath,
                            Import = facial,
                            BodyTiming = new AnimationTiming(
                                take.Clip.FrameRate,
                                take.Clip.FrameCount),
                            Profile = profile,
                            ExactTargetRig = target.Rig,
                        });
                animation = facialReview.ApplyTo(animation);
            }

            animations.Add(animation);
            mappings.Add(proposal);
        }

        return new ExternalFbxTargetVariantBatch(
            sourceRig,
            animations.MoveToImmutable(),
            mappings.MoveToImmutable());
    }

    private static ImportedAnimationSession
        CreateExternalFbxAnimationSession(
            FbxExternalAnimationImportResult take,
            string sourcePath,
            RigDefinition sourceRig,
            Guid? selectedSourceModelId)
    {
        string timingDetail = CreateExternalFbxStackTimingDetail(
            take.Stack,
            take.FacialSourceValueUnit,
            take.SourceRig is null,
            selectedSourceModelId);
        return new ImportedAnimationSession(
            sourceRig,
            take.Clip,
            sourcePath,
            take.SourceRig is null
                ? "Facial FBX bound to selected model"
                : "FBX")
        {
            SourceKindContract = AnimationSourceKind.LocalFbx,
            TimingProvenance =
                AnimationTimingProvenance.EmbeddedFbx,
            SourceRangeStartFrame = 0,
            SourceRangeEndFrame = take.Clip.FrameCount - 1,
            TimingDetail = timingDetail,
            FacialClip = take.Facial?.Clip,
            DeclaredRoles = take.Stack.Roles,
            FacialSourceValueUnit =
                (take.Stack.Roles & AnimationSourceRoles.Facial) != 0
                    ? ToProjectMorphSourceValueUnit(
                        take.FacialSourceValueUnit)
                    : null,
        };
    }

    private static ProjectMorphSourceValueUnit
        ToProjectMorphSourceValueUnit(
            FbxFacialSourceValueUnit value) =>
        value switch
        {
            FbxFacialSourceValueUnit.Normalized =>
                ProjectMorphSourceValueUnit.Normalized,
            FbxFacialSourceValueUnit.Percent =>
                ProjectMorphSourceValueUnit.Percent,
            _ => throw new InvalidDataException(
                "FBX facial sources require an explicit Normalized or Percent DeformPercent unit."),
        };

    internal static string CreateExternalFbxStackTimingDetail(
        FbxExternalAnimationStackDescriptor stack,
        FbxFacialSourceValueUnit sourceValueUnit,
        bool usesSelectedModelRig,
        Guid? selectedSourceModelId = null)
    {
        ArgumentNullException.ThrowIfNull(stack);
        if (stack.StackObjectId <= 0 ||
            !IsSha256Value(stack.StackFingerprint) ||
            sourceValueUnit is not (
                FbxFacialSourceValueUnit.Normalized or
                FbxFacialSourceValueUnit.Percent) ||
            selectedSourceModelId == Guid.Empty ||
            (!usesSelectedModelRig && selectedSourceModelId is not null))
        {
            throw new ArgumentException(
                "External FBX stack provenance requires a stable object ID, fingerprint, and explicit facial unit.");
        }

        string unit = sourceValueUnit ==
            FbxFacialSourceValueUnit.Percent
                ? "percent"
                : "normalized";
        string[] common =
        [
            selectedSourceModelId is null
                ? "external-fbx-stack-v1"
                : "external-fbx-stack-v2",
            stack.StackObjectId.ToString(
                CultureInfo.InvariantCulture),
            stack.StackFingerprint.ToLowerInvariant(),
            unit,
            stack.SourceStartTick.ToString(
                CultureInfo.InvariantCulture),
            stack.SourceStopTick.ToString(
                CultureInfo.InvariantCulture),
            usesSelectedModelRig ? "selected-model-rig" : "embedded-rig",
        ];
        return selectedSourceModelId is { } modelId
            ? string.Join("|", common.Append(modelId.ToString("N")))
            : string.Join("|", common);
    }

    internal static bool TryParseExternalFbxStackTimingDetail(
        string? detail,
        out long stackObjectId,
        out string stackFingerprint,
        out FbxFacialSourceValueUnit sourceValueUnit,
        out bool usesSelectedModelRig) =>
        TryParseExternalFbxStackTimingDetail(
            detail,
            out stackObjectId,
            out stackFingerprint,
            out sourceValueUnit,
            out usesSelectedModelRig,
            out _);

    internal static bool TryParseExternalFbxStackTimingDetail(
        string? detail,
        out long stackObjectId,
        out string stackFingerprint,
        out FbxFacialSourceValueUnit sourceValueUnit,
        out bool usesSelectedModelRig,
        out Guid? selectedSourceModelId)
    {
        stackObjectId = 0;
        stackFingerprint = string.Empty;
        sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
        usesSelectedModelRig = false;
        selectedSourceModelId = null;
        string[] parts = detail?.Split('|') ?? [];
        bool versionOne = parts.Length == 7 &&
            string.Equals(
                parts[0],
                "external-fbx-stack-v1",
                StringComparison.Ordinal);
        bool versionTwo = parts.Length == 8 &&
            string.Equals(
                parts[0],
                "external-fbx-stack-v2",
                StringComparison.Ordinal);
        if ((!versionOne && !versionTwo) ||
            !long.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out stackObjectId) ||
            stackObjectId <= 0 ||
            !IsSha256Value(parts[2]) ||
            !long.TryParse(
                parts[4],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _) ||
            !long.TryParse(
                parts[5],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            return false;
        }

        sourceValueUnit = parts[3] switch
        {
            "percent" => FbxFacialSourceValueUnit.Percent,
            "normalized" => FbxFacialSourceValueUnit.Normalized,
            _ => FbxFacialSourceValueUnit.Unspecified,
        };
        if (sourceValueUnit ==
                FbxFacialSourceValueUnit.Unspecified ||
            parts[6] is not ("embedded-rig" or
                "selected-model-rig"))
        {
            sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
            return false;
        }

        stackFingerprint = parts[2].ToLowerInvariant();
        usesSelectedModelRig = parts[6] == "selected-model-rig";
        if (versionTwo &&
            (!usesSelectedModelRig ||
             !Guid.TryParseExact(
                 parts[7],
                 "N",
                 out Guid modelId) ||
             modelId == Guid.Empty))
        {
            stackObjectId = 0;
            stackFingerprint = string.Empty;
            sourceValueUnit = FbxFacialSourceValueUnit.Unspecified;
            usesSelectedModelRig = false;
            return false;
        }

        selectedSourceModelId = versionTwo
            ? Guid.ParseExact(parts[7], "N")
            : null;
        return true;
    }

    private static FbxExternalAnimationImportResult
        AssertSingleExternalStack(
            ImmutableArray<FbxExternalAnimationImportResult> imported,
            long expectedObjectId,
            string expectedFingerprint)
    {
        if (imported.Length != 1 ||
            imported[0].Stack.StackObjectId != expectedObjectId ||
            !string.Equals(
                imported[0].Stack.StackFingerprint,
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The external FBX stack object identity or fingerprint changed since import.");
        }

        return imported[0];
    }

    private async Task<RigDefinition> DecodeFacialOnlySourceRigAsync(
        ProjectAnimation animation,
        Guid? selectedSourceModelId,
        CancellationToken cancellationToken)
    {
        ProjectModelEntry? sourceModel =
            selectedSourceModelId is { } modelId
                ? _project.Models.FirstOrDefault(model =>
                    model.Id == modelId)
                : null;
        if (selectedSourceModelId is not null && sourceModel is null)
        {
            throw new InvalidDataException(
                "The facial-only FBX source model is no longer in the project model library.");
        }

        ProjectAssetReference targetAsset =
            (sourceModel?.AssetId ?? animation.TargetAssetId) is
                { } targetAssetId
                ? FindProjectAsset(targetAssetId) ??
                  throw new InvalidDataException(
                      "The facial-only FBX source model asset is missing.")
                : throw new InvalidDataException(
                    "The facial-only FBX stack no longer has its explicitly selected source model.");
        if (targetAsset.Kind == ProjectAssetKind.CustomModelSource)
        {
            PreparedCustomTarget custom =
                await DecodeCustomModelTargetAsync(
                    targetAsset,
                    cancellationToken);
            ValidateFacialOnlySourceRigContract(
                sourceModel,
                custom.Rig);
            return custom.Rig;
        }

        (Dl1MeshPreviewPayload payload, _) =
            await DecodeProjectModelAsync(
                targetAsset,
                cancellationToken);
        RigDefinition rig = payload.Source.Rig ??
            throw new InvalidDataException(
                "The facial-only FBX source model has no skeleton.");
        ValidateFacialOnlySourceRigContract(sourceModel, rig);
        return rig;
    }

    private static void ValidateFacialOnlySourceRigContract(
        ProjectModelEntry? sourceModel,
        RigDefinition rig)
    {
        if (sourceModel?.RigSignature is { } signature &&
            !string.Equals(
                signature,
                RigSignature.Compute(rig),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The facial-only FBX source model has a stale rig contract.");
        }
    }

    private static Guid CreateExternalFbxStackGroupId(
        string sourceFingerprint,
        long stackObjectId,
        string stackFingerprint)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(
                "|",
                "dlra-external-fbx-source-v1",
                sourceFingerprint.ToLowerInvariant(),
                stackObjectId.ToString(
                    CultureInfo.InvariantCulture),
                stackFingerprint.ToLowerInvariant())));
        Span<byte> guidBytes = digest.AsSpan(0, 16);
        guidBytes[7] = (byte)((guidBytes[7] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);
    }

    private bool CanPlaySelectedExplorerAnimation() =>
        !IsBusy &&
        AssetBrowser.SelectedAsset is
        {
            Kind: AssetKind.Animation,
            RetailAsset: not null,
        };

    private async Task<DecodedProjectModelSession?>
        ResolveSuggestedLocalAnm2SourceModelAsync(
            CancellationToken cancellationToken)
    {
        if (_targetProjectAsset is { } targetAsset)
        {
            if (_sourceModelContext is { } sourceModel &&
                ProjectModelAssetsMatch(
                    sourceModel.ProjectAsset,
                    targetAsset))
            {
                return sourceModel;
            }

            if (_isolatedBrowsePreviewModel is { } browseModel &&
                ProjectRetailAssetsMatch(
                    browseModel.ProjectAsset,
                    targetAsset))
            {
                return CreateProjectModelSession(browseModel);
            }

            if (targetAsset.Kind == ProjectAssetKind.CustomModelSource)
            {
                PreparedCustomTarget custom =
                    await DecodeCustomModelTargetAsync(
                        targetAsset,
                        cancellationToken);
                return CreateProjectModelSession(custom);
            }

            (Dl1MeshPreviewPayload payload, RetailAssetRecord retail) =
                await DecodeProjectModelAsync(
                    targetAsset,
                    cancellationToken);
            return CreateProjectModelSession(new DecodedRetailModelSession(
                payload,
                retail,
                targetAsset,
                CreatePreviewMeshes(payload)));
        }

        return _sourceModelContext ??
            (_isolatedBrowsePreviewModel is { } preview
                ? CreateProjectModelSession(preview)
                : null);
    }

    private static LocalAnm2ImportPreflight
        CreateLocalAnm2ImportPreflight(
            string sourcePath,
            DecodedProjectModelSession sourceModel,
            Anm2TrackPartition partition)
    {
        ProjectRetailAssetIdentity? identity =
            sourceModel.ProjectAsset.RetailIdentity;
        string exactIdentity = identity is null
            ? sourceModel.ProjectAsset.RelativePath
            : $"{identity.ProviderPack} | type {identity.ResourceType} | index {identity.ResourceIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"} | precedence {identity.Precedence}";
        string fingerprint = sourceModel.ProjectAsset.ContentSha256 ??
            sourceModel.RetailPayload?.ResourceSha256 ??
            throw new InvalidDataException(
                "The proposed source model has no content fingerprint.");
        string displayName = identity?.ResourceName ??
            sourceModel.RetailAsset?.DisplayName ??
            Path.GetFileNameWithoutExtension(
                sourceModel.ProjectAsset.RelativePath);
        return new LocalAnm2ImportPreflight(
            Path.GetFileName(sourcePath),
            displayName,
            exactIdentity,
            fingerprint,
            partition.BodyDescriptors.Length,
            partition.MorphDescriptors.Length,
            partition.AuxiliaryDescriptors.Length,
            partition.UnresolvedDescriptors.Length,
            partition.AmbiguousDescriptors.Length);
    }

    private static DecodedProjectModelSession CreateProjectModelSession(
        DecodedRetailModelSession model) =>
        new(
            model.Payload.Source.Rig ??
                throw new InvalidDataException(
                    "The fingerprinted retail model has no decoded skeleton."),
            model.ProjectAsset,
            model.PreviewMeshes,
            model.Payload.Skeleton ??
                throw new InvalidDataException(
                    "The fingerprinted retail model has no renderable skeleton."),
            model.Payload,
            model.RetailAsset);

    private static DecodedProjectModelSession CreateProjectModelSession(
        PreparedCustomTarget model) =>
        new(
            model.Rig,
            model.ProjectAsset,
            model.Meshes,
            model.Skeleton,
            UsesSourcePreviewFallback: model.UsesSourceFallback,
            PreviewDiagnostics: model.PreviewDiagnostics,
            CustomPreviewSession: model.PreviewSession);

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

        if (_sourceModelContext?.Rig is not
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
                    ProjectModelAssetsMatch(
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
            _activeDirectRigBinding = null;
            _activeAnimationId = animation.Id;
            CommitProject(_project with
            {
                Assets = assets,
                Animations = animations,
                ActiveAnimationId = animation.Id,
            });
            PublishProjectModelAsDirectTarget(
                _sourceModelContext,
                animation);
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
        UseSelectedProjectModelAsSourceCommand.NotifyCanExecuteChanged();
        AssetBrowser.SearchText = string.Empty;
        AssetBrowser.SelectedKindFilter = nameof(AssetKind.Mesh);
        AddDiagnostic(
            "Info",
            "Animation explorer",
            $"Choose the exact source model for {animation.Name}",
            "Use a retail mesh's Use as Source action, or explicitly bind a rigged project model from the animation-source prompt. Preview selection remains isolated; the pending animation is only bound after that explicit action.");
        StatusText = $"Choose the source model for {animation.Name}";
    }

    private void BeginLocalAnm2SourceModelPicker(string sourcePath)
    {
        _pendingLocalAnm2ImportPath = Path.GetFullPath(sourcePath);
        OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
        OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
        CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();
        UseSelectedProjectModelAsSourceCommand.NotifyCanExecuteChanged();
        AssetBrowser.SearchText = string.Empty;
        AssetBrowser.SelectedKindFilter = nameof(AssetKind.Mesh);
        AddDiagnostic(
            "Info",
            "ANM2 source binding",
            $"Choose the exact fingerprinted source model for {Path.GetFileName(sourcePath)}",
            "No animation, target, timeline, recovery snapshot, or viewport state has changed. Use a retail mesh or a rigged project-model row explicitly; descriptor coverage is shown before confirmation.");
        StatusText =
            $"Choose the source model for {Path.GetFileName(sourcePath)}";
    }

    private void CancelExplorerSourceModelPicker()
    {
        _pendingExplorerAnimationSourceChoice = null;
        _pendingLocalAnm2ImportPath = null;
        OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
        OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
        CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();
        UseSelectedProjectModelAsSourceCommand.NotifyCanExecuteChanged();
        StatusText = "Source-model selection canceled";
    }

    private bool CanUseSelectedAnimationLibraryItem() =>
        !IsBusy &&
        SelectedAnimationLibraryItem is
        {
            IsSourceOnly: false,
            IsRuntimeAvailable: true,
        };

    private bool CanActivateSelectedAnimation() =>
        CanUseSelectedAnimationLibraryItem() &&
        SelectedAnimationLibraryItem?.Id != _activeAnimationId;

    private bool CanAddAnimationTargets()
    {
        if (IsBusy ||
            SelectedAnimationLibraryItem is not { } selected ||
            !_project.AnimationSources.Any(source =>
                source.Id == selected.VariantGroupId))
        {
            return false;
        }

        // Keep the picker discoverable even when every model is already
        // assigned or unavailable. The dialog is also where those states are
        // explained, while removal remains an explicit library action.
        return !_project.Models.IsEmpty;
    }

    private bool CanAssignSelectedAnimationLibrary() =>
        !IsBusy &&
        SelectedAnimationLibraryItem is
        {
            IsSourceOnly: false,
            IsRuntimeAvailable: true,
        } selected &&
        _project.AnimationVariants.Any(variant =>
            variant.Id == selected.Id);

    private void AssignSelectedAnimationLibrary()
    {
        if (!CanAssignSelectedAnimationLibrary() ||
            SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        try
        {
            AnimationLibraryEditorRequest request =
                AnimationLibraryEditorRequest.FromProject(
                    _project,
                    selected.Id);
            AnimationLibraryAssignmentResult? result =
                _fileDialogs.EditAnimationLibraryAssignment(request);
            if (result is null)
            {
                return;
            }

            CommitProject(result.ApplyTo(_project, selected.Id));
            StatusText =
                $"Assigned {selected.Name} to its animation SCR";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException)
        {
            StatusText = "Animation SCR assignment was not changed";
            AddDiagnostic(
                "Error",
                "Animations",
                "Animation SCR assignment failed",
                exception.Message);
        }
    }

    private async Task AddAnimationTargetsAsync()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        ProjectAnimationSource source = _project.AnimationSources
                .FirstOrDefault(candidate =>
                    candidate.Id == selected.VariantGroupId)
            ?? throw new InvalidOperationException(
                "The selected animation source is no longer in the project library.");
        HashSet<Guid> assigned = _project.AnimationVariants
            .Where(variant => variant.SourceId == source.Id)
            .Select(static variant => variant.TargetModelId)
            .ToHashSet();
        Dictionary<Guid, ProjectAssetReference> assets = _project.Assets
            .ToDictionary(static asset => asset.Id);
        ImmutableArray<AnimationTargetModelOption> options = _project.Models
            .OrderBy(static model => model.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(model =>
            {
                assets.TryGetValue(
                    model.AssetId,
                    out ProjectAssetReference? asset);
                return new AnimationTargetModelOption(
                    model.Id,
                    model.Name,
                    asset?.Kind == ProjectAssetKind.RetailGameResource
                        ? "Base game reference"
                        : asset?.Kind == ProjectAssetKind.CustomModelSource
                            ? "Project custom model"
                            : "Unavailable model asset",
                    model.IsStatic
                        ? "Static model"
                        : string.IsNullOrWhiteSpace(model.RigSignature)
                            ? "Rig identity unavailable"
                            : $"Rig {model.RigSignature[..Math.Min(12, model.RigSignature.Length)]}...",
                    model.IsStatic
                        ? "Static model - unavailable"
                        : string.IsNullOrWhiteSpace(model.RigSignature)
                            ? "Rig identity unavailable"
                            : null,
                    assigned.Contains(model.Id));
            })
            .ToImmutableArray();
        AnimationTargetSelection? selection =
            _fileDialogs.SelectAnimationTargets(source.Name, options);
        if (selection is null)
        {
            return;
        }

        selection.Validate();
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Add targets for {source.Name}",
            "Animation targets",
            "Decoding immutable source and checked project models");
        try
        {
            ImportedAnimationSession runtimeSource =
                await DecodeAnimationSourceForTargetAssignmentAsync(
                    source,
                    job.CancellationToken);
            ImmutableArray<PreparedExternalFbxTarget> targets =
                await DecodeExternalFbxTargetsAsync(
                    selection.TargetModelIds,
                    job.CancellationToken);
            var classifications = new List<string>();
            // Source or target decoding may normalize a known schema-2
            // custom-model signature state. Build from the current project
            // only after those repairs so this transaction cannot overwrite
            // them with the pre-decode snapshot.
            var variants = _project.AnimationVariants.ToBuilder();
            ProjectAnimationVariant? template = variants.FirstOrDefault(
                variant => variant.SourceId == source.Id);
            foreach (PreparedExternalFbxTarget target in targets)
            {
                if (variants.Any(variant =>
                        variant.SourceId == source.Id &&
                        variant.TargetModelId == target.Model.Id))
                {
                    continue;
                }

                DirectRigCompatibilityResult compatibility =
                    DirectRigBindingAnalyzer.Analyze(
                        runtimeSource.Rig,
                        target.Rig,
                        runtimeSource.Clip);
                DirectRigBinding? directBinding = compatibility.Binding;
                RetargetMap? mapping = compatibility.IsDirect
                    ? null
                    : RetargetMapBuilder.CreateSuggested(
                        runtimeSource.Rig,
                        target.Rig);
                string sourceSignature =
                    RigSignature.Compute(runtimeSource.Rig);
                string targetSignature =
                    RigSignature.Compute(target.Rig);
                ProjectAnimationBindingMode mode = compatibility.Kind switch
                {
                    DirectRigCompatibilityKind.ExactDirect =>
                        ProjectAnimationBindingMode.ExactDirect,
                    DirectRigCompatibilityKind.CompatibleDirect =>
                        ProjectAnimationBindingMode.CompatibleDirect,
                    _ => ProjectAnimationBindingMode.Retarget,
                };
                variants.Add(new ProjectAnimationVariant
                {
                    Id = Guid.NewGuid(),
                    SourceId = source.Id,
                    Name = source.Name,
                    TargetModelId = target.Model.Id,
                    TargetRigId = target.Rig.Id,
                    TargetRigSignature = targetSignature,
                    TargetAnimationSkeletonSignature =
                        AnimationSkeletonSignature.Compute(target.Rig),
                    BindingMode = mode,
                    DirectBinding = directBinding,
                    BindingEvidenceFingerprint =
                        directBinding?.EvidenceFingerprint,
                    BindingPolicyVersion = directBinding?.Policy,
                    MappingFingerprint = mapping is null
                        ? null
                        : RetargetMapFingerprint.Compute(
                            sourceSignature,
                            targetSignature,
                            target.Asset.ContentSha256,
                            mapping),
                    RootMotionMode = template?.RootMotionMode ??
                        Dl1RootMotionMode.Recorded,
                    RootBoneName = template?.RootBoneName,
                    BoneMappings = mapping is null
                        ? []
                        : ToProjectMappings(
                            runtimeSource.Rig,
                            target.Rig,
                            mapping),
                    TargetBindReviews = mapping is null
                        ? []
                        : ToProjectTargetBindReviews(
                            target.Rig,
                            mapping),
                    IncludeInPackage = true,
                });
                classifications.Add(
                    $"{target.Model.Name}: {mode}");
            }

            ProjectAnimationSource refreshedSource = source with
            {
                SourceAnimationSkeletonSignature =
                    AnimationSkeletonSignature.Compute(runtimeSource.Rig),
                EmbeddedCustomModelStack =
                    source.EmbeddedCustomModelStack is { } embedded
                        ? embedded with
                        {
                            SourceRigSignature =
                                RigSignature.Compute(runtimeSource.Rig),
                            SourceAnimationSkeletonSignature =
                                AnimationSkeletonSignature.Compute(
                                    runtimeSource.Rig),
                        }
                        : source.EmbeddedCustomModelStack,
            };
            DlraProject updated = _project with
            {
                AnimationSources = _project.AnimationSources
                    .Select(candidate => candidate.Id == source.Id
                        ? refreshedSource
                        : candidate)
                    .ToImmutableArray(),
                AnimationVariants = variants.ToImmutable(),
            };
            updated.Validate();
            CommitProject(updated);
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Added {classifications.Count:N0} target variant(s) for {source.Name}";
            AddDiagnostic(
                "Information",
                "Animation targets",
                $"Added {classifications.Count:N0} explicit target variant(s)",
                classifications.Count == 0
                    ? "Every checked model was already assigned."
                    : string.Join(" | ", classifications));
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled");
            StatusText = "Adding animation targets was canceled";
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
                "Animation targets",
                "Could not add the checked target models",
                exception.Message);
            StatusText =
                "Animation targets were not changed because validation failed";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<ImportedAnimationSession>
        DecodeAnimationSourceForTargetAssignmentAsync(
            ProjectAnimationSource source,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ProjectAssetReference sourceAsset = FindProjectAsset(
                source.SourceAssetId)
            ?? throw new InvalidDataException(
                "The selected animation source asset is missing.");
        if (source.EmbeddedCustomModelStack is { } embedded)
        {
            string packagePath = await ResolveProjectAssetPathAsync(
                sourceAsset,
                "embedded custom-model package",
                cancellationToken);
            CustomModelPackage package = await Task.Run(
                () => CustomModelPackageSerializer.Load(packagePath),
                cancellationToken);
            if (!TryParseCustomModelResourceId(
                    sourceAsset.ResourceId,
                    out Guid expectedModelId) ||
                package.Document.ModelId != expectedModelId)
            {
                throw new InvalidDataException(
                    "The embedded .dlrmodel identity differs from its project asset.");
            }

            FbxModelAuthoringImportResult imported = await Task.Run(
                () => FbxModelAuthoringImporter.ImportPackage(
                    package,
                    cancellationToken),
                cancellationToken);
            RigDefinition rig = imported.Rig ??
                throw new InvalidDataException(
                    "A static custom model cannot be an animation source.");
            CustomModelProjectIdentityRepairResult repair =
                CustomModelProjectIdentityRepair.Repair(
                    _project,
                    sourceAsset,
                    imported);
            if (repair.WasRepaired)
            {
                CommitProject(repair.Project);
                source = repair.Project.AnimationSources.Single(candidate =>
                    candidate.Id == source.Id);
                embedded = source.EmbeddedCustomModelStack!;
            }

            CustomModelAnimationClip selection = package.Document
                    .AnimationClips
                .FirstOrDefault(candidate => candidate.Id == embedded.ClipId)
                ?? throw new InvalidDataException(
                    "The selected embedded animation stack is missing from its .dlrmodel.");
            if (selection.FbxObjectId != embedded.FbxObjectId ||
                !string.Equals(
                    selection.SourceFingerprint,
                    embedded.StackFingerprint,
                    StringComparison.OrdinalIgnoreCase) ||
                !imported.AnimationClips.TryGetValue(
                    embedded.ClipId,
                    out AnimationClip? decoded))
            {
                throw new InvalidDataException(
                    "The embedded animation stack object identity or fingerprint changed.");
            }

            AnimationClip clip = ApplyCustomModelClipSettings(
                decoded,
                selection with
                {
                    DisplayName = source.Name,
                    FrameRate = source.FrameRate,
                });
            if (clip.FrameCount != source.FrameCount)
            {
                throw new InvalidDataException(
                    "The embedded animation frame count differs from the project source.");
            }

            return new ImportedAnimationSession(
                rig,
                clip,
                packagePath,
                "Custom-model FBX")
            {
                SourceKindContract = AnimationSourceKind.LocalFbx,
                TimingProvenance = AnimationTimingProvenance.EmbeddedFbx,
                DeclaredRoles = embedded.Roles,
            };
        }

        ProjectAnimationSourceBinding binding = source.SourceBinding ??
            throw new InvalidDataException(
                "The selected animation source requires an explicit source rebind before targets can be added.");
        ProjectAnimation sourceDocument =
            CreateTargetAssignmentSourceDocument(source, binding);
        RigDefinition sourceRig;
        AnimationClip clipResult;
        AnimationClip? facialClip = null;
        string sourcePath;
        if (binding.Kind == AnimationSourceKind.LocalFbx)
        {
            sourcePath = await ResolveProjectAssetPathAsync(
                sourceAsset,
                "FBX animation source",
                cancellationToken);
            if (TryParseExternalFbxStackTimingDetail(
                    binding.TimingDetail,
                    out long stackObjectId,
                    out string stackFingerprint,
                    out FbxFacialSourceValueUnit valueUnit,
                    out bool usesSelectedModelRig,
                    out Guid? selectedSourceModelId))
            {
                ImmutableArray<FbxExternalAnimationImportResult> imported =
                    await FbxExternalAnimationImportService
                        .ImportFileSelectedAsync(
                            sourcePath,
                            [stackObjectId],
                            new FbxExternalAnimationImportOptions
                            {
                                FacialSourceValueUnit = valueUnit,
                            },
                            cancellationToken: cancellationToken);
                FbxExternalAnimationImportResult stack =
                    AssertSingleExternalStack(
                        imported,
                        stackObjectId,
                        stackFingerprint);
                sourceRig = stack.SourceRig ??
                    (usesSelectedModelRig
                        ? await DecodeFacialOnlySourceRigAsync(
                            sourceDocument,
                            selectedSourceModelId,
                            cancellationToken)
                        : throw new InvalidDataException(
                            "The external FBX source lost its embedded skeleton."));
                clipResult = stack.Clip;
                facialClip = stack.Facial?.Clip;
            }
            else
            {
                FbxCoreAnimationImportResult imported =
                    await new FbxAnimationDecoder().DecodeFileAsync(
                        sourcePath,
                        cancellationToken: cancellationToken);
                sourceRig = imported.Rig;
                clipResult = imported.Clip;
            }
        }
        else
        {
            ProjectAssetReference sourceModelAsset =
                binding.RetailSourceModelAssetId is { } sourceModelAssetId
                    ? FindProjectAsset(sourceModelAssetId) ??
                      throw new InvalidDataException(
                          "The immutable ANM2 source model is missing.")
                    : throw new InvalidDataException(
                        "The ANM2 source has no explicitly bound model skeleton.");
            if (sourceModelAsset.Kind ==
                ProjectAssetKind.CustomModelSource)
            {
                sourceRig = (await DecodeCustomModelTargetAsync(
                    sourceModelAsset,
                    cancellationToken)).Rig;

                // Decoding a custom model can atomically repair the project's
                // legacy authoring-contract/runtime-rig signatures. Do not
                // continue with the binding captured before that repair.
                source = ResolveCurrentTargetAssignmentSource(
                    _project,
                    source.Id);
                binding = source.SourceBinding ??
                    throw new InvalidDataException(
                        "The repaired animation source lost its source binding.");
                sourceDocument = CreateTargetAssignmentSourceDocument(
                    source,
                    binding);
            }
            else
            {
                (Dl1MeshPreviewPayload payload, _) =
                    await DecodeProjectModelAsync(
                        sourceModelAsset,
                        cancellationToken);
                sourceRig = payload.Source.Rig ??
                    throw new InvalidDataException(
                        "The bound ANM2 source model is static.");
            }

            (Anm2Clip raw, _, _, _, _) = await DecodeProjectAnm2Async(
                sourceAsset,
                sourceDocument,
                cancellationToken);
            Anm2PartitionedImportResult partitioned =
                Anm2TrackPartitioner.Partition(
                    raw,
                    sourceRig,
                    source.FrameRate,
                    cancellationToken);
            if (partitioned.Partition.RequiresReview ||
                binding.Partition is { } savedPartition &&
                !string.Equals(
                    savedPartition.Fingerprint,
                    partitioned.Partition.Fingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The ANM2 descriptor partition differs from its immutable source binding.");
            }

            clipResult = partitioned.CombinedClip;
            facialClip = partitioned.FacialClip;
            sourcePath = binding.Kind == AnimationSourceKind.RetailAnm2
                ? $"retail://{sourceAsset.ResourceId}"
                : await ResolveProjectAssetPathAsync(
                    sourceAsset,
                    "ANM2 animation source",
                    cancellationToken);
        }

        if (!string.Equals(
                RigSignature.Compute(sourceRig),
                binding.SourceRigSignature,
                StringComparison.OrdinalIgnoreCase) ||
            clipResult.FrameCount != source.FrameCount)
        {
            throw new InvalidDataException(
                "The decoded source rig or frame count differs from its immutable project contract.");
        }

        return new ImportedAnimationSession(
            sourceRig,
            clipResult,
            sourcePath,
            binding.Kind.ToString())
        {
            SourceKindContract = binding.Kind,
            RetailSourceModelAssetId =
                binding.RetailSourceModelAssetId,
            Partition = binding.Partition,
            TimingProvenance = binding.TimingProvenance,
            SourceRangeStartFrame = binding.SourceRangeStartFrame,
            SourceRangeEndFrame = binding.SourceRangeEndFrame,
            TimingDetail = binding.TimingDetail,
            FacialClip = facialClip,
            DeclaredRoles = binding.Roles,
        };
    }

    private static ProjectAnimation CreateTargetAssignmentSourceDocument(
        ProjectAnimationSource source,
        ProjectAnimationSourceBinding binding) =>
        new()
        {
            Name = source.Name,
            SourceAssetId = source.SourceAssetId,
            SourceBinding = binding,
            SourceRigSignature = binding.SourceRigSignature,
            SourceAnimationSkeletonSignature =
                source.SourceAnimationSkeletonSignature,
            FrameRate = source.FrameRate,
            FrameCount = source.FrameCount,
            TargetRigId = "dl1-retail:target-not-selected",
            FacialSourceValueUnit =
                source.FacialSourceValueUnit ??
                ProjectMorphSourceValueUnit.Percent,
        };

    internal static ProjectAnimationSource
        ResolveCurrentTargetAssignmentSource(
            DlraProject project,
            Guid sourceId)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.AnimationSources.Single(candidate =>
            candidate.Id == sourceId);
    }

    private void ScheduleSelectedAnimationSourcePreview()
    {
        if (ActiveWorkspace != EditorWorkspaceMode.Animations ||
            SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        ProjectAnimationSource? source = _project.AnimationSources
            .FirstOrDefault(candidate =>
                candidate.Id == selected.VariantGroupId);
        if (source is null)
        {
            return;
        }

        long generation = Interlocked.Increment(
            ref _animationSourcePreviewGeneration);
        _ = PreviewAnimationSourceAsync(
            source,
            selected.Id,
            generation);
    }

    private async Task PreviewAnimationSourceAsync(
        ProjectAnimationSource source,
        Guid selectedRowId,
        long generation)
    {
        try
        {
            if (source.SourceBinding is
                {
                    Kind: AnimationSourceKind.LocalFbx,
                    SourceRigSignature.Length: 0,
                })
            {
                if (IsCurrentAnimationSourcePreview(
                        selectedRowId,
                        generation))
                {
                    SetSourcePreviewScene([], null);
                    SourceViewport.SetPresentation(
                        "Facial-only source",
                        source.Presentation?.OriginName ??
                            "The imported FBX stack contains no skeletal hierarchy.");
                }
                return;
            }

            ImportedAnimationSession session =
                await DecodeAnimationSourceForTargetAssignmentAsync(
                    source,
                    CancellationToken.None);
            if (!IsCurrentAnimationSourcePreview(
                    selectedRowId,
                    generation))
            {
                return;
            }

            if (await TryPublishSelectedCustomAnimationPreviewAsync(
                    session,
                    selectedRowId,
                    generation))
            {
                return;
            }

            SkeletonPose pose = session.Clip.SamplePose(
                session.Rig,
                timeSeconds: 0,
                PlaybackMode.Clamp);
            SetSourcePreviewScene(
                [],
                CorePreviewAdapter.ToRenderSkeleton(pose));
            SourceViewport.SetPresentation(
                "Skeleton-only source",
                $"{source.Presentation?.OriginName ?? session.Rig.Id} | original imported hierarchy | {session.Rig.BoneCount:N0} nodes");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            if (!IsCurrentAnimationSourcePreview(
                    selectedRowId,
                    generation))
            {
                return;
            }

            SetSourcePreviewScene([], null);
            SourceViewport.SetPresentation(
                "Source preview unavailable",
                exception.Message);
        }
    }

    private async Task<bool> TryPublishSelectedCustomAnimationPreviewAsync(
        ImportedAnimationSession session,
        Guid selectedRowId,
        long generation)
    {
        ProjectAnimationVariant? variant = _project.AnimationVariants
            .FirstOrDefault(candidate => candidate.Id == selectedRowId);
        if (variant is null ||
            variant.BindingMode != ProjectAnimationBindingMode.ExactDirect)
        {
            return false;
        }

        ProjectModelEntry? targetModel = _project.Models.FirstOrDefault(
            candidate => candidate.Id == variant.TargetModelId);
        ProjectAssetReference? targetAsset = targetModel is null
            ? null
            : FindProjectAsset(targetModel.AssetId);
        if (targetAsset?.Kind != ProjectAssetKind.CustomModelSource)
        {
            return false;
        }

        PreparedCustomTarget target = await DecodeCustomModelTargetAsync(
            targetAsset,
            CancellationToken.None);
        if (!IsCurrentAnimationSourcePreview(
                selectedRowId,
                generation))
        {
            return true;
        }

        if (!string.Equals(
                RigSignature.Compute(session.Rig),
                RigSignature.Compute(target.Rig),
                StringComparison.Ordinal))
        {
            // A stale ExactDirect declaration is repaired by the normal
            // activation path. Do not force source-local matrices onto an
            // unrelated target merely to populate this compact browser.
            return false;
        }

        CustomModelPreviewPayload payload = target.PreviewSession
            .CreatePayload(
                session.Clip,
                frame: 0,
                SelectedBone?.Index);
        SourceViewport.SceneSource.SetExternalPreviewScene(null);
        SetSourcePreviewScene(
            payload.Meshes,
            payload.Skeleton);
        if (target.UsesSourceFallback)
        {
            SourceViewport.SetPresentation(
                "Animation preview / Source FBX fallback",
                target.PreviewDiagnostics.IsDefaultOrEmpty
                    ? "DL1-output preparation failed; the source presentation is shown explicitly."
                    : string.Join("; ", target.PreviewDiagnostics));
            return true;
        }

        SourceViewport.SetPresentation(
            "Animation preview / DL1 output",
            $"{targetModel!.Name} | target UVs, materials, embedded textures, and authored hierarchy");
        return true;
    }

    private bool IsCurrentAnimationSourcePreview(
        Guid selectedRowId,
        long generation) =>
        generation == Volatile.Read(
            ref _animationSourcePreviewGeneration) &&
        ActiveWorkspace == EditorWorkspaceMode.Animations &&
        SelectedAnimationLibraryItem?.Id == selectedRowId;

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

    private async Task OpenSelectedAnimationAsync()
    {
        if (SelectedAnimationLibraryItem is not { } selected)
        {
            return;
        }

        ProjectAnimationSource? source = _project.AnimationSources
            .FirstOrDefault(candidate =>
                candidate.Id == selected.VariantGroupId);
        if (source is null)
        {
            if (!selected.IsSourceOnly && selected.IsRuntimeAvailable)
            {
                await ActivateAnimationAsync(
                    selected.Id,
                    beginPlayback: false);
            }
            return;
        }

        ProjectAnimationVariant[] variants = _project.AnimationVariants
            .Where(variant => variant.SourceId == source.Id)
            .ToArray();
        if (selected.IsSourceOnly || variants.Length == 0)
        {
            await AddAnimationTargetsAsync();
            return;
        }

        ProjectAnimationVariant? selectedVariant = variants
            .FirstOrDefault(variant => variant.Id == selected.Id);
        if (selectedVariant is null)
        {
            await AddAnimationTargetsAsync();
            return;
        }

        ProjectModelEntry? targetModel = _project.Models.FirstOrDefault(
            model => model.Id == selectedVariant.TargetModelId);
        bool owningDirect = variants.Length == 1 &&
            selectedVariant.BindingMode ==
                ProjectAnimationBindingMode.ExactDirect &&
            targetModel is not null &&
            FindProjectAsset(targetModel.AssetId)?.Kind ==
                ProjectAssetKind.CustomModelSource &&
            (source.Presentation?.OwningModelId == targetModel.Id ||
             source.EmbeddedCustomModelStack is not null &&
             source.SourceAssetId == targetModel.AssetId);
        if (owningDirect)
        {
            OwningAnimationOpenDecision decision =
                _fileDialogs.ConfirmOwningAnimationOpen(
                    selected.Name,
                    targetModel!.Name);
            if (decision ==
                OwningAnimationOpenDecision.AddAnotherTarget)
            {
                await AddAnimationTargetsAsync();
                return;
            }

            if (decision == OwningAnimationOpenDecision.Cancel)
            {
                return;
            }
        }

        await ActivateAnimationAsync(
            selectedVariant.Id,
            beginPlayback: false);
    }

    private ProjectAnimation CreateRuntimeAnimation(
        ProjectAnimationSource source,
        ProjectAnimationVariant variant) =>
        CreateRuntimeAnimation(_project, source, variant);

    private static ProjectAnimation CreateRuntimeAnimation(
        DlraProject project,
        ProjectAnimationSource source,
        ProjectAnimationVariant variant)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(variant);
        ProjectEmbeddedAnimationStackIdentity embedded =
            source.EmbeddedCustomModelStack ??
            throw new InvalidDataException(
                "This schema-2 animation source has no runtime-compatible source binding.");
        ProjectModelEntry targetModel = project.Models.FirstOrDefault(model =>
                model.Id == variant.TargetModelId)
            ?? throw new InvalidDataException(
                "This schema-2 animation variant refers to a missing target model.");
        Guid targetAssetId = targetModel.AssetId;
        return new ProjectAnimation
        {
            Id = variant.Id,
            VariantGroupId = source.Id,
            Name = variant.Name,
            SourceAssetId = source.SourceAssetId,
            SourceBinding = new ProjectAnimationSourceBinding
            {
                Kind = AnimationSourceKind.LocalFbx,
                AssetId = source.SourceAssetId,
                Roles = embedded.Roles,
                SourceRigSignature =
                    embedded.SourceRigSignature,
                TimingProvenance =
                    AnimationTimingProvenance.EmbeddedFbx,
                SourceRangeStartFrame = 0,
                SourceRangeEndFrame = source.FrameCount - 1,
                TimingDetail =
                    $"Embedded custom-model stack {embedded.FbxObjectId}",
            },
            MimicAssetId = source.MimicAssetId,
            FacialAnimationSourceBinding =
                source.FacialAnimationSourceBinding,
            FacialSourceAssetId = source.FacialSourceAssetId,
            FacialSourceValueUnit = source.FacialSourceValueUnit ??
                (source.EmbeddedCustomModelStack is { } facialStack &&
                 (facialStack.Roles & AnimationSourceRoles.Facial) != 0
                    ? facialStack.FacialSourceValueUnit
                    : null),
            FacialTiming = source.FacialTiming,
            TargetAssetId = targetAssetId,
            TargetRigId = variant.TargetRigId,
            SourceRigSignature = source.SourceRigSignature,
            TargetRigSignature = variant.TargetRigSignature,
            SourceAnimationSkeletonSignature =
                source.SourceAnimationSkeletonSignature,
            TargetAnimationSkeletonSignature =
                variant.TargetAnimationSkeletonSignature,
            BindingMode = variant.BindingMode,
            DirectBinding = variant.DirectBinding,
            BindingEvidenceFingerprint =
                variant.BindingEvidenceFingerprint,
            BindingPolicyVersion = variant.BindingPolicyVersion,
            MappingFingerprint = variant.MappingFingerprint,
            MimicProfileId = variant.MimicProfileId,
            MimicMappingFingerprint =
                variant.MimicMappingFingerprint,
            FrameRate = source.FrameRate,
            FrameCount = source.FrameCount,
            RootMotionMode = variant.RootMotionMode,
            RootBoneName = variant.RootBoneName,
            PreviewMotionAccumulationEnabled =
                variant.PreviewMotionAccumulationEnabled,
            BoneMappings = variant.BoneMappings,
            TargetBindReviews = variant.TargetBindReviews,
            EditLayers = variant.EditLayers,
            MorphBindings = variant.MorphBindings,
            MorphEditLayers = variant.MorphEditLayers,
            IkLayers = variant.IkLayers,
            Attachments = variant.Attachments,
        };
    }

    private static DlraProject PersistResolvedAnimationBinding(
        DlraProject project,
        ProjectAnimation animation)
    {
        int variantIndex = -1;
        for (var index = 0;
             index < project.AnimationVariants.Length;
             index++)
        {
            if (project.AnimationVariants[index].Id == animation.Id)
            {
                variantIndex = index;
                break;
            }
        }

        if (variantIndex < 0)
        {
            return project with
            {
                Animations = project.Animations
                    .Select(candidate => candidate.Id == animation.Id
                        ? animation
                        : candidate)
                    .ToImmutableArray(),
            };
        }

        ProjectAnimationVariant existing =
            project.AnimationVariants[variantIndex];
        ProjectAnimationVariant updatedVariant = existing with
        {
            TargetRigId = animation.TargetRigId,
            TargetRigSignature = animation.TargetRigSignature,
            TargetAnimationSkeletonSignature =
                animation.TargetAnimationSkeletonSignature,
            BindingMode = animation.BindingMode,
            DirectBinding = animation.DirectBinding,
            BindingEvidenceFingerprint =
                animation.BindingEvidenceFingerprint,
            BindingPolicyVersion = animation.BindingPolicyVersion,
        };
        ImmutableArray<ProjectAnimationSource> sources = project
            .AnimationSources
            .Select(source => source.Id != existing.SourceId
                ? source
                : source with
                {
                    SourceAnimationSkeletonSignature =
                        animation.SourceAnimationSkeletonSignature,
                    EmbeddedCustomModelStack =
                        source.EmbeddedCustomModelStack is { } embedded &&
                        animation.SourceAnimationSkeletonSignature is
                            { } sourceSkeleton
                            ? embedded with
                            {
                                SourceAnimationSkeletonSignature =
                                    sourceSkeleton,
                            }
                            : source.EmbeddedCustomModelStack,
                })
            .ToImmutableArray();
        return project with
        {
            AnimationSources = sources,
            AnimationVariants = project.AnimationVariants.SetItem(
                variantIndex,
                updatedVariant),
            Animations = project.Animations
                .Select(candidate => candidate.Id == animation.Id
                    ? animation
                    : candidate)
                .ToImmutableArray(),
        };
    }

    private async Task ActivateAnimationAsync(
        Guid animationId,
        bool beginPlayback,
        bool persistActivation = true)
    {
        ProjectEmbeddedAnimationStackIdentity? embeddedStack = null;
        ProjectAnimation? animation = _project.Animations.FirstOrDefault(
            candidate => candidate.Id == animationId);
        if (animation is null)
        {
            ProjectAnimationVariant variant = _project.AnimationVariants
                    .FirstOrDefault(candidate =>
                        candidate.Id == animationId)
                ?? throw new ArgumentException(
                    "The requested animation is not in the project library.",
                    nameof(animationId));
            ProjectAnimationSource schemaSource = _project
                    .AnimationSources
                .First(source => source.Id == variant.SourceId);
            if (schemaSource.EmbeddedCustomModelStack is not null)
            {
                (schemaSource, variant) =
                    await RepairCustomModelIdentityAsync(
                        schemaSource,
                        variant,
                        CancellationToken.None);
            }
            embeddedStack = schemaSource.EmbeddedCustomModelStack;
            animation = CreateRuntimeAnimation(
                schemaSource,
                variant);
        }

        ProjectAnimationSourceBinding binding = animation.SourceBinding
            ?? throw new InvalidDataException(
                "This animation has no provable source model. Use Rebind Source to create a clean document.");
        ProjectAssetReference sourceAsset = FindProjectAsset(
                animation.SourceAssetId)
            ?? throw new InvalidDataException(
                "The active animation source asset is missing.");

        long generation = Interlocked.Increment(
            ref _animationTransitionGeneration);
        AnimationRuntimeSnapshot previous =
            CaptureAnimationRuntimeSnapshot();
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Activate {animation.Name}",
            "Animation library",
            "Resolving immutable source binding");
        try
        {
            Dl1MeshPreviewPayload? sourceModelPayload = null;
            RetailAssetRecord? sourceModelRetail = null;
            DecodedProjectModelSession? sourceModel = null;
            PreparedCustomModelSource? customModelSource = null;
            MeshRenderData[] sourceMeshes = [];
            ImportedAnimationSession session;
            if (binding.Kind == AnimationSourceKind.LocalFbx)
            {
                if (embeddedStack is not null ||
                    TryParseCustomModelStackResourceId(
                        sourceAsset.ResourceId,
                        out _,
                        out _))
                {
                    job.Stage = "Custom-model package";
                    customModelSource = await DecodeCustomModelSourceAsync(
                        sourceAsset,
                        animation,
                        binding,
                        embeddedStack,
                        job.CancellationToken);
                    session = new ImportedAnimationSession(
                        customModelSource.Imported.Rig!,
                        customModelSource.Clip,
                        customModelSource.PackagePath,
                        "Custom-model FBX")
                    {
                        SourceKindContract = AnimationSourceKind.LocalFbx,
                        TimingProvenance = binding.TimingProvenance,
                        TimingDetail = binding.TimingDetail,
                        DeclaredRoles = binding.Roles,
                    };
                    sourceMeshes = customModelSource.Preview.Meshes.ToArray();
                }
                else
                {
                job.Stage = "Source fingerprint";
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

                RigDefinition importedRig;
                AnimationClip importedClip;
                AnimationClip? importedFacialClip = null;
                if (TryParseExternalFbxStackTimingDetail(
                        binding.TimingDetail,
                        out long stackObjectId,
                        out string stackFingerprint,
                        out FbxFacialSourceValueUnit sourceValueUnit,
                        out bool usesSelectedModelRig,
                        out Guid? selectedSourceModelId))
                {
                    ImmutableArray<FbxExternalAnimationImportResult>
                        importedStacks =
                            await FbxExternalAnimationImportService
                                .ImportFileSelectedAsync(
                                    sourcePath,
                                    [stackObjectId],
                                    new FbxExternalAnimationImportOptions
                                    {
                                        FacialSourceValueUnit =
                                            sourceValueUnit,
                                    },
                                    cancellationToken:
                                        job.CancellationToken);
                    FbxExternalAnimationImportResult importedStack =
                        AssertSingleExternalStack(
                            importedStacks,
                            stackObjectId,
                            stackFingerprint);
                    importedRig = importedStack.SourceRig ??
                        (usesSelectedModelRig
                            ? await DecodeFacialOnlySourceRigAsync(
                                animation,
                                selectedSourceModelId,
                                job.CancellationToken)
                            : throw new InvalidDataException(
                                "The external FBX stack lost its embedded source skeleton."));
                    importedClip = importedStack.Clip;
                    importedFacialClip =
                        importedStack.Facial?.Clip;
                }
                else
                {
                    FbxCoreAnimationImportResult imported =
                        await new FbxAnimationDecoder().DecodeFileAsync(
                            sourcePath,
                            cancellationToken:
                                job.CancellationToken);
                    importedRig = imported.Rig;
                    importedClip = imported.Clip;
                }

                if (!string.Equals(
                        RigSignature.Compute(importedRig),
                        binding.SourceRigSignature,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "The local FBX rig differs from its immutable saved source signature.");
                }
                session = new ImportedAnimationSession(
                    importedRig,
                    importedClip,
                    sourcePath,
                    "FBX")
                {
                    SourceKindContract = AnimationSourceKind.LocalFbx,
                    TimingProvenance = binding.TimingProvenance,
                    TimingDetail = binding.TimingDetail,
                    FacialClip = importedFacialClip,
                    DeclaredRoles = binding.Roles,
                };
                }
            }
            else
            {
                job.Stage = "Immutable source model";
                ProjectAssetReference sourceModelAsset =
                    binding.RetailSourceModelAssetId is { } modelId
                        ? FindProjectAsset(modelId)
                            ?? throw new InvalidDataException(
                                "The immutable ANM2 source-model asset is missing.")
                        : throw new InvalidDataException(
                            "The ANM2 source binding does not identify an exact fingerprinted model.");
                if (sourceModelAsset.Kind ==
                    ProjectAssetKind.CustomModelSource)
                {
                    sourceModel = CreateProjectModelSession(
                        await DecodeCustomModelTargetAsync(
                            sourceModelAsset,
                            job.CancellationToken));
                }
                else if (sourceModelAsset.Kind ==
                         ProjectAssetKind.RetailGameResource)
                {
                    (sourceModelPayload, sourceModelRetail) =
                        await DecodeProjectModelAsync(
                            sourceModelAsset,
                            job.CancellationToken);
                    sourceModel = CreateProjectModelSession(
                        new DecodedRetailModelSession(
                            sourceModelPayload,
                            sourceModelRetail,
                            sourceModelAsset,
                            CreatePreviewMeshes(sourceModelPayload)));
                }
                else
                {
                    throw new InvalidDataException(
                        "The ANM2 source model has an unsupported project-asset kind.");
                }
                (Anm2Clip raw, AnimationTimingProvenance provenance,
                    double? start, double? end, string? detail) =
                    await DecodeProjectAnm2Async(
                        sourceAsset,
                        animation,
                        job.CancellationToken);
                Anm2PartitionedImportResult partitioned =
                    Anm2TrackPartitioner.Partition(
                        raw,
                        sourceModel.Rig,
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
                    sourceModel.Rig,
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
                sourceMeshes = sourceModel.PreviewMeshes;
            }

            EnsureCurrentAnimationTransition(
                generation,
                job.CancellationToken);
            job.Stage = "Target model";
            job.Progress = 65.0;
            PreparedRetailTarget? preparedTarget = null;
            PreparedCustomTarget? preparedCustomTarget = null;
            if (animation.TargetAssetId is { } targetAssetId)
            {
                ProjectAssetReference targetAsset = FindProjectAsset(
                        targetAssetId)
                    ?? throw new InvalidDataException(
                        "The animation target asset is missing.");
                if (targetAsset.Kind == ProjectAssetKind.CustomModelSource)
                {
                    if (sourceModel is not null &&
                        ProjectModelAssetsMatch(
                            sourceModel.ProjectAsset,
                            targetAsset))
                    {
                        preparedCustomTarget = new PreparedCustomTarget(
                            sourceModel.Rig,
                            sourceModel.PreviewMeshes,
                            sourceModel.Skeleton,
                            targetAsset,
                            sourceModel.CustomPreviewSession ??
                                throw new InvalidDataException(
                                    "The owning custom model has no prepared DL1-output preview session."),
                            CustomModelPreviewMode.Dl1Output,
                            sourceModel.UsesSourcePreviewFallback
                                ? CustomModelPreviewMode.SourceFbx
                                : CustomModelPreviewMode.Dl1Output,
                            sourceModel.PreviewDiagnostics);
                    }
                    else
                    {
                        preparedCustomTarget = await DecodeCustomModelTargetAsync(
                            targetAsset,
                            job.CancellationToken);
                    }
                }
                else if (sourceModelPayload is not null &&
                    ProjectRetailAssetsMatch(
                        FindProjectAsset(
                            binding.RetailSourceModelAssetId!.Value),
                        targetAsset))
                {
                    preparedTarget = new PreparedRetailTarget(
                        sourceModelPayload,
                        sourceModelRetail!,
                        targetAsset);
                }
                else
                {
                    (Dl1MeshPreviewPayload targetPayload,
                        RetailAssetRecord targetRetail) =
                        await DecodeProjectModelAsync(
                            targetAsset,
                            job.CancellationToken);
                    preparedTarget = new PreparedRetailTarget(
                        targetPayload,
                        targetRetail,
                        targetAsset);
                }
            }

            RigDefinition? targetRig =
                preparedCustomTarget?.Rig ??
                preparedTarget?.Payload.Source.Rig;
            ImportedMimicSession? mimic = null;
            AnimationClip synchronized = session.Clip;
            if (animation.MimicAssetId is { } mimicAssetId)
            {
                job.Stage = "Facial source";
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
                    if (targetRig is null)
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
                            targetRig,
                            session.Clip,
                            animation.FrameRate,
                            animation.FrameCount,
                            job.CancellationToken);
                    facialClip = loaded.Mimic;
                    timing = animation.FacialTiming ?? loaded.Timing;
                }

                mimic = new ImportedMimicSession(
                    mimicAssetId,
                    facialClip,
                    FormatProjectAssetLabel(
                        FindProjectAsset(mimicAssetId)));
                synchronized = AnimationClipSynchronization.Synchronize(
                    session.Clip,
                    facialClip,
                    timing);
            }

            job.Stage = "Mapping validation";
            DirectRigBinding? directBinding = null;
            ProjectAnimationBindingMode bindingMode =
                ProjectAnimationBindingMode.Retarget;
            RetargetMap? mapping = null;
            if (targetRig is not null)
            {
                DirectRigCompatibilityResult directAnalysis =
                    DirectRigBindingAnalyzer.Analyze(
                        session.Rig,
                        targetRig,
                        session.Clip);
                if (directAnalysis.IsDirect)
                {
                    bindingMode = directAnalysis.Kind ==
                        DirectRigCompatibilityKind.ExactDirect
                            ? ProjectAnimationBindingMode.ExactDirect
                            : ProjectAnimationBindingMode.CompatibleDirect;
                    directBinding = directAnalysis.Binding;
                }
                else
                {
                    bindingMode = ProjectAnimationBindingMode.Retarget;
                    mapping = animation.BoneMappings.IsEmpty
                        ? RetargetMapBuilder.CreateSuggested(
                            session.Rig,
                            targetRig)
                        : ToRetargetMap(
                            session.Rig,
                            targetRig,
                            animation.BoneMappings,
                            animation.TargetBindReviews);
                }

                string sourceRuntimeSignature =
                    RigSignature.Compute(session.Rig);
                string targetRuntimeSignature =
                    RigSignature.Compute(targetRig);
                bool generatedRetargetProposal =
                    bindingMode == ProjectAnimationBindingMode.Retarget &&
                    animation.BoneMappings.IsEmpty;
                string? mappingFingerprint =
                    bindingMode == ProjectAnimationBindingMode.Retarget &&
                    mapping is not null
                        ? RetargetMapFingerprint.Compute(
                            sourceRuntimeSignature,
                            targetRuntimeSignature,
                            FindProjectAsset(
                                animation.TargetAssetId!.Value)
                                ?.ContentSha256,
                            mapping)
                        : null;
                animation = animation with
                {
                    SourceRigSignature = sourceRuntimeSignature,
                    TargetRigId = targetRig.Id,
                    TargetRigSignature = targetRuntimeSignature,
                    SourceAnimationSkeletonSignature =
                        AnimationSkeletonSignature.Compute(session.Rig),
                    TargetAnimationSkeletonSignature =
                        AnimationSkeletonSignature.Compute(targetRig),
                    BindingMode = bindingMode,
                    DirectBinding = directBinding,
                    BindingEvidenceFingerprint =
                        directBinding?.EvidenceFingerprint,
                    BindingPolicyVersion = directBinding?.Policy,
                    MappingFingerprint = mappingFingerprint,
                    BoneMappings = generatedRetargetProposal
                        ? ToProjectMappings(
                            session.Rig,
                            targetRig,
                            mapping!)
                        : animation.BoneMappings,
                    TargetBindReviews = generatedRetargetProposal
                        ? ToProjectTargetBindReviews(
                            targetRig,
                            mapping!)
                        : animation.TargetBindReviews,
                };
            }
            TargetBindingStatus bindingStatus = targetRig is null
                ? TargetBindingStatus.Invalid
                : directBinding is not null ||
                  bindingMode == ProjectAnimationBindingMode.ExactDirect
                    ? TargetBindingStatus.Direct
                    : ResolveTargetBindingStatus(
                    session.Rig,
                    targetRig,
                    mapping);
            var prepared = new PreparedAnimationTransition(
                generation,
                animation,
                session,
                sourceMeshes,
                sourceModel,
                preparedTarget,
                preparedCustomTarget,
                mimic,
                synchronized,
                mapping,
                bindingStatus,
                directBinding);
            DlraProject preparedProject = PersistResolvedAnimationBinding(
                _project,
                animation) with
            {
                ActiveAnimationId = animation.Id,
            };
            EnsureCurrentAnimationTransition(
                generation,
                job.CancellationToken);
            job.Stage = "Atomic commit";
            CommitPreparedAnimationTransition(
                prepared,
                preparedProject,
                beginPlayback,
                persistActivation);
            job.Progress = 100.0;
            job.Complete("Complete");
            ClearAnimationOperationFailure();
            StatusText = $"Activated {animation.Name}";
        }
        catch (OperationCanceledException)
        {
            RestoreAnimationRuntimeSnapshot(previous);
            job.Complete("Canceled; previous session retained");
            StatusText =
                "Animation activation canceled; previous animation retained";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            OverflowException)
        {
            RestoreAnimationRuntimeSnapshot(previous);
            job.Complete("Failed");
            ReportAnimationOperationFailure(
                "Activation",
                job.Stage,
                sourceAsset.RelativePath,
                generation,
                exception);
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
        if (index < 0)
        {
            int variantIndex = -1;
            for (int candidateIndex = 0;
                 candidateIndex < _project.AnimationVariants.Length;
                 candidateIndex++)
            {
                if (_project.AnimationVariants[candidateIndex].Id ==
                    selected.Id)
                {
                    variantIndex = candidateIndex;
                    break;
                }
            }

            if (variantIndex < 0 || string.Equals(
                    _project.AnimationVariants[variantIndex].Name,
                    selected.Name,
                    StringComparison.Ordinal))
            {
                return;
            }

            CommitProject(_project with
            {
                AnimationVariants = _project.AnimationVariants.SetItem(
                    variantIndex,
                    _project.AnimationVariants[variantIndex] with
                    {
                        Name = selected.Name.Trim(),
                    }),
            });
            StatusText =
                $"Renamed animation to {selected.Name.Trim()}";
            return;
        }

        if (string.Equals(
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

        ProjectAnimation? source = GetRuntimeAnimation(selected.Id);
        if (source is null)
        {
            return;
        }

        ProjectAnimationVariant? schemaVariant = _project
            .AnimationVariants.FirstOrDefault(variant =>
                variant.Id == selected.Id);
        if (schemaVariant is not null &&
            _project.Animations.All(animation =>
                animation.Id != selected.Id))
        {
            ProjectAnimationVariant duplicateVariant = schemaVariant with
            {
                Id = Guid.NewGuid(),
                Name = source.Name + " Copy",
            };
            _activeAnimationId = duplicateVariant.Id;
            CommitProject(_project with
            {
                AnimationVariants = _project.AnimationVariants.Add(
                    duplicateVariant),
                ActiveAnimationId = duplicateVariant.Id,
                Workflow = _project.Workflow with
                {
                    SelectedModelId =
                        duplicateVariant.TargetModelId,
                    SelectedAnimationSourceId =
                        duplicateVariant.SourceId,
                    SelectedAnimationVariantId =
                        duplicateVariant.Id,
                },
            });
            StatusText = $"Duplicated {source.Name}";
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
            _activeDirectRigBinding = null;
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
        ImmutableArray<ProjectAnimationVariant> variants = _project
            .AnimationVariants
            .Where(variant => variant.Id != selected.Id)
            .ToImmutableArray();
        Guid? next = removedActive
            ? animations.FirstOrDefault()?.Id ??
              variants.FirstOrDefault()?.Id
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
            AnimationVariants = variants,
            ActiveAnimationId = next,
            Workflow = _project.Workflow with
            {
                SelectedAnimationSourceId = next is { } nextId
                    ? variants.FirstOrDefault(variant =>
                        variant.Id == nextId)?.SourceId
                    : null,
                SelectedAnimationVariantId = next,
            },
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

        ProjectAnimation? animation = GetRuntimeAnimation(selected.Id);
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

        ProjectAnimation facial = GetRuntimeAnimation(selected.Id) ??
            throw new InvalidOperationException(
                "The selected facial source is unavailable.");
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                body with
                {
                    MimicAssetId = facial.SourceAssetId,
                    FacialAnimationSourceBinding =
                        facialBinding with
                        {
                            Roles = AnimationSourceRoles.Facial,
                        },
                    FacialTiming = timing,
                },
                bodyIndex));
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
                "Mimic import needs an active body animation and its exact decoded target rig",
                null);
            return;
        }

        try
        {
            EnsureExactFacialTarget(
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
                "The selected target is not the animation's exact saved target",
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
            DlraProject updatedProject =
                WithUpdatedActiveAnimation(
                    _project with
                    {
                        Assets = _project.Assets.Add(asset),
                    },
                    updatedAnimation,
                    animationIndex);
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
                "Facial FBX review needs an explicit source unit, an active body timeline, and its exact decoded target",
                null);
            return;
        }

        try
        {
            EnsureExactFacialTarget(
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
                "The selected target is not the animation's exact saved target",
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
            DlraProject updatedProject =
                WithUpdatedActiveAnimation(
                    _project with
                    {
                        Assets = _project.Assets.Add(asset),
                    },
                    updatedAnimation,
                    animationIndex);
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
            EnsureExactFacialTarget(
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
            EnsureExactFacialTarget(
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
                "Facial mapping review requires the exact saved fingerprinted target",
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                updated,
                animationIndex));
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

    private bool CanExportAnimation()
    {
        if (IsBusy ||
            _sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            GetActiveAnimation() is null)
        {
            return false;
        }

        if (!TryValidateDl1DescriptorInventory(target, out _) ||
            source.Rig.Id.StartsWith("custom:", StringComparison.Ordinal) &&
            !TryValidateDl1DescriptorInventory(source.Rig, out _))
        {
            return false;
        }

        if (HasDirectRigContract(
                source.Rig,
                target,
                _activeDirectRigBinding))
        {
            return ActiveTargetBindingStatus == TargetBindingStatus.Direct;
        }

        return TryAnalyzeActiveMapping(
                out RetargetMappingReviewReport? review) &&
            review is { IsReady: true };
    }

    private static bool TryValidateDl1DescriptorInventory(
        RigDefinition rig,
        out string diagnostic)
    {
        try
        {
            Anm2EvaluationAdapter.ValidateTargetDescriptorInventory(rig);
            diagnostic = "The DL1 bone, helper, and morph descriptor namespace is unique.";
            return true;
        }
        catch (InvalidOperationException exception)
        {
            diagnostic = exception.Message;
            return false;
        }
    }

    private bool TryValidateProjectModelDl1DescriptorInventory(
        ProjectModelEntry model,
        ProjectAssetReference? asset)
    {
        if (asset?.Kind == ProjectAssetKind.RetailGameResource)
        {
            return true;
        }

        if (asset?.Kind != ProjectAssetKind.CustomModelSource)
        {
            return false;
        }

        try
        {
            string packagePath = ResolveLocalProjectAssetPath(asset);
            if (string.IsNullOrWhiteSpace(asset.ContentSha256))
            {
                return false;
            }

            using (FileStream stream = new(
                       packagePath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       128 * 1024,
                       FileOptions.SequentialScan))
            {
                string actualHash = Convert.ToHexString(
                        SHA256.HashData(stream))
                    .ToLowerInvariant();
                if (!string.Equals(
                        actualHash,
                        asset.ContentSha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            CustomModelPackage package = CustomModelPackageSerializer.Load(
                packagePath);
            if (package.Document.ModelId != model.Id)
            {
                return false;
            }

            FbxModelAuthoringImportResult imported =
                FbxModelAuthoringImporter.ImportPackage(package);
            Dl1PreparedAuthoredRig prepared =
                Dl1CustomModelRigPreparer.Prepare(imported);
            RigDefinition outputRig = prepared.PreviewRig;
            Anm2EvaluationAdapter.ValidateTargetDescriptorInventory(
                outputRig);
            if (model.Dl1OutputRigSignature is { } expectedRig &&
                !string.Equals(
                    expectedRig,
                    RigSignature.Compute(outputRig),
                    StringComparison.OrdinalIgnoreCase) ||
                model.Dl1DescriptorInventoryFingerprint is
                    { } expectedDescriptors &&
                !string.Equals(
                    expectedDescriptors,
                    prepared.Contract.DescriptorFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            // The package's DL1-output rig is a compiler/descriptor contract,
            // not the source FBX runtime-rig identity stored in RigSignature.
            // Comparing those two independent signatures made every valid
            // owning-model variant look stale and disabled its export row.
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            JsonException)
        {
            return false;
        }
    }

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
        ExportSelectedMeshToBlenderFbxCommand.NotifyCanExecuteChanged();
    }

    private static void EnsureExactFacialTarget(
        ProjectAnimation animation,
        RigDefinition targetRig,
        ProjectAssetReference targetAsset)
    {
        if (animation.TargetAssetId != targetAsset.Id ||
            targetAsset.Kind is not (
                ProjectAssetKind.RetailGameResource or
                ProjectAssetKind.CustomModelSource) ||
            targetAsset.Kind == ProjectAssetKind.RetailGameResource &&
            targetAsset.RetailIdentity is null ||
            targetAsset.Kind == ProjectAssetKind.CustomModelSource &&
            string.IsNullOrWhiteSpace(targetAsset.ResourceId) ||
            string.IsNullOrWhiteSpace(
                targetAsset.ContentSha256))
        {
            throw new InvalidOperationException(
                "The active animation is not bound to the selected fingerprinted retail or custom-model identity.");
        }

        string signature = RigSignature.Compute(targetRig);
        if (!string.Equals(
                signature,
                animation.TargetRigSignature,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The selected target rig signature differs from the saved animation target.");
        }
    }

    private async Task ExportAnimationAsync(
        Dl1AnimationExportParts parts)
    {
        if (_sourceAnimation is not { } source ||
            _targetRig is not { } target ||
            GetActiveAnimation() is not { } animation)
        {
            AddDiagnostic(
                "Warning",
                "Export",
                "Export needs a loaded source and decoded target",
                null);
            return;
        }

        bool directPlayback = HasDirectRigContract(
            source.Rig,
            target,
            _activeDirectRigBinding);
        RetargetMap? mapping = directPlayback
            ? null
            : _activeRetargetMap;
        if (!directPlayback && mapping is null)
        {
            AddDiagnostic(
                "Warning",
                "Export",
                "Cross-rig export needs a reviewed mapping",
                null);
            return;
        }

        if (!directPlayback)
        {
            RetargetMappingReviewReport review =
                RetargetMappingReview.Analyze(
                    source.Rig,
                    target,
                    mapping!);
            if (!review.IsReady)
            {
                PublishMappingReviewDiagnostics(review);
                MappingReviewStatus = FormatMappingReviewStatus(review);
                StatusText =
                    "Export blocked: review and save the retarget mapping";
                return;
            }
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
            job.Progress = 20.0;
            job.Stage = "Sampling authored frames";
            Dl1AnimationExportResult result =
                await ExportActiveAnimationPayloadAsync(
                    parts,
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

    private async Task<Dl1AnimationExportResult>
        ExportActiveAnimationPayloadAsync(
            Dl1AnimationExportParts parts,
            CancellationToken cancellationToken)
    {
        ImportedAnimationSession source = _sourceAnimation ??
            throw new InvalidOperationException(
                "The authoritative animation source is not loaded.");
        RigDefinition target = _targetRig ??
            throw new InvalidOperationException(
                "The exact target rig is not loaded.");
        ProjectAnimation animation = GetActiveAnimation() ??
            throw new InvalidOperationException(
                "No animation variant is active.");
        bool directPlayback = HasDirectRigContract(
            source.Rig,
            target,
            _activeDirectRigBinding);
        RetargetMap? mapping = directPlayback
            ? null
            : _activeRetargetMap ??
              throw new InvalidOperationException(
                  "Cross-rig export requires its reviewed map.");
        if (!directPlayback)
        {
            RetargetMappingReviewReport review =
                RetargetMappingReview.Analyze(
                    source.Rig,
                    target,
                    mapping!);
            if (!review.IsReady)
            {
                throw new InvalidOperationException(
                    "Cross-rig export is blocked until every required mapping row is reviewed.");
            }
        }

        string sourceSignature = RigSignature.Compute(source.Rig);
        string targetSignature = RigSignature.Compute(target);
        string targetFingerprint =
            _targetProjectAsset?.ContentSha256
            ?? throw new InvalidOperationException(
                "The selected target has no content fingerprint.");
        string? mappingFingerprint = directPlayback
            ? _activeDirectRigBinding?.EvidenceFingerprint
            : RetargetMapFingerprint.Compute(
                sourceSignature,
                targetSignature,
                targetFingerprint,
                mapping!);
        bool mappingIdentityMatches = directPlayback
            ? animation.MappingFingerprint is null &&
              (_activeDirectRigBinding is null ||
               string.Equals(
                   mappingFingerprint,
                   animation.BindingEvidenceFingerprint,
                   StringComparison.Ordinal))
            : string.Equals(
                mappingFingerprint,
                animation.MappingFingerprint,
                StringComparison.Ordinal);
        if (!string.Equals(
                sourceSignature,
                animation.SourceRigSignature,
                StringComparison.Ordinal) ||
            !string.Equals(
                targetSignature,
                animation.TargetRigSignature,
                StringComparison.Ordinal) ||
            !mappingIdentityMatches)
        {
            throw new InvalidOperationException(
                directPlayback
                    ? "The direct source/target rig identity no longer matches the saved project variant. Reload the exact target before export."
                    : "The active source, target, or mapping no longer matches the saved project identities. Review and save the mapping before export.");
        }

        EvaluationRequest evaluation = CreateEvaluationRequest(
            animation,
            0,
            PreviewProfile.RawAuthoring,
            PlaybackMode.Clamp,
            EvaluationPurpose.Export);
        var exporter = new Dl1AnimationExporter(
            new Anm2EvaluationAdapter(
                new AnimationEvaluator()));
        return await Task.Run(
            () => exporter.Export(
                new Dl1AnimationExportRequest
                {
                    Evaluation = evaluation,
                    Parts = parts,
                },
                cancellationToken),
            cancellationToken);
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                updated,
                animationIndex));
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
        CommitProject(WithUpdatedActiveAnimation(
            _project,
            animation with
            {
                MorphEditLayers = layers,
            },
            animationIndex));
        string? keyedMorphName = FacialFpp.Morphs
            .FirstOrDefault()?.Name;
        if (!string.IsNullOrWhiteSpace(keyedMorphName))
        {
            Timeline.SelectTrack(
                $"morph:{updatedLayer.Id:N}:{keyedMorphName}");
        }
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                animation with
                {
                    IkLayers = layers,
                },
                animationIndex));
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
            DlraProject updated = WithUpdatedActiveAnimation(
                _project,
                updatedAnimation,
                animationIndex);
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
        string sourceSkeletonSignature =
            AnimationSkeletonSignature.Compute(session.Rig);
        string? targetSignature = targetRig is null
            ? null
            : RigSignature.Compute(targetRig);
        string? targetSkeletonSignature = targetRig is null
            ? null
            : AnimationSkeletonSignature.Compute(targetRig);
        DirectRigCompatibilityResult? directCompatibility =
            targetRig is null
                ? null
                : DirectRigBindingAnalyzer.Analyze(
                    session.Rig,
                    targetRig,
                    session.Clip);
        RetargetMap? effectiveProposal = directCompatibility?.IsDirect == true
            ? null
            : targetRig is null
                ? null
                : proposal ?? RetargetMapBuilder.CreateSuggested(
                    session.Rig,
                    targetRig);
        DirectRigBinding? directBinding = directCompatibility?.Binding;
        ProjectAnimationBindingMode bindingMode = directCompatibility?.Kind switch
        {
            DirectRigCompatibilityKind.ExactDirect =>
                ProjectAnimationBindingMode.ExactDirect,
            DirectRigCompatibilityKind.CompatibleDirect =>
                ProjectAnimationBindingMode.CompatibleDirect,
            _ => ProjectAnimationBindingMode.Retarget,
        };
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
            SourceAnimationSkeletonSignature =
                sourceSkeletonSignature,
            TargetAnimationSkeletonSignature =
                targetSkeletonSignature,
            BindingMode = bindingMode,
            DirectBinding = directBinding,
            BindingEvidenceFingerprint =
                directBinding?.EvidenceFingerprint,
            BindingPolicyVersion = directBinding?.Policy,
            FacialSourceValueUnit =
                session.FacialSourceValueUnit,
            MappingFingerprint =
                effectiveProposal is null || targetSignature is null
                    ? null
                    : RetargetMapFingerprint.Compute(
                        sourceSignature,
                        targetSignature,
                        targetAssetFingerprint,
                        effectiveProposal),
            FrameRate = session.Clip.FrameRate,
            FrameCount = session.Clip.FrameCount,
            RootMotionMode = session.SourceKindContract ==
                    AnimationSourceKind.LocalFbx
                ? Dl1RootMotionMode.InPlace
                : Dl1RootMotionMode.Recorded,
            BoneMappings = effectiveProposal is null
                ? []
                : ToProjectMappings(
                    session.Rig,
                    targetRig!,
                    effectiveProposal),
            TargetBindReviews = effectiveProposal is null
                ? []
                : ToProjectTargetBindReviews(
                    targetRig!,
                    effectiveProposal),
        };
    }

    private static AnimationSourceRoles ResolveSourceRoles(
        ImportedAnimationSession session)
    {
        AnimationSourceRoles roles = session.DeclaredRoles ??
            session.Partition?.Roles ??
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

        CommitProject(WithUpdatedActiveAnimation(
            _project,
            animation with
            {
                RootMotionMode = mode,
            },
            animationIndex));
        AddDiagnostic(
            "Info",
            "Root motion",
            $"Root policy changed to {mode}",
            "Preview and export use the same authored root/helper policy.");
        RefreshAnimationPreview();
    }

    private void UpdateRootBoneName(string? rootBoneName)
    {
        if (rootBoneName is null ||
            _targetRig is not { } targetRig ||
            targetRig.GetBoneIndex(rootBoneName) < 0 ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex) ||
            string.Equals(
                animation.RootBoneName,
                rootBoneName,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CommitProject(WithUpdatedActiveAnimation(
            _project,
            animation with
            {
                RootBoneName = rootBoneName,
            },
            animationIndex));
        AddDiagnostic(
            "Info",
            "Root motion",
            $"Root track changed to {rootBoneName}",
            "This target variant now uses the selected skeletal root for both preview and export.");
        RefreshAnimationPreview();
    }

    private void RefreshRootBoneCandidates(ProjectAnimation? animation)
    {
        RootBoneCandidates.Clear();
        if (_targetRig is not { } targetRig)
        {
            SelectedRootBoneName = null;
            return;
        }

        IEnumerable<BoneDefinition> candidates = targetRig.Bones
            .Where(static bone =>
                bone.DescriptorHash !=
                    Dl1RootMotionPolicy.MotionAccumulatorDescriptor &&
                (bone.ParentIndex < 0 ||
                 bone.Kind == BoneKind.Root ||
                 string.Equals(
                     bone.SemanticRole,
                     "root.skeletal",
                     StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(
                     bone.Name,
                     "Bip01",
                     StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(static bone =>
                string.Equals(
                    bone.SemanticRole,
                    "root.skeletal",
                    StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(static bone =>
                string.Equals(
                    bone.Name,
                    "Bip01",
                    StringComparison.OrdinalIgnoreCase))
            .ThenBy(static bone => bone.Index);
        foreach (string name in candidates
                     .Select(static bone => bone.Name)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            RootBoneCandidates.Add(name);
        }

        string? selected = animation?.RootBoneName;
        if (selected is null || targetRig.GetBoneIndex(selected) < 0)
        {
            selected = RootBoneCandidates.FirstOrDefault();
        }

        SelectedRootBoneName = selected;
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
                "Auto-map needs a loaded source animation and target rig",
                null);
            return;
        }

        RetargetMap proposal = MergeAutoMapWithLockedRows(
            RetargetMapBuilder.CreateSuggested(
                source.Rig,
                target),
            _activeRetargetMap);
        _activeRetargetMap = proposal;
        _activeDirectRigBinding = null;
        ProjectAnimation updated = animation with
        {
            TargetRigId = target.Id,
            SourceRigSignature = RigSignature.Compute(source.Rig),
            TargetRigSignature = RigSignature.Compute(target),
            SourceAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(source.Rig),
            TargetAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(target),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            DirectBinding = null,
            BindingEvidenceFingerprint = null,
            BindingPolicyVersion = null,
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
        CommitProject(WithUpdatedActiveAnimation(
            _project,
            updated,
            animationIndex));
        PublishMappingProposal(proposal);
        RefreshAnimationPreview();
    }

    private bool CanApplyAssistedReview() =>
        IsAssistedReviewEnabled &&
        !IsBusy &&
        _targetRig is not null &&
        GetActiveAnimation() is { } animation &&
        ((_sourceAnimation is not null &&
          _activeRetargetMap is not null) ||
         !animation.MorphBindings.IsEmpty);

    private void ApplyAssistedReview()
    {
        if (!CanApplyAssistedReview() ||
            _targetRig is not { } target ||
            !TryGetActiveAnimation(
                out ProjectAnimation animation,
                out int animationIndex))
        {
            return;
        }

        RetargetMap? reviewed = null;
        ImmutableArray<ProjectBoneMapping> mappings =
            animation.BoneMappings;
        string? mappingFingerprint = animation.MappingFingerprint;
        if (_sourceAnimation is { } source &&
            _activeRetargetMap is { } proposal)
        {
            reviewed = RetargetSuggestionScorer.ApplyAssistedReview(
                source.Rig,
                target,
                proposal);
            mappings = ToProjectMappings(
                source.Rig,
                target,
                reviewed);
            mappingFingerprint = RetargetMapFingerprint.Compute(
                RigSignature.Compute(source.Rig),
                RigSignature.Compute(target),
                _targetProjectAsset?.ContentSha256,
                reviewed);
        }

        ImmutableArray<ProjectMorphBinding> morphBindings =
            animation.MorphBindings.IsEmpty
                ? animation.MorphBindings
                : ProjectMorphSuggestionScorer.ApplyAssistedReview(
                    animation.MorphBindings,
                    target);
        string? mimicMappingFingerprint =
            animation.MimicMappingFingerprint;
        if (!morphBindings.SequenceEqual(animation.MorphBindings) &&
            animation.MimicProfileId is { } profileId)
        {
            try
            {
                mimicMappingFingerprint =
                    FbxFacialProjectReviewService
                        .ComputeMappingFingerprint(
                            profileId,
                            target,
                            new AnimationTiming(
                                animation.FrameRate,
                                animation.FrameCount),
                            morphBindings);
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                InvalidDataException or
                InvalidOperationException or
                OverflowException)
            {
                AddDiagnostic(
                    "Error",
                    "Assisted review",
                    "Facial evidence could not be applied",
                    exception.Message);
                StatusText =
                    "Assisted review was not applied because the facial mapping fingerprint could not be refreshed";
                return;
            }
        }

        if (string.Equals(
                _facialFbxUnmappedFingerprint,
                animation.MimicMappingFingerprint,
                StringComparison.Ordinal))
        {
            _facialFbxUnmappedFingerprint =
                mimicMappingFingerprint;
        }

        ProjectAnimation updatedAnimation = animation with
        {
            BindingMode = ProjectAnimationBindingMode.Retarget,
            DirectBinding = null,
            BindingEvidenceFingerprint = null,
            BindingPolicyVersion = null,
            BoneMappings = mappings,
            MappingFingerprint = mappingFingerprint,
            MorphBindings = morphBindings,
            MimicMappingFingerprint = mimicMappingFingerprint,
        };
        ImmutableArray<ProjectAnimationVariant> variants =
            _project.AnimationVariants
                .Select(variant => variant.Id == animation.Id
                    ? variant with
                    {
                        BindingMode = ProjectAnimationBindingMode.Retarget,
                        DirectBinding = null,
                        BindingEvidenceFingerprint = null,
                        BindingPolicyVersion = null,
                        BoneMappings = mappings,
                        MappingFingerprint = mappingFingerprint,
                        MorphBindings = morphBindings,
                        MimicMappingFingerprint =
                            mimicMappingFingerprint,
                    }
                    : variant)
                .ToImmutableArray();
        if (reviewed is not null)
        {
            _activeRetargetMap = reviewed;
            _activeDirectRigBinding = null;
        }

        DlraProject assistedProject = _project with
        {
            AnimationVariants = variants,
        };
        CommitProject(WithUpdatedActiveAnimation(
            assistedProject,
            updatedAnimation,
            animationIndex));
        if (reviewed is not null)
        {
            PublishMappingProposal(reviewed);
        }

        RefreshAnimationPreview();
        int approvedBody = reviewed?.Entries.Count(static entry =>
            entry.ReviewOrigin == MappingReviewOrigin.Assisted) ?? 0;
        int approvedFacial = morphBindings.Count(static binding =>
            binding.ReviewOrigin ==
                ProjectMappingReviewOrigin.Assisted);
        int approved = approvedBody + approvedFacial;
        StatusText = approved == 0
            ? "Assisted review found no eligible deterministic rows; the project remains unchanged apart from refreshed scorer evidence"
            : $"Assisted review locked {approvedBody:N0} body/helper and {approvedFacial:N0} facial row(s); all remaining rows still require explicit review";
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
        _activeDirectRigBinding = null;
        string sourceSignature = RigSignature.Compute(source.Rig);
        string targetSignature = RigSignature.Compute(target);
        ProjectAnimation updated = animation with
        {
            SourceRigSignature = sourceSignature,
            TargetRigSignature = targetSignature,
            SourceAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(source.Rig),
            TargetAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(target),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            DirectBinding = null,
            BindingEvidenceFingerprint = null,
            BindingPolicyVersion = null,
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
        CommitProject(WithUpdatedActiveAnimation(
            _project,
            updated,
            animationIndex));
        RetargetMappingReviewReport report =
            RetargetMappingReview.Analyze(
                source.Rig,
                target,
                reviewed);
        PublishMappingReviewDiagnostics(report);
        MappingReviewStatus = FormatMappingReviewStatus(report);
        SetTargetBindingStatus(
            report.IsReady
                ? TargetBindingStatus.Ready
                : TargetBindingStatus.NeedsReview);
        RefreshAnimationPreview();
        StatusText = report.IsReady
            ? "Reviewed mapping saved in project"
            : "Mapping saved, but validation still reports blockers";
        NotifyMappingCommands();
    }

    private void AcceptMappingProposalAndPlay()
    {
        _batchReviewingMapping = true;
        try
        {
            foreach (BoneMappingViewModel row in BoneMappings.Where(
                         static row =>
                             !string.IsNullOrWhiteSpace(row.TargetBone)))
            {
                row.IsReviewed = true;
            }

            foreach (TargetBindReviewViewModel row in
                     RequiredTargetBindReviews)
            {
                row.IsReviewed = true;
            }
        }
        finally
        {
            _batchReviewingMapping = false;
        }

        SaveReviewedMapping();
        if (ActiveTargetBindingStatus != TargetBindingStatus.Ready)
        {
            return;
        }

        Timeline.CurrentFrame = 0;
        SetWorkspace(
            EditorWorkspaceMode.Animate,
            preserveLegacyCutscene: false);
        Timeline.IsPlaying = true;
        StatusText =
            "Mapping proposal explicitly accepted; target playback started";
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
                    row.IsLocked,
                    row.IsReviewed,
                    entry.MappingKind,
                    row.TransferPolicy,
                    row.ComponentPolicy,
                    entry.Evidence,
                    row.IsReviewed
                        ? MappingReviewOrigin.Explicit
                        : MappingReviewOrigin.None,
                    entry.ScorerVersion,
                    entry.EvidenceFingerprint,
                    row.TransformComponents));
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
        AcceptMappingProposalCommand.NotifyCanExecuteChanged();
        ApplyAssistedReviewCommand.NotifyCanExecuteChanged();
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
                            : existing.ComponentPolicy,
                        transformComponents:
                            upgradeLegacyRotation
                                ? entry.TransformComponents
                                : existing.TransformComponents);
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
        proposal.TransformComponents ==
            RetargetTransformComponents.Rotation &&
        existing.MappingKind == RetargetMappingKind.Bone &&
        (existing.TransferPolicy is
            RetargetTransferPolicy.RotationDelta or
            RetargetTransferPolicy.GlobalRotationDelta) &&
        existing.TransformComponents ==
            RetargetTransformComponents.Rotation &&
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
                Confidence = entry.Confidence,
                Evidence = FormatMappingEvidence(entry),
                ReviewOrigin = ToProjectReviewOrigin(
                    entry.ReviewOrigin),
                ScorerVersion = string.IsNullOrWhiteSpace(
                        entry.ScorerVersion)
                    ? "unscored-v1"
                    : entry.ScorerVersion,
                EvidenceFingerprint = string.IsNullOrWhiteSpace(
                        entry.EvidenceFingerprint)
                    ? new string('0', 64)
                    : entry.EvidenceFingerprint,
                IsLocked = entry.IsLocked,
                IsReviewed = entry.IsReviewed,
                MappingKind = entry.MappingKind,
                TransferPolicy = entry.TransferPolicy,
                ComponentPolicy = entry.ComponentPolicy,
                TransformComponents = entry.TransformComponents,
            })
            .ToImmutableArray();

    private static string FormatMappingEvidence(BoneMapEntry entry)
    {
        string evidence = entry.Evidence.IsEmpty
            ? "No mapping evidence recorded."
            : string.Join(
                " | ",
                entry.Evidence.Select(static row =>
                    $"{row.Kind}: {row.Detail}"));
        return evidence.Length <= 2_048
            ? evidence
            : evidence[..2_048];
    }

    private static ProjectMappingReviewOrigin ToProjectReviewOrigin(
        MappingReviewOrigin origin) => origin switch
    {
        MappingReviewOrigin.Explicit =>
            ProjectMappingReviewOrigin.Explicit,
        MappingReviewOrigin.Assisted =>
            ProjectMappingReviewOrigin.Assisted,
        _ => ProjectMappingReviewOrigin.None,
    };

    private static MappingReviewOrigin ToRuntimeReviewOrigin(
        ProjectMappingReviewOrigin origin) => origin switch
    {
        ProjectMappingReviewOrigin.Explicit =>
            MappingReviewOrigin.Explicit,
        ProjectMappingReviewOrigin.Assisted =>
            MappingReviewOrigin.Assisted,
        _ => MappingReviewOrigin.None,
    };

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
            project.Workflow.SelectedAnimationVariantId ??
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
            _pendingLocalAnm2ImportPath = null;
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
            _customTargetPreviewSession = null;
            _sourceModelContext = null;
            _pendingAnm2SourcePath = null;
            _pendingMimicSourcePath = null;
            _pendingMimicAssetId = null;
            _pendingFacialFbxSourcePath = null;
            _pendingFacialFbxAssetId = null;
            _fedDocument = null;
            _targetBaseMeshes = [];
            _sourceBaseMeshes = [];
            _lastPreviewFramePair = null;
            _editorSessionCoordinator.Reset(
                _activeAnimationId,
                frame: 0);
            SetTargetBindingStatus(TargetBindingStatus.Invalid);
            _attachmentRenderAssets.Clear();
            _attachmentStatuses.Clear();
            _lastAttachmentDiagnosticSignature = null;
            AssetBrowser.SelectedAsset = null;
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
        bool hasAuthoritativeSchema2Workflow =
            project.SchemaVersion >= 2 &&
            (!project.Models.IsEmpty ||
             !project.AnimationSources.IsEmpty ||
             !project.AnimationVariants.IsEmpty ||
             project.Animations.IsEmpty);
        if (hasAuthoritativeSchema2Workflow)
        {
            return project.Workflow.ActiveTab switch
            {
                ProjectWorkflowTab.Models =>
                    EditorWorkspaceMode.Models,
                ProjectWorkflowTab.Animations =>
                    EditorWorkspaceMode.Animations,
                ProjectWorkflowTab.Playback =>
                    EditorWorkspaceMode.Playback,
                ProjectWorkflowTab.RetargetEdit =>
                    EditorWorkspaceMode.RetargetEdit,
                ProjectWorkflowTab.Export =>
                    EditorWorkspaceMode.Export,
                _ => EditorWorkspaceMode.Models,
            };
        }

        ProjectAnimation? active = project.ActiveAnimationId is { } id
            ? project.Animations.FirstOrDefault(animation =>
                animation.Id == id)
            : project.Animations.FirstOrDefault();
        if (active is null)
        {
            return EditorWorkspaceMode.Models;
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
        project = SynchronizeSchema2FromCompatibilityAnimations(
            _project,
            project);
        project = ProjectAnimationOutputNormalizer.Normalize(project);
        Guid? activeAnimationId =
            _activeAnimationId is { } activeId &&
            (project.Animations.Any(animation =>
                 animation.Id == activeId) ||
             project.AnimationVariants.Any(variant =>
                 variant.Id == activeId))
                ? activeId
                : project.ActiveAnimationId is { } savedActiveId &&
                  (project.Animations.Any(animation =>
                       animation.Id == savedActiveId) ||
                   project.AnimationVariants.Any(variant =>
                       variant.Id == savedActiveId))
                    ? savedActiveId
                    : project.Workflow.SelectedAnimationVariantId is
                          { } workflowVariantId &&
                      (project.Animations.Any(animation =>
                           animation.Id == workflowVariantId) ||
                       project.AnimationVariants.Any(variant =>
                           variant.Id == workflowVariantId))
                        ? workflowVariantId
                        : null;
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

    private void EnsureCurrentAnimationTransition(
        long generation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(
                ref _animationTransitionGeneration))
        {
            throw new OperationCanceledException(
                "A newer animation operation superseded this result.",
                cancellationToken);
        }
    }

    private AnimationRuntimeSnapshot CaptureAnimationRuntimeSnapshot() =>
        new(
            _project,
            _savedProject,
            _activeAnimationId,
            _sourceAnimation,
            _mimicAnimation,
            _facialFbxAnimation,
            _synchronizedAnimation,
            _targetRig,
            _activeRetargetMap,
            _activeDirectRigBinding,
            _targetProjectAsset,
            _customTargetUsesSourcePreviewFallback,
            _customTargetPreviewDiagnostic,
            _customTargetPreviewSession,
            _sourceModelContext,
            _sourceBaseMeshes,
            _targetBaseMeshes,
            _targetBindingStatus,
            Timeline.CurrentFrame,
            Timeline.IsPlaying,
            Timeline.IsPlaybackEnabled,
            ActiveWorkspace,
            ActiveWorkspaceMode,
            _viewportCoordinator.CaptureOrbitCameras(),
            _lastPreviewFramePair,
            _undoProjects.ToArray(),
            _redoProjects.ToArray());

    private void RestoreAnimationRuntimeSnapshot(
        AnimationRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _project = snapshot.Project;
        _savedProject = snapshot.SavedProject;
        _activeAnimationId = snapshot.ActiveAnimationId;
        _sourceAnimation = snapshot.SourceAnimation;
        _mimicAnimation = snapshot.MimicAnimation;
        _facialFbxAnimation = snapshot.FacialFbxAnimation;
        _synchronizedAnimation = snapshot.SynchronizedAnimation;
        _targetRig = snapshot.TargetRig;
        _activeRetargetMap = snapshot.ActiveRetargetMap;
        _activeDirectRigBinding = snapshot.ActiveDirectRigBinding;
        _targetProjectAsset = snapshot.TargetProjectAsset;
        _customTargetUsesSourcePreviewFallback =
            snapshot.CustomTargetUsesSourcePreviewFallback;
        _customTargetPreviewDiagnostic =
            snapshot.CustomTargetPreviewDiagnostic;
        _customTargetPreviewSession =
            snapshot.CustomTargetPreviewSession;
        _sourceModelContext = snapshot.SourceModelContext;
        _sourceBaseMeshes = snapshot.SourceMeshes;
        _targetBaseMeshes = snapshot.TargetMeshes;
        _lastPreviewFramePair = snapshot.LastPreviewFramePair;
        _undoProjects.Clear();
        foreach (DlraProject project in snapshot.UndoProjects.Reverse())
        {
            _undoProjects.Push(project);
        }

        _redoProjects.Clear();
        foreach (DlraProject project in snapshot.RedoProjects.Reverse())
        {
            _redoProjects.Push(project);
        }

        _viewportCoordinator.RestoreOrbitCameras(
            snapshot.OrbitCameras);
        RefreshProjectBindings();
        SetTargetBindingStatus(snapshot.TargetBindingStatus);
        SetWorkspace(
            snapshot.Workspace,
            preserveLegacyCutscene:
                string.Equals(
                    snapshot.LegacyWorkspace,
                    "Cutscene",
                    StringComparison.Ordinal));
        Timeline.CurrentFrame = snapshot.Frame;
        Timeline.IsPlaybackEnabled = snapshot.IsPlaybackEnabled;
        Timeline.IsPlaying = snapshot.IsPlaying;
        _editorSessionCoordinator.Reset(
            snapshot.ActiveAnimationId,
            snapshot.Frame);
        OnPropertyChanged(nameof(ActiveSourceModelLabel));
        OnPropertyChanged(nameof(ActiveTargetModelLabel));
        UpdateDirtyState();
        NotifyAnimationLibraryCommands();
        if (_sourceAnimation is not null)
        {
            RefreshAnimationPreview();
        }
        else
        {
            RestoreIsolatedBrowsePreview();
        }
    }

    private void CommitPreparedAnimationTransition(
        PreparedAnimationTransition prepared,
        DlraProject preparedProject,
        bool beginPlayback,
        bool persistProject)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(preparedProject);
        EnsureCurrentAnimationTransition(
            prepared.Generation,
            CancellationToken.None);

        _activeAnimationId = prepared.Animation.Id;
        _sourceAnimation = prepared.Source;
        _sourceBaseMeshes = prepared.SourceMeshes;
        _sourceModelContext = prepared.SourceModel;
        _mimicAnimation = prepared.Mimic;
        _facialFbxAnimation = null;
        _synchronizedAnimation = prepared.SynchronizedClip;
        _activeRetargetMap = prepared.Mapping;
        _activeDirectRigBinding = prepared.DirectBinding;
        _pendingAnm2SourcePath = null;
        _pendingLocalAnm2ImportPath = null;
        _pendingMimicSourcePath = null;
        _pendingMimicAssetId = null;
        _pendingFacialFbxSourcePath = null;
        _pendingFacialFbxAssetId = null;
        OnPropertyChanged(nameof(ActiveSourceModelLabel));
        OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
        OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
        CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();

        if (prepared.Target is { } target)
        {
            ClearCustomTargetPreviewPresentation();
            PublishDecodedMesh(
                target.Payload,
                target.RetailAsset,
                target.ProjectAsset,
                restoreRetargetMap: false,
                animationContext: prepared.Animation);
            _activeRetargetMap = prepared.Mapping;
            _activeDirectRigBinding = prepared.DirectBinding;
        }
        else if (prepared.CustomTarget is { } customTarget)
        {
            _targetRig = customTarget.Rig;
            _targetProjectAsset = customTarget.ProjectAsset;
            _activeRetargetMap = prepared.Mapping;
            _activeDirectRigBinding = prepared.DirectBinding;
            SetCustomTargetPreviewPresentation(customTarget);
            OnPropertyChanged(nameof(ActiveTargetModelLabel));
            SetTargetPreviewScene(
                customTarget.Meshes,
                customTarget.Skeleton);
            if (customTarget.UsesSourceFallback)
            {
                AddDiagnostic(
                    "Warning",
                    "Model preview",
                    "DL1 target fidelity preparation fell back to Source FBX",
                    string.Join("; ", customTarget.PreviewDiagnostics));
            }
            ReplaceSkeleton(customTarget.Skeleton);
            FacialFpp.ReplaceMorphs(
                customTarget.Rig.MorphChannels.Select(
                    static morph =>
                        new MorphChannelViewModel(morph.Name)));
            IkEditor.ReplaceChains(
                customTarget.Rig.IkChains.Select(
                    static chain => chain.Name));
            InitializeIkEditorFromBindPose();
            ClearBlenderExportTarget();
        }
        else
        {
            _targetRig = null;
            _targetProjectAsset = null;
            _activeDirectRigBinding = null;
            ClearCustomTargetPreviewPresentation();
            _targetBaseMeshes = [];
            TargetViewport.SceneSource.SetScene([], null, []);
        }

        if (persistProject)
        {
            CommitProject(preparedProject);
        }
        else
        {
            _project = ProjectAnimationOutputNormalizer.Normalize(
                NormalizeAnimationVariantGroups(preparedProject));
            RefreshProjectBindings();
            UpdateDirtyState();
        }

        SetTargetBindingStatus(prepared.BindingStatus);
        _editorSessionCoordinator.Reset(
            prepared.Animation.Id,
            frame: 0);
        SetWorkspace(
            prepared.BindingStatus == TargetBindingStatus.NeedsReview
                ? EditorWorkspaceMode.RetargetEdit
                : EditorWorkspaceMode.Animate,
            preserveLegacyCutscene: false);
        Timeline.CurrentFrame = 0;
        Timeline.IsPlaying = beginPlayback &&
            prepared.BindingStatus is
                TargetBindingStatus.Direct or
                TargetBindingStatus.Ready;
        RefreshAnimationPreview(throwOnFailure: true);
        if (prepared.BindingStatus is (
                TargetBindingStatus.Direct or
                TargetBindingStatus.Ready) &&
            (_lastPreviewFramePair is null ||
             _lastPreviewFramePair.Token.AnimationId !=
                 prepared.Animation.Id))
        {
            throw new InvalidOperationException(
                "The prepared animation did not publish a matching initial viewport frame.");
        }
        if (prepared.Mapping is not null)
        {
            PublishMappingProposal(prepared.Mapping);
        }
    }

    private void PublishProjectModelAsDirectTarget(
        DecodedProjectModelSession model,
        ProjectAnimation animation)
    {
        if (model.RetailPayload is { } payload &&
            model.RetailAsset is { } retail)
        {
            PublishDecodedMesh(
                payload,
                retail,
                model.ProjectAsset,
                restoreRetargetMap: false,
                animationContext: animation);
            return;
        }

        _targetRig = model.Rig;
        _targetProjectAsset = model.ProjectAsset;
        _customTargetUsesSourcePreviewFallback =
            model.UsesSourcePreviewFallback;
        _customTargetPreviewDiagnostic = model.PreviewDiagnostics.IsDefaultOrEmpty
            ? null
            : string.Join("; ", model.PreviewDiagnostics);
        _customTargetPreviewSession = model.CustomPreviewSession;
        OnPropertyChanged(nameof(ActiveTargetModelLabel));
        SetTargetPreviewScene(model.PreviewMeshes, model.Skeleton);
        UpdateTargetPreviewPresentation();
        ReplaceSkeleton(model.Skeleton);
        FacialFpp.ReplaceMorphs(model.Rig.MorphChannels.Select(
            static morph => new MorphChannelViewModel(morph.Name)));
        IkEditor.ReplaceChains(model.Rig.IkChains.Select(
            static chain => chain.Name));
        InitializeIkEditorFromBindPose();
        ClearBlenderExportTarget();
    }

    private void ClearAnimationOperationFailure()
    {
        if (_animationOperationFailureMessage is null)
        {
            return;
        }

        _animationOperationFailureMessage = null;
        OnPropertyChanged(nameof(HasAnimationOperationFailure));
        OnPropertyChanged(nameof(AnimationOperationFailureMessage));
    }

    private void ReportAnimationOperationFailure(
        string operation,
        string stage,
        string source,
        long generation,
        Exception exception)
    {
        string actionable =
            $"{operation} failed during {stage}: {exception.Message} Previous animation retained.";
        _animationOperationFailureMessage = actionable;
        OnPropertyChanged(nameof(HasAnimationOperationFailure));
        OnPropertyChanged(nameof(AnimationOperationFailureMessage));
        StatusText = actionable;
        AddDiagnostic(
            "Error",
            $"Animation {operation.ToLowerInvariant()}",
            $"{operation} failed during {stage}; previous session retained",
            $"Source: {source}\nOperation generation: {generation}\n{exception}");
        _structuredLogger?.Write(
            AppLogLevel.Error,
            $"animation_{operation.ToLowerInvariant()}_failed",
            actionable,
            new Dictionary<string, string>
            {
                ["source"] = source,
                ["stage"] = stage,
                ["generation"] = generation.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["candidateSourceFingerprint"] =
                    _sourceModelContext?.ProjectAsset.ContentSha256 ??
                    string.Empty,
                ["targetFingerprint"] =
                    _targetProjectAsset?.ContentSha256 ?? string.Empty,
                ["previousSessionRetained"] = "true",
            },
            exception);
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

    /// <summary>
    /// The legacy runtime/editor session still evaluates ProjectAnimation
    /// rows. Schema 2 owns the durable source/variant records, so every
    /// ordinary compatibility-row edit is copied into that authoritative
    /// model before the project is refreshed or saved. Package-embedded
    /// sources have no lossy compatibility row and are deliberately left
    /// untouched.
    /// </summary>
    internal static DlraProject
        SynchronizeSchema2FromCompatibilityAnimations(
            DlraProject previous,
            DlraProject project)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(project);
        if (project.SchemaVersion < DlraProject.CurrentSchemaVersion)
        {
            return project;
        }

        Dictionary<Guid, ProjectAssetReference> assets = project.Assets
            .ToDictionary(static asset => asset.Id);
        ImmutableArray<ProjectModelEntry>.Builder models =
            project.Models.ToBuilder();
        ImmutableArray<ProjectAnimationSource>.Builder sources =
            project.AnimationSources.ToBuilder();
        ImmutableArray<ProjectAnimationVariant>.Builder variants =
            project.AnimationVariants.ToBuilder();
        Dictionary<Guid, ProjectAnimation> previousAnimations = previous
            .Animations
            .ToDictionary(static animation => animation.Id);
        HashSet<Guid> compatibilityIds = project.Animations
            .Select(static animation => animation.Id)
            .ToHashSet();

        Dictionary<Guid, ProjectAnimationSource> sourceById = sources
            .ToDictionary(static source => source.Id);
        for (int index = variants.Count - 1; index >= 0; index--)
        {
            ProjectAnimationVariant variant = variants[index];
            if (sourceById.TryGetValue(
                    variant.SourceId,
                    out ProjectAnimationSource? source) &&
                source.SourceBinding is not null &&
                !compatibilityIds.Contains(variant.Id))
            {
                variants.RemoveAt(index);
            }
        }

        foreach (ProjectAnimation animation in project.Animations)
        {
            int variantIndex = FindVariantIndex(
                variants,
                animation.Id);
            Guid sourceId;
            int sourceIndex;
            bool rowChanged =
                !previousAnimations.TryGetValue(
                    animation.Id,
                    out ProjectAnimation? previousAnimation) ||
                !animation.Equals(previousAnimation);
            if (variantIndex >= 0)
            {
                ProjectAnimationVariant existingVariant =
                    variants[variantIndex];
                sourceId = existingVariant.SourceId;
                sourceIndex = FindSourceIndex(sources, sourceId);
                if (sourceIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Animation variant '{animation.Name}' refers to a missing schema-2 source.");
                }
            }
            else
            {
                sourceId = animation.VariantGroupId ?? animation.Id;
                sourceIndex = FindSourceIndex(sources, sourceId);
                if (sourceIndex >= 0 &&
                    !CanRepresentCompatibilityAnimation(
                        sources[sourceIndex],
                        animation))
                {
                    sourceId = Guid.NewGuid();
                    sourceIndex = -1;
                }

                if (sourceIndex < 0)
                {
                    sources.Add(CreateSchema2Source(
                        animation,
                        sourceId));
                    sourceIndex = sources.Count - 1;
                }
            }

            ProjectAnimationSource schemaSource = sources[sourceIndex];
            if (schemaSource.EmbeddedCustomModelStack is null &&
                (rowChanged || variantIndex < 0))
            {
                sources[sourceIndex] = UpdateSchema2Source(
                    schemaSource,
                    animation);
            }
            else if (schemaSource.EmbeddedCustomModelStack is not null &&
                     rowChanged)
            {
                sources[sourceIndex] = schemaSource with
                {
                    MimicAssetId = animation.MimicAssetId,
                    FacialAnimationSourceBinding =
                        animation.FacialAnimationSourceBinding,
                    FacialSourceAssetId =
                        animation.FacialSourceAssetId,
                    FacialSourceValueUnit =
                        animation.FacialSourceValueUnit,
                    FacialTiming = animation.FacialTiming,
                };
            }

            Guid? targetModelId = ResolveSchema2TargetModel(
                models,
                assets,
                animation);
            if (targetModelId is null)
            {
                if (variantIndex >= 0)
                {
                    variants.RemoveAt(variantIndex);
                }

                continue;
            }

            if (variantIndex >= 0)
            {
                variants[variantIndex] = UpdateSchema2Variant(
                    variants[variantIndex],
                    animation,
                    targetModelId.Value);
            }
            else
            {
                variants.Add(UpdateSchema2Variant(
                    new ProjectAnimationVariant
                    {
                        Id = animation.Id,
                        SourceId = sourceId,
                        Name = animation.Name,
                    },
                    animation,
                    targetModelId.Value));
            }
        }

        Guid? requestedActiveId = project.ActiveAnimationId ??
            project.Workflow.SelectedAnimationVariantId;
        ProjectAnimationVariant? activeVariant = requestedActiveId is { } id
            ? variants.FirstOrDefault(variant => variant.Id == id)
            : null;
        Guid? activeId = activeVariant?.Id;
        ProjectWorkflowState workflow = activeVariant is null
            ? project.Workflow with
            {
                ActiveTab = project.Workflow.ActiveTab is
                    ProjectWorkflowTab.Playback or
                    ProjectWorkflowTab.RetargetEdit or
                    ProjectWorkflowTab.Export
                        ? ProjectWorkflowTab.Animations
                        : project.Workflow.ActiveTab,
                SelectedAnimationVariantId = null,
            }
            : project.Workflow with
            {
                SelectedModelId = activeVariant.TargetModelId,
                SelectedAnimationSourceId = activeVariant.SourceId,
                SelectedAnimationVariantId = activeVariant.Id,
            };
        return project with
        {
            Models = models.ToImmutable(),
            AnimationSources = sources.ToImmutable(),
            AnimationVariants = variants.ToImmutable(),
            ActiveAnimationId = activeId,
            Workflow = workflow,
        };
    }

    private static int FindSourceIndex(
        ImmutableArray<ProjectAnimationSource>.Builder sources,
        Guid sourceId)
    {
        for (var index = 0; index < sources.Count; index++)
        {
            if (sources[index].Id == sourceId)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindVariantIndex(
        ImmutableArray<ProjectAnimationVariant>.Builder variants,
        Guid variantId)
    {
        for (var index = 0; index < variants.Count; index++)
        {
            if (variants[index].Id == variantId)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool CanRepresentCompatibilityAnimation(
        ProjectAnimationSource source,
        ProjectAnimation animation) =>
        source.EmbeddedCustomModelStack is null &&
        source.SourceAssetId == animation.SourceAssetId &&
        Equals(source.SourceBinding, animation.SourceBinding);

    private static ProjectAnimationSource CreateSchema2Source(
        ProjectAnimation animation,
        Guid sourceId) =>
        UpdateSchema2Source(
            new ProjectAnimationSource
            {
                Id = sourceId,
                Name = animation.Name,
                SourceAssetId = animation.SourceAssetId,
                LegacyVariantGroupId =
                    animation.VariantGroupId ?? sourceId,
            },
            animation);

    private static ProjectAnimationSource UpdateSchema2Source(
        ProjectAnimationSource source,
        ProjectAnimation animation) =>
        source with
        {
            SourceAssetId = animation.SourceAssetId,
            SourceBinding = animation.SourceBinding,
            EmbeddedCustomModelStack = null,
            RequiresSourceRebind = animation.SourceBinding is null,
            LegacySourceRigSignature =
                animation.SourceBinding is null
                    ? animation.SourceRigSignature
                    : null,
            MimicAssetId = animation.MimicAssetId,
            FacialAnimationSourceBinding =
                animation.FacialAnimationSourceBinding,
            FacialSourceAssetId = animation.FacialSourceAssetId,
            FacialSourceValueUnit = animation.FacialSourceValueUnit,
            FacialTiming = animation.FacialTiming,
            SourceAnimationSkeletonSignature =
                animation.SourceAnimationSkeletonSignature,
            FrameRate = animation.FrameRate,
            FrameCount = animation.FrameCount,
        };

    private static ProjectAnimationVariant UpdateSchema2Variant(
        ProjectAnimationVariant variant,
        ProjectAnimation animation,
        Guid targetModelId) =>
        variant with
        {
            Name = animation.Name,
            TargetModelId = targetModelId,
            TargetRigId = animation.TargetRigId,
            TargetRigSignature = animation.TargetRigSignature,
            TargetAnimationSkeletonSignature =
                animation.TargetAnimationSkeletonSignature,
            BindingMode = animation.BindingMode,
            DirectBinding = animation.DirectBinding,
            BindingEvidenceFingerprint =
                animation.BindingEvidenceFingerprint,
            BindingPolicyVersion = animation.BindingPolicyVersion,
            MappingFingerprint = animation.MappingFingerprint,
            MimicProfileId = animation.MimicProfileId,
            MimicMappingFingerprint =
                animation.MimicMappingFingerprint,
            RootMotionMode = animation.RootMotionMode,
            RootBoneName = animation.RootBoneName,
            PreviewMotionAccumulationEnabled =
                animation.PreviewMotionAccumulationEnabled,
            BoneMappings = animation.BoneMappings,
            TargetBindReviews = animation.TargetBindReviews,
            EditLayers = animation.EditLayers,
            MorphBindings = animation.MorphBindings,
            MorphEditLayers = animation.MorphEditLayers,
            IkLayers = animation.IkLayers,
            Attachments = animation.Attachments,
        };

    private static Guid? ResolveSchema2TargetModel(
        ImmutableArray<ProjectModelEntry>.Builder models,
        Dictionary<Guid, ProjectAssetReference> assets,
        ProjectAnimation animation)
    {
        if (animation.TargetAssetId is not { } targetAssetId)
        {
            return null;
        }

        ProjectModelEntry? existing = models.FirstOrDefault(model =>
            model.AssetId == targetAssetId);
        if (existing is not null)
        {
            return !existing.IsStatic &&
                   IsSha256Value(existing.RigSignature)
                ? existing.Id
                : null;
        }

        if (!assets.TryGetValue(
                targetAssetId,
                out ProjectAssetReference? asset))
        {
            throw new InvalidOperationException(
                $"Animation '{animation.Name}' refers to a missing target asset.");
        }

        string name = asset.RetailIdentity?.ResourceName ??
            Path.GetFileNameWithoutExtension(asset.RelativePath);
        if (!IsSha256Value(animation.TargetRigSignature))
        {
            return null;
        }

        var model = new ProjectModelEntry
        {
            Id = Guid.NewGuid(),
            AssetId = targetAssetId,
            Name = string.IsNullOrWhiteSpace(name)
                ? "Project model"
                : name,
            RigSignature = IsSha256Value(
                    animation.TargetRigSignature)
                ? animation.TargetRigSignature!.ToLowerInvariant()
                : null,
            AnimationSkeletonSignature = IsSha256Value(
                    animation.TargetAnimationSkeletonSignature)
                ? animation.TargetAnimationSkeletonSignature!
                    .ToLowerInvariant()
                : null,
            IsStatic = false,
        };
        models.Add(model);
        return model.Id;
    }

    private static bool IsSha256Value(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => Uri.IsHexDigit(character));

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
            _project.Workflow.SelectedAnimationVariantId ??
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
            RefreshRootBoneCandidates(animation);
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
                    mapping.Confidence,
                    mapping.Method,
                    mapping.IsLocked,
                    mapping.IsReviewed,
                    mapping.MappingKind,
                    mapping.TransferPolicy,
                    mapping.ComponentPolicy,
                    mapping.Evidence,
                    mapping.ReviewOrigin,
                    mapping.ScorerVersion,
                    mapping.EvidenceFingerprint,
                    mapping.EffectiveTransformComponents);
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
                "Load a source animation and fingerprinted target to review mapping.";
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
                "Choose Normalized or Percent, then import a facial FBX against the active body timeline and exact fingerprinted target.";
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
        if (!_project.AnimationSources.IsEmpty)
        {
            PopulateSchema2AnimationLibrary(assets);
            AttachAnimationLibraryRowHandlers();
            SelectedAnimationLibraryItem = AnimationLibrary
                    .FirstOrDefault(item => item.Id == selectedId) ??
                AnimationLibrary.FirstOrDefault(item => item.IsActive) ??
                AnimationLibrary.FirstOrDefault();
            RefreshProjectModelLibrary();
            RefreshExportWorkflow();
            NotifyAnimationLibraryCommands();
            return;
        }

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
            TargetBindingStatus bindingStatus = animation.BindingMode is
                    ProjectAnimationBindingMode.ExactDirect or
                    ProjectAnimationBindingMode.CompatibleDirect
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
                    animation.BindingMode ==
                        ProjectAnimationBindingMode.CompatibleDirect
                        ? "Direct \u2014 compatible skeleton"
                        : "Direct \u2014 owning model",
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

        AttachAnimationLibraryRowHandlers();
        SelectedAnimationLibraryItem = AnimationLibrary.FirstOrDefault(item =>
            item.Id == selectedId) ??
            AnimationLibrary.FirstOrDefault(item => item.IsActive) ??
            AnimationLibrary.FirstOrDefault();
        RefreshProjectModelLibrary();
        RefreshExportWorkflow();
        NotifyAnimationLibraryCommands();
    }

    private void PopulateSchema2AnimationLibrary(
        Dictionary<Guid, ProjectAssetReference> assets)
    {
        Dictionary<Guid, ProjectModelEntry> models = _project.Models
            .ToDictionary(static model => model.Id);
        foreach (ProjectAnimationSource source in _project
                     .AnimationSources
                     .OrderBy(static source => source.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            ProjectAnimationVariant[] variants = _project
                .AnimationVariants
                .Where(variant => variant.SourceId == source.Id)
                .OrderBy(variant =>
                    models.TryGetValue(
                        variant.TargetModelId,
                        out ProjectModelEntry? model)
                        ? model.Name
                        : string.Empty,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
            assets.TryGetValue(
                source.SourceAssetId,
                out ProjectAssetReference? sourceAsset);
            ProjectAssetReference? sourceModelAsset =
                source.SourceBinding?.RetailSourceModelAssetId is
                    { } retailModelId &&
                assets.TryGetValue(
                    retailModelId,
                    out ProjectAssetReference? retailModel)
                    ? retailModel
                    : source.EmbeddedCustomModelStack is not null
                        ? sourceAsset
                        : null;
            string originName = source.Presentation?.OriginName ??
                FormatProjectAssetLabel(sourceModelAsset ?? sourceAsset);
            string roles = source.SourceBinding?.Roles.ToString() ??
                source.EmbeddedCustomModelStack?.Roles.ToString() ??
                "Unproven legacy source";
            double durationSeconds = source.FrameCount <= 1
                ? 0.0
                : source.FrameRate.SecondsForFrame(
                    source.FrameCount - 1);
            string cadence =
                $"{source.FrameRate.Numerator}/{source.FrameRate.Denominator} fps";
            string duration =
                $"{source.FrameCount:N0} frames / {durationSeconds:0.###} s";
            string sourceDiagnostics =
                BuildAnimationSourceDiagnostics(source);
            if (variants.Length == 0)
            {
                AnimationLibrary.Add(new AnimationLibraryItemViewModel(
                    source.Id,
                    source.Name,
                    FormatProjectAssetLabel(sourceAsset),
                    originName,
                    "No target variant",
                    roles,
                    cadence,
                    duration,
                    "Immutable source - assign a project model",
                    sourceDiagnostics,
                    isActive: false,
                    source.Id,
                    source.Name,
                    TargetBindingStatus.Invalid,
                    showVariantGroupHeader: true,
                    isSourceOnly: true,
                    isRuntimeAvailable: false,
                    primaryScript: "Unassigned",
                    effectiveScripts: "None",
                    outputName: "Not assigned",
                    includeInExport: false));
                continue;
            }

            bool first = true;
            foreach (ProjectAnimationVariant variant in variants)
            {
                ProjectModelEntry? targetModel =
                    models.TryGetValue(
                        variant.TargetModelId,
                        out ProjectModelEntry? found)
                        ? found
                        : null;
                ProjectAssetReference? targetAsset = targetModel is not null &&
                    assets.TryGetValue(
                        targetModel.AssetId,
                        out ProjectAssetReference? foundAsset)
                        ? foundAsset
                        : null;
                bool direct = targetModel is { IsStatic: false } &&
                    variant.BindingMode is
                        ProjectAnimationBindingMode.ExactDirect or
                        ProjectAnimationBindingMode.CompatibleDirect;
                bool reviewed = variant.MappingFingerprint is not null &&
                    !variant.BoneMappings.IsEmpty &&
                    variant.BoneMappings.All(static mapping =>
                        mapping.IsReviewed);
                TargetBindingStatus bindingStatus = targetModel switch
                {
                    null => TargetBindingStatus.Invalid,
                    { IsStatic: true } => TargetBindingStatus.Invalid,
                    _ when direct => TargetBindingStatus.Direct,
                    _ when reviewed => TargetBindingStatus.Ready,
                    _ => TargetBindingStatus.NeedsReview,
                };
                string mappingState = bindingStatus switch
                {
                    TargetBindingStatus.Direct =>
                        variant.BindingMode ==
                            ProjectAnimationBindingMode.CompatibleDirect
                            ? "Direct \u2014 compatible skeleton"
                            : "Direct \u2014 owning model",
                    TargetBindingStatus.Ready =>
                        "Reviewed cross-rig mapping",
                    TargetBindingStatus.NeedsReview =>
                        "Draft retarget — preview available, export blocked",
                    _ when targetModel?.IsStatic == true =>
                        "Static model cannot be an animation target",
                    _ => "Target unavailable",
                };
                string diagnostics = string.Join(
                    " | ",
                    new[]
                    {
                        sourceDiagnostics,
                        variant.MorphBindings.Any(static binding =>
                            binding.Enabled &&
                            (!binding.IsReviewed || !binding.IsLocked))
                            ? "Facial review required"
                            : string.Empty,
                    }.Where(static value =>
                        !string.IsNullOrWhiteSpace(value)));
                ProjectAnimationLibrary? library =
                    ResolveAnimationLibrary(
                        variant.OwningAnimationLibraryId);
                AnimationLibrary.Add(new AnimationLibraryItemViewModel(
                    variant.Id,
                    variant.Name,
                    FormatProjectAssetLabel(sourceAsset),
                    originName,
                    targetModel?.Name ?? "Not selected",
                    roles,
                    cadence,
                    duration,
                    mappingState,
                    string.IsNullOrWhiteSpace(diagnostics)
                        ? "Ready"
                        : diagnostics,
                    variant.Id == _activeAnimationId,
                    source.Id,
                    source.Name,
                    bindingStatus,
                    showVariantGroupHeader: first,
                    isSourceOnly: false,
                    isRuntimeAvailable:
                        source.SourceBinding is not null ||
                        source.EmbeddedCustomModelStack is not null,
                    primaryScript: library?.ResourceName ?? "Unassigned",
                    effectiveScripts: FormatEffectiveAnimationLibraryImports(
                        library),
                    outputName: variant.OutputAnm2Name ?? "Unassigned",
                    includeInExport: variant.IncludeInPackage));
                first = false;
            }
        }
    }

    private ProjectAnimationLibrary? ResolveAnimationLibrary(Guid? id) =>
        id is { } libraryId
            ? _project.AnimationLibraries.FirstOrDefault(library =>
                library.Id == libraryId)
            : null;

    private string FormatEffectiveAnimationLibraryImports(
        ProjectAnimationLibrary? root)
    {
        if (root is null)
        {
            return "None";
        }

        var names = new List<string>();
        var visited = new HashSet<Guid>();
        void Visit(ProjectAnimationLibrary library)
        {
            if (!visited.Add(library.Id))
            {
                return;
            }

            foreach (ProjectAnimationLibraryImport import in library.Imports)
            {
                if (import.Kind ==
                        ProjectAnimationLibraryImportKind.ProjectLibrary &&
                    import.ProjectLibraryId is { } projectLibraryId)
                {
                    ProjectAnimationLibrary? imported =
                        ResolveAnimationLibrary(projectLibraryId);
                    if (imported is not null)
                    {
                        names.Add(imported.ResourceName);
                        Visit(imported);
                    }
                }
                else if (import.RetailScriptIdentity is { } retail)
                {
                    names.Add(retail.ResourceName);
                }
            }
        }

        Visit(root);
        return names.Count == 0
            ? "None"
            : string.Join(", ", names.Distinct(
                StringComparer.OrdinalIgnoreCase));
    }

    private void AttachAnimationLibraryRowHandlers()
    {
        foreach (AnimationLibraryItemViewModel item in AnimationLibrary)
        {
            item.PropertyChanged += OnAnimationLibraryRowPropertyChanged;
        }
    }

    private void OnAnimationLibraryRowPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName !=
                nameof(AnimationLibraryItemViewModel.IncludeInPackage) ||
            sender is not AnimationLibraryItemViewModel
            {
                IsSourceOnly: false,
            } item)
        {
            return;
        }

        int index = -1;
        for (int candidate = 0;
             candidate < _project.AnimationVariants.Length;
             candidate++)
        {
            if (_project.AnimationVariants[candidate].Id == item.Id)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0 ||
            _project.AnimationVariants[index].IncludeInPackage ==
                item.IncludeInPackage)
        {
            return;
        }

        CommitProject(_project with
        {
            AnimationVariants = _project.AnimationVariants.SetItem(
                index,
                _project.AnimationVariants[index] with
                {
                    IncludeInPackage = item.IncludeInPackage,
                }),
        });
    }

    private static string BuildAnimationSourceDiagnostics(
        ProjectAnimationSource source)
    {
        var diagnostics = new List<string>();
        if (source.RequiresSourceRebind)
        {
            diagnostics.Add("Source model is unproven; rebind required");
        }

        if (source.EmbeddedCustomModelStack is not null)
        {
            diagnostics.Add("Embedded custom-model stack");
        }
        else if (TryParseExternalFbxStackTimingDetail(
                     source.SourceBinding?.TimingDetail,
                     out long stackObjectId,
                     out _,
                     out FbxFacialSourceValueUnit unit,
                     out _))
        {
            diagnostics.Add(
                $"FBX stack object {stackObjectId}; DeformPercent {unit}");
        }

        if (!string.IsNullOrWhiteSpace(source.MigrationNote))
        {
            diagnostics.Add(source.MigrationNote);
        }

        if (source.MimicAssetId is not null ||
            source.FacialSourceAssetId is not null)
        {
            diagnostics.Add("Separate facial source attached");
        }

        return string.Join(" | ", diagnostics);
    }

    private void RefreshProjectModelLibrary()
    {
        Guid? selectedModelId = SelectedProjectModel?.ModelId ??
            _project.Workflow.SelectedModelId;
        ProjectModelLibrary.Clear();
        Dictionary<Guid, ProjectAssetReference> assets = _project.Assets
            .ToDictionary(static asset => asset.Id);
        foreach (ProjectModelEntry model in _project.Models.OrderBy(
                     static model => model.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            assets.TryGetValue(
                model.AssetId,
                out ProjectAssetReference? asset);
            string source = asset?.Kind switch
            {
                ProjectAssetKind.RetailGameResource => "Base game reference",
                ProjectAssetKind.CustomModelSource => "Project custom model",
                _ => "Unavailable model asset",
            };
            string contract = model.IsStatic
                ? "Static model - preview/export only"
                : string.IsNullOrWhiteSpace(model.RigSignature)
                    ? "Rig identity pending validation"
                    : $"Rig {model.RigSignature[..Math.Min(12, model.RigSignature.Length)]}...";
            ProjectModelLibrary.Add(new ProjectModelItemViewModel(
                model.Id,
                model.Name,
                source,
                contract));
        }

        SelectedProjectModel = ProjectModelLibrary.FirstOrDefault(model =>
            model.ModelId == selectedModelId);
    }

    private void RefreshExportWorkflow()
    {
        bool hadRows = ExportVariants.Count > 0;
        HashSet<Guid> previouslySelected = ExportModelSelections
            .SelectMany(static model => model.Variants)
            .Where(static variant => variant.IsSelected)
            .Select(static variant => variant.AnimationId)
            .ToHashSet();
        if (!hadRows)
        {
            previouslySelected.UnionWith(
                _project.ExportSelection.AnimationVariantIds);
        }
        ExportModelSelections.Clear();
        ExportVariants.Clear();

        Dictionary<Guid, ProjectModelEntry> models = _project.Models
            .ToDictionary(static model => model.Id);
        Dictionary<Guid, ProjectAnimationSource> sources =
            _project.AnimationSources.ToDictionary(static source => source.Id);
        foreach (IGrouping<Guid, ProjectAnimationVariant> group in
                 _project.AnimationVariants
                     .GroupBy(static variant => variant.TargetModelId)
                     .OrderBy(group =>
                         models.TryGetValue(group.Key, out ProjectModelEntry? model)
                             ? model.Name
                             : "Unbound model",
                         StringComparer.OrdinalIgnoreCase))
        {
            ProjectModelEntry? model =
                models.TryGetValue(group.Key, out ProjectModelEntry? found)
                    ? found
                    : null;
            var variants = new List<ExportVariantSelectionViewModel>();
            foreach (ProjectAnimationVariant variant in group.OrderBy(
                         static variant => variant.Name,
                         StringComparer.OrdinalIgnoreCase))
            {
                sources.TryGetValue(
                    variant.SourceId,
                    out ProjectAnimationSource? source);
                ProjectAssetReference? sourceAsset = source is null
                    ? null
                    : FindProjectAsset(source.SourceAssetId);
                bool sourceReady = source is not null &&
                    !source.RequiresSourceRebind &&
                    !string.IsNullOrWhiteSpace(source.SourceRigSignature) &&
                    sourceAsset?.ContentSha256 is not null &&
                    (source.EmbeddedCustomModelStack is null ||
                     !string.IsNullOrWhiteSpace(
                         source.EmbeddedCustomModelStack
                             .StackFingerprint));
                ProjectAssetReference? targetAsset = model is null
                    ? null
                    : FindProjectAsset(model.AssetId);
                bool targetReady = model is { IsStatic: false } &&
                    targetAsset?.ContentSha256 is not null &&
                    !string.IsNullOrWhiteSpace(model.RigSignature) &&
                    string.Equals(
                        model.RigSignature,
                        variant.TargetRigSignature,
                        StringComparison.OrdinalIgnoreCase);
                bool descriptorReady = targetReady &&
                    TryValidateProjectModelDl1DescriptorInventory(
                        model!,
                        targetAsset);
                bool direct = sourceReady &&
                    descriptorReady &&
                    variant.BindingMode is
                        ProjectAnimationBindingMode.ExactDirect or
                        ProjectAnimationBindingMode.CompatibleDirect;
                bool currentBoneEvidence = direct ||
                    variant.BoneMappings.All(static row =>
                        row.ReviewOrigin !=
                            ProjectMappingReviewOrigin.Assisted ||
                        string.Equals(
                            row.ScorerVersion,
                            RetargetSuggestionScorer.PolicyVersion,
                            StringComparison.Ordinal));
                bool boneReady = direct ||
                    variant.MappingFingerprint is not null &&
                    !variant.BoneMappings.IsEmpty &&
                    currentBoneEvidence &&
                    variant.BoneMappings.All(static row =>
                        row.IsReviewed);
                bool hasFacialSource = source is not null &&
                    SourceHasFacialRole(source);
                bool directFacial =
                    variant.BindingMode ==
                        ProjectAnimationBindingMode.ExactDirect &&
                    SourceFacialTracksUseOwningRig(source!);
                bool currentFacialEvidence = !hasFacialSource ||
                    directFacial ||
                    variant.MorphBindings.All(static row =>
                        row.ReviewOrigin !=
                            ProjectMappingReviewOrigin.Assisted ||
                        string.Equals(
                            row.ScorerVersion,
                            ProjectMorphSuggestionScorer.PolicyVersion,
                            StringComparison.Ordinal));
                bool faceReady = IsFacialMappingExportReady(
                    hasFacialSource,
                    directFacial,
                    variant.MorphBindings);
                bool ready = sourceReady &&
                    descriptorReady &&
                    boneReady &&
                    faceReady;
                string readiness = !sourceReady
                    ? "Source fingerprint or stack identity requires rebind"
                    : model is null
                        ? "Target model missing"
                        : model.IsStatic
                            ? "Static models cannot be animation targets"
                            : !targetReady
                                ? "Target fingerprint or rig signature is stale"
                                : !descriptorReady
                                    ? "Target DL1 bone/helper/morph descriptors collide or cannot be validated"
                                : !currentBoneEvidence
                                    ? "Bone scorer evidence is stale"
                            : !boneReady
                                ? "Bone review required"
                                : !currentFacialEvidence
                                    ? "Facial scorer evidence is stale"
                                : !faceReady
                                    ? hasFacialSource &&
                                      !directFacial &&
                                      variant.MorphBindings.IsEmpty
                                        ? "Facial mapping inventory required"
                                        : "Facial review required"
                                    : direct
                                        ? variant.BindingMode ==
                                            ProjectAnimationBindingMode
                                                .CompatibleDirect
                                            ? "Ready \u2014 direct compatible skeleton"
                                            : "Ready \u2014 direct owning model"
                                        : "Ready \u2014 reviewed retarget";
                bool selected = previouslySelected.Count == 0
                    ? false
                    : previouslySelected.Contains(variant.Id) && ready;
                ProjectAnimationLibrary? library =
                    ResolveAnimationLibrary(
                        variant.OwningAnimationLibraryId);
                var exportRow = new ExportVariantSelectionViewModel(
                    variant.Id,
                    string.IsNullOrWhiteSpace(variant.Name)
                        ? source?.Name ?? "Untitled variant"
                        : variant.Name,
                    readiness,
                    ready,
                    selected,
                    originModel: source?.Presentation?.OriginName ??
                        source?.Name,
                    targetModel: model?.Name,
                    primaryScript: library?.ResourceName ?? "Unassigned",
                    effectiveScripts: FormatEffectiveAnimationLibraryImports(
                        library),
                    outputName: variant.OutputAnm2Name ?? "Unassigned",
                    bindingState: direct
                        ? variant.BindingMode ==
                            ProjectAnimationBindingMode.CompatibleDirect
                            ? "Direct — compatible skeleton"
                            : "Direct — owning model"
                        : boneReady
                            ? "Reviewed retarget"
                            : "Draft retarget");
                exportRow.PropertyChanged +=
                    OnExportVariantSelectionChanged;
                variants.Add(exportRow);
                ExportVariants.Add(exportRow);
            }

            ExportModelSelections.Add(new ExportModelSelectionViewModel(
                group.Key,
                model?.Name ?? "Unbound model",
                variants));
        }

        RefreshActiveExportReadiness();
        OnPropertyChanged(nameof(ExportSelectionSummary));
        ApplyExportSelectionCommand.NotifyCanExecuteChanged();
        ExportCheckedPortableCommand.NotifyCanExecuteChanged();
        DeployCheckedToDeveloperToolsCommand.NotifyCanExecuteChanged();
    }

    private void OnExportVariantSelectionChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (args.PropertyName !=
            nameof(ExportVariantSelectionViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(ExportSelectionSummary));
        ExportCheckedPortableCommand.NotifyCanExecuteChanged();
        DeployCheckedToDeveloperToolsCommand.NotifyCanExecuteChanged();
        DeployCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private void RefreshActiveExportReadiness()
    {
        ExportReadiness.Clear();
        ProjectAnimation? active = GetActiveAnimation();
        bool hasSourceFingerprint = active is not null &&
            FindProjectAsset(active.SourceAssetId)?.ContentSha256 is not null;
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Source fingerprint",
            hasSourceFingerprint ? "Ready" : "Blocked",
            hasSourceFingerprint
                ? "The active source content identity is stored."
                : "Load or rebind the immutable animation source.",
            hasSourceFingerprint));

        bool hasTarget = active?.TargetAssetId is { } targetAssetId &&
            FindProjectAsset(targetAssetId) is not null &&
            _targetRig is not null;
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Target model",
            hasTarget ? "Ready" : "Blocked",
            hasTarget
                ? ActiveTargetModelLabel
                : "Assign and load an exact project model target.",
            hasTarget));

        bool bodyReady = CanExportAnimation();
        string descriptorDiagnostic =
            "Load an exact target rig before validating its descriptors.";
        bool descriptorReady = _targetRig is { } descriptorTarget &&
            TryValidateDl1DescriptorInventory(
                descriptorTarget,
                out descriptorDiagnostic);
        if (_sourceAnimation is { Rig: { } descriptorSource } &&
            descriptorSource.Id.StartsWith(
                "custom:",
                StringComparison.Ordinal) &&
            !TryValidateDl1DescriptorInventory(
                descriptorSource,
                out string sourceDescriptorDiagnostic))
        {
            descriptorReady = false;
            descriptorDiagnostic =
                "Custom animation source: " + sourceDescriptorDiagnostic;
        }
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "DL1 descriptors",
            descriptorReady ? "Ready" : "Blocked",
            descriptorReady
                ? descriptorDiagnostic
                : _targetRig is null
                    ? "Load an exact target rig before validating its descriptors."
                    : descriptorDiagnostic,
            descriptorReady));
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Body ANM2",
            bodyReady ? "Ready" : "Blocked",
            bodyReady
                ? ActiveTargetBindingStatus == TargetBindingStatus.Direct
                    ? active?.BindingMode ==
                        ProjectAnimationBindingMode.CompatibleDirect
                        ? "Direct compatible-skeleton evaluation; no retarget map is fabricated."
                        : "Direct owning-model evaluation; no retarget map is fabricated."
                    : "Every required retarget row is reviewed."
                : IsDraftTargetPreview
                    ? "Draft playback is available, but mapping review is incomplete."
                    : "The active source/target variant is not export-ready.",
            bodyReady));

        ProjectAnimationVariant? activeVariant = _project.AnimationVariants
            .FirstOrDefault(variant => variant.Id == _activeAnimationId);
        ProjectAnimationSource? activeSource = activeVariant is null
            ? null
            : _project.AnimationSources.FirstOrDefault(source =>
                source.Id == activeVariant.SourceId);
        bool hasFacialSource = activeSource is not null
            ? SourceHasFacialRole(activeSource)
            : active is not null &&
              (HasSavedFacialSource(active) ||
               !active.MorphBindings.IsEmpty ||
               _sourceAnimation?.Clip.ScalarTracks.IsEmpty == false);
        bool directFacial = active is not null &&
            _sourceAnimation is { } activeRuntimeSource &&
            _targetRig is { } activeRuntimeTarget &&
            HasSameRigContract(
                activeRuntimeSource.Rig,
                activeRuntimeTarget) &&
            ActiveTargetBindingStatus == TargetBindingStatus.Direct &&
            activeSource is not null &&
            SourceFacialTracksUseOwningRig(activeSource);
        bool facialReady = active is not null &&
            hasFacialSource &&
            CanExportAnimation() &&
            IsFacialMappingExportReady(
                hasFacialSource,
                directFacial,
                active.MorphBindings);
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Facial ANM2",
            facialReady ? "Ready" : "Blocked",
            facialReady
                ? directFacial
                    ? "Direct owning-model morph evaluation; no mapping is fabricated."
                    : "Every enabled facial mapping is reviewed and current."
                : !hasFacialSource
                    ? "The active source has no facial animation role."
                    : "Cross-rig facial export requires a current reviewed and locked mapping inventory.",
            facialReady));

        ProjectModelEntry? activeModel = active?.TargetAssetId is { } activeTargetId
            ? _project.Models.FirstOrDefault(model =>
                model.AssetId == activeTargetId)
            : null;
        bool gameCameraReady =
            activeModel?.ExportableEyeCameraHelperCount == 1;
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Game FPP camera",
            gameCameraReady ? "Ready" : "Optional",
            gameCameraReady
                ? "Exactly one unweighted Camera helper is named EyeCamera."
                : activeModel?.ExportableEyeCameraHelperCount > 1
                    ? "Multiple EyeCamera helpers are ambiguous; keep exactly one exportable camera helper."
                    : "The selected editor camera remains usable for preview; game-ready FPP needs exactly one exportable EyeCamera helper.",
            gameCameraReady));

        bool compilerReady = File.Exists(Models.CompilerExecutablePath);
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "RPack / Developer Tools",
            compilerReady ? "Ready" : "Tool needed",
            Models.CompilerStatus,
            compilerReady));
        bool blenderReady = ExportSelectedMeshToBlenderFbxCommand
            .CanExecute(null);
        ExportReadiness.Add(new ExportReadinessItemViewModel(
            "Self-contained FBX",
            blenderReady ? "Mesh ready" : "Model needed",
            BlenderExportStatus,
            blenderReady));
    }

    private void ApplyExportSelection()
    {
        HashSet<Guid> selected = ExportModelSelections
            .SelectMany(static model => model.Variants)
            .Where(static variant => variant.IsSelected)
            .Select(static variant => variant.AnimationId)
            .ToHashSet();
        DlraProject updated = _project with
        {
            ExportSelection = new ProjectExportSelection
            {
                AnimationVariantIds = selected.Order().ToImmutableArray(),
                ModelIds = _project.AnimationVariants
                    .Where(variant => selected.Contains(variant.Id))
                    .Select(static variant => variant.TargetModelId)
                    .Distinct()
                    .Order()
                    .ToImmutableArray(),
            },
        };
        updated.Validate();
        CommitProject(updated);
        StatusText =
            $"Saved package checklist: {selected.Count:N0} animation variant(s) selected";
    }

    private bool CanRunCheckedExportWorkflow() =>
        !IsBusy &&
        ExportModelSelections
            .SelectMany(static model => model.Variants)
            .Any(static variant => variant.IsEnabled);

    private void SelectDeveloperToolsExportMode(string? value)
    {
        if (!Enum.TryParse(
                value,
                ignoreCase: true,
                out DeveloperToolsExportMode mode))
        {
            return;
        }

        _developerToolsExportMode = mode;
        OnPropertyChanged(nameof(IsDeveloperToolsAnimationsOnlySelected));
        OnPropertyChanged(nameof(IsDeveloperToolsCharactersOnlySelected));
        OnPropertyChanged(nameof(IsDeveloperToolsAnm2OnlySelected));
        OnPropertyChanged(nameof(IsDeveloperToolsFullProjectSelected));
        OnPropertyChanged(nameof(IsCharacterCompilerRequired));
        DeployCurrentSelectionCommand.NotifyCanExecuteChanged();
    }

    private Task DeployCurrentSelectionAsync() =>
        _developerToolsExportMode switch
        {
            DeveloperToolsExportMode.AnimationsOnly =>
                DeploySelectedProjectArtifactsAsync(
                    includeAnimations: true,
                    includeAnimationRpack: true,
                    includeCharacters: false),
            DeveloperToolsExportMode.Anm2Only =>
                DeploySelectedProjectArtifactsAsync(
                    includeAnimations: true,
                    includeAnimationRpack: false,
                    includeCharacters: false),
            DeveloperToolsExportMode.CharactersOnly =>
                DeploySelectedProjectArtifactsAsync(
                    includeAnimations: false,
                    includeAnimationRpack: false,
                    includeCharacters: true),
            DeveloperToolsExportMode.Full =>
                DeployCheckedToDeveloperToolsAsync(),
            _ => throw new InvalidOperationException(
                "The selected Developer Tools export mode is unsupported."),
        };

    private async Task DeploySelectedProjectArtifactsAsync(
        bool includeAnimations,
        bool includeAnimationRpack,
        bool includeCharacters)
    {
        if (!TryGetCheckedVariantIds(
                out ImmutableHashSet<Guid> selectedVariantIds))
        {
            StatusText =
                "Developer Tools export needs at least one checked, export-ready animation row";
            return;
        }

        string? projectRoot = Directory.Exists(
                Models.DeveloperToolsProjectRoot)
            ? Models.DeveloperToolsProjectRoot
            : _fileDialogs.ShowSelectDl1DeveloperToolsProjectDialog(
                ProjectPath);
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(projectRoot))
        {
            StatusText =
                "Developer Tools export canceled: selected project is unavailable";
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            "Export to Developer Tools",
            "Selected artifact transaction",
            includeCharacters
                ? "Evaluating animations and compiling selected characters"
                : "Evaluating selected animations without rebuilding characters");
        string stagingRoot = CreateCheckedExportStagingDirectory();
        try
        {
            ImmutableArray<PreparedCheckedExportModel> prepared =
                await PrepareCheckedExportModelsAsync(
                    selectedVariantIds,
                    includeCharacters,
                    stagingRoot,
                    job,
                    job.CancellationToken);
            var writes = ImmutableArray.CreateBuilder<
                ProjectArtifactWrite>();
            int scriptLinkUpdates = 0;
            if (includeAnimations)
            {
                foreach (StandaloneExportArtifact artifact in
                         CreateStandaloneAnm2Artifacts(prepared))
                {
                    string name = Path.GetFileName(
                        artifact.RelativePath);
                    writes.Add(new ProjectArtifactWrite(
                        $"data/characters/animations/{name}",
                        artifact.Payload));
                }

                if (includeAnimationRpack)
                {
                    BuiltProjectAnimationPack pack =
                        BuildProjectAnimationPack(prepared);
                    foreach ((string name, string script) in
                             pack.LooseScripts)
                    {
                        writes.Add(new ProjectArtifactWrite(
                            $"data/characters/animations/animscripts/{name}.scr",
                            new UTF8Encoding(false).GetBytes(script)));
                    }

                    string projectOutputName =
                        Dl1SourceModelWriter.SanitizeName(
                            CreateUnifiedExportPackageName()
                                .Replace(
                                    "-portable-output",
                                    string.Empty,
                                    StringComparison.OrdinalIgnoreCase),
                            63);
                    writes.Add(new ProjectArtifactWrite(
                        $"out/ReAnimated/{projectOutputName}/animations/{projectOutputName}_animations_pc.rpack",
                        pack.Rpack));

                    foreach (PreparedCheckedExportModel model in prepared
                                 .Where(static row =>
                                     row.CustomModel is not null))
                    {
                        CustomModelBuildSettings settings = model
                            .CustomModel!.Package.Document.BuildSettings;
                        if (string.Equals(
                                settings.AnimationScriptAlias,
                                model.AnimationLibraryName,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        string resourceName =
                            Dl1SourceModelWriter.SanitizeName(
                                settings.ResourceName,
                                55);
                        string characterId =
                            string.IsNullOrWhiteSpace(settings.CharacterId)
                                ? resourceName
                                : settings.CharacterId;
                        string ascr =
                            $"AnimScriptAlias(\"{model.AnimationLibraryName}.scr\")\n";
                        writes.Add(new ProjectArtifactWrite(
                            $"data/characters/{characterId}/{resourceName}.ascr",
                            new UTF8Encoding(false).GetBytes(ascr)));
                        scriptLinkUpdates++;
                    }
                }
            }

            if (includeCharacters)
            {
                int characterCount = 0;
                string projectOutputName =
                    Dl1SourceModelWriter.SanitizeName(
                        CreateUnifiedExportPackageName()
                            .Replace(
                                "-portable-output",
                                string.Empty,
                                StringComparison.OrdinalIgnoreCase),
                        63);
                foreach (PreparedCheckedExportModel model in prepared
                             .Where(static row =>
                                 row.CustomModel is not null))
                {
                    byte[] rpack = model.CompiledCustomModelRpack ??
                        throw new InvalidDataException(
                            $"Custom model '{model.Model.Name}' has no validated compiler output.");
                    string resourceName =
                        Dl1SourceModelWriter.SanitizeName(
                            model.CustomModel!.Package.Document
                                .BuildSettings.ResourceName,
                            55);
                    writes.Add(new ProjectArtifactWrite(
                        $"out/ReAnimated/{projectOutputName}/characters/{resourceName}_pc.rpack",
                        rpack));
                    characterCount++;
                }

                if (characterCount == 0)
                {
                    throw new InvalidOperationException(
                        "Characters only needs at least one checked user-owned custom-model target. Retail model bytes are never copied.");
                }
            }

            if (writes.Count == 0)
            {
                throw new InvalidOperationException(
                    "The selected Developer Tools mode produced no artifacts.");
            }

            job.Stage = "Conflict check";
            ValidateDeveloperToolsArtifactConflicts(
                projectRoot,
                writes);
            job.Stage = "One recoverable project transaction";
            job.Progress = 86.0;
            ProjectArtifactTransactionResult result =
                await ProjectArtifactTransactionService.CommitAsync(
                    new ProjectArtifactTransactionRequest
                    {
                        ProjectRoot = projectRoot,
                        OwnerProjectId = _project.ProjectId,
                        Artifacts = writes.MoveToImmutable(),
                    },
                    job.CancellationToken);
            _lastProjectArtifactProjectRoot = projectRoot;
            _lastProjectArtifactReceiptRelativePath =
                result.ReceiptRelativePath;
            _lastDeveloperToolsBatchReceiptPath = null;
            RollBackDeveloperToolsBatchCommand.NotifyCanExecuteChanged();
            job.Progress = 100.0;
            job.Complete("Committed");
            StatusText =
                $"Developer Tools export committed {result.Receipt.Artifacts.Length:N0} artifact(s)";
            AddDiagnostic(
                "Info",
                "Export",
                "Developer Tools selection committed",
                $"Animations: {(includeAnimations ? "yes" : "no")}; animation RPack: {(includeAnimationRpack ? "yes" : "no")}; characters: {(includeCharacters ? "yes" : "no")}. " +
                $"Root SCR link update(s): {scriptLinkUpdates:N0}. " +
                "The internal receipt supports Undo last export. Offline artifact validation is not live-game proof.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled; project retained");
            StatusText = "Developer Tools export canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException or
            TimeoutException or
            Win32Exception)
        {
            job.Complete("Failed or rolled back");
            StatusText = "Developer Tools export failed";
            AddDiagnostic(
                "Error",
                "Export",
                "Developer Tools selection was not committed",
                exception.Message);
        }
        finally
        {
            DeleteCheckedExportStagingDirectory(stagingRoot);
            IsBusy = false;
        }
    }

    private void ValidateDeveloperToolsArtifactConflicts(
        string projectRoot,
        IEnumerable<ProjectArtifactWrite> artifacts)
    {
        foreach (ProjectArtifactWrite artifact in artifacts)
        {
            string path = ResolveStandaloneArtifactPath(
                projectRoot,
                NormalizeStandaloneArtifactPath(
                    artifact.RelativePath));
            if (!File.Exists(path))
            {
                continue;
            }

            string existingHash;
            using (FileStream stream = new(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       128 * 1024,
                       FileOptions.SequentialScan))
            {
                existingHash = Convert.ToHexStringLower(
                    SHA256.HashData(stream));
            }

            string selectedHash = Convert.ToHexStringLower(
                SHA256.HashData(artifact.Bytes.Span));
            if (string.Equals(
                    existingHash,
                    selectedHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DeveloperToolsDeploymentConflictDecision decision =
                _fileDialogs.ResolveDeveloperToolsDeploymentConflict(
                    artifact.RelativePath,
                    "A different file already owns this Developer Tools path. The selected export can back it up and replace it as part of the recoverable transaction.",
                    canSkip: false);
            if (decision !=
                DeveloperToolsDeploymentConflictDecision.BackUpAndReplace)
            {
                throw new OperationCanceledException(
                    $"Replacement of '{artifact.RelativePath}' was canceled; no selected artifact was silently skipped.");
            }
        }
    }

    private Task ExportSelectedAnm2FilesAsync() =>
        ExportStandaloneSelectionAsync(StandaloneExportMode.Anm2Files);

    private Task ExportSelectedAnimationRPackAsync() =>
        ExportStandaloneSelectionAsync(
            StandaloneExportMode.AnimationRpack);

    private Task ExportSelectedCharacterFilesAsync() =>
        ExportStandaloneSelectionAsync(
            StandaloneExportMode.CharacterFiles);

    private async Task ExportStandaloneSelectionAsync(
        StandaloneExportMode mode)
    {
        if (!TryGetCheckedVariantIds(
                out ImmutableHashSet<Guid> selectedVariantIds))
        {
            StatusText = "Select at least one export-ready animation row";
            return;
        }

        string? parentDirectory =
            _fileDialogs.ShowSelectExportDirectoryDialog(
                ProjectPath is null
                    ? null
                    : Path.GetDirectoryName(ProjectPath));
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return;
        }

        bool compileCharacters =
            mode == StandaloneExportMode.CharacterFiles;
        string label = mode switch
        {
            StandaloneExportMode.Anm2Files => "ANM2 files",
            StandaloneExportMode.AnimationRpack => "animation RPack",
            StandaloneExportMode.CharacterFiles => "character files",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Export {label}",
            "Selected project artifacts",
            "Evaluating checked target variants");
        string stagingRoot = CreateCheckedExportStagingDirectory();
        try
        {
            ImmutableArray<PreparedCheckedExportModel> prepared =
                await PrepareCheckedExportModelsAsync(
                    selectedVariantIds,
                    compileCharacters,
                    stagingRoot,
                    job,
                    job.CancellationToken);
            ImmutableArray<StandaloneExportArtifact> artifacts = mode switch
            {
                StandaloneExportMode.Anm2Files =>
                    CreateStandaloneAnm2Artifacts(prepared),
                StandaloneExportMode.AnimationRpack =>
                    CreateStandaloneAnimationPackArtifacts(
                        BuildProjectAnimationPack(prepared)),
                StandaloneExportMode.CharacterFiles =>
                    CreateStandaloneCharacterArtifacts(prepared),
                _ => throw new ArgumentOutOfRangeException(nameof(mode)),
            };

            job.Stage = "Atomic output replacement";
            job.Progress = 88.0;
            string outputDirectory = await PublishStandaloneExportAsync(
                parentDirectory,
                GetStandaloneExportDirectorySuffix(mode),
                artifacts,
                job.CancellationToken);
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText = $"Exported {label}";
            AddDiagnostic(
                "Info",
                "Export",
                $"Selected {label} exported",
                $"Output: {outputDirectory}\n" +
                $"Artifacts: {artifacts.Length:N0}. Previous owned output was replaced transactionally. Offline validation is not live-game proof.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled; previous output retained");
            StatusText = $"{label} export canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException or
            TimeoutException or
            Win32Exception)
        {
            job.Complete("Failed; previous output retained");
            StatusText = $"{label} export failed";
            AddDiagnostic(
                "Error",
                "Export",
                $"Selected {label} was not published",
                exception.Message);
        }
        finally
        {
            DeleteCheckedExportStagingDirectory(stagingRoot);
            IsBusy = false;
        }
    }

    private async Task ExportCheckedPortableAsync()
    {
        if (!TryGetCheckedVariantIds(
                out ImmutableHashSet<Guid> selectedVariantIds))
        {
            StatusText =
                "Portable export needs at least one checked, export-ready variant";
            return;
        }

        string? parentDirectory =
            _fileDialogs.ShowSelectExportDirectoryDialog(
                ProjectPath is null
                    ? null
                    : Path.GetDirectoryName(ProjectPath));
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            "Export full project",
            "Selected project artifacts",
            "Sampling checked target variants");
        string stagingRoot = CreateCheckedExportStagingDirectory();
        try
        {
            ImmutableArray<PreparedCheckedExportModel> prepared =
                await PrepareCheckedExportModelsAsync(
                    selectedVariantIds,
                    compileCustomModels: true,
                    stagingRoot,
                    job,
                    job.CancellationToken);
            BuiltProjectAnimationPack animationPack =
                BuildProjectAnimationPack(prepared);
            ImmutableArray<StandaloneExportArtifact> artifacts =
                CreateStandaloneFullProjectArtifacts(
                    prepared,
                    animationPack);
            job.Stage = "Atomic full-project replacement";
            job.Progress = 90.0;
            string outputDirectory = await PublishStandaloneExportAsync(
                parentDirectory,
                "full-project",
                artifacts,
                job.CancellationToken);
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Exported full project with {artifacts.Length:N0} artifact(s)";
            AddDiagnostic(
                "Info",
                "Export",
                "Full project export completed",
                $"Output: {outputDirectory}\n" +
                "The selection was published as one project-owned animation RPack, individual ANM2 files, custom-character outputs, and a top-level manifest. Retail targets contribute references and authored animation/script resources only; no retail model bytes were copied. Offline package validation is not live-game proof.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled; previous output retained");
            StatusText = "Full project export canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException or
            TimeoutException or
            Win32Exception)
        {
            job.Complete("Failed; previous output retained");
            StatusText = "Full project export failed";
            AddDiagnostic(
                "Error",
                "Export",
                "Full project was not published",
                exception.Message);
        }
        finally
        {
            DeleteCheckedExportStagingDirectory(stagingRoot);
            IsBusy = false;
        }
    }

    private async Task DeployCheckedToDeveloperToolsAsync()
    {
        if (!TryGetCheckedVariantIds(
                out ImmutableHashSet<Guid> selectedVariantIds))
        {
            StatusText =
                "Developer Tools deployment needs at least one checked, export-ready variant";
            return;
        }

        ProjectAnimationVariant[] selectedVariants = _project
            .AnimationVariants
            .Where(variant => selectedVariantIds.Contains(variant.Id))
            .ToArray();
        ProjectModelEntry[] retailTargets = selectedVariants
            .Select(variant => _project.Models.FirstOrDefault(model =>
                model.Id == variant.TargetModelId))
            .Where(model => model is not null &&
                FindProjectAsset(model.AssetId)?.Kind ==
                    ProjectAssetKind.RetailGameResource)
            .DistinctBy(static model => model!.Id)
            .Cast<ProjectModelEntry>()
            .ToArray();
        if (retailTargets.Length > 0)
        {
            StatusText =
                "Developer Tools model deployment is blocked for checked retail targets";
            AddDiagnostic(
                "Warning",
                "Export",
                "Retail targets were not silently skipped",
                "The checked Developer Tools transaction deploys project-owned custom models. " +
                "Retail target(s) remain selected and visible, so this operation was blocked: " +
                string.Join(", ", retailTargets.Select(static model => model.Name)) +
                ". Use portable output for retail-reference animation packages.");
            return;
        }

        string? projectRoot =
            _fileDialogs.ShowSelectDl1DeveloperToolsProjectDialog(
                Directory.Exists(Models.DeveloperToolsProjectRoot)
                    ? Models.DeveloperToolsProjectRoot
                    : ProjectPath);
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return;
        }

        projectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(projectRoot))
        {
            StatusText =
                "Developer Tools deployment canceled: selected project is unavailable";
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            "Deploy checked models",
            "Atomic Developer Tools batch",
            "Sampling checked target variants");
        string stagingRoot = CreateCheckedExportStagingDirectory();
        try
        {
            ImmutableArray<PreparedCheckedExportModel> prepared =
                await PrepareCheckedExportModelsAsync(
                    selectedVariantIds,
                    compileCustomModels: false,
                    stagingRoot,
                    job,
                    job.CancellationToken);
            ImmutableArray<Dl1DeveloperToolsDeploymentRequest> requests =
                CreateDeveloperToolsBatchRequests(
                    prepared,
                    projectRoot);
            var batchRequest = new Dl1DeveloperToolsBatchRequest
            {
                Deployments = requests,
            };
            job.Stage = "Preflight every checked model";
            job.Progress = 72.0;
            Dl1DeveloperToolsBatchPreflight preflight =
                await Dl1DeveloperToolsProjectDeployer
                    .PreflightBatchAsync(
                        batchRequest,
                        job.CancellationToken);
            requests = ResolveDeveloperToolsBatchConflicts(
                requests,
                preflight);
            batchRequest = new Dl1DeveloperToolsBatchRequest
            {
                Deployments = requests,
            };
            preflight = await Dl1DeveloperToolsProjectDeployer
                .PreflightBatchAsync(
                    batchRequest,
                    job.CancellationToken);
            if (!preflight.CanDeploy)
            {
                throw new Dl1DeveloperToolsBatchConflictException(
                    preflight);
            }

            int artifactCount = preflight.Plans.Sum(static plan =>
                plan.Artifacts.Length);
            int animationCount = preflight.Plans.Sum(static plan =>
                plan.Animations.Length);
            if (!_fileDialogs.ConfirmDeveloperToolsDeployment(
                    projectRoot,
                    artifactCount,
                    animationCount))
            {
                job.Complete("Canceled after complete batch preflight");
                StatusText =
                    "Developer Tools batch canceled after preflight; project unchanged";
                return;
            }

            job.Stage = "One recoverable batch transaction";
            job.Progress = 85.0;
            Dl1DeveloperToolsBatchResult result =
                await Dl1DeveloperToolsProjectDeployer.DeployBatchAsync(
                    batchRequest,
                    job.CancellationToken);
            _lastDeveloperToolsBatchReceiptPath = result.ReceiptPath;
            _lastProjectArtifactReceiptRelativePath = null;
            _lastProjectArtifactProjectRoot = null;
            RollBackDeveloperToolsBatchCommand.NotifyCanExecuteChanged();
            job.Progress = 100.0;
            job.Complete("Committed");
            StatusText =
                $"Developer Tools batch committed {result.Deployments.Length:N0} model(s)";
            AddDiagnostic(
                "Info",
                "Export",
                "Checked Developer Tools batch committed",
                $"Receipt: {result.ReceiptPath}\n" +
                "Every checked custom model was preflighted before the first commit. The receipt supports recoverable rollback. Offline compiler and installed-asset checks are not live-game proof.");
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled; project retained");
            StatusText = "Developer Tools batch canceled";
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            OverflowException or
            TimeoutException or
            Win32Exception)
        {
            job.Complete("Failed or rolled back");
            StatusText = "Developer Tools batch failed";
            if (exception is
                Dl1DeveloperToolsBatchTransactionException
                {
                    Receipt.State:
                        Dl1DeveloperToolsBatchState.RollbackRequired,
                } recoverable)
            {
                _lastDeveloperToolsBatchReceiptPath =
                    recoverable.ReceiptPath;
                RollBackDeveloperToolsBatchCommand
                    .NotifyCanExecuteChanged();
            }
            string receipt = exception is
                Dl1DeveloperToolsBatchTransactionException transaction
                    ? $"\nReceipt: {transaction.ReceiptPath}"
                    : string.Empty;
            AddDiagnostic(
                "Error",
                "Export",
                "Checked Developer Tools transaction did not complete",
                exception.Message + receipt);
        }
        finally
        {
            DeleteCheckedExportStagingDirectory(stagingRoot);
            IsBusy = false;
        }
    }

    private async Task RollBackDeveloperToolsBatchAsync()
    {
        bool projectArtifactReceipt =
            !string.IsNullOrWhiteSpace(
                _lastProjectArtifactReceiptRelativePath) &&
            Directory.Exists(_lastProjectArtifactProjectRoot);
        string? receiptPath = projectArtifactReceipt
            ? Path.Combine(
                _lastProjectArtifactProjectRoot!,
                _lastProjectArtifactReceiptRelativePath!
                    .Replace('/', Path.DirectorySeparatorChar))
            : _lastDeveloperToolsBatchReceiptPath;
        if (!File.Exists(receiptPath) ||
            !_fileDialogs.ConfirmDeveloperToolsDeploymentRollback(
                receiptPath))
        {
            return;
        }

        IsBusy = true;
        JobViewModel job = AddJob(
            "Roll back checked model batch",
            "Developer Tools recovery",
            "Validating the batch receipt");
        try
        {
            string rollbackIdentity;
            if (projectArtifactReceipt)
            {
                ProjectArtifactTransactionReceipt receipt =
                    await ProjectArtifactTransactionService.RollbackAsync(
                        _lastProjectArtifactProjectRoot!,
                        _project.ProjectId,
                        _lastProjectArtifactReceiptRelativePath!,
                        job.CancellationToken);
                rollbackIdentity = receipt.TransactionId;
                _lastProjectArtifactReceiptRelativePath = null;
                _lastProjectArtifactProjectRoot = null;
            }
            else
            {
                Dl1DeveloperToolsBatchReceipt receipt =
                    await Dl1DeveloperToolsProjectDeployer
                        .RollbackBatchAsync(
                            receiptPath,
                            job.CancellationToken);
                rollbackIdentity = receipt.BatchId;
                _lastDeveloperToolsBatchReceiptPath = null;
            }

            job.Progress = 100.0;
            job.Complete("Rolled back");
            StatusText =
                $"Developer Tools export {rollbackIdentity} rolled back";
            RollBackDeveloperToolsBatchCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            job.Complete("Canceled; project retained");
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            job.Complete("Rollback requires review");
            AddDiagnostic(
                "Error",
                "Export",
                "Developer Tools batch rollback did not complete",
                exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryGetCheckedVariantIds(
        out ImmutableHashSet<Guid> selectedVariantIds)
    {
        selectedVariantIds = ExportModelSelections
            .SelectMany(static model => model.Variants)
            .Where(static variant =>
                variant.IsEnabled && variant.IsSelected)
            .Select(static variant => variant.AnimationId)
            .ToImmutableHashSet();
        return !selectedVariantIds.IsEmpty;
    }

    private static ImmutableArray<StandaloneExportArtifact>
        CreateStandaloneAnm2Artifacts(
            ImmutableArray<PreparedCheckedExportModel> prepared)
    {
        ImmutableArray<StandaloneExportArtifact> artifacts = prepared
            .SelectMany(static model => model.Animations)
            .OrderBy(static animation => animation.Name,
                StringComparer.OrdinalIgnoreCase)
            .Select(static animation => new StandaloneExportArtifact(
                $"animations/{animation.Name}.anm2",
                animation.Payload))
            .ToImmutableArray();
        if (artifacts.IsEmpty)
        {
            throw new InvalidOperationException(
                "The current export selection produced no ANM2 files.");
        }

        return artifacts;
    }

    private static ImmutableArray<StandaloneExportArtifact>
        CreateStandaloneAnimationPackArtifacts(
            BuiltProjectAnimationPack pack) =>
        [
            new StandaloneExportArtifact(
                "animation-library_pc.rpack",
                pack.Rpack),
            .. pack.LooseScripts
                .OrderBy(static pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static pair => new StandaloneExportArtifact(
                    $"source/animscripts/{pair.Key}.scr",
                    new UTF8Encoding(false).GetBytes(pair.Value))),
        ];

    private ImmutableArray<StandaloneExportArtifact>
        CreateStandaloneFullProjectArtifacts(
            ImmutableArray<PreparedCheckedExportModel> prepared,
            BuiltProjectAnimationPack animationPack)
    {
        var artifacts = ImmutableArray.CreateBuilder<
            StandaloneExportArtifact>();
        foreach (StandaloneExportArtifact artifact in
                 CreateStandaloneAnimationPackArtifacts(animationPack))
        {
            artifacts.Add(artifact with
            {
                RelativePath = "animations/" + artifact.RelativePath,
            });
        }

        foreach (StandaloneExportArtifact artifact in
                 CreateStandaloneAnm2Artifacts(prepared))
        {
            artifacts.Add(artifact with
            {
                RelativePath =
                    "animations/files/" +
                    Path.GetFileName(artifact.RelativePath),
            });
        }

        artifacts.AddRange(CreateStandaloneCharacterArtifacts(
            prepared,
            requireCustomModel: false));
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "dl-reanimated-full-project-export",
            schemaVersion = 1,
            projectId = _project.ProjectId,
            animationRpack =
                "animations/animation-library_pc.rpack",
            models = prepared
                .OrderBy(static row => row.Model.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(static row => new
                {
                    id = row.Model.Id,
                    row.Model.Name,
                    targetKind = row.Asset.Kind.ToString(),
                    targetFingerprint = row.Asset.ContentSha256,
                    rootScr = row.AnimationLibraryName,
                    customCharacterIncluded =
                        row.CustomModel is not null,
                }),
            animations = prepared
                .SelectMany(static row => row.Animations.Select(
                    animation => new
                    {
                        targetModelId = row.Model.Id,
                        animation.VariantId,
                        animation.Name,
                        role = animation.Role.ToString(),
                        animation.SourceFingerprint,
                    }))
                .OrderBy(static row => row.targetModelId)
                .ThenBy(static row => row.Name,
                    StringComparer.OrdinalIgnoreCase),
        }, IndentedJsonOptions);
        artifacts.Add(new StandaloneExportArtifact(
            "project-export.json",
            manifest));
        return artifacts.MoveToImmutable();
    }

    private static ImmutableArray<StandaloneExportArtifact>
        CreateStandaloneCharacterArtifacts(
            ImmutableArray<PreparedCheckedExportModel> prepared,
            bool requireCustomModel = true)
    {
        var artifacts = ImmutableArray.CreateBuilder<
            StandaloneExportArtifact>();
        foreach (PreparedCheckedExportModel model in prepared
                     .Where(static row => row.CustomModel is not null)
                     .OrderBy(static row => row.Model.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            byte[] rpack = model.CompiledCustomModelRpack ??
                throw new InvalidDataException(
                    $"Custom model '{model.Model.Name}' has no validated compiler output.");
            Dl1OfficialModelCompilerEvidence evidence =
                model.OfficialCompilerEvidence ??
                throw new InvalidDataException(
                    $"Custom model '{model.Model.Name}' has no compiler evidence.");
            string resourceName = Dl1SourceModelWriter.SanitizeName(
                model.CustomModel!.Package.Document.BuildSettings
                    .ResourceName,
                55);
            artifacts.Add(new StandaloneExportArtifact(
                $"characters/{resourceName}_pc.rpack",
                rpack));
            artifacts.Add(new StandaloneExportArtifact(
                $"characters/{resourceName}.compiler-evidence.json",
                JsonSerializer.SerializeToUtf8Bytes(
                    evidence,
                    IndentedJsonOptions)));
        }

        if (requireCustomModel && artifacts.Count == 0)
        {
            throw new InvalidOperationException(
                "Character-only export needs at least one checked user-owned custom-model target. Retail model bytes are never redistributed.");
        }

        return artifacts.MoveToImmutable();
    }

    private BuiltProjectAnimationPack BuildProjectAnimationPack(
        ImmutableArray<PreparedCheckedExportModel> prepared) =>
        BuildProjectAnimationPack(
            _project,
            prepared.Select(static model =>
                new ProjectAnimationPackTarget(
                    model.Model,
                    model.CustomModel is not null,
                    model.Animations))
                .ToImmutableArray());

    internal static BuiltProjectAnimationPack BuildProjectAnimationPack(
        DlraProject project,
        ImmutableArray<ProjectAnimationPackTarget> prepared)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (prepared.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "An animation RPack requires at least one selected target.");
        }

        Dictionary<Guid, ProjectAnimationVariant> variants = project
            .AnimationVariants
            .ToDictionary(static variant => variant.Id);
        Dictionary<Guid, ProjectAnimationSource> sources = project
            .AnimationSources
            .ToDictionary(static source => source.Id);
        Dictionary<Guid, ProjectAnimationLibrary> libraries = project
            .AnimationLibraries
            .ToDictionary(static library => library.Id);
        var animations = ImmutableDictionary.CreateBuilder<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        var localSequences = new Dictionary<Guid, List<AnimationScrSequence>>();
        var selectedLibraryIds = new HashSet<Guid>();

        foreach (ProjectAnimationPackTarget model in prepared)
        {
            var modelLibraryIds = new HashSet<Guid>();
            foreach (Dl1PortableAnimationResource resource in
                     model.Animations)
            {
                if (!animations.TryAdd(resource.Name, resource.Payload))
                {
                    throw new InvalidOperationException(
                        $"Selected type-320 resource identity '{resource.Name}' is duplicated. Assign a unique target-specific ANM2 output name.");
                }

                if (!variants.TryGetValue(
                        resource.VariantId,
                        out ProjectAnimationVariant? variant) ||
                    !sources.TryGetValue(
                        variant.SourceId,
                        out ProjectAnimationSource? source) ||
                    variant.OwningAnimationLibraryId is not { } libraryId ||
                    !libraries.TryGetValue(
                        libraryId,
                        out ProjectAnimationLibrary? owningLibrary))
                {
                    throw new InvalidDataException(
                        $"Selected animation '{resource.Name}' has no valid owning SCR assignment.");
                }

                selectedLibraryIds.Add(libraryId);
                modelLibraryIds.Add(libraryId);
                if (resource.Role != Dl1PortableAnimationRole.Body)
                {
                    continue;
                }

                if (!localSequences.TryGetValue(
                        libraryId,
                        out List<AnimationScrSequence>? rows))
                {
                    rows = [];
                    localSequences.Add(libraryId, rows);
                }

                string sequenceName = Dl1SourceModelWriter.SanitizeName(
                    source.Name,
                    63);
                if (rows.Any(row => string.Equals(
                        row.Name,
                        sequenceName,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Animation SCR '{owningLibrary.ResourceName}' has more than one selected sequence named '{sequenceName}'. Rename or reassign one row.");
                }

                rows.Add(new AnimationScrSequence(
                    sequenceName,
                    resource.Name + ".anm2",
                    0,
                    resource.FrameCount - 1,
                    resource.FramesPerSecond));
            }

            if (model.Model.RootAnimationLibraryId is { } rootLibraryId)
            {
                selectedLibraryIds.Add(rootLibraryId);
                foreach (Guid assignedLibraryId in modelLibraryIds)
                {
                    if (!CanReachLibrary(
                            rootLibraryId,
                            assignedLibraryId))
                    {
                        throw new InvalidOperationException(
                            $"Model '{model.Model.Name}' root SCR '{libraries[rootLibraryId].ResourceName}' does not import assigned SCR '{libraries[assignedLibraryId].ResourceName}'. Add that project library to the root SCR's ordered imports or reassign the animation before export.");
                    }
                }
            }
            else if (model.IsCustomModel)
            {
                throw new InvalidOperationException(
                    $"Custom model '{model.Model.Name}' has no root SCR link. Assign a project animation library before export.");
            }
        }

        // Every emitted !include must have a matching project-owned type-322
        // resource and loose source file. Expand the selected roots/owners to
        // their complete project-library dependency closure before building.
        var pendingLibraries = new Queue<Guid>(selectedLibraryIds);
        while (pendingLibraries.TryDequeue(out Guid libraryId))
        {
            if (!libraries.TryGetValue(
                    libraryId,
                    out ProjectAnimationLibrary? library))
            {
                throw new InvalidDataException(
                    "An animation target refers to a missing project SCR.");
            }

            foreach (ProjectAnimationLibraryImport import in library.Imports)
            {
                if (import.Kind ==
                        ProjectAnimationLibraryImportKind.ProjectLibrary &&
                    selectedLibraryIds.Add(
                        import.ProjectLibraryId!.Value))
                {
                    pendingLibraries.Enqueue(
                        import.ProjectLibraryId.Value);
                }
            }
        }

        var scripts = ImmutableDictionary.CreateBuilder<
            string,
            Rp6lAnimationScript>(StringComparer.OrdinalIgnoreCase);
        var looseScripts = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var compiled = new Dictionary<
            Guid,
            ImmutableArray<AnimationScrSequence>>();
        var visiting = new HashSet<Guid>();
        foreach (Guid libraryId in selectedLibraryIds.Order())
        {
            ImmutableArray<AnimationScrSequence> effective =
                ResolveEffectiveSequences(libraryId);
            ProjectAnimationLibrary library = libraries[libraryId];
            AnimationScrSections sections = AnimationScrCodec.Build(effective);
            scripts.Add(
                library.ResourceName,
                new Rp6lAnimationScript(
                    sections.RecordsAndNames,
                    sections.IndexAndNames));
            looseScripts.Add(
                library.ResourceName,
                BuildLooseProjectAnimationScript(
                    library,
                    libraries,
                    localSequences.GetValueOrDefault(libraryId) ?? []));
        }

        byte[] rpack = Rp6lAnimationLibraryCodec.Build(
            animations,
            scripts);
        return new BuiltProjectAnimationPack(
            rpack,
            animations.ToImmutable(),
            scripts.ToImmutable(),
            looseScripts.ToImmutable());

        bool CanReachLibrary(Guid rootLibraryId, Guid targetLibraryId)
        {
            if (rootLibraryId == targetLibraryId)
            {
                return true;
            }

            var visited = new HashSet<Guid>();
            var pending = new Stack<Guid>();
            pending.Push(rootLibraryId);
            while (pending.TryPop(out Guid currentId))
            {
                if (!visited.Add(currentId) ||
                    !libraries.TryGetValue(
                        currentId,
                        out ProjectAnimationLibrary? current))
                {
                    continue;
                }

                foreach (ProjectAnimationLibraryImport import in
                         current.Imports.Where(static import =>
                             import.Kind ==
                                 ProjectAnimationLibraryImportKind.ProjectLibrary))
                {
                    Guid dependencyId = import.ProjectLibraryId!.Value;
                    if (dependencyId == targetLibraryId)
                    {
                        return true;
                    }

                    pending.Push(dependencyId);
                }
            }

            return false;
        }

        ImmutableArray<AnimationScrSequence> ResolveEffectiveSequences(
            Guid libraryId)
        {
            if (compiled.TryGetValue(
                    libraryId,
                    out ImmutableArray<AnimationScrSequence> cached))
            {
                return cached;
            }

            if (!libraries.TryGetValue(
                    libraryId,
                    out ProjectAnimationLibrary? library))
            {
                throw new InvalidDataException(
                    "An animation SCR imports a missing project library.");
            }

            if (!visiting.Add(libraryId))
            {
                throw new InvalidDataException(
                    "Animation SCR imports contain a cycle.");
            }

            if (library.Mode ==
                ProjectAnimationLibraryMode.ExistingScriptExtension)
            {
                throw new InvalidOperationException(
                    $"Animation SCR '{library.ResourceName}' extends a retail script. Export is blocked until the exact fingerprinted base type-322 resource is decoded and preserved; a minimal same-name replacement will not be emitted.");
            }

            var merged = new Dictionary<string, AnimationScrSequence>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ProjectAnimationLibraryImport import in
                     library.Imports)
            {
                if (import.Kind ==
                    ProjectAnimationLibraryImportKind.RetailScript)
                {
                    throw new InvalidOperationException(
                        $"Animation SCR '{library.ResourceName}' imports retail script '{import.RetailScriptIdentity?.ResourceName}'. Export requires that exact fingerprinted type-322 base resource; it cannot be reconstructed from names alone.");
                }

                foreach (AnimationScrSequence sequence in
                         ResolveEffectiveSequences(
                             import.ProjectLibraryId!.Value))
                {
                    Merge(sequence, "imported");
                }
            }

            foreach (AnimationScrSequence sequence in
                     localSequences.GetValueOrDefault(libraryId) ?? [])
            {
                Merge(sequence, "local");
            }

            visiting.Remove(libraryId);
            ImmutableArray<AnimationScrSequence> result = merged.Values
                .OrderBy(static row => row.Name,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            compiled.Add(libraryId, result);
            return result;

            void Merge(AnimationScrSequence sequence, string origin)
            {
                if (!merged.TryAdd(sequence.Name, sequence) &&
                    library.CollisionPolicy ==
                        ProjectAnimationSequenceCollisionPolicy.Reject)
                {
                    throw new InvalidOperationException(
                        $"Animation SCR '{library.ResourceName}' has an unapproved {origin} sequence collision for '{sequence.Name}'. Choose Replace existing explicitly or rename the project clip.");
                }

                if (library.CollisionPolicy ==
                    ProjectAnimationSequenceCollisionPolicy.ReplaceExisting)
                {
                    merged[sequence.Name] = sequence;
                }
            }
        }
    }

    private static string BuildLooseProjectAnimationScript(
        ProjectAnimationLibrary library,
        Dictionary<Guid, ProjectAnimationLibrary> libraries,
        IReadOnlyList<AnimationScrSequence> localSequences)
    {
        var text = new StringBuilder();
        foreach (ProjectAnimationLibraryImport import in library.Imports)
        {
            string name = import.Kind ==
                ProjectAnimationLibraryImportKind.ProjectLibrary
                ? libraries.TryGetValue(
                    import.ProjectLibraryId!.Value,
                    out ProjectAnimationLibrary? dependency)
                    ? dependency.ResourceName
                    : throw new InvalidDataException(
                        $"Animation SCR '{library.ResourceName}' imports a missing project library.")
                : import.RetailScriptIdentity?.ResourceName ??
                  throw new InvalidDataException(
                      $"Animation SCR '{library.ResourceName}' has a missing retail import identity.");
            text.Append("!include(\"")
                .Append(name)
                .Append(".scr\")")
                .AppendLine();
        }

        foreach (AnimationScrSequence sequence in localSequences
                     .OrderBy(static row => row.Name,
                         StringComparer.OrdinalIgnoreCase))
        {
            text.Append("SeqTrack( \"")
                .Append(sequence.Name)
                .Append("\", \"")
                .Append(sequence.Anm2Name)
                .Append("\", ")
                .Append(sequence.StartFrame.ToString(
                    CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.EndFrame.ToString(
                    CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.FramesPerSecond.ToString(
                    CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.Enabled.ToString(
                    CultureInfo.InvariantCulture))
                .Append(", ")
                .Append(sequence.Blend.ToString(
                    CultureInfo.InvariantCulture))
                .AppendLine(" )");
        }

        return text.ToString();
    }

    private static string GetStandaloneExportDirectorySuffix(
        StandaloneExportMode mode) => mode switch
    {
        StandaloneExportMode.Anm2Files => "anm2-files",
        StandaloneExportMode.AnimationRpack => "animation-rpack",
        StandaloneExportMode.CharacterFiles => "character-files",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private async Task<string> PublishStandaloneExportAsync(
        string parentDirectory,
        string suffix,
        ImmutableArray<StandaloneExportArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        const int maximumArtifacts = 16_384;
        const long maximumPayloadBytes = 8L * 1024 * 1024 * 1024;
        const string markerName = ".dl-reanimated-export-owned";
        if (artifacts.IsDefaultOrEmpty ||
            artifacts.Length > maximumArtifacts)
        {
            throw new InvalidDataException(
                $"An export must contain 1-{maximumArtifacts:N0} artifacts.");
        }

        string parent = Path.GetFullPath(parentDirectory);
        Directory.CreateDirectory(parent);
        string outputName = Dl1SourceModelWriter.SanitizeName(
            $"{CreateUnifiedExportPackageName()}-{suffix}",
            96);
        string target = Path.Combine(parent, outputName);
        string transactionId = Guid.NewGuid().ToString("N");
        string staging = Path.Combine(
            parent,
            $".{outputName}.staging-{transactionId}");
        string backup = Path.Combine(
            parent,
            $".{outputName}.backup-{transactionId}");
        string expectedMarker =
            $"dl-reanimated-owned-export-v1\n{_project.ProjectId:N}\n";
        var normalized = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (StandaloneExportArtifact artifact in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeStandaloneArtifactPath(
                artifact.RelativePath);
            if (!normalized.Add(relative))
            {
                throw new InvalidDataException(
                    $"Export artifact path '{relative}' is duplicated.");
            }

            totalBytes = checked(totalBytes + artifact.Payload.LongLength);
            if (totalBytes > maximumPayloadBytes)
            {
                throw new InvalidDataException(
                    "The selected export exceeds the bounded 8 GiB payload limit.");
            }
        }

        if (Directory.Exists(target))
        {
            string markerPath = Path.Combine(target, markerName);
            if (!File.Exists(markerPath) ||
                !string.Equals(
                    await File.ReadAllTextAsync(
                        markerPath,
                        cancellationToken),
                    expectedMarker,
                    StringComparison.Ordinal))
            {
                throw new IOException(
                    $"Output directory '{target}' is not owned by this project and will not be replaced.");
            }
        }

        bool backupReady = false;
        bool published = false;
        try
        {
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(
                Path.Combine(staging, markerName),
                expectedMarker,
                new UTF8Encoding(false),
                cancellationToken);
            var manifestRows = new List<object>(artifacts.Length);
            foreach (StandaloneExportArtifact artifact in artifacts
                         .OrderBy(static row => row.RelativePath,
                             StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = NormalizeStandaloneArtifactPath(
                    artifact.RelativePath);
                string path = ResolveStandaloneArtifactPath(
                    staging,
                    relative);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(path) ?? staging);
                await File.WriteAllBytesAsync(
                    path,
                    artifact.Payload,
                    cancellationToken);
                manifestRows.Add(new
                {
                    relativePath = relative,
                    length = artifact.Payload.LongLength,
                    sha256 = Convert.ToHexStringLower(
                        SHA256.HashData(artifact.Payload)),
                });
            }

            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format = "dl-reanimated-selected-export",
                schemaVersion = 1,
                projectId = _project.ProjectId,
                exportedAtUtc = DateTimeOffset.UtcNow,
                artifacts = manifestRows,
            }, IndentedJsonOptions);
            await File.WriteAllBytesAsync(
                Path.Combine(staging, "export-manifest.json"),
                manifest,
                cancellationToken);

            if (Directory.Exists(target))
            {
                Directory.Move(target, backup);
                backupReady = true;
            }

            Directory.Move(staging, target);
            published = true;
            if (backupReady && Directory.Exists(backup))
            {
                TryDeleteStandaloneTransactionDirectory(backup, parent);
            }

            return target;
        }
        catch
        {
            if (!published && backupReady &&
                !Directory.Exists(target) && Directory.Exists(backup))
            {
                Directory.Move(backup, target);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDeleteStandaloneTransactionDirectory(staging, parent);
            }
        }
    }

    private static string NormalizeStandaloneArtifactPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (Path.IsPathRooted(value) ||
            segments.Length == 0 ||
            segments.Any(static segment => segment is "." or "..") ||
            segments.Any(static segment =>
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw new InvalidDataException(
                $"Export artifact path '{value}' is unsafe.");
        }

        return string.Join('/', segments);
    }

    private static string ResolveStandaloneArtifactPath(
        string root,
        string relativePath)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string resolved = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.StartsWith(
                fullRoot,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "An export artifact resolves outside its staging directory.");
        }

        return resolved;
    }

    private static void TryDeleteStandaloneTransactionDirectory(
        string directory,
        string parent)
    {
        string fullParent = Path.GetFullPath(parent)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        string fullDirectory = Path.GetFullPath(directory);
        if (!fullDirectory.StartsWith(
                fullParent,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fullDirectory.TrimEnd(Path.DirectorySeparatorChar),
                fullParent.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Refusing to remove an export transaction directory outside its validated parent.");
        }

        try
        {
            Directory.Delete(fullDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The published output is already durable. A same-parent backup
            // may remain for manual recovery when antivirus keeps a handle.
        }
        catch (UnauthorizedAccessException)
        {
            // Same recovery behavior as an in-use backup.
        }
    }

    private async Task<ImmutableArray<PreparedCheckedExportModel>>
        PrepareCheckedExportModelsAsync(
            ImmutableHashSet<Guid> selectedVariantIds,
            bool compileCustomModels,
            string stagingRoot,
            JobViewModel job,
            CancellationToken cancellationToken)
    {
        if (selectedVariantIds.IsEmpty)
        {
            throw new InvalidOperationException(
                "No export-ready variants are checked.");
        }

        ProjectAnimationVariant[] selectedVariants = _project
            .AnimationVariants
            .Where(variant => selectedVariantIds.Contains(variant.Id))
            .ToArray();
        if (selectedVariants.Length != selectedVariantIds.Count)
        {
            throw new InvalidDataException(
                "The checked variant inventory changed before export began.");
        }

        AnimationRuntimeSnapshot runtimeSnapshot =
            CaptureAnimationRuntimeSnapshot();
        try
        {
            Dictionary<Guid, ProjectAnimationSource> sources = _project
                .AnimationSources
                .ToDictionary(static source => source.Id);
            var prepared =
                ImmutableArray.CreateBuilder<PreparedCheckedExportModel>();
            IGrouping<Guid, ProjectAnimationVariant>[] modelGroups =
                selectedVariants
                    .GroupBy(static variant => variant.TargetModelId)
                    .OrderBy(static group => group.Key)
                    .ToArray();

            var usedResourceNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            for (var modelIndex = 0;
                 modelIndex < modelGroups.Length;
                 modelIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IGrouping<Guid, ProjectAnimationVariant> group =
                    modelGroups[modelIndex];
                ProjectModelEntry model = _project.Models.FirstOrDefault(
                        candidate => candidate.Id == group.Key)
                    ?? throw new InvalidDataException(
                        "A checked variant target model is missing.");
                ProjectAssetReference asset = FindProjectAsset(
                        model.AssetId)
                    ?? throw new InvalidDataException(
                        $"Checked model '{model.Name}' has no target asset.");
                if (model.IsStatic ||
                    string.IsNullOrWhiteSpace(model.RigSignature) ||
                    string.IsNullOrWhiteSpace(asset.ContentSha256))
                {
                    throw new InvalidDataException(
                        $"Checked model '{model.Name}' is not a fingerprinted rigged animation target.");
                }

                job.Stage = $"Evaluate {model.Name}";
                job.Progress = 5.0 +
                    (55.0 * modelIndex / Math.Max(1, modelGroups.Length));
                var resources =
                    ImmutableArray.CreateBuilder<Dl1PortableAnimationResource>();
                foreach (ProjectAnimationVariant variant in group
                             .OrderBy(static variant => variant.Name,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(static variant => variant.Id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!sources.TryGetValue(
                            variant.SourceId,
                            out ProjectAnimationSource? source) ||
                        source.RequiresSourceRebind)
                    {
                        throw new InvalidDataException(
                            $"Checked variant '{variant.Name}' has no immutable source contract.");
                    }

                    ProjectAssetReference sourceAsset = FindProjectAsset(
                            source.SourceAssetId)
                        ?? throw new InvalidDataException(
                            $"Checked variant '{variant.Name}' source asset is missing.");
                    string sourceFingerprint =
                        source.EmbeddedCustomModelStack
                            ?.StackFingerprint ??
                        sourceAsset.ContentSha256
                        ?? throw new InvalidDataException(
                            $"Checked variant '{variant.Name}' source has no fingerprint.");
                    await ActivateAnimationAsync(
                        variant.Id,
                        beginPlayback: false,
                        persistActivation: false);
                    IsBusy = true;
                    if (_activeAnimationId != variant.Id ||
                        GetActiveAnimation()?.Id != variant.Id ||
                        !CanExportAnimation())
                    {
                        throw new InvalidOperationException(
                            $"Checked variant '{variant.Name}' could not enter an authoritative export-ready runtime state.");
                    }

                    bool hasFacialSource =
                        SourceHasFacialRole(source);
                    bool directSameRig = string.Equals(
                        source.SourceRigSignature,
                        variant.TargetRigSignature,
                        StringComparison.OrdinalIgnoreCase) &&
                        SourceFacialTracksUseOwningRig(source);
                    if (!IsFacialMappingExportReady(
                            hasFacialSource,
                            directSameRig,
                            variant.MorphBindings))
                    {
                        throw new InvalidOperationException(
                            $"Checked variant '{variant.Name}' has unresolved facial mappings.");
                    }

                    Dl1AnimationExportParts selectedParts =
                        SourceHasBodyRole(source)
                            ? hasFacialSource
                                ? Dl1AnimationExportParts.BodyAndMimic
                                : Dl1AnimationExportParts.Body
                            : hasFacialSource
                                ? Dl1AnimationExportParts.Mimic
                                : throw new InvalidOperationException(
                                    $"Checked variant '{variant.Name}' declares no body or facial animation role.");
                    Dl1AnimationExportResult result =
                        await ExportActiveAnimationPayloadAsync(
                            selectedParts,
                            cancellationToken);
                    float framesPerSecond = checked((float)
                        source.FrameRate.FramesPerSecond);
                    if (!float.IsFinite(framesPerSecond) ||
                        framesPerSecond is < 1 or > 240)
                    {
                        throw new InvalidDataException(
                            $"Checked variant '{variant.Name}' has unsupported playback cadence {source.FrameRate.Numerator}/{source.FrameRate.Denominator} FPS.");
                    }
                    if (result.BodyAnm2 is { } body)
                    {
                        string resourceName = AllocateCheckedResourceName(
                            variant.OutputAnm2Name,
                            variant.Name,
                            Dl1PortableAnimationRole.Body,
                            usedResourceNames);
                        resources.Add(new Dl1PortableAnimationResource
                        {
                            VariantId = variant.Id,
                            Name = resourceName,
                            Role = Dl1PortableAnimationRole.Body,
                            Payload = body,
                            FrameCount = Anm2Reader.Read(
                                body,
                                resourceName,
                                cancellationToken: cancellationToken)
                                .Header.FrameCount,
                            FramesPerSecond = framesPerSecond,
                            SourceFingerprint = sourceFingerprint,
                        });
                    }

                    if (result.MimicAnm2 is { } mimic)
                    {
                        string resourceName = AllocateCheckedResourceName(
                            variant.OutputAnm2Name,
                            variant.Name,
                            Dl1PortableAnimationRole.Facial,
                            usedResourceNames);
                        resources.Add(new Dl1PortableAnimationResource
                        {
                            VariantId = variant.Id,
                            Name = resourceName,
                            Role = Dl1PortableAnimationRole.Facial,
                            Payload = mimic,
                            FrameCount = Anm2Reader.Read(
                                mimic,
                                resourceName,
                                cancellationToken: cancellationToken)
                                .Header.FrameCount,
                            FramesPerSecond = framesPerSecond,
                            SourceFingerprint = sourceFingerprint,
                        });
                    }

                    if (result.BodyAnm2 is null &&
                        result.MimicAnm2 is null)
                    {
                        throw new InvalidDataException(
                            $"Checked variant '{variant.Name}' produced no ANM2 artifact.");
                    }
                }

                string libraryName = AllocateCheckedLibraryName(
                    model,
                    ResolveAnimationLibrary(model.RootAnimationLibraryId)
                        ?.ResourceName);
                LoadedProjectCustomModel? customModel = null;
                byte[]? compiledRpack = null;
                Dl1OfficialModelCompilerEvidence? compilerEvidence = null;
                ImmutableArray<Dl1PortableMorphResource>
                    compiledMorphResources = [];
                if (asset.Kind == ProjectAssetKind.CustomModelSource)
                {
                    customModel = await LoadProjectCustomModelAsync(
                        model,
                        asset,
                        cancellationToken);
                    if (compileCustomModels)
                    {
                        job.Stage = $"Official compiler: {model.Name}";
                        CompiledCheckedCustomModel compiled =
                            await CompileCheckedCustomModelAsync(
                                customModel,
                                libraryName,
                                stagingRoot,
                                cancellationToken);
                        compiledRpack = compiled.Rpack;
                        compilerEvidence = compiled.Evidence;
                        compiledMorphResources =
                            CreateCompiledMorphInventory(
                                customModel.Imported);
                    }
                }
                else if (asset.Kind != ProjectAssetKind.RetailGameResource)
                {
                    throw new InvalidDataException(
                        $"Checked model '{model.Name}' has an unsupported target asset kind.");
                }

                prepared.Add(new PreparedCheckedExportModel(
                    model,
                    asset,
                    resources.ToImmutable(),
                    customModel,
                    compiledRpack,
                    compilerEvidence,
                    compiledMorphResources,
                    libraryName));
            }

            return prepared.ToImmutable();
        }
        finally
        {
            RestoreAnimationRuntimeSnapshot(runtimeSnapshot);
        }
    }

    private async Task<LoadedProjectCustomModel>
        LoadProjectCustomModelAsync(
            ProjectModelEntry model,
            ProjectAssetReference asset,
            CancellationToken cancellationToken)
    {
        string packagePath = ResolveLocalProjectAssetPath(asset);
        await VerifyLocalProjectAssetHashAsync(
            asset,
            packagePath,
            "custom-model package",
            cancellationToken);
        CustomModelPackage package = await Task.Run(
            () => CustomModelPackageSerializer.Load(packagePath),
            cancellationToken);
        if (package.Document.ModelId != model.Id)
        {
            throw new InvalidDataException(
                $"Custom model '{model.Name}' package identity differs from its project-model identity.");
        }

        FbxModelAuthoringImportResult imported = await Task.Run(
            () => FbxModelAuthoringImporter.ImportPackage(
                package,
                cancellationToken),
            cancellationToken);
        RigDefinition rig = imported.Rig ??
            throw new InvalidDataException(
                $"Custom model '{model.Name}' no longer has an animation rig.");
        if (!string.Equals(
                RigSignature.Compute(rig),
                model.RigSignature,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Custom model '{model.Name}' rig signature is stale.");
        }

        return new LoadedProjectCustomModel(imported, package);
    }

    private async Task<CompiledCheckedCustomModel>
        CompileCheckedCustomModelAsync(
        LoadedProjectCustomModel customModel,
        string animationLibraryName,
        string stagingRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(Models.CompilerExecutablePath))
        {
            throw new FileNotFoundException(
                "Select Techland's Developer Tools compiler in Models before exporting checked custom models.",
                Models.CompilerExecutablePath);
        }

        string retailData0PakPath = ResolveRetailData0PakPath()
            ?? throw new FileNotFoundException(
                "A complete Dying Light 1 Data0.pak is required for the isolated official-compiler bootstrap.");
        CustomModelBuildSettings settings =
            customModel.Package.Document.BuildSettings;
        string resourceName = Dl1SourceModelWriter.SanitizeName(
            settings.ResourceName,
            55);
        string outputDirectory = Path.Combine(
            stagingRoot,
            $"compiled-{customModel.Package.Document.ModelId:N}");
        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(
            outputDirectory,
            resourceName + "_pc.rpack");
        Dl1OfficialModelCompilerResult result =
            await Dl1OfficialModelCompiler.CompileAsync(
                new Dl1OfficialModelCompilerRequest
                {
                    Model = WithAnimationLibraryBuildSettings(
                        customModel.Imported,
                        animationLibraryName),
                    CompilerExecutablePath =
                        Models.CompilerExecutablePath,
                    RetailData0PakPath = retailData0PakPath,
                    OutputRpackPath = outputPath,
                    CharacterId = string.IsNullOrWhiteSpace(
                        settings.CharacterId)
                            ? resourceName
                            : settings.CharacterId,
                    ResourceName = resourceName,
                    SurfaceName = settings.SurfaceName,
                    AnimationScriptAlias = animationLibraryName,
                },
                cancellationToken);
        FbxModelAuthoringImportResult compiledInput =
            WithAnimationLibraryBuildSettings(
                customModel.Imported,
                animationLibraryName);
        if (!Dl1OfficialModelCompiler.IsCurrentBuildReceipt(
                result.BuildReceipt,
                compiledInput))
        {
            throw new InvalidDataException(
                "The official compiler returned a stale or mismatched custom-model validation receipt.");
        }

        byte[] rpack = await File.ReadAllBytesAsync(
            result.OutputRpackPath,
            cancellationToken);
        string actualRpackSha256 = Convert.ToHexStringLower(
            SHA256.HashData(rpack));
        if (!string.Equals(
                actualRpackSha256,
                result.CompilerEvidence.OutputRpackSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CompilerEvidence.ToolFingerprint,
                result.BuildReceipt.ToolFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CompilerEvidence.CompilerFingerprint,
                result.CompilerFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CompilerEvidence.CompilerFingerprint,
                result.BuildReceipt.CompilerFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CompilerEvidence.BuildReceiptInputFingerprint,
                result.BuildReceipt.InputFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                result.CompilerEvidence
                    .BuildReceiptOutputManifestFingerprint,
                result.BuildReceipt.OutputManifestFingerprint,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The official compiler output bytes or typed evidence differ from its validated build receipt.");
        }

        return new CompiledCheckedCustomModel(
            rpack,
            result.CompilerEvidence);
    }

    internal static ImmutableArray<Dl1PortableMorphResource>
        CreateCompiledMorphInventory(
            FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ImmutableArray<CustomModelMorphChannel> morphs =
            model.Package.Document.MorphChannels;
        if (morphs.IsEmpty)
        {
            return [];
        }

        var rows = ImmutableArray.CreateBuilder<
            Dl1PortableMorphResource>(morphs.Length);
        foreach (CustomModelMorphChannel morph in morphs)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                format =
                    "dl-reanimated-compiled-morph-inventory-reference",
                schemaVersion = 1,
                morph.Index,
                morph.Name,
                descriptorHash = $"0x{morph.DescriptorHash:X8}",
                morph.BlendShapeChannelObjectId,
                morph.ShapeObjectId,
                geometryObjectIds = morph.GeometryObjectIds,
                compiledPayload =
                    "embedded-in-official-compiler-model-rpack",
            });
            rows.Add(new Dl1PortableMorphResource
            {
                RelativePath =
                    $"{morph.Index:D4}-{Dl1SourceModelWriter.SanitizeName(morph.Name, 48)}.morph.json",
                Payload = payload,
            });
        }

        return rows.MoveToImmutable();
    }

    private ImmutableArray<Dl1DeveloperToolsDeploymentRequest>
        CreateDeveloperToolsBatchRequests(
            ImmutableArray<PreparedCheckedExportModel> prepared,
            string projectRoot)
    {
        if (!File.Exists(Models.CompilerExecutablePath))
        {
            throw new FileNotFoundException(
                "Select Techland's Developer Tools compiler in Models before deployment.",
                Models.CompilerExecutablePath);
        }

        string retailData0PakPath = ResolveRetailData0PakPath()
            ?? throw new FileNotFoundException(
                "A complete Dying Light 1 Data0.pak is required for Developer Tools deployment.");
        Dictionary<Guid, ProjectAnimationVariant> variants = _project
            .AnimationVariants
            .ToDictionary(static variant => variant.Id);
        return prepared.Select(model =>
        {
            LoadedProjectCustomModel custom = model.CustomModel ??
                throw new InvalidOperationException(
                    $"Developer Tools model deployment cannot publish retail target '{model.Model.Name}'.");
            CustomModelBuildSettings settings =
                custom.Package.Document.BuildSettings;
            string resourceName = Dl1SourceModelWriter.SanitizeName(
                settings.ResourceName,
                55);
            PreparedCustomModelAnimationLibrary library =
                CreatePreparedTargetVariantLibrary(
                    model.AnimationLibraryName,
                    model.Animations,
                    variants);
            return new Dl1DeveloperToolsDeploymentRequest
            {
                Model = WithAnimationLibraryBuildSettings(
                    custom.Imported,
                    model.AnimationLibraryName),
                ProjectRoot = projectRoot,
                CompilerExecutablePath = Models.CompilerExecutablePath,
                RetailData0PakPath = retailData0PakPath,
                CharacterId = string.IsNullOrWhiteSpace(
                    settings.CharacterId)
                        ? resourceName
                        : settings.CharacterId,
                ModelResourceName = resourceName,
                SurfaceName = settings.SurfaceName,
                AnimationLibraryName = model.AnimationLibraryName,
                PreparedAnimationLibrary = library,
                InstallLooseAnm2 = true,
                ExportPortableAnimationRpack = true,
            };
        }).ToImmutableArray();
    }

    private static FbxModelAuthoringImportResult
        WithAnimationLibraryBuildSettings(
            FbxModelAuthoringImportResult source,
            string animationLibraryName)
    {
        CustomModelDocument document = source.Package.Document with
        {
            BuildSettings = source.Package.Document.BuildSettings with
            {
                AnimationScriptAlias = animationLibraryName,
            },
            LastBuildReceipt = null,
        };
        document.Validate();
        return source with
        {
            Package = new CustomModelPackage(
                document,
                source.Package.SourceFbx,
                source.Package.TexturePayloads),
        };
    }

    internal static PreparedCustomModelAnimationLibrary
        CreatePreparedTargetVariantLibrary(
            string animationLibraryName,
            ImmutableArray<Dl1PortableAnimationResource> resources,
            IReadOnlyDictionary<Guid, ProjectAnimationVariant> variants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(
            animationLibraryName);
        ArgumentNullException.ThrowIfNull(variants);
        string libraryName = Dl1SourceModelWriter.SanitizeName(
            animationLibraryName,
            63);
        if (!string.Equals(
                libraryName,
                animationLibraryName,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The target-variant animation library name is not an exact DL1 resource identity.");
        }
        if (resources.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException(
                "A target-variant animation library cannot be empty.");
        }

        var animations =
            ImmutableArray.CreateBuilder<PreparedCustomModelAnimation>(
                resources.Length);
        var sequences =
            ImmutableArray.CreateBuilder<AnimationScrSequence>(
                resources.Length);
        foreach (Dl1PortableAnimationResource resource in resources)
        {
            if (!variants.TryGetValue(
                    resource.VariantId,
                    out ProjectAnimationVariant? variant))
            {
                throw new InvalidDataException(
                    $"Prepared animation '{resource.Name}' has no persisted target variant.");
            }

            animations.Add(new PreparedCustomModelAnimation(
                resource.Name,
                resource.Name + ".anm2",
                resource.Payload,
                resource.FrameCount,
                resource.FramesPerSecond,
                variant.Name,
                resource.SourceFingerprint,
                variant.RootMotionMode,
                variant.RootBoneName));
            sequences.Add(new AnimationScrSequence(
                resource.Name,
                resource.Name + ".anm2",
                0,
                resource.FrameCount - 1,
                resource.FramesPerSecond));
        }

        ImmutableArray<AnimationScrSequence> sequenceRows =
            sequences.MoveToImmutable();
        return new PreparedCustomModelAnimationLibrary(
            libraryName,
            animations.MoveToImmutable(),
            sequenceRows,
            CustomModelAnimationLibraryExporter
                .BuildLooseAnimationScript(sequenceRows),
            []);
    }

    private ImmutableArray<Dl1DeveloperToolsDeploymentRequest>
        ResolveDeveloperToolsBatchConflicts(
            ImmutableArray<Dl1DeveloperToolsDeploymentRequest> requests,
            Dl1DeveloperToolsBatchPreflight preflight)
    {
        if (!preflight.Conflicts.IsEmpty)
        {
            throw new Dl1DeveloperToolsBatchConflictException(
                preflight);
        }

        var resolved = requests.ToBuilder();
        for (var index = 0;
             index < preflight.Plans.Length;
             index++)
        {
            Dl1DeveloperToolsDeploymentPlan plan =
                preflight.Plans[index];
            var decisions = ImmutableDictionary.CreateBuilder<
                string,
                Dl1DeploymentConflictResolution>(
                StringComparer.OrdinalIgnoreCase);
            foreach (Dl1DeveloperToolsDeploymentConflict conflict in
                     plan.Conflicts)
            {
                DeveloperToolsDeploymentConflictDecision decision =
                    _fileDialogs.ResolveDeveloperToolsDeploymentConflict(
                        conflict.RelativePath,
                        conflict.Message,
                        conflict.CanSkip);
                if (decision ==
                    DeveloperToolsDeploymentConflictDecision.Cancel)
                {
                    throw new OperationCanceledException(
                        "Developer Tools batch conflict resolution was canceled.");
                }

                decisions[conflict.RelativePath] = decision ==
                    DeveloperToolsDeploymentConflictDecision
                        .BackUpAndReplace
                            ? Dl1DeploymentConflictResolution
                                .BackUpAndReplace
                            : Dl1DeploymentConflictResolution.Skip;
            }

            resolved[index] = resolved[index] with
            {
                ConflictResolutions = decisions.ToImmutable(),
            };
        }

        return resolved.MoveToImmutable();
    }

    private static bool SourceHasFacialRole(
        ProjectAnimationSource source) =>
        source.EmbeddedCustomModelStack?.Roles.HasFlag(
            AnimationSourceRoles.Facial) == true ||
        source.SourceBinding?.Roles.HasFlag(
            AnimationSourceRoles.Facial) == true ||
        source.MimicAssetId is not null ||
        source.FacialSourceAssetId is not null;

    private static bool SourceFacialTracksUseOwningRig(
        ProjectAnimationSource source) =>
        source.MimicAssetId is null &&
        source.FacialSourceAssetId is null;

    internal static bool IsFacialMappingExportReady(
        bool hasFacialSource,
        bool directSameRig,
        ImmutableArray<ProjectMorphBinding> bindings)
    {
        if (!hasFacialSource || directSameRig)
        {
            return true;
        }

        return !bindings.IsEmpty &&
               bindings.All(static row =>
                   (row.ReviewOrigin !=
                        ProjectMappingReviewOrigin.Assisted ||
                    string.Equals(
                        row.ScorerVersion,
                        ProjectMorphSuggestionScorer.PolicyVersion,
                        StringComparison.Ordinal)) &&
                   (!row.Enabled || row.IsReviewed && row.IsLocked));
    }

    private static bool SourceHasBodyRole(
        ProjectAnimationSource source)
    {
        AnimationSourceRoles? roles =
            source.EmbeddedCustomModelStack?.Roles ??
            source.SourceBinding?.Roles;
        return roles is null ||
               roles == AnimationSourceRoles.None ||
               roles.Value.HasFlag(AnimationSourceRoles.Body);
    }

    private static string AllocateCheckedResourceName(
        string? outputAnm2Name,
        string variantName,
        Dl1PortableAnimationRole role,
        HashSet<string> usedNames)
    {
        if (string.IsNullOrWhiteSpace(outputAnm2Name) ||
            !string.Equals(
                Path.GetExtension(outputAnm2Name),
                ".anm2",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(outputAnm2Name),
                outputAnm2Name,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Checked variant '{variantName}' needs one stable target-specific .anm2 output name.");
        }

        string configuredStem = Path.GetFileNameWithoutExtension(
            outputAnm2Name);
        string suffix = role == Dl1PortableAnimationRole.Facial
            ? "_mimic"
            : string.Empty;
        string candidate = configuredStem + suffix;
        string normalized = Dl1SourceModelWriter.SanitizeName(
            candidate,
            63);
        if (!string.Equals(
                candidate,
                normalized,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Checked variant '{variantName}' output '{candidate}' is not an exact DL1 resource identity. Rename it in Assign SCR; export never silently rewrites assigned identities.");
        }

        if (!usedNames.Add(candidate))
        {
            throw new InvalidOperationException(
                $"Selected type-320 animation identity '{candidate}' is duplicated. Assign a different stable target-specific ANM2 name; export never silently renames it.");
        }

        return candidate;
    }

    private static string AllocateCheckedLibraryName(
        ProjectModelEntry model,
        string? configuredName)
    {
        if (string.IsNullOrWhiteSpace(configuredName))
        {
            throw new InvalidDataException(
                $"Checked model '{model.Name}' has no root SCR assignment.");
        }

        string normalized = Dl1SourceModelWriter.SanitizeName(
            configuredName,
            63);
        if (!string.Equals(
                configuredName,
                normalized,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Checked model '{model.Name}' root SCR '{configuredName}' is not an exact DL1 resource identity.");
        }

        return configuredName;
    }

    private string CreateUnifiedExportPackageName()
    {
        string name = ProjectPath is null
            ? "dl-reanimated-portable-output"
            : Path.GetFileNameWithoutExtension(ProjectPath) +
              "-portable-output";
        return Dl1SourceModelWriter.SanitizeName(name, 80);
    }

    private static string CreateCheckedExportStagingDirectory()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "DLReAnimated",
            "CheckedExport");
        Directory.CreateDirectory(root);
        string staging = Path.Combine(
            root,
            "job-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        return staging;
    }

    private static void DeleteCheckedExportStagingDirectory(
        string stagingRoot)
    {
        if (Directory.Exists(stagingRoot) &&
            Path.GetFileName(stagingRoot).StartsWith(
                "job-",
                StringComparison.Ordinal))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
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
        OpenSelectedAnimationCommand.NotifyCanExecuteChanged();
        AddAnimationTargetsCommand.NotifyCanExecuteChanged();
        AssignAnimationLibraryCommand.NotifyCanExecuteChanged();
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
        CommitProject(WithUpdatedActiveAnimation(
            _project,
            animation with
            {
                EditLayers = animation.EditLayers.SetItem(
                    layerIndex,
                    updated),
            },
            animationIndex));
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
        if (_batchReviewingMapping)
        {
            return;
        }

        bool targetChanged = args.PropertyName ==
            nameof(BoneMappingViewModel.TargetBone);
        bool policyChanged = args.PropertyName is
            nameof(BoneMappingViewModel.TransferPolicy) or
            nameof(BoneMappingViewModel.TransformComponents);
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
        if (_batchReviewingMapping ||
            sender is not TargetBindReviewViewModel ||
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
                bool becameManual = ReferenceEquals(
                        row,
                        changedRow) &&
                    (mappingIdentityChanged || policyChanged);
                bool explicitReviewChanged =
                    ReferenceEquals(row, changedRow) &&
                    !becameManual;
                rows.Add(new ProjectBoneMapping
                {
                    SourceBoneName =
                        source.Rig.Bones[sourceIndex].Name,
                    TargetBoneName =
                        target.Bones[targetIndex].Name,
                    Method = becameManual
                        ? BoneMappingMethod.Manual.ToString()
                        : row.Status,
                    Confidence = becameManual
                        ? 1.0
                        : row.Confidence,
                    Evidence = becameManual
                        ? "Manual mapping or transfer-policy selection; automatic approval is not allowed."
                        : row.Evidence,
                    ReviewOrigin = becameManual
                        ? ProjectMappingReviewOrigin.None
                        : !row.IsReviewed
                            ? ProjectMappingReviewOrigin.None
                            : explicitReviewChanged
                                ? ProjectMappingReviewOrigin.Explicit
                                : row.ReviewOrigin,
                    ScorerVersion = becameManual ||
                        string.IsNullOrWhiteSpace(row.ScorerVersion)
                            ? "unscored-v1"
                            : row.ScorerVersion,
                    EvidenceFingerprint = becameManual ||
                        string.IsNullOrWhiteSpace(
                            row.EvidenceFingerprint)
                            ? new string('0', 64)
                            : row.EvidenceFingerprint,
                    IsLocked = !becameManual && row.IsLocked,
                    IsReviewed = becameManual
                            ? false
                            : row.IsReviewed,
                    MappingKind = row.MappingKind,
                    TransferPolicy =
                        row.TransferPolicy,
                    ComponentPolicy =
                        row.ComponentPolicy,
                    TransformComponents =
                        row.TransformComponents,
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
            _activeDirectRigBinding = null;
            ProjectAnimation updated = animation with
            {
                BoneMappings = rows.ToImmutableArray(),
                TargetBindReviews = targetBindReviews,
                SourceRigSignature =
                    RigSignature.Compute(source.Rig),
                TargetRigSignature =
                    RigSignature.Compute(target),
                SourceAnimationSkeletonSignature =
                    AnimationSkeletonSignature.Compute(source.Rig),
                TargetAnimationSkeletonSignature =
                    AnimationSkeletonSignature.Compute(target),
                BindingMode = ProjectAnimationBindingMode.Retarget,
                DirectBinding = null,
                BindingEvidenceFingerprint = null,
                BindingPolicyVersion = null,
                MappingFingerprint =
                    RetargetMapFingerprint.Compute(
                        RigSignature.Compute(source.Rig),
                        RigSignature.Compute(target),
                        _targetProjectAsset?.ContentSha256,
                        map),
            };
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                updated,
                animationIndex));
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
                proposedByTarget.TryGetValue(
                    targetIndex,
                    out BoneMapEntry? currentEvidence);
                bool evidenceIsCurrent = currentEvidence is not null &&
                    string.Equals(
                        mapping.ScorerVersion,
                        currentEvidence.ScorerVersion,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        mapping.EvidenceFingerprint,
                        currentEvidence.EvidenceFingerprint,
                        StringComparison.OrdinalIgnoreCase);
                MappingReviewOrigin reviewOrigin =
                    ToRuntimeReviewOrigin(mapping.ReviewOrigin);
                if (reviewOrigin == MappingReviewOrigin.Assisted &&
                    !evidenceIsCurrent)
                {
                    reviewOrigin = MappingReviewOrigin.None;
                }
                bool keepReview = reviewOrigin !=
                        MappingReviewOrigin.None ||
                    mapping.ReviewOrigin ==
                        ProjectMappingReviewOrigin.None &&
                    mapping.IsReviewed;
                BoneMapEntry saved = new(
                    sourceIndex,
                    targetIndex,
                    method,
                    mapping.Confidence,
                    mapping.IsLocked && keepReview,
                    mapping.IsReviewed && keepReview,
                    mapping.MappingKind,
                    mapping.TransferPolicy,
                    mapping.ComponentPolicy,
                    evidenceIsCurrent
                        ? currentEvidence!.Evidence
                        : [],
                    reviewOrigin,
                    mapping.ScorerVersion,
                    mapping.EvidenceFingerprint,
                    mapping.EffectiveTransformComponents);
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
                        proposed.ComponentPolicy,
                        transformComponents:
                            proposed.TransformComponents);
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
        bool isSetupCapableWorkspace =
            ActiveWorkspace == EditorWorkspaceMode.Animate ||
            ActiveWorkspace == EditorWorkspaceMode.RetargetEdit;
        if (isSetupCapableWorkspace &&
            GetActiveAnimation() is not null &&
            TargetViewport.SceneSource.HasExternalPreviewScene)
        {
            SuspendIsolatedBrowsePreview();
        }

        if (isSetupCapableWorkspace)
        {
            // Adding or removing the active clip changes Animate/Retarget from
            // setup presentation to authoritative comparison (or back) even
            // though the selected workspace itself did not change.
            SetWorkspace(
                ActiveWorkspace,
                preserveLegacyCutscene: false);
        }

        OnPropertyChanged(nameof(CurrentProject));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ActiveAnimationLabel));
        OnPropertyChanged(nameof(ActiveSourceModelLabel));
        OnPropertyChanged(nameof(ActiveTargetModelLabel));
        OnPropertyChanged(nameof(TargetBindingStatusText));
        OnPropertyChanged(nameof(TargetPlaybackMessage));
        OnPropertyChanged(nameof(IsTargetPlaybackBlocked));
        OnPropertyChanged(nameof(AnimateWorkspaceHint));
        OnPropertyChanged(nameof(IsRetargetSetupVisible));
        OnPropertyChanged(nameof(RetargetSetupSelectionLabel));
        OnPropertyChanged(nameof(RetargetSetupInstructions));
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
            || !ReferenceEquals(_project, _savedProject)
            || Models.PersistenceRevision != _savedModelsRevision;
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

        bool directPlayback = HasDirectRigContract(
            source.Rig,
            target,
            _activeDirectRigBinding);
        RetargetMap? mapping = directPlayback
            ? null
            : _activeRetargetMap;
        if (!directPlayback && mapping is null)
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
            },
            animation.RootBoneName,
            directRigBinding: _activeDirectRigBinding);
        var cacheKey = new RootMotionTrailCacheKey(
            _activeAnimationId,
            source,
            target,
            mapping,
            _activeDirectRigBinding,
            animation,
            evaluationClip,
            sampleCount);
        return new RootMotionTrailBuildSnapshot(
            cacheKey,
            source,
            target,
            mapping,
            _activeDirectRigBinding,
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
                snapshot.Animation.PreviewMotionAccumulationEnabled,
                snapshot.DirectBinding),
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
        bool direct = HasDirectRigContract(
            key.Source.Rig,
            key.Target,
            _activeDirectRigBinding);
        RetargetMap? expectedMapping = direct
            ? null
            : _activeRetargetMap;
        if (key.ActiveAnimationId != _activeAnimationId ||
            !ReferenceEquals(key.Source, _sourceAnimation) ||
            !ReferenceEquals(key.Target, _targetRig) ||
            !ReferenceEquals(key.Mapping, expectedMapping) ||
            !ReferenceEquals(
                key.DirectBinding,
                direct ? _activeDirectRigBinding : null) ||
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
        ReferenceEquals(first.DirectBinding, second.DirectBinding) &&
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

    private void OpenBoneEditor()
    {
        if (GetActiveAnimation() is null || _targetRig is null)
        {
            StatusText =
                "Bone editing needs an active animation and decoded target model";
            return;
        }

        SetWorkspace(
            EditorWorkspaceMode.RetargetEdit,
            preserveLegacyCutscene: false);
        SelectedExplorerTabIndex = 2;
        SelectedInspectorTabIndex = 1;
        SelectedBone ??= EnumerateSkeletonNodes().FirstOrDefault(
            static node => node.Role == BoneRenderRole.Deform);
        if (SelectedBone is null)
        {
            StatusText =
                "The target model has no selectable decoded deform bones";
            return;
        }

        ShowSkeletonOverlay = true;
        ShowDeformBones = true;
        StatusText =
            $"Editing {SelectedBone.Name}; select another bone in the Skeleton tree or viewport";
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
        if (FrameComparisonPanes(force: true))
        {
            StatusText = "Framed the raw source and DL1 target independently";
            return;
        }

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

    private bool FrameComparisonPanes(bool force)
    {
        if (!IsSourceViewportVisible ||
            PreviewLayout != PreviewLayoutMode.RetargetComparison ||
            _viewportCoordinator.HasTargetPreviewCameraOverride)
        {
            return false;
        }

        string key = string.Join(
            '|',
            _activeAnimationId?.ToString("N") ?? "no-animation",
            _sourceAnimation?.Rig.Id ?? "no-source",
            _targetRig?.Id ?? "no-target");
        if (!force && string.Equals(
                key,
                _lastComparisonFramingKey,
                StringComparison.Ordinal))
        {
            return false;
        }

        RenderFrameSnapshot sourceFrame =
            SourceViewport.SceneSource.CaptureFrame();
        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();
        if (!RenderCameraFraming.TryFrame(
                sourceFrame,
                out RenderCamera sourceCamera) ||
            !RenderCameraFraming.TryFrame(
                targetFrame,
                out RenderCamera targetCamera))
        {
            return false;
        }

        _viewportCoordinator.RestoreOrbitCameras(
            new ViewportOrbitCameraPair(
                sourceCamera,
                targetCamera));
        _lastComparisonFramingKey = key;
        return true;
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
            bool restoreActiveAnimation =
                activeAnimation is not null &&
                CanRestoreAnimationFromLoadedCatalog(
                    activeAnimation,
                    _project.Assets,
                    assets);
            if (restoreActiveAnimation)
            {
                job.Stage = "Animation source";
                job.State =
                    "Restoring the active animation's saved source and target";
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
            StatusText = restoreActiveAnimation &&
                         _sourceAnimation is null
                ? "Loaded the asset catalog, but the active animation could not be restored; see Diagnostics"
                : $"Loaded {assets.Length:N0} Dying Light 1 assets from {catalogSource}";
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

    private void ScheduleProjectModelPreview(
        ProjectModelItemViewModel selected)
    {
        if (!IsModelsWorkspace || _disposed)
        {
            return;
        }

        CancelAutomaticAssetPreview();
        CancellationTokenSource source =
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeSource.Token);
        _automaticAssetPreviewSource = source;
        _automaticAssetPreviewTask =
            PreviewProjectModelAfterDelayAsync(selected, source);
    }

    private async Task PreviewProjectModelAfterDelayAsync(
        ProjectModelItemViewModel selected,
        CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(200, source.Token);
            source.Token.ThrowIfCancellationRequested();
            if (_disposed ||
                !IsModelsWorkspace ||
                SelectedProjectModel?.ModelId != selected.ModelId)
            {
                return;
            }

            await PreviewProjectModelAsync(
                selected,
                source.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer model/browser selection superseded this preview.
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

    private async Task PreviewProjectModelAsync(
        ProjectModelItemViewModel selected,
        CancellationToken cancellationToken)
    {
        ProjectModelEntry model = _project.Models.FirstOrDefault(candidate =>
                candidate.Id == selected.ModelId)
            ?? throw new InvalidDataException(
                "The selected project model is no longer available.");
        ProjectAssetReference asset = FindProjectAsset(model.AssetId)
            ?? throw new InvalidDataException(
                "The selected project model asset is missing.");

        IsBusy = true;
        JobViewModel job = BeginExclusiveAssetDecode(
            $"Preview {model.Name}",
            "Reading fingerprinted project model preview");
        using CancellationTokenRegistration registration =
            cancellationToken.Register(job.Cancel);
        try
        {
            MeshRenderData[] meshes;
            SkeletonRenderData? skeleton;
            if (asset.Kind == ProjectAssetKind.CustomModelSource)
            {
                PreparedCustomTarget custom =
                    await DecodeCustomModelTargetAsync(
                        asset,
                        job.CancellationToken);
                meshes = custom.Meshes;
                skeleton = custom.Skeleton;
                _isolatedBrowsePreviewModel = null;
                ClearBlenderExportTarget();
            }
            else if (asset.Kind ==
                     ProjectAssetKind.RetailGameResource)
            {
                (Dl1MeshPreviewPayload payload,
                    RetailAssetRecord retail) =
                    await DecodeProjectModelAsync(
                        asset,
                        job.CancellationToken);
                meshes = CreatePreviewMeshes(payload);
                skeleton = payload.Skeleton;
                _isolatedBrowsePreviewModel =
                    new DecodedRetailModelSession(
                        payload,
                        retail,
                        asset,
                        meshes);
                SetBlenderExportTarget(payload, retail);
            }
            else
            {
                throw new InvalidDataException(
                    "The selected project model has an unsupported asset kind.");
            }

            if (_disposed ||
                !ReferenceEquals(_assetDecodeJob, job) ||
                SelectedProjectModel?.ModelId != selected.ModelId)
            {
                job.Complete("Superseded");
                return;
            }

            long generation = Interlocked.Increment(
                ref _previewGeneration);
            if (_isolatedBrowsePreviewFrame is null)
            {
                _authoringOrbitCameras = _viewportCoordinator
                    .CaptureOrbitCameras();
            }

            RenderFrameSnapshot authored = TargetViewport.SceneSource
                .CaptureFrame();
            _isolatedBrowsePreviewFrame = authored with
            {
                Meshes = meshes,
                Skeleton = skeleton,
                Gizmos = [],
                MorphWeights = [],
                Generation = generation,
                FppProjectionState = null,
                AuthoringOverlays =
                    RenderAuthoringOverlayState.Disabled,
            };
            _isolatedBrowsePreviewTitle =
                $"Project Model - {model.Name}";
            _isolatedBrowsePreviewFidelity =
                $"Fingerprint-validated {selected.Source.ToLowerInvariant()}; active animation and target variants unchanged";
            TargetViewport.SceneSource.SetExternalPreviewScene(
                _isolatedBrowsePreviewFrame);
            TargetViewport.SetPresentation(
                _isolatedBrowsePreviewTitle,
                _isolatedBrowsePreviewFidelity);
            UpdateIsolatedPreviewPresentation();
            FrameIsolatedBrowsePreview();
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"Previewing project model {model.Name}; animation targets unchanged";
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
            UnauthorizedAccessException or
            OverflowException)
        {
            job.Complete("Failed");
            AddDiagnostic(
                "Error",
                "Models",
                $"Could not preview project model {model.Name}",
                exception.Message);
            StatusText = "Project model preview failed";
        }
        finally
        {
            if (ReferenceEquals(_assetDecodeJob, job))
            {
                _assetDecodeJob = null;
                IsBusy = false;
            }
        }
    }

    private async Task AddSelectedModelToProjectAsync()
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
        IsBusy = true;
        JobViewModel job = BeginExclusiveAssetDecode(
            $"Add {selected.Name}",
            "Validating project model identity");
        try
        {
            DecodedRetailModelSession decoded =
                await DecodeRetailModelAsync(selected, job);
            ProjectAssetReference asset =
                FindMatchingProjectRetailAsset(decoded.ProjectAsset) ??
                decoded.ProjectAsset;
            ProjectModelEntry? existing = _project.Models
                .FirstOrDefault(model => model.AssetId == asset.Id);
            if (existing is not null)
            {
                CommitProject(_project with
                {
                    Workflow = _project.Workflow with
                    {
                        SelectedModelId = existing.Id,
                    },
                });
                job.Progress = 100.0;
                job.Complete("Already in project");
                StatusText =
                    $"{existing.Name} is already in the project model library; animation targets were unchanged";
                return;
            }

            var model = new ProjectModelEntry
            {
                Name = selected.Name,
                AssetId = asset.Id,
                RigSignature = decoded.Payload.Source.Rig is { } rig
                    ? RigSignature.Compute(rig)
                    : null,
                IsStatic = decoded.Payload.Source.Rig is null,
            };
            ImmutableArray<ProjectAssetReference> assets =
                _project.Assets.Any(candidate => candidate.Id == asset.Id)
                    ? _project.Assets
                    : _project.Assets.Add(asset);
            DlraProject updated = _project with
            {
                Assets = assets,
                Models = _project.Models.Add(model),
                Workflow = _project.Workflow with
                {
                    SelectedModelId = model.Id,
                },
            };
            updated.Validate();
            CommitProject(updated);
            job.Progress = 100.0;
            job.Complete("Added");
            StatusText =
                $"Added {selected.Name} to the project model library; selection did not change any animation target";
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
            AddDiagnostic(
                "Error",
                "Models",
                $"Could not add {selected.Name} to the project",
                exception.Message);
            StatusText = "Project model was not added";
        }
        finally
        {
            if (ReferenceEquals(_assetDecodeJob, job))
            {
                _assetDecodeJob = null;
            }

            IsBusy = false;
        }
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
            bool retainCurrentSetupWorkspace =
                IsModelsWorkspace ||
                ActiveWorkspace == EditorWorkspaceMode.Animations ||
                (GetActiveAnimation() is null &&
                 (ActiveWorkspace == EditorWorkspaceMode.Animate ||
                  ActiveWorkspace == EditorWorkspaceMode.RetargetEdit));
            if (!retainCurrentSetupWorkspace)
            {
                SetWorkspace(
                    EditorWorkspaceMode.Models,
                    preserveLegacyCutscene: false);
            }
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
            _isolatedBrowsePreviewModel = model;
            _isolatedBrowsePreviewTitle =
                $"Asset Preview - {selected.Name}";
            _isolatedBrowsePreviewFidelity =
                $"Isolated retail asset; {model.PreviewMeshes.Length:N0} draw surface(s); active animation unchanged";
            TargetViewport.SceneSource.SetExternalPreviewScene(
                _isolatedBrowsePreviewFrame);
            TargetViewport.SetPresentation(
                _isolatedBrowsePreviewTitle,
                _isolatedBrowsePreviewFidelity);
            UpdateIsolatedPreviewPresentation();
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
                (!IsModelsWorkspace &&
                 ActiveWorkspace != EditorWorkspaceMode.Animations &&
                 !(GetActiveAnimation() is null &&
                   ActiveWorkspace is
                       EditorWorkspaceMode.Animate or
                       EditorWorkspaceMode.RetargetEdit)) ||
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
            _sourceModelContext = CreateProjectModelSession(model);
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
            else if (_pendingLocalAnm2ImportPath is { } localAnm2Path)
            {
                _pendingLocalAnm2ImportPath = null;
                OnPropertyChanged(
                    nameof(IsExplorerSourceModelPickerActive));
                OnPropertyChanged(
                    nameof(ExplorerSourceModelPickerPrompt));
                CancelExplorerSourceModelPickerCommand
                    .NotifyCanExecuteChanged();
                if (ReferenceEquals(_assetDecodeJob, job))
                {
                    _assetDecodeJob = null;
                }

                IsBusy = false;
                await ImportAnimationPathAsync(
                    localAnm2Path,
                    CreateProjectModelSession(model));
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

    private bool CanUseSelectedProjectModelAsSource() =>
        !IsBusy &&
        SelectedProjectModel is { } selected &&
        _project.Models.FirstOrDefault(model =>
            model.Id == selected.ModelId) is
        {
            IsStatic: false,
            RigSignature.Length: > 0,
        };

    private async Task UseSelectedProjectModelAsSourceAsync()
    {
        if (SelectedProjectModel is not { } selected)
        {
            return;
        }

        ProjectModelEntry modelEntry = _project.Models.FirstOrDefault(
                model => model.Id == selected.ModelId)
            ?? throw new InvalidDataException(
                "The selected project model is no longer available.");
        if (modelEntry.IsStatic ||
            string.IsNullOrWhiteSpace(modelEntry.RigSignature))
        {
            StatusText =
                "Static project models cannot interpret ANM2 tracks";
            return;
        }

        ProjectAssetReference asset = FindProjectAsset(modelEntry.AssetId)
            ?? throw new InvalidDataException(
                "The selected project model asset is missing.");
        IsBusy = true;
        JobViewModel job = AddJob(
            $"Use {modelEntry.Name} as source",
            "Project model",
            "Validating immutable source-model candidate");
        try
        {
            DecodedProjectModelSession sourceModel;
            if (asset.Kind == ProjectAssetKind.CustomModelSource)
            {
                sourceModel = CreateProjectModelSession(
                    await DecodeCustomModelTargetAsync(
                        asset,
                        job.CancellationToken));
                ClearBlenderExportTarget();
            }
            else if (asset.Kind == ProjectAssetKind.RetailGameResource)
            {
                (Dl1MeshPreviewPayload payload,
                    RetailAssetRecord retail) =
                        await DecodeProjectModelAsync(
                            asset,
                            job.CancellationToken);
                sourceModel = CreateProjectModelSession(
                    new DecodedRetailModelSession(
                        payload,
                        retail,
                        asset,
                        CreatePreviewMeshes(payload)));
                SetBlenderExportTarget(payload, retail);
            }
            else
            {
                throw new InvalidDataException(
                    "The selected project model has an unsupported asset kind.");
            }

            string decodedSignature = RigSignature.Compute(sourceModel.Rig);
            if (!string.Equals(
                    decodedSignature,
                    modelEntry.RigSignature,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The selected project model's decoded rig differs from its saved contract. Reimport or review the model before using it as an ANM2 source.");
            }

            _sourceModelContext = sourceModel;
            OnPropertyChanged(nameof(ActiveSourceModelLabel));
            SetSourcePreviewScene(
                sourceModel.PreviewMeshes,
                sourceModel.Skeleton);
            SourceViewport.SetPresentation(
                "Source Model",
                "Explicit fingerprinted project-model source; the animation target is unchanged");
            job.Progress = 100.0;
            job.Complete("Complete");
            StatusText =
                $"{modelEntry.Name} is the explicit source model for the pending ANM2 clip";

            AssetItemViewModel? pendingRetailAnimation =
                _pendingExplorerAnimationSourceChoice;
            string? pendingLocalPath = _pendingLocalAnm2ImportPath;
            _pendingExplorerAnimationSourceChoice = null;
            _pendingLocalAnm2ImportPath = null;
            OnPropertyChanged(nameof(IsExplorerSourceModelPickerActive));
            OnPropertyChanged(nameof(ExplorerSourceModelPickerPrompt));
            CancelExplorerSourceModelPickerCommand.NotifyCanExecuteChanged();
            UseSelectedProjectModelAsSourceCommand.NotifyCanExecuteChanged();
            IsBusy = false;
            if (pendingRetailAnimation is not null)
            {
                AssetBrowser.SelectedAsset = pendingRetailAnimation;
                await PlaySelectedExplorerAnimationAsync();
            }
            else if (pendingLocalPath is not null)
            {
                await ImportAnimationPathAsync(
                    pendingLocalPath,
                    sourceModel);
            }
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
                "Animation source",
                $"Could not use {modelEntry.Name} as the source model",
                exception.Message);
            StatusText = "Project source-model selection failed";
        }
        finally
        {
            IsBusy = false;
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
                _sourceModelContext?.Rig ??
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
                TargetAnimationSkeletonSignature =
                    AnimationSkeletonSignature.Compute(targetRig),
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

            DirectRigCompatibilityResult compatibility =
                DirectRigBindingAnalyzer.Analyze(
                    sourceRig,
                    targetRig,
                    _sourceAnimation!.Clip);
            bool direct = compatibility.IsDirect;
            DirectRigBinding? directBinding = compatibility.Binding;
            RetargetMap? mapping;
            ProjectAnimation activated;
            ImmutableArray<ProjectAnimation> animations =
                _project.Animations;
            if (exactVariant is not null)
            {
                activated = exactVariant with
                {
                    VariantGroupId = variantGroupId,
                    SourceAnimationSkeletonSignature =
                        AnimationSkeletonSignature.Compute(sourceRig),
                    TargetAnimationSkeletonSignature =
                        AnimationSkeletonSignature.Compute(targetRig),
                    BindingMode = compatibility.Kind switch
                    {
                        DirectRigCompatibilityKind.ExactDirect =>
                            ProjectAnimationBindingMode.ExactDirect,
                        DirectRigCompatibilityKind.CompatibleDirect =>
                            ProjectAnimationBindingMode.CompatibleDirect,
                        _ => ProjectAnimationBindingMode.Retarget,
                    },
                    DirectBinding = directBinding,
                    BindingEvidenceFingerprint =
                        directBinding?.EvidenceFingerprint,
                    BindingPolicyVersion = directBinding?.Policy,
                    MappingFingerprint = direct ? null :
                        exactVariant.MappingFingerprint,
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
                    mapping,
                    directBinding);
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
                        _activeDirectRigBinding = directBinding;
                        SetTargetBindingStatus(bindingStatus);
                        RefreshProjectBindings();
                        if (mapping is not null)
                        {
                            PublishMappingProposal(mapping);
                        }
                        else
                        {
                            MappingReviewStatus = direct
                                ? directBinding is null
                                    ? "Exact source/target runtime rig: direct local-transform playback; PoseRetargeter is bypassed."
                                    : "Compatible animation skeleton: deterministic direct binding; PoseRetargeter is bypassed."
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
        RetargetMap? proposal,
        DirectRigBinding? directBinding = null)
    {
        ArgumentNullException.ThrowIfNull(sourceVariant);
        ArgumentNullException.ThrowIfNull(targetAsset);
        ArgumentNullException.ThrowIfNull(targetRig);
        ArgumentNullException.ThrowIfNull(sourceRig);
        string sourceSignature = RigSignature.Compute(sourceRig);
        string targetSignature = RigSignature.Compute(targetRig);
        bool exactDirect = string.Equals(
            sourceSignature,
            targetSignature,
            StringComparison.OrdinalIgnoreCase);
        if (directBinding is not null)
        {
            directBinding.ValidateFor(sourceRig, targetRig);
        }

        if (proposal is not null && (exactDirect || directBinding is not null))
        {
            throw new ArgumentException(
                "A direct target variant cannot also store a retarget proposal.",
                nameof(proposal));
        }
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
            SourceAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(sourceRig),
            TargetAnimationSkeletonSignature =
                AnimationSkeletonSignature.Compute(targetRig),
            BindingMode = exactDirect
                ? ProjectAnimationBindingMode.ExactDirect
                : directBinding is not null
                    ? ProjectAnimationBindingMode.CompatibleDirect
                    : ProjectAnimationBindingMode.Retarget,
            DirectBinding = directBinding,
            BindingEvidenceFingerprint =
                directBinding?.EvidenceFingerprint,
            BindingPolicyVersion = directBinding?.Policy,
            RootBoneName = null,
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
        RetargetMap? mapping,
        DirectRigBinding? directBinding = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (HasDirectRigContract(source, target, directBinding))
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
        AddSelectedModelToProjectCommand.NotifyCanExecuteChanged();
        UseSelectedAssetAsSourceCommand.NotifyCanExecuteChanged();
        UseSelectedAssetAsTargetCommand.NotifyCanExecuteChanged();
        ExportSelectedBrowserMeshToFbxCommand
            .NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RetargetSetupSelectionLabel));
        OnPropertyChanged(nameof(RetargetSetupInstructions));
        if (_disposed || selected is null)
        {
            return;
        }

        if (selected.Kind == AssetKind.Mesh &&
            selected.RetailAsset is not null &&
            (IsModelsWorkspace ||
             ActiveWorkspace == EditorWorkspaceMode.Animations ||
             (GetActiveAnimation() is null &&
              (ActiveWorkspace == EditorWorkspaceMode.Animate ||
            ActiveWorkspace == EditorWorkspaceMode.RetargetEdit))))
        {
            StatusText =
                $"Selected {selected.Name}; loading an isolated model preview";
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
        ClearCustomTargetPreviewPresentation();
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
            int publishedMorphCount = previewMeshes
                .SelectMany(static mesh => mesh.MorphTargets)
                .Select(static target => target.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int nameOnlyMorphCount =
                payload.Source.MorphTargets.Count - decodedMorphCount;
            AddDiagnostic(
                publishedMorphCount > 0 ? "Info" : "Warning",
                "Facial",
                $"{payload.MorphChannelNames.Count:N0} morph names; {publishedMorphCount:N0} bounded position-delta targets available for preview",
                publishedMorphCount > 0
                    ? $"The codec decoded {decodedMorphCount:N0} target payloads and the preview boundary admitted {publishedMorphCount:N0} after finite displacement checks. {nameOnlyMorphCount:N0} inventory channels are name-only for this resource. Facial preview remains DL1 profile until matching Windows 1.55 visual captures are approved."
                    : $"The codec decoded {decodedMorphCount:N0} target payloads, but none passed the bounded renderer-safety contract. Named inventory remains available for non-destructive authoring; moving it cannot deform the retail mesh until the remaining compact-payload unit/layout contract is proven.");
        }

        if (evidenceClassifiedFppHands)
        {
            AddDiagnostic(
                "Info",
                "FPP",
                "Retail mesh uses the explicit FPP-hands projection role",
                "The resource identity has a high-confidence explicit FPP token. A captured hands frustum is used when available; otherwise the EyeCamera pane retains a clearly labeled ordinary-scene-projection fallback instead of hiding the mesh.");
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
                "Playback is blocked. Select an exact-signature fingerprinted model and use Rebind Source; the existing authored document will not be mutated.");
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
                    "The selected model is an exact-signature source candidate, but its fingerprinted identity differs",
                    "Use Rebind Source to create a new clean animation document. The existing document remains bound to its saved model identity.");
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
                $"{imported.Partition.UnresolvedDescriptors.Length:N0} saved descriptors do not exist in the exact source rig",
                string.Join(
                    ", ",
                    imported.Partition.UnresolvedDescriptors
                        .Take(12)
                        .Select(static value => $"0x{value:X8}")));
        }

        RefreshProjectBindings();
        RestoreOrCreateRetargetMap();
        StatusText =
            $"Loaded saved ANM2 source {Path.GetFileName(sourcePath)} against its exact fingerprinted rig";
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

        EnsureExactFacialTarget(
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

        EnsureExactFacialTarget(
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
            FbxFacialProjectReviewService
                .IsTargetInventoryProfileId(profileId)
                ? FbxFacialProjectReviewService
                    .CreateTargetInventoryProfile(targetRig)
                : Dl1MimicProfileCodec.ReadBuiltInCommon46();
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
                "The saved facial FBX mapping fingerprint no longer matches its exact target rig, body timing, and reviewed rows.");
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
            $"{facialClip.FrameCount:N0} frames at {facialClip.FrameRate.Numerator}/{facialClip.FrameRate.Denominator} fps; project-relative SHA-256, exact fingerprinted target, profile, and mapping fingerprint were verified before preview.");
    }

    private void RestoreOrCreateRetargetMap()
    {
        AutoMapCommand.NotifyCanExecuteChanged();
        if (_sourceAnimation is not null &&
            _targetRig is not null)
        {
            ProjectAnimation? animation = GetActiveAnimation();
            DirectRigBinding? savedDirect = animation?.DirectBinding;
            if (HasDirectRigContract(
                    _sourceAnimation.Rig,
                    _targetRig,
                    savedDirect))
            {
                _activeRetargetMap = null;
                _activeDirectRigBinding = savedDirect;
                RefreshProjectBindings();
                MappingReviewStatus =
                    savedDirect is null
                        ? "Exact source/target runtime rig: direct local-transform playback; PoseRetargeter is bypassed."
                        : "Compatible animation skeleton: deterministic direct binding; PoseRetargeter is bypassed.";
                RefreshAnimationPreview();
                NotifyMappingCommands();
                return;
            }

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
                _activeDirectRigBinding = null;
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
            CommitProject(WithUpdatedActiveAnimation(
                _project with
                {
                    Assets = assets,
                },
                updated,
                animationIndex));
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                updatedAnimation,
                animationIndex));
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
        CommitProject(WithUpdatedActiveAnimation(
            _project,
            updated,
            animationIndex));
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

    internal static bool CanRestoreAnimationFromLoadedCatalog(
        ProjectAnimation animation,
        IReadOnlyList<ProjectAssetReference> projectAssets,
        IReadOnlyList<AssetItemViewModel> catalogAssets)
    {
        ArgumentNullException.ThrowIfNull(animation);
        ArgumentNullException.ThrowIfNull(projectAssets);
        ArgumentNullException.ThrowIfNull(catalogAssets);
        if (animation.SourceBinding is not { } binding ||
            catalogAssets.Count == 0)
        {
            return false;
        }

        Dictionary<Guid, ProjectAssetReference> assets = projectAssets
            .ToDictionary(static asset => asset.Id);
        List<ProjectAssetReference> requiredRetailAssets = [];
        if (binding.Kind == AnimationSourceKind.RetailAnm2)
        {
            if (!assets.TryGetValue(
                    animation.SourceAssetId,
                    out ProjectAssetReference? retailAnimation))
            {
                return false;
            }

            requiredRetailAssets.Add(retailAnimation);
        }

        if (binding.Kind is
                AnimationSourceKind.LocalAnm2 or
                AnimationSourceKind.RetailAnm2)
        {
            if (binding.RetailSourceModelAssetId is not { } modelId ||
                !assets.TryGetValue(
                    modelId,
                    out ProjectAssetReference? sourceModel))
            {
                return false;
            }

            requiredRetailAssets.Add(sourceModel);
        }

        if (animation.TargetAssetId is { } targetId)
        {
            if (!assets.TryGetValue(
                    targetId,
                    out ProjectAssetReference? target))
            {
                return false;
            }

            if (target.Kind == ProjectAssetKind.RetailGameResource)
            {
                requiredRetailAssets.Add(target);
            }
        }

        // A local FBX with no target needs no retail lookup and is restored by
        // LoadActiveSourceAsync. Everything listed here must resolve by exact
        // retail identity; names and the currently selected browser row are
        // never accepted as substitutes.
        return requiredRetailAssets.Count > 0 &&
               requiredRetailAssets.All(asset =>
                   FindRetailCatalogAsset(
                       asset,
                       catalogAssets) is not null);
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

    internal static bool ProjectModelAssetsMatch(
        ProjectAssetReference? left,
        ProjectAssetReference? right)
    {
        if (left is null || right is null || left.Kind != right.Kind)
        {
            return false;
        }

        if (left.Kind == ProjectAssetKind.RetailGameResource)
        {
            return ProjectRetailAssetsMatch(left, right);
        }

        return left.Kind == ProjectAssetKind.CustomModelSource &&
            string.Equals(
                left.ContentSha256,
                right.ContentSha256,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                left.ResourceId,
                right.ResourceId,
                StringComparison.Ordinal);
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
            CommitProject(WithUpdatedActiveAnimation(
                _project,
                updatedAnimation,
                animationIndex));
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

        ProjectAnimation? schemaAnimation =
            GetRuntimeAnimation(destination.AnimationId);
        if (schemaAnimation is not null)
        {
            animation = schemaAnimation;
            animationIndex = -1;
            frame = destination.Frame;
            preferredLayerId = destination.PreferredLayerId;
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

    private void RefreshAnimationPreview(bool throwOnFailure = false)
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
            (!HasDirectRigContract(
                 source.Rig,
                 target,
                 _activeDirectRigBinding) &&
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

        bool directTarget = HasDirectRigContract(
            source.Rig,
            target,
            _activeDirectRigBinding);
        if (!directTarget)
        {
            SetTargetBindingStatus(ResolveTargetBindingStatus(
                source.Rig,
                target,
                _activeRetargetMap,
                _activeDirectRigBinding));
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
                _customTargetPreviewSession is { } customPreview
                    ? customPreview.CreateSkeleton(
                        frame.DisplayPose,
                        SelectedBone?.Index,
                        frame.ActorWorldTransform)
                    : CorePreviewAdapter.ToRenderSkeleton(
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
            FrameComparisonPanes(force: false);
            SetTargetBindingStatus(
                directTarget
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
                directTarget
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

            if (throwOnFailure)
            {
                throw;
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
                HasDirectRigContract(
                    source.Rig,
                    target,
                    _activeDirectRigBinding)
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
                _customTargetPreviewSession is { } customPreview
                    ? customPreview.CreateSkeleton(
                        target.CreateBindPose(),
                        SelectedBone?.Index)
                    : CorePreviewAdapter.ToRenderSkeleton(
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
        UpdateTargetPreviewPresentation();
        if (!_customTargetUsesSourcePreviewFallback)
        {
            TargetViewport.SetPresentation(
                "DL1 Target",
                $"{message}; bind pose is held to prevent unsafe deformation");
        }
        FrameComparisonPanes(force: false);
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
        IsSourceViewportVisible = PreviewLayout is
            PreviewLayoutMode.RetargetComparison or
            PreviewLayoutMode.FacialComparison or
            PreviewLayoutMode.FppDualView;
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
        UpdateTargetPreviewPresentation();
    }

    private void SetCustomTargetPreviewPresentation(
        PreparedCustomTarget target)
    {
        _customTargetUsesSourcePreviewFallback =
            target.UsesSourceFallback;
        _customTargetPreviewDiagnostic = target.PreviewDiagnostics
            .IsDefaultOrEmpty
                ? null
                : string.Join("; ", target.PreviewDiagnostics);
        _customTargetPreviewSession = target.PreviewSession;
    }

    private void ClearCustomTargetPreviewPresentation()
    {
        _customTargetUsesSourcePreviewFallback = false;
        _customTargetPreviewDiagnostic = null;
        _customTargetPreviewSession = null;
    }

    private void UpdateTargetPreviewPresentation()
    {
        if (UsesLinkedTargetExternalView())
        {
            return;
        }

        if (_targetProjectAsset?.Kind ==
            ProjectAssetKind.CustomModelSource)
        {
            if (_customTargetUsesSourcePreviewFallback)
            {
                TargetViewport.SetPresentation(
                    "Target / Source FBX fallback",
                    string.IsNullOrWhiteSpace(
                        _customTargetPreviewDiagnostic)
                        ? "DL1-output preparation failed; the Source FBX presentation is shown and target-fidelity playback is unavailable."
                        : "DL1-output preparation failed; Source FBX presentation shown. " +
                          _customTargetPreviewDiagnostic);
                return;
            }

            TargetViewport.SetPresentation(
                TargetPaneTitle,
                "Custom-model DL1-output UVs, material bindings, authored hierarchy, and embedded textures");
            return;
        }

        TargetViewport.SetPresentation(
            TargetPaneTitle,
            TargetPaneFidelity);
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
                "No target rig is loaded.");
        ValidateActiveSourceBinding(animation, source);
        bool directPlayback = HasDirectRigContract(
            source.Rig,
            target,
            _activeDirectRigBinding);
        RetargetMap? mapping = directPlayback
            ? null
            : _activeRetargetMap ??
              throw new InvalidOperationException(
                  "Cross-rig playback is unavailable until a valid source-to-target mapping exists.");
        if (!directPlayback)
        {
            RetargetMappingReviewReport review =
                RetargetMappingReview.Analyze(
                    source.Rig,
                    target,
                    mapping!);
            if (!review.IsReady &&
                purpose == EvaluationPurpose.Export)
            {
                throw new InvalidOperationException(
                    "Retarget review is required before export. " +
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
            },
            animation.RootBoneName,
            directRigBinding: _activeDirectRigBinding);
        ImmutableArray<MorphChannelBinding> morphBindings =
            HasSameRigContract(source.Rig, target) &&
            !HasSavedFacialSource(animation)
                ? []
                : ProjectMorphBindingResolver.Resolve(
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
                animation.PreviewMotionAccumulationEnabled,
            directRigBinding: _activeDirectRigBinding);
    }

    private static bool HasSameRigContract(
        RigDefinition source,
        RigDefinition target) =>
        string.Equals(
            RigSignature.Compute(source),
            RigSignature.Compute(target),
            StringComparison.OrdinalIgnoreCase);

    private static bool HasDirectRigContract(
        RigDefinition source,
        RigDefinition target,
        DirectRigBinding? directBinding)
    {
        if (HasSameRigContract(source, target))
        {
            return true;
        }

        if (directBinding is null)
        {
            return false;
        }

        try
        {
            directBinding.ValidateFor(source, target);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

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
                    "The saved mimic project asset has not been hash-checked, decoded, and synchronized against the exact target rig.");
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
                    "The saved facial FBX has not been hash-checked, decoded, and synchronized against the exact body timeline and target rig.");
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
            SourceViewport.SetDiagnosticOverlay(null);
            baseline = PreviewProfile.MovieAuthoring;
            return ApplySavedGameValidationEvidence(baseline);
        }

        if (ActiveWorkspaceMode != "FPP")
        {
            SourceViewport.SetDiagnosticOverlay(null);
            baseline = PreviewProfile.ThirdPersonAuthoring;
            return ApplySavedGameValidationEvidence(baseline);
        }

        baseline = PreviewProfile.FirstPersonAuthoring;
        string previewCameraNode =
            ResolveTargetPreviewCameraNodeName();
        AuthoringPreviewFidelity fidelity = baseline.Fidelity;
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
            string.Equals(
                previewCameraNode,
                Dl1PreviewContract.EyeCameraBoneName,
                StringComparison.Ordinal)
                ? baseline.Id
                : "project_model_preview_camera",
            baseline.ViewMode,
            fidelity,
            baseline.VisualStyle,
            previewCameraNode,
            new CameraLens(
                FacialFpp.FieldOfView,
                baseline.CameraLens.AspectRatio,
                FacialFpp.NearPlane,
                baseline.CameraLens.FarClipMeters),
            baseline.CameraOffset,
            baseline.FidelityTier,
            string.Equals(
                previewCameraNode,
                Dl1PreviewContract.EyeCameraBoneName,
                StringComparison.Ordinal)
                ? baseline.Context
                : Dl1PreviewContext.Raw,
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
            profile.Context != Dl1PreviewContext.Dl1Movie;
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
            // Playback's FPP toggle follows the target model's persisted
            // editor camera. EyeCamera remains a separate export contract.
            FacialFpp.UseFppCamera = true;
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

        return new PreviewProfile(
            "raw_fpp_authoring",
            PreviewViewMode.Split,
            fidelity,
            PreviewVisualStyle.UnlitDiagnostic,
            ResolveTargetPreviewCameraNodeName(),
            new CameraLens(
                FacialFpp.FieldOfView,
                PreviewProfile.RawAuthoring.CameraLens.AspectRatio,
                FacialFpp.NearPlane,
                PreviewProfile.RawAuthoring.CameraLens.FarClipMeters),
            TransformTRS.Identity,
            PreviewFidelityTier.Raw,
            Dl1PreviewContext.Raw);
    }

    private string ResolveTargetPreviewCameraNodeName()
    {
        ProjectModelEntry? activeModel =
            _targetProjectAsset is { } targetAsset
                ? _project.Models.FirstOrDefault(model =>
                    model.AssetId == targetAsset.Id)
                : null;
        return ResolvePlaybackPreviewCameraNodeName(activeModel);
    }

    internal static string ResolvePlaybackPreviewCameraNodeName(
        ProjectModelEntry? activeModel) =>
        string.IsNullOrWhiteSpace(activeModel?.PreviewCameraNodeName)
            ? Dl1PreviewContract.EyeCameraBoneName
            : activeModel.PreviewCameraNodeName;

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
            _viewportCoordinator.SetPreviewCameraOverride(
                ViewportSide.Source,
                null);
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

        if (ActiveWorkspaceMode != "FPP")
        {
            _viewportCoordinator.SetPreviewCameraOverride(
                ViewportSide.Source,
                null);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            FacialFpp.PreviewStatus =
                "Orbit preview active. Enable the Playback FPP camera toggle to use the target model's selected preview camera.";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        if (frame.Camera is null)
        {
            _viewportCoordinator.SetPreviewCameraOverride(
                ViewportSide.Source,
                null);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            string unavailable = frame.Dl1PreviewStages
                .FirstOrDefault(static stage =>
                    stage.StageId == Dl1PreviewStageIds.FppViewTransform)
                ?.Message
                ?? "The selected rig does not provide an evaluated FPP camera.";
            FacialFpp.PreviewStatus =
                $"FPP camera unavailable: {unavailable}";
            SourceViewport.SetDiagnosticOverlay(
                $"Preview camera '{ResolveTargetPreviewCameraNodeName()}' is missing or unavailable. {unavailable}");
            PublishLinkedTargetExternalView(frame);
            return;
        }

        if (frame.Camera.Source !=
            EvaluatedCameraSource.Dl1FppEyeCamera)
        {
            TargetViewport.SceneSource.SetFppProjectionState(null);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            _viewportCoordinator.SetPreviewCameraOverride(
                ViewportSide.Source,
                Dl1PreviewCameraAdapter.ToRenderCamera(
                    frame.Camera,
                    preserveLensAspectRatio: false));
            string cameraName =
                ResolveTargetPreviewCameraNodeName();
            FacialFpp.PreviewStatus =
                $"Left viewport follows the target model's editor preview camera '{cameraName}'; animation data is unchanged and the right viewport remains a free external orbit.";
            PublishLinkedTargetExternalView(frame);
            return;
        }

        bool hasFirstPersonTargetGeometry =
            _targetProjectAsset?.Kind ==
                ProjectAssetKind.CustomModelSource ||
            TargetViewport.SceneSource.CaptureFrame().Meshes.Any(
                static mesh =>
                    mesh.ProjectionRole == MeshProjectionRole.FppHands);
        if (!hasFirstPersonTargetGeometry)
        {
            _viewportCoordinator.SetPreviewCameraOverride(
                ViewportSide.Source,
                null);
            _viewportCoordinator.SetTargetPreviewCameraOverride(null);
            TargetViewport.SceneSource.SetFppProjectionState(null);
            string targetName =
                _targetProjectAsset?.RetailIdentity?.ResourceName ??
                "the current target";
            string unavailable =
                $"FPP EyeCamera preview requires an FPP retail target, but '{targetName}' is not classified as FPP geometry. Choose the matching player_*_fpp model as Target.";
            FacialFpp.PreviewStatus = unavailable;
            SourceViewport.SetDiagnosticOverlay(unavailable);
            PublishLinkedTargetExternalView(frame);
            return;
        }

        SourceViewport.SetDiagnosticOverlay(null);

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
        var projectionState = new RenderFppProjectionState(
                // A separate hands frustum is used only when explicit
                // capture data supplied it. Otherwise the FPP mesh remains
                // visible through the ordinary EyeCamera scene projection.
                // Routing with a null projection makes the renderer fail
                // closed and caused the reported completely black pane.
                RouteHandsMeshes: handsProjection is not null,
                SceneAspectRatio: capturedSceneProjection
                    ? checked((float)frame.Camera.Lens.AspectRatio)
                    : null,
                HandsProjection: handsProjection);
        TargetViewport.SceneSource.SetFppProjectionState(null);
        _viewportCoordinator.SetTargetPreviewCameraOverride(null);
        _viewportCoordinator.SetPreviewCameraOverride(
            ViewportSide.Source,
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
            "Left viewport follows the evaluated EyeCamera authoring fallback; the right viewport remains a free external orbit. " +
            (string.IsNullOrWhiteSpace(stageSummary)
                ? string.Empty
                : stageSummary);
        PublishLinkedTargetExternalView(
            frame,
            projectionState);
    }

    private bool UsesLinkedTargetExternalView() =>
        ActiveWorkspaceMode is "FPP" or "Cutscene";

    private void PublishLinkedTargetExternalView(
        EvaluationFrame frame,
        RenderFppProjectionState? fppProjection = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!UsesLinkedTargetExternalView())
        {
            ClearLinkedTargetExternalView();
            return;
        }

        if (_lastFppExternalOrbitFrame is not null)
        {
            TargetViewport.SceneSource.SetExternalPreviewScene(null);
        }
        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();

        if (ActiveWorkspaceMode == "Cutscene")
        {
            SourceViewport.SetCameraViewActive(false);
            SourceViewport.SceneSource.SetExternalPreviewScene(
                targetFrame);
            SourceViewport.SetPresentation(
                "DL1 Target / External",
                "Same evaluated target | free orbit | movie camera override disabled");
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
            _viewportCoordinator.HasPreviewCameraOverride(
                ViewportSide.Source);
        SourceViewport.SetCameraViewActive(hasFppCamera);
        _suspendedTargetProjection = fppProjection;
        SourceViewport.SceneSource.SetExternalPreviewScene(
            targetFrame with
            {
                FppProjectionState = fppProjection,
            },
            preserveFppProjectionState: true);
        RenderFrameSnapshot authoredOrbitFrame =
            CreateFppExternalOrbitFrame(frame, targetFrame);
        _lastFppExternalOrbitFrame = authoredOrbitFrame;
        TargetViewport.SceneSource.SetExternalPreviewScene(
            authoredOrbitFrame);
        RenderFppProjectionState? projection = fppProjection;
        string cameraName = ResolveTargetPreviewCameraNodeName();
        string fidelity = hasFppCamera
            ? frame.Camera!.Source ==
                EvaluatedCameraSource.Dl1FppEyeCamera
                ? projection?.HandsProjection is not null
                    ? "Evaluated EyeCamera | captured scene and separate hands projections"
                    : "Evaluated EyeCamera | hands projection unavailable or disabled"
                : $"Evaluated editor camera '{cameraName}' | ordinary scene projection"
            : $"Evaluated target | camera '{cameraName}' unavailable";
        SourceViewport.SetPresentation(
            hasFppCamera
                ? $"Target / {cameraName}"
                : $"Preview camera {cameraName} unavailable",
            fidelity);
        TargetViewport.SetPresentation(
            "DL1 Target / External Orbit",
            "Same evaluated target | free orbit | FPP camera projection disabled");
    }

    private void ClearLinkedTargetExternalView(
        bool evaluationUnavailable = false)
    {
        SourceViewport.SceneSource.SetExternalPreviewScene(null);
        SourceViewport.SetCameraViewActive(false);
        if (_lastFppExternalOrbitFrame is not null)
        {
            TargetViewport.SceneSource.SetExternalPreviewScene(null);
            _lastFppExternalOrbitFrame = null;
        }
        if (!UsesLinkedTargetExternalView())
        {
            SourceViewport.SetDiagnosticOverlay(null);
            SourceViewport.SetPresentation(
                AuthoredSourcePaneTitle,
                AuthoredSourcePaneFidelity);
            UpdateTargetPreviewPresentation();
            return;
        }

        if (ActiveWorkspaceMode == "FPP")
        {
            string cameraName = ResolveTargetPreviewCameraNodeName();
            SourceViewport.SetPresentation(
                $"Preview camera {cameraName} unavailable",
                evaluationUnavailable
                    ? $"The selected rig did not produce evaluated camera '{cameraName}'; the external orbit remains available on the right"
                    : $"Waiting for evaluated camera '{cameraName}'");
            SourceViewport.SetDiagnosticOverlay(
                evaluationUnavailable
                    ? $"Preview camera '{cameraName}' is missing or ambiguous. The external orbit remains usable on the right."
                    : null);
            TargetViewport.SetPresentation(
                "DL1 Target / External Orbit",
                evaluationUnavailable
                    ? "Evaluated target unavailable"
                    : "Same evaluated target | free orbit");
            return;
        }

        SourceViewport.SetPresentation(
            AuthoredSourcePaneTitle,
            evaluationUnavailable
                ? "Authored scene restored | evaluated DL1 target unavailable"
                : AuthoredSourcePaneFidelity);
        TargetViewport.SetPresentation(
            "DL1 Target / Movie Camera",
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

        if (ActiveWorkspaceMode == "FPP")
        {
            RenderFrameSnapshot? authoredOrbitFrame =
                _lastFppExternalOrbitFrame;
            if (authoredOrbitFrame is not null)
            {
                TargetViewport.SceneSource.SetExternalPreviewScene(null);
            }
            RenderFrameSnapshot fppDisplayFrame =
                TargetViewport.SceneSource.CaptureFrame();
            bool hasPreviewCamera =
                _viewportCoordinator.HasPreviewCameraOverride(
                    ViewportSide.Source);
            string cameraName = ResolveTargetPreviewCameraNodeName();
            SourceViewport.SceneSource.SetExternalPreviewScene(
                fppDisplayFrame with
                {
                    FppProjectionState = _suspendedTargetProjection,
                },
                preserveFppProjectionState: true);
            SourceViewport.SetPresentation(
                hasPreviewCamera
                    ? $"Target / {cameraName}"
                    : $"Preview camera {cameraName} unavailable",
                hasPreviewCamera
                    ? $"Restored evaluated camera '{cameraName}' | current published frame"
                    : "The external orbit remains available on the right");
            SourceViewport.SetDiagnosticOverlay(
                hasPreviewCamera
                    ? null
                    : $"Preview camera '{cameraName}' is missing or ambiguous. The external orbit remains usable on the right.");
            SourceViewport.SetCameraViewActive(hasPreviewCamera);
            if (authoredOrbitFrame is not null)
            {
                TargetViewport.SceneSource.SetExternalPreviewScene(
                    authoredOrbitFrame);
            }
            TargetViewport.SetPresentation(
                "DL1 Target / External Orbit",
                "Same evaluated target | free orbit | FPP camera projection disabled");
            return;
        }

        RenderFrameSnapshot targetFrame =
            TargetViewport.SceneSource.CaptureFrame();
        SourceViewport.SceneSource.SetExternalPreviewScene(targetFrame);
        SourceViewport.SetPresentation(
            "DL1 Target / External",
            "Same evaluated target | free orbit | movie camera override disabled");
        bool hasMoviePreviewCamera =
            _viewportCoordinator.HasTargetPreviewCameraOverride;
        TargetViewport.SetPresentation(
            hasMoviePreviewCamera
                ? "DL1 Target / Movie Camera"
                : "DL1 Target / Orbit",
            hasMoviePreviewCamera
                ? "Restored evaluated movie camera | current published frame"
                : "Evaluated target | free orbit until a camera is available");
    }

    private RenderFrameSnapshot CreateFppExternalOrbitFrame(
        EvaluationFrame frame,
        RenderFrameSnapshot evaluatedDisplayFrame)
    {
        SkeletonRenderData skeleton =
            CorePreviewAdapter.ToRenderSkeleton(
                frame.AuthoredPose,
                SelectedBone?.Index,
                frame.ActorWorldTransform);
        AttachmentSceneComposition scene =
            AttachmentSceneComposer.Compose(
                _targetBaseMeshes,
                frame.AuthoredAttachments,
                _attachmentRenderAssets,
                frame.ActorWorldTransform);
        Guid? selectedBindingId =
            AttachmentEditor.SelectedAttachment?.Id;
        MeshRenderData[] meshes = scene.Meshes
            .Select(mesh => mesh with
            {
                IsSelected = selectedBindingId.HasValue
                    ? IsAttachmentMeshForBinding(
                        mesh,
                        selectedBindingId.Value)
                    : !mesh.Id.StartsWith(
                        "attachment/",
                        StringComparison.Ordinal),
            })
            .ToArray();
        if (meshes.Length == 0 &&
            _targetBaseMeshes.Length == 0 &&
            frame.AuthoredAttachments.IsEmpty)
        {
            // A renderer-only or test publication may not have the decoded
            // retail base meshes available to rebuild the authored orbit.
            // Reuse the already-evaluated geometry in that narrow case while
            // still removing the FPP camera/projection override.
            meshes = evaluatedDisplayFrame.Meshes.ToArray();
        }
        MorphWeight[] morphs = frame.AuthoredMorphWeights
            .Select(static pair => new MorphWeight(
                pair.Key,
                checked((float)pair.Value)))
            .ToArray();
        return evaluatedDisplayFrame with
        {
            Meshes = meshes,
            Skeleton = skeleton,
            Gizmos = BuildBoneEditGizmos(skeleton),
            MorphWeights = morphs,
            FppProjectionState = null,
        };
    }

    private void UpdateUnevaluatedPreviewStatus(string nextStep)
    {
        FacialFpp.PreviewStatus = ActiveWorkspaceMode switch
        {
            "FPP" when SelectedPreviewMode == RawPreviewModeLabel =>
                $"Raw target camera '{ResolveTargetPreviewCameraNodeName()}' is requested without DL1 procedural projection. {nextStep}",
            "FPP" =>
                $"Playback camera '{ResolveTargetPreviewCameraNodeName()}' is requested for the left viewport; the right viewport remains an external orbit. {nextStep}",
            "Cutscene" when
                FacialFpp.UseMovieReferenceCameraCapture =>
                $"The explicit external DL1 movie reference camera is requested. {nextStep}",
            "Cutscene" =>
                $"DL1 movie context is active, but no external reference-camera snapshot is loaded. {nextStep}",
            _ =>
                "Orbit preview active. Enable FPP to use the target model's selected preview camera.",
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
        string? preferredTrackId = null;
        if (_sourceAnimation is { } source)
        {
            int? sourceCurveBoneIndex = ResolveSourceCurveBoneIndex(
                source,
                animation);
            foreach (TransformTrack track in
                     source.Clip.TransformTracks)
            {
                string boneName = (uint)track.BoneIndex <
                        (uint)source.Rig.BoneCount
                    ? source.Rig.Bones[track.BoneIndex].Name
                    : $"Bone {track.BoneIndex}";
                string trackId = $"source-transform:{track.BoneIndex}";
                var sourceTrack = new TimelineTrackViewModel(
                    trackId,
                    boneName,
                    $"Transform | {track.Keyframes.Length:N0} source keys",
                    "Source animation",
                    isReadOnly: true,
                    totalKeyCount: track.Keyframes.Length,
                    exactKeyFrames: track.Keyframes.Select(
                        static keyframe => checked((int)Math.Round(
                            keyframe.Frame))));

                viewModels.Add(sourceTrack);
                AddTransformCurves(
                    curveModels,
                    trackId,
                    $"Source clip / {boneName}",
                    SelectTimelineTransformKeys(
                            track.Keyframes,
                            maximumCount: 256)
                        .ToImmutableArray());
                if (track.BoneIndex == sourceCurveBoneIndex)
                {
                    preferredTrackId = trackId;
                }
            }

            foreach (ScalarTrack track in source.Clip.ScalarTracks)
            {
                string trackId =
                    $"source-scalar:{track.ChannelName}";
                var sourceTrack = new TimelineTrackViewModel(
                    trackId,
                    track.ChannelName,
                    $"Scalar | {track.Keyframes.Length:N0} source keys",
                    "Facial",
                    isReadOnly: true,
                    totalKeyCount: track.Keyframes.Length,
                    exactKeyFrames: track.Keyframes.Select(
                        static keyframe => checked((int)Math.Round(
                            keyframe.Frame))));

                viewModels.Add(sourceTrack);
                curveModels.Add(
                    new TimelineCurveTrackViewModel(
                        "Value",
                        "#E599F7",
                        SelectTimelineScalarKeys(
                                track.Keyframes,
                                maximumCount: 256)
                            .Select(static key =>
                                new TimelineCurveKeyViewModel(
                                    key.Frame,
                                    key.Value)),
                        trackId,
                        $"Source clip / {track.ChannelName}"));
                preferredTrackId ??= trackId;
            }
        }

        foreach (BoneEditLayer layer in animation.EditLayers)
        {
            foreach (BoneEditTrack track in layer.Tracks)
            {
                SkeletonNodeViewModel? bone = FindBone(track.BoneIndex);
                string trackId =
                    $"edit:{layer.Id:N}:{track.BoneIndex}";
                var viewModel = new TimelineTrackViewModel(
                    trackId,
                    bone?.Path ?? $"Bone {track.BoneIndex}",
                    $"{layer.Name} | Transform",
                    "Authored edits",
                    isReadOnly: false);
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
                    trackId,
                    $"{layer.Name} / {viewModel.Name}",
                    track.Keyframes);
                if (SelectedBone?.Index == track.BoneIndex)
                {
                    preferredTrackId ??= trackId;
                }
            }
        }

        foreach (MorphEditLayer layer in animation.MorphEditLayers)
        {
            foreach (MorphEditTrack track in layer.Tracks)
            {
                string trackId =
                    $"morph:{layer.Id:N}:{track.MorphName}";
                var viewModel = new TimelineTrackViewModel(
                    trackId,
                    track.MorphName,
                    $"{layer.Name} | Morph",
                    "Facial",
                    isReadOnly: false);
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
                    "Value",
                    "#E599F7",
                    track.Keyframes.Select(static key =>
                        new TimelineCurveKeyViewModel(
                            key.Frame,
                            key.Value)),
                    trackId,
                    $"{layer.Name} / {track.MorphName}"));
            }
        }

        foreach (ProjectIkLayer layer in animation.IkLayers)
        {
            string trackId = $"ik:{layer.Id:N}";
            var viewModel = new TimelineTrackViewModel(
                trackId,
                layer.ChainName,
                $"{layer.Name} | Two-bone IK",
                "IK",
                isReadOnly: false);
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
                trackId,
                $"{layer.Name} / {layer.ChainName} / Effector",
                layer.Keyframes.Select(static key =>
                    (key.Frame, key.Effector)));
            AddVectorCurves(
                curveModels,
                trackId,
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
            string trackId = $"attachment:{attachment.Id:N}";
            var viewModel = new TimelineTrackViewModel(
                trackId,
                attachment.Name,
                $"Parent: {parent}",
                "Attachments",
                isReadOnly: false);
            viewModel.Keyframes.Add(
                new TimelineKeyframeViewModel(
                    viewModel.Name,
                    0,
                    0,
                    12.0));
            viewModels.Add(viewModel);
            AddTransformCurves(
                curveModels,
                trackId,
                $"Attachment / {attachment.Name}",
                [new TransformKeyframe(
                    0.0,
                    attachment.LocalOffset)]);
        }

        Timeline.ReplaceTracks(viewModels);
        Timeline.SelectTrack(preferredTrackId);
        Timeline.ReplaceCurves(curveModels);
    }

    private int? ResolveSourceCurveBoneIndex(
        ImportedAnimationSession source,
        ProjectAnimation animation)
    {
        if (SelectedBone is { } selected)
        {
            if (string.Equals(
                    animation.SourceRigSignature,
                    animation.TargetRigSignature,
                    StringComparison.OrdinalIgnoreCase) &&
                source.Clip.TransformTracks.Any(track =>
                    track.BoneIndex == selected.Index))
            {
                return selected.Index;
            }

            BoneMapEntry? mapped = _activeRetargetMap?.Entries
                .FirstOrDefault(entry =>
                    entry.TargetBoneIndex == selected.Index &&
                    entry.MappingKind == RetargetMappingKind.Bone);
            if (mapped is not null)
            {
                return mapped.SourceBoneIndex;
            }
        }

        return source.Clip.TransformTracks
            .FirstOrDefault()?.BoneIndex;
    }

    private static IEnumerable<TransformKeyframe>
        SelectTimelineTransformKeys(
            ImmutableArray<TransformKeyframe> keys,
            int maximumCount) =>
        SelectTimelineKeys(
            keys,
            maximumCount,
            static key => key.Frame);

    private static IEnumerable<ScalarKeyframe>
        SelectTimelineScalarKeys(
            ImmutableArray<ScalarKeyframe> keys,
            int maximumCount) =>
        SelectTimelineKeys(
            keys,
            maximumCount,
            static key => key.Frame);

    private static IEnumerable<T> SelectTimelineKeys<T>(
        ImmutableArray<T> keys,
        int maximumCount,
        Func<T, double> getFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumCount);
        if (keys.Length <= maximumCount)
        {
            return keys;
        }

        var selected = new List<T>(maximumCount);
        double denominator = maximumCount - 1.0;
        var previousIndex = -1;
        for (var slot = 0; slot < maximumCount; slot++)
        {
            int index = checked((int)Math.Round(
                slot * (keys.Length - 1) / denominator));
            if (index == previousIndex)
            {
                continue;
            }

            T key = keys[index];
            _ = getFrame(key);
            selected.Add(key);
            previousIndex = index;
        }

        return selected;
    }

    private static void AddTransformCurves(
        List<TimelineCurveTrackViewModel> curves,
        string ownerTrackId,
        string prefix,
        ImmutableArray<TransformKeyframe> keyframes)
    {
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Translation X",
            "#F26C6C",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.X)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Translation Y",
            "#6BCB77",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.Y)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Translation Z",
            "#5C7CFA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Translation.Z)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Rotation X",
            "#FFA94D",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.X)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Rotation Y",
            "#38D9A9",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.Y)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Rotation Z",
            "#9775FA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.Z)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Rotation W",
            "#CED4DA",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Rotation.W)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Scale X",
            "#FF8787",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.X)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Scale Y",
            "#8CE99A",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.Y)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            "Scale Z",
            "#91A7FF",
            keyframes.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Scale.Z)));
    }

    private static void AddVectorCurves(
        List<TimelineCurveTrackViewModel> curves,
        string ownerTrackId,
        string prefix,
        IEnumerable<(double Frame, Vector3D Value)> keys)
    {
        (double Frame, Vector3D Value)[] rows = keys.ToArray();
        string component = prefix
            .Split('/')
            .Last()
            .Trim();
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            $"{component} X",
            "#F26C6C",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.X)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            $"{component} Y",
            "#6BCB77",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Y)));
        AddCurve(
            curves,
            ownerTrackId,
            prefix,
            $"{component} Z",
            "#5C7CFA",
            rows.Select(static key =>
                new TimelineCurveKeyViewModel(
                    key.Frame,
                    key.Value.Z)));
    }

    private static void AddCurve(
        List<TimelineCurveTrackViewModel> curves,
        string ownerTrackId,
        string ownerLabel,
        string name,
        string color,
        IEnumerable<TimelineCurveKeyViewModel> keys) =>
        curves.Add(new TimelineCurveTrackViewModel(
            name,
            color,
            keys,
            ownerTrackId,
            ownerLabel));

    private ProjectAnimation? GetActiveAnimation()
    {
        if (_activeAnimationId is not { } id)
        {
            return null;
        }

        return GetRuntimeAnimation(id);
    }

    private ProjectAnimation? GetRuntimeAnimation(Guid id)
    {
        ProjectAnimation? compatibility = _project.Animations
            .FirstOrDefault(animation => animation.Id == id);
        if (compatibility is not null)
        {
            return compatibility;
        }

        ProjectAnimationVariant? variant = _project.AnimationVariants
            .FirstOrDefault(candidate => candidate.Id == id);
        ProjectAnimationSource? source = variant is null
            ? null
            : _project.AnimationSources.FirstOrDefault(candidate =>
                candidate.Id == variant.SourceId);
        return source?.EmbeddedCustomModelStack is not null
            ? CreateRuntimeAnimation(source, variant!)
            : null;
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

            ProjectAnimationVariant? variant = _project
                .AnimationVariants
                .FirstOrDefault(candidate => candidate.Id == id);
            ProjectAnimationSource? source = variant is null
                ? null
                : _project.AnimationSources.FirstOrDefault(candidate =>
                    candidate.Id == variant.SourceId);
            if (source?.EmbeddedCustomModelStack is not null)
            {
                animation = CreateRuntimeAnimation(source, variant!);
                animationIndex = -1;
                return true;
            }
        }

        animation = null!;
        animationIndex = -1;
        return false;
    }

    private DlraProject WithUpdatedActiveAnimation(
        DlraProject project,
        ProjectAnimation animation,
        int compatibilityIndex)
    {
        if (compatibilityIndex >= 0)
        {
            return project with
            {
                Animations = project.Animations.SetItem(
                    compatibilityIndex,
                    animation),
            };
        }

        DlraProject carrier = project with
        {
            Animations = project.Animations.Add(animation),
        };
        DlraProject synchronized =
            SynchronizeSchema2FromCompatibilityAnimations(
                _project,
                carrier);
        return synchronized with
        {
            Animations = project.Animations,
        };
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
            RenderFppProjectionState? fppProjection =
                ActiveWorkspaceMode == "FPP"
                    ? _suspendedTargetProjection
                    : null;
            RenderFrameSnapshot targetExternal =
                TargetViewport.SceneSource.CaptureFrame() with
                {
                    MorphWeights = weights,
                    FppProjectionState = null,
                };
            TargetViewport.SceneSource.SetExternalPreviewScene(
                targetExternal);
            if (ActiveWorkspaceMode == "FPP")
            {
                _lastFppExternalOrbitFrame = targetExternal;
            }

            RenderFrameSnapshot cameraExternal =
                SourceViewport.SceneSource.CaptureFrame() with
                {
                    MorphWeights = weights,
                    FppProjectionState = fppProjection,
                };
            SourceViewport.SceneSource.SetExternalPreviewScene(
                cameraExternal,
                preserveFppProjectionState:
                    fppProjection is not null);
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
    private string? _diagnosticOverlay;
    private bool _isCameraViewActive;

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

    public string? DiagnosticOverlay
    {
        get => _diagnosticOverlay;
        private set => SetProperty(ref _diagnosticOverlay, value);
    }

    public bool IsCameraViewActive
    {
        get => _isCameraViewActive;
        private set => SetProperty(ref _isCameraViewActive, value);
    }

    internal void SetPresentation(
        string title,
        string fidelityLabel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(fidelityLabel);
        Title = title;
        FidelityLabel = fidelityLabel;
    }

    internal void SetDiagnosticOverlay(string? message)
    {
        DiagnosticOverlay = string.IsNullOrWhiteSpace(message)
            ? null
            : message.Trim();
    }

    internal void SetCameraViewActive(bool active)
    {
        IsCameraViewActive = active;
    }
}

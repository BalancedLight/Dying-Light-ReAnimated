using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private Func<CustomModelPackage, IEnumerable<string>, Task<FbxModelAuthoringImportResult>>? _characterDependencyResolver;
    private AsyncRelayCommand? _resolveCharacterDependenciesCommand;
    private RelayCommand? _applyBoneBoundsCommand, _openCharacterSystemsCommand, _fitBoneBoundsCommand;
    private bool _isCharacterTabSelected;
    private CharacterSubsystem _selectedOptionalSubsystem;
    private bool _subsystemAbsenceReviewed;
    private RelayCommand? _markSubsystemNotApplicableCommand;
    public IReadOnlyList<CharacterSubsystem> CharacterSubsystemChoices {get;}=Enum.GetValues<CharacterSubsystem>();
    public CharacterSubsystem SelectedOptionalSubsystem {get=>_selectedOptionalSubsystem;set {if(SetProperty(ref _selectedOptionalSubsystem,value))SubsystemAbsenceReviewed=false;}}
    public bool SubsystemAbsenceReviewed {get=>_subsystemAbsenceReviewed;set=>SetProperty(ref _subsystemAbsenceReviewed,value);}
    public RelayCommand MarkSubsystemNotApplicableCommand=>_markSubsystemNotApplicableCommand??=new(MarkSubsystemNotApplicable);
    private bool _showCharacterBounds;
    public bool IsCharacterTabSelected { get => _isCharacterTabSelected; set { if (SetProperty(ref _isCharacterTabSelected, value)) RefreshPreview(); } }
    public bool ShowCharacterBounds { get => _showCharacterBounds; set { if (SetProperty(ref _showCharacterBounds, value)) RefreshPreview(); } }
    public RelayCommand OpenCharacterSystemsCommand => _openCharacterSystemsCommand ??= new(() => { Conformance.IsAdvancedSetupMode = true; IsCharacterTabSelected = true; });
    private string _characterCompanionRoots = string.Empty;
    private string _boneBoundsText = string.Empty;
    public ObservableCollection<CharacterSubsystemReview> CharacterSubsystems { get; } = [];
    public ObservableCollection<CharacterResourceRecord> CharacterResources { get; } = [];
    public ObservableCollection<CharacterMorphBinding> CharacterMorphBindings { get; } = [];
    public ObservableCollection<MorphAuthoringRecord> CharacterExpressionReviews { get; } = [];
    public bool HasCharacterInventory => _model?.Package.Document.CharacterResources is not null;
    public string CharacterCompleteness
    {
        get
        {
            if (_model is null) return "Load a character model.";
            var expressionBlockers = ReAnimated.Codecs.Models.MorphAuthoringEvidence.ExportBlockers(_model).AddRange(ReAnimated.Codecs.Models.CharacterBodyRegionAuthoring.ExportBlockers(_model));
            if (!expressionBlockers.IsEmpty)
                return $"{expressionBlockers.Length} authoring or export checks need attention.";
            if (_model.Package.Document.CharacterResources is not { } c)
                return "Attach any required character systems.";
            return c.IsGameReady ? "Runtime validation complete." : c.IsDependencyComplete ?
                "Dependencies ready. Compile and validate to finish." : $"{c.ExportBlockers.Length} unresolved checks. Resolve them before exporting.";
        }
    }
    public string CharacterCompanionRoots { get => _characterCompanionRoots; set => SetProperty(ref _characterCompanionRoots, value); }
    public string BoneBoundsText { get => _boneBoundsText; set => SetProperty(ref _boneBoundsText, value); }
    public AsyncRelayCommand ResolveCharacterDependenciesCommand => _resolveCharacterDependenciesCommand ??= new(ResolveCharacterDependenciesAsync);
    public RelayCommand FitBoneBoundsCommand=>_fitBoneBoundsCommand??=new(FitBoneBounds);
    public RelayCommand ApplyBoneBoundsCommand => _applyBoneBoundsCommand ??= new(ApplyBoneBounds);
    internal void SetCharacterDependencyResolver(Func<CustomModelPackage, IEnumerable<string>, Task<FbxModelAuthoringImportResult>> resolver) => _characterDependencyResolver = resolver;

    private void RefreshCharacterInventory()
    {
        InvalidateExpressionProposal();
        CharacterSubsystems.Clear(); CharacterResources.Clear(); CharacterMorphBindings.Clear(); CharacterExpressionReviews.Clear();
        foreach (var review in _model?.Package.Document.MorphAuthoringRecords ?? []) CharacterExpressionReviews.Add(review);
        CharacterSurfaceIds.Clear();
        foreach (var s in _model?.Surfaces ?? []) CharacterSurfaceIds.Add(s.Id);
        if (!CharacterSurfaceIds.Contains(SelectedCharacterSurface ?? string.Empty)) SelectedCharacterSurface = CharacterSurfaceIds.FirstOrDefault();
        if (_model?.Package.Document.CharacterResources is { } c)
        {
            foreach (var row in c.Subsystems) CharacterSubsystems.Add(row);
            foreach (var row in c.Resources) CharacterResources.Add(row);
            foreach (var row in c.MorphBindings) CharacterMorphBindings.Add(row);
        }
        RefreshCompanionCandidates();
        RefreshCharacterActorSources();
        RefreshCharacterMaterialChoices();
        OnPropertyChanged(nameof(HasCharacterInventory)); OnPropertyChanged(nameof(CharacterCompleteness));
        LoadBoneBounds();
    }
    private void MarkSubsystemNotApplicable()
    {
        if(_model?.Package.Document.CharacterResources is not { } inventory)return;
        var review=inventory.Subsystems.SingleOrDefault(s=>s.Subsystem==SelectedOptionalSubsystem);
        if(!SubsystemAbsenceReviewed || review?.Status!=CharacterDependencyStatus.Missing || inventory.Resources.Any(r=>r.Subsystem==SelectedOptionalSubsystem && r.Required && !r.IsOriginalArchive))
        {BuildStatus="Review absence first. A declared, missing, ambiguous or existing dependency cannot be dismissed as not used.";return;}
        var before=CaptureAuthoringSnapshot();
        var updated=inventory with {Subsystems=inventory.Subsystems.Select(s=>s.Subsystem==SelectedOptionalSubsystem?s with {Status=CharacterDependencyStatus.NotApplicable,Detail="Explicitly reviewed as unused for source SHA-256 "+_model.Package.Document.Source.ContentSha256}:s).ToImmutableArray(),CompiledSemanticSha256=null,LoadedResourceSha256=null,VerifiedPlayerScenarios=[]};
        CommitModel(_model with {Package=_model.Package with {Document=_model.Package.Document with {CharacterResources=updated,LastBuildReceipt=null}}},_sourcePath,_packagePath,preserveAuthoringHistory:true);
        RecordAuthoringUndo(before);SubsystemAbsenceReviewed=false;BuildStatus="System marked as unused.";
    }
    private void LoadBoneBounds()
    {
        var bounds = _model?.Package.Document.Bones.FirstOrDefault(b => b.Name == SelectedBone?.Name)?.LocalBounds;
        BoneBoundsText = bounds is { } b ? string.Join(" ", new[] { b.Center.X, b.Center.Y, b.Center.Z, b.HalfExtents.X * 2, b.HalfExtents.Y * 2, b.HalfExtents.Z * 2 }
            .Select(v => v.ToString("R", CultureInfo.InvariantCulture))) : string.Empty;
    }
    private async Task ResolveCharacterDependenciesAsync()
    {
        if (_disposed) return;
        if (IsBusy) { BuildStatus = "Wait for the current model operation to finish before resolving dependencies."; return; }
        if(_model?.Package.Document.Source.Kind==CustomModelSourceKind.BinaryFbx){BuildStatus="Resolve additional dependencies in the original reference package before attaching its systems to the custom character. Existing reference data and edits are preserved.";return;}
        if (_characterDependencyResolver is null || _model is null) { BuildStatus = "Import this character from the loaded catalog to resolve companion roots."; return; }
        var model = _model;
        long revision = PersistenceRevision;
        try
        {
            var names = CharacterCompanionRoots.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var resolved = await _characterDependencyResolver(model.Package,names);
            if (_disposed) return;
            if (IsBusy || !ReferenceEquals(_model, model) || PersistenceRevision != revision)
                throw new InvalidOperationException("The model changed during dependency resolution. Resolve its dependencies again.");
            if (resolved.Package.Document.Source.ContentSha256 != model.Package.Document.Source.ContentSha256)
                throw new InvalidOperationException("The catalog character source changed; retain this package and import the new source separately.");
            CustomModelPackage updated = MergeResolvedCharacterDependencies(model.Package, resolved.Package, names);
            updated.Document.Validate();
            var before = CaptureAuthoringSnapshot();
            CommitModel(model with { Package = updated }, _sourcePath, _packagePath, preserveAuthoringHistory: true);
            RecordAuthoringUndo(before); BuildStatus = "Dependencies updated.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        { BuildStatus = "Dependency resolution failed: " + e.Message; }
    }
    /// <summary>Merge exact source custody without inferring actor or runtime association.</summary>
    internal static CustomModelPackage MergeResolvedCharacterDependencies(
        CustomModelPackage current, CustomModelPackage refreshed, IEnumerable<string> selectedRootNames)
    {
        CharacterResourceInventory original = current.Document.CharacterResources ??
            throw new InvalidDataException("The original character inventory is missing.");
        CharacterResourceInventory incoming = refreshed.Document.CharacterResources ??
            throw new InvalidDataException("The refreshed character inventory is missing.");
        if (!string.Equals(current.Document.Source.ContentSha256, refreshed.Document.Source.ContentSha256,
                StringComparison.OrdinalIgnoreCase) || original.RootResourceId != incoming.RootResourceId)
            throw new InvalidDataException("The character source changed during companion resolution.");

        var oldById = original.Resources.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var newById = incoming.Resources.ToDictionary(r => r.Id, StringComparer.Ordinal);
        if (!oldById.TryGetValue(original.RootResourceId, out CharacterResourceRecord? oldRoot) ||
            !newById.TryGetValue(incoming.RootResourceId, out CharacterResourceRecord? newRoot))
            throw new InvalidDataException("The exact character root is missing.");
        RequireSameSource(oldRoot, newRoot);
        bool supersedeScanLimits = CharacterDependencyScanEvidence.CanSupersedeLimits(current, refreshed);

        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in selectedRootNames.Distinct(StringComparer.Ordinal))
        {
            CharacterResourceRecord[] matches = incoming.Resources.Where(r => r.EntryPath is not null &&
                (r.Id == name || r.LogicalName == name)).ToArray();
            if (matches.Length != 1)
                throw new InvalidDataException("An explicitly selected companion root is missing or ambiguous after refresh.");
            selectedIds.Add(matches[0].Id);
        }

        var archivedIds = original.Resources.Where(r => r.IsOriginalArchive && r.Id.StartsWith("original:", StringComparison.Ordinal))
            .Select(r => r.Id["original:".Length..]).ToHashSet(StringComparer.Ordinal);
        var retained = new Dictionary<string, CharacterResourceRecord>(StringComparer.Ordinal);
        foreach (CharacterResourceRecord old in original.Resources.Where(r => r.EntryPath is not null && (!CharacterDependencyScanEvidence.IsReceipt(r) || !incoming.Resources.Any(CharacterDependencyScanEvidence.IsReceipt))))
        {
            if (old.IsOriginalArchive)
            {
                VerifyPayload(old, current.CompanionPayloads);
                retained.Add(old.Id, old);
                continue;
            }
            if (newById.TryGetValue(old.Id, out CharacterResourceRecord? fresh))
            {
                CharacterResourceRecord comparison = archivedIds.Contains(old.Id)
                    ? oldById["original:" + old.Id] with { Id = old.Id } : old;
                RequireSameSource(comparison, fresh);
                if (old.Id == original.RootResourceId && old.NativeResource is null && fresh.NativeResource is { ResourceType: 272 })
                {
                    VerifyPayload(old, current.CompanionPayloads);
                    VerifyPayload(fresh, refreshed.CompanionPayloads);
                    retained.Add(old.Id, old with { NativeResource = fresh.NativeResource });
                    continue;
                }
            }
            if (old.Status != CharacterDependencyStatus.Ambiguous)
            {
                VerifyPayload(old, current.CompanionPayloads);
                retained.Add(old.Id, old);
                continue;
            }
            VerifyPayload(old, current.CompanionPayloads);
            if (!selectedIds.Contains(old.Id))
            {
                retained.Add(old.Id, old);
                continue;
            }
            if (archivedIds.Contains(old.Id))
                throw new InvalidDataException("An authored companion edit cannot be replaced by catalog re-resolution.");
            if (!newById.TryGetValue(old.Id, out CharacterResourceRecord? selected) ||
                selected.Status != CharacterDependencyStatus.Preserved)
                throw new InvalidDataException("The explicitly selected companion is not available from its exact source.");
            retained.Add(old.Id, old with
            {
                Status = CharacterDependencyStatus.Preserved,
                Subsystem = selected.Subsystem,
                Detail = "Exact companion source identity and bytes were explicitly selected and retained; actor association and runtime behavior remain unverified.",
            });
        }

        // Supersede only known old limits backed by completed exact-root scan evidence.
        foreach (CharacterResourceRecord old in original.Resources.Where(r => r.EntryPath is null &&
                     (r.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Ambiguous or CharacterDependencyStatus.Unsupported)))
        {
            if (supersedeScanLimits && CharacterDependencyScanEvidence.IsKnownLimitFinding(old) &&
                !incoming.Resources.Any(resource => resource.Id == old.Id))
                continue;
            if (supersedeScanLimits && CharacterDependencyScanEvidence.CanSupersedeIndexedTemplate(old, current, refreshed))
                continue;
            if (supersedeScanLimits && CharacterDependencyScanEvidence.CanMakeConditionalRelicAdvisory(old, current, refreshed))
            {
                retained.Add(old.Id, old with { Required = false,
                    Detail = "Alternative declaration outside the verified generic preload gather. " + old.Detail });
                continue;
            }
            bool reviewedExactResolution = (old.Status is CharacterDependencyStatus.Missing or CharacterDependencyStatus.Ambiguous) &&
                !old.Id.StartsWith("discovery:scan", StringComparison.Ordinal) &&
                incoming.Resources.Any(fresh => fresh.EntryPath is not null &&
                    fresh.Status == CharacterDependencyStatus.Preserved &&
                    fresh.LogicalName == old.LogicalName && selectedIds.Contains(fresh.Id));
            if (!reviewedExactResolution) retained.Add(old.Id, old);
        }

        ImmutableArray<CharacterResourceRecord> resources = incoming.Resources.Where(r => !retained.ContainsKey(r.Id))
            .Concat(original.Resources.Where(r => retained.ContainsKey(r.Id)).Select(r => retained[r.Id])).ToImmutableArray();
        var payloads = refreshed.CompanionPayloads.ToBuilder();
        foreach (CharacterResourceRecord record in retained.Values.Where(r => r.EntryPath is not null))
        {
            ImmutableArray<byte> bytes = VerifyPayload(record, current.CompanionPayloads);
            if (payloads.TryGetValue(record.EntryPath!, out ImmutableArray<byte> collision) &&
                !collision.AsSpan().SequenceEqual(bytes.AsSpan()))
                throw new InvalidDataException("Companion package paths conflict with retained original or authored bytes.");
            payloads[record.EntryPath!] = bytes;
        }
        var paths = resources.Where(r => r.EntryPath is not null).Select(r => r.EntryPath!).ToHashSet(StringComparer.Ordinal);
        ImmutableDictionary<string, ImmutableArray<byte>> mergedPayloads = payloads.Where(p => paths.Contains(p.Key))
            .ToImmutableDictionary(StringComparer.Ordinal);
        var editedSubsystems = original.Resources.Where(r => archivedIds.Contains(r.Id)).Select(r => r.Subsystem).ToHashSet();
        ImmutableArray<CharacterSubsystemReview> reviews = incoming.Subsystems.Select(review =>
        {
            CharacterSubsystemReview? prior = original.Subsystems.FirstOrDefault(s => s.Subsystem == review.Subsystem);
            if (prior is null) return review;
            if (editedSubsystems.Contains(review.Subsystem) && prior.Status == CharacterDependencyStatus.Ambiguous)
                return prior;
            return prior.Status == CharacterDependencyStatus.NotApplicable && review.Status == CharacterDependencyStatus.Missing
                ? prior : review;
        }).ToImmutableArray();
        CharacterResourceInventory inventory = original with
        {
            Resources = resources, Subsystems = reviews, CompiledSemanticSha256 = null,
            LoadedResourceSha256 = null, VerifiedPlayerScenarios = [],
        };
        inventory.Validate();
        return current with
        {
            CompanionPayloads = mergedPayloads,
            Document = current.Document with { CharacterResources = inventory, LastBuildReceipt = null },
        };

        void RequireSameSource(CharacterResourceRecord old, CharacterResourceRecord fresh)
        {
            if (old.Id != fresh.Id || old.LogicalName != fresh.LogicalName ||
                old.ProviderIdentity != fresh.ProviderIdentity || old.SourceFingerprint != fresh.SourceFingerprint ||
                old.ByteLength != fresh.ByteLength ||
                !string.Equals(old.ContentSha256, fresh.ContentSha256, StringComparison.OrdinalIgnoreCase) ||
                !VerifyPayload(old, current.CompanionPayloads).AsSpan()
                    .SequenceEqual(VerifyPayload(fresh, refreshed.CompanionPayloads).AsSpan()))
                throw new InvalidDataException("An existing companion's exact source identity or content changed.");
        }

        static ImmutableArray<byte> VerifyPayload(CharacterResourceRecord record,
            ImmutableDictionary<string, ImmutableArray<byte>> packagePayloads)
        {
            if (record.EntryPath is null || !packagePayloads.TryGetValue(record.EntryPath, out ImmutableArray<byte> bytes) ||
                bytes.IsDefault || bytes.Length != record.ByteLength || record.ContentSha256 is null ||
                !Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan()))
                    .Equals(record.ContentSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A companion payload no longer matches its recorded source hash.");
            return bytes;
        }
    }

    private void PublishCharacterBounds()
    {
        if (!ShowCharacterBounds || !IsCharacterTabSelected || _model is null || SelectedBone is null) return;
        var bone = _model.Package.Document.Bones.FirstOrDefault(b => b.Name == SelectedBone.Name);
        if (bone?.LocalBounds is not { } bounds) return;
        var bones = _model.Package.Document.CreateEffectiveBones();
        var globals = new TransformMatrix[bones.Length];
        foreach (var b in bones) globals[b.Index] = b.ParentIndex < 0 ? b.ExactLocalBindMatrix : globals[b.ParentIndex] * b.ExactLocalBindMatrix;
        var frame = globals[bone.Index];
        var corners = Enumerable.Range(0,8).Select(i => frame.TransformPoint(bounds.Center + new Vector3D(
            (i & 1) == 0 ? -bounds.HalfExtents.X : bounds.HalfExtents.X,
            (i & 2) == 0 ? -bounds.HalfExtents.Y : bounds.HalfExtents.Y,
            (i & 4) == 0 ? -bounds.HalfExtents.Z : bounds.HalfExtents.Z))).ToArray();
        var lines = new List<ReAnimated.Renderer.D3D11.GizmoRenderData>();
        for (int i=0;i<8;i++) for (int axis=0;axis<3;axis++)
            if ((i & (1 << axis)) == 0) Line(corners[i],corners[i | (1 << axis)],new(1,.6f,.1f,1));
        double length = Math.Max(bounds.HalfExtents.Length, .02);
        Line(frame.Translation,frame.TransformPoint(Vector3D.UnitX*length),new(1,.2f,.2f,1));
        Line(frame.Translation,frame.TransformPoint(Vector3D.UnitY*length),new(.2f,1,.2f,1));
        Line(frame.Translation,frame.TransformPoint(Vector3D.UnitZ*length),new(.2f,.5f,1,1));
        Viewport.SceneSource.SetGizmos(lines);
        void Line(Vector3D a,Vector3D b,System.Numerics.Vector4 color) => lines.Add(new(ReAnimated.Renderer.D3D11.GizmoKind.Line,
            new((float)a.X,(float)a.Y,(float)a.Z),new((float)b.X,(float)b.Y,(float)b.Z),color,2));
    }
    private void FitBoneBounds()
    {
        if(_model is null || SelectedBone is null) return;
        try
        {
            var bounds=ReAnimated.Codecs.Models.CharacterBoneBoundsAuthoring.Fit(_model,SelectedBone.Name);
            var center=bounds.Center;var full=bounds.HalfExtents*2;
            BoneBoundsText=string.Join(" ",new[] {center.X,center.Y,center.Z,full.X,full.Y,full.Z}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)));
            BuildStatus="Bounds fitted. Review the dimensions and apply.";
        }
        catch(Exception e) when(e is ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException) {BuildStatus="Bounds fit refused: "+e.Message;}
    }

    private void ApplyBoneBounds()
    {
        if (_model is null || SelectedBone is null) return;
        try
        {
            var bounds = ReAnimated.Codecs.Models.CharacterBoneBoundsAuthoring.ParseFullDimensions(BoneBoundsText);
            var updated = ReAnimated.Codecs.Models.CharacterBoneBoundsAuthoring.UpdateDocument(
                _model.Package.Document, SelectedBone.Name, bounds, reviewed: true);
            ApplyHelperMutation(updated, SelectedBone.Name, "Bone bounds updated.");
            RefreshCharacterInventory();
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or System.IO.InvalidDataException or System.IO.IOException)
        { BuildStatus = "Bounds edit rejected: " + e.Message; }
    }
}


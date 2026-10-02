using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record RigChannelOwnerChoice(RigComponentOwner? Owner, string Label);
public sealed record RigChannelLodChoice(RigAnimationLod Lod, string Label);
public sealed record RigStockPolicyApplyScopeChoice(bool IncludeHelpers, string Label, bool OnlyUnset = false);
public sealed record RigStockRootChannelChoice(StockHumanoidRootChannel Value, string Label);

public sealed record RigChannelPolicyRow(Guid EntityId, string Name, AnimationComponentPolicy? Policy)
{
    public string Summary => Policy is null ? "No saved channel decisions" :
        $"Position: {Owners(Policy.Position)} · Rotation: {Owners(Policy.Rotation)} · Scale: {Owners(Policy.Scale)}";
    public string ExportSummary => Policy?.EmittedMask is { } mask && Policy.AnimationLod is { } lod
        ? $"{Dl1BoneScriptPolicyResolver.FormatComponents(mask)} · {Dl1BoneScriptPolicyResolver.FormatLod(lod)}" : "Export channels or LOD unset";
    private static string Owners(RigChannelOwnership channel) => channel.Owners.IsEmpty ? "unset" : string.Join(" + ", channel.Owners);
}

/// <summary>One immutable display row from a source-observed channel/LOD preview.</summary>
public sealed record RigStockChannelPolicyPreviewRow(
    string Name,
    string CurrentMask,
    string CurrentLod,
    string ProposedMask,
    string ProposedLod,
    string RawFlags,
    string SourceHash,
    string Status,
    string Reason);

public sealed record RigStockHumanoidPolicyPreviewRow(
    string Name, string CurrentMask, string ProposedMask, string Lod, string Status, string Reason);

public sealed class RigPoliciesEventArgs(FbxModelAuthoringImportResult model, RiggingJobToken token, RiggingSession session) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingJobToken Token { get; } = token;
    public RiggingSession Session { get; } = session;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private RigChannelPolicyRow? _selectedChannelPolicy;
    [ObservableProperty] private string _channelPolicyNameFilter = "";
    [ObservableProperty] private RigChannelOwnerChoice? _positionChannelOwner;
    [ObservableProperty] private RigChannelOwnerChoice? _rotationChannelOwner;
    [ObservableProperty] private RigChannelOwnerChoice? _scaleChannelOwner;
    [ObservableProperty] private RigChannelLodChoice? _channelLod;
    [ObservableProperty] private bool? _emitPositionChannel;
    [ObservableProperty] private bool? _emitRotationChannel;
    [ObservableProperty] private bool? _emitScaleChannel;
    [ObservableProperty] private string _channelPolicyStatus = "Select a node to review its saved channel decisions.";
    [ObservableProperty] private string _stockPolicyPreviewStatus = "Stock channel/LOD values have not been previewed.";
    [ObservableProperty] private ImmutableArray<RigStockChannelPolicyPreviewRow> _stockPolicyPreviewRows = [];
    [ObservableProperty] private bool _isStockPolicyPreviewRunning;
    [ObservableProperty] private bool _stockPolicyReviewed;
    [ObservableProperty] private RigChannelOwnerChoice? _stockEnabledChannelOwner;
    [ObservableProperty] private RigChannelOwnerChoice? _stockOmittedChannelOwner;
    [ObservableProperty] private RigStockPolicyApplyScopeChoice? _stockPolicyApplyScope;
    [ObservableProperty] private string _stockPolicyApplyStatus = "No observed stock policy has been applied.";
    [ObservableProperty] private RigStockRootChannelChoice? _stockHumanoidRootPosition;
    [ObservableProperty] private RigStockRootChannelChoice? _stockHumanoidRootRotation;
    [ObservableProperty] private RigStockRootChannelChoice? _stockHumanoidRootScale;
    [ObservableProperty] private bool _stockHumanoidReviewed;
    [ObservableProperty] private bool _stockHumanoidIncludeExisting;
    [ObservableProperty] private string _stockHumanoidStatus = "Choose root channels, then preview the stock-humanoid rotation-only option.";
    [ObservableProperty] private ImmutableArray<RigStockHumanoidPolicyPreviewRow> _stockHumanoidRows = [];
    private StockHumanoidChannelPolicyProposal? _stockHumanoidProposal;
    private Guid? _channelModelId;
    private Func<Dl1RigTemplate, CancellationToken, Task<Dl1MeshPreviewPayload?>>? _stockPolicySourcePicker;
    private CancellationTokenSource? _stockPolicyPreviewCancellation;
    private Dl1StockBoneScriptPolicyProposal? _stockPolicyProposal;
    private RiggingJobToken? _stockPolicyPreviewToken;

    public ObservableCollection<RigChannelPolicyRow> ChannelPolicies { get; } = [];
    public ObservableCollection<RigChannelPolicyRow> VisibleChannelPolicies { get; } = [];
    public IReadOnlyList<RigChannelOwnerChoice> ChannelOwnerChoices { get; } = [
        new(null, "Keep each node's saved owner(s)"),
        new(RigComponentOwner.BindInherited, "Rest / inherited transform"),
        new(RigComponentOwner.Clip, "Animation clip"),
        new(RigComponentOwner.Procedural, "Procedural motion"),
        new(RigComponentOwner.Attachment, "Attachment"),
        new(RigComponentOwner.RuntimeBodyScale, "Runtime body scale"),
    ];
    public IReadOnlyList<RigChannelLodChoice> ChannelLodChoices { get; } = [
        new(RigAnimationLod.Lod0, "LOD_0"), new(RigAnimationLod.Lod1, "LOD_1"),
        new(RigAnimationLod.Lod2, "LOD_2"), new(RigAnimationLod.Lod3, "LOD_3"), new(RigAnimationLod.Off, "LOD_OFF"),
    ];
    public IReadOnlyList<RigStockPolicyApplyScopeChoice> StockPolicyApplyScopes { get; } = [
        new(false, "DL1 bones only"),
        new(true, "All exact stock matches, including helpers"),
        new(true, "Only exact stock matches with no saved decisions", true),
    ];
    public IReadOnlyList<RigStockRootChannelChoice> StockHumanoidRootChoices { get; } = [
        new(StockHumanoidRootChannel.Bind, "Keep authored bind channel"),
        new(StockHumanoidRootChannel.Clip, "Accept stock clip channel"),
    ];
    public IReadOnlyList<RigChannelOwnerChoice> ExplicitChannelOwnerChoices =>
        ChannelOwnerChoices.Skip(1).ToArray();
    public bool HasChannelPolicies => ChannelPolicies.Count > 0;
    public bool CanEditChannelPolicies => HasChannelPolicies && !IsBusy;
    public bool CanApplyFilteredChannelPolicies => CanEditChannelPolicies &&
        !string.IsNullOrWhiteSpace(ChannelPolicyNameFilter) && VisibleChannelPolicies.Count > 0;
    public string FilteredChannelPolicySummary =>
        $"{VisibleChannelPolicies.Count} of {ChannelPolicies.Count} nodes match the name filter";
    public string ChannelPolicyScope => !HasStudioSession
        ? "Start a studio session in the header before reviewing animation channels. Choosing Adapt or a fit mode alone does not save channel decisions."
        : ChannelPolicies.Count == 0
            ? "No hierarchy nodes are available for channel review. See the channel status below for the specific reason."
        : $"{ChannelPolicies.Count} observed bones and helpers in this model";
    public string SelectedChannelSummary => SelectedChannelPolicy is { } row ? row.Summary + "\n" + row.ExportSummary : "No node selected.";
    public string ChannelMaskSummary => $"Position: {ChannelState(EmitPositionChannel)} · Rotation: {ChannelState(EmitRotationChannel)} · Scale: {ChannelState(EmitScaleChannel)}";
    public IRelayCommand ApplySelectedChannelPolicyCommand { get; private set; } = null!;
    public IRelayCommand ApplyFilteredChannelPoliciesCommand { get; private set; } = null!;
    public IRelayCommand ApplyAllChannelPoliciesCommand { get; private set; } = null!;
    public IRelayCommand OmitAllChannelComponentsCommand { get; private set; } = null!;
    public IAsyncRelayCommand PreviewStockChannelPoliciesCommand { get; private set; } = null!;
    public IRelayCommand ApplyReviewedStockPolicyCommand { get; private set; } = null!;
    public IRelayCommand PreviewStockHumanoidPolicyCommand { get; private set; } = null!;
    public IRelayCommand ApplyStockHumanoidPolicyCommand { get; private set; } = null!;
    public bool CanPreviewStockHumanoidPolicy => CanEditChannelPolicies && _template is not null &&
        StockHumanoidRootPosition is not null && StockHumanoidRootRotation is not null && StockHumanoidRootScale is not null;
    public bool CanApplyStockHumanoidPolicy => CanPreviewStockHumanoidPolicy && StockHumanoidReviewed &&
        _stockHumanoidProposal is not null && _model?.Package.Document.RiggingSession is { } session &&
        session.Matches(_stockHumanoidProposal.Token) &&
        _stockHumanoidProposal.Rows.Any(row => row.Status == StockHumanoidPolicyRowStatus.Proposed ||
            StockHumanoidIncludeExisting && row.Status == StockHumanoidPolicyRowStatus.ExistingDecision);
    public bool CanPreviewStockChannelPolicies => !IsBusy && !IsStockPolicyPreviewRunning && _model is not null &&
        !string.IsNullOrWhiteSpace(TemplateProfileName) &&
        _model.Package.Document.RiggingSession is not null && _stockPolicySourcePicker is not null;
    public bool CanApplyReviewedStockPolicy => !IsBusy && !IsStockPolicyPreviewRunning && StockPolicyReviewed &&
        _stockPolicyProposal is not null && _stockPolicyPreviewToken is not null &&
        StockEnabledChannelOwner?.Owner is not null && StockOmittedChannelOwner?.Owner is not null &&
        StockPolicyApplyScope is not null && _model?.Package.Document.RiggingSession is { } session &&
        session.Matches(_stockPolicyPreviewToken);
    public event EventHandler<RigPoliciesEventArgs>? RigPoliciesApplyRequested;

    private void InitializeChannelPolicies()
    {
        ApplySelectedChannelPolicyCommand = new RelayCommand(() => ApplyChannelPolicies(all: false), () => CanEditChannelPolicies && SelectedChannelPolicy is not null);
        ApplyFilteredChannelPoliciesCommand = new RelayCommand(
            () => ApplyChannelPolicies(all: false, filtered: true),
            () => CanApplyFilteredChannelPolicies);
        ApplyAllChannelPoliciesCommand = new RelayCommand(() => ApplyChannelPolicies(all: true), () => CanEditChannelPolicies);
        OmitAllChannelComponentsCommand = new RelayCommand(() =>
        {
            EmitPositionChannel = false;
            EmitRotationChannel = false;
            EmitScaleChannel = false;
        }, () => CanEditChannelPolicies);
        PreviewStockChannelPoliciesCommand = new AsyncRelayCommand(
            PreviewStockChannelPoliciesAsync,
            () => CanPreviewStockChannelPolicies);
        StockPolicyApplyScope = StockPolicyApplyScopes[0];
        ApplyReviewedStockPolicyCommand = new RelayCommand(
            ApplyReviewedStockPolicy,
            () => CanApplyReviewedStockPolicy);
        PreviewStockHumanoidPolicyCommand = new RelayCommand(PreviewStockHumanoidPolicy,
            () => CanPreviewStockHumanoidPolicy);
        ApplyStockHumanoidPolicyCommand = new RelayCommand(ApplyStockHumanoidPolicy,
            () => CanApplyStockHumanoidPolicy);
        InitializeTerminalHelperPolicies();
        FitChanged += (_, _) => InvalidateStockPolicyPreview("The fit or selected template changed. Preview the stock values again.");
        PropertyChanged += InvalidateStockPreviewOnTemplatePropertyChanged;
    }

    /// <summary>Supplies an exact decoded retail hierarchy for the currently selected template.</summary>
    public void SetStockPolicySourcePicker(
        Func<Dl1RigTemplate, CancellationToken, Task<Dl1MeshPreviewPayload?>>? picker)
    {
        if (ReferenceEquals(_stockPolicySourcePicker, picker)) return;
        _stockPolicySourcePicker = picker;
        InvalidateStockPolicyPreview(picker is null
            ? "Stock channel/LOD preview is unavailable because no source picker is connected."
            : "The source picker changed. Preview the stock values again.");
        NotifyChannelPolicies();
    }

    private void RestoreChannelPolicies()
    {
        InvalidateStockHumanoidPolicy("The source model or saved rig changed. Preview the stock-humanoid option again.");
        InvalidateStockPolicyPreview("The source model or rigging session changed. Preview the stock values again.");
        Guid? selectedId = _channelModelId == _model?.Package.Document.ModelId ? SelectedChannelPolicy?.EntityId : null;
        _channelModelId = _model?.Package.Document.ModelId;
        SelectedChannelPolicy = null;
        ChannelPolicies.Clear();
        if (_model?.Package.Document is { RiggingSession: { } session } document && session.MatchesSource(document.Source.ContentSha256))
        {
            try
            {
                var policies = session.Recipe.ComponentPolicies.ToDictionary(static p => p.EntityId);
                var entities = session.Recipe.Entities.ToDictionary(static e => e.EntityId);
                foreach (var observed in RiggingSessions.ObserveSourceHierarchy(document))
                {
                    var entity = entities[observed.EntityId];
                    ChannelPolicies.Add(new(entity.EntityId, entity.NativeName, policies.GetValueOrDefault(entity.EntityId)));
                }
                SelectedChannelPolicy = ChannelPolicies.FirstOrDefault(row => row.EntityId == selectedId) ?? ChannelPolicies.FirstOrDefault();
                ChannelPolicyStatus = "Choose channel owners, emitted channels and LOD, then apply explicitly. Undecided channels must be set before applying.";
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
            {
                ChannelPolicies.Clear();
                ChannelPolicyStatus = "Channel decisions require a current, unambiguous hierarchy: " + error.Message;
            }
        }
        RefreshVisibleChannelPolicies();
        NotifyChannelPolicies();
    }

    partial void OnChannelPolicyNameFilterChanged(string value) => RefreshVisibleChannelPolicies();

    private void RefreshVisibleChannelPolicies()
    {
        string filter = ChannelPolicyNameFilter?.Trim() ?? "";
        Guid? preferred = SelectedChannelPolicy?.EntityId;
        VisibleChannelPolicies.Clear();
        foreach (RigChannelPolicyRow row in ChannelPolicies)
        {
            if (filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                VisibleChannelPolicies.Add(row);
        }

        SelectedChannelPolicy = VisibleChannelPolicies.FirstOrDefault(row => row.EntityId == preferred)
            ?? VisibleChannelPolicies.FirstOrDefault();
        OnPropertyChanged(nameof(FilteredChannelPolicySummary));
        OnPropertyChanged(nameof(CanApplyFilteredChannelPolicies));
        ApplyFilteredChannelPoliciesCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedChannelPolicyChanged(RigChannelPolicyRow? value)
    {
        // Keep existing owners by default, including mixed ownership and its evidence.
        PositionChannelOwner = ChannelOwnerChoices[0];
        RotationChannelOwner = ChannelOwnerChoices[0];
        ScaleChannelOwner = ChannelOwnerChoices[0];
        var mask = value?.Policy?.EmittedMask;
        EmitPositionChannel = mask?.HasFlag(RigAnimationComponents.Position);
        EmitRotationChannel = mask?.HasFlag(RigAnimationComponents.Rotation);
        EmitScaleChannel = mask?.HasFlag(RigAnimationComponents.Scale);
        ChannelLod = ChannelLodChoices.FirstOrDefault(choice => choice.Lod == value?.Policy?.AnimationLod);
        OnPropertyChanged(nameof(SelectedChannelSummary));
        NotifyChannelPolicies();
    }

    partial void OnEmitPositionChannelChanged(bool? value) => OnPropertyChanged(nameof(ChannelMaskSummary));
    partial void OnEmitRotationChannelChanged(bool? value) => OnPropertyChanged(nameof(ChannelMaskSummary));
    partial void OnEmitScaleChannelChanged(bool? value) => OnPropertyChanged(nameof(ChannelMaskSummary));
    partial void OnStockPolicyReviewedChanged(bool value) => NotifyStockPolicyApplyState();
    partial void OnStockEnabledChannelOwnerChanged(RigChannelOwnerChoice? value) => NotifyStockPolicyApplyState();
    partial void OnStockOmittedChannelOwnerChanged(RigChannelOwnerChoice? value) => NotifyStockPolicyApplyState();
    partial void OnStockPolicyApplyScopeChanged(RigStockPolicyApplyScopeChoice? value) => NotifyStockPolicyApplyState();
    partial void OnStockHumanoidRootPositionChanged(RigStockRootChannelChoice? value) => InvalidateStockHumanoidPolicy("Root choices changed. Preview again before applying.");
    partial void OnStockHumanoidRootRotationChanged(RigStockRootChannelChoice? value) => InvalidateStockHumanoidPolicy("Root choices changed. Preview again before applying.");
    partial void OnStockHumanoidRootScaleChanged(RigStockRootChannelChoice? value) => InvalidateStockHumanoidPolicy("Root choices changed. Preview again before applying.");
    partial void OnStockHumanoidReviewedChanged(bool value) => NotifyStockHumanoidState();
    partial void OnStockHumanoidIncludeExistingChanged(bool value)
    {
        // The overwrite scope changes the meaning of the review acknowledgment.
        StockHumanoidReviewed = false;
        NotifyStockHumanoidState();
    }
    private static string ChannelState(bool? value) => value switch { true => "export", false => "omit", null => "undecided" };

    private void ApplyChannelPolicies(bool all, bool filtered = false)
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not { } current || !CanEditChannelPolicies) return;
        if (ChannelLod is not { } lod || EmitPositionChannel is not { } position || EmitRotationChannel is not { } rotation || EmitScaleChannel is not { } scale ||
            PositionChannelOwner is null || RotationChannelOwner is null || ScaleChannelOwner is null)
        {
            ChannelPolicyStatus = "Choose each emitted channel (on or off), all three owner choices and an animation LOD before applying.";
            return;
        }
        var mask = (position ? RigAnimationComponents.Position : RigAnimationComponents.None) |
            (rotation ? RigAnimationComponents.Rotation : RigAnimationComponents.None) |
            (scale ? RigAnimationComponents.Scale : RigAnimationComponents.None);
        var rows = filtered && CanApplyFilteredChannelPolicies
            ? VisibleChannelPolicies.ToArray()
            : all ? ChannelPolicies.ToArray()
            : !filtered && SelectedChannelPolicy is { } selected
                ? [selected]
                : Array.Empty<RigChannelPolicyRow>();
        if (rows.Length == 0) return;
        var token = current.CreateJobToken();
        var edits = rows.Select(row => new RigComponentPolicyEdit(row.EntityId, mask, lod.Lod,
            PositionChannelOwner.Owner, RotationChannelOwner.Owner, ScaleChannelOwner.Owner)).ToImmutableArray();
        try
        {
            if (!RigComponentPolicyAuthoring.TryApply(model.Package.Document, token, edits, out var updated))
            { ChannelPolicyStatus = "The rig inputs changed. Review the current node before applying."; return; }
            if (ReferenceEquals(updated, current))
            { ChannelPolicyStatus = "These channel decisions are already saved."; return; }
            InvalidateStockPolicyPreview("The channel policy session changed. Preview the stock values again.");
            RigPoliciesApplyRequested?.Invoke(this, new(model, token, updated));
            ChannelPolicyStatus = $"Applied channel decisions for {rows.Length} node(s). These record authoring intent; native animation behavior is still unverified.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        { ChannelPolicyStatus = "Channel decisions were not applied: " + error.Message; }
    }

    private void NotifyChannelPolicies()
    {
        OnPropertyChanged(nameof(HasChannelPolicies));
        OnPropertyChanged(nameof(CanEditChannelPolicies));
        OnPropertyChanged(nameof(ChannelPolicyScope));
        ApplySelectedChannelPolicyCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanApplyFilteredChannelPolicies));
        ApplyFilteredChannelPoliciesCommand?.NotifyCanExecuteChanged();
        ApplyAllChannelPoliciesCommand?.NotifyCanExecuteChanged();
        OmitAllChannelComponentsCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPreviewStockChannelPolicies));
        PreviewStockChannelPoliciesCommand?.NotifyCanExecuteChanged();
        NotifyStockPolicyApplyState();
        NotifyStockHumanoidState();
        NotifyTerminalHelperPolicyState();
    }

    private void NotifyStockHumanoidState()
    {
        OnPropertyChanged(nameof(CanPreviewStockHumanoidPolicy));
        OnPropertyChanged(nameof(CanApplyStockHumanoidPolicy));
        PreviewStockHumanoidPolicyCommand?.NotifyCanExecuteChanged();
        ApplyStockHumanoidPolicyCommand?.NotifyCanExecuteChanged();
    }

    private void InvalidateStockHumanoidPolicy(string message)
    {
        _stockHumanoidProposal = null;
        StockHumanoidRows = [];
        StockHumanoidReviewed = false;
        StockHumanoidStatus = StockHumanoidRootPosition is null || StockHumanoidRootRotation is null || StockHumanoidRootScale is null
            ? "Choose root position, rotation, and scale, then preview the proposed channel choices."
            : message;
        NotifyStockHumanoidState();
    }

    private void PreviewStockHumanoidPolicy()
    {
        if (!CanPreviewStockHumanoidPolicy || _model is not { } model || _template is not { } template ||
            StockHumanoidRootPosition is not { } position || StockHumanoidRootRotation is not { } rotation ||
            StockHumanoidRootScale is not { } scale) return;
        try
        {
            StockHumanoidChannelPolicyProposal proposal = StockHumanoidChannelPolicyAuthoring.Propose(
                model.Package.Document, template, position.Value, rotation.Value, scale.Value);
            _stockHumanoidProposal = proposal;
            StockHumanoidRows = proposal.Rows.Select(row => new RigStockHumanoidPolicyPreviewRow(
                row.Name, row.CurrentMask?.ToString() ?? "Unset", row.ProposedMask?.ToString() ?? "Unchanged",
                row.ProposedLod?.ToString() ?? "Unchanged", row.Status.ToString(), row.Reason)).ToImmutableArray();
            StockHumanoidReviewed = false;
            int fresh = proposal.Rows.Count(row => row.Status == StockHumanoidPolicyRowStatus.Proposed);
            int existing = proposal.Rows.Count(row => row.Status == StockHumanoidPolicyRowStatus.ExistingDecision);
            StockHumanoidStatus = $"Read-only preview: {fresh} new and {existing} existing decisions; " +
                "extras and helpers remain unchanged. Native animation behavior is unverified.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            InvalidateStockHumanoidPolicy("Unable to preview: " + error.Message);
        }
        NotifyStockHumanoidState();
    }

    private void ApplyStockHumanoidPolicy()
    {
        if (!CanApplyStockHumanoidPolicy || _model is not { } model || _template is not { } template ||
            _stockHumanoidProposal is not { } proposal) return;
        try
        {
            if (!StockHumanoidChannelPolicyAuthoring.TryApply(model.Package.Document, template, proposal,
                    StockHumanoidIncludeExisting, StockHumanoidReviewed, out RiggingSession updated))
            {
                InvalidateStockHumanoidPolicy("The rigging session changed. Preview again.");
                return;
            }
            if (RigPoliciesApplyRequested is null)
            {
                StockHumanoidStatus = "No workspace commit handler is available.";
                return;
            }
            CustomModelDocument updatedDocument = model.Package.Document with
            {
                RiggingSession = updated,
                LastBuildReceipt = null,
            };
            var byEntity = updated.Recipe.ComponentPolicies.ToDictionary(static row => row.EntityId);
            int undecided = RiggingSessions.ObserveSourceHierarchy(updatedDocument).Count(row =>
                !byEntity.TryGetValue(row.EntityId, out AnimationComponentPolicy? policy) ||
                policy.EmittedMask is null || policy.AnimationLod is null);
            RigPoliciesApplyRequested.Invoke(this, new(model, proposal.Token, updated));
            StockHumanoidStatus = "Saved reviewed stock-humanoid channel choices as one undoable edit. " +
                (undecided == 0
                    ? "Every observed node now has a channel mask and LOD choice. "
                    : $"{undecided} observed node(s) still need channel/LOD choices. Preview exact stock matches with the only-unset scope; its owner choices apply uniformly, so review camera and other helpers separately before accepting them. Review unmatched extras individually before export. ") +
                "Check the exact animation bank in Editor and Player; some poses need clip translations.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            StockHumanoidStatus = "Stock-humanoid policy was not applied: " + error.Message;
        }
    }

    private void NotifyStockPolicyApplyState()
    {
        OnPropertyChanged(nameof(CanApplyReviewedStockPolicy));
        ApplyReviewedStockPolicyCommand?.NotifyCanExecuteChanged();
    }

    private void ApplyReviewedStockPolicy()
    {
        if (!CanApplyReviewedStockPolicy || _model is not { } model || _template is not { } template ||
            _stockPolicyProposal is not { } proposal || _stockPolicyPreviewToken is not { } token ||
            StockEnabledChannelOwner?.Owner is not { } enabledOwner ||
            StockOmittedChannelOwner?.Owner is not { } omittedOwner ||
            StockPolicyApplyScope is not { } scope)
            return;

        var current = model.Package.Document;
        if (current.RiggingSession is not { } session || !session.Matches(token))
        {
            StockPolicyApplyStatus = "The rigging session changed. Preview and review the stock values again.";
            InvalidateStockPolicyPreview(StockPolicyApplyStatus);
            return;
        }

        var kinds = session.Recipe.Entities.ToDictionary(static entity => entity.EntityId, static entity => entity.Kind);
        var existingIds = session.Recipe.ComponentPolicies.Select(static policy => policy.EntityId).ToHashSet();
        Dl1StockBoneScriptPolicyReviewRow[] selected = proposal.Rows.Where(row =>
            row.Status == Dl1StockPolicyProposalStatus.Proposed &&
            row.DestinationEntityId is { } id &&
            (scope.IncludeHelpers || kinds.GetValueOrDefault(id) == RigNativeEntityKind.Bone) &&
            (!scope.OnlyUnset || !existingIds.Contains(id))).ToArray();
        if (selected.Length == 0)
        {
            StockPolicyApplyStatus = "The selected scope contains no exact stock-policy matches.";
            return;
        }

        var decisions = selected.Select(row =>
        {
            RigAnimationComponents mask = row.ProposedMask!.Value;
            return new Dl1StockBoneScriptOwnerDecision(
                row.DestinationEntityId!.Value,
                mask.HasFlag(RigAnimationComponents.Position) ? enabledOwner : omittedOwner,
                mask.HasFlag(RigAnimationComponents.Rotation) ? enabledOwner : omittedOwner,
                mask.HasFlag(RigAnimationComponents.Scale) ? enabledOwner : omittedOwner);
        }).ToArray();
        try
        {
            Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(model);
            var acknowledgment = new Dl1StockBoneScriptPolicyReviewAcknowledgment(
                proposal.Fingerprint, proposal.SourceSha256, StockPolicyReviewed);
            if (!Dl1StockBoneScriptPolicyReviewApplyService.TryApply(
                    current, token, template, prepared, proposal, decisions, acknowledgment,
                    out CustomModelDocument updated))
            {
                StockPolicyApplyStatus = "The rigging session changed before the reviewed stock rows could be applied.";
                InvalidateStockPolicyPreview(StockPolicyApplyStatus);
                return;
            }
            if (ReferenceEquals(updated, current))
            {
                StockPolicyApplyStatus = "These reviewed stock values and observations are already saved.";
                return;
            }
            if (RigPoliciesApplyRequested is null)
            {
                StockPolicyApplyStatus = "No workspace commit handler is available for this review.";
                return;
            }

            RigPoliciesApplyRequested.Invoke(this, new(model, token, updated.RiggingSession!));
            StockPolicyApplyStatus = _model?.Package.Document.RiggingSession is { } saved && !saved.Matches(token)
                ? $"Saved {selected.Length} reviewed stock mask/LOD rows with explicit authoring owner choices. Native behavior remains unverified; unmatched extras were not changed."
                : "The reviewed stock policy request was not committed; inspect the current model and retry.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            StockPolicyApplyStatus = "Reviewed stock policy was not applied: " + error.Message;
        }
    }

    private async Task PreviewStockChannelPoliciesAsync(CancellationToken commandToken)
    {
        if (!CanPreviewStockChannelPolicies || _model is not { } model ||
            _stockPolicySourcePicker is not { } picker || model.Package.Document.RiggingSession is not { } session)
            return;
        if (!session.MatchesSource(model.Package.Document.Source.ContentSha256))
        {
            StockPolicyPreviewRows = [];
            StockPolicyPreviewStatus = "The destination rigging session is stale for this source model.";
            return;
        }

        // Resolve before opening a preview job: selecting a reference invalidates
        // prior jobs, and must not cancel the job that is about to decode it.
        if (_template is null || !string.Equals(_template.ProfileName, TemplateProfileName, StringComparison.Ordinal))
            await ResolveTemplateAsync(commandToken).ConfigureAwait(true);
        commandToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_model, model) || _template is not { } template ||
            !string.Equals(template.ProfileName, TemplateProfileName, StringComparison.Ordinal))
        {
            StockPolicyPreviewStatus = "The source or selected template changed while its reference was loading.";
            return;
        }

        _stockPolicyPreviewCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(commandToken);
        _stockPolicyPreviewCancellation = cancellation;
        RiggingJobToken jobToken = session.CreateJobToken();
        IsStockPolicyPreviewRunning = true;
        _stockPolicyProposal = null;
        _stockPolicyPreviewToken = null;
        StockPolicyReviewed = false;
        StockPolicyPreviewRows = [];
        StockPolicyPreviewStatus = "Decoding the selected stock reference and preparing a read-only proposal…";
        NotifyChannelPolicies();
        try
        {
            Dl1MeshPreviewPayload? payload = await picker(template, cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentStockPolicyPreview(model, template, jobToken))
            {
                InvalidateStockPolicyPreview("The source, session, or selected template changed while the preview was loading.");
                return;
            }
            if (payload is null)
            {
                StockPolicyPreviewStatus = "The stock source picker returned no decoded hierarchy.";
                return;
            }
            if (!string.Equals(payload.Source.ResourceName, template.SourceResourceName, StringComparison.Ordinal) ||
                !string.Equals(payload.ResourceSha256, template.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                StockPolicyPreviewStatus = "The decoded source resource name or hash does not match the selected template.";
                return;
            }

            Dl1StockBoneScriptPolicyProposal proposal = await Task.Run(() =>
            {
                Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(model, cancellation.Token);
                return Dl1StockBoneScriptPolicyProposalService.Propose(template, payload.Source.Hierarchy,
                    payload.ResourceSha256!, model.Package.Document, prepared);
            }, cancellation.Token).ConfigureAwait(true);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentStockPolicyPreview(model, template, jobToken))
            {
                InvalidateStockPolicyPreview("The source, session, or selected template changed while the preview was being prepared.");
                return;
            }
            _stockPolicyProposal = proposal;
            _stockPolicyPreviewToken = jobToken;
            StockPolicyApplyScope = model.Package.Document.RiggingSession?.Recipe.ComponentPolicies.IsEmpty == false
                ? StockPolicyApplyScopes.Single(static choice => choice.OnlyUnset)
                : StockPolicyApplyScopes[0];
            StockPolicyReviewed = false;
            StockPolicyPreviewRows = proposal.Rows.Select(ToPreviewRow).ToImmutableArray();
            int unresolved = proposal.Rows.Count(static row => row.Status != Dl1StockPolicyProposalStatus.Proposed);
            StockPolicyPreviewStatus = $"Read-only preview from {payload.Source.ResourceName} ({proposal.SourceSha256}); proposal {proposal.Fingerprint}; {proposal.Rows.Length - unresolved} proposed and {unresolved} unresolved. No policy was applied.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (ReferenceEquals(_stockPolicyPreviewCancellation, cancellation))
                StockPolicyPreviewStatus = "Stock channel/LOD preview was cancelled.";
        }
        catch (Exception error)
        {
            if (ReferenceEquals(_stockPolicyPreviewCancellation, cancellation))
            {
                StockPolicyPreviewRows = [];
                StockPolicyPreviewStatus = "Stock channel/LOD preview failed: " + error.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_stockPolicyPreviewCancellation, cancellation))
            {
                _stockPolicyPreviewCancellation = null;
                IsStockPolicyPreviewRunning = false;
                NotifyChannelPolicies();
            }
            cancellation.Dispose();
        }
    }

    private bool IsCurrentStockPolicyPreview(FbxModelAuthoringImportResult model,
        Dl1RigTemplate template, RiggingJobToken token) =>
        ReferenceEquals(_model, model) && ReferenceEquals(_template, template) &&
        _model?.Package.Document.RiggingSession is { } current && current.Matches(token);

    private void InvalidateStockPolicyPreview(string status)
    {
        _stockPolicyPreviewCancellation?.Cancel();
        _stockPolicyPreviewCancellation = null;
        _stockPolicyProposal = null;
        _stockPolicyPreviewToken = null;
        StockPolicyReviewed = false;
        IsStockPolicyPreviewRunning = false;
        StockPolicyPreviewRows = [];
        StockPolicyPreviewStatus = status;
        InvalidateTerminalHelperPolicy("The source, fit, or stock comparison changed. Preview terminal helpers again.");
        NotifyChannelPolicies();
    }

    private void InvalidateStockPreviewOnTemplatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TemplateProfileName) or nameof(TemplateStatus))
        {
            InvalidateStockPolicyPreview("The selected template changed or was re-resolved. Preview the stock values again.");
            InvalidateStockHumanoidPolicy("The selected template changed. Preview the stock-humanoid option again.");
        }
    }

    private static RigStockChannelPolicyPreviewRow ToPreviewRow(Dl1StockBoneScriptPolicyReviewRow row) => new(
        row.DestinationName,
        row.CurrentMask?.ToString() ?? "Unset",
        row.CurrentLod?.ToString() ?? "Unset",
        row.ProposedMask?.ToString() ?? "Unresolved",
        row.ProposedLod?.ToString() ?? "Unresolved",
        row.RawFlags is { } flags ? $"0x{flags:X8}" : "—",
        row.SourceSha256 ?? "—",
        row.Status.ToString(),
        row.Reason);
}

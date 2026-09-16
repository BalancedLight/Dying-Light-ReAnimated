using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record RigChannelOwnerChoice(RigComponentOwner? Owner, string Label);
public sealed record RigChannelLodChoice(RigAnimationLod Lod, string Label);

public sealed record RigChannelPolicyRow(Guid EntityId, string Name, AnimationComponentPolicy? Policy)
{
    public string Summary => Policy is null ? "No saved channel decisions" :
        $"Position: {Owners(Policy.Position)} · Rotation: {Owners(Policy.Rotation)} · Scale: {Owners(Policy.Scale)}";
    public string ExportSummary => Policy?.EmittedMask is { } mask && Policy.AnimationLod is { } lod
        ? $"{Dl1BoneScriptPolicyResolver.FormatComponents(mask)} · {Dl1BoneScriptPolicyResolver.FormatLod(lod)}" : "Export channels or LOD unset";
    private static string Owners(RigChannelOwnership channel) => channel.Owners.IsEmpty ? "unset" : string.Join(" + ", channel.Owners);
}

public sealed class RigPoliciesEventArgs(FbxModelAuthoringImportResult model, RiggingJobToken token, RiggingSession session) : EventArgs
{
    public FbxModelAuthoringImportResult Model { get; } = model;
    public RiggingJobToken Token { get; } = token;
    public RiggingSession Session { get; } = session;
}

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private RigChannelPolicyRow? _selectedChannelPolicy;
    [ObservableProperty] private RigChannelOwnerChoice? _positionChannelOwner;
    [ObservableProperty] private RigChannelOwnerChoice? _rotationChannelOwner;
    [ObservableProperty] private RigChannelOwnerChoice? _scaleChannelOwner;
    [ObservableProperty] private RigChannelLodChoice? _channelLod;
    [ObservableProperty] private bool? _emitPositionChannel;
    [ObservableProperty] private bool? _emitRotationChannel;
    [ObservableProperty] private bool? _emitScaleChannel;
    [ObservableProperty] private string _channelPolicyStatus = "Select a node to review its saved channel decisions.";
    private Guid? _channelModelId;

    public ObservableCollection<RigChannelPolicyRow> ChannelPolicies { get; } = [];
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
    public bool HasChannelPolicies => ChannelPolicies.Count > 0;
    public bool CanEditChannelPolicies => HasChannelPolicies && !IsBusy;
    public string ChannelPolicyScope => $"{ChannelPolicies.Count} observed bones and helpers in this model";
    public string SelectedChannelSummary => SelectedChannelPolicy is { } row ? row.Summary + "\n" + row.ExportSummary : "No node selected.";
    public string ChannelMaskSummary => $"Position: {ChannelState(EmitPositionChannel)} · Rotation: {ChannelState(EmitRotationChannel)} · Scale: {ChannelState(EmitScaleChannel)}";
    public IRelayCommand ApplySelectedChannelPolicyCommand { get; private set; } = null!;
    public IRelayCommand ApplyAllChannelPoliciesCommand { get; private set; } = null!;
    public event EventHandler<RigPoliciesEventArgs>? RigPoliciesApplyRequested;

    private void InitializeChannelPolicies()
    {
        ApplySelectedChannelPolicyCommand = new RelayCommand(() => ApplyChannelPolicies(all: false), () => CanEditChannelPolicies && SelectedChannelPolicy is not null);
        ApplyAllChannelPoliciesCommand = new RelayCommand(() => ApplyChannelPolicies(all: true), () => CanEditChannelPolicies);
    }

    private void RestoreChannelPolicies()
    {
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
        NotifyChannelPolicies();
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
    private static string ChannelState(bool? value) => value switch { true => "export", false => "omit", null => "undecided" };

    private void ApplyChannelPolicies(bool all)
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
        var rows = all ? ChannelPolicies.ToArray() : SelectedChannelPolicy is { } selected ? [selected] : Array.Empty<RigChannelPolicyRow>();
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
        ApplyAllChannelPoliciesCommand?.NotifyCanExecuteChanged();
    }
}

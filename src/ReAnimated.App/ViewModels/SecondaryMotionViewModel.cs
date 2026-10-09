using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class SecondaryMotionViewModel : ObservableObject
{
    private SecondaryMotionDefinition definition = new();
    private string? selectedGroup;
    private bool enabled = true;
    private bool showAnchors;
    private bool showCollisions;
    private RelayCommand? _undoCollisionEditCommand;
    private RelayCommand? _newCollisionProposalCommand;
    private readonly Stack<ColliderHistory> _collisionUndo = new();
    private int? _selectedColliderIndex;
    private bool _hasCollisionDraft;
    private string? _startBoneName;
    private string? _endBoneName;
    private string _collisionShape = "Sphere";
    private string _startOffsetText = "0 0 0";
    private string _endOffsetText = "0 0 0";
    private string _collisionRadiusText = "0.05";
    private string collisionDetails = string.Empty;
    private ImmutableArray<Dl1NativeClothCollisionOverlay> nativeCollisionOverlays = [];
    private string status = "Select a custom model or load an editor setup. MPC preview is an approximation.";
    private string persistenceStatus = "Save model copy creates a new package. Open that copy in Models and save the project to retain its settings.";
    public event EventHandler? Changed;
    public ObservableCollection<string> GroupNames { get; } = [];
    public ObservableCollection<string> BoneNames { get; } = [];
    public ObservableCollection<string> ColliderNames { get; } = [];
    public IReadOnlyList<string> CollisionShapeOptions { get; } = ["Sphere", "Capsule"];
    public SecondaryMotionDefinition Definition => definition;
    public SecondaryCollider? CurrentColliderProposal => CreateProposal();
    public ImmutableArray<Dl1NativeClothCollisionOverlay> NativeCollisionOverlays => nativeCollisionOverlays;
    public IRelayCommand ResetCommand { get; set; } = null!;
    public IRelayCommand LoadSetupCommand { get; set; } = null!;
    public IRelayCommand SaveSetupCommand { get; set; } = null!;
    public IRelayCommand ImportNativeCommand { get; set; } = null!;
    public IRelayCommand ExportNativeCommand { get; set; } = null!;
    public IRelayCommand SaveModelCopyCommand { get; set; } = null!;
    public IRelayCommand FitCollisionSphereCommand { get; set; } = null!;
    public IRelayCommand FitCollisionCapsuleCommand { get; set; } = null!;
    public IRelayCommand ApplyCollisionProposalCommand { get; set; } = null!;
    public RelayCommand NewCollisionProposalCommand => _newCollisionProposalCommand ??= new(NewCollisionProposal, () => Group is not null);
    public RelayCommand UndoCollisionEditCommand => _undoCollisionEditCommand ??= new(UndoCollisionEdit, () => _collisionUndo.Count > 0);
    public bool Enabled { get => enabled; set { if (SetProperty(ref enabled, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool ShowAnchors { get => showAnchors; set { if (SetProperty(ref showAnchors, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool ShowCollisions { get => showCollisions; set { if (SetProperty(ref showCollisions, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string PersistenceStatus { get => persistenceStatus; set => SetProperty(ref persistenceStatus, value); }
    public string CollisionDetails { get => collisionDetails; set => SetProperty(ref collisionDetails, value); }
    public string? StartBoneName { get => _startBoneName; set { if (SetProperty(ref _startBoneName, value)) DraftEdited(); } }
    public string? EndBoneName { get => _endBoneName; set { if (SetProperty(ref _endBoneName, value)) DraftEdited(); } }
    public string CollisionShape { get => _collisionShape; set { if (SetProperty(ref _collisionShape, value)) DraftEdited(); } }
    public string StartOffsetText { get => _startOffsetText; set { if (SetProperty(ref _startOffsetText, value)) DraftEdited(); } }
    public string EndOffsetText { get => _endOffsetText; set { if (SetProperty(ref _endOffsetText, value)) DraftEdited(); } }
    public string CollisionRadiusText { get => _collisionRadiusText; set { if (SetProperty(ref _collisionRadiusText, value)) DraftEdited(); } }
    public int? SelectedColliderIndex
    {
        get => _selectedColliderIndex;
        set
        {
            if (!SetProperty(ref _selectedColliderIndex, value)) return;
            if (value is int index && Group is { } group && (uint)index < (uint)group.Colliders.Length)
                SetProposal(group.Colliders[index]);
            else ResetProposal();
            OnPropertyChanged(nameof(CurrentColliderProposal));
            CollisionPreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string? SelectedGroup
    {
        get => selectedGroup;
        set
        {
            if (!SetProperty(ref selectedGroup, value)) return;
            SelectedColliderIndex = null;
            RefreshColliderChoices();
            NewCollisionProposalCommand.NotifyCanExecuteChanged();
            NotifySettings();
        }
    }
    public bool GroupEnabled { get => Group?.Enabled ?? false; set => Change(g => g with { Enabled = value }); }
    public double Damping { get => Group?.Preview.Damping ?? 3; set => Tune(s => s with { Damping = Math.Clamp(value, 0, 1000) }); }
    public double Stiffness { get => Group?.Preview.StructuralStiffness ?? 0.95; set => Tune(s => s with { StructuralStiffness = Math.Clamp(value, 0, 1) }); }
    public double BendStiffness { get => Group?.Preview.BendStiffness ?? 0.3; set => Tune(s => s with { BendStiffness = Math.Clamp(value, 0, 1) }); }
    public double MotionInfluence { get => Group?.Preview.AnimationFollow ?? 0.65; set => Tune(s => s with { AnimationFollow = Math.Clamp(value, 0, 1) }); }
    public double RestShapeStiffness { get => Group?.Preview.RestShapeStiffness ?? 0; set => Tune(s => s with { RestShapeStiffness = Math.Clamp(value, 0, 1000) }); }
    public double PreviewActorScale
    {
        get => definition.PreviewActorScale;
        set
        {
            if (!double.IsFinite(value) || value < 0.1 || value > 4)
            {
                Status = "Actor scale must be between 0.1 and 4.";
                return;
            }
            if (definition.PreviewActorScale == value) return;
            SecondaryMotionDefinition next = definition with { PreviewActorScale = value };
            next.Validate(BoneNames.Count > 0 ? BoneNames : null);
            definition = next;
            OnPropertyChanged(nameof(Definition));
            OnPropertyChanged(nameof(PreviewActorScale));
            Status = "Actor scale updated.";
            PersistenceStatus = "Save a model copy to retain this preview scale.";
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    private SecondaryMotionGroup? Group => definition.Groups.FirstOrDefault(g => g.Name == selectedGroup);
    public event EventHandler? CollisionPreviewChanged;

    public void Load(SecondaryMotionDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        definition = value;
        _nativeCollisionUndo.Clear();
        RefreshNativeCollisionChoices();
        _collisionUndo.Clear();
        _selectedColliderIndex = null;
        _hasCollisionDraft = false;
        UndoCollisionEditCommand.NotifyCanExecuteChanged();
        GroupNames.Clear();
        foreach (SecondaryMotionGroup group in value.Groups) GroupNames.Add(group.Name);
        selectedGroup = GroupNames.FirstOrDefault();
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(SelectedGroup));
        OnPropertyChanged(nameof(SelectedColliderIndex));
        OnPropertyChanged(nameof(PreviewActorScale));
        RefreshColliderChoices();
        NewCollisionProposalCommand.NotifyCanExecuteChanged();
        NotifySettings();
        Status = $"{value.Groups.Length} preview groups; {value.NativeSources.Length} losslessly retained native scripts. MPC preview approximation.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCollisionBoneNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] next = names.Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (BoneNames.SequenceEqual(next, StringComparer.Ordinal)) return;
        BoneNames.Clear();
        foreach (string name in next) BoneNames.Add(name);
        if (_startBoneName is null || !BoneNames.Contains(_startBoneName)) _startBoneName = BoneNames.FirstOrDefault();
        if (_endBoneName is not null && !BoneNames.Contains(_endBoneName)) _endBoneName = null;
        OnPropertyChanged(nameof(StartBoneName));
        OnPropertyChanged(nameof(EndBoneName));
        DraftChanged();
    }

    public void SetCollisionProposal(SecondaryCollider proposal)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        SetProposal(proposal);
        OnPropertyChanged(nameof(CurrentColliderProposal));
        CollisionPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetNativeCollisionOverlays(Dl1NativeClothCollisionOverlayResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        string details = string.Join(Environment.NewLine, resolution.Diagnostics);
        if (nativeCollisionOverlays.SequenceEqual(resolution.Overlays) && collisionDetails == details) return;
        nativeCollisionOverlays = resolution.Overlays;
        CollisionDetails = details;
        OnPropertyChanged(nameof(NativeCollisionOverlays));
        CollisionPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Tune(Func<SecondaryPreviewSettings, SecondaryPreviewSettings> edit) => Change(g => g with { Preview = edit(g.Preview) });
    public void ApplyCollisionProposal(SecondaryCollider collider)
    {
        ArgumentNullException.ThrowIfNull(collider);
        if (Group is not { } group) throw new InvalidOperationException("Select a motion group before applying a collision.");
        var colliders = group.Colliders.ToBuilder();
        int targetIndex;
        if (_selectedColliderIndex is int selected && (uint)selected < (uint)colliders.Count)
        {
            targetIndex = selected;
            colliders[selected] = collider;
        }
        else
        {
            targetIndex = colliders.Count;
            colliders.Add(collider);
        }
        CommitColliders(group, colliders.ToImmutable());
        _selectedColliderIndex = targetIndex;
        SetProposal(collider);
        RefreshColliderChoices();
        OnPropertyChanged(nameof(SelectedColliderIndex));
        Status = "Collision applied.";
        PersistenceStatus = "Save a model copy to retain this collision.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void NewCollisionProposal()
    {
        if (Group is null) return;
        _selectedColliderIndex = null;
        OnPropertyChanged(nameof(SelectedColliderIndex));
        ResetProposal();
        DraftEdited();
        Status = "New collision proposal ready.";
    }

    private void UndoCollisionEdit()
    {
        if (_collisionUndo.Count == 0) return;
        ColliderHistory snapshot = _collisionUndo.Pop();
        SecondaryMotionGroup? group = definition.Groups.FirstOrDefault(candidate => candidate.Name == snapshot.GroupName);
        if (group is null) return;
        if (!string.Equals(SelectedGroup, snapshot.GroupName, StringComparison.Ordinal))
            SelectedGroup = snapshot.GroupName;
        definition = definition with { Groups = definition.Groups.Replace(group, group with { Colliders = snapshot.Before }) };
        _selectedColliderIndex = snapshot.SelectedIndex;
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(SelectedColliderIndex));
        RefreshColliderChoices();
        if (_selectedColliderIndex is int index && (uint)index < (uint)snapshot.Before.Length)
            SetProposal(snapshot.Before[index]);
        else ResetProposal();
        UndoCollisionEditCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CurrentColliderProposal));
        Status = "Collision edit undone.";
        PersistenceStatus = "Save a model copy to retain collision edits.";
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CommitColliders(SecondaryMotionGroup group, ImmutableArray<SecondaryCollider> colliders)
    {
        SecondaryMotionGroup updated = group with { Colliders = colliders };
        SecondaryMotionDefinition next = definition with { Groups = definition.Groups.Replace(group, updated) };
        next.Validate(BoneNames.Count > 0 ? BoneNames : null);
        _collisionUndo.Push(new(group.Name, group.Colliders, _selectedColliderIndex));
        _collisionUndoCommandChanged();
        definition = next;
        OnPropertyChanged(nameof(Definition));
        RefreshColliderChoices();
        OnPropertyChanged(nameof(CurrentColliderProposal));
        CollisionPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void _collisionUndoCommandChanged() => UndoCollisionEditCommand.NotifyCanExecuteChanged();

    private void RefreshColliderChoices()
    {
        ColliderNames.Clear();
        if (Group is not { } group) return;
        foreach (SecondaryCollider collider in group.Colliders)
        {
            string kind = collider.EndBoneName is null ? "Sphere" : "Capsule";
            ColliderNames.Add($"{kind} · {collider.BoneName}" + (collider.EndBoneName is { } end ? $" → {end}" : string.Empty));
        }
        if (_selectedColliderIndex is int selected && selected >= group.Colliders.Length)
            _selectedColliderIndex = null;
    }

    private void ResetProposal()
    {
        _hasCollisionDraft = false;
        _startBoneName = BoneNames.FirstOrDefault();
        _endBoneName = null;
        _collisionShape = "Sphere";
        _startOffsetText = "0 0 0";
        _endOffsetText = "0 0 0";
        _collisionRadiusText = "0.05";
        OnPropertyChanged(nameof(StartBoneName));
        OnPropertyChanged(nameof(EndBoneName));
        OnPropertyChanged(nameof(CollisionShape));
        OnPropertyChanged(nameof(StartOffsetText));
        OnPropertyChanged(nameof(EndOffsetText));
        OnPropertyChanged(nameof(CollisionRadiusText));
    }

    private void SetProposal(SecondaryCollider collider)
    {
        _hasCollisionDraft = true;
        _startBoneName = collider.BoneName;
        _endBoneName = collider.EndBoneName;
        _collisionShape = collider.EndBoneName is null ? "Sphere" : "Capsule";
        _startOffsetText = Format(collider.LocalPosition);
        _endOffsetText = Format(collider.EndLocalPosition);
        _collisionRadiusText = collider.Radius.ToString("R", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(StartBoneName));
        OnPropertyChanged(nameof(EndBoneName));
        OnPropertyChanged(nameof(CollisionShape));
        OnPropertyChanged(nameof(StartOffsetText));
        OnPropertyChanged(nameof(EndOffsetText));
        OnPropertyChanged(nameof(CollisionRadiusText));
    }

    private void DraftChanged()
    {
        OnPropertyChanged(nameof(CurrentColliderProposal));
        CollisionPreviewChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DraftEdited()
    {
        _hasCollisionDraft = true;
        DraftChanged();
    }

    private SecondaryCollider? CreateProposal()
    {
        if (!_hasCollisionDraft || StartBoneName is not { Length: > 0 } start ||
            CollisionShape == "Capsule" && EndBoneName is not { Length: > 0 } ||
            !TryVector(StartOffsetText, out Vector3D startOffset) ||
            !TryVector(EndOffsetText, out Vector3D endOffset) ||
            !double.TryParse(CollisionRadiusText, NumberStyles.Float, CultureInfo.InvariantCulture, out double radius) ||
            !double.IsFinite(radius) || radius <= 0)
            return null;
        return new()
        {
            BoneName = start,
            LocalPosition = startOffset,
            EndBoneName = CollisionShape == "Capsule" ? EndBoneName : null,
            EndLocalPosition = endOffset,
            Radius = radius,
        };
    }

    private static bool TryVector(string text, out Vector3D vector)
    {
        string[] values = text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 3 &&
            double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) &&
            double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
        {
            vector = new(x, y, z);
            return vector.IsFinite;
        }
        vector = default;
        return false;
    }

    private static string Format(Vector3D vector) => string.Join(" ", new[] { vector.X, vector.Y, vector.Z }
        .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
    private void Change(Func<SecondaryMotionGroup, SecondaryMotionGroup> edit)
    {
        if (Group is not { } group) return;
        SecondaryMotionGroup updated = edit(group);
        if (updated == group) return;
        SecondaryMotionDefinition next = definition with { Groups = definition.Groups.Replace(group, updated) };
        next.Validate();
        definition = next;
        OnPropertyChanged(nameof(Definition));
        NotifySettings();
        Status = "Preview setup edited; save a model copy to persist it. Native parameters remain unchanged.";
        PersistenceStatus = "This preview has pending setup changes. Save a model copy, open that copy in Models, then save the project.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void NotifySettings()
    {
        foreach (string name in new[] { nameof(GroupEnabled), nameof(Damping), nameof(Stiffness), nameof(BendStiffness), nameof(MotionInfluence), nameof(RestShapeStiffness), nameof(PreviewActorScale) })
            OnPropertyChanged(name);
    }

    private sealed record ColliderHistory(string GroupName, ImmutableArray<SecondaryCollider> Before, int? SelectedIndex);
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed class SecondaryMotionViewModel : ObservableObject
{
    private SecondaryMotionDefinition definition = new();
    private string? selectedGroup;
    private bool enabled = true;
    private bool showAnchors;
    private bool showCollisions;
    private string status = "Select a custom model or load an editor setup. MPC preview is an approximation.";
    private string persistenceStatus = "Save model copy creates a new package. Open that copy in Models and save the project to retain its settings.";
    public event EventHandler? Changed;
    public ObservableCollection<string> GroupNames { get; } = [];
    public SecondaryMotionDefinition Definition => definition;
    public IRelayCommand ResetCommand { get; set; } = null!;
    public IRelayCommand LoadSetupCommand { get; set; } = null!;
    public IRelayCommand SaveSetupCommand { get; set; } = null!;
    public IRelayCommand ImportNativeCommand { get; set; } = null!;
    public IRelayCommand ExportNativeCommand { get; set; } = null!;
    public IRelayCommand SaveModelCopyCommand { get; set; } = null!;
    public bool Enabled { get => enabled; set { if (SetProperty(ref enabled, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool ShowAnchors { get => showAnchors; set { if (SetProperty(ref showAnchors, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool ShowCollisions { get => showCollisions; set { if (SetProperty(ref showCollisions, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public string Status { get => status; set => SetProperty(ref status, value); }
    public string PersistenceStatus { get => persistenceStatus; set => SetProperty(ref persistenceStatus, value); }
    public string? SelectedGroup
    {
        get => selectedGroup;
        set { if (SetProperty(ref selectedGroup, value)) NotifySettings(); }
    }
    public bool GroupEnabled { get => Group?.Enabled ?? false; set => Change(g => g with { Enabled = value }); }
    public double Damping { get => Group?.Preview.Damping ?? 3; set => Tune(s => s with { Damping = Math.Clamp(value, 0, 1000) }); }
    public double Stiffness { get => Group?.Preview.StructuralStiffness ?? 0.95; set => Tune(s => s with { StructuralStiffness = Math.Clamp(value, 0, 1) }); }
    public double BendStiffness { get => Group?.Preview.BendStiffness ?? 0.3; set => Tune(s => s with { BendStiffness = Math.Clamp(value, 0, 1) }); }
    public double MotionInfluence { get => Group?.Preview.AnimationFollow ?? 0.65; set => Tune(s => s with { AnimationFollow = Math.Clamp(value, 0, 1) }); }
    public double RestShapeStiffness { get => Group?.Preview.RestShapeStiffness ?? 0; set => Tune(s => s with { RestShapeStiffness = Math.Clamp(value, 0, 1000) }); }
    private SecondaryMotionGroup? Group => definition.Groups.FirstOrDefault(g => g.Name == selectedGroup);

    public void Load(SecondaryMotionDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        definition = value;
        GroupNames.Clear();
        foreach (SecondaryMotionGroup group in value.Groups) GroupNames.Add(group.Name);
        selectedGroup = GroupNames.FirstOrDefault();
        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(SelectedGroup));
        NotifySettings();
        Status = $"{value.Groups.Length} preview groups; {value.NativeSources.Length} losslessly retained native scripts. MPC preview approximation.";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Tune(Func<SecondaryPreviewSettings, SecondaryPreviewSettings> edit) => Change(g => g with { Preview = edit(g.Preview) });
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
        foreach (string name in new[] { nameof(GroupEnabled), nameof(Damping), nameof(Stiffness), nameof(BendStiffness), nameof(MotionInfluence), nameof(RestShapeStiffness) })
            OnPropertyChanged(name);
    }
}

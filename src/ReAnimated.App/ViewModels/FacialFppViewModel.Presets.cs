using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class FacialFppViewModel
{
    private FacialPresetLibrary _facialLibrary = new();
    private string? _facialModelSourceIdentity;
    private string? _selectedExpression;
    private string _presetName = "Expression";
    private string _selectedGroup = "All";
    private string _expressionStatus = "Select a preset to preview it. Save or key explicitly to author changes.";
    private double _presetIntensity = 1;
    private bool _linkSides = true;
    private bool _updatingFacialPreview;
    private bool _speechActive;
    private bool _hasPreviewOverride;
    private ImmutableDictionary<string, double> _presetValues = ImmutableDictionary<string, double>.Empty;
    private IReadOnlyDictionary<string, double> _speechValues = ImmutableDictionary<string, double>.Empty;

    public event EventHandler? FacialLibraryChanged;
    public FacialPresetLibrary FacialLibrary => _facialLibrary;
    public ObservableCollection<string> ExpressionPresets { get; } = [];
    public IReadOnlyList<string> FacialGroups { get; } = ["All", "Eyes", "Brows", "Mouth", "Other"];
    public IRelayCommand PreviewExpressionCommand { get; private set; } = null!;
    public IRelayCommand SaveExpressionCommand { get; private set; } = null!;

    public string? SelectedExpression
    {
        get => _selectedExpression;
        set { if (SetProperty(ref _selectedExpression, value) && value is not null) PreviewExpression(); }
    }
    public string PresetName { get => _presetName; set => SetProperty(ref _presetName, value); }
    public double PresetIntensity
    {
        get => _presetIntensity;
        set { if (SetProperty(ref _presetIntensity, double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0)) PreviewExpression(); }
    }
    public bool LinkSides { get => _linkSides; set => SetProperty(ref _linkSides, value); }
    public string SelectedFacialGroup
    {
        get => _selectedGroup;
        set { if (SetProperty(ref _selectedGroup, value)) RebuildVisibleFacialItems(); }
    }
    public string ExpressionStatus { get => _expressionStatus; set => SetProperty(ref _expressionStatus, value); }

    private void InitializeFacialPresets()
    {
        PreviewExpressionCommand = new RelayCommand(PreviewExpression);
        SaveExpressionCommand = new RelayCommand(SaveExpression);
    }

    public void LoadFacialLibrary(FacialPresetLibrary? library)
    {
        library ??= new();
        library.Validate(Morphs.Select(x => x.Name));
        _facialLibrary = library;
        _selectedExpression = null;
        OnPropertyChanged(nameof(SelectedExpression));
        ExpressionPresets.Clear();
        foreach (FacialPresetDefinition preset in library.Presets) ExpressionPresets.Add(preset.Name);
        _presetValues = ImmutableDictionary<string, double>.Empty;
        _hasPreviewOverride = false;
        RebuildVisibleFacialItems();
        OnPropertyChanged(nameof(FacialLibrary));
    }

    public void LoadModelFacialLibrary(FacialPresetLibrary? library, string? sourceIdentity)
    {
        if (sourceIdentity is not null && string.Equals(sourceIdentity, _facialModelSourceIdentity, StringComparison.Ordinal))
        {
            // A clip switch can decode a fresh document for the same immutable
            // model. Keep working presets and reapply the selected face pose to
            // the freshly replaced control rows instead of discarding edits.
            _facialLibrary.Validate(Morphs.Select(x => x.Name));
            RebuildVisibleFacialItems();
            RefreshActiveExpressionPreview();
            return;
        }

        _facialModelSourceIdentity = sourceIdentity;
        LoadFacialLibrary(library);
    }

    public void PreviewWeights(IReadOnlyDictionary<string, double> weights, bool retainPresetSelection = false)
    {
        HashSet<string> targets = Morphs.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (weights.Any(x => !targets.Contains(x.Key) || !double.IsFinite(x.Value) || x.Value is < -4 or > 4))
            throw new InvalidOperationException("A preview weight is invalid or its facial target is missing.");
        _presetValues = weights.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        _hasPreviewOverride = true;
        if (!retainPresetSelection)
        {
            _selectedExpression = null;
            OnPropertyChanged(nameof(SelectedExpression));
        }
        ApplyPreviewValues();
    }

    public void SetSpeechPreview(IReadOnlyDictionary<string, double>? values)
    {
        if (values is not null && values.Any(x => !double.IsFinite(x.Value) || x.Value is < -4 or > 4 ||
            !Morphs.Any(morph => string.Equals(morph.Name, x.Key, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Speech preview contains a missing target or invalid value.");
        _speechActive = values is not null;
        _speechValues = values ?? ImmutableDictionary<string, double>.Empty;
        if (_selectedExpression is not null) PreviewExpression();
        else ApplyPreviewValues();
    }

    public void RefreshActiveExpressionPreview()
    {
        if (_selectedExpression is not null) PreviewExpression();
        else if (_hasPreviewOverride) ApplyPreviewValues();
    }

    public void ReleasePreviewOverrides()
    {
        _hasPreviewOverride = false;
        _selectedExpression = null;
        OnPropertyChanged(nameof(SelectedExpression));
    }

    public void SynchronizeEvaluatedWeights(IReadOnlyDictionary<string, double> weights)
    {
        if (!_hasPreviewOverride) _presetValues = weights.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
        _updatingFacialPreview = true;
        try
        {
            foreach (MorphChannelViewModel morph in Morphs)
                morph.Weight = weights.TryGetValue(morph.Name, out double value) ? checked((float)value) : 0;
        }
        finally { _updatingFacialPreview = false; }
    }

    public ImmutableDictionary<string, double> CapturePose() => Morphs
        .ToImmutableDictionary(x => x.Name, x => (double)x.Weight, StringComparer.OrdinalIgnoreCase);

    public void SaveExpression()
    {
        FacialPresetDefinition preset = new() { Name = PresetName.Trim(), Weights = CapturePose() };
        FacialPresetLibrary updated = _facialLibrary with
        {
            Presets = _facialLibrary.Presets.Where(x => !string.Equals(x.Name, preset.Name, StringComparison.OrdinalIgnoreCase))
                .Append(preset).ToImmutableArray(),
        };
        try { updated.Validate(Morphs.Select(x => x.Name)); }
        catch (ArgumentException exception) { ExpressionStatus = exception.Message; return; }
        LoadFacialLibrary(updated);
        SelectedExpression = preset.Name;
        FacialLibraryChanged?.Invoke(this, EventArgs.Empty);
        ExpressionStatus = $"Captured '{preset.Name}' in the working expression library. Save model copy to persist it.";
    }

    private void PreviewExpression()
    {
        FacialPresetDefinition? preset = _facialLibrary.Presets.FirstOrDefault(x => x.Name == SelectedExpression);
        if (preset is null) return;
        ImmutableDictionary<string, double> source = _speechActive && !preset.SpeechWeights.IsEmpty ? preset.SpeechWeights : preset.Weights;
        PreviewWeights(source.ToDictionary(x => x.Key, x => x.Value * PresetIntensity, StringComparer.OrdinalIgnoreCase), retainPresetSelection: true);
        ExpressionStatus = $"Preview: {preset.Name} ({PresetIntensity:P0}). Key pose to add it to the animation.";
    }

    private void ClearFacialPreview()
    {
        _selectedExpression = null;
        OnPropertyChanged(nameof(SelectedExpression));
        _presetValues = ImmutableDictionary<string, double>.Empty;
        _hasPreviewOverride = true;
        _speechValues = ImmutableDictionary<string, double>.Empty;
        _speechActive = false;
        ApplyPreviewValues();
    }

    private void ApplyPreviewValues()
    {
        _updatingFacialPreview = true;
        try
        {
            foreach (MorphChannelViewModel morph in Morphs)
            {
                _presetValues.TryGetValue(morph.Name, out double expression);
                _speechValues.TryGetValue(morph.Name, out double speech);
                morph.Weight = (float)(expression + speech);
            }
        }
        finally { _updatingFacialPreview = false; }
        MorphWeightsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool HandleLinkedFacialControl(object? sender)
    {
        if (_updatingFacialPreview) return true;
        if (sender is not MorphChannelViewModel changed) return false;
        _selectedExpression = null;
        OnPropertyChanged(nameof(SelectedExpression));
        if (!LinkSides) { CaptureEditedBase(); return false; }
        string? partner = _facialLibrary.Controls.FirstOrDefault(x => x.MorphName == changed.Name)?.PartnerName;
        if (partner is null) { CaptureEditedBase(); return false; }
        MorphChannelViewModel? other = Morphs.FirstOrDefault(x => x.Name == partner);
        if (other is null) { CaptureEditedBase(); return false; }
        _updatingFacialPreview = true;
        try { other.Weight = changed.Weight; }
        finally { _updatingFacialPreview = false; }
        CaptureEditedBase();
        return false;
    }

    private void CaptureEditedBase()
    {
        _hasPreviewOverride = true;
        _presetValues = Morphs.ToImmutableDictionary(x => x.Name,
            x => x.Weight - (_speechValues.TryGetValue(x.Name, out double speech) ? speech : 0), StringComparer.OrdinalIgnoreCase);
    }

    private bool MatchesFacialGroup(string name)
    {
        if (SelectedFacialGroup == "All") return true;
        FacialControlGroup? group = _facialLibrary.Controls.FirstOrDefault(x => x.MorphName == name)?.Group;
        // Classification affects browsing only; it never creates a binding or a linked control.
        group ??= name.Contains("brow", StringComparison.OrdinalIgnoreCase) ? FacialControlGroup.Brows :
            name.Contains("eye", StringComparison.OrdinalIgnoreCase) || name.Contains("pupil", StringComparison.OrdinalIgnoreCase) || name.Contains("blink", StringComparison.OrdinalIgnoreCase)
                ? FacialControlGroup.Eyes : name.Contains("mouth", StringComparison.OrdinalIgnoreCase) || name.Contains("lip", StringComparison.OrdinalIgnoreCase)
                ? FacialControlGroup.Mouth : FacialControlGroup.Other;
        return group.ToString() == SelectedFacialGroup;
    }
}

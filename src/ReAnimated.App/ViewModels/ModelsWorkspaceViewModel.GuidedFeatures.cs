using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public sealed class GuidedMorphPreviewChoice(string name, Action changed) : ObservableObject
{
    private double _weight;
    public string Name { get; } = name;
    public bool IsOverridden { get; private set; }
    public double Weight
    {
        get => _weight;
        set
        {
            if (!double.IsFinite(value)) return;
            value = Math.Clamp(value, -1, 1);
            if (!SetProperty(ref _weight, value)) return;
            IsOverridden = true;
            changed();
        }
    }
    internal void Reset()
    {
        IsOverridden = false;
        _weight = 0;
        OnPropertyChanged(nameof(Weight));
    }
}

public sealed partial class ModelsWorkspaceViewModel
{
    private string? _guidedMorphIdentity;
    private GuidedMorphPreviewChoice? _guidedSelectedMorph;
    private RelayCommand? _resetGuidedMorphsCommand;
    public ObservableCollection<GuidedMorphPreviewChoice> GuidedMorphs { get; } = [];
    public bool HasGuidedMorphs => GuidedMorphs.Count > 0;
    public GuidedMorphPreviewChoice? GuidedSelectedMorph
    {
        get => _guidedSelectedMorph;
        set => SetProperty(ref _guidedSelectedMorph, value);
    }
    public IRelayCommand ResetGuidedMorphsCommand => _resetGuidedMorphsCommand ??= new RelayCommand(() =>
    {
        foreach (GuidedMorphPreviewChoice choice in GuidedMorphs) choice.Reset();
        RefreshPreview();
    }, () => HasGuidedMorphs);
    public bool CanPlayGuidedEmbeddedAnimation => SelectedAnimation?.DecodedClip is { } clip &&
        MainWindowViewModel.AnimationContainsTemporalMovement([clip]);

    private void RefreshGuidedFeatures()
    {
        string? identity = _model is { } model
            ? $"{model.Package.Document.ModelId:N}/{model.Package.Document.MorphSignature}" : null;
        if (identity != _guidedMorphIdentity)
        {
            _guidedMorphIdentity = identity;
            GuidedMorphs.Clear();
            if (_model is { } current)
                foreach (var morph in current.Package.Document.MorphChannels)
                    GuidedMorphs.Add(new GuidedMorphPreviewChoice(morph.Name, OnGuidedMorphChanged));
            GuidedSelectedMorph = GuidedMorphs.FirstOrDefault();
            OnPropertyChanged(nameof(HasGuidedMorphs));
            _resetGuidedMorphsCommand?.NotifyCanExecuteChanged();
        }
        OnPropertyChanged(nameof(CanPlayGuidedEmbeddedAnimation));
    }

    private void OnGuidedMorphChanged()
    {
        Timeline.IsPlaying = false;
        RefreshPreview();
    }

    private ImmutableArray<MorphWeight> MergeGuidedMorphOverrides(
        FbxModelAuthoringImportResult model, ImmutableArray<MorphWeight> sampled)
    {
        if (_model?.Package.Document.ModelId != model.Package.Document.ModelId) return sampled;
        var weights = sampled.ToDictionary(static sample => sample.Name, StringComparer.Ordinal);
        foreach (GuidedMorphPreviewChoice choice in GuidedMorphs.Where(static choice => choice.IsOverridden))
            if (model.Package.Document.MorphChannels.Any(channel => channel.Name == choice.Name))
                weights[choice.Name] = new MorphWeight(choice.Name, (float)choice.Weight);
        return weights.Values.ToImmutableArray();
    }
}

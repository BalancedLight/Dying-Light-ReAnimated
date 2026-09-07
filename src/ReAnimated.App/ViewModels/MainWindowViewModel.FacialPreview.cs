using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fed;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private SpeechCurveExchange? _speechExchange;
    private MorphEditLayer? _speechPreviewLayer;
    private SpeechPreviewAudio? _speechAudio;
    private string? _selectedSpeechEntry;
    private bool _enableSpeechPreview = true;
    private string _speechPreviewStatus = "Load a DyingAudio speech exchange or configure its headless inspector.";
    public ObservableCollection<string> SpeechEntries { get; } = [];
    public IRelayCommand PreviewFedExpressionCommand { get; private set; } = null!;
    public IRelayCommand ExportFacialPresetsCommand { get; private set; } = null!;
    public IAsyncRelayCommand LoadSpeechExchangeCommand { get; private set; } = null!;
    public IRelayCommand LoadSpeechAudioCommand { get; private set; } = null!;
    public IRelayCommand ConfigureSpeechToolCommand { get; private set; } = null!;
    public IRelayCommand FollowFacialAnimationCommand { get; private set; } = null!;
    public string SpeechPreviewStatus { get => _speechPreviewStatus; private set => SetProperty(ref _speechPreviewStatus, value); }
    public string? SelectedSpeechEntry
    {
        get => _selectedSpeechEntry;
        set { if (SetProperty(ref _selectedSpeechEntry, value)) RebuildSpeechPreview(); }
    }
    public bool EnableSpeechPreview
    {
        get => _enableSpeechPreview;
        set
        {
            if (!SetProperty(ref _enableSpeechPreview, value)) return;
            if (!value) FacialFpp.SetSpeechPreview(null);
            if (value) RebuildSpeechPreview();
            else UpdateFacialSpeechPreview();
        }
    }

    private void InitializeFacialPreviewFeature()
    {
        PreviewFedExpressionCommand = new RelayCommand(PreviewFedExpression);
        ExportFacialPresetsCommand = new RelayCommand(ExportFacialPresets);
        LoadSpeechExchangeCommand = new AsyncRelayCommand(LoadSpeechExchangeDialogAsync);
        LoadSpeechAudioCommand = new RelayCommand(LoadSpeechAudio);
        ConfigureSpeechToolCommand = new RelayCommand(ConfigureSpeechTool);
        FollowFacialAnimationCommand = new RelayCommand(() => { FacialFpp.ReleasePreviewOverrides(); RefreshAnimationPreview(); });
        Timeline.PropertyChanged += OnSpeechTimelinePropertyChanged;
    }

    private void DisposeFacialPreviewFeature()
    {
        Timeline.PropertyChanged -= OnSpeechTimelinePropertyChanged;
        _speechAudio?.Dispose();
    }

    private void OnSpeechTimelinePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TimelineViewModel.IsPlaying)) UpdateFacialSpeechPreview();
        if (args.PropertyName == nameof(TimelineViewModel.FramesPerSecond)) RebuildSpeechPreview();
    }

    /// <summary>Invoke after the ordinary animation morph evaluation, never before it.</summary>
    private void UpdateFacialSpeechPreview()
    {
        double seconds = Timeline.CurrentFrame / Timeline.FramesPerSecond;
        if (_speechPreviewLayer is not null && EnableSpeechPreview)
        {
            FacialFpp.SetSpeechPreview(_speechPreviewLayer.Tracks.ToDictionary(x => x.MorphName,
                x => x.Sample(Timeline.CurrentFrame), StringComparer.OrdinalIgnoreCase));
        }
        else FacialFpp.RefreshActiveExpressionPreview();
        _speechAudio?.Update(seconds, Timeline.IsPlaying && EnableSpeechPreview &&
            _speechPreviewLayer is not null && FacialFpp.Morphs.Count > 0);
    }

    public void LoadFacialModelLibrary(FacialPresetLibrary? library)
    {
        CustomModelDocument? document = _customTargetPreviewSession?.Document;
        string? identity = document is null ? null :
            $"{_project.ProjectId:N}|{document.ModelId:N}|{document.Source.ContentSha256}|{_targetProjectAsset?.Id:N}|{_targetProjectAsset?.ContentSha256}|{_targetProjectAsset?.RelativePath}";
        FacialFpp.LoadModelFacialLibrary(library, identity);
        RebuildSpeechPreview();
    }

    public void LoadSpeechExchange(SpeechCurveExchange exchange)
    {
        SpeechCurveExchangeReader.Validate(exchange);
        _speechExchange = exchange;
        SpeechEntries.Clear();
        foreach (SpeechCurveEntry entry in exchange.Entries) SpeechEntries.Add(entry.Name);
        SelectedSpeechEntry = SpeechEntries.FirstOrDefault();
        RebuildSpeechPreview();
    }

    private void RebuildSpeechPreview()
    {
        _speechPreviewLayer = null;
        if (!EnableSpeechPreview || FacialFpp.Morphs.Count == 0)
        {
            FacialFpp.SetSpeechPreview(null);
            _speechAudio?.Update(0, false);
            SpeechPreviewStatus = !EnableSpeechPreview
                ? "Speech preview is disabled."
                : "This model has no facial targets; speech preview is inactive.";
            return;
        }
        if (_speechExchange is null || _targetRig is null || SelectedSpeechEntry is null) return;
        try
        {
            SpeechPreviewBuildResult built = SpeechCurveDomainAdapter.BuildPreview(_speechExchange, SelectedSpeechEntry,
                _targetRig, Timeline.FramesPerSecond);
            _speechPreviewLayer = built.Layer;
            SpeechPreviewStatus = $"{SelectedSpeechEntry}: {_speechPreviewLayer.Tracks.Length} channels. Source {_speechExchange.Source.FileName}. {built.Resolution.EvidenceBoundary}";
            foreach (SpeechResolutionDiagnostic diagnostic in built.Resolution.Diagnostics)
                AddDiagnostic(diagnostic.Severity.ToString(), "Speech preview", diagnostic.Message, diagnostic.Code);
            UpdateFacialSpeechPreview();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            FacialFpp.SetSpeechPreview(null);
            _speechAudio?.Update(0, false);
            SpeechPreviewStatus = exception.Message;
            AddDiagnostic("Error", "Speech preview", "Speech entry could not resolve against this model", exception.Message);
        }
    }

    private void PreviewFedExpression()
    {
        if (_fedDocument is null || FacialFpp.SelectedMimicPreset is null || _targetRig is null) return;
        try
        {
            FedExpression? selected = _fedDocument.FindExpression(FacialFpp.SelectedMimicPreset);
            if (selected is { Weights.Count: 0 })
            {
                FacialFpp.PreviewWeights(ImmutableDictionary<string, double>.Empty);
                StatusText = $"Previewing neutral FED {selected.Name}";
                return;
            }
            FedLayerBuildResult result = FedDomainAdapter.CreateLayer(_fedDocument, FacialFpp.SelectedMimicPreset, _targetRig,
                scope: MorphEditLayerScope.PreviewOnly, blendMode: MorphEditBlendMode.Override,
                compatibilityPolicy: FedLayerCompatibilityPolicy.RequireComplete);
            FacialFpp.PreviewWeights(result.Layer.Tracks.ToDictionary(x => x.MorphName,
                x => x.Sample(0) * FacialFpp.PresetIntensity, StringComparer.OrdinalIgnoreCase));
            StatusText = $"Previewing FED {FacialFpp.SelectedMimicPreset}; key pose to author it";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
        { AddDiagnostic("Error", "FED", "FED preview failed", exception.Message); }
    }

    private void ExportFacialPresets()
    {
        SaveFileDialog dialog = new() { Title = "Save model expression presets", Filter = "Facial expressions (*.fed)|*.fed", DefaultExt = ".fed" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            FacialFpp.FacialLibrary.Validate(FacialFpp.Morphs.Select(x => x.Name));
            List<FedExpression> expressions = [];
            foreach (FacialPresetDefinition preset in FacialFpp.FacialLibrary.Presets)
            {
                expressions.Add(new FedExpression(preset.Name, CompleteWeights(preset.Weights)));
                if (!preset.SpeechWeights.IsEmpty)
                    expressions.Add(new FedExpression($"{preset.Name} speech", CompleteWeights(preset.SpeechWeights)));
            }
            FedWriter.Write(dialog.FileName, new FedDocument("Expressions", expressions, []), new FedLimits { RejectDuplicateNames = true });
            StatusText = $"Saved {expressions.Count} reader-validated FED expressions";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        { AddDiagnostic("Error", "FED", "FED save failed", exception.Message); }

        FedMorphWeight[] CompleteWeights(IReadOnlyDictionary<string, double> values) => FacialFpp.Morphs.Select(morph =>
            new FedMorphWeight(morph.Name, values.TryGetValue(morph.Name, out double value) ? (float)value : 0)).ToArray();
    }

    private async Task LoadSpeechExchangeDialogAsync()
    {
        OpenFileDialog dialog = new() { Title = "Load speech curves", Filter = "Speech curves (*.json;*.spb)|*.json;*.spb" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            SpeechCurveExchange exchange = Path.GetExtension(dialog.FileName).Equals(".spb", StringComparison.OrdinalIgnoreCase)
                ? await SpeechInspectionTool.InspectAsync(dialog.FileName, SpeechInspectionToolSettings.Load()
                    ?? throw new InvalidOperationException("Configure the DyingAudio Python executable first, or load its JSON exchange."))
                : SpeechCurveExchangeReader.Read(dialog.FileName);
            LoadSpeechExchange(exchange);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException)
        { SpeechPreviewStatus = exception.Message; AddDiagnostic("Error", "Speech preview", "Speech load failed", exception.Message); }
    }

    private void LoadSpeechAudio()
    {
        OpenFileDialog dialog = new() { Title = "Load matched dialogue audio", Filter = "Audio (*.wav;*.mp3;*.wma)|*.wav;*.mp3;*.wma" };
        if (dialog.ShowDialog() != true) return;
        try { LoadMatchedSpeechAudio(dialog.FileName); }
        catch (Exception exception) when (exception is IOException or ArgumentException)
        { SpeechPreviewStatus = exception.Message; }
    }

    public void LoadMatchedSpeechAudio(string path)
    {
        _speechAudio ??= CreateAudio();
        _speechAudio.Open(path);
        UpdateFacialSpeechPreview();
        SpeechPreviewAudio CreateAudio()
        {
            SpeechPreviewAudio player = new();
            player.Failed += (_, message) => SpeechPreviewStatus = $"Audio could not play: {message}";
            return player;
        }
    }

    private void ConfigureSpeechTool()
    {
        OpenFileDialog dialog = new() { Title = "Choose Python with the DyingAudio package installed", Filter = "Python executable (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;
        SpeechInspectionToolSettings settings = new() { PythonExecutable = dialog.FileName };
        settings.Save();
        SpeechPreviewStatus = "DyingAudio inspector configured. The dyingaudio package must be installed in this Python environment.";
    }
}

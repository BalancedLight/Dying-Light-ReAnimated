using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record ActorModelDeclarationChoice(int CallIndex, string ModelName, string Label);
public sealed record ActorSourceScopeChoice(int CallIndex, string Label);
public sealed class ActorSourceReferenceRow(CharacterActorQuotedReference reference) : ObservableObject
{
    private bool _isReviewed;
    public CharacterActorQuotedReference Reference { get; } = reference;
    public string Setting => Reference.CallName;
    public string Resource => Reference.Value;
    public string Scope => CanReview ? "Selected model source" : "Unverified lead";
    public bool CanReview => Reference.Scope == CharacterActorReferenceScope.Scoped;
    public bool IsReviewed { get => _isReviewed; set => SetProperty(ref _isReviewed, CanReview && value); }
}

public sealed partial class ModelsWorkspaceViewModel
{
    private CharacterResourceRecord? _selectedActorSource;
    private ActorModelDeclarationChoice? _selectedActorDeclaration;
    private ActorSourceScopeChoice? _selectedActorScope;
    private CharacterActorSourceReviewResult? _actorSourceReview;
    private bool _actorSourceReviewed;
    private string _actorSourceReviewDetail = string.Empty;
    private RelayCommand? _recordActorSourceReviewCommand;
    public ObservableCollection<CharacterResourceRecord> ActorSourceChoices { get; } = [];
    public ObservableCollection<ActorModelDeclarationChoice> ActorDeclarationChoices { get; } = [];
    public ObservableCollection<ActorSourceScopeChoice> ActorScopeChoices { get; } = [];
    public ObservableCollection<ActorSourceReferenceRow> ActorSourceReferences { get; } = [];
    public ObservableCollection<CharacterActorSourceReceipt> ActorSourceReceipts { get; } = [];
    public CharacterResourceRecord? SelectedActorSource { get => _selectedActorSource; set { if (SetProperty(ref _selectedActorSource, value)) LoadActorDeclarations(); } }
    public ActorModelDeclarationChoice? SelectedActorDeclaration { get => _selectedActorDeclaration; set { if (SetProperty(ref _selectedActorDeclaration, value)) LoadActorScopes(); } }
    public ActorSourceScopeChoice? SelectedActorScope { get => _selectedActorScope; set { if (SetProperty(ref _selectedActorScope, value)) LoadActorSourceReview(); } }
    public bool ActorSourceReviewed { get => _actorSourceReviewed; set => SetProperty(ref _actorSourceReviewed, value); }
    public string ActorSourceReviewDetail { get => _actorSourceReviewDetail; private set => SetProperty(ref _actorSourceReviewDetail, value); }
    public RelayCommand RecordActorSourceReviewCommand => _recordActorSourceReviewCommand ??= new(RecordActorSourceReview);

    private void RefreshCharacterActorSources()
    {
        string? priorId = SelectedActorSource?.Id;
        ActorSourceChoices.Clear(); ActorSourceReceipts.Clear();
        if (_model?.Package.Document.CharacterResources is { } inventory)
        {
            foreach (var resource in inventory.Resources.Where(r => r.EntryPath is not null && !r.IsOriginalArchive &&
                Path.GetExtension(r.LogicalName).ToLowerInvariant() is ".scr" or ".def" or ".chr" or ".pre")) ActorSourceChoices.Add(resource);
            foreach (var receipt in inventory.ActorSourceReviews) ActorSourceReceipts.Add(receipt);
        }
        SelectedActorSource = ActorSourceChoices.FirstOrDefault(r => r.Id == priorId) ?? ActorSourceChoices.FirstOrDefault();
        if (SelectedActorSource is null) LoadActorDeclarations();
    }
    private NativeCharacterScriptDocument ReadActorSource()
    {
        if (_model is null || SelectedActorSource?.EntryPath is not { } path ||
            !_model.Package.CompanionPayloads.TryGetValue(path, out var bytes))
            throw new InvalidDataException("Select a retained actor source first.");
        return NativeCharacterScriptCodec.Parse(new UTF8Encoding(false, true).GetString(bytes.AsSpan()));
    }
    private void LoadActorDeclarations()
    {
        ActorSourceReviewed = false; _actorSourceReview = null;
        SelectedActorDeclaration = null;
        ActorDeclarationChoices.Clear(); ActorScopeChoices.Clear(); ActorSourceReferences.Clear();
        try
        {
            if (SelectedActorSource is null) { ActorSourceReviewDetail = "Resolve and retain the exact actor source before reviewing its model association."; SelectedActorDeclaration = null; return; }
            var source = ReadActorSource();
            var inventory = _model?.Package.Document.CharacterResources
                ?? throw new InvalidDataException("The model source inventory is missing.");
            var root = inventory.Resources.SingleOrDefault(resource => resource.Id == inventory.RootResourceId)
                ?? throw new InvalidDataException("The exact model source root is missing.");
            bool presetDeclarations = false;
            for (int i = 0; i < source.Calls.Length; i++)
            {
                var call = source.Calls[i];
                if (!CharacterActorSourceReview.IsSupportedModelDeclaration(source.Calls, i)) continue;
                var name = CharacterActorSourceReview.GetModelNameArgument(call)?.Value;
                if (name is null || !CharacterActorSourceAuthoring.ModelNameMatchesRoot(name, root.LogicalName)) continue;
                presetDeclarations |= call.Name == "SetField";
                string scope = call.ParentCallIndex < 0 ? "source root" : ScopeLabel(source.Calls[call.ParentCallIndex]);
                ActorDeclarationChoices.Add(new(i, name, name + " (" + scope + ")"));
            }
            SelectedActorDeclaration = presetDeclarations && ActorDeclarationChoices.Count > 1
                ? null : ActorDeclarationChoices.FirstOrDefault();
            if (SelectedActorDeclaration is null) ActorSourceReviewDetail = ActorDeclarationChoices.Count > 1
                ? "Select the exact preset model declaration."
                : "No supported model declaration found. Choose another actor source.";
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or DecoderFallbackException or FormatException)
        { ActorSourceReviewDetail = "Actor source cannot be reviewed: " + e.Message; SelectedActorDeclaration = null; }
    }
    private void LoadActorScopes()
    {
        ActorSourceReviewed = false; _actorSourceReview = null; SelectedActorScope = null; ActorScopeChoices.Clear(); ActorSourceReferences.Clear();
        if (SelectedActorDeclaration is null) { SelectedActorScope = null; return; }
        try
        {
            var source = ReadActorSource();
            var declaration = source.Calls[SelectedActorDeclaration.CallIndex];
            if (declaration.Name == "SetField")
            {
                int preset = declaration.ParentCallIndex;
                ActorScopeChoices.Add(new(preset, ScopeLabel(source.Calls[preset])));
                SelectedActorScope = ActorScopeChoices[0];
            }
            else
            {
                ActorScopeChoices.Add(new(-1, "Selected model block only"));
                int parent = declaration.ParentCallIndex;
                while (parent >= 0) { ActorScopeChoices.Add(new(parent, ScopeLabel(source.Calls[parent]))); parent = source.Calls[parent].ParentCallIndex; }
                SelectedActorScope = ActorScopeChoices.Count > 1 ? ActorScopeChoices[1] : ActorScopeChoices[0];
            }
        }
        catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or DecoderFallbackException or FormatException)
        { ActorSourceReviewDetail = e.Message; SelectedActorScope = null; }
    }
    private void LoadActorSourceReview()
    {
        ActorSourceReviewed = false; _actorSourceReview = null; ActorSourceReferences.Clear();
        if (_model is null || SelectedActorSource is null || SelectedActorDeclaration is null || SelectedActorScope is null) return;
        try
        {
            var review = CharacterActorSourceAuthoring.Inspect(_model.Package, SelectedActorSource.Id,
                SelectedActorDeclaration.CallIndex, SelectedActorScope.CallIndex);
            _actorSourceReview = review;
            foreach (var reference in review.References) ActorSourceReferences.Add(new(reference));
            ActorSourceReviewDetail = $"{review.ScopedReferences.Length} references in scope; {review.UnverifiedLeads.Length} outside scope. Review the settings for this character.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        { ActorSourceReviewDetail = "Source association refused: " + e.Message; }
    }
    private void RecordActorSourceReview()
    {
        if (_model is null || SelectedActorSource is null || _actorSourceReview is null || !ActorSourceReviewed)
        { BuildStatus = "Select the actor source and model declaration, then review the source association before recording it."; return; }
        try
        {
            var before = CaptureAuthoringSnapshot();
            var selected = ActorSourceReferences.Where(r => r.IsReviewed && r.CanReview).Select(r =>
                new CharacterActorReferenceExpectation(r.Reference.CallIndex, r.Reference.ArgumentIndex,
                    r.Reference.ArgumentStart, r.Reference.ArgumentLength, r.Reference.Value));
            var package = CharacterActorSourceAuthoring.Apply(_model.Package, SelectedActorSource.Id, _actorSourceReview, selected);
            CommitModel(_model with { Package = package }, _sourcePath, _packagePath, preserveAuthoringHistory: true);
            RecordAuthoringUndo(before);
            BuildStatus = "Actor source review saved.";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        { BuildStatus = "Actor source review refused: " + e.Message; }
    }
    private static string ScopeLabel(NativeCharacterCall call) => call.Name +
        (call.QuotedArguments.FirstOrDefault(a => a.ArgumentIndex == 0)?.Value is { } name ? " (" + name + ")" : string.Empty);
}

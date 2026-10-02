using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    public ObservableCollection<FppSurfaceVisibilityRow> FppSurfaceVisibilityRows { get; } = [];

    private void InitializeModelPerspectives()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(HasModel) or nameof(PersistenceRevision) or nameof(ModelName))
            {
                RefreshPerspectiveRows();
                OnPropertyChanged(nameof(FppTppSummary));
            }
            if (args.PropertyName is nameof(IsBusy) or nameof(HasModel))
            {
                _prepareFppTppCommand?.NotifyCanExecuteChanged();
                _buildFppTppPackagesCommand?.NotifyCanExecuteChanged();
            }
        };
    }

    private void RefreshPerspectiveRows()
    {
        FppSurfaceVisibilityRows.Clear();
        if (_model is not { } model) return;
        foreach (FbxModelSurface surface in model.Surfaces)
        {
            var key = new CustomModelPerspectiveSurfaceKey(surface.SourceGeometry?.Id ?? surface.Id, surface.Id);
            string mode = model.Package.Document.FirstPersonVisibility?.HiddenSurfaces.Contains(key) == true
                ? FppSurfaceVisibilityRow.Hide
                : model.Package.Document.FirstPersonVisibility?.KeptSurfaces.Contains(key) == true
                    ? FppSurfaceVisibilityRow.Keep : FppSurfaceVisibilityRow.Automatic;
            FppSurfaceVisibilityRows.Add(new(surface.MeshName, mode, value => SetPerspectiveSurface(surface.Id, value)));
        }
    }

    private void SetPerspectiveSurface(string id, string mode)
    {
        if (_model is not { } model || IsBusy) return;
        var surface = model.Surfaces.Single(row => row.Id == id);
        var component = surface.SourceGeometry?.Id ?? surface.Id;
        var key = new CustomModelPerspectiveSurfaceKey(component, id);
        var auto = FbxModelPerspectiveAuthoring.ProposeFirstPerson(model).Selection;
        var current = model.Package.Document.FirstPersonVisibility ?? auto;
        var triangleKeys = surface.SourceTriangles.Select(triangle =>
            new CustomModelPerspectiveTriangleKey(component, triangle)).ToHashSet();
        var hiddenTriangles = current.HiddenTriangles.Where(triangle => !triangleKeys.Contains(triangle));
        if (mode == FppSurfaceVisibilityRow.Automatic)
            hiddenTriangles = hiddenTriangles.Concat(auto.HiddenTriangles.Where(triangle => triangleKeys.Contains(triangle)));
        var surfaces = current.HiddenSurfaces.Where(candidate => candidate != key);
        if (mode == FppSurfaceVisibilityRow.Hide) surfaces = surfaces.Append(key);
        var keptSurfaces = current.KeptSurfaces.Where(candidate => candidate != key);
        if (mode == FppSurfaceVisibilityRow.Keep) keptSurfaces = keptSurfaces.Append(key);
        var choice = current with { HiddenSurfaces = surfaces.ToImmutableArray(), KeptSurfaces = keptSurfaces.ToImmutableArray(),
            HiddenTriangles = hiddenTriangles.Distinct().ToImmutableArray() };
        var before = CaptureAuthoringSnapshot();
        var document = model.Package.Document with { FirstPersonVisibility = choice, LastBuildReceipt = null };
        document.Validate();
        CommitModel(model with { Package = model.Package with { Document = document } }, _sourcePath, _packagePath,
            preserveAuthoringHistory: true);
        RecordAuthoringUndo(before);
        RefreshPerspectiveRows();
        OnPropertyChanged(nameof(FppTppSummary));
    }

    private RelayCommand? _prepareFppTppCommand;
    private AsyncRelayCommand? _buildFppTppPackagesCommand;

    public RelayCommand PrepareFppTppCommand => _prepareFppTppCommand ??=
        new RelayCommand(PrepareFppTpp, () => HasModel && !IsBusy);
    public IAsyncRelayCommand BuildFppTppPackagesCommand => _buildFppTppPackagesCommand ??=
        new AsyncRelayCommand(BuildFppTppPackagesAsync, () => HasModel && !IsBusy && !GuidedChannelPolicyReviewRequired);
    public string FppTppSummary => _model?.Package.Document.FirstPersonVisibility is { } choice
        ? $"FPP hides {choice.HiddenTriangles.Length:N0} head triangle(s) and {choice.HiddenSurfaces.Length:N0} selected surface(s). TPP retains the complete character."
        : "Generate a head-hidden FPP view and a complete TPP view. Mixed neck and body faces stay visible for review.";

    private void PrepareFppTpp()
    {
        if (_model is not { } model || IsBusy) return;
        try
        {
            SyncDocument();
            model = _model!;
            var proposal = FbxModelPerspectiveAuthoring.ProposeFirstPerson(model);
            var document = model.Package.Document with { FirstPersonVisibility = proposal.Selection, LastBuildReceipt = null };
            document.Validate();
            var before = CaptureAuthoringSnapshot();
            CommitModel(model with { Package = model.Package with { Document = document } },
                _sourcePath, _packagePath, preserveAuthoringHistory: true);
            RecordAuthoringUndo(before);
            RefreshPerspectiveRows();
            OnPropertyChanged(nameof(FppTppSummary));
            BuildStatus = FppTppSummary + " The choices are saved with the editable model and can be undone. Preview both views before export.";
            _setStatus(BuildStatus);
        }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            BuildStatus = "FPP/TPP preparation could not finish: " + error.Message;
            _setStatus(BuildStatus);
        }
    }

    private async Task BuildFppTppPackagesAsync()
    {
        if (_model is null) return;
        if (_model.Package.Document.FirstPersonVisibility is null) PrepareFppTpp();
        if (_model?.Package.Document.FirstPersonVisibility is not { } selection) return;
        if (!File.Exists(CompilerExecutablePath)) SelectModelCompiler();
        if (!File.Exists(CompilerExecutablePath)) return;
        if (!TryShowModelPicker("Choose a parent folder for the FPP and TPP packages",
                () => _fileDialogs.ShowSelectCustomModelOutputDirectory(_packagePath ?? _sourcePath), out string? parent) ||
            string.IsNullOrWhiteSpace(parent)) return;
        long generation = BeginOperation(CancellationToken.None, out CancellationToken token);
        try
        {
            SyncDocument();
            var complete = _model!;
            var firstPersonView = FbxModelPerspectiveAuthoring.CreateFirstPersonExportView(complete, selection);
            var firstPerson = firstPersonView.Model;
            if (firstPerson.Surfaces.IsEmpty) throw new InvalidOperationException("The FPP selection hides every surface. Keep the arms or body visible before building.");
            string basename = Dl1SourceModelWriter.SanitizeName(ResourceName, 51);
            var selections = GetGuidedPackageAnimationSelections(complete);
            var outputs = new List<string>();
            foreach (var variant in new[] { (Suffix: "_tpp", Model: complete), (Suffix: "_fpp", Model: firstPerson) })
            {
                string resource = basename + variant.Suffix;
                BuildStatus = $"Building {variant.Suffix[1..].ToUpperInvariant()} and checking compiled geometry…";
                var result = await Dl1CustomModelPackageBuilder.BuildAsync(new()
                {
                    Model = variant.Model, ParentOutputDirectory = parent,
                    CompilerExecutablePath = CompilerExecutablePath, RetailData0PakPath = _getRetailData0PakPath(),
                    ResourceName = resource, SurfaceName = SurfaceName, AnimationSelections = selections,
                    Perspective = variant.Suffix == "_fpp" ? CustomModelPerspective.FirstPerson : CustomModelPerspective.ThirdPerson,
                    RetainedMorphNames = variant.Model.Package.Document.MorphChannels.Select(channel => channel.Name).ToImmutableArray(),
                    DroppedMorphNames = variant.Suffix == "_fpp" ? firstPersonView.DroppedMorphNames : [],
                    AnimationScriptAlias = ResolveGuidedPackageAnimationAlias(selections, AnimationScriptAlias, ReferenceExistingAnimationLibrary, resource),
                }, token);
                EnsureCurrentGeneration(generation);
                outputs.Add(result.PackageDirectory);
            }
            BuildStatus = "FPP and TPP packages built and checked: " + string.Join("; ", outputs);
            _setStatus(BuildStatus);
        }
        catch (OperationCanceledException) { BuildStatus = "FPP/TPP package build cancelled; completed packages remain available."; }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException or NotSupportedException)
        {
            BuildStatus = "FPP/TPP package build could not finish: " + error.Message;
            _setStatus(BuildStatus);
        }
        finally { EndOperation(generation); }
    }
}

public sealed class FppSurfaceVisibilityRow : ObservableObject
{
    public const string Automatic = "Automatic head mask";
    public const string Keep = "Keep whole surface";
    public const string Hide = "Hide whole surface";
    private string _mode;
    private readonly Action<string> _changed;
    public FppSurfaceVisibilityRow(string name, string mode, Action<string> changed)
    { Name = name; _mode = mode; _changed = changed; }
    public string Name { get; }
    public IReadOnlyList<string> Modes { get; } = [Automatic, Keep, Hide];
    public string Mode
    {
        get => _mode;
        set { if (Modes.Contains(value) && SetProperty(ref _mode, value)) _changed(value); }
    }
}

using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Core.Project;

namespace ReAnimated.App.Infrastructure;

public sealed record AnimationLibraryModeOption(
    ProjectAnimationLibraryMode Value,
    string Label,
    string Description);

public sealed record AnimationLibraryCollisionOption(
    ProjectAnimationSequenceCollisionPolicy Value,
    string Label,
    string Description);

public sealed class AnimationLibraryImportRowViewModel
{
    public AnimationLibraryImportRowViewModel(
        ProjectAnimationLibraryImport import,
        string resourceName,
        string displayName,
        string evidence)
    {
        Import = import ?? throw new ArgumentNullException(nameof(import));
        ResourceName = resourceName;
        DisplayName = displayName;
        Evidence = evidence;
    }

    public ProjectAnimationLibraryImport Import { get; }

    public string Kind => Import.Kind ==
        ProjectAnimationLibraryImportKind.ProjectLibrary
        ? "Project library"
        : "Retail script";

    public string ResourceName { get; }

    public string DisplayName { get; }

    public string Evidence { get; }
}

public sealed class AnimationLibraryEditRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<AnimationLibraryRetailScriptOption>
        _retailScripts;
    private string _resourceName;
    private string _displayName;
    private AnimationLibraryModeOption _selectedMode;
    private AnimationLibraryCollisionOption _selectedCollision;
    private AnimationLibraryRetailScriptOption? _selectedExistingScript;

    public AnimationLibraryEditRowViewModel(
        ProjectAnimationLibrary library,
        IReadOnlyDictionary<Guid, ProjectAnimationLibrary> allLibraries,
        IReadOnlyList<AnimationLibraryRetailScriptOption> retailScripts,
        IReadOnlyList<AnimationLibraryModeOption> modeOptions,
        IReadOnlyList<AnimationLibraryCollisionOption> collisionOptions)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(allLibraries);
        ArgumentNullException.ThrowIfNull(retailScripts);
        ArgumentNullException.ThrowIfNull(modeOptions);
        ArgumentNullException.ThrowIfNull(collisionOptions);
        Id = library.Id;
        _resourceName = library.ResourceName;
        _displayName = library.DisplayName;
        _retailScripts = retailScripts;
        _selectedMode = modeOptions.Single(option =>
            option.Value == library.Mode);
        _selectedCollision = collisionOptions.Single(option =>
            option.Value == library.CollisionPolicy);
        _selectedExistingScript = library.ExistingScriptIdentity is null
            ? null
            : retailScripts.FirstOrDefault(option =>
                RetailIdentityEquals(
                    option.Identity,
                    library.ExistingScriptIdentity));
        Imports = new ObservableCollection<AnimationLibraryImportRowViewModel>(
            library.Imports.Select(import =>
                CreateImportRow(import, allLibraries)));
    }

    public Guid Id { get; }

    public IReadOnlyList<AnimationLibraryModeOption> ModeOptions { get; init; } = [];

    public IReadOnlyList<AnimationLibraryCollisionOption> CollisionOptions
    { get; init; } = [];

    public IReadOnlyList<AnimationLibraryRetailScriptOption>
        ExistingScriptOptions => _retailScripts;

    public ObservableCollection<AnimationLibraryImportRowViewModel> Imports
    { get; }

    public string ResourceName
    {
        get => _resourceName;
        set
        {
            if (SetProperty(ref _resourceName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(DlcConventionMessage));
            }
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Summary => string.IsNullOrWhiteSpace(DisplayName)
        ? string.IsNullOrWhiteSpace(ResourceName)
            ? "Unnamed animation library"
            : ResourceName
        : DisplayName;

    public string DlcConventionMessage
    {
        get
        {
            if (ProjectAnimationLibrary.TryGetDlcNumber(
                    ResourceName.Trim(),
                    out int dlcNumber))
            {
                return dlcNumber < 60
                    ? $"Valid DLC append name, but dlc{dlcNumber} is below the conventional 60+ range."
                    : $"DLC append convention detected: dlc{dlcNumber}.";
            }

            return ResourceName.Contains(
                "_dlc",
                StringComparison.OrdinalIgnoreCase)
                ? "Invalid DLC suffix. Use <base>_dlc<NN>, for example anims_man_all_dlc60."
                : "For a base-game character append script, use <base>_dlc<NN>; 60+ is conventional.";
        }
    }

    public AnimationLibraryModeOption SelectedMode
    {
        get => _selectedMode;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!SetProperty(ref _selectedMode, value))
            {
                return;
            }

            if (value.Value == ProjectAnimationLibraryMode.CustomAdditive)
            {
                SelectedExistingScript = null;
            }
            else if (SelectedExistingScript is null)
            {
                SelectedExistingScript = ExistingScriptOptions.Count == 0
                    ? null
                    : ExistingScriptOptions[0];
            }

            OnPropertyChanged(nameof(IsExistingExtension));
            OnPropertyChanged(nameof(ModeDescription));
        }
    }

    public string ModeDescription => SelectedMode.Description;

    public bool IsExistingExtension => SelectedMode.Value ==
        ProjectAnimationLibraryMode.ExistingScriptExtension;

    public AnimationLibraryCollisionOption SelectedCollision
    {
        get => _selectedCollision;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (SetProperty(ref _selectedCollision, value))
            {
                OnPropertyChanged(nameof(CollisionDescription));
            }
        }
    }

    public string CollisionDescription => SelectedCollision.Description;

    public AnimationLibraryRetailScriptOption? SelectedExistingScript
    {
        get => _selectedExistingScript;
        set
        {
            if (!SetProperty(ref _selectedExistingScript, value))
            {
                return;
            }

            if (IsExistingExtension && value is not null)
            {
                ResourceName = value.Identity.ResourceName;
            }
        }
    }

    public ProjectAnimationLibrary ToProjectLibrary() => new()
    {
        Id = Id,
        ResourceName = ResourceName.Trim(),
        DisplayName = DisplayName.Trim(),
        Mode = SelectedMode.Value,
        ExistingScriptIdentity = IsExistingExtension
            ? SelectedExistingScript?.Identity
            : null,
        Imports = Imports
            .Select(static row => row.Import)
            .ToImmutableArray(),
        CollisionPolicy = SelectedCollision.Value,
    };

    public void AddImport(AnimationLibraryImportRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Imports.Add(row);
    }

    private static AnimationLibraryImportRowViewModel CreateImportRow(
        ProjectAnimationLibraryImport import,
        IReadOnlyDictionary<Guid, ProjectAnimationLibrary> libraries)
    {
        if (import.Kind ==
            ProjectAnimationLibraryImportKind.ProjectLibrary)
        {
            ProjectAnimationLibrary? library =
                import.ProjectLibraryId is { } id &&
                libraries.TryGetValue(id, out ProjectAnimationLibrary? found)
                    ? found
                    : null;
            return new AnimationLibraryImportRowViewModel(
                import,
                library?.ResourceName ?? "Missing project library",
                library?.DisplayName ?? "Missing project library",
                library is null ? "Missing" : "Project-owned");
        }

        ProjectRetailAssetIdentity? retail = import.RetailScriptIdentity;
        return new AnimationLibraryImportRowViewModel(
            import,
            retail?.ResourceName ?? "Missing retail script",
            retail?.ResourceName ?? "Missing retail script",
            retail is null
                ? "Missing"
                : $"{retail.ProviderId} | {retail.ContentSha256[..12]}");
    }

    private static bool RetailIdentityEquals(
        ProjectRetailAssetIdentity left,
        ProjectRetailAssetIdentity right) =>
        left.ResourceType == right.ResourceType &&
        left.ResourceIndex == right.ResourceIndex &&
        left.Precedence == right.Precedence &&
        string.Equals(
            left.InstallFingerprint,
            right.InstallFingerprint,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.ProviderId,
            right.ProviderId,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.ProviderPack,
            right.ProviderPack,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.ResourceName,
            right.ResourceName,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.ContentSha256,
            right.ContentSha256,
            StringComparison.OrdinalIgnoreCase);
}

public sealed class AnimationLibraryEditorViewModel : ObservableObject
{
    private AnimationLibraryEditRowViewModel? _selectedLibrary;
    private AnimationLibraryEditRowViewModel? _selectedProjectImport;
    private AnimationLibraryRetailScriptOption? _selectedRetailImport;
    private AnimationLibraryImportRowViewModel? _selectedImport;
    private string _outputAnm2Name;
    private string _validationMessage = string.Empty;
    private string _generatedSourcePreview = string.Empty;

    public AnimationLibraryEditorViewModel(
        AnimationLibraryEditorRequest request)
    {
        AnimationLibraryAssignmentValidator.ValidateRequest(request);
        Request = request;
        ModeOptions =
        [
            new AnimationLibraryModeOption(
                ProjectAnimationLibraryMode.CustomAdditive,
                "Custom / additive",
                "Creates a project-owned type-322 script. Imported scripts remain separate dependencies."),
            new AnimationLibraryModeOption(
                ProjectAnimationLibraryMode.ExistingScriptExtension,
                "Extend existing script",
                "Preserves the exact fingerprinted retail script, then applies explicit sequence additions or replacements."),
        ];
        CollisionOptions =
        [
            new AnimationLibraryCollisionOption(
                ProjectAnimationSequenceCollisionPolicy.Reject,
                "Add — reject collisions",
                "Adds new sequence names and stops if an imported or preserved script already owns one."),
            new AnimationLibraryCollisionOption(
                ProjectAnimationSequenceCollisionPolicy.ReplaceExisting,
                "Replace existing explicitly",
                "Allows selected project sequences to replace same-name imported or preserved base sequences."),
        ];

        Dictionary<Guid, ProjectAnimationLibrary> librariesById =
            request.Libraries.ToDictionary(static library => library.Id);
        Libraries = new ObservableCollection<AnimationLibraryEditRowViewModel>(
            request.Libraries.Select(library =>
                CreateRow(library, librariesById)));
        foreach (AnimationLibraryEditRowViewModel row in Libraries)
        {
            Subscribe(row);
        }

        _outputAnm2Name = request.Assignment.OutputAnm2Name;
        SelectedLibrary = request.SelectedLibraryId is { } selectedId
            ? Libraries.FirstOrDefault(row => row.Id == selectedId)
            : Libraries.Count == 0
                ? null
                : Libraries[0];
        SelectedProjectImport = Libraries.FirstOrDefault(row =>
            row.Id != SelectedLibrary?.Id);
        SelectedRetailImport = RetailScripts.Count == 0
            ? null
            : RetailScripts[0];
        RefreshPreview();
    }

    public AnimationLibraryEditorRequest Request { get; }

    public IReadOnlyList<AnimationLibraryModeOption> ModeOptions { get; }

    public IReadOnlyList<AnimationLibraryCollisionOption> CollisionOptions
    { get; }

    public IReadOnlyList<AnimationLibraryRetailScriptOption> RetailScripts =>
        Request.RetailScripts;

    public ObservableCollection<AnimationLibraryEditRowViewModel> Libraries
    { get; }

    public AnimationLibraryEditRowViewModel? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (!SetProperty(ref _selectedLibrary, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasSelectedLibrary));
            if (SelectedProjectImport?.Id == value?.Id)
            {
                SelectedProjectImport = Libraries.FirstOrDefault(row =>
                    row.Id != value?.Id);
            }

            RefreshPreview();
        }
    }

    public bool HasSelectedLibrary => SelectedLibrary is not null;

    public AnimationLibraryEditRowViewModel? SelectedProjectImport
    {
        get => _selectedProjectImport;
        set => SetProperty(ref _selectedProjectImport, value);
    }

    public AnimationLibraryRetailScriptOption? SelectedRetailImport
    {
        get => _selectedRetailImport;
        set => SetProperty(ref _selectedRetailImport, value);
    }

    public AnimationLibraryImportRowViewModel? SelectedImport
    {
        get => _selectedImport;
        set => SetProperty(ref _selectedImport, value);
    }

    public string SequenceName => Request.Assignment.SequenceName;

    public string OutputAnm2Name
    {
        get => _outputAnm2Name;
        set
        {
            if (SetProperty(ref _outputAnm2Name, value ?? string.Empty))
            {
                RefreshPreview();
            }
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage =>
        !string.IsNullOrWhiteSpace(ValidationMessage);

    public string GeneratedSourcePreview
    {
        get => _generatedSourcePreview;
        private set => SetProperty(ref _generatedSourcePreview, value);
    }

    public void AddLibrary()
    {
        string resourceName = AllocateResourceName("animation_library");
        ProjectAnimationLibrary library = new()
        {
            ResourceName = resourceName,
            DisplayName = "New animation library",
        };
        Dictionary<Guid, ProjectAnimationLibrary> allLibraries = Libraries
            .Select(static row => row.ToProjectLibrary())
            .Append(library)
            .ToDictionary(static candidate => candidate.Id);
        AnimationLibraryEditRowViewModel row = CreateRow(
            library,
            allLibraries);
        Subscribe(row);
        Libraries.Add(row);
        SelectedLibrary = row;
        ValidationMessage = string.Empty;
    }

    public void RemoveSelectedLibrary()
    {
        if (SelectedLibrary is not { } selected)
        {
            return;
        }

        int index = Libraries.IndexOf(selected);
        Unsubscribe(selected);
        Libraries.Remove(selected);
        SelectedLibrary = Libraries.Count == 0
            ? null
            : Libraries[Math.Clamp(index, 0, Libraries.Count - 1)];
        ValidationMessage = string.Empty;
    }

    public void AddSelectedProjectImport()
    {
        if (SelectedLibrary is not { } owner ||
            SelectedProjectImport is not { } dependency)
        {
            ValidationMessage =
                "Choose both an owning library and a project library to import.";
            return;
        }

        if (owner.Id == dependency.Id)
        {
            ValidationMessage =
                "An animation library cannot import itself.";
            return;
        }

        owner.AddImport(new AnimationLibraryImportRowViewModel(
            new ProjectAnimationLibraryImport
            {
                Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
                ProjectLibraryId = dependency.Id,
            },
            dependency.ResourceName,
            dependency.DisplayName,
            "Project-owned"));
        ValidationMessage = string.Empty;
    }

    public void AddSelectedRetailImport()
    {
        if (SelectedLibrary is not { } owner ||
            SelectedRetailImport is not { } retail)
        {
            ValidationMessage =
                "Choose both an owning library and a fingerprinted retail script to import.";
            return;
        }

        owner.AddImport(new AnimationLibraryImportRowViewModel(
            new ProjectAnimationLibraryImport
            {
                Kind = ProjectAnimationLibraryImportKind.RetailScript,
                RetailScriptIdentity = retail.Identity,
            },
            retail.Identity.ResourceName,
            retail.DisplayName,
            $"{retail.Identity.ProviderId} | {retail.Identity.ContentSha256[..12]}"));
        ValidationMessage = string.Empty;
    }

    public void RemoveSelectedImport()
    {
        if (SelectedLibrary is not { } owner ||
            SelectedImport is not { } import)
        {
            return;
        }

        owner.Imports.Remove(import);
        SelectedImport = null;
        ValidationMessage = string.Empty;
    }

    public void MoveSelectedImport(int delta)
    {
        if (SelectedLibrary is not { } owner ||
            SelectedImport is not { } import ||
            delta == 0)
        {
            return;
        }

        int oldIndex = owner.Imports.IndexOf(import);
        int newIndex = oldIndex + Math.Sign(delta);
        if (oldIndex < 0 || newIndex < 0 ||
            newIndex >= owner.Imports.Count)
        {
            return;
        }

        owner.Imports.Move(oldIndex, newIndex);
        ValidationMessage = string.Empty;
    }

    public bool TryCreateResult(
        out AnimationLibraryAssignmentResult? result)
    {
        result = null;
        if (SelectedLibrary is null)
        {
            ValidationMessage =
                "Create or select an animation library for this variant.";
            return false;
        }

        try
        {
            ImmutableArray<ProjectAnimationLibrary> libraries = Libraries
                .Select(static row => row.ToProjectLibrary())
                .ToImmutableArray();
            AnimationLibraryAssignmentValidator.ValidateAssignment(
                libraries,
                SelectedLibrary.Id,
                OutputAnm2Name.Trim());
            result = new AnimationLibraryAssignmentResult(
                libraries,
                SelectedLibrary.Id,
                OutputAnm2Name.Trim());
            ValidationMessage = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            ValidationMessage = exception.Message;
            RefreshPreview();
            return false;
        }
    }

    private AnimationLibraryEditRowViewModel CreateRow(
        ProjectAnimationLibrary library,
        IReadOnlyDictionary<Guid, ProjectAnimationLibrary> libraries) =>
        new(
            library,
            libraries,
            RetailScripts,
            ModeOptions,
            CollisionOptions)
        {
            ModeOptions = ModeOptions,
            CollisionOptions = CollisionOptions,
        };

    private string AllocateResourceName(string prefix)
    {
        var existing = Libraries
            .Select(static row => row.ResourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(prefix))
        {
            return prefix;
        }

        for (int suffix = 2; suffix <=
                AnimationLibraryAssignmentValidator.MaximumLibraries + 1;
             suffix++)
        {
            string candidate = $"{prefix}_{suffix}";
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No unique animation-library resource name is available.");
    }

    private void Subscribe(AnimationLibraryEditRowViewModel row)
    {
        row.PropertyChanged += OnLibraryPropertyChanged;
        row.Imports.CollectionChanged += OnImportsChanged;
    }

    private void Unsubscribe(AnimationLibraryEditRowViewModel row)
    {
        row.PropertyChanged -= OnLibraryPropertyChanged;
        row.Imports.CollectionChanged -= OnImportsChanged;
    }

    private void OnLibraryPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        _ = sender;
        _ = args;
        ValidationMessage = string.Empty;
        RefreshPreview();
    }

    private void OnImportsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        _ = sender;
        _ = args;
        ValidationMessage = string.Empty;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        if (SelectedLibrary is not { } selected)
        {
            GeneratedSourcePreview =
                "// Create or select a project animation library.";
            return;
        }

        try
        {
            ProjectAnimationLibrary library = selected.ToProjectLibrary();
            Dictionary<Guid, ProjectAnimationLibrary> libraries = Libraries
                .Select(static row => row.ToProjectLibrary())
                .ToDictionary(static candidate => candidate.Id);
            GeneratedSourcePreview =
                AnimationLibraryAssignmentValidator.BuildGeneratedSourcePreview(
                    library,
                    libraries,
                    Request.Assignment with
                    {
                        OutputAnm2Name = OutputAnm2Name.Trim(),
                    });
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException)
        {
            GeneratedSourcePreview =
                $"// Complete the assignment to preview generated source.\n// {exception.Message}";
        }
    }
}

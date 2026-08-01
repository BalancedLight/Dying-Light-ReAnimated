using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.ViewModels;

public enum AssetKind
{
    Mesh,
    Animation,
    AnimationScript,
    FacialDefinition,
    CharacterPreset,
    Texture,
    Unknown,
}

public enum AssetProfileState
{
    NotDecoded,
    Classifying,
    Classified,
    Failed,
}

public sealed record AssetFilterOption(
    string Key,
    string Label);

public sealed class AssetProfileScanRequestedEventArgs(
    IReadOnlyList<AssetItemViewModel> assets)
    : EventArgs
{
    public IReadOnlyList<AssetItemViewModel> Assets { get; } =
        assets ?? throw new ArgumentNullException(nameof(assets));
}

public sealed class AssetItemViewModel : ObservableObject
{
    private const int MaximumProfileErrorCharacters = 2_048;
    private Dl1RetailMeshProfile? _meshProfile;
    private AssetProfileState _profileState;
    private string? _profileError;

    public AssetItemViewModel(
        string id,
        string name,
        AssetKind kind,
        string provider,
        string logicalPath,
        RetailAssetRecord? retailAsset = null,
        Dl1RetailMeshProfile? meshProfile = null)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Provider = provider;
        LogicalPath = logicalPath;
        RetailAsset = retailAsset;
        if (meshProfile is not null)
        {
            ApplyMeshProfile(meshProfile);
        }
    }

    public string Id { get; }

    public string Name { get; }

    public AssetKind Kind { get; }

    public string Provider { get; }

    public string LogicalPath { get; }

    public RetailAssetRecord? RetailAsset { get; }

    public Dl1RetailMeshProfile? MeshProfile => _meshProfile;

    public AssetProfileState ProfileState => _profileState;

    public string? ProfileError => _profileError;

    public string ProfileStatus => ProfileState switch
    {
        AssetProfileState.NotDecoded => "Not decoded",
        AssetProfileState.Classifying => "Classifying...",
        AssetProfileState.Classified => "Classified",
        AssetProfileState.Failed => "Classification failed",
        _ => "Unknown",
    };

    public string ProfileSummary
    {
        get
        {
            if (Kind != AssetKind.Mesh)
            {
                return string.Empty;
            }

            if (MeshProfile is not { } profile)
            {
                return ProfileState switch
                {
                    AssetProfileState.Classifying =>
                        "Mesh profile: classifying...",
                    AssetProfileState.Failed =>
                        "Mesh profile: failed (treated as unknown)",
                    _ =>
                        "Mesh profile: not decoded (unknown)",
                };
            }

            string family = profile.RigFamily == Dl1RigFamily.Unknown
                ? "unknown rig"
                : Humanize(profile.RigFamily.ToString());
            string perspective = profile.Perspective switch
            {
                Dl1MeshPerspective.FirstPerson => "FPP",
                Dl1MeshPerspective.ThirdPerson => "TPP",
                _ => "unknown view",
            };
            string facial = profile.FacialSupport switch
            {
                Dl1FacialSupport.MorphChannels => "facial channels",
                Dl1FacialSupport.DecodedMorphDeltas =>
                    "facial deltas",
                Dl1FacialSupport.None => "no facial",
                _ => "facial unknown",
            };
            string variant = profile.VariantNames.Count switch
            {
                0 => "no decoded variants",
                1 => "1 variant",
                int count => $"{count:N0} variants",
            };
            return string.Join(
                " | ",
                Humanize(profile.GeometryKind.ToString()),
                family,
                perspective,
                facial,
                Humanize(profile.SourceScope.ToString()),
                variant);
        }
    }

    public string ProfileEvidenceSummary
    {
        get
        {
            if (ProfileState == AssetProfileState.Failed)
            {
                return ProfileError
                    ?? "This mesh could not be classified and remains unknown.";
            }

            if (MeshProfile is not { } profile)
            {
                return Kind == AssetKind.Mesh
                    ? "Classification is lazy. Select the mesh or classify the filtered batch to decode evidence."
                    : string.Empty;
            }

            return string.Join(
                Environment.NewLine,
                profile.Evidence
                    .Take(12)
                    .Select(static evidence =>
                        $"{evidence.Code}: {evidence.Message}"));
        }
    }

    public void MarkProfileClassifying()
    {
        if (Kind != AssetKind.Mesh || MeshProfile is not null)
        {
            return;
        }

        _profileError = null;
        SetProfileState(AssetProfileState.Classifying);
    }

    public void ApplyMeshProfile(Dl1RetailMeshProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Kind != AssetKind.Mesh ||
            RetailAsset is not { } retailAsset ||
            !string.Equals(
                retailAsset.Id.StableKey,
                profile.AssetId.StableKey,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The classified profile does not belong to this retail mesh row.",
                nameof(profile));
        }

        _meshProfile = profile;
        _profileError = null;
        SetProfileState(AssetProfileState.Classified);
        OnPropertyChanged(nameof(MeshProfile));
        OnPropertyChanged(nameof(ProfileError));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(ProfileEvidenceSummary));
    }

    public void MarkProfileFailed(string message)
    {
        if (Kind != AssetKind.Mesh || MeshProfile is not null)
        {
            return;
        }

        string error = string.IsNullOrWhiteSpace(message)
            ? "The mesh could not be classified."
            : message.Trim();
        _profileError = error.Length <= MaximumProfileErrorCharacters
            ? error
            : error[..MaximumProfileErrorCharacters];
        SetProfileState(AssetProfileState.Failed);
        OnPropertyChanged(nameof(ProfileError));
        OnPropertyChanged(nameof(ProfileSummary));
        OnPropertyChanged(nameof(ProfileEvidenceSummary));
    }

    public void ResetProfileClassifying()
    {
        if (ProfileState == AssetProfileState.Classifying &&
            MeshProfile is null)
        {
            SetProfileState(AssetProfileState.NotDecoded);
            OnPropertyChanged(nameof(ProfileSummary));
            OnPropertyChanged(nameof(ProfileEvidenceSummary));
        }
    }

    private void SetProfileState(AssetProfileState state)
    {
        if (_profileState == state)
        {
            return;
        }

        _profileState = state;
        OnPropertyChanged(nameof(ProfileState));
        OnPropertyChanged(nameof(ProfileStatus));
    }

    private static string Humanize(string value) =>
        string.Concat(
            value.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $" {character}"
                    : character.ToString()));
}

public sealed class AnimationLibraryItemViewModel : ObservableObject
{
    private string _name;

    public AnimationLibraryItemViewModel(
        Guid id,
        string name,
        string source,
        string sourceModel,
        string targetModel,
        string roles,
        string cadence,
        string duration,
        string mappingState,
        string diagnostics,
        bool isActive,
        Guid? variantGroupId = null,
        string? variantGroupLabel = null,
        TargetBindingStatus targetBindingStatus =
            TargetBindingStatus.Invalid,
        bool showVariantGroupHeader = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "Animation library identifiers cannot be empty.",
                nameof(id));
        }

        Id = id;
        _name = string.IsNullOrWhiteSpace(name)
            ? "Untitled animation"
            : name;
        Source = source ?? string.Empty;
        SourceModel = sourceModel ?? string.Empty;
        TargetModel = targetModel ?? string.Empty;
        Roles = roles ?? string.Empty;
        Cadence = cadence ?? string.Empty;
        Duration = duration ?? string.Empty;
        MappingState = mappingState ?? string.Empty;
        Diagnostics = diagnostics ?? string.Empty;
        IsActive = isActive;
        VariantGroupId = variantGroupId ?? id;
        VariantGroupKey = VariantGroupId.ToString("N");
        VariantGroupLabel = string.IsNullOrWhiteSpace(variantGroupLabel)
            ? _name
            : variantGroupLabel.Trim();
        TargetBindingStatus = targetBindingStatus;
        ShowVariantGroupHeader = showVariantGroupHeader;
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(
            ref _name,
            string.IsNullOrWhiteSpace(value)
                ? "Untitled animation"
                : value.Trim());
    }

    public string Source { get; }

    public string SourceModel { get; }

    public string TargetModel { get; }

    public string Roles { get; }

    public string Cadence { get; }

    public string Duration { get; }

    public string MappingState { get; }

    public string Diagnostics { get; }

    public bool IsActive { get; }

    public Guid VariantGroupId { get; }

    public string VariantGroupKey { get; }

    public string VariantGroupLabel { get; }

    public TargetBindingStatus TargetBindingStatus { get; }

    public bool ShowVariantGroupHeader { get; }
}

public sealed class AssetBrowserViewModel : ObservableObject
{
    public const string AllKinds = "All types";
    public const string AllProviders = "All providers";
    public const int MaximumVisibleAssets = 5_000;
    public const int ProfileScanBatchSize = 128;
    public const string AllFilterKey = "all";
    public const string UnknownFilterKey = "unknown";
    public const string HasValueFilterKey = "has-value";
    public const string NoValueFilterKey = "no-value";
    private const string ValueFilterPrefix = "value:";
    private readonly List<AssetItemViewModel> _allAssets = [];
    private string _searchText = string.Empty;
    private string _selectedKindFilter = AllKinds;
    private string _selectedProviderFilter = AllProviders;
    private AssetFilterOption _selectedGeometryFilter;
    private AssetFilterOption _selectedRigFamilyFilter;
    private AssetFilterOption _selectedRigSignatureFilter;
    private AssetFilterOption _selectedPerspectiveFilter;
    private AssetFilterOption _selectedFacialFilter;
    private AssetFilterOption _selectedSourceScopeFilter;
    private AssetFilterOption _selectedDlcFilter;
    private AssetFilterOption _selectedVariantFilter;
    private AssetItemViewModel? _selectedAsset;
    private bool _isCatalogLoading;
    private bool _isProfileScanRunning;
    private string _profileScanStatus =
        "Profiles are decoded lazily; unknown rows never satisfy evidence filters.";
    private int _filteredAssetCount;

    public AssetBrowserViewModel()
    {
        ReplaceOptions(
            GeometryFilters,
            [
                new(AllFilterKey, "All geometry"),
                new(
                    Dl1MeshGeometryKind.Static.ToString(),
                    "Static geometry"),
                new(
                    Dl1MeshGeometryKind.Skinned.ToString(),
                    "Skinned geometry"),
                new(
                    Dl1MeshGeometryKind.MetadataContainer.ToString(),
                    "Metadata containers"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            RigFamilyFilters,
            [
                new(AllFilterKey, "All rig families"),
                .. Enum.GetValues<Dl1RigFamily>()
                    .Where(static value => value != Dl1RigFamily.Unknown)
                    .Select(static value => new AssetFilterOption(
                        value.ToString(),
                        Humanize(value.ToString()))),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            RigSignatureFilters,
            [
                new(AllFilterKey, "All rig signatures"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            PerspectiveFilters,
            [
                new(AllFilterKey, "All perspectives"),
                new(
                    Dl1MeshPerspective.FirstPerson.ToString(),
                    "First person (FPP)"),
                new(
                    Dl1MeshPerspective.ThirdPerson.ToString(),
                    "Third person (TPP)"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            FacialFilters,
            [
                new(AllFilterKey, "All facial evidence"),
                new(HasValueFilterKey, "Facial support"),
                new(NoValueFilterKey, "No facial support"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            SourceScopeFilters,
            [
                new(AllFilterKey, "All retail scopes"),
                new(
                    Dl1RetailSourceScope.BaseGame.ToString(),
                    "Base game"),
                new(Dl1RetailSourceScope.Dlc.ToString(), "DLC"),
                new(
                    Dl1RetailSourceScope.UserAdded.ToString(),
                    "User-added roots"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            DlcFilters,
            [
                new(AllFilterKey, "All DLC identifiers"),
                new(NoValueFilterKey, "No DLC identifier"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        ReplaceOptions(
            VariantFilters,
            [
                new(AllFilterKey, "All variants"),
                new(HasValueFilterKey, "Has decoded variants"),
                new(NoValueFilterKey, "No decoded variants"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ]);
        _selectedGeometryFilter = GeometryFilters[0];
        _selectedRigFamilyFilter = RigFamilyFilters[0];
        _selectedRigSignatureFilter = RigSignatureFilters[0];
        _selectedPerspectiveFilter = PerspectiveFilters[0];
        _selectedFacialFilter = FacialFilters[0];
        _selectedSourceScopeFilter = SourceScopeFilters[0];
        _selectedDlcFilter = DlcFilters[0];
        _selectedVariantFilter = VariantFilters[0];

        IndexGameCommand = new RelayCommand(
            () => IndexGameRequested?.Invoke(this, EventArgs.Empty),
            () => !IsCatalogLoading);
        ClearSearchCommand = new RelayCommand(
            () => SearchText = string.Empty,
            () => SearchText.Length > 0);
        ClassifyFilteredMeshesCommand = new RelayCommand(
            RequestProfileScan,
            () => !IsProfileScanRunning &&
                  PendingProfileCount > 0);
        CancelProfileScanCommand = new RelayCommand(
            () => ProfileScanCancellationRequested?.Invoke(
                this,
                EventArgs.Empty),
            () => IsProfileScanRunning);
        ResetProfileFiltersCommand = new RelayCommand(
            ResetProfileFilters,
            HasActiveProfileFilters);
    }

    public event EventHandler? IndexGameRequested;

    public event EventHandler<AssetItemViewModel?>? SelectedAssetChanged;

    public event EventHandler<AssetProfileScanRequestedEventArgs>?
        ProfileScanRequested;

    public event EventHandler? ProfileScanCancellationRequested;

    public ObservableCollection<AssetItemViewModel> VisibleAssets { get; } = [];

    public ObservableCollection<string> KindFilters { get; } =
        [AllKinds, .. Enum.GetNames<AssetKind>()];

    public ObservableCollection<string> ProviderFilters { get; } =
        [AllProviders];

    public ObservableCollection<AssetFilterOption> GeometryFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> RigFamilyFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> RigSignatureFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> PerspectiveFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> FacialFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> SourceScopeFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> DlcFilters { get; } = [];

    public ObservableCollection<AssetFilterOption> VariantFilters { get; } = [];

    public IRelayCommand IndexGameCommand { get; }

    public IRelayCommand ClearSearchCommand { get; }

    public IRelayCommand ClassifyFilteredMeshesCommand { get; }

    public IRelayCommand CancelProfileScanCommand { get; }

    public IRelayCommand ResetProfileFiltersCommand { get; }

    public bool IsCatalogLoading => _isCatalogLoading;

    public string CatalogActionLabel => IsCatalogLoading
        ? "Loading..."
        : HasIndexedAssets
            ? "Refresh catalog"
            : "Load catalog";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ClearSearchCommand.NotifyCanExecuteChanged();
                RebuildVisibleAssets();
            }
        }
    }

    public string SelectedKindFilter
    {
        get => _selectedKindFilter;
        set
        {
            string requested = string.IsNullOrWhiteSpace(value)
                ? AllKinds
                : value;
            if (SetProperty(ref _selectedKindFilter, requested))
            {
                RebuildVisibleAssets();
            }
        }
    }

    public string SelectedProviderFilter
    {
        get => _selectedProviderFilter;
        set
        {
            string requested = string.IsNullOrWhiteSpace(value)
                ? AllProviders
                : value;
            if (SetProperty(ref _selectedProviderFilter, requested))
            {
                RebuildVisibleAssets();
            }
        }
    }

    public AssetFilterOption SelectedGeometryFilter
    {
        get => _selectedGeometryFilter;
        set => SetProfileFilter(
            ref _selectedGeometryFilter,
            value,
            GeometryFilters);
    }

    public AssetFilterOption SelectedRigFamilyFilter
    {
        get => _selectedRigFamilyFilter;
        set => SetProfileFilter(
            ref _selectedRigFamilyFilter,
            value,
            RigFamilyFilters);
    }

    public AssetFilterOption SelectedRigSignatureFilter
    {
        get => _selectedRigSignatureFilter;
        set => SetProfileFilter(
            ref _selectedRigSignatureFilter,
            value,
            RigSignatureFilters);
    }

    public AssetFilterOption SelectedPerspectiveFilter
    {
        get => _selectedPerspectiveFilter;
        set => SetProfileFilter(
            ref _selectedPerspectiveFilter,
            value,
            PerspectiveFilters);
    }

    public AssetFilterOption SelectedFacialFilter
    {
        get => _selectedFacialFilter;
        set => SetProfileFilter(
            ref _selectedFacialFilter,
            value,
            FacialFilters);
    }

    public AssetFilterOption SelectedSourceScopeFilter
    {
        get => _selectedSourceScopeFilter;
        set => SetProfileFilter(
            ref _selectedSourceScopeFilter,
            value,
            SourceScopeFilters);
    }

    public AssetFilterOption SelectedDlcFilter
    {
        get => _selectedDlcFilter;
        set => SetProfileFilter(
            ref _selectedDlcFilter,
            value,
            DlcFilters);
    }

    public AssetFilterOption SelectedVariantFilter
    {
        get => _selectedVariantFilter;
        set => SetProfileFilter(
            ref _selectedVariantFilter,
            value,
            VariantFilters);
    }

    public AssetItemViewModel? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (SetProperty(ref _selectedAsset, value))
            {
                SelectedAssetChanged?.Invoke(this, value);
            }
        }
    }

    public int IndexedAssetCount => _allAssets.Count;

    public bool HasIndexedAssets => IndexedAssetCount > 0;

    public int FilteredAssetCount => _filteredAssetCount;

    public bool HasFilteredAssets => FilteredAssetCount > 0;

    public string EmptyResultMessage => IsCatalogLoading
        ? "Loading the saved Dying Light 1 asset catalog..."
        : HasIndexedAssets
            ? "No resources match the current filters. Undecoded meshes only appear in explicit unknown/not-decoded profile filters."
            : "No saved asset catalog is available. Load the Dying Light 1 assets once; later launches reuse the validated local cache.";

    public bool IsResultTruncated =>
        FilteredAssetCount > VisibleAssets.Count;

    public string ResultSummary => IsCatalogLoading
        ? "Loading saved catalog"
        : IsResultTruncated
            ? $"Showing {VisibleAssets.Count:N0} of {FilteredAssetCount:N0} matches"
            : $"{FilteredAssetCount:N0} matching resources";

    public int ClassifiedMeshCount =>
        _allAssets.Count(static item =>
            item.MeshProfile is not null);

    public int PendingProfileCount =>
        ApplyGeneralFilters(_allAssets)
            .Count(static item =>
                item.Kind == AssetKind.Mesh &&
                item.RetailAsset is not null &&
                item.MeshProfile is null &&
                item.ProfileState == AssetProfileState.NotDecoded);

    public bool IsProfileScanRunning
    {
        get => _isProfileScanRunning;
        private set
        {
            if (SetProperty(ref _isProfileScanRunning, value))
            {
                ClassifyFilteredMeshesCommand.NotifyCanExecuteChanged();
                CancelProfileScanCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string ProfileScanStatus
    {
        get => _profileScanStatus;
        private set => SetProperty(
            ref _profileScanStatus,
            value ?? string.Empty);
    }

    public void ReplaceAssets(IEnumerable<AssetItemViewModel> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string? selectedId = SelectedAsset?.Id;
        _allAssets.Clear();
        _allAssets.AddRange(assets);
        string previousProvider = SelectedProviderFilter;
        ProviderFilters.Clear();
        ProviderFilters.Add(AllProviders);
        foreach (string provider in _allAssets
                     .Select(static asset => asset.Provider)
                     .Where(static provider =>
                         !string.IsNullOrWhiteSpace(provider))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(static provider => provider, StringComparer.OrdinalIgnoreCase))
        {
            ProviderFilters.Add(provider);
        }

        SelectedProviderFilter = ProviderFilters.Contains(
            previousProvider,
            StringComparer.OrdinalIgnoreCase)
                ? ProviderFilters.First(provider => string.Equals(
                    provider,
                    previousProvider,
                    StringComparison.OrdinalIgnoreCase))
                : AllProviders;
        RefreshDynamicProfileOptions();
        SelectedAsset = selectedId is null
            ? null
            : _allAssets.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    selectedId,
                    StringComparison.Ordinal));
        OnPropertyChanged(nameof(IndexedAssetCount));
        OnPropertyChanged(nameof(HasIndexedAssets));
        OnPropertyChanged(nameof(EmptyResultMessage));
        OnPropertyChanged(nameof(CatalogActionLabel));
        NotifyProfileCountsChanged();
        RebuildVisibleAssets();
    }

    public void SetCatalogLoading(bool isLoading)
    {
        if (!SetProperty(
                ref _isCatalogLoading,
                isLoading,
                nameof(IsCatalogLoading)))
        {
            return;
        }

        OnPropertyChanged(nameof(CatalogActionLabel));
        OnPropertyChanged(nameof(EmptyResultMessage));
        OnPropertyChanged(nameof(ResultSummary));
        IndexGameCommand.NotifyCanExecuteChanged();
    }

    public void NotifyProfileChanged(AssetItemViewModel asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!_allAssets.Contains(asset))
        {
            return;
        }

        RefreshDynamicProfileOptions();
        NotifyProfileCountsChanged();
        RebuildVisibleAssets();
    }

    public IReadOnlyList<AssetItemViewModel> GetProfileScanCandidates(
        int maximumResults)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumResults);
        return ApplyGeneralFilters(_allAssets)
            .Where(static item =>
                item.Kind == AssetKind.Mesh &&
                item.RetailAsset is not null &&
                item.MeshProfile is null &&
                item.ProfileState == AssetProfileState.NotDecoded)
            .Take(maximumResults)
            .ToArray();
    }

    public void SetProfileScanRunning(
        bool isRunning,
        string status)
    {
        IsProfileScanRunning = isRunning;
        ProfileScanStatus = status;
        if (!isRunning)
        {
            foreach (AssetItemViewModel item in _allAssets)
            {
                item.ResetProfileClassifying();
            }

            NotifyProfileCountsChanged();
            RebuildVisibleAssets();
        }
    }

    private void RebuildVisibleAssets()
    {
        AssetItemViewModel[] matches = ApplyGeneralFilters(_allAssets)
            .Where(MatchesProfileFilters)
            .ToArray();
        _filteredAssetCount = matches.Length;

        SynchronizeVisibleAssets(
            matches.Take(MaximumVisibleAssets).ToArray());

        OnPropertyChanged(nameof(FilteredAssetCount));
        OnPropertyChanged(nameof(HasFilteredAssets));
        OnPropertyChanged(nameof(EmptyResultMessage));
        OnPropertyChanged(nameof(IsResultTruncated));
        OnPropertyChanged(nameof(ResultSummary));
        OnPropertyChanged(nameof(PendingProfileCount));
        ClassifyFilteredMeshesCommand.NotifyCanExecuteChanged();
    }

    private IEnumerable<AssetItemViewModel> ApplyGeneralFilters(
        IEnumerable<AssetItemViewModel> assets)
    {
        string filter = SearchText.Trim();
        IEnumerable<AssetItemViewModel> matches = assets;
        if (!string.Equals(
                SelectedKindFilter,
                AllKinds,
                StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse(
                SelectedKindFilter,
                ignoreCase: true,
                out AssetKind selectedKind))
        {
            matches = matches.Where(item => item.Kind == selectedKind);
        }

        if (!string.Equals(
                SelectedProviderFilter,
                AllProviders,
                StringComparison.OrdinalIgnoreCase))
        {
            matches = matches.Where(item => string.Equals(
                item.Provider,
                SelectedProviderFilter,
                StringComparison.OrdinalIgnoreCase));
        }

        if (filter.Length > 0)
        {
            matches = matches.Where(item =>
                item.Name.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase) ||
                item.LogicalPath.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase) ||
                item.Provider.Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase) ||
                item.Kind.ToString().Contains(
                    filter,
                    StringComparison.OrdinalIgnoreCase));
        }

        return matches;
    }

    private bool MatchesProfileFilters(AssetItemViewModel item)
    {
        if (!HasActiveProfileFilters())
        {
            return true;
        }

        if (item.Kind != AssetKind.Mesh)
        {
            return false;
        }

        Dl1RetailMeshProfile? profile = item.MeshProfile;
        return
            MatchEnum(
                SelectedGeometryFilter,
                profile?.GeometryKind,
                Dl1MeshGeometryKind.Unknown) &&
            MatchEnum(
                SelectedRigFamilyFilter,
                profile?.RigFamily,
                Dl1RigFamily.Unknown) &&
            MatchString(
                SelectedRigSignatureFilter,
                profile?.RigSignature) &&
            MatchEnum(
                SelectedPerspectiveFilter,
                profile?.Perspective,
                Dl1MeshPerspective.Unknown) &&
            MatchFacial(SelectedFacialFilter, profile) &&
            MatchEnum(
                SelectedSourceScopeFilter,
                profile?.SourceScope,
                Dl1RetailSourceScope.Unknown) &&
            MatchDlc(SelectedDlcFilter, profile) &&
            MatchVariant(SelectedVariantFilter, profile);
    }

    private static bool MatchEnum<T>(
        AssetFilterOption option,
        T? actual,
        T unknown)
        where T : struct, Enum
    {
        if (option.Key == AllFilterKey)
        {
            return true;
        }

        if (option.Key == UnknownFilterKey)
        {
            return actual is null ||
                   EqualityComparer<T>.Default.Equals(
                       actual.Value,
                       unknown);
        }

        return actual is { } value &&
               Enum.TryParse(option.Key, out T requested) &&
               EqualityComparer<T>.Default.Equals(value, requested);
    }

    private static bool MatchString(
        AssetFilterOption option,
        string? actual)
    {
        if (option.Key == AllFilterKey)
        {
            return true;
        }

        if (option.Key == UnknownFilterKey)
        {
            return string.IsNullOrWhiteSpace(actual);
        }

        return option.Key.StartsWith(
                   ValueFilterPrefix,
                   StringComparison.Ordinal) &&
               string.Equals(
                   actual,
                   option.Key[ValueFilterPrefix.Length..],
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchFacial(
        AssetFilterOption option,
        Dl1RetailMeshProfile? profile) =>
        option.Key switch
        {
            AllFilterKey => true,
            HasValueFilterKey => profile?.HasFacialSupport == true,
            NoValueFilterKey =>
                profile?.FacialSupport == Dl1FacialSupport.None,
            UnknownFilterKey =>
                profile is null ||
                profile.FacialSupport == Dl1FacialSupport.Unknown,
            _ => false,
        };

    private static bool MatchDlc(
        AssetFilterOption option,
        Dl1RetailMeshProfile? profile) =>
        option.Key switch
        {
            AllFilterKey => true,
            NoValueFilterKey =>
                profile is not null &&
                profile.SourceScope != Dl1RetailSourceScope.Unknown &&
                string.IsNullOrWhiteSpace(profile.DlcIdentifier),
            UnknownFilterKey =>
                profile is null ||
                profile.SourceScope == Dl1RetailSourceScope.Unknown,
            _ when option.Key.StartsWith(
                ValueFilterPrefix,
                StringComparison.Ordinal) =>
                profile is not null &&
                string.Equals(
                    profile.DlcIdentifier,
                    option.Key[ValueFilterPrefix.Length..],
                    StringComparison.OrdinalIgnoreCase),
            _ => false,
        };

    private static bool MatchVariant(
        AssetFilterOption option,
        Dl1RetailMeshProfile? profile) =>
        option.Key switch
        {
            AllFilterKey => true,
            HasValueFilterKey => profile?.VariantNames.Count > 0,
            NoValueFilterKey =>
                profile is not null &&
                profile.VariantNames.Count == 0,
            UnknownFilterKey => profile is null,
            _ when option.Key.StartsWith(
                ValueFilterPrefix,
                StringComparison.Ordinal) =>
                profile?.VariantNames.Contains(
                    option.Key[ValueFilterPrefix.Length..],
                    StringComparer.OrdinalIgnoreCase) == true,
            _ => false,
        };

    private void SetProfileFilter(
        ref AssetFilterOption field,
        AssetFilterOption? requested,
        ObservableCollection<AssetFilterOption> available,
        [CallerMemberName] string? propertyName = null)
    {
        AssetFilterOption value = requested is null
            ? available[0]
            : available.FirstOrDefault(option =>
                string.Equals(
                    option.Key,
                    requested.Key,
                    StringComparison.Ordinal)) ??
              available[0];
        if (SetProperty(ref field, value, propertyName))
        {
            ResetProfileFiltersCommand.NotifyCanExecuteChanged();
            RebuildVisibleAssets();
        }
    }

    private bool HasActiveProfileFilters() =>
        SelectedGeometryFilter.Key != AllFilterKey ||
        SelectedRigFamilyFilter.Key != AllFilterKey ||
        SelectedRigSignatureFilter.Key != AllFilterKey ||
        SelectedPerspectiveFilter.Key != AllFilterKey ||
        SelectedFacialFilter.Key != AllFilterKey ||
        SelectedSourceScopeFilter.Key != AllFilterKey ||
        SelectedDlcFilter.Key != AllFilterKey ||
        SelectedVariantFilter.Key != AllFilterKey;

    private void ResetProfileFilters()
    {
        _selectedGeometryFilter = GeometryFilters[0];
        _selectedRigFamilyFilter = RigFamilyFilters[0];
        _selectedRigSignatureFilter = RigSignatureFilters[0];
        _selectedPerspectiveFilter = PerspectiveFilters[0];
        _selectedFacialFilter = FacialFilters[0];
        _selectedSourceScopeFilter = SourceScopeFilters[0];
        _selectedDlcFilter = DlcFilters[0];
        _selectedVariantFilter = VariantFilters[0];
        OnPropertyChanged(nameof(SelectedGeometryFilter));
        OnPropertyChanged(nameof(SelectedRigFamilyFilter));
        OnPropertyChanged(nameof(SelectedRigSignatureFilter));
        OnPropertyChanged(nameof(SelectedPerspectiveFilter));
        OnPropertyChanged(nameof(SelectedFacialFilter));
        OnPropertyChanged(nameof(SelectedSourceScopeFilter));
        OnPropertyChanged(nameof(SelectedDlcFilter));
        OnPropertyChanged(nameof(SelectedVariantFilter));
        ResetProfileFiltersCommand.NotifyCanExecuteChanged();
        RebuildVisibleAssets();
    }

    private void RequestProfileScan()
    {
        IReadOnlyList<AssetItemViewModel> candidates =
            GetProfileScanCandidates(ProfileScanBatchSize);
        if (candidates.Count == 0)
        {
            return;
        }

        if (ProfileScanRequested is null)
        {
            ProfileScanStatus =
                "The host did not provide a retail mesh classifier.";
            return;
        }

        IsProfileScanRunning = true;
        ProfileScanStatus =
            $"Classifying up to {candidates.Count:N0} filtered mesh rows...";
        ProfileScanRequested.Invoke(
            this,
            new AssetProfileScanRequestedEventArgs(candidates));
    }

    private void RefreshDynamicProfileOptions()
    {
        string rigSignatureKey = _selectedRigSignatureFilter.Key;
        RefreshDynamicOptions(
            RigSignatureFilters,
            [
                new(AllFilterKey, "All rig signatures"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ],
            _allAssets
                .Select(static item => item.MeshProfile?.RigSignature)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!),
            static value =>
                value.Length <= 20
                    ? value
                    : $"{value[..20]}...");
        _selectedRigSignatureFilter = FindOption(
            RigSignatureFilters,
            rigSignatureKey);
        OnPropertyChanged(nameof(SelectedRigSignatureFilter));

        string dlcKey = _selectedDlcFilter.Key;
        RefreshDynamicOptions(
            DlcFilters,
            [
                new(AllFilterKey, "All DLC identifiers"),
                new(NoValueFilterKey, "No DLC identifier"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ],
            _allAssets
                .Select(static item => item.MeshProfile?.DlcIdentifier)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!),
            static value => GetBoundedFilterLabel(value));
        _selectedDlcFilter = FindOption(
            DlcFilters,
            dlcKey);
        OnPropertyChanged(nameof(SelectedDlcFilter));

        string variantKey = _selectedVariantFilter.Key;
        RefreshDynamicOptions(
            VariantFilters,
            [
                new(AllFilterKey, "All variants"),
                new(HasValueFilterKey, "Has decoded variants"),
                new(NoValueFilterKey, "No decoded variants"),
                new(UnknownFilterKey, "Unknown / not decoded"),
            ],
            _allAssets
                .Where(static item => item.MeshProfile is not null)
                .SelectMany(static item =>
                    item.MeshProfile!.VariantNames),
            static value => GetBoundedFilterLabel(value));
        _selectedVariantFilter = FindOption(
            VariantFilters,
            variantKey);
        OnPropertyChanged(nameof(SelectedVariantFilter));
    }

    private static void RefreshDynamicOptions(
        ObservableCollection<AssetFilterOption> destination,
        IReadOnlyList<AssetFilterOption> fixedOptions,
        IEnumerable<string> values,
        Func<string, string> labelSelector)
    {
        AssetFilterOption[] options =
        [
            .. fixedOptions,
            .. values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    static value => value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(value => new AssetFilterOption(
                    $"{ValueFilterPrefix}{value}",
                    labelSelector(value))),
        ];
        ReplaceOptions(destination, options);
    }

    private static AssetFilterOption FindOption(
        ObservableCollection<AssetFilterOption> options,
        string key) =>
        options.FirstOrDefault(option =>
            string.Equals(
                option.Key,
                key,
                StringComparison.Ordinal)) ??
        options[0];

    private static void ReplaceOptions(
        ObservableCollection<AssetFilterOption> destination,
        IEnumerable<AssetFilterOption> options)
    {
        AssetFilterOption[] requested = options.ToArray();
        int sharedCount = Math.Min(
            destination.Count,
            requested.Length);
        for (int index = 0; index < sharedCount; index++)
        {
            if (destination[index] != requested[index])
            {
                destination[index] = requested[index];
            }
        }

        while (destination.Count > requested.Length)
        {
            destination.RemoveAt(destination.Count - 1);
        }

        for (int index = destination.Count;
             index < requested.Length;
             index++)
        {
            destination.Add(requested[index]);
        }
    }

    private void SynchronizeVisibleAssets(
        AssetItemViewModel[] requested)
    {
        for (int index = 0; index < requested.Length; index++)
        {
            AssetItemViewModel item = requested[index];
            if (index < VisibleAssets.Count &&
                ReferenceEquals(VisibleAssets[index], item))
            {
                continue;
            }

            int existingIndex = VisibleAssets.IndexOf(item);
            if (existingIndex >= 0)
            {
                VisibleAssets.Move(existingIndex, index);
            }
            else
            {
                VisibleAssets.Insert(index, item);
            }
        }

        while (VisibleAssets.Count > requested.Length)
        {
            VisibleAssets.RemoveAt(VisibleAssets.Count - 1);
        }
    }

    private void NotifyProfileCountsChanged()
    {
        OnPropertyChanged(nameof(ClassifiedMeshCount));
        OnPropertyChanged(nameof(PendingProfileCount));
        ClassifyFilteredMeshesCommand.NotifyCanExecuteChanged();
    }

    private static string Humanize(string value) =>
        string.Concat(
            value.Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $" {character}"
                    : character.ToString()));

    private static string GetBoundedFilterLabel(string value) =>
        value.Length <= 48
            ? value
            : $"{value[..48]}...";
}

public sealed class SkeletonNodeViewModel : ObservableObject
{
    private bool _isMapped;
    private bool _isLocked;
    private double _positionX;
    private double _positionY;
    private double _positionZ;
    private double _rotationX;
    private double _rotationY;
    private double _rotationZ;
    private double _scaleX = 1.0;
    private double _scaleY = 1.0;
    private double _scaleZ = 1.0;

    public SkeletonNodeViewModel(
        string name,
        string path,
        int index,
        int parentIndex,
        Matrix4x4? restLocalTransform = null,
        Matrix4x4? restWorldTransform = null,
        BoneRenderRole role = BoneRenderRole.Deform,
        bool isHierarchyOverlayVisible = true)
    {
        Name = name;
        Path = path;
        Index = index;
        ParentIndex = parentIndex;
        RestLocalTransform = restLocalTransform ?? Matrix4x4.Identity;
        RestWorldTransform = restWorldTransform ?? Matrix4x4.Identity;
        Role = role;
        IsHierarchyOverlayVisible = isHierarchyOverlayVisible;
    }

    public string Name { get; }

    public string Path { get; }

    public int Index { get; }

    public int ParentIndex { get; }

    public Matrix4x4 RestLocalTransform { get; }

    public Matrix4x4 RestWorldTransform { get; }

    public BoneRenderRole Role { get; }

    public bool IsHierarchyOverlayVisible { get; }

    public ObservableCollection<SkeletonNodeViewModel> Children { get; } = [];

    public bool IsMapped
    {
        get => _isMapped;
        set => SetProperty(ref _isMapped, value);
    }

    public bool IsLocked
    {
        get => _isLocked;
        set => SetProperty(ref _isLocked, value);
    }

    public double PositionX
    {
        get => _positionX;
        set => SetProperty(ref _positionX, value);
    }

    public double PositionY
    {
        get => _positionY;
        set => SetProperty(ref _positionY, value);
    }

    public double PositionZ
    {
        get => _positionZ;
        set => SetProperty(ref _positionZ, value);
    }

    public double RotationX
    {
        get => _rotationX;
        set => SetProperty(ref _rotationX, value);
    }

    public double RotationY
    {
        get => _rotationY;
        set => SetProperty(ref _rotationY, value);
    }

    public double RotationZ
    {
        get => _rotationZ;
        set => SetProperty(ref _rotationZ, value);
    }

    public double ScaleX
    {
        get => _scaleX;
        set => SetProperty(ref _scaleX, value);
    }

    public double ScaleY
    {
        get => _scaleY;
        set => SetProperty(ref _scaleY, value);
    }

    public double ScaleZ
    {
        get => _scaleZ;
        set => SetProperty(ref _scaleZ, value);
    }
}

public sealed class BoneTransformEditorViewModel : ObservableObject
{
    private SkeletonNodeViewModel? _bone;
    private QuaternionD _exactRotation = QuaternionD.Identity;
    private bool _rotationFieldsDirty;
    private bool _synchronizingTransform;
    private RenderTransformGizmoMode _gizmoMode =
        RenderTransformGizmoMode.Translate;
    private RenderGizmoSpace _gizmoSpace =
        RenderGizmoSpace.Local;

    public BoneTransformEditorViewModel()
    {
        ApplyCommand = new RelayCommand(Apply, CanEdit);
        ResetCommand = new RelayCommand(
            Reset,
            () => Bone is { IsLocked: false });
    }

    public event EventHandler<SkeletonNodeViewModel>? TransformApplied;

    public event EventHandler? GizmoModeChanged;

    public event EventHandler? GizmoSpaceChanged;

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand ResetCommand { get; }

    public IReadOnlyList<RenderGizmoSpace> GizmoSpaces { get; } =
        Enum.GetValues<RenderGizmoSpace>();

    public IReadOnlyList<RenderTransformGizmoMode> GizmoModes { get; } =
        Enum.GetValues<RenderTransformGizmoMode>();

    public RenderTransformGizmoMode GizmoMode
    {
        get => _gizmoMode;
        set
        {
            if (SetProperty(ref _gizmoMode, value))
            {
                OnPropertyChanged(nameof(IsGizmoSpaceEnabled));
                OnPropertyChanged(nameof(EffectiveGizmoSpace));
                GizmoModeChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool IsGizmoSpaceEnabled =>
        GizmoMode != RenderTransformGizmoMode.Scale;

    public RenderGizmoSpace EffectiveGizmoSpace =>
        GizmoMode == RenderTransformGizmoMode.Scale
            ? RenderGizmoSpace.Local
            : GizmoSpace;

    public RenderGizmoSpace GizmoSpace
    {
        get => _gizmoSpace;
        set
        {
            if (SetProperty(ref _gizmoSpace, value))
            {
                OnPropertyChanged(nameof(EffectiveGizmoSpace));
                GizmoSpaceChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public SkeletonNodeViewModel? Bone
    {
        get => _bone;
        set
        {
            SkeletonNodeViewModel? previous = _bone;
            if (SetProperty(ref _bone, value))
            {
                if (previous is not null)
                {
                    previous.PropertyChanged -= OnBonePropertyChanged;
                }

                if (_bone is not null)
                {
                    _bone.PropertyChanged += OnBonePropertyChanged;
                }

                OnPropertyChanged(nameof(HasSelection));
                ApplyCommand.NotifyCanExecuteChanged();
                ResetCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelection => Bone is not null;

    public void SetTransform(TransformTRS transform)
    {
        if (!transform.IsFinite ||
            transform.Rotation.LengthSquared <= 1.0e-12)
        {
            throw new ArgumentException(
                "The editor transform must be finite and contain a non-zero rotation.",
                nameof(transform));
        }

        if (Bone is null)
        {
            return;
        }

        TransformTRS normalized = transform.Normalized();
        _synchronizingTransform = true;
        try
        {
            Bone.PositionX = normalized.Translation.X;
            Bone.PositionY = normalized.Translation.Y;
            Bone.PositionZ = normalized.Translation.Z;
            SetEulerDegrees(Bone, normalized.Rotation);
            Bone.ScaleX = normalized.Scale.X;
            Bone.ScaleY = normalized.Scale.Y;
            Bone.ScaleZ = normalized.Scale.Z;
            _exactRotation = normalized.Rotation;
            _rotationFieldsDirty = false;
        }
        finally
        {
            _synchronizingTransform = false;
        }

        NotifyTransformCanExecuteChanged();
    }

    public bool TryGetTransform(
        out TransformTRS transform,
        out string? validationError)
    {
        transform = default;
        if (Bone is null)
        {
            validationError = "Select a bone before editing its transform.";
            return false;
        }

        Vector3D translation = new(
            Bone.PositionX,
            Bone.PositionY,
            Bone.PositionZ);
        Vector3D scale = new(
            Bone.ScaleX,
            Bone.ScaleY,
            Bone.ScaleZ);
        if (!translation.IsFinite)
        {
            validationError =
                "Bone translation values must be finite.";
            return false;
        }

        if (!BoneTransformAuthoringPolicy.IsValidScale(scale))
        {
            validationError =
                $"Bone scale values must be finite and between {BoneTransformAuthoringPolicy.MinimumScale:G} and {BoneTransformAuthoringPolicy.MaximumScale:G}.";
            return false;
        }

        try
        {
            QuaternionD rotation = _rotationFieldsDirty
                ? CreateRotationFromEulerDegrees(Bone)
                : _exactRotation;
            if (!rotation.IsFinite ||
                rotation.LengthSquared <= 1.0e-12)
            {
                validationError =
                    "Bone rotation must contain finite values and a non-zero quaternion.";
                return false;
            }

            transform = new TransformTRS(
                translation,
                rotation.Normalized(),
                scale);
            validationError = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArithmeticException
            or InvalidOperationException
            or OverflowException)
        {
            validationError = exception.Message;
            return false;
        }
    }

    private bool CanEdit()
    {
        return Bone is { IsLocked: false } &&
            TryGetTransform(out _, out _);
    }

    private void OnBonePropertyChanged(
        object? sender,
        PropertyChangedEventArgs args)
    {
        if (_synchronizingTransform)
        {
            return;
        }

        if (args.PropertyName is
            nameof(SkeletonNodeViewModel.RotationX) or
            nameof(SkeletonNodeViewModel.RotationY) or
            nameof(SkeletonNodeViewModel.RotationZ))
        {
            _rotationFieldsDirty = true;
        }

        if (args.PropertyName is
            nameof(SkeletonNodeViewModel.IsLocked) or
            nameof(SkeletonNodeViewModel.PositionX) or
            nameof(SkeletonNodeViewModel.PositionY) or
            nameof(SkeletonNodeViewModel.PositionZ) or
            nameof(SkeletonNodeViewModel.RotationX) or
            nameof(SkeletonNodeViewModel.RotationY) or
            nameof(SkeletonNodeViewModel.RotationZ) or
            nameof(SkeletonNodeViewModel.ScaleX) or
            nameof(SkeletonNodeViewModel.ScaleY) or
            nameof(SkeletonNodeViewModel.ScaleZ))
        {
            NotifyTransformCanExecuteChanged();
        }
    }

    private void Apply()
    {
        if (Bone is not null &&
            TryGetTransform(out _, out _))
        {
            TransformApplied?.Invoke(this, Bone);
        }
    }

    private void Reset()
    {
        if (Bone is null)
        {
            return;
        }

        SetTransform(TransformTRS.Identity);
        TransformApplied?.Invoke(this, Bone);
    }

    private void NotifyTransformCanExecuteChanged()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
    }

    private static QuaternionD CreateRotationFromEulerDegrees(
        SkeletonNodeViewModel bone)
    {
        const double degreesToRadians = Math.PI / 180.0;
        System.Numerics.Quaternion rotation =
            System.Numerics.Quaternion.CreateFromYawPitchRoll(
                checked((float)(bone.RotationY * degreesToRadians)),
                checked((float)(bone.RotationX * degreesToRadians)),
                checked((float)(bone.RotationZ * degreesToRadians)));
        return new QuaternionD(
            rotation.X,
            rotation.Y,
            rotation.Z,
            rotation.W);
    }

    private static void SetEulerDegrees(
        SkeletonNodeViewModel bone,
        QuaternionD rotation)
    {
        QuaternionD q = rotation.Normalized();
        double pitch = Math.Asin(Math.Clamp(
            2.0 * ((q.W * q.X) - (q.Y * q.Z)),
            -1.0,
            1.0));
        double yaw = Math.Atan2(
            2.0 * ((q.W * q.Y) + (q.X * q.Z)),
            1.0 - (2.0 * ((q.X * q.X) + (q.Y * q.Y))));
        double roll = Math.Atan2(
            2.0 * ((q.W * q.Z) + (q.X * q.Y)),
            1.0 - (2.0 * ((q.X * q.X) + (q.Z * q.Z))));
        const double radiansToDegrees = 180.0 / Math.PI;
        bone.RotationX = pitch * radiansToDegrees;
        bone.RotationY = yaw * radiansToDegrees;
        bone.RotationZ = roll * radiansToDegrees;
    }
}

internal static class BoneTransformAuthoringPolicy
{
    public const double MinimumScale = 0.001;
    public const double MaximumScale = 1000.0;

    public static bool IsValidScale(Vector3D scale) =>
        scale.IsFinite &&
        scale.X is >= MinimumScale and <= MaximumScale &&
        scale.Y is >= MinimumScale and <= MaximumScale &&
        scale.Z is >= MinimumScale and <= MaximumScale;
}

public sealed class IkConstraintEditorViewModel : ObservableObject
{
    private string? _selectedChain;
    private double _effectorX;
    private double _effectorY;
    private double _effectorZ;
    private double _poleX;
    private double _poleY;
    private double _poleZ = 1;
    private double _weight = 1;
    private bool _useEndOrientation;
    private bool _bakeToEditLayer;
    private double _endRotationX;
    private double _endRotationY;
    private double _endRotationZ;

    public ObservableCollection<string> Chains { get; } = [];

    public string? SelectedChain
    {
        get => _selectedChain;
        set => SetProperty(ref _selectedChain, value);
    }

    public double EffectorX
    {
        get => _effectorX;
        set => SetProperty(ref _effectorX, value);
    }

    public double EffectorY
    {
        get => _effectorY;
        set => SetProperty(ref _effectorY, value);
    }

    public double EffectorZ
    {
        get => _effectorZ;
        set => SetProperty(ref _effectorZ, value);
    }

    public double PoleX
    {
        get => _poleX;
        set => SetProperty(ref _poleX, value);
    }

    public double PoleY
    {
        get => _poleY;
        set => SetProperty(ref _poleY, value);
    }

    public double PoleZ
    {
        get => _poleZ;
        set => SetProperty(ref _poleZ, value);
    }

    public double Weight
    {
        get => _weight;
        set => SetProperty(
            ref _weight,
            double.IsFinite(value)
                ? Math.Clamp(value, 0, 1)
                : 0);
    }

    public bool UseEndOrientation
    {
        get => _useEndOrientation;
        set => SetProperty(ref _useEndOrientation, value);
    }

    public bool BakeToEditLayer
    {
        get => _bakeToEditLayer;
        set => SetProperty(ref _bakeToEditLayer, value);
    }

    public double EndRotationX
    {
        get => _endRotationX;
        set => SetProperty(ref _endRotationX, value);
    }

    public double EndRotationY
    {
        get => _endRotationY;
        set => SetProperty(ref _endRotationY, value);
    }

    public double EndRotationZ
    {
        get => _endRotationZ;
        set => SetProperty(ref _endRotationZ, value);
    }

    public void ReplaceChains(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string? previous = SelectedChain;
        Chains.Clear();
        foreach (string name in names)
        {
            Chains.Add(name);
        }

        SelectedChain = previous is not null &&
                        Chains.Contains(previous)
            ? previous
            : Chains.FirstOrDefault();
    }
}

public sealed class BoneMappingViewModel : ObservableObject
{
    private string? _targetBone;
    private bool _isLocked;
    private bool _isReviewed;
    private RetargetTransferPolicy _transferPolicy;
    private RetargetComponentPolicy _componentPolicy;

    public BoneMappingViewModel(
        string sourceBone,
        string? targetBone,
        double confidence,
        string status,
        bool isLocked = false,
        bool isReviewed = false,
        RetargetMappingKind mappingKind =
            RetargetMappingKind.Bone,
        RetargetTransferPolicy transferPolicy =
            RetargetTransferPolicy.GlobalBindBasis,
        RetargetComponentPolicy componentPolicy =
            RetargetComponentPolicy.FullTransform)
    {
        SourceBone = sourceBone;
        _targetBone = targetBone;
        Confidence = confidence;
        Status = status;
        MappingKind = mappingKind;
        _transferPolicy = transferPolicy;
        _componentPolicy = componentPolicy;
        _isLocked =
            !string.IsNullOrWhiteSpace(targetBone) &&
            isLocked;
        _isReviewed =
            !string.IsNullOrWhiteSpace(targetBone) &&
            isReviewed;
    }

    public string SourceBone { get; }

    public double Confidence { get; }

    public string Status { get; }

    public RetargetMappingKind MappingKind { get; }

    public string MappingKindLabel =>
        MappingKind == RetargetMappingKind.HelperOverride
            ? "Helper override"
            : "Body / bone";

    public bool IsHelperOverride =>
        MappingKind == RetargetMappingKind.HelperOverride;

    public IReadOnlyList<RetargetTransferPolicy>
        TransferPolicies
    { get; } =
            Enum.GetValues<RetargetTransferPolicy>();

    public IReadOnlyList<RetargetComponentPolicy>
        ComponentPolicies
    { get; } =
            Enum.GetValues<RetargetComponentPolicy>();

    public string? TargetBone
    {
        get => _targetBone;
        set
        {
            if (_isLocked &&
                !string.Equals(
                    _targetBone,
                    value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (SetProperty(ref _targetBone, value))
            {
                OnPropertyChanged(nameof(HasTarget));
                OnPropertyChanged(nameof(ReviewState));
            }
        }
    }

    public bool HasTarget => !string.IsNullOrWhiteSpace(TargetBone);

    public RetargetTransferPolicy TransferPolicy
    {
        get => _transferPolicy;
        set => SetProperty(ref _transferPolicy, value);
    }

    public RetargetComponentPolicy ComponentPolicy
    {
        get => _componentPolicy;
        set => SetProperty(ref _componentPolicy, value);
    }

    public bool RequiresExplicitReview =>
        !UsesAutomaticPolicyTuple ||
        !string.Equals(
            Status,
            BoneMappingMethod.DescriptorHash.ToString(),
            StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(
            Status,
            BoneMappingMethod.ExactName.ToString(),
            StringComparison.OrdinalIgnoreCase);

    private bool UsesAutomaticPolicyTuple =>
        MappingKind switch
        {
            RetargetMappingKind.Bone =>
                TransferPolicy ==
                    RetargetTransferPolicy.GlobalBindBasis &&
                ComponentPolicy ==
                    RetargetComponentPolicy.FullTransform,
            RetargetMappingKind.HelperOverride =>
                TransferPolicy ==
                    RetargetTransferPolicy.RestRelative &&
                ComponentPolicy ==
                    RetargetMapBuilder
                        .GetDefaultHelperComponentPolicy(
                            TargetBone ?? SourceBone),
            _ => false,
        };

    public string ReviewState =>
        !HasTarget
            ? "Unmapped"
            : !RequiresExplicitReview
                ? "Automatic"
                : IsReviewed
                    ? IsHelperOverride
                        ? "Helper reviewed"
                        : "Reviewed"
                    : IsHelperOverride
                        ? "Helper review required"
                        : "Review required";

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            bool normalized = HasTarget && value;
            SetProperty(ref _isLocked, normalized);
        }
    }

    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            bool normalized = HasTarget && value;
            if (SetProperty(ref _isReviewed, normalized))
            {
                OnPropertyChanged(nameof(ReviewState));
            }
        }
    }
}

public sealed class TargetBindReviewViewModel : ObservableObject
{
    private bool _isReviewed;

    public TargetBindReviewViewModel(
        int targetBoneIndex,
        string targetBone,
        BoneKind boneKind,
        bool isReviewed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(
            targetBoneIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(
            targetBone);
        TargetBoneIndex = targetBoneIndex;
        TargetBone = targetBone;
        BoneKind = boneKind;
        _isReviewed = isReviewed;
    }

    public int TargetBoneIndex { get; }

    public string TargetBone { get; }

    public BoneKind BoneKind { get; }

    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            if (SetProperty(ref _isReviewed, value))
            {
                OnPropertyChanged(nameof(ReviewState));
            }
        }
    }

    public string ReviewState =>
        IsReviewed
            ? "Bind accepted"
            : "Review required";
}

public sealed class MorphChannelViewModel : ObservableObject
{
    private float _weight;

    public MorphChannelViewModel(string name, float weight = 0.0f)
    {
        Name = name;
        _weight = weight;
    }

    public string Name { get; }

    public float Weight
    {
        get => _weight;
        set => SetProperty(
            ref _weight,
            float.IsFinite(value)
                ? value
                : 0.0f);
    }
}

public sealed class FacialMorphBindingReviewViewModel :
    ObservableObject
{
    private bool _enabled;
    private bool _isReviewed;
    private bool _isLocked;

    public FacialMorphBindingReviewViewModel(
        ProjectMorphBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        Binding = binding;
        _enabled = binding.Enabled;
        _isReviewed = binding.IsReviewed;
        _isLocked = binding.IsLocked && binding.IsReviewed;
    }

    private ProjectMorphBinding Binding { get; }

    public string SourceChannel => Binding.SourceChannel;

    public ProjectMorphSourceValueUnit SourceValueUnit =>
        Binding.SourceValueUnit;

    public string TargetMorph => Binding.TargetMorph;

    public string TargetDescriptor =>
        Binding.TargetDescriptorHash is uint descriptor
            ? $"0x{descriptor:X8}"
            : "No descriptor";

    public string Method => Binding.Method;

    public double Confidence => Binding.Confidence;

    public string ConfidenceLabel =>
        $"{Confidence:P0} {Method}";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(ReviewState));
            }
        }
    }

    public bool IsReviewed
    {
        get => _isReviewed;
        set
        {
            bool lockChanged = !value && _isLocked;
            if (_isReviewed == value && !lockChanged)
            {
                return;
            }

            _isReviewed = value;
            if (!value)
            {
                _isLocked = false;
            }

            OnPropertyChanged();
            if (lockChanged)
            {
                OnPropertyChanged(nameof(IsLocked));
            }

            OnPropertyChanged(nameof(ReviewState));
        }
    }

    public bool IsLocked
    {
        get => _isLocked;
        set
        {
            bool normalized = value && IsReviewed;
            if (SetProperty(ref _isLocked, normalized))
            {
                OnPropertyChanged(nameof(ReviewState));
            }
        }
    }

    public string ReviewState =>
        !Enabled
            ? "Disabled"
            : IsReviewed && IsLocked
                ? "Reviewed and locked"
                : IsReviewed
                    ? "Reviewed; lock required"
                    : "Review required";

    public ProjectMorphBinding BuildBinding() =>
        Binding with
        {
            Enabled = Enabled,
            IsReviewed = IsReviewed,
            IsLocked = IsLocked,
        };
}

public sealed record FidelityBadgeViewModel(
    string Label,
    string State,
    string Detail);

public sealed record DiagnosticEntryViewModel(
    DateTimeOffset Timestamp,
    string Severity,
    string Area,
    string Message,
    string? Detail);

public sealed class JobViewModel : ObservableObject, IDisposable
{
    private double _progress;
    private string _stage;
    private string _state;
    private CancellationTokenSource? _cancellationSource;
    private bool _disposed;

    public JobViewModel(
        string name,
        string stage,
        string state,
        bool isCancellable = true)
    {
        Name = name;
        _stage = stage;
        _state = state;
        _cancellationSource = isCancellable
            ? new CancellationTokenSource()
            : null;
        CancelCommand = new RelayCommand(Cancel, () => IsCancellable);
    }

    public string Name { get; }

    public string Stage
    {
        get => _stage;
        set => SetProperty(ref _stage, value);
    }

    public bool IsCancellable =>
        _cancellationSource is { IsCancellationRequested: false };

    public CancellationToken CancellationToken =>
        _cancellationSource?.Token ??
        System.Threading.CancellationToken.None;

    public IRelayCommand CancelCommand { get; }

    public double Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, Math.Clamp(value, 0.0, 100.0));
    }

    public string State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public void Complete(string state)
    {
        State = state;
        CancellationTokenSource? source =
            Interlocked.Exchange(ref _cancellationSource, null);
        source?.Dispose();
        OnPropertyChanged(nameof(IsCancellable));
        CancelCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource? source =
            Interlocked.Exchange(ref _cancellationSource, null);
        source?.Cancel();
        source?.Dispose();
        OnPropertyChanged(nameof(IsCancellable));
        CancelCommand.NotifyCanExecuteChanged();
        GC.SuppressFinalize(this);
    }

    public void Cancel()
    {
        _cancellationSource?.Cancel();
        State = "Cancellation requested";
        OnPropertyChanged(nameof(IsCancellable));
        CancelCommand.NotifyCanExecuteChanged();
    }
}

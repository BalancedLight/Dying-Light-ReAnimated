using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Core.Domain;

namespace ReAnimated.App.ViewModels;

public sealed class BoneEditLayerItemViewModel : ObservableObject
{
    private bool _layerEnabled;
    private double _weight;
    private BoneEditBlendMode _blendMode;
    private ImmutableDictionary<int, double> _boneMask;
    private ImmutableDictionary<int, BoneEditInterpolation>
        _trackInterpolations;
    private int? _selectedBoneIndex;
    private string _selectedBoneLabel = "No bone selected";
    private bool _selectedBoneHasTrack;
    private BoneEditInterpolation _selectedBoneInterpolation =
        BoneEditInterpolation.Linear;
    private bool _hasSelectedBoneMask;
    private double _selectedBoneMaskWeight = 1.0;

    public BoneEditLayerItemViewModel(BoneEditLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        Id = layer.Id;
        Name = layer.Name;
        _blendMode = layer.BlendMode;
        Scope = layer.Scope;
        _layerEnabled = layer.Enabled;
        _weight = layer.Weight;
        _boneMask = layer.BoneMask;
        _trackInterpolations = layer.Tracks.ToImmutableDictionary(
            static track => track.BoneIndex,
            static track => track.Interpolation);
        ApplyCommand = new RelayCommand(
            () => ApplyRequested?.Invoke(this, EventArgs.Empty));
    }

    public Guid Id { get; }

    public string Name { get; }

    public IReadOnlyList<BoneEditBlendMode> BlendModes { get; } =
        Enum.GetValues<BoneEditBlendMode>();

    public BoneEditBlendMode BlendMode
    {
        get => _blendMode;
        set
        {
            if (SetProperty(ref _blendMode, value))
            {
                OnPropertyChanged(nameof(ModeLabel));
            }
        }
    }

    public BoneEditLayerScope Scope { get; }

    public IRelayCommand ApplyCommand { get; }

    public event EventHandler? ApplyRequested;

    public bool LayerEnabled
    {
        get => _layerEnabled;
        set => SetProperty(ref _layerEnabled, value);
    }

    public double Weight
    {
        get => _weight;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            SetProperty(
                ref _weight,
                Math.Clamp(value, 0.0, 1.0));
        }
    }

    public string ModeLabel => $"{BlendMode} · {Scope}";

    public IReadOnlyList<BoneEditInterpolation> InterpolationModes { get; } =
        Enum.GetValues<BoneEditInterpolation>();

    public bool CanEditSelectedBoneInterpolation =>
        _selectedBoneHasTrack;

    public BoneEditInterpolation SelectedBoneInterpolation
    {
        get => _selectedBoneInterpolation;
        set
        {
            if (!CanEditSelectedBoneInterpolation ||
                !Enum.IsDefined(value))
            {
                return;
            }

            if (SetProperty(
                    ref _selectedBoneInterpolation,
                    value) &&
                _selectedBoneIndex is { } boneIndex)
            {
                _trackInterpolations =
                    _trackInterpolations.SetItem(
                        boneIndex,
                        value);
            }
        }
    }

    public bool CanEditSelectedBoneMask =>
        _selectedBoneIndex.HasValue;

    public string SelectedBoneLabel => _selectedBoneLabel;

    public bool HasSelectedBoneMask
    {
        get => _hasSelectedBoneMask;
        set
        {
            if (!CanEditSelectedBoneMask)
            {
                return;
            }

            SetProperty(
                ref _hasSelectedBoneMask,
                value);
        }
    }

    public double SelectedBoneMaskWeight
    {
        get => _selectedBoneMaskWeight;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            SetProperty(
                ref _selectedBoneMaskWeight,
                Math.Clamp(value, 0.0, 1.0));
        }
    }

    public int ExplicitBoneMaskCount => _boneMask.Count;

    public void SetSelectedBone(
        int? boneIndex,
        string? label)
    {
        if (boneIndex < 0)
        {
            boneIndex = null;
        }

        _selectedBoneIndex = boneIndex;
        _selectedBoneLabel = boneIndex.HasValue
            ? label ?? $"Bone {boneIndex.Value}"
            : "No bone selected";
        BoneEditInterpolation interpolation =
            BoneEditInterpolation.Linear;
        _selectedBoneHasTrack =
            boneIndex.HasValue &&
            _trackInterpolations.TryGetValue(
                boneIndex.Value,
                out interpolation);
        _selectedBoneInterpolation =
            _selectedBoneHasTrack
                ? interpolation
                : BoneEditInterpolation.Linear;
        double maskWeight = 1.0;
        _hasSelectedBoneMask =
            boneIndex.HasValue &&
            _boneMask.TryGetValue(
                boneIndex.Value,
                out maskWeight);
        _selectedBoneMaskWeight =
            _hasSelectedBoneMask
                ? maskWeight
                : 1.0;
        OnPropertyChanged(nameof(CanEditSelectedBoneMask));
        OnPropertyChanged(
            nameof(CanEditSelectedBoneInterpolation));
        OnPropertyChanged(nameof(SelectedBoneInterpolation));
        OnPropertyChanged(nameof(SelectedBoneLabel));
        OnPropertyChanged(nameof(HasSelectedBoneMask));
        OnPropertyChanged(nameof(SelectedBoneMaskWeight));
    }

    public ImmutableDictionary<int, double> BuildBoneMask()
    {
        if (_selectedBoneIndex is not { } boneIndex)
        {
            return _boneMask;
        }

        return HasSelectedBoneMask
            ? _boneMask.SetItem(
                boneIndex,
                SelectedBoneMaskWeight)
            : _boneMask.Remove(boneIndex);
    }

    public ImmutableArray<BoneEditTrack> BuildTracks(
        ImmutableArray<BoneEditTrack> sourceTracks)
    {
        if (sourceTracks.IsDefault)
        {
            throw new ArgumentException(
                "Bone edit tracks must be initialized.",
                nameof(sourceTracks));
        }

        return sourceTracks
            .Select(track =>
            {
                BoneEditInterpolation interpolation =
                    _trackInterpolations.TryGetValue(
                        track.BoneIndex,
                        out BoneEditInterpolation staged)
                        ? staged
                        : track.Interpolation;
                return interpolation == track.Interpolation
                    ? track
                    : new BoneEditTrack(
                        track.BoneIndex,
                        track.Keyframes,
                        interpolation);
            })
            .ToImmutableArray();
    }
}

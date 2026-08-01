using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Numerics;
using CommunityToolkit.Mvvm.ComponentModel;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public sealed record AttachmentBoneOptionViewModel(
    int Index,
    string Name,
    BoneKind Kind,
    string? SemanticRole)
{
    public string DisplayName =>
        string.IsNullOrWhiteSpace(SemanticRole)
            ? $"{Name} [{Kind}]"
            : $"{Name} [{Kind}; {SemanticRole}]";
}

public sealed class AttachmentItemViewModel : ObservableObject
{
    private string _status;

    public AttachmentItemViewModel(
        AttachmentBinding binding,
        string assetLabel,
        string parentLabel,
        string status)
    {
        Binding = binding;
        AssetLabel = assetLabel;
        ParentLabel = parentLabel;
        _status = status;
    }

    public AttachmentBinding Binding { get; }

    public Guid Id => Binding.Id;

    public string Name => Binding.Name;

    public string AssetLabel { get; }

    public string ParentLabel { get; }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

/// <summary>
/// UI-only state for rigid prop/weapon attachment authoring. Immutable project
/// edits remain owned by <see cref="MainWindowViewModel"/>.
/// </summary>
public sealed class AttachmentEditorViewModel : ObservableObject
{
    public const int MaximumVisibleCatalogAssets = 5_000;

    private readonly List<AssetItemViewModel> _allCatalogAssets = [];
    private string _assetSearch = string.Empty;
    private AssetItemViewModel? _selectedCatalogAsset;
    private AttachmentBoneOptionViewModel? _selectedParentBone;
    private AttachmentItemViewModel? _selectedAttachment;
    private string _name = "Attachment";
    private double _positionX;
    private double _positionY;
    private double _positionZ;
    private double _rotationX;
    private double _rotationY;
    private double _rotationZ;
    private double _scaleX = 1.0;
    private double _scaleY = 1.0;
    private double _scaleZ = 1.0;
    private bool _isPreviewOnly;

    public ObservableCollection<AssetItemViewModel>
        VisibleCatalogAssets
    { get; } = [];

    public ObservableCollection<AttachmentBoneOptionViewModel>
        ParentBones
    { get; } = [];

    public ObservableCollection<AttachmentItemViewModel>
        Attachments
    { get; } = [];

    public string AssetSearch
    {
        get => _assetSearch;
        set
        {
            if (SetProperty(ref _assetSearch, value ?? string.Empty))
            {
                RebuildVisibleCatalogAssets();
            }
        }
    }

    public AssetItemViewModel? SelectedCatalogAsset
    {
        get => _selectedCatalogAsset;
        set
        {
            if (SetProperty(ref _selectedCatalogAsset, value) &&
                SelectedAttachment is null &&
                value is not null)
            {
                Name = value.Name;
            }
        }
    }

    public AttachmentBoneOptionViewModel? SelectedParentBone
    {
        get => _selectedParentBone;
        set => SetProperty(ref _selectedParentBone, value);
    }

    public AttachmentItemViewModel? SelectedAttachment
    {
        get => _selectedAttachment;
        set
        {
            if (SetProperty(ref _selectedAttachment, value) &&
                value is not null)
            {
                LoadBinding(value.Binding);
            }
        }
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
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

    public bool IsPreviewOnly
    {
        get => _isPreviewOnly;
        set => SetProperty(ref _isPreviewOnly, value);
    }

    public void ReplaceCatalogAssets(
        IEnumerable<AssetItemViewModel> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);
        string? selectedId = SelectedCatalogAsset?.Id;
        _allCatalogAssets.Clear();
        _allCatalogAssets.AddRange(
            assets.Where(static asset =>
                asset.Kind == AssetKind.Mesh &&
                asset.RetailAsset is not null));
        RebuildVisibleCatalogAssets();
        SelectedCatalogAsset = selectedId is null
            ? null
            : _allCatalogAssets.FirstOrDefault(asset =>
                string.Equals(
                    asset.Id,
                    selectedId,
                    StringComparison.Ordinal));
        OnPropertyChanged(nameof(CatalogAssetCount));
    }

    public void ReplaceParentBones(RigDefinition? rig)
    {
        string? selectedName = SelectedParentBone?.Name;
        ParentBones.Clear();
        if (rig is null)
        {
            SelectedParentBone = null;
            return;
        }

        foreach (BoneDefinition bone in rig.Bones)
        {
            ParentBones.Add(
                new AttachmentBoneOptionViewModel(
                    bone.Index,
                    bone.Name,
                    bone.Kind,
                    bone.SemanticRole));
        }

        SelectedParentBone = selectedName is null
            ? ChooseDefaultParent(ParentBones)
            : ParentBones.FirstOrDefault(bone =>
                string.Equals(
                    bone.Name,
                    selectedName,
                    StringComparison.OrdinalIgnoreCase))
              ?? ChooseDefaultParent(ParentBones);
    }

    public void ReplaceBindings(
        IEnumerable<AttachmentBinding> bindings,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        RigDefinition? targetRig,
        IReadOnlyDictionary<Guid, string>? statuses = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(assets);
        Guid? selectedId = SelectedAttachment?.Id;
        Attachments.Clear();
        foreach (AttachmentBinding binding in bindings)
        {
            string assetLabel = assets.TryGetValue(
                binding.AssetId,
                out ProjectAssetReference? asset)
                    ? asset.RetailIdentity?.ResourceName
                      ?? asset.ResourceId
                      ?? asset.RelativePath
                    : $"Missing project asset {binding.AssetId}";
            string parentLabel = ResolveParentLabel(
                binding,
                targetRig);
            string status = statuses is not null &&
                            statuses.TryGetValue(
                                binding.Id,
                                out string? resolvedStatus)
                ? resolvedStatus
                : GetInitialStatus(binding, assets, targetRig);
            Attachments.Add(
                new AttachmentItemViewModel(
                    binding,
                    assetLabel,
                    parentLabel,
                    status));
        }

        SelectedAttachment = selectedId is { } id
            ? Attachments.FirstOrDefault(item => item.Id == id)
            : null;
    }

    public void SetBindingStatus(Guid bindingId, string status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        AttachmentItemViewModel? item = Attachments.FirstOrDefault(
            candidate => candidate.Id == bindingId);
        if (item is not null)
        {
            item.Status = status;
        }
    }

    public TransformTRS CreateLocalOffset()
    {
        const double degreesToRadians = Math.PI / 180.0;
        var rotation = Quaternion.CreateFromYawPitchRoll(
            checked((float)(RotationY * degreesToRadians)),
            checked((float)(RotationX * degreesToRadians)),
            checked((float)(RotationZ * degreesToRadians)));
        return new TransformTRS(
            new Vector3D(PositionX, PositionY, PositionZ),
            new QuaternionD(
                rotation.X,
                rotation.Y,
                rotation.Z,
                rotation.W),
            new Vector3D(ScaleX, ScaleY, ScaleZ));
    }

    public void ResetOffset()
    {
        PositionX = 0;
        PositionY = 0;
        PositionZ = 0;
        RotationX = 0;
        RotationY = 0;
        RotationZ = 0;
        ScaleX = 1;
        ScaleY = 1;
        ScaleZ = 1;
    }

    public int CatalogAssetCount => _allCatalogAssets.Count;

    private void RebuildVisibleCatalogAssets()
    {
        string search = AssetSearch.Trim();
        IEnumerable<AssetItemViewModel> matches =
            _allCatalogAssets;
        if (search.Length > 0)
        {
            matches = matches.Where(asset =>
                asset.Name.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase) ||
                asset.LogicalPath.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase) ||
                asset.Provider.Contains(
                    search,
                    StringComparison.OrdinalIgnoreCase));
        }

        VisibleCatalogAssets.Clear();
        foreach (AssetItemViewModel asset in matches
                     .Take(MaximumVisibleCatalogAssets))
        {
            VisibleCatalogAssets.Add(asset);
        }
    }

    private void LoadBinding(AttachmentBinding binding)
    {
        Name = binding.Name;
        AttachmentBoneOptionViewModel? parent =
            ParentBones.FirstOrDefault(bone =>
                bone.Index == binding.ParentBoneIndex &&
                (string.IsNullOrWhiteSpace(
                     binding.ParentBoneName) ||
                 string.Equals(
                     bone.Name,
                     binding.ParentBoneName,
                     StringComparison.OrdinalIgnoreCase)));
        SelectedParentBone = parent;
        TransformTRS transform = binding.LocalOffset;
        PositionX = transform.Translation.X;
        PositionY = transform.Translation.Y;
        PositionZ = transform.Translation.Z;
        SetEulerDegrees(transform.Rotation);
        ScaleX = transform.Scale.X;
        ScaleY = transform.Scale.Y;
        ScaleZ = transform.Scale.Z;
        IsPreviewOnly =
            binding.Scope == AttachmentScope.PreviewOnly;
    }

    private void SetEulerDegrees(QuaternionD rotation)
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
        RotationX = pitch * radiansToDegrees;
        RotationY = yaw * radiansToDegrees;
        RotationZ = roll * radiansToDegrees;
    }

    private static AttachmentBoneOptionViewModel?
        ChooseDefaultParent(
            IEnumerable<AttachmentBoneOptionViewModel> bones) =>
        bones.FirstOrDefault(static bone =>
            bone.Kind == BoneKind.Prop &&
            ContainsRightHand(bone))
        ?? bones.FirstOrDefault(static bone =>
            bone.Kind is BoneKind.Prop or BoneKind.Helper)
        ?? bones.FirstOrDefault(static bone =>
            ContainsRightHand(bone))
        ?? bones.FirstOrDefault();

    private static bool ContainsRightHand(
        AttachmentBoneOptionViewModel bone) =>
        bone.Name.Contains(
            "right",
            StringComparison.OrdinalIgnoreCase) &&
        bone.Name.Contains(
            "hand",
            StringComparison.OrdinalIgnoreCase)
        || bone.SemanticRole?.Contains(
            "right_hand",
            StringComparison.OrdinalIgnoreCase) == true;

    private static string ResolveParentLabel(
        AttachmentBinding binding,
        RigDefinition? targetRig)
    {
        if (targetRig is null ||
            binding.ParentBoneIndex >= targetRig.BoneCount)
        {
            return binding.ParentBoneName
                ?? $"Bone {binding.ParentBoneIndex}";
        }

        BoneDefinition bone =
            targetRig.Bones[binding.ParentBoneIndex];
        return $"{bone.Name} [{bone.Kind}]";
    }

    private static string GetInitialStatus(
        AttachmentBinding binding,
        IReadOnlyDictionary<Guid, ProjectAssetReference> assets,
        RigDefinition? targetRig)
    {
        if (!assets.ContainsKey(binding.AssetId))
        {
            return "Error: project asset is missing";
        }

        if (targetRig is null)
        {
            return "Waiting for target rig";
        }

        if (binding.ParentBoneIndex >= targetRig.BoneCount)
        {
            return "Error: parent bone index is missing";
        }

        string actual =
            targetRig.Bones[binding.ParentBoneIndex].Name;
        if (!string.IsNullOrWhiteSpace(
                binding.ParentBoneName) &&
            !string.Equals(
                binding.ParentBoneName,
                actual,
                StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: parent is '{actual}', expected '{binding.ParentBoneName}'";
        }

        return "Waiting for retail asset decode";
    }
}

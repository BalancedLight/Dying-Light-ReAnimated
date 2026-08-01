namespace ReAnimated.Core.Domain;

/// <summary>
/// Identifies the user-owned game resource from which a runtime rig was decoded.
/// The fingerprint carries identity only; it never embeds retail payload bytes.
/// </summary>
public sealed record SourceAssetFingerprint
{
    public SourceAssetFingerprint(
        string relativeResourcePath,
        string contentSha256,
        string? resourceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeResourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentSha256);
        if (Path.IsPathRooted(relativeResourcePath) ||
            relativeResourcePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                .Any(static segment => segment == ".."))
        {
            throw new ArgumentException(
                "Source resource paths must remain relative to the user-owned game installation.",
                nameof(relativeResourcePath));
        }

        if (contentSha256.Length != 64 ||
            contentSha256.Any(static character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException(
                "The source fingerprint must be a 64-character SHA-256 value.",
                nameof(contentSha256));
        }

        RelativeResourcePath = relativeResourcePath;
        ContentSha256 = contentSha256.ToUpperInvariant();
        ResourceId = resourceId;
    }

    public string RelativeResourcePath { get; }

    public string ContentSha256 { get; }

    public string? ResourceId { get; }
}

public sealed record MorphChannelDefinition
{
    public MorphChannelDefinition(
        int index,
        string name,
        uint? descriptorHash = null,
        string? semanticRole = null,
        double minimumValue = 0.0,
        double maximumValue = 1.0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!double.IsFinite(minimumValue) ||
            !double.IsFinite(maximumValue) ||
            maximumValue < minimumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumValue),
                "Morph bounds must be finite and ordered.");
        }

        Index = index;
        Name = name;
        DescriptorHash = descriptorHash;
        SemanticRole = semanticRole;
        MinimumValue = minimumValue;
        MaximumValue = maximumValue;
    }

    public int Index { get; }

    public string Name { get; }

    public uint? DescriptorHash { get; }

    public string? SemanticRole { get; }

    public double MinimumValue { get; }

    public double MaximumValue { get; }
}

/// <summary>
/// A named, direct root/joint/end chain packaged with a retail-derived rig.
/// </summary>
public sealed record TwoBoneIkChainDefinition
{
    public TwoBoneIkChainDefinition(
        string name,
        int rootBoneIndex,
        int jointBoneIndex,
        int endBoneIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(rootBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(jointBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(endBoneIndex);
        if (rootBoneIndex == jointBoneIndex ||
            rootBoneIndex == endBoneIndex ||
            jointBoneIndex == endBoneIndex)
        {
            throw new ArgumentException("An IK chain must contain three distinct bones.");
        }

        Name = name;
        RootBoneIndex = rootBoneIndex;
        JointBoneIndex = jointBoneIndex;
        EndBoneIndex = endBoneIndex;
    }

    public string Name { get; }

    public int RootBoneIndex { get; }

    public int JointBoneIndex { get; }

    public int EndBoneIndex { get; }
}

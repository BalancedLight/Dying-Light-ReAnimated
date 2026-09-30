using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>A named frame in its owning prop's model space, not a character node.</summary>
public sealed record AttachmentGripFrame
{
    public int NodeIndex { get; init; }
    public string NodeName { get; init; } = string.Empty;
    public ImmutableArray<double> Elements { get; init; } = [];
    [JsonIgnore] public TransformMatrix Matrix => new(Elements[0],Elements[1],Elements[2],Elements[3],
        Elements[4],Elements[5],Elements[6],Elements[7],Elements[8],Elements[9],Elements[10],Elements[11],Elements[12],Elements[13],Elements[14],Elements[15]);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(NodeIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(NodeName);
        if(NodeName.Length>AttachmentBinding.MaximumNameLength || Elements.IsDefault || Elements.Length!=16 || Elements.Any(static x=>!double.IsFinite(x)))
            throw new ArgumentException("Grip frames require a bounded name and 16 finite matrix values.");
        var m=Matrix;
        if(Math.Abs(m.M41)>1e-12||Math.Abs(m.M42)>1e-12||Math.Abs(m.M43)>1e-12||Math.Abs(m.M44-1)>1e-12||Math.Abs(m.LinearDeterminant)<=1e-12)
            throw new ArgumentException("Grip frames must be nonsingular affine transforms.");
    }
    public static AttachmentGripFrame FromMatrix(int index,string name,TransformMatrix matrix)
    {
        var result=new AttachmentGripFrame {NodeIndex=index,NodeName=name,Elements=[matrix.M11,matrix.M12,matrix.M13,matrix.M14,
            matrix.M21,matrix.M22,matrix.M23,matrix.M24,matrix.M31,matrix.M32,matrix.M33,matrix.M34,matrix.M41,matrix.M42,matrix.M43,matrix.M44]};
        result.Validate();return result;
    }
}

public sealed record AttachmentSecondaryGrip
{
    public int CharacterBoneIndex { get; init; }
    public string CharacterBoneName { get; init; } = string.Empty;
    public TransformTRS CharacterLocalOffset { get; init; } = TransformTRS.Identity;
    public AttachmentGripFrame PropFrame { get; init; } = new();
    public AttachmentSecondaryIk? Ik { get; init; }
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(CharacterBoneIndex);
        ArgumentException.ThrowIfNullOrWhiteSpace(CharacterBoneName);
        if(CharacterBoneName.Length>AttachmentBinding.MaximumNameLength||!CharacterLocalOffset.IsFinite||
            Math.Abs(CharacterLocalOffset.ToMatrix().LinearDeterminant)<=1e-12)throw new ArgumentException("Secondary hand frame is invalid.");
        ArgumentNullException.ThrowIfNull(PropFrame);PropFrame.Validate();Ik?.Validate();
    }
}

/// <summary>Calibrates a prop to a character frame; native equipment configuration is not implied.</summary>
public sealed record AttachmentGripCalibration
{
    public Guid PropAssetId { get; init; }
    public string PropContentSha256 { get; init; } = string.Empty;
    public AttachmentGripFrame PrimaryPropFrame { get; init; } = new();
    public AttachmentSecondaryGrip? Secondary { get; init; }
    public void Validate(Guid expectedAssetId)
    {
        if(PropAssetId==Guid.Empty||PropAssetId!=expectedAssetId)throw new ArgumentException("Grip calibration belongs to a different prop asset.");
        if(PropContentSha256 is null||PropContentSha256.Length!=64||PropContentSha256.Any(static c=>!char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Grip calibration requires the exact prop content SHA-256.");
        ArgumentNullException.ThrowIfNull(PrimaryPropFrame);PrimaryPropFrame.Validate();Secondary?.Validate();
    }
}

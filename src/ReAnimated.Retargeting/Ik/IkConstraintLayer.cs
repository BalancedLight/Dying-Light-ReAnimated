using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Ik;

public sealed record IkConstraintKeyframe
{
    public IkConstraintKeyframe(
        double frame,
        Vector3D effector,
        Vector3D pole,
        QuaternionD? endOrientation = null)
    {
        if (!double.IsFinite(frame) || frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        if (!effector.IsFinite ||
            !pole.IsFinite ||
            (endOrientation.HasValue &&
             !endOrientation.Value.IsFinite))
        {
            throw new ArgumentException(
                "IK keyframe values must be finite.");
        }

        Frame = frame;
        Effector = effector;
        Pole = pole;
        EndOrientation = endOrientation?.Normalized();
    }

    public double Frame { get; }

    public Vector3D Effector { get; }

    public Vector3D Pole { get; }

    public QuaternionD? EndOrientation { get; }
}

/// <summary>
/// A keyed, non-destructive two-bone IK layer suitable for hand and foot
/// authoring. BakeToEditLayer is intent metadata; baking produces ordinary
/// BoneEditLayer keys in the editor transaction that requests it.
/// </summary>
public sealed record IkConstraintLayer
{
    public IkConstraintLayer(
        Guid id,
        string name,
        int rootBoneIndex,
        int jointBoneIndex,
        int endBoneIndex,
        double weight,
        IEnumerable<IkConstraintKeyframe> keyframes,
        bool enabled = true,
        bool bakeToEditLayer = false,
        IkConstraintScope scope = IkConstraintScope.AuthoredExportable)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "An IK layer requires a stable identifier.",
                nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(rootBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(jointBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(endBoneIndex);
        if (rootBoneIndex == jointBoneIndex ||
            rootBoneIndex == endBoneIndex ||
            jointBoneIndex == endBoneIndex)
        {
            throw new ArgumentException(
                "An IK layer requires three distinct bones.");
        }

        if (!double.IsFinite(weight) || weight is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        ArgumentNullException.ThrowIfNull(keyframes);
        ImmutableArray<IkConstraintKeyframe> keys =
            keyframes.ToImmutableArray();
        if (keys.IsEmpty)
        {
            throw new ArgumentException(
                "An IK layer requires at least one keyframe.",
                nameof(keyframes));
        }

        for (var index = 1; index < keys.Length; index++)
        {
            if (keys[index].Frame <= keys[index - 1].Frame)
            {
                throw new ArgumentException(
                    "IK keyframes must be strictly increasing.",
                    nameof(keyframes));
            }
        }

        bool hasOrientation = keys[0].EndOrientation.HasValue;
        if (keys.Any(key =>
                key.EndOrientation.HasValue != hasOrientation))
        {
            throw new ArgumentException(
                "End-orientation keys must be present on every key or none.",
                nameof(keyframes));
        }

        Id = id;
        Name = name;
        RootBoneIndex = rootBoneIndex;
        JointBoneIndex = jointBoneIndex;
        EndBoneIndex = endBoneIndex;
        Weight = weight;
        Keyframes = keys;
        Enabled = enabled;
        BakeToEditLayer = bakeToEditLayer;
        Scope = scope;
    }

    public Guid Id { get; }

    public string Name { get; }

    public int RootBoneIndex { get; }

    public int JointBoneIndex { get; }

    public int EndBoneIndex { get; }

    public double Weight { get; }

    public ImmutableArray<IkConstraintKeyframe> Keyframes { get; }

    public bool Enabled { get; }

    public bool BakeToEditLayer { get; }

    public IkConstraintScope Scope { get; }

    public TwoBoneIkConstraint Sample(double frame)
    {
        if (!double.IsFinite(frame))
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        int upperIndex = FindUpperKeyframe(frame);
        IkConstraintKeyframe sample;
        if (upperIndex <= 0)
        {
            sample = Keyframes[0];
        }
        else if (upperIndex >= Keyframes.Length)
        {
            sample = Keyframes[^1];
        }
        else
        {
            IkConstraintKeyframe lower = Keyframes[upperIndex - 1];
            IkConstraintKeyframe upper = Keyframes[upperIndex];
            double amount =
                (frame - lower.Frame) /
                (upper.Frame - lower.Frame);
            sample = new IkConstraintKeyframe(
                frame,
                Vector3D.Lerp(lower.Effector, upper.Effector, amount),
                Vector3D.Lerp(lower.Pole, upper.Pole, amount),
                lower.EndOrientation.HasValue
                    ? QuaternionD.Slerp(
                        lower.EndOrientation.Value,
                        upper.EndOrientation!.Value,
                        amount)
                    : null);
        }

        return new TwoBoneIkConstraint(
            RootBoneIndex,
            JointBoneIndex,
            EndBoneIndex,
            sample.Effector,
            sample.Pole,
            Enabled ? Weight : 0,
            Scope,
            sample.EndOrientation);
    }

    private int FindUpperKeyframe(double frame)
    {
        int low = 0;
        int high = Keyframes.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (Keyframes[middle].Frame <= frame)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}

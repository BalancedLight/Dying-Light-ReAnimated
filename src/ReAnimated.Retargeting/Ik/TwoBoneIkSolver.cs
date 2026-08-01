using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Ik;

public enum IkConstraintScope
{
    AuthoredExportable,
    PreviewOnly,
}

public sealed record TwoBoneIkConstraint
{
    public TwoBoneIkConstraint(
        int rootBoneIndex,
        int jointBoneIndex,
        int endBoneIndex,
        Vector3D target,
        Vector3D pole,
        double weight = 1.0,
        IkConstraintScope scope = IkConstraintScope.AuthoredExportable,
        QuaternionD? endOrientation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rootBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(jointBoneIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(endBoneIndex);
        if (rootBoneIndex == jointBoneIndex ||
            rootBoneIndex == endBoneIndex ||
            jointBoneIndex == endBoneIndex)
        {
            throw new ArgumentException("A two-bone IK chain requires three distinct bones.");
        }

        if (!target.IsFinite || !pole.IsFinite)
        {
            throw new ArgumentException("IK target and pole positions must be finite.");
        }

        if (endOrientation.HasValue &&
            !endOrientation.Value.IsFinite)
        {
            throw new ArgumentException(
                "IK end orientation must be finite.",
                nameof(endOrientation));
        }

        if (!double.IsFinite(weight) || weight < 0.0 || weight > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(weight));
        }

        RootBoneIndex = rootBoneIndex;
        JointBoneIndex = jointBoneIndex;
        EndBoneIndex = endBoneIndex;
        Target = target;
        Pole = pole;
        Weight = weight;
        Scope = scope;
        EndOrientation = endOrientation?.Normalized();
    }

    public int RootBoneIndex { get; }

    public int JointBoneIndex { get; }

    public int EndBoneIndex { get; }

    public Vector3D Target { get; }

    public Vector3D Pole { get; }

    public double Weight { get; }

    public IkConstraintScope Scope { get; }

    public QuaternionD? EndOrientation { get; }
}

public readonly record struct TwoBoneIkSolution(
    Vector3D RootPosition,
    Vector3D JointPosition,
    Vector3D EndPosition,
    double UpperLength,
    double LowerLength,
    bool WasClamped);

/// <summary>
/// Stable analytic two-bone IK position solver with a pole-vector bend plane.
/// </summary>
public static class TwoBoneIkSolver
{
    public static TwoBoneIkSolution Solve(
        Vector3D root,
        Vector3D joint,
        Vector3D end,
        Vector3D target,
        Vector3D pole,
        double epsilon = 1e-8)
    {
        if (!root.IsFinite ||
            !joint.IsFinite ||
            !end.IsFinite ||
            !target.IsFinite ||
            !pole.IsFinite)
        {
            throw new ArgumentException("IK positions must be finite.");
        }

        double upperLength = Vector3D.Distance(root, joint);
        double lowerLength = Vector3D.Distance(joint, end);
        if (upperLength <= epsilon || lowerLength <= epsilon)
        {
            throw new InvalidOperationException("A two-bone IK segment has zero length.");
        }

        Vector3D toTarget = target - root;
        double requestedDistance = toTarget.Length;
        Vector3D direction;
        if (!toTarget.TryNormalize(out direction, epsilon))
        {
            direction = (end - root).TryNormalize(out Vector3D currentDirection, epsilon)
                ? currentDirection
                : Vector3D.UnitZ;
        }

        double minimumReach = Math.Abs(upperLength - lowerLength) + epsilon;
        double maximumReach = Math.Max(minimumReach, upperLength + lowerLength - epsilon);
        double solvedDistance = Math.Clamp(requestedDistance, minimumReach, maximumReach);
        bool wasClamped = Math.Abs(solvedDistance - requestedDistance) > epsilon;

        Vector3D poleOffset = pole - root;
        Vector3D bendDirection =
            poleOffset - (direction * Vector3D.Dot(poleOffset, direction));
        if (!bendDirection.TryNormalize(out bendDirection, epsilon))
        {
            Vector3D currentUpper = joint - root;
            bendDirection =
                currentUpper - (direction * Vector3D.Dot(currentUpper, direction));
        }

        if (!bendDirection.TryNormalize(out bendDirection, epsilon))
        {
            Vector3D reference =
                Math.Abs(Vector3D.Dot(direction, Vector3D.UnitY)) < 0.9
                    ? Vector3D.UnitY
                    : Vector3D.UnitX;
            bendDirection = Vector3D.Cross(
                Vector3D.Cross(direction, reference),
                direction).Normalized(epsilon);
        }

        double along =
            ((upperLength * upperLength) +
             (solvedDistance * solvedDistance) -
             (lowerLength * lowerLength)) /
            (2.0 * solvedDistance);
        double heightSquared = Math.Max(
            0.0,
            (upperLength * upperLength) - (along * along));
        double height = Math.Sqrt(heightSquared);

        Vector3D solvedJoint =
            root + (direction * along) + (bendDirection * height);
        Vector3D solvedEnd = root + (direction * solvedDistance);

        return new(
            root,
            solvedJoint,
            solvedEnd,
            upperLength,
            lowerLength,
            wasClamped);
    }
}

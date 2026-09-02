using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// The source rig posed so that its mapped joints land on a target skeleton,
/// together with the rigid per-bone transform that carries the mesh with it.
/// </summary>
public sealed record RigRestPoseTransferResult
{
    /// <summary>Posed global transform for every source bone.</summary>
    public required ImmutableArray<TransformMatrix> PosedGlobals { get; init; }

    /// <summary>
    /// <c>posed · bind⁻¹</c> per source bone. Linear-blending vertices through
    /// this moves the mesh from its authored rest pose into the target's.
    /// </summary>
    public required ImmutableArray<TransformMatrix> SkinningTransforms { get; init; }

    /// <summary>
    /// Largest distance any mapped joint ended up from the target position it
    /// was asked to reach. Zero by construction unless a chain could not be
    /// oriented, so a non-zero value is a real defect worth reporting.
    /// </summary>
    public required double MaximumJointResidual { get; init; }
}

/// <summary>
/// Poses an imported rig into a target skeleton's rest pose.
/// </summary>
/// <remarks>
/// <para>
/// This is the stage that makes a converted model animate correctly. Dying
/// Light couples a bone's inverse bind to its place in the hierarchy - on the
/// retail player every serialized reference matrix is the inverse of its global
/// bind - and stock clips key per-bone translation. So the bind pose has to be
/// DL1's rest pose; it cannot be the model's own pose with DL1 names attached.
/// Rather than bending the skeleton to the mesh, the mesh is carried into the
/// skeleton's pose.
/// </para>
/// <para>
/// Each bone's rotation is solved from <em>all</em> of the mapped joints
/// hanging off it at once, as an orthogonal Procrustes fit. Aiming at a single
/// child instead is not good enough: the pelvis carries both thighs and the
/// spine, and picking one of them makes the other two inherit its correction -
/// which is how a ten-degree leg error turns into forty centimetres of moved
/// geometry. Solving them together also recovers twist about the bone axis,
/// which a single aim direction leaves free and which decides where a hand's
/// fingers end up.
/// </para>
/// <para>
/// Every emitted transform is a rotation and a translation, never a shear or a
/// scale, so blending them cannot distort geometry beyond ordinary linear-blend
/// skinning around a joint.
/// </para>
/// </remarks>
public static class RigRestPoseTransfer
{
    private const double DirectionEpsilon = 1e-9;

    /// <summary>
    /// Weight of the bias toward the parent's rotation, relative to the fitted
    /// data. It settles the twist a single correspondence leaves free without
    /// materially bending a well-constrained joint.
    /// </summary>
    private const double ParentBias = 1e-2;

    /// <summary>
    /// Poses <paramref name="sourceRig"/> so that each bone carrying a target
    /// position reaches it.
    /// </summary>
    /// <param name="targetPositions">
    /// Per source bone, the world position it must reach, or
    /// <see langword="null"/> where the bone is unmapped and should simply
    /// follow its parent.
    /// </param>
    public static RigRestPoseTransferResult Solve(
        RigDefinition sourceRig,
        ImmutableArray<TransformMatrix> sourceBindGlobals,
        ImmutableArray<Vector3D?> targetPositions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRig);
        if (sourceBindGlobals.Length != sourceRig.BoneCount ||
            targetPositions.Length != sourceRig.BoneCount)
        {
            throw new ArgumentException(
                "The bind poses and target positions must cover every source bone.",
                nameof(targetPositions));
        }

        int count = sourceRig.BoneCount;
        List<int>[] constraints = BuildConstraints(sourceRig, targetPositions);
        var deltas = new TransformMatrix[count];
        var positions = new Vector3D[count];
        var posed = ImmutableArray.CreateBuilder<TransformMatrix>(count);
        var skinning = ImmutableArray.CreateBuilder<TransformMatrix>(count);
        double residual = 0.0;

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int parent = sourceRig.Bones[index].ParentIndex;
            TransformMatrix parentDelta = parent < 0
                ? TransformMatrix.Identity
                : deltas[parent];

            // A mapped bone is pinned exactly onto its target. Everything else
            // is carried rigidly by its parent, which is what keeps facial and
            // twist rows attached to the joint they belong to.
            Vector3D position;
            if (targetPositions[index] is { } pinned)
            {
                position = pinned;
            }
            else if (parent < 0)
            {
                position = sourceBindGlobals[index].Translation;
            }
            else
            {
                Vector3D bindOffset =
                    sourceBindGlobals[index].Translation -
                    sourceBindGlobals[parent].Translation;
                position = positions[parent] + parentDelta.TransformDirection(bindOffset);
            }

            positions[index] = position;
            deltas[index] = ResolveDelta(
                index,
                constraints[index],
                parentDelta,
                position,
                sourceBindGlobals,
                targetPositions);

            TransformMatrix posedGlobal = WithTranslation(
                deltas[index] * RotationOnly(sourceBindGlobals[index]),
                position);
            posed.Add(posedGlobal);
            skinning.Add(posedGlobal * sourceBindGlobals[index].InvertedAffine());

            if (targetPositions[index] is { } expected)
            {
                residual = Math.Max(residual, Vector3D.Distance(expected, position));
            }
        }

        return new RigRestPoseTransferResult
        {
            PosedGlobals = posed.MoveToImmutable(),
            SkinningTransforms = skinning.MoveToImmutable(),
            MaximumJointResidual = residual,
        };
    }

    /// <summary>
    /// Solves the rotation that best carries this bone's mapped children from
    /// where the model has them to where the target wants them.
    /// </summary>
    private static TransformMatrix ResolveDelta(
        int index,
        List<int> constraints,
        TransformMatrix parentDelta,
        Vector3D position,
        ImmutableArray<TransformMatrix> sourceBindGlobals,
        ImmutableArray<Vector3D?> targetPositions)
    {
        if (targetPositions[index] is null || constraints.Count == 0)
        {
            return parentDelta;
        }

        // Cross-covariance of the authored offsets against the wanted ones. Its
        // nearest orthogonal factor is the least-squares rotation.
        double[] covariance = new double[9];
        double scale = 0.0;
        int contributing = 0;
        foreach (int child in constraints)
        {
            if (targetPositions[child] is not { } childTarget)
            {
                continue;
            }

            Vector3D bind =
                sourceBindGlobals[child].Translation -
                sourceBindGlobals[index].Translation;
            Vector3D wanted = childTarget - position;
            if (bind.Length < DirectionEpsilon || wanted.Length < DirectionEpsilon)
            {
                continue;
            }

            Accumulate(covariance, wanted, bind);
            scale += bind.LengthSquared;
            contributing++;
        }

        if (scale <= DirectionEpsilon)
        {
            return parentDelta;
        }

        // One constraint pins a direction but leaves twist about it free, so
        // bias toward the parent to settle it on the twist the rest of the limb
        // already has. Two or more constraints determine the rotation outright,
        // and biasing them would pull a correct fit off by a fraction of a
        // degree for nothing.
        if (contributing < 2)
        {
            AddScaled(covariance, parentDelta, scale * ParentBias);
        }

        TransformMatrix rotation = NearestRotation(covariance);
        return rotation.IsFinite &&
               Math.Abs(rotation.LinearDeterminant - 1.0) < 1e-6
            ? rotation
            : parentDelta;
    }

    /// <summary>
    /// For every bone, the mapped joints it is directly responsible for: those
    /// whose nearest mapped ancestor is this bone. A chain interrupted by
    /// unmapped rows still reaches the next real joint.
    /// </summary>
    private static List<int>[] BuildConstraints(
        RigDefinition sourceRig,
        ImmutableArray<Vector3D?> targetPositions)
    {
        int count = sourceRig.BoneCount;
        var constraints = new List<int>[count];
        for (int index = 0; index < count; index++)
        {
            constraints[index] = [];
        }

        for (int index = 0; index < count; index++)
        {
            if (targetPositions[index] is null)
            {
                continue;
            }

            int ancestor = sourceRig.Bones[index].ParentIndex;
            while (ancestor >= 0 && targetPositions[ancestor] is null)
            {
                ancestor = sourceRig.Bones[ancestor].ParentIndex;
            }

            if (ancestor >= 0)
            {
                constraints[ancestor].Add(index);
            }
        }

        return constraints;
    }

    private static void Accumulate(double[] covariance, Vector3D left, Vector3D right)
    {
        covariance[0] += left.X * right.X;
        covariance[1] += left.X * right.Y;
        covariance[2] += left.X * right.Z;
        covariance[3] += left.Y * right.X;
        covariance[4] += left.Y * right.Y;
        covariance[5] += left.Y * right.Z;
        covariance[6] += left.Z * right.X;
        covariance[7] += left.Z * right.Y;
        covariance[8] += left.Z * right.Z;
    }

    private static void AddScaled(
        double[] covariance,
        TransformMatrix rotation,
        double weight)
    {
        covariance[0] += rotation.M11 * weight;
        covariance[1] += rotation.M12 * weight;
        covariance[2] += rotation.M13 * weight;
        covariance[3] += rotation.M21 * weight;
        covariance[4] += rotation.M22 * weight;
        covariance[5] += rotation.M23 * weight;
        covariance[6] += rotation.M31 * weight;
        covariance[7] += rotation.M32 * weight;
        covariance[8] += rotation.M33 * weight;
    }

    /// <summary>
    /// Nearest rotation to a 3x3 matrix, by Newton polar iteration. This is the
    /// same projection the Chrome frame authoring uses; it converges in a few
    /// steps for the well-conditioned covariances a skeleton produces.
    /// </summary>
    private static TransformMatrix NearestRotation(double[] values)
    {
        TransformMatrix current = new(
            values[0], values[1], values[2], 0.0,
            values[3], values[4], values[5], 0.0,
            values[6], values[7], values[8], 0.0,
            0.0, 0.0, 0.0, 1.0);
        double frobenius = Math.Sqrt(values.Sum(static value => value * value));
        if (!double.IsFinite(frobenius) || frobenius <= 1e-12)
        {
            return TransformMatrix.Identity;
        }

        current = ScaleLinear(current, 1.0 / frobenius);
        for (int iteration = 0; iteration < 48; iteration++)
        {
            TransformMatrix inverse;
            try
            {
                inverse = current.InvertedAffine();
            }
            catch (InvalidOperationException)
            {
                return TransformMatrix.Identity;
            }

            TransformMatrix next = AverageLinear(current, TransposeLinear(inverse));
            double delta = MaximumLinearDifference(current, next);
            current = next;
            if (delta <= 1e-14)
            {
                break;
            }
        }

        // A reflection would mirror the model; refuse it rather than emit one.
        return current.LinearDeterminant > 0.0
            ? current
            : TransformMatrix.Identity;
    }

    private static TransformMatrix RotationOnly(TransformMatrix value) =>
        new(
            value.M11, value.M12, value.M13, 0.0,
            value.M21, value.M22, value.M23, 0.0,
            value.M31, value.M32, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix WithTranslation(
        TransformMatrix rotation,
        Vector3D translation) =>
        new(
            rotation.M11, rotation.M12, rotation.M13, translation.X,
            rotation.M21, rotation.M22, rotation.M23, translation.Y,
            rotation.M31, rotation.M32, rotation.M33, translation.Z,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix ScaleLinear(TransformMatrix value, double scale) =>
        new(
            value.M11 * scale, value.M12 * scale, value.M13 * scale, 0.0,
            value.M21 * scale, value.M22 * scale, value.M23 * scale, 0.0,
            value.M31 * scale, value.M32 * scale, value.M33 * scale, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix TransposeLinear(TransformMatrix value) =>
        new(
            value.M11, value.M21, value.M31, 0.0,
            value.M12, value.M22, value.M32, 0.0,
            value.M13, value.M23, value.M33, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static TransformMatrix AverageLinear(
        TransformMatrix left,
        TransformMatrix right) =>
        new(
            (left.M11 + right.M11) * 0.5, (left.M12 + right.M12) * 0.5, (left.M13 + right.M13) * 0.5, 0.0,
            (left.M21 + right.M21) * 0.5, (left.M22 + right.M22) * 0.5, (left.M23 + right.M23) * 0.5, 0.0,
            (left.M31 + right.M31) * 0.5, (left.M32 + right.M32) * 0.5, (left.M33 + right.M33) * 0.5, 0.0,
            0.0, 0.0, 0.0, 1.0);

    private static double MaximumLinearDifference(
        TransformMatrix left,
        TransformMatrix right)
    {
        double maximum = 0.0;
        maximum = Math.Max(maximum, Math.Abs(left.M11 - right.M11));
        maximum = Math.Max(maximum, Math.Abs(left.M12 - right.M12));
        maximum = Math.Max(maximum, Math.Abs(left.M13 - right.M13));
        maximum = Math.Max(maximum, Math.Abs(left.M21 - right.M21));
        maximum = Math.Max(maximum, Math.Abs(left.M22 - right.M22));
        maximum = Math.Max(maximum, Math.Abs(left.M23 - right.M23));
        maximum = Math.Max(maximum, Math.Abs(left.M31 - right.M31));
        maximum = Math.Max(maximum, Math.Abs(left.M32 - right.M32));
        return Math.Max(maximum, Math.Abs(left.M33 - right.M33));
    }
}

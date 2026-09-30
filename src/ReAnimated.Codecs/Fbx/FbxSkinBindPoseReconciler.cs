using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Read-only evidence summary for choosing between FBX bind-pose rows and
/// evaluated Model globals for weighted skin joints.
/// </summary>
public sealed record FbxSkinBindPoseAssessment(
    bool PreferEvaluatedModelGlobals,
    int WeightedClusterCount,
    int UsableClusterCount,
    int EvaluatedAgreementCount,
    int EvaluatedConflictCount,
    int PoseComparedCount,
    int PoseAgreementCount,
    int PoseConflictCount,
    double MaximumTranslationDiscrepancyMeters,
    double MaximumLinearDiscrepancy,
    double MaximumPoseTranslationDiscrepancyMeters,
    double MaximumPoseLinearDiscrepancy)
{
    /// <summary>Distinct weighted Model IDs whose BindPose rows conflict with their skin links.</summary>
    public ImmutableArray<long> ConflictingPoseModelIds { get; init; } = [];
}

/// <summary>
/// Compares weighted skin Cluster TransformLink matrices with their linked
/// Models' evaluated globals and optional BindPose globals. This class only
/// assesses evidence; it does not alter the FBX scene or its geometry.
/// </summary>
public static class FbxSkinBindPoseReconciler
{
    private const double PositiveWeightEpsilon = 1.0e-12;
    private const double AffineRowTolerance = 1.0e-9;

    /// <summary>
    /// Recommends evaluated Model globals only when every usable weighted
    /// Cluster agrees with its linked Model and at least one corresponding
    /// BindPose global conflicts with the Cluster TransformLink.
    /// Translation discrepancies are reported in meters; linear discrepancies
    /// are the largest absolute difference among the 3x3 linear coefficients.
    /// </summary>
    public static FbxSkinBindPoseAssessment Assess(
        FbxSemanticScene scene,
        IReadOnlyDictionary<long, TransformMatrix> poseGlobals,
        IReadOnlyDictionary<long, TransformMatrix> evaluatedModelGlobals,
        double metersPerUnit,
        double translationToleranceMeters = 1.0e-4,
        double linearTolerance = 1.0e-5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(poseGlobals);
        ArgumentNullException.ThrowIfNull(evaluatedModelGlobals);
        if (!double.IsFinite(metersPerUnit) || metersPerUnit <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(metersPerUnit), "Meters per unit must be finite and positive.");
        }

        ValidateTolerance(translationToleranceMeters, nameof(translationToleranceMeters));
        ValidateTolerance(linearTolerance, nameof(linearTolerance));

        int weightedClusterCount = 0;
        int usableClusterCount = 0;
        int evaluatedAgreementCount = 0;
        int evaluatedConflictCount = 0;
        int poseComparedCount = 0;
        int poseAgreementCount = 0;
        int poseConflictCount = 0;
        var conflictingPoseModelIds = new HashSet<long>();
        double maximumTranslationDiscrepancyMeters = 0.0;
        double maximumLinearDiscrepancy = 0.0;
        double maximumPoseTranslationDiscrepancyMeters = 0.0;
        double maximumPoseLinearDiscrepancy = 0.0;

        foreach ((long clusterId, FbxNode cluster) in scene.ObjectNodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCluster(cluster) || !HasPositiveWeight(cluster, clusterId))
            {
                continue;
            }

            weightedClusterCount++;
            long[] linkedModels = scene.GetChildren(clusterId)
                .Where(connection =>
                    string.Equals(connection.Kind, "OO", StringComparison.Ordinal) &&
                    scene.Models.ContainsKey(connection.ChildId))
                .Select(static connection => connection.ChildId)
                .Distinct()
                .ToArray();
            if (linkedModels.Length != 1 ||
                !evaluatedModelGlobals.TryGetValue(linkedModels[0], out TransformMatrix evaluated) ||
                !TryReadTransformLink(cluster, out TransformMatrix transformLink))
            {
                continue;
            }

            usableClusterCount++;
            (double translationMeters, double linear) = MeasureDifference(
                transformLink,
                evaluated,
                metersPerUnit);
            maximumTranslationDiscrepancyMeters = Math.Max(
                maximumTranslationDiscrepancyMeters,
                translationMeters);
            maximumLinearDiscrepancy = Math.Max(maximumLinearDiscrepancy, linear);
            if (translationMeters <= translationToleranceMeters && linear <= linearTolerance)
            {
                evaluatedAgreementCount++;
            }
            else
            {
                evaluatedConflictCount++;
            }

            if (!poseGlobals.TryGetValue(linkedModels[0], out TransformMatrix pose) || !IsUsableAffine(pose))
            {
                continue;
            }

            poseComparedCount++;
            (double poseTranslationMeters, double poseLinear) = MeasureDifference(
                transformLink,
                pose,
                metersPerUnit);
            maximumPoseTranslationDiscrepancyMeters = Math.Max(
                maximumPoseTranslationDiscrepancyMeters,
                poseTranslationMeters);
            maximumPoseLinearDiscrepancy = Math.Max(maximumPoseLinearDiscrepancy, poseLinear);
            if (poseTranslationMeters <= translationToleranceMeters && poseLinear <= linearTolerance)
            {
                poseAgreementCount++;
            }
            else
            {
                poseConflictCount++;
                conflictingPoseModelIds.Add(linkedModels[0]);
            }
        }

        bool preferEvaluated = weightedClusterCount > 0 &&
            usableClusterCount == weightedClusterCount &&
            evaluatedAgreementCount == usableClusterCount &&
            poseComparedCount > 0 &&
            poseConflictCount > 0;
        return new FbxSkinBindPoseAssessment(
            preferEvaluated,
            weightedClusterCount,
            usableClusterCount,
            evaluatedAgreementCount,
            evaluatedConflictCount,
            poseComparedCount,
            poseAgreementCount,
            poseConflictCount,
            maximumTranslationDiscrepancyMeters,
            maximumLinearDiscrepancy,
            maximumPoseTranslationDiscrepancyMeters,
            maximumPoseLinearDiscrepancy)
        {
            ConflictingPoseModelIds = conflictingPoseModelIds.Order().ToImmutableArray(),
        };
    }

    private static bool IsCluster(FbxNode node) =>
        string.Equals(node.Name, "Deformer", StringComparison.Ordinal) &&
        node.Properties.Length >= 3 &&
        string.Equals(FbxSemanticValues.ConvertString(node.Properties[2].Value), "Cluster", StringComparison.Ordinal);

    private static bool HasPositiveWeight(FbxNode cluster, long clusterId)
    {
        FbxNode? weightsNode = cluster.FindChild("Weights");
        if (weightsNode is null)
        {
            return false;
        }

        ImmutableArray<double> weights;
        try
        {
            weights = FbxSemanticValues.ReadDoubleArray(weightsNode, $"Cluster {clusterId} Weights");
        }
        catch (InvalidDataException)
        {
            return false;
        }

        return weights.Any(static weight => double.IsFinite(weight) && weight > PositiveWeightEpsilon);
    }

    private static bool TryReadTransformLink(FbxNode cluster, out TransformMatrix matrix)
    {
        ImmutableArray<double> values;
        try
        {
            values = FbxSemanticValues.ReadDoubleArray(
                cluster.FindChild("TransformLink"),
                "Cluster TransformLink");
        }
        catch (InvalidDataException)
        {
            matrix = default;
            return false;
        }

        if (values.Length != 16 || values.Any(static value => !double.IsFinite(value)))
        {
            matrix = default;
            return false;
        }

        // FBX matrices are row-vector arrays; convert once to Core's
        // column-vector convention (translation becomes elements 12..14).
        matrix = new TransformMatrix(
            values[0], values[4], values[8], values[12],
            values[1], values[5], values[9], values[13],
            values[2], values[6], values[10], values[14],
            values[3], values[7], values[11], values[15]);
        return IsUsableAffine(matrix);
    }

    private static bool IsUsableAffine(TransformMatrix matrix) =>
        matrix.IsFinite &&
        Math.Abs(matrix.M41) <= AffineRowTolerance &&
        Math.Abs(matrix.M42) <= AffineRowTolerance &&
        Math.Abs(matrix.M43) <= AffineRowTolerance &&
        Math.Abs(matrix.M44 - 1.0) <= AffineRowTolerance &&
        Math.Abs(matrix.LinearDeterminant) > 1.0e-12;

    private static (double TranslationMeters, double Linear) MeasureDifference(
        TransformMatrix left,
        TransformMatrix right,
        double metersPerUnit)
    {
        double dx = (left.M14 - right.M14) * metersPerUnit;
        double dy = (left.M24 - right.M24) * metersPerUnit;
        double dz = (left.M34 - right.M34) * metersPerUnit;
        double translationMeters = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        double linear = Math.Max(
            Math.Max(
                Math.Max(Math.Abs(left.M11 - right.M11), Math.Abs(left.M12 - right.M12)),
                Math.Max(Math.Abs(left.M13 - right.M13), Math.Abs(left.M21 - right.M21))),
            Math.Max(
                Math.Max(
                    Math.Max(Math.Abs(left.M22 - right.M22), Math.Abs(left.M23 - right.M23)),
                    Math.Abs(left.M31 - right.M31)),
                Math.Max(Math.Abs(left.M32 - right.M32), Math.Abs(left.M33 - right.M33))));
        return (translationMeters, linear);
    }

    private static void ValidateTolerance(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Tolerance must be finite and non-negative.");
        }
    }
}

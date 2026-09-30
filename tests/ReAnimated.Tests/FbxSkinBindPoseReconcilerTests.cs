using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class FbxSkinBindPoseReconcilerTests
{
    [Fact]
    public void PrefersEvaluatedGlobalsOnlyWhenEveryWeightedClusterAgreesAndPoseConflicts()
    {
        FbxSemanticScene scene = CreateScene(
            (101, 1, TransformMatrix.Identity),
            (102, 2, TransformMatrix.CreateTranslation(new Vector3D(200.0, 0.0, 0.0))));
        ImmutableDictionary<long, TransformMatrix> evaluated = new Dictionary<long, TransformMatrix>
        {
            [1] = TransformMatrix.Identity,
            [2] = TransformMatrix.CreateTranslation(new Vector3D(200.0, 0.0, 0.0)),
        }.ToImmutableDictionary();
        ImmutableDictionary<long, TransformMatrix> pose = new Dictionary<long, TransformMatrix>
        {
            [1] = TransformMatrix.CreateTranslation(new Vector3D(100.0, 0.0, 0.0)),
            [2] = TransformMatrix.CreateTranslation(new Vector3D(200.0, 0.0, 0.0)),
            [3] = TransformMatrix.CreateTranslation(new Vector3D(900.0, 0.0, 0.0)),
        }.ToImmutableDictionary();

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene,
            pose,
            evaluated,
            metersPerUnit: 0.01,
            translationToleranceMeters: 1.0e-5);

        Assert.True(result.PreferEvaluatedModelGlobals);
        Assert.Equal(2, result.WeightedClusterCount);
        Assert.Equal(2, result.UsableClusterCount);
        Assert.Equal(2, result.EvaluatedAgreementCount);
        Assert.Equal(0, result.EvaluatedConflictCount);
        Assert.Equal(2, result.PoseComparedCount);
        Assert.Equal(1, result.PoseAgreementCount);
        Assert.Equal(1, result.PoseConflictCount);
        Assert.Equal(1.0, result.MaximumPoseTranslationDiscrepancyMeters, 12);
        Assert.Equal(0.0, result.MaximumPoseLinearDiscrepancy, 12);
        Assert.Equal(new long[] { 1 }, result.ConflictingPoseModelIds.ToArray());
    }

    [Fact]
    public void PartialPoseCoverageScopesFallbackToOnlyContradictedWeightedModel()
    {
        TransformMatrix second = TransformMatrix.CreateTranslation(new Vector3D(200.0, 0.0, 0.0));
        FbxSemanticScene scene = CreateScene(
            (101, 1, TransformMatrix.Identity),
            (102, 2, second));
        ImmutableDictionary<long, TransformMatrix> evaluated = new Dictionary<long, TransformMatrix>
        {
            [1] = TransformMatrix.Identity,
            [2] = second,
        }.ToImmutableDictionary();
        ImmutableDictionary<long, TransformMatrix> pose = new Dictionary<long, TransformMatrix>
        {
            [1] = TransformMatrix.CreateTranslation(new Vector3D(100.0, 0.0, 0.0)),
            [3] = TransformMatrix.CreateTranslation(new Vector3D(900.0, 0.0, 0.0)),
        }.ToImmutableDictionary();

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene, pose, evaluated, metersPerUnit: 0.01);

        Assert.True(result.PreferEvaluatedModelGlobals);
        Assert.Equal(2, result.WeightedClusterCount);
        Assert.Equal(1, result.PoseComparedCount);
        Assert.Equal(new long[] { 1 }, result.ConflictingPoseModelIds.ToArray());
        Assert.Equal(900.0, pose[3].Translation.X);
    }

    [Fact]
    public void PreservesPoseWhenPoseMatchesClusters()
    {
        TransformMatrix bind = TransformMatrix.CreateTranslation(new Vector3D(35.0, 0.0, 0.0));
        FbxSemanticScene scene = CreateScene((101, 1, bind));

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene,
            new Dictionary<long, TransformMatrix> { [1] = bind },
            new Dictionary<long, TransformMatrix> { [1] = bind },
            metersPerUnit: 0.01);

        Assert.False(result.PreferEvaluatedModelGlobals);
        Assert.Equal(1, result.PoseAgreementCount);
        Assert.Equal(0, result.PoseConflictCount);
        Assert.Empty(result.ConflictingPoseModelIds);
    }

    [Fact]
    public void PreservesPoseWhenOnlyPoseMatchesWeightedCluster()
    {
        TransformMatrix bind = TransformMatrix.CreateTranslation(new Vector3D(40.0, 0.0, 0.0));
        FbxSemanticScene scene = CreateScene((101, 1, bind));

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene,
            new Dictionary<long, TransformMatrix> { [1] = bind },
            new Dictionary<long, TransformMatrix> { [1] = TransformMatrix.Identity },
            metersPerUnit: 0.01);

        Assert.False(result.PreferEvaluatedModelGlobals);
        Assert.Equal(0, result.EvaluatedAgreementCount);
        Assert.Equal(1, result.EvaluatedConflictCount);
        Assert.Equal(1, result.PoseAgreementCount);
        Assert.Equal(0, result.PoseConflictCount);
    }

    [Fact]
    public void PreservesPoseWhenClusterEvidenceIsMixed()
    {
        TransformMatrix offset = TransformMatrix.CreateTranslation(new Vector3D(5.0, 0.0, 0.0));
        FbxSemanticScene scene = CreateScene(
            (101, 1, TransformMatrix.Identity),
            (102, 2, offset));
        ImmutableDictionary<long, TransformMatrix> evaluated = new Dictionary<long, TransformMatrix>
        {
            [1] = TransformMatrix.Identity,
            [2] = TransformMatrix.Identity,
        }.ToImmutableDictionary();
        ImmutableDictionary<long, TransformMatrix> pose = new Dictionary<long, TransformMatrix>
        {
            [1] = offset,
            [2] = offset,
        }.ToImmutableDictionary();

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene,
            pose,
            evaluated,
            metersPerUnit: 0.01);

        Assert.False(result.PreferEvaluatedModelGlobals);
        Assert.Equal(1, result.EvaluatedConflictCount);
        Assert.Equal(1, result.PoseConflictCount);
    }

    [Fact]
    public void MissingOrUnusableEvidenceNeverCreatesRecommendation()
    {
        FbxSemanticScene scene = CreateScene((101, 1, TransformMatrix.Identity));

        FbxSkinBindPoseAssessment noPose = FbxSkinBindPoseReconciler.Assess(
            scene,
            ImmutableDictionary<long, TransformMatrix>.Empty,
            new Dictionary<long, TransformMatrix> { [1] = TransformMatrix.Identity },
            metersPerUnit: 1.0);
        Assert.False(noPose.PreferEvaluatedModelGlobals);
        Assert.Equal(1, noPose.UsableClusterCount);
        Assert.Equal(0, noPose.PoseComparedCount);

        FbxSkinBindPoseAssessment noEvaluation = FbxSkinBindPoseReconciler.Assess(
            scene,
            new Dictionary<long, TransformMatrix> { [1] = TransformMatrix.CreateTranslation(new Vector3D(1.0, 0.0, 0.0)) },
            ImmutableDictionary<long, TransformMatrix>.Empty,
            metersPerUnit: 1.0);
        Assert.False(noEvaluation.PreferEvaluatedModelGlobals);
        Assert.Equal(0, noEvaluation.UsableClusterCount);
    }

    [Fact]
    public void ComparesTheFullLinearBasisAndReportsTranslationInMeters()
    {
        TransformMatrix link = new(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0);
        FbxSemanticScene scene = CreateScene((101, 1, link));
        TransformMatrix evaluated = new(
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.02, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0);
        TransformMatrix pose = TransformMatrix.CreateTranslation(new Vector3D(12.0, 0.0, 0.0));

        FbxSkinBindPoseAssessment result = FbxSkinBindPoseReconciler.Assess(
            scene,
            new Dictionary<long, TransformMatrix> { [1] = pose },
            new Dictionary<long, TransformMatrix> { [1] = evaluated },
            metersPerUnit: 0.01);

        Assert.False(result.PreferEvaluatedModelGlobals);
        Assert.Equal(0.02, result.MaximumLinearDiscrepancy, 12);
        Assert.Equal(0.12, result.MaximumPoseTranslationDiscrepancyMeters, 12);
    }

    private static FbxSemanticScene CreateScene(params (long Id, long ModelId, TransformMatrix Link)[] clusters)
    {
        FbxNode[] models = clusters
            .Select(static item => Model(item.ModelId))
            .DistinctBy(static node => (long)node.Properties[0].Value)
            .ToArray();
        FbxNode[] connections = clusters.Select(
                static item => Node("C", ["OO", item.ModelId, item.Id]))
            .ToArray();
        FbxNode connectionNode = Node("Connections", [], connections);
        FbxNode[] allObjects =
        [
            .. models,
            .. clusters.Select(static item => Cluster(item.Id, item.Link)),
        ];
        var document = new FbxBinaryDocument(
            7400,
            [Node("Objects", [], allObjects), connectionNode]);
        return FbxSemanticScene.Parse(document);
    }

    private static FbxNode Model(long id) =>
        Node("Model", [id, $"Model::{id}", "LimbNode"]);

    private static FbxNode Cluster(long id, TransformMatrix? transformLink = null) =>
        Node(
            "Deformer",
            [id, $"Deformer::{id}", "Cluster"],
            Node("Indexes", [ImmutableArray.Create(0L)]),
            Node("Weights", [ImmutableArray.Create(1.0)]),
            Node("TransformLink", [ToFbxRowVector(transformLink ?? TransformMatrix.Identity)]));

    private static ImmutableArray<double> ToFbxRowVector(TransformMatrix matrix) =>
        ImmutableArray.Create(
            matrix.M11, matrix.M21, matrix.M31, matrix.M41,
            matrix.M12, matrix.M22, matrix.M32, matrix.M42,
            matrix.M13, matrix.M23, matrix.M33, matrix.M43,
            matrix.M14, matrix.M24, matrix.M34, matrix.M44);

    private static FbxNode Node(string name, object[] properties, params FbxNode[] children) =>
        new(
            name,
            properties.Select(static value => new FbxProperty(
                value switch
                {
                    long => 'L',
                    int => 'I',
                    double => 'D',
                    string => 'S',
                    ImmutableArray<long> => 'l',
                    ImmutableArray<double> => 'd',
                    _ => 'R',
                },
                value)).ToImmutableArray(),
            children.ToImmutableArray(),
            0,
            0);
}

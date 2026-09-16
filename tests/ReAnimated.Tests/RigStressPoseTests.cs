using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RigStressPoseTests
{
    [Fact]
    public void LocalRotationPreservesPivotAffineResidualAndUneditedBranch()
    {
        var model = AffineModel();
        var document = model.Package.Document;
        var rest = RigStressPose.Evaluate(document, model.Rig!, [], 0);
        var posed = RigStressPose.Evaluate(document, model.Rig!, [new(1, new(0, 0, 90))]);
        Assert.Equal<TransformMatrix>(document.CreateEffectiveBones().Select(b => b.ExactLocalBindMatrix), rest.LocalMatrices);
        Assert.Equal(rest.GlobalMatrices[1].Translation, posed.GlobalMatrices[1].Translation);
        Assert.Equal(rest.LocalMatrices[1].LinearDeterminant, posed.LocalMatrices[1].LinearDeterminant, 10);
        var before = Columns(rest.LocalMatrices[1]); var after = Columns(posed.LocalMatrices[1]);
        for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++)
            Assert.Equal(Vector3D.Dot(before[a], before[b]), Vector3D.Dot(after[a], after[b]), 10);
        Assert.Equal(rest.GlobalMatrices[^2], posed.GlobalMatrices[^2]);
        Assert.True((rest.GlobalMatrices[^1].Translation - posed.GlobalMatrices[^1].Translation).Length > .1);
        var zero = RigStressPose.Evaluate(document, model.Rig!, [new(1, new(0, 0, 90))], 0);
        Assert.Equal<TransformMatrix>(rest.LocalMatrices, zero.LocalMatrices);
        Assert.Equal<TransformMatrix>(rest.GlobalMatrices, zero.GlobalMatrices);
    }

    [Fact]
    public void ExactRestFlowsThroughSourceAndPreparedRenderSkeletons()
    {
        var model = AffineModel();
        var source = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        var sourceSkeleton = source.CreateSkeleton(null, 0)!;
        Assert.Equal(CorePreviewAdapter.ToSystemMatrix(model.Package.Document.Bones[1].ExactLocalBindMatrix), sourceSkeleton.Bones[1].LocalTransform);
        var rest = RigStressPose.Evaluate(model.Package.Document, model.Rig!, []);
        var prepared = Dl1CustomModelRigPreparer.Prepare(model);
        var output = prepared.RebasePose(rest);
        Assert.True(output.HasAffineLocalMatrices);
        foreach (var node in prepared.Contract.Nodes)
            Assert.True(node.GlobalBindMatrix.NearlyEquals(output.GlobalMatrices[node.PhysicalIndex], 1e-7));
        var render = CorePreviewAdapter.ToRenderSkeleton(output);
        Assert.Equal(CorePreviewAdapter.ToSystemMatrix(output.LocalMatrices[1]), render.Bones[1].LocalTransform);
    }

    [Fact]
    public void CycleIsRestPeakRestAndInvalidEditsAreRefused()
    {
        Assert.Equal(0, RigStressPose.CycleAmount(0, 4));
        Assert.Equal(.5, RigStressPose.CycleAmount(1, 4), 12);
        Assert.Equal(1, RigStressPose.CycleAmount(2, 4));
        Assert.Equal(0, RigStressPose.CycleAmount(4, 4));
        var model = AffineModel();
        Assert.Throws<ArgumentException>(() => RigStressPose.Evaluate(model.Package.Document, model.Rig!, [new(1, Vector3D.Zero), new(1, Vector3D.Zero)]));
        Assert.Throws<ArgumentException>(() => RigStressPose.Evaluate(model.Package.Document, model.Rig!, [new(1, new(double.NaN, 0, 0))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => RigStressPose.Evaluate(model.Package.Document, model.Rig!, [], double.NaN));
        Assert.Throws<ArgumentException>(() => RigStressPose.CycleAmount(0, 0));
    }

    [Fact]
    public async Task WorkspaceStressMorphAndPaintingAreExclusiveAndNeverChangePackage()
    {
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null);
        workspace.CommitProjectRestore(new(FbxSkinWeightAuthoringTests.Model(), "stress.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        workspace.IsConformTabSelected = true;
        var original = workspace.CaptureProjectSession().Model!;
        var fingerprint = Dl1OfficialModelCompiler.CalculateInputFingerprint(original, "generic", "default", null);
        var wizard = workspace.Conformance;
        wizard.SelectedStressJoint = wizard.StressJoints.Single(j => j.Name == "joint_a");
        wizard.StressAngleZ = 90; wizard.SetJointStressCommand.Execute(null);
        wizard.SelectedStressMorph = "expression"; wizard.StressMorphValue = -.5; wizard.SetStressMorphCommand.Execute(null);
        Assert.True(wizard.TryGetStressPreview(out var pose, out var morphs));
        Assert.Equal(-.5f, Assert.Single(morphs).Weight);
        Assert.NotEqual(original.Rig!.CreateBindPose().GlobalMatrices[1], pose!.GlobalMatrices[1]);
        var frame = workspace.Viewport.SceneSource.CaptureFrame();
        Assert.Equal(-.5f, Assert.Single(frame.MorphWeights).Weight);
        var vertices = CpuMeshDeformationEvaluator.Evaluate(frame.Meshes[0], frame.Skeleton, frame.MorphWeights);
        Assert.All(vertices, v => Assert.True(float.IsFinite(v.Position.X) && float.IsFinite(v.Normal.Y)));
        await wizard.MeasureStressCommand.ExecuteAsync(null);
        Assert.Contains("vertex samples", wizard.StressMeasurementStatus, StringComparison.Ordinal);
        wizard.ScaleStressMorphsWithAmount = true;
        wizard.ReturnStressToRestCommand.Execute(null);
        Assert.True(wizard.TryGetStressPreview(out var rest, out var restMorphs));
        Assert.All(restMorphs, w => Assert.Equal(0, w.Weight));
        Assert.Equal<TransformMatrix>(original.Package.Document.CreateEffectiveBones().Select(b => b.ExactLocalBindMatrix), rest!.LocalMatrices);
        await wizard.InspectWeightsCommand.ExecuteAsync(null);
        wizard.WeightBrushEnabled = true;
        Assert.False(wizard.StressPreviewEnabled);
        wizard.StressPreviewEnabled = true;
        Assert.False(wizard.WeightBrushEnabled);
        Assert.Equal(fingerprint, Dl1OfficialModelCompiler.CalculateInputFingerprint(workspace.CaptureProjectSession().Model!, "generic", "default", null));
        workspace.IsConformTabSelected = false;
        Assert.False(wizard.StressPreviewEnabled);
        Assert.False(wizard.IsStressCycling);
    }

    [Fact]
    public void SourceOrRestChangeClearsOffsetsWhileMetadataRefreshKeepsThem()
    {
        var model = FbxSkinWeightAuthoringTests.Model();
        var wizard = new RigConformanceWizardViewModel((_, _) => Task.FromResult(Dl1RigTemplateResolution.Failed("generic", "Fixture")), static _ => { });
        wizard.SetModel(model); wizard.SelectedStressJoint = wizard.StressJoints[1]; wizard.StressAngleX = 45; wizard.SetJointStressCommand.Execute(null);
        var renamed = model with { Package = model.Package with { Document = model.Package.Document with { Name = "Metadata edit" } } };
        wizard.SetModel(renamed);
        Assert.Single(wizard.StressOffsets); Assert.True(wizard.StressPreviewEnabled);
        wizard.SetModel(null);
        Assert.Empty(wizard.StressOffsets); Assert.False(wizard.StressPreviewEnabled);
    }

    internal static FbxModelAuthoringImportResult AffineModel()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "affine-stress.fbx");
        var bones = model.Package.Document.Bones;
        Assert.True(bones.Length >= 2);
        var shear = new TransformMatrix(-1, .2, .1, 0, 0, 1.5, .15, 0, 0, 0, .7, 0, 0, 0, 0, 1);
        bones = bones.SetItem(1, bones[1] with { ExactLocalBindMatrix = bones[1].LocalBindTransform.ToMatrix() * shear });
        var sibling = new TransformTRS(new(2, 0, 0), QuaternionD.Identity, Vector3D.One);
        var leaf = new TransformTRS(new(0, .5, 0), QuaternionD.Identity, Vector3D.One);
        bones = bones.Add(new() { Index = bones.Length, Name = "unaffected_branch", ParentIndex = 0, LocalBindTransform = sibling, ExactLocalBindMatrix = sibling.ToMatrix() })
            .Add(new() { Index = bones.Length + 1, Name = "stress_descendant", ParentIndex = 1, LocalBindTransform = leaf, ExactLocalBindMatrix = leaf.ToMatrix() });
        var document = model.Package.Document with { Bones = bones, RigSignature = CustomModelContractSignatures.ComputeRig(bones) };
        return model with { Package = model.Package with { Document = document }, Rig = document.CreateRigDefinition() };
    }
    private static Vector3D[] Columns(TransformMatrix m) => [new(m.M11, m.M21, m.M31), new(m.M12, m.M22, m.M32), new(m.M13, m.M23, m.M33)];

    [Fact]
    public void MeasurementsUseSameMorphsAtBothPosesAndDoNotDeclareQualityPass()
    {
        var model = FbxSkinWeightAuthoringTests.Model();
        var session = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        var rest = session.CreateSkeleton(RigStressPose.Evaluate(model.Package.Document, model.Rig!, []));
        var posed = session.CreateSkeleton(RigStressPose.Evaluate(model.Package.Document, model.Rig!, [new(1, new(0, 0, 90))]));
        MorphWeight[] morphs = [new("expression", -.5f)];
        var unchanged = StressDeformationMeasurement.Measure(session.Meshes, rest, rest, morphs);
        Assert.Equal(0, unchanged.MaximumDisplacement);
        Assert.Equal(1, unchanged.MinimumEdgeRatio); Assert.Equal(1, unchanged.MaximumEdgeRatio);
        var changed = StressDeformationMeasurement.Measure(session.Meshes, rest, posed, morphs);
        Assert.True(changed.MaximumDisplacement > 0);
        Assert.True(changed.FiniteNormals); Assert.True(changed.RequiresVisualReview);
        Assert.Equal(model.Surfaces.Sum(s => s.Vertices.Length), changed.VertexSamples);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StressDeformationMeasurement.Measure(session.Meshes, rest, posed, morphs, cancellation.Token));
    }
    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}

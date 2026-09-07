using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionDomainTests
{
    [Fact]
    public void ChangingFppDisplayCorrectionAndLensCannotInjectPhysicsMotion()
    {
        RigDefinition rig = Rig();
        SecondaryMotionDefinition definition = Definition();
        var baseline = new SecondaryMotionSession(definition, rig.Bones.Select(b => b.Name));
        var changingView = new SecondaryMotionSession(definition, rig.Bones.Select(b => b.Name));
        SecondaryMotionFrame Sample(double seconds, bool changeView)
        {
            EvaluationFrame evaluated = Frame(rig, seconds, changeView && seconds >= 0.3);
            return new(SecondaryMotionDomain.SelectPhysicsPose(evaluated).GlobalMatrices, evaluated.ActorWorldTransform);
        }
        for (int frame = 0; frame <= 120; frame++)
        {
            double seconds = frame / 60.0;
            var expected = baseline.Sample(seconds, t => Sample(t, false));
            var actual = changingView.Sample(seconds, t => Sample(t, true));
            Assert.Equal(expected.Globals.ToArray(), actual.Globals.ToArray());
            Assert.Equal(expected.Particles.ToArray(), actual.Particles.ToArray());
        }
        EvaluationFrame view = Frame(rig, 1, true);
        Assert.NotEqual(view.AuthoredPose.GlobalMatrices[0], view.DisplayPose.GlobalMatrices[0]);
        Assert.Same(view.AuthoredPose, SecondaryMotionDomain.SelectPhysicsPose(view));
    }

    [Fact]
    public void BothFppPanesReceiveTheSamePhysicalBoneLocalCorrection()
    {
        RigDefinition rig = Rig();
        EvaluationFrame frame = Frame(rig, 0.4, true);
        var session = new SecondaryMotionSession(Definition(), rig.Bones.Select(b => b.Name));
        SecondaryMotionResult simulated = session.Sample(0.4, t =>
        {
            EvaluationFrame evaluated = Frame(rig, t, t > 0.3);
            return new(SecondaryMotionDomain.SelectPhysicsPose(evaluated).GlobalMatrices, evaluated.ActorWorldTransform);
        });
        TransformMatrix delta = frame.AuthoredPose.GlobalMatrices[2].InvertedAffine() * simulated.Globals[2];
        var corrections = new Dictionary<string, TransformMatrix>(StringComparer.Ordinal) { ["secondary_tip"] = delta };
        var display = CorePreviewAdapter.ToRenderSkeleton(frame.DisplayPose, actorWorldTransform: frame.ActorWorldTransform);
        var orbit = CorePreviewAdapter.ToRenderSkeleton(frame.AuthoredPose, actorWorldTransform: frame.ActorWorldTransform);
        var correctedDisplay = SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(display, corrections);
        var correctedOrbit = SecondaryMotionRenderAdapter.ApplyBoneLocalDeltas(orbit, corrections);
        foreach (int index in new[] { 0, 1, 3, 4 })
        {
            Assert.Equal(display.Bones[index], correctedDisplay.Bones[index]);
            Assert.Equal(orbit.Bones[index], correctedOrbit.Bones[index]);
        }
        Assert.Equal(CorePreviewAdapter.ToSystemMatrix(frame.DisplayPose.GlobalMatrices[2] * delta), correctedDisplay.Bones[2].WorldTransform);
        Assert.Equal(CorePreviewAdapter.ToSystemMatrix(frame.AuthoredPose.GlobalMatrices[2] * delta), correctedOrbit.Bones[2].WorldTransform);
        Assert.Equal(display.RootTransform, correctedDisplay.RootTransform);
        Assert.Equal(orbit.RootTransform, correctedOrbit.RootTransform);
    }

    [Fact]
    public void ThirdPersonStillUsesItsFinalDisplayPose()
    {
        RigDefinition rig = Rig();
        var frame = Frame(rig, 1, true, firstPerson: false);
        Assert.Same(frame.DisplayPose, SecondaryMotionDomain.SelectPhysicsPose(frame));
    }

    private static SecondaryMotionDefinition Definition()
    {
        SecondaryMotionDefinition definition = SecondaryMotionTests.Definition();
        return definition with { Groups = [definition.Groups[0] with { Preview = new() { Gravity = new(1, -9.81, 0), Damping = 5 } }] };
    }

    private static RigDefinition Rig() => new("generic-preview-rig", "Preview rig", [
        new(0, "root", -1, TransformTRS.Identity, BoneKind.Root),
        new(1, "secondary_anchor", 0, new(new(0, 1, 0), QuaternionD.Identity, Vector3D.One)),
        new(2, "secondary_tip", 1, new(new(0, -1, 0), QuaternionD.Identity, Vector3D.One)),
        new(3, "weapon", 0, new(new(1, 1, 0), QuaternionD.Identity, Vector3D.One), BoneKind.Prop),
        new(4, "camera", 0, new(new(0, 2, 0), QuaternionD.Identity, Vector3D.One), BoneKind.Camera),
    ]);

    private static EvaluationFrame Frame(RigDefinition rig, double seconds, bool changedView, bool firstPerson = true)
    {
        var authored = rig.CreateBindPose();
        var display = authored.WithLocalTransform(0, changedView
            ? new(new(8, -3, 5), QuaternionD.Identity, new(2, 2, 2)) : TransformTRS.Identity);
        CameraLens lens = changedView ? new(100, 2, 0.005, 1000) : CameraLens.Default;
        PreviewProfile profile = firstPerson
            ? new("fpp-domain-control", PreviewViewMode.Split, AuthoringPreviewFidelity.AuthoringAccurate,
                PreviewVisualStyle.MaterialApproximation, "camera", lens, TransformTRS.Identity, context: Dl1PreviewContext.Dl1Fpp)
            : PreviewProfile.ThirdPersonAuthoring;
        return new(seconds * 30, authored, display, ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty, profile,
            new(TransformMatrix.CreateTranslation(new(changedView ? 20 : 0, 2, 0)), lens, firstPerson), [], [], null, [],
            actorWorldTransform: TransformMatrix.CreateTranslation(new(2, 0, 0)));
    }
}

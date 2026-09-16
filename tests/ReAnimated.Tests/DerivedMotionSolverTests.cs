using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class DerivedMotionSolverTests
{
    [Fact]
    public void DerivePreservesMappedWorldMotionAcrossEditedTargetBindAndRetimesAuxiliaryTracks()
    {
        RigDefinition source = Rig([("root", -1, Vector3D.Zero), ("parent", 0, new(0, 1, 0)), ("child", 1, new(1, 0, 0))]);
        RigDefinition target = Rig([("root", -1, new(.2, 0, 0)), ("parent", 0, new(0, 1.2, 0)), ("child", 1, new(1.1, 0, 0))]);
        ImmutableArray<TransformMatrix> sourceBind = Bind(source);
        ImmutableArray<TransformMatrix> targetBind = Bind(target);
        AnimationClip clip = new("source", new(30, 1), 3,
            [new TransformTrack(1, [new(0, new(new(0, 1, 0), QuaternionD.Identity, Vector3D.One)), new(2, new(new(0, 1.2, 0), QuaternionD.Identity, new(1.1, 1, 1)))])],
            [new ScalarTrack("signed", [new(0, -.5), new(2, .75)])],
            [new AuxiliaryTransformTrack(77, [new(0, new(new(0, .1, 0), QuaternionD.Identity, Vector3D.One)), new(2, new(new(0, .2, 0), QuaternionD.Identity, Vector3D.One))])]);

        DerivedMotionResult result = DerivedMotionSolver.Derive(source, sourceBind, clip, target, targetBind, [0, 1, 2]);

        Assert.NotNull(result.Clip);
        Assert.Equal(5, result.Clip.FrameCount);
        Assert.Equal(new FrameRate(60, 1), result.Clip.FrameRate);
        Assert.Equal(4, result.Clip.ScalarTracks[0].Keyframes[1].Frame);
        Assert.Equal(4, result.Clip.AuxiliaryTransformTracks[0].Keyframes[1].Frame);
        Assert.Equal(3, result.Report.MappedTargetCount);
        Assert.Equal(0, result.Report.UnmappedTargetCount);
        Assert.True(result.Report.CanExport, string.Join(";", result.Report.Diagnostics));
        SkeletonPose rest = result.Clip.SamplePose(target, 0);
        Assert.Equal(new Vector3D(.2, 0, 0), rest.GlobalMatrices[0].Translation);
        Assert.Equal(new Vector3D(.2, 1.2, 0), rest.GlobalMatrices[1].Translation);
        Assert.Equal(new Vector3D(1.3, 1.2, 0), rest.GlobalMatrices[2].Translation);
        Assert.Empty(clip.TransformTracks[0].Keyframes.Where(key => key.Frame == 4));
        Assert.Equal(0, result.Report.MaximumPositionError, 8);
    }

    [Fact]
    public void UntrackedAffineTargetHelperRemainsAConstantOmittedLocal()
    {
        RigDefinition source = Rig([("root", -1, Vector3D.Zero)]);
        RigDefinition target = Rig([("root", -1, Vector3D.Zero), ("helper", 0, new(.2, 0, 0))]);
        ImmutableArray<TransformMatrix> targetBind = Bind(target).SetItem(1, new(
            1, .2, 0, .2,
            0, 1, .1, 0,
            0, 0, 1, 0,
            0, 0, 0, 1));
        DerivedMotionResult result = DerivedMotionSolver.Derive(source, Bind(source), new("static", new(30, 1), 2), target, targetBind, [0, null]);

        Assert.NotNull(result.Clip);
        Assert.DoesNotContain(result.Clip.TransformTracks, track => track.BoneIndex == 1);
        Assert.Equal(1, result.Report.MappedTargetCount);
        Assert.Equal(1, result.Report.UnmappedTargetCount);
        Assert.False(result.Report.Unrepresentable);
    }

    [Fact]
    public void DynamicShearIsRejectedRatherThanProjected()
    {
        RigDefinition source = Rig([("root", -1, Vector3D.Zero), ("child", 0, new(1, 0, 0))]);
        RigDefinition target = Rig([("root", -1, Vector3D.Zero), ("child", 0, new(1, 0, 0))]);
        ImmutableArray<TransformMatrix> targetBind = Bind(target).SetItem(1, new(
            1, .4, 0, 1,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1));
        AnimationClip clip = new("shear", new(30, 1), 2,
            [new TransformTrack(0, [new(0, TransformTRS.Identity), new(1, new(Vector3D.Zero,
                QuaternionD.FromAxisAngle(Vector3D.UnitZ, .4), Vector3D.One))]),
             new TransformTrack(1, [new(0, new(new(1, 0, 0), QuaternionD.Identity, Vector3D.One)), new(1, new(new(1, 0, 0),
                QuaternionD.FromAxisAngle(Vector3D.UnitY, .3), Vector3D.One))])]);

        DerivedMotionResult result = DerivedMotionSolver.Derive(source, Bind(source), clip, target, targetBind, [0, 1]);

        Assert.Null(result.Clip);
        Assert.True(result.Report.Unrepresentable);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Contains("shear", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CurvedReparentedTrajectoryImprovesWithDenserOutputSampling()
    {
        RigDefinition source = Rig([("root", -1, Vector3D.Zero), ("parent", 0, new(0, 1, 0)), ("child", 1, new(1, 0, 0))]);
        RigDefinition target = Rig([("root", -1, Vector3D.Zero), ("parent", 0, new(0, 1, 0)), ("child", 0, new(1, 1, 0))]);
        AnimationClip clip = new("curved", new(30, 1), 3,
            [new TransformTrack(1, [new(0, TransformTRS.Identity), new(1, new(Vector3D.Zero, QuaternionD.FromAxisAngle(Vector3D.UnitZ, 1.1), Vector3D.One)), new(2, TransformTRS.Identity)])]);
        ImmutableArray<TransformMatrix> sourceBind = Bind(source);
        ImmutableArray<TransformMatrix> targetBind = Bind(target);
        DerivedMotionResult coarse = DerivedMotionSolver.Derive(source, sourceBind, clip, target, targetBind, [0, 1, 2], new() { SampleMultiplier = 1, PositionTolerance = 1e-5, AngularTolerance = 1e-5, LinearTolerance = 1e-5 });
        DerivedMotionResult dense = DerivedMotionSolver.Derive(source, sourceBind, clip, target, targetBind, [0, 1, 2], new() { SampleMultiplier = 4, PositionTolerance = 1e-5, AngularTolerance = 1e-5, LinearTolerance = 1e-5 });

        Assert.True(coarse.Report.TolerancesExceeded);
        Assert.True(dense.Report.MaximumPositionError < coarse.Report.MaximumPositionError);
        Assert.True(dense.Report.MaximumLinearError <= coarse.Report.MaximumLinearError + 1e-12);
    }

    [Fact]
    public void CancellationIsObservedBeforePublishingAClip()
    {
        RigDefinition rig = Rig([("root", -1, Vector3D.Zero)]);
        AnimationClip clip = new("cancel", new(30, 1), 100);
        Assert.Throws<OperationCanceledException>(() => DerivedMotionSolver.Derive(rig, Bind(rig), clip, rig, Bind(rig), [0], cancellationToken: new(true)));
    }

    [Fact]
    public void SampleBudgetFailureCannotBeExported()
    {
        RigDefinition rig = Rig([("root", -1, Vector3D.Zero)]);
        AnimationClip clip = new("budget", new(30, 1), 3);
        DerivedMotionResult result = DerivedMotionSolver.Derive(rig, Bind(rig), clip, rig, Bind(rig), [0],
            new() { MaximumSampleKeys = 2 });

        Assert.Null(result.Clip);
        Assert.False(result.Report.CanExport);
        Assert.Contains(result.Report.Diagnostics, diagnostic => diagnostic.Contains("budget", StringComparison.OrdinalIgnoreCase));
    }

    private static RigDefinition Rig((string Name, int Parent, Vector3D Position)[] rows) =>
        new("derived-test", "derived", rows.Select((row, index) => new BoneDefinition(index, row.Name, row.Parent,
            new TransformTRS(row.Position, QuaternionD.Identity, Vector3D.One), BoneKind.Deform)).ToImmutableArray());

    private static ImmutableArray<TransformMatrix> Bind(RigDefinition rig) =>
        rig.Bones.Select(static bone => bone.LocalBindPose.ToMatrix()).ToImmutableArray();
}

using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class SecondaryMotionTests
{
    private static readonly string[] BoneNames = ["root", "secondary_anchor", "secondary_tip", "weapon", "camera"];

    [Fact]
    public void ReplaySeekLoopAndFrameRateProduceIdenticalResults()
    {
        SecondaryMotionDefinition definition = Definition();
        var sequential = new SecondaryMotionSession(definition, BoneNames);
        for (int i = 0; i <= 67; i++) sequential.Sample(i / 60.0, Animated);
        SecondaryMotionResult expected = sequential.Sample(1.1234, Animated);
        var direct = new SecondaryMotionSession(definition, BoneNames);
        Assert.Equal(expected.Globals.ToArray(), direct.Sample(1.1234, Animated).Globals.ToArray());
        Assert.Equal(expected.Globals.ToArray(), direct.Sample(1.1234, Animated).Globals.ToArray()); // paused
        direct.Sample(2.5, Animated);
        Assert.Equal(expected.Globals.ToArray(), direct.Sample(1.1234, Animated).Globals.ToArray()); // backward seek
        direct.Sample(0, Animated); // loop
        Assert.Equal(expected.Globals.ToArray(), direct.Sample(1.1234, Animated).Globals.ToArray());
        direct.Reset();
        Assert.Equal(expected.Globals.ToArray(), direct.Sample(1.1234, Animated).Globals.ToArray());
    }

    [Fact]
    public void OnlyDeclaredDynamicBoneChangesAndAnchorsRemainExact()
    {
        SecondaryMotionFrame source = Animated(0.7);
        var session = new SecondaryMotionSession(Definition(), BoneNames);
        SecondaryMotionResult result = session.Sample(0.7, Animated);
        foreach (int index in new[] { 0, 1, 3, 4 }) Assert.Equal(source.Globals[index], result.Globals[index]);
        Assert.NotEqual(source.Globals[2], result.Globals[2]);
        Assert.Equal(Animated(0.7).Globals.ToArray(), source.Globals.ToArray()); // input is immutable
        Assert.Equal((source.ActorWorldTransform * source.Globals[1]).Translation, result.Particles[0].WorldPosition);
        Assert.Contains(result.Diagnostics, d => d.Contains("approximation", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledGroupsLeaveDisplayAndAuthoredHelpersUntouched()
    {
        SecondaryMotionDefinition definition = Definition();
        definition = definition with { Groups = [definition.Groups[0] with { Enabled = false }] };
        var result = new SecondaryMotionSession(definition, BoneNames).Sample(2, Animated);
        Assert.Equal(Animated(2).Globals.ToArray(), result.Globals.ToArray());
        Assert.Empty(result.Particles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingSphereAndCapsuleKeepFreeParticleOutside(bool capsule)
    {
        SecondaryMotionDefinition definition = Definition();
        definition = definition with
        {
            Groups = [definition.Groups[0] with
            {
                Preview = new() { Gravity = Vector3D.Zero, AnimationFollow = 1 },
                Colliders = [new() { BoneName = "root", LocalPosition = new(0, 0.15, 0),
                    EndBoneName = capsule ? "root" : null, EndLocalPosition = new(0, -0.15, 0), Radius = 0.4 }],
            }],
        };
        SecondaryMotionResult result = new SecondaryMotionSession(definition, BoneNames).Sample(0.2, MovingCollider);
        SecondaryMotionFrame frame = MovingCollider(0.2);
        Vector3D a = (frame.ActorWorldTransform * frame.Globals[0]).TransformPoint(new(0, 0.15, 0));
        Vector3D b = capsule ? (frame.ActorWorldTransform * frame.Globals[0]).TransformPoint(new(0, -0.15, 0)) : a;
        Vector3D point = result.Particles[1].WorldPosition;
        Vector3D axis = b - a;
        double t = axis.LengthSquared == 0 ? 0 : Math.Clamp(Vector3D.Dot(point - a, axis) / axis.LengthSquared, 0, 1);
        Assert.True(Vector3D.Distance(point, a + axis * t) >= 0.405 - 1e-9);
    }

    [Fact]
    public void RigidDirectionChangesWithoutChangingBoneScale()
    {
        SecondaryMotionDefinition definition = Definition();
        SecondaryMotionGroup group = definition.Groups[0];
        definition = definition with { Groups = [group with
        {
            Particles = [group.Particles[0], group.Particles[1] with { AimParticleIndex = 2 },
                new() { ReferenceBoneName = "secondary_tip", LocalPosition = new(0, -0.5, 0) }],
            Constraints = [group.Constraints[0], new() { First = 1, Second = 2 }, new() { First = 0, Second = 2, Kind = SecondaryConstraintKind.Bend }],
        }] };
        var result = new SecondaryMotionSession(definition, BoneNames).Sample(1, Animated);
        Assert.True(result.Globals.All(m => m.IsFinite));
        Assert.InRange(Math.Abs(result.Globals[2].LinearDeterminant - Animated(1).Globals[2].LinearDeterminant), 0, 1e-10);
    }

    [Fact]
    public void InvalidNamesDisconnectedParticlesAndDuplicateOutputsAreRejected()
    {
        var d = Definition();
        Assert.Throws<ArgumentException>(() => d.Validate(["different"]));
        Assert.Throws<ArgumentException>(() => (d with { Groups = [d.Groups[0] with { Constraints = [] }] }).Validate());
        Assert.Throws<ArgumentException>(() => (d with { Groups = [d.Groups[0], d.Groups[0] with { Name = "duplicate output" }] }).Validate());
        Assert.Throws<ArgumentException>(() => (d with { Groups = [d.Groups[0] with { Preview = new() { Damping = double.NaN } }] }).Validate());
    }

    [Fact]
    public void ExcessiveOrNonFiniteTimeAndSingularActorAreRejected()
    {
        var session = new SecondaryMotionSession(Definition(), BoneNames);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Sample(double.NaN, Animated));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.Sample(3601, Animated));
        Assert.Throws<ArgumentException>(() => session.Sample(0, t => Animated(t) with { ActorWorldTransform = TransformMatrix.CreateScale(Vector3D.Zero) }));
    }

    [Theory]
    [InlineData(2, 2, 2)]
    [InlineData(0.5, 0.5, 0.5)]
    [InlineData(1, 2, 1)]
    [InlineData(-1, 1, 1)]
    public void ScaledActorPlacementIsRejectedInsteadOfShrinkingExplicitRestLengths(double x, double y, double z)
    {
        var session = new SecondaryMotionSession(Definition(), BoneNames);
        var frame = Animated(0) with { ActorWorldTransform = TransformMatrix.CreateScale(new(x, y, z)) };
        ArgumentException sampled = Assert.Throws<ArgumentException>(() => session.Sample(0, _ => frame));
        Assert.Contains("unit-scale", sampled.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new SecondaryMotionSession(Definition(), BoneNames, frame));
    }

    [Fact]
    public void UnitScaleRigidActorRotationAndTranslationRemainSupported()
    {
        var session = new SecondaryMotionSession(Definition(), BoneNames);
        var result = session.Sample(0.5, t => Animated(t) with
        {
            ActorWorldTransform = TransformMatrix.CreateTranslation(new(2, 3, 4)) * TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, 0.7)),
        });
        Assert.All(result.Globals, matrix => Assert.True(matrix.IsFinite));
    }

    [Fact]
    public void HalfTurnActorRotationKeepsAnchorsExactAndAllOutputsFinite()
    {
        var session = new SecondaryMotionSession(Definition(), BoneNames);
        SecondaryMotionFrame HalfTurn(double seconds) => Animated(seconds) with
        {
            ActorWorldTransform = TransformMatrix.CreateRotation(QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.Min(1, seconds) * Math.PI)),
        };
        for (int i = 0; i <= 120; i++)
        {
            double seconds = i / 120.0;
            SecondaryMotionResult result = session.Sample(seconds, HalfTurn);
            Assert.All(result.Globals, m => Assert.True(m.IsFinite));
            Assert.All(result.Particles, p => Assert.True(p.WorldPosition.IsFinite));
            Assert.Equal((HalfTurn(seconds).ActorWorldTransform * HalfTurn(seconds).Globals[1]).Translation, result.Particles[0].WorldPosition);
            foreach (int helper in new[] { 0, 3, 4 }) Assert.Equal(HalfTurn(seconds).Globals[helper], result.Globals[helper]);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public void AnimatedShapeSpringIsStableAndRestrainsMotionWithoutFreezing(double stiffness)
    {
        var definition = Definition();
        definition = definition with { Groups = [definition.Groups[0] with { Preview = new()
        { RestShapeStiffness = stiffness, Damping = 18, AnimationFollow = 0.1 } }] };
        var session = new SecondaryMotionSession(definition, BoneNames);
        double largestLag = 0;
        for (int i = 0; i <= 600; i++)
        {
            double seconds = i / 120.0;
            var result = session.Sample(seconds, Animated);
            Assert.All(result.Globals, matrix => Assert.True(matrix.IsFinite));
            Assert.Equal(Animated(seconds).Globals[1], result.Globals[1]);
            largestLag = Math.Max(largestLag, Vector3D.Distance(result.Globals[2].Translation, Animated(seconds).Globals[2].Translation));
        }
        Assert.InRange(largestLag, 0.0001, 0.5);
        var first = session.Sample(2.125, Animated);
        session.Reset();
        Assert.Equal(first.Globals.ToArray(), session.Sample(2.125, Animated).Globals.ToArray());
        Assert.Throws<ArgumentException>(() => (definition with { Groups = [definition.Groups[0] with
        { Preview = definition.Groups[0].Preview with { RestShapeStiffness = 1001 } }] }).Validate());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LinkInteriorCollisionDetectsCapsuleBetweenOutsideEndpoints(bool moving, bool anchorFirst)
    {
        string[] names = ["left_anchor", "right_anchor", "left_edge", "right_edge", "body"];
        var definition = new SecondaryMotionDefinition { Groups = [new()
        {
            Name = "panel",
            Particles = [new() { ReferenceBoneName = "left_anchor", Fixed = true }, new() { ReferenceBoneName = "right_anchor", Fixed = true },
                new() { ReferenceBoneName = "left_edge", Fixed = anchorFirst, DrivenBoneName = "left_edge", Radius = 0 },
                new() { ReferenceBoneName = "right_edge", DrivenBoneName = "right_edge", Radius = 0 }],
            Constraints = [new() { First = 0, Second = 2 }, new() { First = 1, Second = 3 }, new() { First = 2, Second = 3 }],
            Colliders = [new() { BoneName = "body", LocalPosition = new(0, -0.5, 0), EndBoneName = "body", EndLocalPosition = new(0, 0.5, 0), Radius = 0.25 }],
            Preview = new() { Gravity = Vector3D.Zero, StructuralStiffness = 0.1, AnimationFollow = 0, Damping = 18 },
        }] };
        SecondaryMotionFrame Pose(double seconds) => new([
            TransformMatrix.CreateTranslation(new(-1, 1, 0)), TransformMatrix.CreateTranslation(new(1, 1, 0)),
            TransformMatrix.CreateTranslation(new(-1, 0, 0)), TransformMatrix.CreateTranslation(new(1, 0, 0)),
            TransformMatrix.CreateTranslation(new(moving ? seconds * 0.2 : 0, 0, 0)),
        ], TransformMatrix.Identity);
        var session = new SecondaryMotionSession(definition, names);
        SecondaryMotionResult result = session.Sample(0.2, Pose);
        Vector3D middle = (result.Particles[2].WorldPosition + result.Particles[3].WorldPosition) * 0.5;
        Vector3D body = Pose(0.2).Globals[4].Translation;
        double distance = Math.Sqrt(Math.Pow(middle.X - body.X, 2) + Math.Pow(middle.Z - body.Z, 2));
        Assert.True(distance >= 0.24, $"Unresolved segment contact: {distance}");
        Assert.Equal(Pose(0.2).Globals[0], result.Globals[0]);
        Assert.Equal(Pose(0.2).Globals[1], result.Globals[1]);
        if (anchorFirst) Assert.Equal(Pose(0.2).Globals[2], result.Globals[2]);
        Assert.All(result.Globals, matrix => Assert.True(matrix.IsFinite));
    }

    [Fact]
    public void ContactNearEmbeddedFixedAnchorCannotLaunchTheFreeEndpoint()
    {
        var definition = Definition();
        definition = definition with { Groups = [definition.Groups[0] with
        {
            Preview = new() { Gravity = Vector3D.Zero, Damping = 10 },
            Colliders = [new() { BoneName = "secondary_anchor", LocalPosition = new(0, -0.002, 0), Radius = 0.12 }],
        }] };
        SecondaryMotionFrame Still(double seconds) => Animated(0);
        var session = new SecondaryMotionSession(definition, BoneNames);
        for (int i = 0; i <= 240; i++)
        {
            var result = session.Sample(i / 120.0, Still);
            Assert.Equal(Still(0).Globals[1], result.Globals[1]);
            Assert.InRange(Vector3D.Distance(result.Globals[2].Translation, Still(0).Globals[2].Translation), 0, 1);
        }
    }

    [Fact]
    public void OptionalBindPrerollIsRepeatableAndNeverChangesPublishedCore()
    {
        SecondaryMotionFrame Display(double time) => Animated(time + 0.35);
        var session = new SecondaryMotionSession(Definition(), BoneNames, Animated(0));
        SecondaryMotionResult first = session.Sample(0, Display);
        foreach (int index in new[] { 0, 1, 3, 4 }) Assert.Equal(Display(0).Globals[index], first.Globals[index]);
        Assert.Equal((Display(0).ActorWorldTransform * Display(0).Globals[1]).Translation, first.Particles[0].WorldPosition);
        session.Sample(1, Display);
        Assert.Equal(first.Globals.ToArray(), session.Sample(0, Display).Globals.ToArray());
        var independent = new SecondaryMotionSession(Definition(), BoneNames, Animated(0));
        Assert.Equal(first.Globals.ToArray(), independent.Sample(0, Display).Globals.ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => new SecondaryMotionSession(Definition(), BoneNames, Animated(0), 3));
        Assert.Throws<ArgumentException>(() => new SecondaryMotionSession(Definition(), BoneNames, Animated(0), parentIndices: [-1, 0]));
        var disabled = new SecondaryMotionSession(Definition(), BoneNames, Animated(0), initializationSeconds: 0);
        var noInitializer = new SecondaryMotionSession(Definition(), BoneNames);
        Assert.Equal(noInitializer.Sample(0.2, Display).Globals.ToArray(), disabled.Sample(0.2, Display).Globals.ToArray());
    }

    internal static SecondaryMotionDefinition Definition() => new()
    {
        Groups = [new()
        {
            Name = "fabric",
            Particles = [new() { ReferenceBoneName = "secondary_anchor", DrivenBoneName = "secondary_anchor", Fixed = true },
                new() { ReferenceBoneName = "secondary_tip", DrivenBoneName = "secondary_tip" }],
            Constraints = [new() { First = 0, Second = 1 }],
            Preview = new() { AnimationFollow = 0.1 },
        }],
    };

    private static SecondaryMotionFrame Animated(double seconds)
    {
        double x = 0.5 * Math.Sin(seconds * 4);
        TransformMatrix root = TransformMatrix.CreateTranslation(new(x, 1, 0));
        return new([TransformMatrix.Identity, root, root * TransformMatrix.CreateTranslation(new(0, -1, 0)),
            TransformMatrix.CreateTranslation(new(1, 2, 3)), TransformMatrix.CreateTranslation(new(-1, 2, 3))],
            TransformMatrix.CreateTranslation(new(seconds * 0.1, 0, 0)));
    }

    private static SecondaryMotionFrame MovingCollider(double seconds)
    {
        SecondaryMotionFrame pose = Animated(seconds);
        return pose with { Globals = pose.Globals.SetItem(0, TransformMatrix.CreateTranslation(new(0.2 * seconds, 0, 0))) };
    }
}

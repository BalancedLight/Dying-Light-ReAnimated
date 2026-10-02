using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class OpenDynamicsCollisionBackendTests
{
    [Fact]
    public void EmbeddedOdePerformsSphereCapsuleShaftAndCapQueries()
    {
        OpenDynamicsCollisionBackend backend = OpenDynamicsCollisionBackend.CreateRequired();
        Assert.Contains("ODE 0.16.6", backend.Identity, StringComparison.Ordinal);
        Assert.True(backend.IsNative);

        AssertContact(backend, new(1.4, 0, 0), .5, Vector3D.Zero, Vector3D.Zero, 1, Vector3D.UnitX, .1);
        Assert.False(backend.TryContact(new(2, 0, 0), .5, Vector3D.Zero, Vector3D.Zero, 1,
            Vector3D.UnitX, out _));
        AssertContact(backend, new(.7, 0, 0), .3, new(0, 0, -1), new(0, 0, 1), .5, Vector3D.UnitX, .1);
        AssertContact(backend, new(0, 0, 1.7), .4, new(0, 0, -1), new(0, 0, 1), .5, Vector3D.UnitZ, .2);
        Assert.Equal(4, backend.NativeContactQueryCount);
    }

    [Fact]
    public void CapsuleCenterlineRejectsAnAxialNormalHint()
    {
        OpenDynamicsCollisionBackend backend = OpenDynamicsCollisionBackend.CreateRequired();
        Vector3D start = new(0, 0, -1), end = new(0, 0, 1);
        Assert.True(backend.TryContact(Vector3D.Zero, .2, start, end, .5, Vector3D.UnitZ, out var contact));
        Assert.InRange(Math.Abs(Vector3D.Dot(contact.Normal, Vector3D.UnitZ)), 0, 1e-8);
        Assert.InRange(Math.Abs(contact.PenetrationDepth - .7), 0, 1e-8);
        Assert.False(backend.TryContact(contact.Normal * (contact.PenetrationDepth + 1e-6), .2,
            start, end, .5, Vector3D.UnitZ, out _));
        Assert.Equal(2, backend.NativeContactQueryCount);
    }

    [Fact]
    public void InvalidInputIsRejectedBeforeCallingNativeBackend()
    {
        OpenDynamicsCollisionBackend backend = OpenDynamicsCollisionBackend.CreateRequired();
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.TryContact(
            new(double.NaN, 0, 0), .1, Vector3D.Zero, Vector3D.Zero, .5, Vector3D.UnitX, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => backend.TryContact(
            Vector3D.Zero, .1, Vector3D.Zero, Vector3D.Zero, 0, Vector3D.UnitX, out _));
        Assert.Equal(0, backend.NativeContactQueryCount);
    }

    [Fact]
    public void SecondaryMotionSessionReportsAndUsesRequiredNativeBackend()
    {
        OpenDynamicsCollisionBackend backend = OpenDynamicsCollisionBackend.CreateRequired();
        TransformMatrix[] globals =
        [
            TransformMatrix.CreateTranslation(new(0, 2, 0)),
            TransformMatrix.CreateTranslation(new(0, 0, -.1)),
            TransformMatrix.Identity,
        ];
        var frame = new SecondaryMotionFrame(globals.ToImmutableArray(), TransformMatrix.Identity);
        var definition = new SecondaryMotionDefinition
        {
            Groups = [new SecondaryMotionGroup
            {
                Name = "native-contact",
                Particles =
                [
                    new() { ReferenceBoneName = "anchor", Fixed = true, Radius = 0 },
                    new() { ReferenceBoneName = "free", DrivenBoneName = "free", Radius = 0 },
                ],
                Constraints = [new() { First = 0, Second = 1, Kind = SecondaryConstraintKind.Bend }],
                Colliders = [new() { BoneName = "body", EndBoneName = "body", LocalPosition = new(-1, 0, 0), EndLocalPosition = new(1, 0, 0), Radius = .25 }],
                Preview = new() { Gravity = Vector3D.Zero, BendStiffness = 0, StructuralStiffness = 0, AnimationFollow = 0 },
            }],
        };
        var session = new SecondaryMotionSession(definition, ["anchor", "free", "body"],
            collisionBackend: backend, requireOpenDynamicsCollisionBackend: true);

        SecondaryMotionResult result = session.Sample(1.0 / SecondaryMotionSession.StepsPerSecond, _ => frame);

        Assert.True(result.UsesOpenDynamicsEngine);
        Assert.Contains("ODE 0.16.6", result.CollisionBackendIdentity, StringComparison.Ordinal);
        Assert.True(result.NativeContactQueryCount > 0);
        Assert.Equal(backend.NativeContactQueryCount, result.NativeContactQueryCount);
    }

    private static void AssertContact(
        OpenDynamicsCollisionBackend backend,
        Vector3D particle,
        double particleRadius,
        Vector3D start,
        Vector3D end,
        double colliderRadius,
        Vector3D expectedNormal,
        double expectedDepth)
    {
        Assert.True(backend.TryContact(particle, particleRadius, start, end, colliderRadius,
            expectedNormal, out SecondaryCollisionContact contact));
        Assert.InRange(Vector3D.Distance(contact.Normal, expectedNormal), 0, 1e-8);
        Assert.InRange(Math.Abs(contact.PenetrationDepth - expectedDepth), 0, 1e-8);
    }
}

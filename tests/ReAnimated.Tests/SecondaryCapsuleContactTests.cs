using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class SecondaryCapsuleContactTests
{
    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(1, 2, 3)]
    public void OnAxisParticleExitsCapsuleWithoutMovingItsFixedAnchor(double x, double y, double z)
    {
        Vector3D axis = new Vector3D(x, y, z).Normalized();
        var definition = Definition(-axis, axis);
        var session = new SecondaryMotionSession(definition, ["anchor", "free", "body"]);
        var frame = Pose();
        var result = session.Sample(1.0 / SecondaryMotionSession.StepsPerSecond, _ => frame);
        Vector3D particle = result.Particles[1].WorldPosition;
        Vector3D closest = axis * Math.Clamp(Vector3D.Dot(particle, axis), -1, 1);
        Assert.InRange(Vector3D.Distance(particle, closest), .25 - 1e-10, .25 + 1e-10);
        Assert.Equal(frame.Globals[0].Translation, result.Particles[0].WorldPosition);
        Assert.Equal(frame.Globals[0], result.Globals[0]);
        Assert.Equal(frame.Globals[2], result.Globals[2]);
        Assert.True(result.Globals[1].IsFinite);
        session.Reset();
        Assert.Equal(particle, session.Sample(1.0 / SecondaryMotionSession.StepsPerSecond, _ => frame).Particles[1].WorldPosition);
    }

    [Fact]
    public void ZeroLengthCapsuleUsesSphereFallback()
    {
        var session = new SecondaryMotionSession(Definition(Vector3D.Zero, Vector3D.Zero), ["anchor", "free", "body"]);
        var result = session.Sample(.1, _ => Pose());
        Assert.InRange(result.Particles[1].WorldPosition.Length, .25 - 1e-10, .25 + 1e-10);
    }

    [Fact]
    public void OffAxisContactKeepsItsOutwardSideWithParticleRadius()
    {
        var definition = Definition(-Vector3D.UnitX, Vector3D.UnitX);
        definition = definition with { Groups = [definition.Groups[0] with
        { Particles = [definition.Groups[0].Particles[0], definition.Groups[0].Particles[1] with { Radius = .05 }] }] };
        var frame = Pose() with { Globals = [Pose().Globals[0], TransformMatrix.CreateTranslation(new(0, 0, -.1)), TransformMatrix.Identity] };
        var result = new SecondaryMotionSession(definition, ["anchor", "free", "body"]).Sample(1.0 / SecondaryMotionSession.StepsPerSecond, _ => frame);
        Assert.InRange(result.Particles[1].WorldPosition.Z, -.3 - 1e-10, -.3 + 1e-10);
    }

    private static SecondaryMotionFrame Pose() => new([TransformMatrix.CreateTranslation(new(0, 2, 0)), TransformMatrix.Identity, TransformMatrix.Identity], TransformMatrix.Identity);
    private static SecondaryMotionDefinition Definition(Vector3D start, Vector3D end) => new()
    {
        Groups = [new()
        {
            Name = "contact-control",
            Particles = [new() { ReferenceBoneName = "anchor", Fixed = true, Radius = 0 }, new() { ReferenceBoneName = "free", DrivenBoneName = "free", Radius = 0 }],
            Constraints = [new() { First = 0, Second = 1, Kind = SecondaryConstraintKind.Bend }],
            Colliders = [new() { BoneName = "body", EndBoneName = "body", LocalPosition = start, EndLocalPosition = end, Radius = .25 }],
            Preview = new() { Gravity = Vector3D.Zero, BendStiffness = 0, StructuralStiffness = 0, AnimationFollow = 0 },
        }],
    };
}

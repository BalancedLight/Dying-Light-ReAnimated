using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Evaluation;

public static class ActorMotionEvaluator
{
    public static TransformMatrix Evaluate(
        AuxiliaryTransformTrack track,
        double frame,
        Vector3D? worldUpAxis = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        Vector3D up = worldUpAxis ?? Vector3D.UnitY;
        if (!up.TryNormalize(out Vector3D normalizedUp))
        {
            throw new ArgumentException(
                "Actor motion requires a non-zero world-up axis.",
                nameof(worldUpAxis));
        }

        TransformTRS first = track.Sample(0.0);
        TransformTRS current = track.Sample(frame);
        QuaternionD delta =
            (current.Rotation * first.Rotation.Inverse()).Normalized();
        QuaternionD yaw = ExtractTwist(delta, normalizedUp);
        return TransformMatrix.FromTrs(new TransformTRS(
            current.Translation - first.Translation,
            yaw,
            Vector3D.One));
    }

    private static QuaternionD ExtractTwist(
        QuaternionD rotation,
        Vector3D axis)
    {
        QuaternionD unit = rotation.Normalized();
        Vector3D vector = new(unit.X, unit.Y, unit.Z);
        Vector3D projected = axis * Vector3D.Dot(vector, axis);
        var twist = new QuaternionD(
            projected.X,
            projected.Y,
            projected.Z,
            unit.W);
        return twist.LengthSquared <= 1e-20
            ? QuaternionD.Identity
            : twist.Normalized();
    }
}

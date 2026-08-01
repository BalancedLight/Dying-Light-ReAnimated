namespace ReAnimated.Core.Mathematics;

/// <summary>
/// A local authoring transform using translation, an XYZW quaternion, and scale.
/// </summary>
public readonly record struct TransformTRS(
    Vector3D Translation,
    QuaternionD Rotation,
    Vector3D Scale)
{
    public static TransformTRS Identity =>
        new(Vector3D.Zero, QuaternionD.Identity, Vector3D.One);

    public bool IsFinite =>
        Translation.IsFinite &&
        Rotation.IsFinite &&
        Scale.IsFinite;

    public TransformTRS Normalized() =>
        new(Translation, Rotation.Normalized(), Scale);

    public TransformMatrix ToMatrix() => TransformMatrix.FromTrs(this);

    public static TransformTRS Interpolate(
        TransformTRS from,
        TransformTRS to,
        double amount) =>
        new(
            Vector3D.Lerp(from.Translation, to.Translation, amount),
            QuaternionD.Slerp(from.Rotation, to.Rotation, amount),
            Vector3D.Lerp(from.Scale, to.Scale, amount));
}

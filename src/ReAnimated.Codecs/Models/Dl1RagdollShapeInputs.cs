using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Codecs.Models;

public enum Dl1RagdollShapeKind { Sphere, Capsule, Box }
public enum Dl1RagdollShapeAxis { X, Y, Z }

public sealed record Dl1RagdollShapeInputs(
    Dl1RagdollShapeKind Shape,
    Vector3D PaddedSpans,
    Dl1RagdollShapeAxis? CapsuleAxis,
    double? Radius,
    double? CylinderLength);

/// <summary>Scalar shape inputs from min/max corner spans already expressed in the bone frame.</summary>
public static class Dl1RagdollShapeInputCalculator
{
    public static ImmutableArray<string> SupportedShapeTokens { get; } = ["box", "capsule", "sphere"];
    public static Dl1RagdollShapeInputs Calculate(
        Dl1RagdollShapeKind shape, Vector3D boneFrameSpans, double scale = 1)
    {
        if (!Enum.IsDefined(shape))
            throw new ArgumentOutOfRangeException(nameof(shape));
        if (!boneFrameSpans.IsFinite || boneFrameSpans.X < 0 ||
            boneFrameSpans.Y < 0 || boneFrameSpans.Z < 0)
            throw new ArgumentException("Bone-frame spans must be finite and nonnegative.", nameof(boneFrameSpans));
        if (!double.IsFinite(scale) || scale < 0 || !float.IsFinite((float)scale))
            throw new ArgumentOutOfRangeException(nameof(scale), "Scale must be finite and nonnegative.");

        float multiplier = (float)scale;
        float x = Padded(boneFrameSpans.X, multiplier);
        float y = Padded(boneFrameSpans.Y, multiplier);
        float z = Padded(boneFrameSpans.Z, multiplier);
        var spans = new Vector3D(x, y, z);
        float longest = Math.Max(Math.Max(x, y), z);
        if (shape == Dl1RagdollShapeKind.Box)
            return new(shape, spans, null, null, null);
        if (shape == Dl1RagdollShapeKind.Sphere)
            return new(shape, spans, null, longest * .5f, null);

        // Equal maximum spans choose X first, then Y, then Z.
        Dl1RagdollShapeAxis axis = longest == x ? Dl1RagdollShapeAxis.X :
            longest == y ? Dl1RagdollShapeAxis.Y : Dl1RagdollShapeAxis.Z;
        float radius = .5f * (axis switch
        {
            Dl1RagdollShapeAxis.X => Math.Max(y, z),
            Dl1RagdollShapeAxis.Y => Math.Max(x, z),
            _ => Math.Max(x, y),
        });
        float length = Math.Max(longest - radius, 0);
        if (multiplier > 0) length /= multiplier;
        length += .08f;
        if (!float.IsFinite(length))
            throw new ArgumentOutOfRangeException(nameof(scale), "The capsule length exceeds the supported range.");
        return new(shape, spans, axis, radius, length);
    }

    public static ImmutableArray<double> NormalizeMasses(
        IReadOnlyList<int> declaredWeights, double totalMass)
    {
        ArgumentNullException.ThrowIfNull(declaredWeights);
        if (!double.IsFinite(totalMass) || totalMass < 0 || !float.IsFinite((float)totalMass))
            throw new ArgumentOutOfRangeException(nameof(totalMass));
        float total = 0;
        foreach (int weight in declaredWeights) total += Math.Max((float)weight, 1);
        if (!float.IsFinite(total))
            throw new ArgumentException("The relative weights exceed the supported range.", nameof(declaredWeights));
        var result = ImmutableArray.CreateBuilder<double>(declaredWeights.Count);
        foreach (int weight in declaredWeights)
        {
            float numerator = Math.Max((float)weight, 1) * (float)totalMass;
            float value = total > .01f ? numerator / total : 1;
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(totalMass), "A normalized mass exceeds the supported range.");
            result.Add(value);
        }
        return result.MoveToImmutable();
    }

    private static float Padded(double span, float multiplier)
    {
        float single = (float)span;
        float result = single * multiplier + .01f;
        if (!float.IsFinite(single) || !float.IsFinite(result))
            throw new ArgumentOutOfRangeException(nameof(span), "A shape span exceeds the supported range.");
        return result;
    }
}

using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

/// <summary>Wrist and metacarpal-base positions in authoring-model coordinates.</summary>
public sealed record RigPalmLandmarks(
    Vector3D Wrist,
    Vector3D IndexBase,
    Vector3D MiddleBase,
    Vector3D RingBase,
    Vector3D LittleBase)
{
    public static RigPalmLandmarks FromRolePositions(
        IReadOnlyDictionary<string, Vector3D> positions,
        RigHandSide side)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        string suffix = side.ToString().ToLowerInvariant();
        Vector3D Read(string role) => positions.TryGetValue(role, out Vector3D position)
            ? position
            : throw new InvalidOperationException($"Grip placement requires the '{role}' hand landmark.");
        return new(Read($"hand.{suffix}"), Read($"finger.{suffix}.index.1"),
            Read($"finger.{suffix}.middle.1"), Read($"finger.{suffix}.ring.1"),
            Read($"finger.{suffix}.little.1"));
    }

    public static RigPalmLandmarks FromHandSetup(RiggingSession session, RigHandSide side)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!Enum.IsDefined(side)) throw new ArgumentOutOfRangeException(nameof(side));
        RigHandSetup setup = session.Hands.SingleOrDefault(hand => hand.Side == side)
            ?? throw new InvalidOperationException($"Set up the {side.ToString().ToLowerInvariant()} hand before placing its grip.");
        var guides = session.Landmarks.ToDictionary(static guide => guide.Id);
        setup.Validate(guides);
        Vector3D ReadFinger(string digit)
        {
            RigFingerDeclaration? finger = setup.Fingers.SingleOrDefault(finger => finger.Id == digit);
            if (finger is null || finger.Presence != RigFingerPresence.Present || finger.JointGuideIds.IsEmpty)
                throw new InvalidOperationException($"Grip placement requires the {digit} finger's base guide.");
            return guides[finger.JointGuideIds[0]].Position;
        }
        return new(guides[setup.WristGuideId].Position, ReadFinger("index"), ReadFinger("middle"),
            ReadFinger("ring"), ReadFinger("little"));
    }
}

public sealed record RigGripPlacementSolution(
    TransformMatrix SourcePalmFrame,
    TransformMatrix ReferencePalmFrame,
    double HandScale,
    TransformMatrix GlobalFrame,
    TransformMatrix LocalFrame);

/// <summary>Transfers a reference grip into the current model's palm without changing its anatomy.</summary>
public static class RigGripPlacementSolver
{
    private const double DirectionEpsilon = 1e-10;

    public static RigGripPlacementSolution Solve(
        RigPalmLandmarks source,
        RigPalmLandmarks reference,
        TransformMatrix referenceGripGlobal,
        TransformMatrix sourceHandGlobal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reference);
        RigRecipeRules.Affine(referenceGripGlobal, nameof(referenceGripGlobal));
        RigRecipeRules.Affine(sourceHandGlobal, nameof(sourceHandGlobal));
        (TransformMatrix sourcePalm, double sourceLength) = PalmFrame(source, "source");
        (TransformMatrix referencePalm, double referenceLength) = PalmFrame(reference, "reference");
        double scale = sourceLength / referenceLength;
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentException("The source and reference palms must have usable finite dimensions.");

        TransformMatrix rotation = WithoutTranslation(sourcePalm) * WithoutTranslation(referencePalm).InvertedAffine();
        TransformMatrix desired = rotation * RigidFrame(referenceGripGlobal);
        Vector3D position = source.Wrist +
            rotation.TransformDirection(referenceGripGlobal.Translation - reference.Wrist) * scale;
        desired = desired with { M14 = position.X, M24 = position.Y, M34 = position.Z };
        TransformMatrix local = sourceHandGlobal.InvertedAffine() * desired;
        RigRecipeRules.Affine(local, nameof(local));
        return new(sourcePalm, referencePalm, scale, desired, local);
    }

    private static (TransformMatrix Frame, double Length) PalmFrame(RigPalmLandmarks palm, string label)
    {
        if (!palm.Wrist.IsFinite || !palm.IndexBase.IsFinite || !palm.MiddleBase.IsFinite ||
            !palm.RingBase.IsFinite || !palm.LittleBase.IsFinite)
            throw new ArgumentException($"The {label} palm landmarks must be finite model-space positions.");
        Vector3D forward = (palm.IndexBase + palm.MiddleBase + palm.RingBase) / 3 - palm.Wrist;
        if (!forward.TryNormalize(out Vector3D x, DirectionEpsilon))
            throw new ArgumentException($"The {label} wrist and finger bases do not define a palm direction.");
        Vector3D across = palm.IndexBase - palm.LittleBase;
        across -= x * Vector3D.Dot(across, x);
        if (!across.TryNormalize(out Vector3D y, DirectionEpsilon))
            throw new ArgumentException($"The {label} finger bases are collinear; adjust the index and little-finger guides.");
        Vector3D z = Vector3D.Cross(x, y).Normalized();
        return (Frame(x, y, z, palm.Wrist), forward.Length);
    }

    private static TransformMatrix RigidFrame(TransformMatrix frame)
    {
        Vector3D first = new(frame.M11, frame.M21, frame.M31);
        Vector3D second = new(frame.M12, frame.M22, frame.M32);
        if (!first.TryNormalize(out Vector3D x, DirectionEpsilon))
            throw new ArgumentException("The reference grip has no usable first axis.", nameof(frame));
        second -= x * Vector3D.Dot(second, x);
        if (!second.TryNormalize(out Vector3D y, DirectionEpsilon))
            throw new ArgumentException("The reference grip axes are collinear.", nameof(frame));
        return Frame(x, y, Vector3D.Cross(x, y).Normalized(), frame.Translation);
    }

    private static TransformMatrix Frame(Vector3D x, Vector3D y, Vector3D z, Vector3D origin) =>
        new(x.X, y.X, z.X, origin.X, x.Y, y.Y, z.Y, origin.Y,
            x.Z, y.Z, z.Z, origin.Z, 0, 0, 0, 1);

    private static TransformMatrix WithoutTranslation(TransformMatrix frame) =>
        frame with { M14 = 0, M24 = 0, M34 = 0 };
}

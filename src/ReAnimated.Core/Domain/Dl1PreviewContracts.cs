using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

/// <summary>
/// Stable DL1 names and roles observed in the retail player-camera path.
/// These identify contracts only; they do not claim that editor-side
/// procedural behavior is game validated.
/// </summary>
public static class Dl1PreviewContract
{
    public const string HSpineBoneName = "HSpine";

    public const string HSpine1BoneName = "HSpine1";

    public const string HSpineSemanticRole = "body.spine.base";

    public const string HSpine1SemanticRole = "body.spine.camera";

    public const string EyeCameraBoneName = "EyeCamera";

    public const string ReferenceCameraBoneName = "RefCamera";

    public const string EyeCameraSemanticRole = "camera.eye";

    public const string ReferenceCameraSemanticRole = "camera.reference";

    public const string EyeCameraHelperRole = "fpp.eye_camera";

    public const string ReferenceCameraHelperRole = "fpp.reference_camera";
}

/// <summary>
/// Public identifiers used by profiles, stage reports, and future UI toggles.
/// </summary>
public static class Dl1PreviewStageIds
{
    /// <summary>
    /// Explicit marker for a profile that disables every optional procedural
    /// stage. Empty toggle inventories retain the legacy third-party-stage
    /// behavior.
    /// </summary>
    public const string NoProceduralStages = "no_procedural_stages";

    public const string FppCameraHelpers = "fpp_camera_helpers";

    public const string FppViewTransform = "fpp_view_transform";

    public const string FppSceneProjection = "fpp_scene_projection";

    public const string FppHandsProjection = "hands_projection";

    /// <summary>
    /// Legacy load-compatibility alias grouping the bounded HSpine basis
    /// correction and the separate runtime-dependent head-position correction.
    /// New profiles persist the two concrete stage identifiers independently.
    /// </summary>
    public const string FppHeadSpineCorrection = "head_spine_correction";

    public const string FppHSpineBasisCorrection =
        "hspine_basis_correction";

    public const string FppHeadPositionCorrection =
        "head_position_correction";

    public const string FppHandInertia = "hand_inertia";

    public const string MovieReferenceCamera = "movie_reference_camera";

    public static bool IsBuiltIn(string stageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageId);
        return stageId is
            NoProceduralStages or
            FppCameraHelpers or
            FppViewTransform or
            FppSceneProjection or
            FppHandsProjection or
            FppHeadSpineCorrection or
            FppHSpineBasisCorrection or
            FppHeadPositionCorrection or
            FppHandInertia or
            MovieReferenceCamera;
    }
}

public enum Dl1ProjectionFovAxis
{
    Horizontal,
    Vertical,
}

public enum Dl1ProjectionFarPlane
{
    Finite,
    Infinite,
}

/// <summary>
/// Projection parameters that can represent DL1's separate infinite-far hands
/// projection without coercing it into the renderer's finite scene lens.
/// </summary>
public readonly record struct Dl1ProjectionParameters
{
    [JsonConstructor]
    public Dl1ProjectionParameters(
        double fieldOfViewDegrees,
        Dl1ProjectionFovAxis fieldOfViewAxis,
        double aspectRatio,
        double nearClipMeters,
        Dl1ProjectionFarPlane farPlane,
        double? farClipMeters = null)
    {
        if (!double.IsFinite(fieldOfViewDegrees) ||
            fieldOfViewDegrees <= 1.0 ||
            fieldOfViewDegrees >= 179.0)
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewDegrees));
        }

        if (!Enum.IsDefined(fieldOfViewAxis))
        {
            throw new ArgumentOutOfRangeException(nameof(fieldOfViewAxis));
        }

        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        }

        if (!double.IsFinite(nearClipMeters) || nearClipMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nearClipMeters));
        }

        if (!Enum.IsDefined(farPlane))
        {
            throw new ArgumentOutOfRangeException(nameof(farPlane));
        }

        if (farPlane == Dl1ProjectionFarPlane.Infinite)
        {
            if (farClipMeters is not null)
            {
                throw new ArgumentException(
                    "An infinite projection cannot declare a finite far clip.",
                    nameof(farClipMeters));
            }
        }
        else if (farClipMeters is not double finiteFar ||
                 !double.IsFinite(finiteFar) ||
                 finiteFar <= nearClipMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(farClipMeters));
        }

        FieldOfViewDegrees = fieldOfViewDegrees;
        FieldOfViewAxis = fieldOfViewAxis;
        AspectRatio = aspectRatio;
        NearClipMeters = nearClipMeters;
        FarPlane = farPlane;
        FarClipMeters = farClipMeters;
    }

    public double FieldOfViewDegrees { get; }

    public Dl1ProjectionFovAxis FieldOfViewAxis { get; }

    public double AspectRatio { get; }

    public double NearClipMeters { get; }

    public Dl1ProjectionFarPlane FarPlane { get; }

    public double? FarClipMeters { get; }
}

/// <summary>
/// Explicit externally supplied FPP projection state. DL1 obtains the scene
/// FOV/aspect/near and separate hands values from runtime player/camera state;
/// the finite scene far clip remains an editor culling boundary and is not a
/// claim about DL1. The authoring core deliberately has no guessed numeric
/// defaults for the runtime-derived values.
/// </summary>
public sealed record Dl1FppProjectionSnapshot
{
    [JsonConstructor]
    public Dl1FppProjectionSnapshot(
        CameraLens sceneCameraLens,
        Dl1ProjectionParameters handsProjection)
    {
        if (handsProjection.FarPlane != Dl1ProjectionFarPlane.Infinite)
        {
            throw new ArgumentException(
                "DL1's FPP hands projection uses an infinite far plane.",
                nameof(handsProjection));
        }

        SceneCameraLens = sceneCameraLens;
        HandsProjection = handsProjection;
    }

    public CameraLens SceneCameraLens { get; }

    public Dl1ProjectionParameters HandsProjection { get; }
}

/// <summary>
/// Snapshot of the external IBaseCamera registered as a movie reference camera.
/// It is intentionally distinct from a player-rig bone named RefCamera.
/// </summary>
public sealed record Dl1MovieReferenceCameraSnapshot
{
    [JsonConstructor]
    public Dl1MovieReferenceCameraSnapshot(
        TransformMatrix worldTransform,
        CameraLens lens)
    {
        if (!worldTransform.IsFinite ||
            Math.Abs(worldTransform.LinearDeterminant) <= 1e-12)
        {
            throw new ArgumentException(
                "The movie reference camera transform must be finite and non-singular.",
                nameof(worldTransform));
        }

        WorldTransform = worldTransform;
        Lens = lens;
    }

    public TransformMatrix WorldTransform { get; }

    public CameraLens Lens { get; }
}

/// <summary>
/// Explicit state required by the bounded DL1 1.55 HSpine/HSpine1 preview
/// correction. The directions describe the same world-up, model-left, and
/// model-forward inputs consumed by the retail functions. They are normalized
/// on construction and must form an orthogonal basis; no editor or animation
/// transform is silently treated as the live model basis.
/// </summary>
public sealed record Dl1FppBodyCorrectionSnapshot
{
    private const double BasisTolerance = 1e-5;

    [JsonConstructor]
    public Dl1FppBodyCorrectionSnapshot(
        Vector3D worldUp,
        Vector3D modelLeft,
        Vector3D modelForward,
        bool vehicleControllerActive)
    {
        WorldUp = NormalizeDirection(worldUp, nameof(worldUp));
        ModelLeft = NormalizeDirection(modelLeft, nameof(modelLeft));
        ModelForward = NormalizeDirection(
            modelForward,
            nameof(modelForward));
        if (Math.Abs(Vector3D.Dot(WorldUp, ModelLeft)) > BasisTolerance ||
            Math.Abs(Vector3D.Dot(WorldUp, ModelForward)) > BasisTolerance ||
            Math.Abs(Vector3D.Dot(ModelLeft, ModelForward)) > BasisTolerance)
        {
            throw new ArgumentException(
                "The FPP body-correction directions must form an orthogonal basis.");
        }

        VehicleControllerActive = vehicleControllerActive;
    }

    public Vector3D WorldUp { get; }

    public Vector3D ModelLeft { get; }

    public Vector3D ModelForward { get; }

    public bool VehicleControllerActive { get; }

    private static Vector3D NormalizeDirection(
        Vector3D value,
        string parameterName)
    {
        if (!value.IsFinite || !value.TryNormalize(out Vector3D normalized))
        {
            throw new ArgumentException(
                "The FPP body-correction direction must be finite and non-zero.",
                parameterName);
        }

        return normalized;
    }
}

/// <summary>
/// Optional runtime-derived evidence supplied to preview evaluation. Missing
/// values remain unavailable and are surfaced through stage diagnostics.
/// </summary>
public sealed record Dl1PreviewInputs
{
    [JsonConstructor]
    public Dl1PreviewInputs(
        Dl1FppProjectionSnapshot? fppProjection = null,
        Dl1MovieReferenceCameraSnapshot? movieReferenceCamera = null,
        Dl1FppBodyCorrectionSnapshot? fppBodyCorrection = null)
    {
        FppProjection = fppProjection;
        MovieReferenceCamera = movieReferenceCamera;
        FppBodyCorrection = fppBodyCorrection;
    }

    public Dl1FppProjectionSnapshot? FppProjection { get; }

    public Dl1MovieReferenceCameraSnapshot? MovieReferenceCamera { get; }

    public Dl1FppBodyCorrectionSnapshot? FppBodyCorrection { get; }

    public static Dl1PreviewInputs Empty { get; } = new();
}

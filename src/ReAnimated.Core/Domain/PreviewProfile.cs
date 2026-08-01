using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.Domain;

[Flags]
public enum AuthoringPreviewFidelity
{
    None = 0,
    Bones = 1 << 0,
    Skinning = 1 << 1,
    MorphTargets = 1 << 2,
    Timing = 1 << 3,
    Camera = 1 << 4,
    RootMotion = 1 << 5,
    FirstPersonOcclusion = 1 << 6,
    AuthoringAccurate =
        Bones |
        Skinning |
        MorphTargets |
        Timing |
        Camera |
        RootMotion,
}

public enum PreviewViewMode
{
    ThirdPerson,
    FirstPerson,
    Split,
}

/// <summary>
/// Surface styling is intentionally independent from animation fidelity.
/// Dying Light shader parity is not part of the authoring contract.
/// </summary>
public enum PreviewVisualStyle
{
    UnlitDiagnostic,
    MaterialApproximation,
}

/// <summary>
/// Evidence level for preview behavior, independent from enabled authoring features.
/// </summary>
public enum PreviewFidelityTier
{
    Raw,
    Dl1Profile,
    GameValidated,
}

public enum Dl1PreviewContext
{
    Raw,
    Dl1Body,
    Dl1Fpp,
    Dl1Movie,
}

public readonly record struct CameraLens
{
    [JsonConstructor]
    public CameraLens(
        double verticalFieldOfViewDegrees,
        double aspectRatio,
        double nearClipMeters,
        double farClipMeters)
    {
        if (!double.IsFinite(verticalFieldOfViewDegrees) ||
            verticalFieldOfViewDegrees <= 1.0 ||
            verticalFieldOfViewDegrees >= 179.0)
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFieldOfViewDegrees));
        }

        if (!double.IsFinite(aspectRatio) || aspectRatio <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio));
        }

        if (!double.IsFinite(nearClipMeters) || nearClipMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(nearClipMeters));
        }

        if (!double.IsFinite(farClipMeters) || farClipMeters <= nearClipMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(farClipMeters));
        }

        VerticalFieldOfViewDegrees = verticalFieldOfViewDegrees;
        AspectRatio = aspectRatio;
        NearClipMeters = nearClipMeters;
        FarClipMeters = farClipMeters;
    }

    public double VerticalFieldOfViewDegrees { get; }

    public double AspectRatio { get; }

    public double NearClipMeters { get; }

    public double FarClipMeters { get; }

    public static CameraLens Default =>
        new(60.0, 16.0 / 9.0, 0.01, 1000.0);
}

/// <summary>
/// Renderer-facing preview settings that never alter exportable authored pose.
/// </summary>
public sealed record PreviewProfile
{
    [JsonConstructor]
    public PreviewProfile(
        string id,
        PreviewViewMode viewMode,
        AuthoringPreviewFidelity fidelity,
        PreviewVisualStyle visualStyle,
        string? cameraBoneName,
        CameraLens cameraLens,
        TransformTRS cameraOffset,
        PreviewFidelityTier fidelityTier = PreviewFidelityTier.Raw,
        Dl1PreviewContext context = Dl1PreviewContext.Raw,
        int profileVersion = 1,
        string? buildFingerprint = null,
        ImmutableArray<string> proceduralToggles = default,
        double morphActivationThreshold = 0,
        int? maximumActiveMorphTargets = null,
        bool clampMorphWeightsToRigBounds = false,
        string? captureFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!cameraOffset.IsFinite)
        {
            throw new ArgumentException("The camera offset must be finite.", nameof(cameraOffset));
        }

        if (viewMode is PreviewViewMode.FirstPerson or PreviewViewMode.Split &&
            context != Dl1PreviewContext.Dl1Movie &&
            string.IsNullOrWhiteSpace(cameraBoneName))
        {
            throw new ArgumentException(
                "First-person and split profiles require a camera bone unless they use an external DL1 movie reference camera.",
                nameof(cameraBoneName));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(profileVersion);
        ImmutableArray<string> toggles = (proceduralToggles.IsDefault
                ? []
                : proceduralToggles)
            .Where(static toggle => !string.IsNullOrWhiteSpace(toggle))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        if (buildFingerprint is not null &&
            !IsSha256Fingerprint(buildFingerprint))
        {
            throw new ArgumentException(
                "Build fingerprints must be 64 hexadecimal SHA-256 characters.",
                nameof(buildFingerprint));
        }

        if (captureFingerprint is not null &&
            !IsSha256Fingerprint(captureFingerprint))
        {
            throw new ArgumentException(
                "Capture fingerprints must be 64 hexadecimal SHA-256 characters.",
                nameof(captureFingerprint));
        }

        if (fidelityTier == PreviewFidelityTier.GameValidated)
        {
            if (buildFingerprint is null)
            {
                throw new ArgumentException(
                    "Game-validated profiles require a captured build fingerprint.",
                    nameof(buildFingerprint));
            }

            if (captureFingerprint is null)
            {
                throw new ArgumentException(
                    "Game-validated profiles require a captured validation-profile fingerprint.",
                    nameof(captureFingerprint));
            }
        }

        if (!double.IsFinite(morphActivationThreshold) ||
            morphActivationThreshold < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(morphActivationThreshold));
        }

        if (maximumActiveMorphTargets <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumActiveMorphTargets));
        }

        Id = id;
        ViewMode = viewMode;
        Fidelity = fidelity;
        VisualStyle = visualStyle;
        CameraBoneName = cameraBoneName;
        CameraLens = cameraLens;
        CameraOffset = cameraOffset.Normalized();
        FidelityTier = fidelityTier;
        Context = context;
        ProfileVersion = profileVersion;
        BuildFingerprint = buildFingerprint;
        CaptureFingerprint = captureFingerprint;
        ProceduralToggles = toggles;
        MorphActivationThreshold = morphActivationThreshold;
        MaximumActiveMorphTargets = maximumActiveMorphTargets;
        ClampMorphWeightsToRigBounds = clampMorphWeightsToRigBounds;
    }

    public string Id { get; }

    public PreviewViewMode ViewMode { get; }

    public AuthoringPreviewFidelity Fidelity { get; }

    public PreviewVisualStyle VisualStyle { get; }

    public string? CameraBoneName { get; }

    public CameraLens CameraLens { get; }

    public TransformTRS CameraOffset { get; }

    public PreviewFidelityTier FidelityTier { get; }

    public Dl1PreviewContext Context { get; }

    public int ProfileVersion { get; }

    public string? BuildFingerprint { get; }

    /// <summary>
    /// SHA-256 identity of the independently captured validation profile.
    /// A project-provided value is evidence metadata, not trust by itself.
    /// </summary>
    public string? CaptureFingerprint { get; }

    public ImmutableArray<string> ProceduralToggles { get; }

    /// <summary>
    /// Preview-only magnitude threshold below which a morph is not submitted
    /// to the emulated runtime path. Authored values remain untouched.
    /// </summary>
    public double MorphActivationThreshold { get; }

    /// <summary>
    /// Preview-only cap for simultaneously submitted active morphs.
    /// </summary>
    public int? MaximumActiveMorphTargets { get; }

    /// <summary>
    /// Optional profile rule. This is false for the evidence-backed DL1
    /// profiles because the inspected game setters store weights verbatim.
    /// </summary>
    public bool ClampMorphWeightsToRigBounds { get; }

    public PreviewFidelityTier GetEffectiveFidelityTier(
        string? installedBuildFingerprint,
        string? trustedCaptureFingerprint = null)
    {
        if (FidelityTier != PreviewFidelityTier.GameValidated)
        {
            return FidelityTier;
        }

        bool buildMatches = string.Equals(
            BuildFingerprint,
            installedBuildFingerprint,
            StringComparison.OrdinalIgnoreCase);
        bool trustedCaptureMatches = string.Equals(
            CaptureFingerprint,
            trustedCaptureFingerprint,
            StringComparison.OrdinalIgnoreCase);
        return buildMatches && trustedCaptureMatches
            ? PreviewFidelityTier.GameValidated
            : Context == Dl1PreviewContext.Raw
                ? PreviewFidelityTier.Raw
                : PreviewFidelityTier.Dl1Profile;
    }

    private static bool IsSha256Fingerprint(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            bool isDigit = character is >= '0' and <= '9';
            bool isLowerHex = character is >= 'a' and <= 'f';
            bool isUpperHex = character is >= 'A' and <= 'F';
            if (!isDigit && !isLowerHex && !isUpperHex)
            {
                return false;
            }
        }

        return true;
    }

    public static PreviewProfile RawAuthoring =>
        new(
            "raw_authoring",
            PreviewViewMode.ThirdPerson,
            AuthoringPreviewFidelity.AuthoringAccurate,
            PreviewVisualStyle.UnlitDiagnostic,
            null,
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Raw,
            Dl1PreviewContext.Raw);

    public static PreviewProfile ThirdPersonAuthoring =>
        new(
            "dl1_tpp_authoring",
            PreviewViewMode.ThirdPerson,
            AuthoringPreviewFidelity.AuthoringAccurate,
            PreviewVisualStyle.MaterialApproximation,
            null,
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Body,
            morphActivationThreshold: 0.001,
            maximumActiveMorphTargets: 64);

    public static PreviewProfile FirstPersonAuthoring =>
        new(
            "dl1_fpp_authoring",
            PreviewViewMode.Split,
            AuthoringPreviewFidelity.AuthoringAccurate |
            AuthoringPreviewFidelity.FirstPersonOcclusion,
            PreviewVisualStyle.MaterialApproximation,
            Dl1PreviewContract.EyeCameraBoneName,
            // This is an editor fallback, not a claimed DL1 runtime default.
            // PlayerFppVis derives FOV and near clip from live player/camera
            // state; a captured Dl1FppProjectionSnapshot supersedes it.
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Fpp,
            proceduralToggles:
            [
                Dl1PreviewStageIds.FppHSpineBasisCorrection,
                Dl1PreviewStageIds.FppHandsProjection,
            ],
            morphActivationThreshold: 0.001,
            maximumActiveMorphTargets: 64);

    public static PreviewProfile MovieAuthoring =>
        new(
            "dl1_movie_authoring",
            PreviewViewMode.Split,
            AuthoringPreviewFidelity.AuthoringAccurate,
            PreviewVisualStyle.MaterialApproximation,
            null,
            CameraLens.Default,
            TransformTRS.Identity,
            PreviewFidelityTier.Dl1Profile,
            Dl1PreviewContext.Dl1Movie,
            proceduralToggles:
            [
                Dl1PreviewStageIds.MovieReferenceCamera,
            ],
            morphActivationThreshold: 0.001,
            maximumActiveMorphTargets: 64);
}

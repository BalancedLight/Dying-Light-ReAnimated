using System.Collections.Immutable;
using ReAnimated.Core.Geometry;

namespace ReAnimated.Core.ModelAuthoring;

public enum CustomModelPerspective
{
    FirstPerson,
    ThirdPerson,
}

public readonly record struct CustomModelPerspectiveSurfaceKey(
    string ComponentId,
    string SurfaceId);

public readonly record struct CustomModelPerspectiveTriangleKey(
    string ComponentId,
    GeometrySourceTriangle Triangle);

/// <summary>
/// Reviewed, source-linked geometry visibility choices for one model perspective.
/// The source FBX and its full imported model remain authoritative.
/// </summary>
public sealed record CustomModelPerspectiveSelection
{
    public CustomModelPerspective Perspective { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public string SourceGeometryFingerprint { get; init; } = string.Empty;
    public ImmutableArray<CustomModelPerspectiveSurfaceKey> HiddenSurfaces { get; init; } = [];
    public ImmutableArray<CustomModelPerspectiveSurfaceKey> KeptSurfaces { get; init; } = [];
    public ImmutableArray<CustomModelPerspectiveTriangleKey> HiddenTriangles { get; init; } = [];

    public void Validate()
    {
        if (!Enum.IsDefined(Perspective)) throw new ArgumentOutOfRangeException(nameof(Perspective));
        ValidateHash(SourceSha256, nameof(SourceSha256));
        ValidateHash(SourceGeometryFingerprint, nameof(SourceGeometryFingerprint));
        if (HiddenSurfaces.IsDefault || KeptSurfaces.IsDefault || HiddenTriangles.IsDefault)
            throw new ArgumentException("Perspective visibility collections must be initialized.");
        if (HiddenSurfaces.Any(key => string.IsNullOrWhiteSpace(key.ComponentId) || string.IsNullOrWhiteSpace(key.SurfaceId)) ||
            HiddenSurfaces.Distinct().Count() != HiddenSurfaces.Length ||
            KeptSurfaces.Any(key => string.IsNullOrWhiteSpace(key.ComponentId) || string.IsNullOrWhiteSpace(key.SurfaceId)) ||
            KeptSurfaces.Distinct().Count() != KeptSurfaces.Length ||
            KeptSurfaces.Intersect(HiddenSurfaces).Any() ||
            HiddenTriangles.Any(key => string.IsNullOrWhiteSpace(key.ComponentId) || key.Triangle.PolygonIndex < 0 || key.Triangle.TriangleInPolygon < 0) ||
            HiddenTriangles.Distinct().Count() != HiddenTriangles.Length)
            throw new ArgumentException("Perspective visibility identities must be non-empty and unique.");
    }

    public bool Hides(string componentId, string surfaceId, GeometrySourceTriangle triangle)
    {
        if (KeptSurfaces.Contains(new(componentId, surfaceId))) return false;
        if (HiddenSurfaces.Contains(new(componentId, surfaceId))) return true;
        return HiddenTriangles.Contains(new(componentId, triangle));
    }

    private static void ValidateHash(string value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(static c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A perspective source identity must be a SHA-256 hash.", name);
    }
}

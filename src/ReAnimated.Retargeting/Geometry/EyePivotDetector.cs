using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

public enum EyePivotDetectionStatus { GlobeCandidate, NeedsReview, UnsupportedGeometry }
public sealed record EyePivotDetectionOptions
{
    public bool PaintedSurface { get; init; }
    public double MaximumNormalizedSurfaceError { get; init; } = .08;
    public double MinimumDepthRatio { get; init; } = .01;
}
public sealed record EyePivotDetectionResult(string SourceSha256, string ComponentId, int IslandIndex, string InputFingerprint,
    EyePivotDetectionStatus Status, Vector3D? Center, double? Radius, double? NormalizedVertexError,
    double? NormalizedSurfaceError, double? DepthRatio, bool ClosedSurface,
    ImmutableArray<int> SourceControlPointIds, ImmutableArray<AnatomicalDetectionDiagnostic> Diagnostics)
{
    public bool RequiresReview { get; } = true;
}

/// <summary>
/// Fits a spherical pivot to one selected source island. Surface quadrature and
/// depth/topology checks distinguish a globe candidate from flat artwork or a
/// sparse set of co-spherical vertices. Geometry alone does not identify an eye.
/// </summary>
public static class EyePivotDetector
{
    public static EyePivotDetectionResult Detect(SourceGeometryAnalysis analysis, string componentId, int islandIndex,
        EyePivotDetectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(analysis); ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        cancellationToken.ThrowIfCancellationRequested();
        if (analysis.SourceSha256 is not { Length: 64 } || analysis.SourceSha256.Any(static c => !Uri.IsHexDigit(c)) || analysis.Components.IsDefault)
            throw new ArgumentException("Eye detection requires a source SHA-256 and an initialized component inventory.", nameof(analysis));
        options ??= new();
        if (!double.IsFinite(options.MaximumNormalizedSurfaceError) || options.MaximumNormalizedSurfaceError is <= 0 or > .5 ||
            !double.IsFinite(options.MinimumDepthRatio) || options.MinimumDepthRatio is <= 0 or > 1)
            throw new ArgumentException("Eye-fit thresholds must be finite normalized values.", nameof(options));
        var component = analysis.Components.SingleOrDefault(c => c.Geometry.Id == componentId) ?? throw new ArgumentException("The selected source component is missing.", nameof(componentId));
        if ((uint)islandIndex >= (uint)component.Topology.Islands.Length) throw new ArgumentOutOfRangeException(nameof(islandIndex));
        var island = component.Topology.Islands[islandIndex];
        if (island.SourceControlPointIds.Length > 250_000 || island.TriangleIndices.Length > 1_000_000)
            throw new InvalidOperationException("The selected eye island exceeds its analysis budget.");
        var diagnostics = ImmutableArray.CreateBuilder<AnatomicalDetectionDiagnostic>();
        var ids = island.SourceControlPointIds;
        var points = ids.Select(id => component.Geometry.ControlPoints[id]).ToArray();
        if (points.Any(static p => !p.IsFinite)) throw new ArgumentException("Eye geometry must be finite.", nameof(analysis));
        string fingerprint = Fingerprint(analysis.SourceSha256, component, islandIndex, options, cancellationToken);
        Vector3D? center = null; double? radius = null, vertexError = null, surfaceError = null, depthRatio = null;
        bool closed = false;
        if (options.PaintedSurface) { Note("eye_painted_surface", "Painted or flat eyes require an existing eye rig or manually authored gaze setup; no globe pivot was inferred."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        if (ids.Length < 6) { Note("eye_insufficient_samples", "Too few source points support a globe fit."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        var anchor = points[0];
        double scale = points.Max(p => (p - anchor).Length);
        if (!double.IsFinite(scale) || scale <= 0) { Note("eye_zero_extent", "The selected island has no usable geometric extent."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        var indexById = ids.Select((id, i) => (id, i)).ToDictionary(static p => p.id, static p => p.i);
        var weights = new double[points.Length];
        var faces = new List<(int A, int B, int C, double Area)>();
        foreach (int triangleIndex in island.TriangleIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = component.Triangles[triangleIndex];
            int a = indexById[t.A], b = indexById[t.B], c = indexById[t.C];
            double area = Vector3D.Cross((points[b] - points[a]) / scale, (points[c] - points[a]) / scale).Length * .5;
            if (!double.IsFinite(area) || area <= 1e-16) { Note("eye_degenerate_surface", "Degenerate source triangles prevent reliable globe identification."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
            weights[a] += area / 3; weights[b] += area / 3; weights[c] += area / 3; faces.Add((a, b, c, area));
        }
        double total = weights.Sum();
        if (!(total > 0)) { Note("eye_empty_surface", "The selected eye island has no area."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        var mean = Vector3D.Zero;
        for (int i = 0; i < points.Length; i++) { weights[i] /= total; mean += (points[i] - anchor) / scale * weights[i]; }
        var normalized = points.Select(p => (p - anchor) / scale - mean).ToArray();
        var covariance = new double[3, 3]; var rhs = new double[3];
        for (int i = 0; i < points.Length; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            double[] p = [normalized[i].X, normalized[i].Y, normalized[i].Z]; double squared = normalized[i].LengthSquared;
            for (int row = 0; row < 3; row++)
            {
                rhs[row] += .5 * weights[i] * p[row] * squared;
                for (int col = 0; col < 3; col++) covariance[row, col] += weights[i] * p[row] * p[col];
            }
        }
        var eigenvalues = Eigenvalues(covariance);
        depthRatio = eigenvalues[2] > 0 ? Math.Max(0, eigenvalues[0]) / eigenvalues[2] : 0;
        if (depthRatio < options.MinimumDepthRatio || !TrySolve(covariance, rhs, out var offset))
        { Note("eye_insufficient_depth", "This island is too flat or poorly conditioned to establish a three-dimensional globe pivot."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        double radiusNormalized = Math.Sqrt(normalized.Select((p, i) => (p - offset).LengthSquared * weights[i]).Sum());
        if (!double.IsFinite(radiusNormalized) || radiusNormalized <= 0)
        { Note("eye_invalid_radius", "A finite positive globe radius could not be fitted."); return Result(EyePivotDetectionStatus.UnsupportedGeometry); }
        center = anchor + (mean + offset) * scale; radius = radiusNormalized * scale;
        if (!center.Value.IsFinite || !double.IsFinite(radius.Value)) throw new ArgumentException("The fitted eye exceeds finite authoring coordinates.", nameof(analysis));
        vertexError = Math.Sqrt(normalized.Select((p, i) => Math.Pow((p - offset).Length - radiusNormalized, 2) * weights[i]).Sum()) / radiusNormalized;
        double surfaceSquared = 0;
        foreach (var face in faces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Edge-midpoint and face-centroid samples expose flat faces whose
            // vertices alone fit a sphere, such as a cube or coarse cap.
            var a = normalized[face.A]; var b = normalized[face.B]; var c = normalized[face.C];
            foreach (var p in new[] { (a + b) * .5, (a + c) * .5, (b + c) * .5, (a + b + c) / 3 })
                surfaceSquared += face.Area / (total * 4) * Math.Pow((p - offset).Length - radiusNormalized, 2);
        }
        surfaceError = Math.Sqrt(surfaceSquared) / radiusNormalized;
        var selectedTriangles = island.TriangleIndices.Select(i => component.Triangles[i]).ToImmutableArray();
        var topology = SourceMeshTopology.Build(component.Geometry.ControlPoints.Length, selectedTriangles.Select(t => (t.A, t.B, t.C)).ToArray(), cancellationToken);
        closed = topology.BoundaryEdges.IsEmpty && topology.NonManifoldEdges.IsEmpty && topology.WindingConflictEdges.IsEmpty;
        if (!closed) Note("eye_open_surface", "The selected island is open or topologically inconsistent; the fitted center is only a manual-review proposal.");
        if (surfaceError > options.MaximumNormalizedSurfaceError) Note("eye_non_spherical_surface", "The surface departs from the fitted sphere. Review the pivot manually instead of accepting it as globe geometry.");
        if (closed)
        {
            var volume = SourceGeometryVolume.Build(new(analysis.SourceSha256, [new(component.Geometry, selectedTriangles, topology)]), cancellationToken: cancellationToken);
            if (volume.HasUnreliableTopology || volume.Sample(center.Value, cancellationToken: cancellationToken).Location != SourceVolumeLocation.Interior)
            { closed = false; Note("eye_center_outside", "The fitted pivot is not inside a reliable closed source volume."); }
        }
        Note("eye_semantic_review", "A spherical shape is geometric evidence only. Confirm that the selected island is the intended eye and review its gaze direction.");
        return Result(closed && surfaceError <= options.MaximumNormalizedSurfaceError ? EyePivotDetectionStatus.GlobeCandidate : EyePivotDetectionStatus.NeedsReview);

        void Note(string code, string message) => diagnostics.Add(new(code, message));
        EyePivotDetectionResult Result(EyePivotDetectionStatus status) => new(analysis.SourceSha256, componentId, islandIndex, fingerprint,
            status, center, radius, vertexError, surfaceError, depthRatio, closed, ids, diagnostics.ToImmutable());
    }

    private static bool TrySolve(double[,] matrix, double[] rhs, out Vector3D value)
    {
        var a = new double[3, 4]; value = Vector3D.Zero;
        for (int i = 0; i < 3; i++) { for (int j = 0; j < 3; j++) a[i, j] = matrix[i, j]; a[i, 3] = rhs[i]; }
        for (int col = 0; col < 3; col++)
        {
            int pivot = col; for (int row = col + 1; row < 3; row++) if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-12) return false;
            for (int j = col; j < 4; j++) (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
            double divisor = a[col, col]; for (int j = col; j < 4; j++) a[col, j] /= divisor;
            for (int row = 0; row < 3; row++) if (row != col) { double factor = a[row, col]; for (int j = col; j < 4; j++) a[row, j] -= factor * a[col, j]; }
        }
        value = new(a[0, 3], a[1, 3], a[2, 3]); return value.IsFinite;
    }
    private static double[] Eigenvalues(double[,] matrix)
    {
        var a = (double[,])matrix.Clone();
        for (int iteration = 0; iteration < 24; iteration++)
        {
            int p = 0, q = 1;
            if (Math.Abs(a[0, 2]) > Math.Abs(a[p, q])) { p = 0; q = 2; }
            if (Math.Abs(a[1, 2]) > Math.Abs(a[p, q])) { p = 1; q = 2; }
            if (Math.Abs(a[p, q]) <= 1e-14) break;
            double angle = .5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
            double c = Math.Cos(angle), s = Math.Sin(angle), pp = a[p, p], qq = a[q, q], pq = a[p, q];
            a[p, p] = c * c * pp - 2 * c * s * pq + s * s * qq;
            a[q, q] = s * s * pp + 2 * c * s * pq + c * c * qq; a[p, q] = a[q, p] = 0;
            for (int i = 0; i < 3; i++) if (i != p && i != q) { double ip = a[i, p], iq = a[i, q]; a[i, p] = a[p, i] = c * ip - s * iq; a[i, q] = a[q, i] = s * ip + c * iq; }
        }
        return new[] { a[0, 0], a[1, 1], a[2, 2] }.Order().ToArray();
    }
    private static string Fingerprint(string source, SourceGeometryComponentAnalysis component, int islandIndex, EyePivotDetectionOptions options, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));
        Add(string.Create(CultureInfo.InvariantCulture, $"eye-pivot-v1|{source}|{component.Geometry.Id}|{islandIndex}|{options.PaintedSurface}|{options.MaximumNormalizedSurfaceError:R}|{options.MinimumDepthRatio:R}"));
        var island = component.Topology.Islands[islandIndex];
        foreach (int id in island.SourceControlPointIds)
        {
            token.ThrowIfCancellationRequested(); var p = component.Geometry.ControlPoints[id];
            Add(string.Create(CultureInfo.InvariantCulture, $"{id}:{p.X:R},{p.Y:R},{p.Z:R}"));
        }
        foreach (int index in island.TriangleIndices) { var t = component.Triangles[index]; Add(string.Create(CultureInfo.InvariantCulture, $"t:{t.A},{t.B},{t.C}")); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

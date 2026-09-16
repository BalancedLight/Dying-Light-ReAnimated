using System.Numerics;

namespace ReAnimated.Renderer.D3D11;

public sealed record StressDeformationReport(int VertexSamples, int EdgeSamples, double MaximumDisplacement,
    double MinimumEdgeRatio, double MaximumEdgeRatio, int DegenerateReferenceEdges, int OpenedDegenerateEdges,
    int CollapsedPosedEdges, bool FiniteNormals)
{
    public bool RequiresVisualReview { get; } = true;
}

/// <summary>Bounded inspection using the renderer's existing CPU shader reference, without assigning visual-quality or native acceptance.</summary>
public static class StressDeformationMeasurement
{
    public static StressDeformationReport Measure(IReadOnlyList<MeshRenderData> meshes, SkeletonRenderData reference,
        SkeletonRenderData posed, IReadOnlyList<MorphWeight> morphs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(meshes); ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(posed); ArgumentNullException.ThrowIfNull(morphs);
        long vertexCount = meshes.Sum(static m => (long)m.Vertices.Length);
        long edgeCount = meshes.Sum(static m => (long)m.Indices.Length);
        if (vertexCount > 1_000_000 || edgeCount > 3_000_000) throw new InvalidOperationException("This measurement exceeds the review budget of one million vertices or three million edge samples.");
        double maximumDisplacement = 0, minimumRatio = double.PositiveInfinity, maximumRatio = 0;
        int degenerate = 0, opened = 0, collapsed = 0, validEdges = 0;
        bool finiteNormals = true;
        foreach (var mesh in meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (mesh.Indices.Length % 3 != 0) throw new ArgumentException("Stress measurement requires complete triangle indices.", nameof(meshes));
            var restVertices = CpuMeshDeformationEvaluator.Evaluate(mesh, reference, morphs, cancellationToken);
            var posedVertices = CpuMeshDeformationEvaluator.Evaluate(mesh, posed, morphs, cancellationToken);
            for (int i = 0; i < restVertices.Length; i++)
            {
                if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                double displacement = Vector3.Distance(restVertices[i].Position, posedVertices[i].Position);
                if (!double.IsFinite(displacement)) throw new InvalidOperationException("Stress evaluation produced a non-finite position.");
                maximumDisplacement = Math.Max(maximumDisplacement, displacement);
                finiteNormals &= IsFinite(restVertices[i].Normal) && IsFinite(posedVertices[i].Normal);
            }
            var indices = mesh.Indices.Span;
            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                if ((triangle & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                for (int corner = 0; corner < 3; corner++)
                {
                    int a = checked((int)indices[triangle + corner]), b = checked((int)indices[triangle + (corner + 1) % 3]);
                    double length = Vector3.Distance(restVertices[a].Position, restVertices[b].Position);
                    double deformed = Vector3.Distance(posedVertices[a].Position, posedVertices[b].Position);
                    if (!double.IsFinite(length) || !double.IsFinite(deformed)) throw new InvalidOperationException("Stress evaluation produced a non-finite edge.");
                    if (length <= 1e-8) { degenerate++; if (deformed > 1e-8) opened++; continue; }
                    if (deformed <= 1e-8) collapsed++;
                    double ratio = deformed / length;
                    minimumRatio = Math.Min(minimumRatio, ratio); maximumRatio = Math.Max(maximumRatio, ratio); validEdges++;
                }
            }
        }
        return new((int)vertexCount, (int)edgeCount, maximumDisplacement, validEdges == 0 ? 1 : minimumRatio,
            validEdges == 0 ? 1 : maximumRatio, degenerate, opened, collapsed, finiteNormals);
    }
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Pure geometry transfer for reviewed morph-source reuse.</summary>
public static class MorphDeformationTransfer
{
    public static MorphReferenceProfile CreateProfile(string sourceSha256, string? rigSignature,
        IEnumerable<MorphReferenceChannel> channels, IEnumerable<MorphReferenceSurface> surfaces)
    {
        MorphReferenceSurface[] geometry = surfaces.ToArray();
        var profile = new MorphReferenceProfile { SourceSha256 = sourceSha256, RigSignature = rigSignature,
            Channels = channels.OrderBy(c => c.Index).ToImmutableArray(), Surfaces = geometry.ToImmutableArray(),
            TopologyFingerprint = MorphProfileFingerprint.Compute(geometry) };
        profile.Validate();
        return profile;
    }

    public static MorphExpressionProposal Propose(MorphReferenceProfile source, MorphReferenceProfile target,
        string sourceChannelName, IReadOnlyDictionary<string, ImmutableArray<ImmutableArray<Vector3D>>> sourceDeltas,
        MorphTransferOptions? options = null)
    {
        source.Validate(); target.Validate(); options ??= new();
        var channel = source.Channels.SingleOrDefault(c => string.Equals(c.Name, sourceChannelName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"Source morph '{sourceChannelName}' is not in the source inventory.");
        if (!sourceDeltas.TryGetValue(sourceChannelName, out var deltas) || deltas.Length != source.Surfaces.Length ||
            deltas.Zip(source.Surfaces).Any(pair => pair.First.Length != pair.Second.NeutralPositions.Length))
            throw new InvalidDataException("Source morph deltas do not cover the source surface inventory.");
        if (deltas.Any(surface => surface.Any(delta => !delta.IsFinite)))
            throw new InvalidDataException("Source morph deltas must be finite.");
        HashSet<(int Surface, int Vertex)> hardFixed = ValidateCorrespondences(source, target, options);
        bool exact = SameTopology(source, target);
        MorphTransferMethod method = options.Method;
        if (!exact && method == MorphTransferMethod.ExactTopology)
            throw new InvalidDataException("Exact morph transfer requires matching surface topology.");
        ImmutableArray<ImmutableArray<Vector3D>> output = method == MorphTransferMethod.ExactTopology
            ? target.Surfaces.Select((s, i) => deltas[i].Select((delta, vertex) =>
                hardFixed.Contains((i, vertex)) ? Vector3D.Zero : delta).ToImmutableArray()).ToImmutableArray()
            : TransferByCorrespondence(source, target, deltas, options, hardFixed);
        return new MorphExpressionProposal(source.SourceSha256, source.TopologyFingerprint,
            target.TopologyFingerprint, channel.Name, channel.DescriptorHash, channel.Name, method,
            target.Surfaces, output, options.Correspondences, options.TriangleCorrespondences,
            method != MorphTransferMethod.ExactTopology || hardFixed.Count > 0,
            method == MorphTransferMethod.ExactTopology
                ? "Exact topology and channel identity verified; target locks and mask applied."
                : "Reviewed vertex or triangle correspondences and target locks applied to deformation-gradient transfer.");
    }

    public static MorphExpressionProposal Apply(MorphExpressionProposal proposal, MorphReferenceProfile source,
        MorphReferenceProfile target, MorphExpressionProposal? existing = null,
        MorphTransferConflict conflict = MorphTransferConflict.Reject)
    {
        proposal.ValidateAgainst(source, target);
        if (proposal.RequiresExplicitReview && !proposal.IsReviewed)
            throw new InvalidOperationException("This morph transfer requires explicit review before application.");
        if (existing is not null && proposal.TargetExpressionName.Equals(existing.TargetExpressionName, StringComparison.OrdinalIgnoreCase))
        {
            if (conflict == MorphTransferConflict.Reject) throw new InvalidOperationException("An existing morph requires an explicit keep or replace decision.");
            if (conflict == MorphTransferConflict.KeepExisting) return existing;
            if (proposal.IsReviewed == false) throw new InvalidOperationException("A replacement morph requires explicit review.");
            if (proposal.Method != MorphTransferMethod.ExactTopology && proposal.Correspondences.Length == 0 && proposal.TriangleCorrespondences.Length == 0)
                throw new InvalidDataException("A topology-changing replacement has no correspondence evidence.");
            if (existing.PositionDeltas.Length != proposal.PositionDeltas.Length)
                throw new InvalidDataException("Existing and replacement morph surfaces do not match.");
        }
        return proposal with { IsReviewed = true };
    }


    private static bool SameTopology(MorphReferenceProfile a, MorphReferenceProfile b) =>
        a.Surfaces.Length == b.Surfaces.Length && a.Surfaces.Zip(b.Surfaces).All(pair =>
            pair.First.NeutralPositions.Length == pair.Second.NeutralPositions.Length &&
            pair.First.Indices.SequenceEqual(pair.Second.Indices));

    private static HashSet<(int Surface, int Vertex)> ValidateCorrespondences(
        MorphReferenceProfile source, MorphReferenceProfile target, MorphTransferOptions options)
    {
        var hardFixed = new HashSet<(int Surface, int Vertex)>();
        for (int surface = 0; surface < target.Surfaces.Length; surface++)
            for (int vertex = 0; vertex < target.Surfaces[surface].NeutralPositions.Length; vertex++)
                if (options.LockedTargetVertices.Contains(vertex) ||
                    options.TargetVertexMask.Count > 0 && !options.TargetVertexMask.Contains(vertex))
                    hardFixed.Add((surface, vertex));

        foreach (MorphVertexCorrespondence row in options.Correspondences)
        {
            ValidateWeight(row.Weight);
            int si = FindSurfaceIndex(source, row.SourceGeometryIdentity);
            int ti = FindSurfaceIndex(target, row.TargetGeometryIdentity);
            if ((uint)row.SourceVertexIndex >= (uint)source.Surfaces[si].NeutralPositions.Length ||
                (uint)row.TargetVertexIndex >= (uint)target.Surfaces[ti].NeutralPositions.Length)
                throw new InvalidDataException("Morph correspondence references an invalid vertex.");
            if (row.Locked) hardFixed.Add((ti, row.TargetVertexIndex));
        }
        foreach (MorphTriangleCorrespondence link in options.TriangleCorrespondences)
        {
            ValidateWeight(link.Weight);
            int si = FindSurfaceIndex(source, link.SourceGeometryIdentity);
            int ti = FindSurfaceIndex(target, link.TargetGeometryIdentity);
            if ((uint)link.SourceTriangleIndex >= (uint)(source.Surfaces[si].Indices.Length / 3) ||
                (uint)link.TargetTriangleIndex >= (uint)(target.Surfaces[ti].Indices.Length / 3))
                throw new InvalidDataException("Triangle correspondence is outside the surface topology.");
            if (link.Locked)
                for (int corner = 0; corner < 3; corner++)
                    hardFixed.Add((ti, target.Surfaces[ti].Indices[link.TargetTriangleIndex * 3 + corner]));
        }
        return hardFixed;
    }

    private static int FindSurfaceIndex(MorphReferenceProfile profile, string geometryIdentity)
    {
        int found = -1;
        for (int i = 0; i < profile.Surfaces.Length; i++)
        {
            if (!string.Equals(profile.Surfaces[i].GeometryIdentity, geometryIdentity, StringComparison.Ordinal)) continue;
            if (found >= 0) throw new InvalidDataException("Morph correspondence surface identity is ambiguous across LODs.");
            found = i;
        }
        if (found < 0) throw new InvalidDataException("Morph correspondence references an unknown surface.");
        return found;
    }

    private static void ValidateWeight(double weight)
    {
        if (!double.IsFinite(weight) || weight <= 0)
            throw new InvalidDataException("Morph correspondence weight must be finite and positive.");
    }

    private static ImmutableArray<ImmutableArray<Vector3D>> TransferByCorrespondence(MorphReferenceProfile source,
        MorphReferenceProfile target, ImmutableArray<ImmutableArray<Vector3D>> deltas, MorphTransferOptions options,
        HashSet<(int Surface, int Vertex)> hardFixed)
    {
        var rows = options.Correspondences.ToArray();
        var triangles = options.TriangleCorrespondences.ToArray();
        if (rows.Length < options.MinimumAnchors && triangles.Length == 0) throw new InvalidDataException("Topology-changing transfer needs reviewed vertex or triangle correspondences.");
        var result = ImmutableArray.CreateBuilder<ImmutableArray<Vector3D>>();
        for (int ti = 0; ti < target.Surfaces.Length; ti++)
        {
            MorphReferenceSurface surface = target.Surfaces[ti];
            var values = ImmutableArray.CreateBuilder<Vector3D>(surface.NeutralPositions.Length);
            for (int i = 0; i < surface.NeutralPositions.Length; i++)
            {
                var matches = rows.Where(c => c.TargetGeometryIdentity == surface.GeometryIdentity && c.TargetVertexIndex == i).ToArray();
                if (hardFixed.Contains((ti, i))) { values.Add(Vector3D.Zero); continue; }
                if (matches.Length == 0) { values.Add(Vector3D.Zero); continue; }
                double total = matches.Sum(c => c.Weight); Vector3D value = Vector3D.Zero;
                if (!double.IsFinite(total)) throw new InvalidDataException("Morph correspondence weights overflow.");
                foreach (var match in matches)
                {
                    int si = FindSurfaceIndex(source, match.SourceGeometryIdentity);
                    value += deltas[si][match.SourceVertexIndex] * (match.Weight / total);
                }
                values.Add(value);
            }
            result.Add(values.ToImmutable());
        }
        if (triangles.Length > 0)
            ApplyTriangleDeformationGradients(source, target, deltas, triangles, hardFixed, result);
        return result.ToImmutable();
    }

    private static void ApplyTriangleDeformationGradients(MorphReferenceProfile source, MorphReferenceProfile target,
        ImmutableArray<ImmutableArray<Vector3D>> sourceDeltas, MorphTriangleCorrespondence[] links,
        HashSet<(int Surface, int Vertex)> hardFixed, ImmutableArray<ImmutableArray<Vector3D>>.Builder result)
    {
        var constraints = new Dictionary<(int Surface, int Vertex), List<(Vector3D Value, double Weight)>>();
        var edges = new List<((int Surface, int A), (int Surface, int B), Vector3D Value, double Weight)>();
        foreach (var link in links)
        {
            int si = FindSurfaceIndex(source, link.SourceGeometryIdentity);
            int ti = FindSurfaceIndex(target, link.TargetGeometryIdentity);
            MorphReferenceSurface ss = source.Surfaces[si], ts = target.Surfaces[ti];
            int s0 = ss.Indices[link.SourceTriangleIndex * 3], s1 = ss.Indices[link.SourceTriangleIndex * 3 + 1], s2 = ss.Indices[link.SourceTriangleIndex * 3 + 2];
            int t0 = ts.Indices[link.TargetTriangleIndex * 3], t1 = ts.Indices[link.TargetTriangleIndex * 3 + 1], t2 = ts.Indices[link.TargetTriangleIndex * 3 + 2];
            Vector3D sourceBase = ss.NeutralPositions[s0];
            Vector3D targetBase = ts.NeutralPositions[t0];
            Vector3D deformedBase = sourceBase + sourceDeltas[si][s0];
            Vector3D sourceE1 = ss.NeutralPositions[s1] - sourceBase, sourceE2 = ss.NeutralPositions[s2] - sourceBase;
            Vector3D deformedE1 = (ss.NeutralPositions[s1] + sourceDeltas[si][s1]) - deformedBase;
            Vector3D deformedE2 = (ss.NeutralPositions[s2] + sourceDeltas[si][s2]) - deformedBase;
            Vector3D targetE1 = ts.NeutralPositions[t1] - targetBase, targetE2 = ts.NeutralPositions[t2] - targetBase;
            Vector3D sourceNormal = Vector3D.Cross(sourceE1, sourceE2), targetNormal = Vector3D.Cross(targetE1, targetE2);
            double sourceArea = sourceNormal.LengthSquared, targetArea = targetNormal.LengthSquared;
            if (sourceArea < 1e-18 || targetArea < 1e-18) throw new InvalidDataException("Triangle correspondence is degenerate.");
            // F is the source neutral-to-deformed deformation gradient.  The
            // source normal is carried as an extra constrained basis vector so
            // a planar triangle remains solvable in 3D; with zero source
            // deltas this is exactly identity and cannot reshape the target.
            Vector3D deformedNormal = Vector3D.Cross(deformedE1, deformedE2);
            if (deformedNormal.LengthSquared < 1e-18) throw new InvalidDataException("Deformed triangle correspondence is degenerate.");
            Vector3D sourceAugmentedNormal = sourceNormal / Math.Sqrt(sourceNormal.Length);
            Vector3D deformedAugmentedNormal = deformedNormal / Math.Sqrt(deformedNormal.Length);
            Vector3D[] gradient = MapBasis(sourceE1, sourceE2, sourceAugmentedNormal, deformedE1, deformedE2, deformedAugmentedNormal);
            int[] targetVertices = [t0, t1, t2];
            Vector3D[] desired = new Vector3D[3];
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = targetVertices[corner];
                Vector3D point = ts.NeutralPositions[vertex];
                Vector3D translation = deformedBase - sourceBase;
                desired[corner] = translation + ApplyBasis(gradient, point - targetBase) - (point - targetBase);
            }
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = targetVertices[corner];
                constraints.TryAdd((ti, vertex), []);
                if (hardFixed.Contains((ti, vertex)))
                {
                    constraints[(ti, vertex)].Add((Vector3D.Zero, link.Weight));
                    continue;
                }
                constraints[(ti, vertex)].Add((desired[corner], link.Weight));
            }
            edges.Add(((ti, t0), (ti, t1), desired[1] - desired[0], link.Weight));
            edges.Add(((ti, t0), (ti, t2), desired[2] - desired[0], link.Weight));
        }
        Dictionary<(int Surface, int Vertex), Vector3D> solved = constraints.ToDictionary(p => p.Key, p => WeightedAverage(p.Value));
        bool converged = false;
        for (int iteration = 0; iteration < 256; iteration++)
        {
            var diagonal = constraints.Keys.ToDictionary(k => k, _ => 0.0);
            var rhs = constraints.Keys.ToDictionary(k => k, _ => Vector3D.Zero);
            var neighbours = constraints.Keys.ToDictionary(k => k, _ => Vector3D.Zero);
            foreach (var pair in constraints)
            {
                double weight = pair.Value.Sum(v => v.Weight); diagonal[pair.Key] += weight; rhs[pair.Key] += WeightedAverage(pair.Value) * weight;
            }
            foreach (var edge in edges)
            {
                var a = edge.Item1; var b = edge.Item2; double w = edge.Weight;
                bool aFixed = hardFixed.Contains(a), bFixed = hardFixed.Contains(b);
                if (aFixed && bFixed) continue;
                if (aFixed) { diagonal[b] += w; rhs[b] += edge.Value * w; continue; }
                if (bFixed) { diagonal[a] += w; rhs[a] -= edge.Value * w; continue; }
                diagonal[a] += w; diagonal[b] += w; neighbours[a] += solved[b] * w - edge.Value * w; neighbours[b] += solved[a] * w + edge.Value * w;
            }
            var next = new Dictionary<(int, int), Vector3D>(solved); double residual = 0;
            foreach (var key in solved.Keys)
            {
                if (hardFixed.Contains(key)) { next[key] = Vector3D.Zero; continue; }
                Vector3D value = (rhs[key] + neighbours[key]) / Math.Max(diagonal[key], 1e-12); residual = Math.Max(residual, (value - solved[key]).Length); next[key] = value;
            }
            solved = next;
            if (residual < 1e-9) { converged = true; break; }
        }
        if (!converged && edges.Count > 0) throw new InvalidDataException("Morph deformation least-squares constraints did not converge.");
        foreach (var pair in solved)
            result[pair.Key.Surface] = result[pair.Key.Surface].SetItem(pair.Key.Vertex,
                hardFixed.Contains(pair.Key) ? Vector3D.Zero : pair.Value);
    }

    private static Vector3D WeightedAverage(List<(Vector3D Value, double Weight)> values)
    {
        double total = values.Sum(v => v.Weight); Vector3D sum = Vector3D.Zero;
        if (!double.IsFinite(total) || total <= 0) throw new InvalidDataException("Morph correspondence weights overflow.");
        foreach (var (value, weight) in values) sum += value * (weight / total);
        return sum;
    }

    private static Vector3D[] MapBasis(Vector3D a, Vector3D b, Vector3D c, Vector3D x, Vector3D y, Vector3D z)
    {
        double[,] n = {{a.X,b.X,c.X},{a.Y,b.Y,c.Y},{a.Z,b.Z,c.Z}};
        double det = n[0,0]*(n[1,1]*n[2,2]-n[1,2]*n[2,1]) - n[0,1]*(n[1,0]*n[2,2]-n[1,2]*n[2,0]) + n[0,2]*(n[1,0]*n[2,1]-n[1,1]*n[2,0]);
        if (Math.Abs(det) < 1e-12) throw new InvalidDataException("Triangle deformation basis is singular.");
        double[,] inv = {{(n[1,1]*n[2,2]-n[1,2]*n[2,1])/det,(n[0,2]*n[2,1]-n[0,1]*n[2,2])/det,(n[0,1]*n[1,2]-n[0,2]*n[1,1])/det},{(n[1,2]*n[2,0]-n[1,0]*n[2,2])/det,(n[0,0]*n[2,2]-n[0,2]*n[2,0])/det,(n[1,0]*n[0,2]-n[0,0]*n[1,2])/det},{(n[1,0]*n[2,1]-n[1,1]*n[2,0])/det,(n[0,1]*n[2,0]-n[0,0]*n[2,1])/det,(n[0,0]*n[1,1]-n[0,1]*n[1,0])/det}};
        double[,] d = {{x.X,y.X,z.X},{x.Y,y.Y,z.Y},{x.Z,y.Z,z.Z}};
        Vector3D Col(int j) => new(Enumerable.Range(0,3).Sum(i => d[0,i]*inv[i,j]), Enumerable.Range(0,3).Sum(i => d[1,i]*inv[i,j]), Enumerable.Range(0,3).Sum(i => d[2,i]*inv[i,j]));
        return [Col(0), Col(1), Col(2)];
    }

    private static Vector3D ApplyBasis(Vector3D[] basis, Vector3D v) =>
        basis[0] * v.X + basis[1] * v.Y + basis[2] * v.Z;
}

internal static class MorphSurfaceExtensions
{
    public static int FindIndex(this ImmutableArray<MorphReferenceSurface> surfaces, Func<MorphReferenceSurface, bool> predicate)
    {
        for (int i = 0; i < surfaces.Length; i++) if (predicate(surfaces[i])) return i;
        return -1;
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

/// <summary>
/// A local volume branch detector. Geodesic superlevel components separate
/// persistent digit branches; interior paths propose knuckles and curl planes.
/// Coarse, fused, touching or unusual geometry remains explicitly reviewable.
/// </summary>
public static class LocalHandDetector
{
    public static LocalHandDetectionResult Detect(SourceVolumeGrid grid, LocalHandDetectionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid); ArgumentNullException.ThrowIfNull(options);
        if (options.Side is not ("left" or "right") || !options.Wrist.IsFinite ||
            options.ExpectedDigits is < 1 or > 8 || !double.IsFinite(options.MinimumBranchFraction) || options.MinimumBranchFraction is <= 0 or > .5 ||
            options.MaximumInteriorCells is < 16 or > 1_000_000 || !double.IsFinite(options.PathClearanceWeight) || options.PathClearanceWeight is < 0 or > 1_000_000 ||
            options.Guides.IsDefault || options.Guides.Length > 64 || options.Guides.Any(g => g is null || string.IsNullOrWhiteSpace(g.Role) || !g.Position.IsFinite) ||
            options.Guides.Select(static g => g.Role).Distinct(StringComparer.Ordinal).Count() != options.Guides.Length)
            throw new ArgumentException("Hand detection requires finite local inputs, a side, bounded digit count and unique guides.", nameof(options));
        if (!options.Forward.TryNormalize(out var forward) ||
            !(options.PalmNormalHint - forward * Vector3D.Dot(options.PalmNormalHint, forward)).TryNormalize(out var normalHint))
            throw new ArgumentException("The hand forward and palm-normal hint must be finite, nonzero and independent.", nameof(options));
        string fingerprint = Fingerprint(grid, options);
        var diagnostics = ImmutableArray.CreateBuilder<AnatomicalDetectionDiagnostic>();
        var preserved = options.Guides.Select(g => new AnatomicalJointProposal(g.Role, g.Position, AnatomicalPlacementMethod.UserGuide, 1, g.Locked, null)).ToImmutableArray();
        var proposals = ImmutableArray.CreateBuilder<HandFingerProposal>();
        TransformMatrix? palmFrame = null;
        int interior = 0;
        if (!grid.HasCompleteField) { Miss("hand_incomplete_field", "Hand detection needs a complete closed-volume field; open or unknown geometry was not filled."); return Result(); }
        if (grid.CellCount > 8_000_000) throw new ArgumentException("The hand grid exceeds its supported storage budget.", nameof(grid));
        var positions = new Vector3D[grid.CellCount];
        var included = new bool[grid.CellCount];
        var clearance = new double[grid.CellCount];
        var distance = Enumerable.Repeat(-1, grid.CellCount).ToArray();
        int wrist = -1; double wristDistance = double.PositiveInfinity;
        for (int i = 0; i < grid.CellCount; i++)
        {
            if ((i & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var c = Coordinate(i); var cell = grid.GetCell(c.X, c.Y, c.Z);
            if (cell.Location != SourceVolumeLocation.Interior || cell.SignedField is not { } field || field >= 0 || !double.IsFinite(field)) continue;
            var p = grid.GetPosition(c.X, c.Y, c.Z); positions[i] = p;
            clearance[i] = -field;
            // The entered wrist plane excludes the forearm behind the local hand.
            if (Vector3D.Dot(p - options.Wrist, forward) < -grid.CellSize * 2) continue;
            included[i] = true;
            if (++interior > options.MaximumInteriorCells) throw new InvalidOperationException("The hand region exceeds its interior-cell budget. Reduce the region or resolution.");
            double d = (p - options.Wrist).LengthSquared;
            if (d < wristDistance) { wristDistance = d; wrist = i; }
        }
        if (wrist < 0 || wristDistance > 16 * grid.CellSize * grid.CellSize)
        { Miss("hand_wrist_outside", "The wrist seed is not within four cells of the hand interior. Correct the seed or region."); return Result(); }
        var queue = new Queue<int>(); var connected = new List<int>(interior);
        queue.Enqueue(wrist); distance[wrist] = 0;
        while (queue.TryDequeue(out int i))
        {
            cancellationToken.ThrowIfCancellationRequested(); connected.Add(i);
            foreach (int neighbor in Neighbors(i)) if (included[neighbor] && distance[neighbor] < 0)
            { distance[neighbor] = distance[i] + 1; queue.Enqueue(neighbor); }
        }
        if (connected.Count < 16) { Miss("hand_region_small", "The connected hand contains too few samples."); return Result(); }
        if (connected.Count != interior) Miss("hand_disconnected_regions", "Only the volume connected to the wrist was analyzed; other interior islands remain excluded.");
        interior = connected.Count;
        int longest = connected.Max(i => distance[i]);
        double persistenceThreshold = Math.Max(3, longest * options.MinimumBranchFraction) * grid.CellSize;
        // Prefer medial samples over distant skin corners. Persistence relative
        // to branch radius rejects the short corner branches of a plain palm.
        var height = new double[grid.CellCount];
        foreach (int i in connected) height[i] = distance[i] * grid.CellSize + 2 * clearance[i];
        var parent = Enumerable.Repeat(-1, grid.CellCount).ToArray();
        var peak = new int[grid.CellCount]; var counts = new int[grid.CellCount];
        var dominantJoin = new Dictionary<int, int>();
        var candidates = new List<Branch>();
        foreach (int i in connected.OrderByDescending(i => height[i]).ThenBy(static i => i))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parent[i] = i; peak[i] = i; counts[i] = 1;
            foreach (int neighbor in Neighbors(i))
            {
                if (parent[neighbor] < 0) continue;
                int a = Find(i), b = Find(neighbor);
                if (a == b) continue;
                if (height[peak[b]] > height[peak[a]] || height[peak[b]] == height[peak[a]] && peak[b] < peak[a]) (a, b) = (b, a);
                double persistence = height[peak[b]] - height[i];
                if (persistence >= Math.Max(persistenceThreshold, 4 * clearance[peak[b]]) && counts[b] >= 4)
                {
                    candidates.Add(new(peak[b], i, persistence));
                    if (!dominantJoin.TryGetValue(peak[a], out int previous) || distance[i] > distance[previous]) dominantJoin[peak[a]] = i;
                }
                parent[b] = a; counts[a] += counts[b];
            }
        }
        int survivor = peak[Find(wrist)];
        if (dominantJoin.TryGetValue(survivor, out int rootJoin)) candidates.Add(new(survivor, rootJoin, height[survivor] - height[rootJoin]));
        candidates = candidates.OrderByDescending(static b => b.Persistence).ThenBy(static b => b.Peak).ToList();
        if (candidates.Count is < 2 or > 8)
        { Miss("hand_branch_count", $"Found {candidates.Count} persistent branches. Fused, touching or insufficiently resolved digits require local assistance."); return Result(); }
        if (candidates.Count != options.ExpectedDigits) Miss("hand_digit_count_mismatch", $"Expected {options.ExpectedDigits} digits but observed {candidates.Count} branches. Declare missing or unusual digits; none were invented.");
        double palmLimit = candidates.Min(b => distance[b.Saddle]) + 1;
        var palmPoints = connected.Where(i => distance[i] <= palmLimit).Select(i => positions[i]).ToArray();
        var (normal, flatness) = PalmNormal(palmPoints, normalHint);
        if (flatness > .65) Miss("hand_palm_orientation_ambiguous", "The local palm does not define a clearly thin plane; its orientation needs review.");
        if (!(forward - normal * Vector3D.Dot(forward, normal)).TryNormalize(out var palmForward))
        {
            Miss("hand_palm_direction_ambiguous", "The sampled palm plane conflicts with the forward hint. Review its orientation manually.");
            normal = normalHint; palmForward = forward;
        }
        forward = palmForward;
        var lateral = Vector3D.Cross(normal, forward).Normalized();
        var center = palmPoints.Aggregate(Vector3D.Zero, static (sum, p) => sum + p) / palmPoints.Length;
        palmFrame = Frame(center, forward, normal);
        var graph = VolumeInteriorGraph.BuildForRegion(grid, connected.Select(Coordinate).ToHashSet(), cancellationToken);
        var chains = new List<(Branch Branch, ImmutableArray<Vector3D> Points)>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = graph.FindPath(Coordinate(wrist), Coordinate(candidate.Peak), options.PathClearanceWeight, cancellationToken: cancellationToken);
            if (path is null) { Miss("hand_branch_disconnected", "A digit branch has no supported interior path."); continue; }
            int first = 0;
            while (first < path.Cells.Length - 1 && distance[Index(path.Cells[first])] < distance[candidate.Saddle]) first++;
            var points = path.Positions.Skip(first).ToImmutableArray();
            if (points.Length < 7) { Miss("hand_branch_short", "A digit branch has too few samples for a knuckle chain."); continue; }
            chains.Add((candidate, points));
        }
        // Digit naming is distinct from branch discovery. A proximal, lateral
        // branch may support a thumb assignment; otherwise keep anonymous IDs.
        int thumb = -1;
        if (chains.Count == 5)
        {
            var ordered = chains.Select((c, i) => (i, d: Vector3D.Dot(c.Points[0] - options.Wrist, forward))).OrderBy(static c => c.d).ThenBy(static c => c.i).ToArray();
            int first = ordered[0].i;
            double thumbLateral = Vector3D.Dot(chains[first].Points[0] - center, lateral);
            var others = chains.Where((_, i) => i != first).Select(c => Vector3D.Dot(c.Points[0] - center, lateral)).ToArray();
            if (ordered[1].d - ordered[0].d >= 2 * grid.CellSize && (thumbLateral < others.Min() || thumbLateral > others.Max())) thumb = first;
        }
        if (thumb < 0) Miss("hand_digit_names_ambiguous", "Branches were detected, but a unique thumb and finger order were not established. Assign names during hand review.");
        var digitNames = new Dictionary<int, string>();
        if (thumb >= 0)
        {
            digitNames[thumb] = "thumb";
            double sign = Math.Sign(Vector3D.Dot(chains[thumb].Points[0] - center, lateral));
            string[] names = ["index", "middle", "ring", "little"];
            var fingers = chains.Select((c, i) => (c, i)).Where(p => p.i != thumb)
                .OrderByDescending(p => sign * Vector3D.Dot(p.c.Points[0] - center, lateral)).ThenBy(static p => p.i).ToArray();
            for (int i = 0; i < fingers.Length; i++) digitNames[fingers[i].i] = names[i];
        }
        for (int i = 0; i < chains.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chain = chains[i];
            var (joints, curved) = FitKnuckles(chain.Points, grid.CellSize, cancellationToken);
            string branchId = "branch-" + chain.Branch.Peak.ToString(CultureInfo.InvariantCulture);
            string? digit = digitNames.GetValueOrDefault(i);
            string prefix = digit is null ? $"hand.{options.Side}.{branchId}" : $"finger.{options.Side}.{digit}";
            Vector3D curl = Vector3D.Cross(joints[1] - joints[0], joints[2] - joints[1]);
            bool rollAmbiguous = !curved || !curl.TryNormalize(out curl, grid.CellSize * grid.CellSize);
            if (rollAmbiguous && !Vector3D.Cross(joints[^1] - joints[0], normal).TryNormalize(out curl)) curl = lateral;
            else if (Vector3D.Dot(curl, normal) < 0) curl = -curl;
            var proposalsForChain = joints.Select((p, j) =>
            {
                string role = prefix + "." + (j + 1).ToString(CultureInfo.InvariantCulture);
                var guide = options.Guides.FirstOrDefault(g => g.Role == role);
                return guide is null ? new AnatomicalJointProposal(role, p, curved ? AnatomicalPlacementMethod.PathBend : AnatomicalPlacementMethod.ArcInterpolation,
                    curved ? .5 : .3, false, null) : new(role, guide.Position, AnatomicalPlacementMethod.UserGuide, 1, guide.Locked, null);
            }).ToImmutableArray();
            proposals.Add(new(branchId, digit, proposalsForChain, curl, rollAmbiguous,
                ArcLength(chain.Points), chain.Branch.Persistence));
        }
        if (proposals.Any(static p => p.RollAmbiguous)) Miss("hand_roll_review", "Straight or weakly bent digits derive a curl-plane proposal from palm and finger directions; joint positions and roll require calibration.");
        if (connected.Any(i => { var c = Coordinate(i); return c.X == 0 || c.Y == 0 || c.Z == 0 || c.X == grid.SizeX - 1 || c.Y == grid.SizeY - 1 || c.Z == grid.SizeZ - 1; }))
            Miss("hand_region_boundary", "The hand volume touches the sampling box boundary. Confirm that no digit was clipped from analysis.");
        return Result();

        void Miss(string code, string message) => diagnostics.Add(new(code, message, "hand." + options.Side));
        LocalHandDetectionResult Result() => new(grid.SourceSha256, grid.InputFingerprint, fingerprint, options.Side,
            diagnostics.Any(d => d.Code != "hand_roll_review") ? AnatomicalDetectionStatus.NeedsAssistance : AnatomicalDetectionStatus.Proposed,
            palmFrame, proposals.ToImmutable(), preserved, diagnostics.ToImmutable(), grid.CellSize, interior);
        VolumeGridCoordinate Coordinate(int i) => new(i % grid.SizeX, i / grid.SizeX % grid.SizeY, i / (grid.SizeX * grid.SizeY));
        int Index(VolumeGridCoordinate c) => (c.Z * grid.SizeY + c.Y) * grid.SizeX + c.X;
        IEnumerable<int> Neighbors(int i)
        {
            var c = Coordinate(i);
            if (c.X > 0) yield return i - 1; if (c.X + 1 < grid.SizeX) yield return i + 1;
            if (c.Y > 0) yield return i - grid.SizeX; if (c.Y + 1 < grid.SizeY) yield return i + grid.SizeX;
            if (c.Z > 0) yield return i - grid.SizeX * grid.SizeY; if (c.Z + 1 < grid.SizeZ) yield return i + grid.SizeX * grid.SizeY;
        }
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
    }

    private readonly record struct Branch(int Peak, int Saddle, double Persistence);
    private static TransformMatrix Frame(Vector3D origin, Vector3D forward, Vector3D normal)
    {
        var right = Vector3D.Cross(normal, forward).Normalized();
        return new(right.X, normal.X, forward.X, origin.X, right.Y, normal.Y, forward.Y, origin.Y,
            right.Z, normal.Z, forward.Z, origin.Z, 0, 0, 0, 1);
    }
    private static double ArcLength(IReadOnlyList<Vector3D> points)
    { double total = 0; for (int i = 1; i < points.Count; i++) total += (points[i] - points[i - 1]).Length; return total; }

    private static (Vector3D[] Points, bool Curved) FitKnuckles(ImmutableArray<Vector3D> path, double spacing, CancellationToken token)
    {
        int count = Math.Min(48, path.Length);
        var sampled = Enumerable.Range(0, count).Select(i => path[(int)Math.Round(i * (path.Length - 1.0) / (count - 1))]).ToArray();
        int minimum = Math.Max(1, count / 8);
        double best = double.PositiveInfinity; int first = 0, second = 0;
        double straight = sampled.Sum(p => DistanceToSegmentSquared(p, sampled[0], sampled[^1]));
        for (int a = minimum; a < count - 2 * minimum; a++) for (int b = a + minimum; b < count - minimum; b++)
        {
            token.ThrowIfCancellationRequested();
            double cost = 0;
            for (int i = 0; i < count; i++) cost += i <= a ? DistanceToSegmentSquared(sampled[i], sampled[0], sampled[a]) :
                i <= b ? DistanceToSegmentSquared(sampled[i], sampled[a], sampled[b]) : DistanceToSegmentSquared(sampled[i], sampled[b], sampled[^1]);
            // A weak straight-chain prior resolves the otherwise unobservable
            // hinge positions; these are reported as interpolation, not detection.
            cost += spacing * spacing * .01 * (Math.Pow(a / (count - 1.0) - .5, 2) + Math.Pow(b / (count - 1.0) - .8, 2));
            if (cost < best) { best = cost; first = a; second = b; }
        }
        // A few lattice steps near a cap or web are not evidence of a knuckle.
        // Require a resolved bend beyond the sampling scale before proposing roll.
        bool curved = straight - best > count * spacing * spacing * 4 &&
            sampled.Max(p => DistanceToSegmentSquared(p, sampled[0], sampled[^1])) > 9 * spacing * spacing;
        if (!curved) { first = (int)Math.Round((count - 1) * .5); second = (int)Math.Round((count - 1) * .8); }
        return ([sampled[0], sampled[first], sampled[second], sampled[^1]], curved);
    }
    private static double DistanceToSegmentSquared(Vector3D p, Vector3D a, Vector3D b)
    {
        var delta = b - a; double denominator = delta.LengthSquared;
        double t = denominator > 0 ? Math.Clamp(Vector3D.Dot(p - a, delta) / denominator, 0, 1) : 0;
        return (p - a - delta * t).LengthSquared;
    }

    private static (Vector3D Normal, double Flatness) PalmNormal(Vector3D[] points, Vector3D hint)
    {
        var center = points.Aggregate(Vector3D.Zero, static (sum, p) => sum + p) / points.Length;
        var matrix = new double[3, 3]; var vectors = new double[3, 3];
        for (int i = 0; i < 3; i++) vectors[i, i] = 1;
        foreach (var point in points)
        {
            var d = point - center; double[] values = [d.X, d.Y, d.Z];
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) matrix[i, j] += values[i] * values[j];
        }
        for (int step = 0; step < 24; step++)
        {
            int p = 0, q = 1;
            if (Math.Abs(matrix[0, 2]) > Math.Abs(matrix[p, q])) { p = 0; q = 2; }
            if (Math.Abs(matrix[1, 2]) > Math.Abs(matrix[p, q])) { p = 1; q = 2; }
            if (Math.Abs(matrix[p, q]) <= Math.Max(matrix[0, 0], Math.Max(matrix[1, 1], matrix[2, 2])) * 1e-12) break;
            double angle = .5 * Math.Atan2(2 * matrix[p, q], matrix[q, q] - matrix[p, p]);
            double c = Math.Cos(angle), s = Math.Sin(angle), pp = matrix[p, p], qq = matrix[q, q], pq = matrix[p, q];
            matrix[p, p] = c * c * pp - 2 * c * s * pq + s * s * qq;
            matrix[q, q] = s * s * pp + 2 * c * s * pq + c * c * qq; matrix[p, q] = matrix[q, p] = 0;
            for (int i = 0; i < 3; i++)
            {
                if (i != p && i != q) { double ip = matrix[i, p], iq = matrix[i, q]; matrix[i, p] = matrix[p, i] = c * ip - s * iq; matrix[i, q] = matrix[q, i] = s * ip + c * iq; }
                double vp = vectors[i, p], vq = vectors[i, q]; vectors[i, p] = c * vp - s * vq; vectors[i, q] = s * vp + c * vq;
            }
        }
        int[] order = Enumerable.Range(0, 3).OrderBy(i => matrix[i, i]).ToArray();
        var normal = new Vector3D(vectors[0, order[0]], vectors[1, order[0]], vectors[2, order[0]]).Normalized();
        if (Vector3D.Dot(normal, hint) < 0) normal = -normal;
        return (normal, matrix[order[1], order[1]] > 0 ? Math.Max(0, matrix[order[0], order[0]]) / matrix[order[1], order[1]] : 1);
    }
    private static string Fingerprint(SourceVolumeGrid grid, LocalHandDetectionOptions options)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"local-hand-detector-v1|{grid.InputFingerprint}|{options.Side}|{options.Wrist.X:R},{options.Wrist.Y:R},{options.Wrist.Z:R}|{options.Forward.X:R},{options.Forward.Y:R},{options.Forward.Z:R}|{options.PalmNormalHint.X:R},{options.PalmNormalHint.Y:R},{options.PalmNormalHint.Z:R}|{options.ExpectedDigits}|{options.MinimumBranchFraction:R}|{options.PathClearanceWeight:R}|{options.MaximumInteriorCells}");
        foreach (var g in options.Guides.OrderBy(static g => g.Role, StringComparer.Ordinal)) text.Append(CultureInfo.InvariantCulture, $"|{g.Role}:{g.Position.X:R},{g.Position.Y:R},{g.Position.Z:R},{g.Locked}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

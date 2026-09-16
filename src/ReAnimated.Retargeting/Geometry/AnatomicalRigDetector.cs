using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Retargeting.Geometry;

/// <summary>
/// Geometry-only upright-biped proposals in a supplied authoring frame. Uses persistent bilateral
/// lower sections, torso/head thickness structure and separately constrained limb paths. Joint
/// positions come from the observed volume; no native/template dimensions are applied.
/// Open fields, unresolved branches and unobservable hinges remain review/assistance cases.
/// </summary>
public static class AnatomicalRigDetector
{
    private static readonly string[] RequiredRoles = ["body.pelvis", "body.spine.1", "body.head",
        "arm.left.upper", "arm.left.lower", "hand.left", "arm.right.upper", "arm.right.lower", "hand.right",
        "leg.left.upper", "leg.left.lower", "foot.left", "leg.right.upper", "leg.right.lower", "foot.right"];

    public static AnatomicalDetectionResult Detect(SourceVolumeGrid grid, AnatomicalDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grid);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        Validate(options);
        var diagnostics = new List<AnatomicalDetectionDiagnostic> {
            new("supplied_frame", "Detection uses the supplied up/left frame. Automatic orientation and calibrated confidence remain unverified.") };
        var proposals = new Dictionary<string, AnatomicalJointProposal>(StringComparer.Ordinal);
        var regions = new AnatomicalRegion[grid.CellCount];
        var connections = new List<AnatomicalConnection>();
        bool incomplete = false;
        foreach (var guide in options.Guides.Where(static g => g.Locked))
            proposals.Add(guide.Role, new(guide.Role, guide.Position, AnatomicalPlacementMethod.UserGuide, 1, true, null));
        try
        {
            if (!grid.HasCompleteField) throw new DetectionRefusal("incomplete_volume", "A complete interior field is required; inspect the source components or provide a supported regional input.");
            if (grid.InteriorCellCount > options.MaximumInteriorCells) throw new DetectionRefusal("interior_budget", "The interior cell budget was exceeded; use a smaller region or resolution.");
            var context = new Context(grid, options.Frame, cancellationToken);
            if (context.Body.Count < 32) throw new DetectionRefusal("insufficient_interior", "Too few interior samples support anatomical detection.");
            if (context.DisconnectedCellCount > 0)
                diagnostics.Add(new("detached_regions", $"{context.DisconnectedCellCount} interior cells outside the dominant connected component were excluded from body inference."));
            var slices = context.BuildSlices();
            double low = context.Body.Min(i => context.Local[i].Y), high = context.Body.Max(i => context.Local[i].Y);
            double height = high - low;
            var pairs = new List<(int Slice, Section Left, Section Right)>();
            for (int i = 0; i < slices.Count; i++)
            {
                var slice = slices[i];
                if (slice.Height > low + .55 * height) break;
                var largest = slice.Sections.OrderByDescending(static s => s.Cells.Count).Take(2).ToArray();
                if (largest.Length != 2 || largest[1].Cells.Count < Math.Max(2, largest[0].Cells.Count / 5)) continue;
                var a = largest[0]; var b = largest[1];
                if (Math.Abs(a.LeftCoordinate - b.LeftCoordinate) < 2 * grid.CellSize * (1 - 1e-6)) continue;
                pairs.Add((i, a.LeftCoordinate > b.LeftCoordinate ? a : b, a.LeftCoordinate > b.LeftCoordinate ? b : a));
            }
            var run = LongestConsecutiveRun(pairs);
            if (run.Count < 3) throw new DetectionRefusal("bilateral_legs_unresolved", "No persistent separated lower branches were found. Check orientation, garments, touching limbs or missing anatomy.");
            // A local knee bulge or voxel contact can briefly join the legs. Once persistent lower
            // evidence exists, use the final supported pair to locate their torso merge, not that contact.
            run = pairs.Where(p => p.Slice >= run[0].Slice).ToList();
            if (run.Zip(run.Skip(1)).Any(static p => p.Second.Slice != p.First.Slice + 1))
                diagnostics.Add(new("intermittent_leg_separation", "Lower branches touch or become unresolved in some sections; the final supported separation was used and needs review."));
            int mergeSlice = run[^1].Slice + 1;
            if (mergeSlice >= slices.Count) throw new DetectionRefusal("leg_merge_unresolved", "Lower branches never merge into a supported torso region.");
            double middle = Median(run.Select(p => (p.Left.LeftCoordinate + p.Right.LeftCoordinate) / 2));
            var central = slices.Select(s => s.Sections.OrderBy(c => Math.Abs(c.LeftCoordinate - middle)).FirstOrDefault()).ToArray();
            int upperStart = mergeSlice + Math.Max(2, (slices.Count - mergeSlice) / 3);
            int upperEnd = slices.Count - 3;
            var neckCandidates = Enumerable.Range(Math.Min(upperStart, upperEnd), Math.Max(0, upperEnd - upperStart + 1))
                .Where(i => central[i] is not null && i > 0 && i + 2 < central.Length && central[i - 1] is not null)
                .Select(i => (Index: i, Area: SmoothedArea(i, central)))
                .Where(p => Enumerable.Range(p.Index + 1, central.Length - p.Index - 1).Select(i => SmoothedArea(i, central)).DefaultIfEmpty(0).Max() > p.Area * 1.3)
                .OrderBy(static p => p.Area).ThenBy(static p => p.Index).ToArray();
            if (neckCandidates.Length == 0) throw new DetectionRefusal("head_neck_unresolved", "No supported neck-to-head volume transition was found; a head/neck guide is needed.");
            int neckSlice = neckCandidates[0].Index;
            if (neckSlice - mergeSlice < 3) throw new DetectionRefusal("torso_unresolved", "Insufficient torso extent separates lower branches from the head region.");
            double torsoWidth = Median(Enumerable.Range(mergeSlice, neckSlice - mergeSlice).Where(i => central[i] is not null)
                .Select(i => (central[i]!.MaxLeft - central[i]!.MinLeft) / 2));
            torsoWidth = Math.Max(torsoWidth, 2 * grid.CellSize);
            var torsoCells = context.Body.Where(i => context.SliceIndex[i] >= mergeSlice && context.SliceIndex[i] <= neckSlice &&
                Math.Abs(context.Local[i].X - middle) <= torsoWidth + grid.CellSize * 1e-6).ToHashSet();
            if (torsoCells.Count == 0) throw new DetectionRefusal("torso_unresolved", "No central torso samples remain after branch separation.");
            foreach (int i in torsoCells) regions[i] = AnatomicalRegion.Torso;
            var torsoSections = slices.Select((_, y) => context.Section(torsoCells.Where(i => context.SliceIndex[i] == y).ToList())).ToArray();
            int pelvisEnd = mergeSlice + Math.Max(1, (neckSlice - mergeSlice) / 2);
            int pelvisSlice = Enumerable.Range(mergeSlice, pelvisEnd - mergeSlice + 1).OrderByDescending(i => SmoothedArea(i, torsoSections)).ThenBy(static i => i).First();
            int pelvis = torsoSections[pelvisSlice]?.Representative ?? context.Representative(torsoCells);
            int neck = central[neckSlice]!.Representative;
            var headCells = context.Body.Where(i => context.SliceIndex[i] > neckSlice && Math.Abs(context.Local[i].X - middle) <= Math.Max(torsoWidth, central[neckSlice]!.Radius * 3)).ToHashSet();
            if (headCells.Count < 8) throw new DetectionRefusal("head_region_unresolved", "The upper region does not contain enough head samples.");
            foreach (int i in headCells) regions[i] = AnatomicalRegion.Head;
            int head = context.Deepest(headCells);
            pelvis = GuidedIndex("body.pelvis", pelvis);
            neck = GuidedIndex("body.neck.0", neck);
            head = GuidedIndex("body.head", head);
            Add("body.pelvis", pelvis, AnatomicalPlacementMethod.SectionCenter, .65);
            Add("body.neck.0", neck, AnatomicalPlacementMethod.SectionCenter, .6);
            Add("body.head", head, AnatomicalPlacementMethod.RegionCenter, .6);
            diagnostics.Add(new("head_center_proposal", "The head proposal is a geometric interior centre; its final rotation pivot needs fitting review.", "body.head"));
            var axialRegion = torsoCells.Concat(headCells).ToHashSet();
            var axial = Path(pelvis, neck, axialRegion, "body.spine.1");
            if (axial is not null)
            {
                Add("body.spine.0", AtArc(axial, .25), AnatomicalPlacementMethod.ArcInterpolation, .45);
                Add("body.spine.1", AtArc(axial, .5), AnatomicalPlacementMethod.ArcInterpolation, .45);
                Add("body.spine.2", AtArc(axial, .75), AnatomicalPlacementMethod.ArcInterpolation, .45);
            }
            foreach (bool left in new[] { true, false })
            {
                string side = left ? "left" : "right";
                var legCells = context.Body.Where(i => context.SliceIndex[i] < mergeSlice && (context.Local[i].X > middle) == left).ToHashSet();
                var legTop = left ? run[^1].Left : run[^1].Right;
                var legBottom = left ? run[0].Left : run[0].Right;
                int hip = context.Nearest(legTop.Center + options.Frame.Up * legTop.Radius,
                    context.Body.Where(i => (context.Local[i].X > middle) == left && context.Local[i].Y <= context.Local[pelvis].Y + grid.CellSize));
                int ankle = legBottom.Representative;
                hip = GuidedIndex($"leg.{side}.upper", hip);
                ankle = GuidedIndex($"foot.{side}", ankle);
                // Extend the regional mask through the hip attachment without allowing the opposite leg.
                var legMask = legCells.Concat(context.Body.Where(i => (context.Local[i].X > middle) == left && context.SliceIndex[i] <= pelvisSlice)).ToHashSet();
                legMask.Add(hip);
                foreach (int i in legCells) regions[i] = left ? AnatomicalRegion.LeftLeg : AnatomicalRegion.RightLeg;
                Add($"leg.{side}.upper", hip, AnatomicalPlacementMethod.BranchBoundary, .6);
                Add($"foot.{side}", ankle, AnatomicalPlacementMethod.SectionCenter, .4);
                Limb($"leg.{side}.lower", hip, ankle, legMask);

                double mergeHeight = slices[mergeSlice].Height;
                var armCandidates = context.Body.Where(i => (context.Local[i].X - middle) * (left ? 1 : -1) > torsoWidth + grid.CellSize * 1e-6 &&
                    context.Local[i].Y >= mergeHeight - legTop.Radius && context.SliceIndex[i] <= neckSlice).ToHashSet();
                var armGroups = context.Components(armCandidates).Where(static g => g.Count >= 4).ToArray();
                if (armGroups.Length == 0) { Miss($"arm_{side}_unresolved", "No separated lateral arm branch was found.", $"arm.{side}.upper"); continue; }
                var arm = armGroups.OrderByDescending(g => g.Max(i => context.Local[i].Y)).ThenByDescending(static g => g.Count).First();
                var nearRoot = arm.Where(i => Math.Abs(context.Local[i].X - middle) <= torsoWidth + 1.5 * grid.CellSize).ToList();
                if (nearRoot.Count == 0) { Miss($"arm_{side}_attachment", "The lateral branch has no supported torso attachment.", $"arm.{side}.upper"); continue; }
                int shoulder = context.Deepest(nearRoot);
                var terminal = arm.OrderByDescending(i => (context.Positions[i] - context.Positions[shoulder]).LengthSquared).First();
                var endCap = arm.Where(i => (context.Positions[i] - context.Positions[terminal]).Length <= Math.Max(2 * grid.CellSize, context.Clearance[terminal] * 2)).ToList();
                int wrist = context.Representative(endCap);
                shoulder = GuidedIndex($"arm.{side}.upper", shoulder);
                wrist = GuidedIndex($"hand.{side}", wrist);
                foreach (int i in arm) regions[i] = left ? AnatomicalRegion.LeftArm : AnatomicalRegion.RightArm;
                Add($"arm.{side}.upper", shoulder, AnatomicalPlacementMethod.BranchBoundary, .6);
                Add($"hand.{side}", wrist, AnatomicalPlacementMethod.RegionCenter, .4);
                Limb($"arm.{side}.lower", shoulder, wrist, arm);
            }
            Connect("body.pelvis", "body.spine.0"); Connect("body.spine.0", "body.spine.1"); Connect("body.spine.1", "body.spine.2");
            Connect("body.spine.2", "body.neck.0"); Connect("body.neck.0", "body.head");
            foreach (string side in new[] { "left", "right" })
            {
                Connect("body.spine.2", $"arm.{side}.upper"); Connect($"arm.{side}.upper", $"arm.{side}.lower"); Connect($"arm.{side}.lower", $"hand.{side}");
                Connect("body.pelvis", $"leg.{side}.upper"); Connect($"leg.{side}.upper", $"leg.{side}.lower"); Connect($"leg.{side}.lower", $"foot.{side}");
            }
            foreach (var guide in options.Guides)
                if (!context.TryCell(guide.Position, out int cell) || !context.Body.Contains(cell))
                    Miss("guide_outside_supported_region", "A guide is outside the sampled body interior; its requested position was retained.", guide.Role);
            ValidateSides("leg.left.upper", "leg.right.upper"); ValidateSides("leg.left.lower", "leg.right.lower"); ValidateSides("foot.left", "foot.right");
            ValidateSides("arm.left.upper", "arm.right.upper"); ValidateSides("arm.left.lower", "arm.right.lower"); ValidateSides("hand.left", "hand.right");

            int GuidedIndex(string role, int fallback)
            {
                var guide = options.Guides.FirstOrDefault(g => g.Role == role);
                if (guide is null) return fallback;
                if (context.TryCell(guide.Position, out int cell) && context.Body.Contains(cell)) return cell;
                Miss("guide_outside_supported_region", "The exact guide was retained, but it cannot constrain an interior path at this resolution.", role);
                return fallback;
            }

            void Add(string role, int index, AnatomicalPlacementMethod method, double strength)
            {
                var guide = options.Guides.FirstOrDefault(g => g.Role == role);
                if (guide is not null)
                {
                    proposals[role] = new(role, guide.Position, AnatomicalPlacementMethod.UserGuide, 1, guide.Locked,
                        context.TryCell(guide.Position, out int guidedCell) && context.Body.Contains(guidedCell) ? context.Coordinate(guidedCell) : null);
                    return;
                }
                proposals[role] = new(role, context.Positions[index], method, strength, false, context.Coordinate(index));
            }
            SourceVolumeGridPath? Path(int start, int end, HashSet<int> cells, string role)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cells.Count == 0) { Miss("empty_region", "No usable regional path cells remain.", role); return null; }
                if (!cells.Contains(start) || !cells.Contains(end)) { Miss("guide_outside_chain_region", "A requested endpoint is outside this anatomical region; no snapping or cross-region path was substituted.", role); return null; }
                var graph = VolumeInteriorGraph.BuildForRegion(grid, cells.Select(context.Coordinate).ToHashSet(), cancellationToken);
                var path = graph.FindPath(context.Coordinate(start), context.Coordinate(end), options.PathClearanceWeight, cancellationToken: cancellationToken);
                if (path is null) Miss("disconnected_chain", "A limb or torso chain is disconnected in the sampled region.", role);
                return path;
            }
            int AtArc(SourceVolumeGridPath path, double fraction) => context.Index(path.Cells[(int)Math.Round((path.Cells.Length - 1) * fraction)]);
            void Limb(string role, int start, int end, HashSet<int> cells)
            {
                var guide = options.Guides.FirstOrDefault(g => g.Role == role);
                if (guide is not null)
                {
                    int pivot = GuidedIndex(role, start);
                    Add(role, pivot, AnatomicalPlacementMethod.UserGuide, 1);
                    _ = Path(start, pivot, cells, role);
                    _ = Path(pivot, end, cells, role);
                    return;
                }
                var path = Path(start, end, cells, role);
                if (path is not null) Hinge(role, path);
            }
            void Hinge(string role, SourceVolumeGridPath path)
            {
                if (path.Cells.Length < 5) { Add(role, AtArc(path, .5), AnatomicalPlacementMethod.ArcInterpolation, .25); Miss("short_chain", "The chain has too few samples to resolve an interior hinge.", role); return; }
                int[] samples = Enumerable.Range(0, Math.Min(128, path.Cells.Length)).Select(i => (int)Math.Round(i * (path.Cells.Length - 1.0) / (Math.Min(128, path.Cells.Length) - 1))).Distinct().ToArray();
                int first = Math.Max(2, samples.Length / 5), last = Math.Min(samples.Length - 3, samples.Length * 4 / 5);
                Vector3D start = path.Positions[0], end = path.Positions[^1];
                double straightResidual = samples.Sum(i => SegmentDistanceSquared(path.Positions[i], start, end));
                var bends = Enumerable.Range(first, Math.Max(0, last - first + 1)).Select(i => {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pivot = path.Positions[samples[i]];
                    double residual = 0;
                    for (int j = 0; j < samples.Length; j++)
                        residual += j <= i ? SegmentDistanceSquared(path.Positions[samples[j]], start, pivot) : SegmentDistanceSquared(path.Positions[samples[j]], pivot, end);
                    var a = pivot - start; var b = end - pivot;
                    double angle = a.TryNormalize(out var an) && b.TryNormalize(out var bn) ? 1 - Vector3D.Dot(an, bn) : 0;
                    return (Index: samples[i], Bend: angle, Residual: residual);
                }).OrderBy(static b => b.Residual).ThenBy(b => Math.Abs(b.Index - path.Cells.Length / 2)).ToArray();
                // A lattice staircase is not an anatomical hinge. Require a two-segment fit to improve
                // on the full-chain line by more than sampling noise, with a measurable angular change.
                if (bends.Length > 0 && bends[0].Bend > .1 && straightResidual > grid.CellSize * grid.CellSize * samples.Length * .25 &&
                    bends[0].Residual < straightResidual * .6)
                    Add(role, context.Index(path.Cells[bends[0].Index]), AnatomicalPlacementMethod.PathBend, .6);
                else
                {
                    Add(role, AtArc(path, .5), AnatomicalPlacementMethod.ArcInterpolation, .3);
                    diagnostics.Add(new("unobservable_hinge", "No clear hinge bend was resolved; the path midpoint is a reviewable interpolation.", role));
                }
            }
            void Connect(string parent, string child)
            {
                if (proposals.ContainsKey(parent) && proposals.ContainsKey(child)) connections.Add(new(parent, child));
            }
            void ValidateSides(string left, string right)
            {
                if (proposals.TryGetValue(left, out var l) && proposals.TryGetValue(right, out var r) && Vector3D.Dot(l.Position - r.Position, options.Frame.Left) <= 0)
                    Miss("side_constraint", "Left/right proposals conflict with the supplied frame or guides; guide positions were not moved.", left);
            }
        }
        catch (DetectionRefusal refusal) { Miss(refusal.Code, refusal.Message); }
        foreach (string role in RequiredRoles.Where(r => !proposals.ContainsKey(r))) Miss("missing_joint", "The body proposal set is incomplete.", role);
        string key = string.Create(CultureInfo.InvariantCulture, $"anatomical-volume-v1|{options.Frame.Up.X:R},{options.Frame.Up.Y:R},{options.Frame.Up.Z:R}|{options.Frame.Left.X:R},{options.Frame.Left.Y:R},{options.Frame.Left.Z:R}|{options.PathClearanceWeight:R}|{options.MaximumInteriorCells}|") +
            string.Join(";", options.Guides.OrderBy(static g => g.Role, StringComparer.Ordinal).Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Role}:{g.Position.X:R},{g.Position.Y:R},{g.Position.Z:R}:{g.Locked}")));
        string configuration = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        string input = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(grid.InputFingerprint + "|" + configuration)));
        return new(grid.SourceSha256, grid.InputFingerprint, input, configuration,
            incomplete ? AnatomicalDetectionStatus.NeedsAssistance : AnatomicalDetectionStatus.Proposed,
            proposals.Values.OrderBy(static p => p.Role, StringComparer.Ordinal).ToImmutableArray(), connections.ToImmutableArray(), regions.ToImmutableArray(), diagnostics.ToImmutableArray(), grid.CellSize);
        void Miss(string code, string message, string? role = null) { incomplete = true; diagnostics.Add(new(code, message, role)); }
    }

    private static void Validate(AnatomicalDetectionOptions options)
    {
        if (options.Frame is null || !options.Frame.Up.IsFinite || !options.Frame.Left.IsFinite || Math.Abs(options.Frame.Up.Length - 1) > 1e-6 ||
            Math.Abs(options.Frame.Left.Length - 1) > 1e-6 || Math.Abs(Vector3D.Dot(options.Frame.Up, options.Frame.Left)) > 1e-6 ||
            options.MaximumInteriorCells is < 1 or > 2_000_000 || options.PathClearanceWeight is < 0 or > VolumeInteriorGraph.MaximumClearanceWeight || !double.IsFinite(options.PathClearanceWeight) || options.Guides.IsDefault)
            throw new ArgumentException("Anatomical detection requires an orthonormal frame, initialized guides and supported budgets.", nameof(options));
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var guide in options.Guides)
            if (guide is null || string.IsNullOrWhiteSpace(guide.Role) || !guide.Position.IsFinite || !roles.Add(guide.Role))
                throw new ArgumentException("Anatomical guides need unique roles and finite positions.", nameof(options));
    }
    private static double Median(IEnumerable<double> values) { var sorted = values.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2]; }
    private static double SegmentDistanceSquared(Vector3D point, Vector3D start, Vector3D end)
    {
        Vector3D segment = end - start;
        double length = segment.LengthSquared;
        double t = length > 0 ? Math.Clamp(Vector3D.Dot(point - start, segment) / length, 0, 1) : 0;
        return (point - (start + segment * t)).LengthSquared;
    }
    private static double SmoothedArea(int index, Section?[] sections) => Enumerable.Range(Math.Max(0, index - 1), Math.Min(sections.Length - 1, index + 1) - Math.Max(0, index - 1) + 1).Average(i => sections[i]?.Cells.Count ?? 0);
    private static List<(int Slice, Section Left, Section Right)> LongestConsecutiveRun(List<(int Slice, Section Left, Section Right)> pairs)
    {
        var best = new List<(int, Section, Section)>(); var current = new List<(int, Section, Section)>(); int previous = -2;
        foreach (var pair in pairs) { if (pair.Slice != previous + 1) current.Clear(); current.Add(pair); previous = pair.Slice; if (current.Count > best.Count) best = [.. current]; }
        return best;
    }
    private sealed class DetectionRefusal(string code, string message) : Exception(message) { public string Code { get; } = code; }
    private sealed record Section(List<int> Cells, Vector3D Center, int Representative, double LeftCoordinate, double MinLeft, double MaxLeft, double Radius);
    private sealed record Slice(double Height, List<Section> Sections);

    private sealed class Context
    {
        private readonly SourceVolumeGrid _grid;
        private readonly CancellationToken _cancellation;
        public Vector3D[] Positions { get; }
        public Vector3D[] Local { get; }
        public double[] Clearance { get; }
        public int[] SliceIndex { get; }
        public HashSet<int> Body { get; }
        public int DisconnectedCellCount { get; }
        public Context(SourceVolumeGrid grid, AnatomicalDetectionFrame frame, CancellationToken cancellation)
        {
            _grid = grid; _cancellation = cancellation;
            Positions = new Vector3D[grid.CellCount]; Local = new Vector3D[grid.CellCount]; Clearance = new double[grid.CellCount]; SliceIndex = new int[grid.CellCount];
            var interior = new HashSet<int>();
            var depth = Vector3D.Cross(frame.Left, frame.Up);
            for (int i = 0; i < grid.CellCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var c = Coordinate(i); var cell = grid.GetCell(c.X, c.Y, c.Z);
                if (cell.Location != SourceVolumeLocation.Interior || cell.SignedField is not < 0) continue;
                var point = grid.GetPosition(c.X, c.Y, c.Z); Positions[i] = point;
                Local[i] = new(Vector3D.Dot(point, frame.Left), Vector3D.Dot(point, frame.Up), Vector3D.Dot(point, depth));
                Clearance[i] = -cell.SignedField.Value; interior.Add(i);
            }
            Body = Components(interior).OrderByDescending(static c => c.Count).ThenBy(static c => c.Min()).FirstOrDefault() ?? [];
            DisconnectedCellCount = interior.Count - Body.Count;
        }
        public List<Slice> BuildSlices()
        {
            double min = Body.Min(i => Local[i].Y);
            foreach (int i in Body) SliceIndex[i] = (int)Math.Floor((Local[i].Y - min) / _grid.CellSize + 1e-7);
            int count = Body.Max(i => SliceIndex[i]) + 1;
            var lists = Enumerable.Range(0, count).Select(static _ => new HashSet<int>()).ToArray();
            foreach (int i in Body) lists[SliceIndex[i]].Add(i);
            return lists.Select((cells, y) => new Slice(min + y * _grid.CellSize, Components(cells, planar: true).Select(g => Section(g.ToList())!).ToList())).ToList();
        }
        public List<HashSet<int>> Components(HashSet<int> cells, bool planar = false)
        {
            var remaining = new HashSet<int>(cells); var groups = new List<HashSet<int>>(); var queue = new Queue<int>();
            foreach (int seed in cells.Order())
            {
                _cancellation.ThrowIfCancellationRequested();
                if (!remaining.Remove(seed)) continue;
                var group = new HashSet<int> { seed }; queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    int current = queue.Dequeue();
                    foreach (int neighbor in Neighbors(current, planar))
                        if (remaining.Remove(neighbor)) { group.Add(neighbor); queue.Enqueue(neighbor); }
                }
                groups.Add(group);
            }
            return groups;
        }
        private IEnumerable<int> Neighbors(int index, bool diagonal)
        {
            var c = Coordinate(index);
            for (int dz = -1; dz <= 1; dz++) for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
            {
                int length = Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz);
                if (length == 0 || !diagonal && length != 1) continue;
                int x = c.X + dx, y = c.Y + dy, z = c.Z + dz;
                if ((uint)x < (uint)_grid.SizeX && (uint)y < (uint)_grid.SizeY && (uint)z < (uint)_grid.SizeZ) yield return (z * _grid.SizeY + y) * _grid.SizeX + x;
            }
        }
        public Section? Section(List<int> cells) => cells.Count == 0 ? null : new(cells, Mean(cells), Representative(cells),
            cells.Average(i => Local[i].X), cells.Min(i => Local[i].X), cells.Max(i => Local[i].X), cells.Max(i => Clearance[i]));
        private Vector3D Mean(IEnumerable<int> cells)
        {
            Vector3D sum = Vector3D.Zero; double mass = 0;
            foreach (int i in cells) { double weight = Math.Max(_grid.CellSize * .1, Clearance[i]); sum += Positions[i] * weight; mass += weight; }
            return sum / mass;
        }
        public int Representative(IEnumerable<int> cells) { var array = cells.ToArray(); return Nearest(Mean(array), array); }
        public int Nearest(Vector3D point, IEnumerable<int> cells) => cells.OrderBy(i => (Positions[i] - point).LengthSquared).ThenBy(static i => i).First();
        public int Deepest(IEnumerable<int> cells) => cells.OrderByDescending(i => Clearance[i]).ThenBy(static i => i).First();
        public VolumeGridCoordinate Coordinate(int i) => new(i % _grid.SizeX, i / _grid.SizeX % _grid.SizeY, i / (_grid.SizeX * _grid.SizeY));
        public int Index(VolumeGridCoordinate c) => (c.Z * _grid.SizeY + c.Y) * _grid.SizeX + c.X;
        public bool TryCell(Vector3D point, out int index)
        {
            Vector3D p = (point - _grid.Min) / _grid.CellSize;
            if (!p.IsFinite || p.X < 0 || p.Y < 0 || p.Z < 0 || p.X >= _grid.SizeX || p.Y >= _grid.SizeY || p.Z >= _grid.SizeZ) { index = -1; return false; }
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y), z = (int)Math.Floor(p.Z);
            if ((uint)x >= (uint)_grid.SizeX || (uint)y >= (uint)_grid.SizeY || (uint)z >= (uint)_grid.SizeZ) { index = -1; return false; }
            index = (z * _grid.SizeY + y) * _grid.SizeX + x; return true;
        }
    }
}

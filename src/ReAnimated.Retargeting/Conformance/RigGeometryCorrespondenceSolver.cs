using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.Retargeting.Conformance;

/// <summary>
/// Deterministic geometry-assisted existing-rig proposals. Preserves source data and reports candidates
/// for review. The template supplies role relationships; this solver never changes source proportions.
/// Scores are heuristics, not calibrated probabilities or native compatibility evidence.
/// </summary>
internal static class RigGeometryCorrespondenceSolver
{
    public static RigCorrespondence Solve(Dl1RigTemplate template, RigDefinition source,
        RigCorrespondenceOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var geometry = options.GeometryEvidence!;
        geometry.Validate(source);
        if (source.BoneCount > 4096 || template.EntityCount > 4096)
            throw new ArgumentException("Geometry correspondence currently supports up to 4096 source and target entities.");
        var support = geometry.Supports.ToDictionary(static s => s.BoneIndex);
        var supportedDescendants = new int[source.BoneCount];
        foreach (var pair in support.Where(static pair => pair.Value.SurfaceMass > 0)) supportedDescendants[pair.Key] = 1;
        for (int i = source.BoneCount - 1; i >= 0; i--)
            if (source.Bones[i].ParentIndex >= 0) supportedDescendants[source.Bones[i].ParentIndex] += supportedDescendants[i];
        int sourceAnchor = Enumerable.Range(0, source.BoneCount).OrderByDescending(i => supportedDescendants[i]).ThenBy(static i => i).First();
        var sourceFrame = Normalization.Create(geometry.GlobalBindMatrices.Where((_, i) => supportedDescendants[i] > 0).Select(static m => m.Translation),
            geometry.GlobalBindMatrices[sourceAnchor].Translation);
        Vector3D targetAnchor = (template.Entities.FirstOrDefault(e => Role(e) == "body.pelvis") ?? template.Entities[0]).GlobalRestMatrix.Translation;
        var targetFrame = Normalization.Create(template.Entities.Where(e => Role(e) is not null &&
            (e.IsDeform || Role(e) == "body.root")).Select(static e => e.GlobalRestMatrix.Translation), targetAnchor);
        var sourceRoles = source.Bones.Select(b => b.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(b.Name)?.Role).ToArray();
        var targetRoles = template.Entities.Select(Role).ToArray();
        var sourceHierarchy = new Hierarchy(source.Bones.Select(static b => b.ParentIndex).ToArray(), cancellationToken);
        var targetHierarchy = new Hierarchy(template.Entities.Select(static e => e.ParentIndex).ToArray(), cancellationToken);
        var sourceDirections = Enumerable.Range(0, source.BoneCount).Select(i => Directions(i, sourceHierarchy,
            j => geometry.GlobalBindMatrices[j].Translation, j => supportedDescendants[j] > 0, cancellationToken)).ToArray();
        var targetDirections = Enumerable.Range(0, template.EntityCount).Select(i => Directions(i, targetHierarchy,
            j => template[j].GlobalRestMatrix.Translation, j => targetRoles[j] is not null, cancellationToken)).ToArray();
        var overrides = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pair in options.RoleOverrides)
        {
            int index = source.GetBoneIndex(pair.Value);
            if (index < 0 || options.ExcludedSourceBones.Contains(pair.Value))
                throw new ArgumentException($"Role override '{pair.Key}' must select a unique, non-excluded source bone.");
            if (!template.Entities.Any(e => Role(e) == pair.Key))
                throw new ArgumentException($"Role override '{pair.Key}' has no target role.");
            if (overrides.ContainsValue(index)) throw new ArgumentException("Two role overrides cannot claim the same source bone.");
            overrides.Add(pair.Key, index);
        }
        var reserved = overrides.Values.ToHashSet();
        var anchors = new Dictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var target in template.Entities)
        {
            string? targetRole = Role(target);
            if (targetRole is null) continue;
            if (overrides.TryGetValue(targetRole, out int anchor)) { anchors[targetRole] = [anchor]; continue; }
            Vector3D expected = targetFrame.Apply(target.GlobalRestMatrix.Translation);
            int[] matches = source.Bones.Where(b => sourceRoles[b.Index] == targetRole && support.TryGetValue(b.Index, out var s) && s.SurfaceMass > 0 &&
                AnchorSegmentAgrees(target, b.Index) &&
                Math.Exp(-24 * (sourceFrame.Apply(geometry.GlobalBindMatrices[b.Index].Translation) - expected).LengthSquared) >= 0.5)
                .Select(static b => b.Index).ToArray();
            if (matches.Length > 0) anchors[targetRole] = matches;
        }
        bool AnchorSegmentAgrees(Dl1RigTemplateEntity target, int index)
        {
            int parent = source.Bones[index].ParentIndex;
            if (parent < 0) return Role(target) is "body.root" or "body.pelvis";
            if (target.ParentIndex < 0) return false;
            int referenceParent = target.ParentIndex;
            while (referenceParent >= 0 &&
                (target.GlobalRestMatrix.Translation - template[referenceParent].GlobalRestMatrix.Translation).Length <= 1e-8 * targetFrame.Height)
                referenceParent = template[referenceParent].ParentIndex;
            double referenceLength = referenceParent < 0 ? 0 :
                (target.GlobalRestMatrix.Translation - template[referenceParent].GlobalRestMatrix.Translation).Length / targetFrame.Height;
            double sourceLength = (geometry.GlobalBindMatrices[index].Translation - geometry.GlobalBindMatrices[parent].Translation).Length / sourceFrame.Height;
            return sourceLength <= Math.Max(0.1, referenceLength * 2);
        }
        var claimed = new HashSet<int>();
        var mapped = new Dictionary<int, int>();
        var rows = ImmutableArray.CreateBuilder<RigCorrespondenceRow>();
        var ambiguities = ImmutableArray.CreateBuilder<RigCorrespondenceAmbiguity>();
        foreach (var entity in template.Entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? role = Role(entity);
            int[][] descendantAnchors = template.Entities.Where(e => targetHierarchy.Distance(entity.Index, e.Index) > 0 && targetRoles[e.Index] is { } r && anchors.ContainsKey(r))
                .Select(e => anchors[Role(e)!]).ToArray();
            var candidates = new List<RigCorrespondenceCandidateEvidence>();
            int manual = role is not null && overrides.TryGetValue(role, out int selected) ? selected : -1;
            if (role is not null)
            {
                foreach (var bone in source.Bones)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (claimed.Contains(bone.Index) || options.ExcludedSourceBones.Contains(bone.Name) ||
                        reserved.Contains(bone.Index) && bone.Index != manual) continue;
                    bool explicitChoice = bone.Index == manual;
                    bool nameAgrees = sourceRoles[bone.Index] == role;
                    bool hasSupport = support.TryGetValue(bone.Index, out var region) && region.SurfaceMass > 0;
                    // Many imported humanoids start at the pelvis. Do not consume that weighted anatomical
                    // node as an extra motion root just because the native template has a separate root.
                    if (!explicitChoice && role == "body.root" && !nameAgrees &&
                        (hasSupport || sourceRoles[bone.Index] is not null || bone.ParentIndex >= 0)) continue;
                    if (!explicitChoice && (bone.Kind is BoneKind.Camera or BoneKind.Prop ||
                        HumanoidBoneSemanticClassifier.IsStructuralName(bone.Name) ||
                        bone.Kind == BoneKind.Helper && !hasSupport && role != "body.root")) continue;
                    if (!explicitChoice && !nameAgrees && (!entity.IsDeform && role != "body.root" || supportedDescendants[bone.Index] == 0)) continue;
                    // A plausible local match cannot become the ancestor of a body branch when all
                    // independently supported role anchors for that branch live elsewhere in the rig.
                    if (!explicitChoice && descendantAnchors.Any(group => !group.Any(anchor => sourceHierarchy.Distance(bone.Index, anchor) > 0))) continue;
                    double hierarchy = HierarchyAgreement(targetHierarchy, sourceHierarchy, entity.Index, bone.Index, mapped);
                    if (!explicitChoice && hierarchy < 0) continue;
                    if (hierarchy >= 0)
                        hierarchy = 0.5 * hierarchy + 0.5 * DirectionAgreement(targetDirections[entity.Index], sourceDirections[bone.Index]);
                    Vector3D sourcePoint = sourceFrame.Apply(geometry.GlobalBindMatrices[bone.Index].Translation);
                    Vector3D targetPoint = targetFrame.Apply(entity.GlobalRestMatrix.Translation);
                    double position = Math.Exp(-24 * (sourcePoint - targetPoint).LengthSquared);
                    if (!explicitChoice && !nameAgrees && position < 0.65) continue;
                    // A misleading anatomical label is evidence, not identity. Require strong independent
                    // positional, ancestry and surface support before contradicting it, and retain review.
                    if (!explicitChoice && !nameAgrees && sourceRoles[bone.Index] is not null &&
                        (!hasSupport || position < 0.85 || hierarchy < 0.8)) continue;
                    double influence = hasSupport ? 1.0 : supportedDescendants[bone.Index] > 0 ? 0.35 : 0.0;
                    double nameSupport = nameAgrees ? hasSupport || supportedDescendants[bone.Index] > 0 ? 0.20 : 0.05 : 0;
                    double score = explicitChoice ? 1 : 0.35 * position + 0.25 * hierarchy + 0.20 * influence + nameSupport;
                    if (!explicitChoice && role == "body.root")
                        score = 0.25 * hierarchy + 0.5 * (nameAgrees ? 1 : 0.8) + 0.20 + 0.05 * position;
                    if (!explicitChoice && score < 0.58) continue;
                    string evidence = explicitChoice ? "explicit user choice; source data retained" : string.Create(CultureInfo.InvariantCulture,
                        $"position {position:0.000}; hierarchy {hierarchy:0.000}; selected-surface support {influence:0.000}; name agreement {nameAgrees}; source name role '{sourceRoles[bone.Index] ?? "unknown"}'; {geometry.GeometryBasis}");
                    candidates.Add(new(bone.Index, bone.Name, score, position, Math.Max(0, hierarchy), influence, nameAgrees, explicitChoice, evidence));
                }
            }
            var ordered = candidates.OrderByDescending(static c => c.UserSelected).ThenByDescending(static c => c.Score)
                .ThenBy(static c => c.SourceBoneIndex).ToImmutableArray();
            if (ordered.Length > 0)
            {
                var chosen = ordered[0];
                bool review = !chosen.UserSelected && (!chosen.NameAgrees || ordered.Length > 1);
                claimed.Add(chosen.SourceBoneIndex);
                mapped.Add(entity.Index, chosen.SourceBoneIndex);
                rows.Add(new() { Disposition = RigBoneDisposition.Mapped, TemplateIndex = entity.Index, SourceBoneIndex = chosen.SourceBoneIndex,
                    Name = entity.Name, TemplateName = entity.Name, SourceName = chosen.SourceName, Role = role, Confidence = Math.Min(chosen.Score, chosen.UserSelected ? 1 : 0.95),
                    Evidence = chosen.Evidence, WasAmbiguous = review, Candidates = ordered });
                if (review || ordered.Length > 1)
                    ambiguities.Add(new(role!, entity.Name, ordered.Select(static c => c.SourceName).ToImmutableArray(), chosen.SourceName,
                        review ? "geometry/hierarchy proposal requires review; scores are not calibrated confidence" : "candidate alternatives retained for review"));
            }
            else
            {
                if (manual >= 0) throw new ArgumentException($"Explicit role '{role}' could not claim its selected source bone.");
                rows.Add(new() { Disposition = RigBoneDisposition.Synthesized, TemplateIndex = entity.Index, SourceBoneIndex = -1, Name = entity.Name,
                    TemplateName = entity.Name, Role = role, Confidence = role is null ? 1 : 0,
                    Evidence = role is null ? "structural target entity; no anatomical proposal" : "no supported correspondence; source anatomy requires review" });
            }
        }
        foreach (var bone in source.Bones.Where(b => !claimed.Contains(b.Index)))
        {
            bool excluded = options.DropExtraBones || options.ExcludedSourceBones.Contains(bone.Name);
            rows.Add(new() { Disposition = excluded ? RigBoneDisposition.Dropped : RigBoneDisposition.Extra, TemplateIndex = -1,
                SourceBoneIndex = bone.Index, Name = bone.Name, SourceName = bone.Name, Role = sourceRoles[bone.Index], Confidence = 1,
                Evidence = excluded ? "explicitly excluded; application must remap its weights" : "extra source anatomy retained with its original binding" });
        }
        return new(template.TemplateId, rows, ambiguities);
    }

    private static string? Role(Dl1RigTemplateEntity entity) => entity.SemanticRole ?? HumanoidBoneSemanticClassifier.Classify(entity.Name)?.Role;

    private static double DirectionAgreement(List<Vector3D> expected, List<Vector3D> actual)
    {
        if (expected.Count == 0) return 1;
        if (actual.Count == 0) return 0;
        return expected.Average(direction => actual.Max(other => Math.Max(0, Vector3D.Dot(direction, other))));
    }

    private static List<Vector3D> Directions(int root, Hierarchy hierarchy, Func<int, Vector3D> position, Func<int, bool> eligible,
        CancellationToken cancellationToken)
    {
        var result = new List<Vector3D>();
        foreach (int child in hierarchy.Children[root])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!eligible(child)) continue;
            var queue = new Queue<int>();
            queue.Enqueue(child);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int next = queue.Dequeue();
                if ((position(next) - position(root)).TryNormalize(out var direction)) { result.Add(direction); break; }
                foreach (int descendant in hierarchy.Children[next])
                    if (eligible(descendant)) queue.Enqueue(descendant);
            }
        }
        return result;
    }

    private static double HierarchyAgreement(Hierarchy template, Hierarchy source, int entity,
        int candidate, Dictionary<int, int> mapped)
    {
        int closestTarget = -1, closestSource = -1, targetDistance = int.MaxValue;
        foreach (var pair in mapped)
        {
            int distance = template.Distance(pair.Key, entity);
            int sourceDistance = source.Distance(pair.Value, candidate);
            if (distance > 0)
            {
                if (sourceDistance <= 0) return -1;
                if (distance < targetDistance) { targetDistance = distance; closestTarget = pair.Key; closestSource = pair.Value; }
            }
            else if (sourceDistance > 0 || source.Distance(candidate, pair.Value) > 0) return -1;
        }
        if (closestTarget < 0) return source.Parents[candidate] < 0 ? 1 : 0.6;
        int hops = source.Distance(closestSource, candidate);
        return 1 / (1 + 0.25 * Math.Abs(hops - targetDistance));
    }

    private sealed class Hierarchy
    {
        public int[] Parents { get; }
        public List<int>[] Children { get; }
        private readonly int[] _entry;
        private readonly int[] _exit;
        private readonly int[] _depth;
        public Hierarchy(int[] parents, CancellationToken cancellationToken)
        {
            Parents = parents;
            Children = Enumerable.Range(0, parents.Length).Select(static _ => new List<int>()).ToArray();
            _entry = new int[parents.Length]; _exit = new int[parents.Length]; _depth = new int[parents.Length];
            for (int i = 0; i < parents.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parents[i] >= 0) { Children[parents[i]].Add(i); _depth[i] = _depth[parents[i]] + 1; }
            }
            var pending = new Stack<(int Node, bool Exit)>();
            for (int i = parents.Length - 1; i >= 0; i--) if (parents[i] < 0) pending.Push((i, false));
            int clock = 0;
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = pending.Pop();
                if (step.Exit) { _exit[step.Node] = clock++; continue; }
                _entry[step.Node] = clock++;
                pending.Push((step.Node, true));
                for (int i = Children[step.Node].Count - 1; i >= 0; i--) pending.Push((Children[step.Node][i], false));
            }
        }
        public int Distance(int ancestor, int child) => _entry[ancestor] <= _entry[child] && _exit[child] <= _exit[ancestor]
            ? _depth[child] - _depth[ancestor] : -1;
    }

    private readonly record struct Normalization(Vector3D Center, double Height)
    {
        public Vector3D Apply(Vector3D value) => (value - Center) / Height;
        public static Normalization Create(IEnumerable<Vector3D> points, Vector3D horizontalOrigin)
        {
            var values = points.ToArray();
            if (values.Length == 0) throw new ArgumentException("Geometry correspondence requires selected surface influence support.");
            Vector3D min = new(values.Min(static p => p.X), values.Min(static p => p.Y), values.Min(static p => p.Z));
            Vector3D max = new(values.Max(static p => p.X), values.Max(static p => p.Y), values.Max(static p => p.Z));
            double height = Math.Max(max.Y - min.Y, Math.Max(max.X - min.X, max.Z - min.Z) * 0.25);
            if (!double.IsFinite(height) || height <= 1e-12) throw new ArgumentException("Geometry correspondence has no usable spatial extent.");
            // Forward-reaching hands, weapons and arm pose must not shift the torso's reference plane.
            return new(new(horizontalOrigin.X, (min.Y + max.Y) / 2, horizontalOrigin.Z), height);
        }
    }
}

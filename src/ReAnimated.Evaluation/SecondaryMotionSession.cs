using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Evaluation;

public sealed record SecondaryMotionFrame(ImmutableArray<TransformMatrix> Globals, TransformMatrix ActorWorldTransform);
public sealed record SecondaryMotionParticleState(string Group, int Index, bool Fixed, Vector3D WorldPosition);
public sealed record SecondaryMotionResult(
    ImmutableArray<TransformMatrix> Globals,
    ImmutableArray<SecondaryMotionParticleState> Particles,
    ImmutableArray<string> Diagnostics);

/// <summary>
/// Deterministic, preview-only MPC approximation. Supply the physical model pose and actor placement:
/// final display pose for TPP, authored pose before view-only compensation for FPP. Inputs are never
/// mutated or exported. The callback is a pure random
/// access sampler in seconds. Reset when the clip, model, parameters, or evaluation inputs change.
/// No wall-clock time is consumed, so pause, seek and frame-rate changes cannot advance the solver.
/// This session is owned by one preview timeline and is not thread-safe.
/// </summary>
public sealed class SecondaryMotionSession
{
    public const int StepsPerSecond = 120;
    public const int SolverIterations = 8;
    public const string ApproximationLabel = "MPC preview approximation";
    private const double Step = 1.0 / StepsPerSecond;
    private readonly SecondaryMotionDefinition definition;
    private readonly Dictionary<string, int> bones;
    private readonly int boneCount;
    private readonly SecondaryMotionFrame? initializationPose;
    private readonly double initializationSeconds;
    private readonly int[]? initializationParents;
    private GroupState[]? state;
    private long tick;

    public SecondaryMotionSession(SecondaryMotionDefinition definition, IEnumerable<string> boneNames,
        SecondaryMotionFrame? initializationPose = null, double initializationSeconds = 0.5,
        IEnumerable<int>? parentIndices = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(boneNames);
        string[] names = boneNames.ToArray();
        definition.Validate(names);
        this.definition = definition;
        bones = names.Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index, StringComparer.Ordinal);
        boneCount = names.Length;
        if (!double.IsFinite(initializationSeconds) || initializationSeconds < 0 || initializationSeconds > 2)
            throw new ArgumentOutOfRangeException(nameof(initializationSeconds));
        if (initializationPose is not null && (initializationPose.Globals.IsDefault || initializationPose.Globals.Length != boneCount ||
            initializationPose.Globals.Any(m => !m.IsFinite) || !initializationPose.ActorWorldTransform.IsFinite))
            throw new ArgumentException("Invalid secondary initialization pose.", nameof(initializationPose));
        if (initializationPose is not null) ValidateActorPlacement(initializationPose.ActorWorldTransform, nameof(initializationPose));
        this.initializationPose = initializationPose;
        this.initializationSeconds = initializationSeconds;
        initializationParents = parentIndices?.ToArray();
        if (initializationParents is not null && (initializationParents.Length != boneCount ||
            initializationParents.Where((parent, index) => parent < -1 || parent >= index).Any()))
            throw new ArgumentException("Initialization parents must match the topological bone order.", nameof(parentIndices));
    }

    public void Reset() { state = null; tick = 0; }

    public SecondaryMotionResult Sample(double timeSeconds, Func<double, SecondaryMotionFrame> sampleFinalDisplay)
    {
        ArgumentNullException.ThrowIfNull(sampleFinalDisplay);
        if (!double.IsFinite(timeSeconds) || timeSeconds < 0 || timeSeconds > 3600)
            throw new ArgumentOutOfRangeException(nameof(timeSeconds), "Preview replay is bounded to one hour.");
        long targetTick = (long)Math.Floor(timeSeconds * StepsPerSecond + 1e-9);
        if (state is null || targetTick < tick)
        {
            SecondaryMotionFrame initial = Read(0);
            SecondaryMotionFrame start = initializationSeconds > 0 ? initializationPose ?? initial : initial;
            state = definition.Groups.Where(g => g.Enabled).Select(g => CreateState(g, start)).ToArray();
            if (initializationPose is not null && initializationSeconds > 0)
            {
                // Begin outside the body in the authored bind configuration, then sweep the
                // kinematic colliders/anchors into the first animation pose. Loading a run or
                // crouch directly inside an undeformed skirt otherwise gives an ambiguous exit
                // direction. This deterministic hidden transition never changes published bones.
                int steps = Math.Max(1, (int)Math.Ceiling(initializationSeconds * StepsPerSecond));
                for (int i = 0; i < steps / 2; i++) Advance(state, start, Step);
                for (int i = 1; i <= steps; i++)
                {
                    double amount = i / (double)steps;
                    amount = amount * amount * (3 - 2 * amount);
                    Advance(state, i == steps ? initial : BlendFrame(start, initial, amount, initializationParents), Step);
                }
            }
            tick = 0;
        }
        while (tick < targetTick)
        {
            tick++;
            Advance(state, Read(tick * Step), Step);
        }
        SecondaryMotionFrame frame = Read(timeSeconds);
        // Fractional frames run on a disposable state. Cached fixed ticks therefore do not depend
        // on the sequence of preview requests (including a forward seek followed by a backward seek).
        GroupState[] display = state;
        double remainder = timeSeconds - tick * Step;
        if (remainder > 1e-9)
        {
            display = state.Select(s => s.Copy()).ToArray();
            Advance(display, frame, remainder);
        }
        var globals = frame.Globals.ToArray();
        var overlays = ImmutableArray.CreateBuilder<SecondaryMotionParticleState>();
        TransformMatrix inverseActor = frame.ActorWorldTransform.InvertedAffine();
        foreach (GroupState s in display)
        {
            Vector3D[] targets = Targets(s.Group, frame);
            for (int i = 0; i < s.Position.Length; i++)
            {
                SecondaryParticle p = s.Group.Particles[i];
                overlays.Add(new(s.Group.Name, i, p.Fixed, p.Fixed ? targets[i] : s.Position[i]));
                if (p.Fixed || p.DrivenBoneName is not { } driven) continue;
                int bone = bones[driven];
                TransformMatrix animatedWorld = frame.ActorWorldTransform * frame.Globals[bone];
                TransformMatrix rotation = TransformMatrix.Identity;
                if (p.AimParticleIndex is int aim)
                {
                    Vector3D from = targets[aim] - targets[i];
                    Vector3D to = s.Position[aim] - s.Position[i];
                    if (from.LengthSquared > 1e-16 && to.LengthSquared > 1e-16)
                        rotation = TransformMatrix.CreateRotation(QuaternionD.FromToRotation(from, to));
                }
                // Rotate around the animated particle position, retaining the complete authored
                // scale/shear and the offset between the particle and the driven bone pivot.
                globals[bone] = inverseActor * TransformMatrix.CreateTranslation(s.Position[i]) * rotation *
                    TransformMatrix.CreateTranslation(-targets[i]) * animatedWorld;
            }
        }
        return new(globals.ToImmutableArray(), overlays.ToImmutable(),
            definition.Groups.IsEmpty ? [] : [ApproximationLabel + ": sampled cloth contacts do not prove mesh collision or native runtime parity."]);

        SecondaryMotionFrame Read(double time)
        {
            SecondaryMotionFrame value = sampleFinalDisplay(time);
            if (value.Globals.IsDefault || value.Globals.Length != boneCount ||
                value.Globals.Any(m => !m.IsFinite) || !value.ActorWorldTransform.IsFinite)
                throw new ArgumentException("The display sampler returned an invalid rig pose.", nameof(sampleFinalDisplay));
            ValidateActorPlacement(value.ActorWorldTransform, nameof(sampleFinalDisplay));
            _ = value.ActorWorldTransform.InvertedAffine();
            return value;
        }
    }

    private static void ValidateActorPlacement(TransformMatrix matrix, string parameterName)
    {
        const double tolerance = 1e-5;
        Vector3D x = matrix.TransformDirection(Vector3D.UnitX), y = matrix.TransformDirection(Vector3D.UnitY), z = matrix.TransformDirection(Vector3D.UnitZ);
        if (Math.Abs(x.LengthSquared - 1) > tolerance || Math.Abs(y.LengthSquared - 1) > tolerance || Math.Abs(z.LengthSquared - 1) > tolerance ||
            Math.Abs(Vector3D.Dot(x, y)) > tolerance || Math.Abs(Vector3D.Dot(x, z)) > tolerance || Math.Abs(Vector3D.Dot(y, z)) > tolerance ||
            matrix.LinearDeterminant <= 0)
            throw new ArgumentException("Secondary motion preview requires a unit-scale rigid actor transform. Use unscaled actor placement and author model geometry and physics dimensions at the intended size; scaled, sheared or mirrored actor placement is not supported.", parameterName);
    }

    private static SecondaryMotionFrame BlendFrame(SecondaryMotionFrame from, SecondaryMotionFrame to, double amount, int[]? parents)
    {
        static TransformMatrix Blend(TransformMatrix a, TransformMatrix b, double t)
        {
            TransformTRS first = a.Decompose(1e-4), second = b.Decompose(1e-4);
            return new TransformTRS(Vector3D.Lerp(first.Translation, second.Translation, t),
                QuaternionD.Slerp(first.Rotation, second.Rotation, t), Vector3D.Lerp(first.Scale, second.Scale, t)).ToMatrix();
        }
        TransformMatrix[] globals = new TransformMatrix[from.Globals.Length];
        for (int i = 0; i < globals.Length; i++)
        {
            int parent = parents?[i] ?? -1;
            TransformMatrix a = parent < 0 ? from.Globals[i] : from.Globals[parent].InvertedAffine() * from.Globals[i];
            TransformMatrix b = parent < 0 ? to.Globals[i] : to.Globals[parent].InvertedAffine() * to.Globals[i];
            TransformMatrix local = Blend(a, b, amount);
            globals[i] = parent < 0 ? local : globals[parent] * local;
        }
        return new(globals.ToImmutableArray(), Blend(from.ActorWorldTransform, to.ActorWorldTransform, amount));
    }

    private GroupState CreateState(SecondaryMotionGroup group, SecondaryMotionFrame frame)
    {
        Vector3D[] targets = Targets(group, frame);
        double[] lengths = group.Constraints.Select(c => c.RestLength ?? Vector3D.Distance(targets[c.First], targets[c.Second])).ToArray();
        if (lengths.Any(v => !double.IsFinite(v) || v <= 1e-9))
            throw new ArgumentException($"Secondary group '{group.Name}' has a zero-length initial constraint.");
        return new(group, targets, new Vector3D[targets.Length], (Vector3D[])targets.Clone(), lengths);
    }

    private Vector3D[] Targets(SecondaryMotionGroup group, SecondaryMotionFrame frame) => group.Particles
        .Select(p => (frame.ActorWorldTransform * frame.Globals[bones[p.ReferenceBoneName]]).TransformPoint(p.LocalPosition)).ToArray();

    private void Advance(GroupState[] states, SecondaryMotionFrame frame, double dt)
    {
        foreach (GroupState s in states)
        {
            SecondaryMotionGroup group = s.Group;
            SecondaryPreviewSettings settings = group.Preview;
            Vector3D[] targets = Targets(group, frame);
            Vector3D[] old = (Vector3D[])s.Position.Clone();
            Vector3D[] follow = new Vector3D[old.Length];
            for (int i = 0; i < old.Length; i++)
            {
                if (group.Particles[i].Fixed) { s.Position[i] = targets[i]; continue; }
                follow[i] = (targets[i] - s.PreviousTargets[i]) * settings.AnimationFollow;
                Vector3D shapeAcceleration = (targets[i] - s.Position[i] - follow[i]) * settings.RestShapeStiffness;
                s.Velocity[i] = s.Velocity[i] * Math.Exp(-settings.Damping * dt) + (settings.Gravity + shapeAcceleration) * dt;
                s.Position[i] += follow[i] + s.Velocity[i] * dt;
            }
            var colliders = group.Colliders.Select(c =>
            {
                TransformMatrix a = frame.ActorWorldTransform * frame.Globals[bones[c.BoneName]];
                TransformMatrix b = c.EndBoneName is { } end ? frame.ActorWorldTransform * frame.Globals[bones[end]] : a;
                double scale = Math.Max(MaxScale(a), MaxScale(b));
                Vector3D first = a.TransformPoint(c.LocalPosition);
                return (Start: first, End: c.EndBoneName is null ? first : b.TransformPoint(c.EndLocalPosition), Radius: c.Radius * scale);
            }).ToArray();
            var collided = new bool[old.Length];
            var contactNormals = new Vector3D[old.Length];
            double particleScale = MaxScale(frame.ActorWorldTransform);
            for (int iteration = 0; iteration < SolverIterations; iteration++)
            {
                for (int k = 0; k < group.Constraints.Length; k++)
                {
                    SecondaryDistanceConstraint c = group.Constraints[k];
                    Vector3D delta = s.Position[c.Second] - s.Position[c.First];
                    double length = delta.Length;
                    double firstWeight = group.Particles[c.First].Fixed ? 0 : 1;
                    double secondWeight = group.Particles[c.Second].Fixed ? 0 : 1;
                    double sum = firstWeight + secondWeight;
                    if (sum == 0 || length <= 1e-12) continue;
                    double stiffness = c.Kind switch
                    {
                        SecondaryConstraintKind.Structural => settings.StructuralStiffness,
                        SecondaryConstraintKind.Shear => settings.ShearStiffness,
                        _ => settings.BendStiffness,
                    };
                    double amount = 1 - Math.Pow(1 - stiffness, dt * StepsPerSecond / SolverIterations);
                    Vector3D correction = delta * ((length - s.Lengths[k]) / length * amount / sum);
                    s.Position[c.First] += correction * firstWeight;
                    s.Position[c.Second] -= correction * secondWeight;
                }
                for (int i = 0; i < old.Length; i++)
                {
                    if (group.Particles[i].Fixed) { s.Position[i] = targets[i]; continue; }
                    foreach (var c in colliders)
                    {
                        Vector3D axis = c.End - c.Start;
                        double u = axis.LengthSquared <= 1e-16 ? 0 : Math.Clamp(Vector3D.Dot(s.Position[i] - c.Start, axis) / axis.LengthSquared, 0, 1);
                        Vector3D center = c.Start + axis * u;
                        Vector3D offset = s.Position[i] - center;
                        double radius = c.Radius + group.Particles[i].Radius * particleScale;
                        if (offset.LengthSquared >= radius * radius) continue;
                        if (!offset.TryNormalize(out Vector3D normal) && !(targets[i] - center).TryNormalize(out normal))
                            normal = Vector3D.UnitX;
                        s.Position[i] = center + normal * radius;
                        collided[i] = true;
                        contactNormals[i] += normal;
                    }
                }
                // Sparse cloth particles can all miss a limb passing through the edge or center
                // of a quad. Sample structural edges and shear diagonals inside the same solver
                // loop; the diagonal midpoint covers a quad's interior. Project a contact back
                // to its free endpoints using the barycentric constraint gradient. Fixed anchors
                // retain zero inverse mass and therefore remain exactly at their animated poses.
                foreach (SecondaryDistanceConstraint link in group.Constraints)
                {
                    if (link.Kind == SecondaryConstraintKind.Bend) continue;
                    double massA = group.Particles[link.First].Fixed ? 0 : 1;
                    double massB = group.Particles[link.Second].Fixed ? 0 : 1;
                    if (massA + massB == 0) continue;
                    for (int sample = 1; sample <= 4; sample++)
                    {
                        foreach (var c in colliders)
                        {
                            double broadRadius = c.Radius + Math.Max(group.Particles[link.First].Radius, group.Particles[link.Second].Radius) * particleScale;
                            if (!SegmentBoundsOverlap(s.Position[link.First], s.Position[link.Second], c.Start, c.End, broadRadius)) continue;
                            double t = sample <= 3 ? sample * 0.25 : ClosestSegmentParameter(
                                s.Position[link.First], s.Position[link.Second], c.Start, c.End);
                            double a = 1 - t;
                            double denominator = massA * a * a + massB * t * t;
                            // A contact arbitrarily close to a fixed endpoint cannot be satisfied
                            // by translating the other endpoint without an unbounded correction.
                            // Keep that attachment region fixed; endpoint contacts and farther
                            // interior samples still resolve the free portion of the link.
                            if (denominator < 0.04) continue;
                            double particleRadius = (group.Particles[link.First].Radius * a + group.Particles[link.Second].Radius * t) * particleScale;
                            Vector3D point = s.Position[link.First] * a + s.Position[link.Second] * t;
                            Vector3D axis = c.End - c.Start;
                            double u = axis.LengthSquared <= 1e-16 ? 0 : Math.Clamp(Vector3D.Dot(point - c.Start, axis) / axis.LengthSquared, 0, 1);
                            Vector3D center = c.Start + axis * u;
                            Vector3D offset = point - center;
                            double radius = c.Radius + particleRadius;
                            double distance = offset.Length;
                            if (distance >= radius) continue;
                            if (!offset.TryNormalize(out Vector3D normal, 1e-8))
                            {
                                // A segment through the exact center has no unique outward normal.
                                // Use a stable perpendicular to the cloth segment, not a direction
                                // along it that would merely collapse/stretch the edge through the body.
                                Vector3D edge = s.Position[link.Second] - s.Position[link.First];
                                Vector3D reference = targets[link.First] * a + targets[link.Second] * t - center;
                                if (edge.LengthSquared > 1e-16) reference -= edge * (Vector3D.Dot(reference, edge) / edge.LengthSquared);
                                if (!reference.TryNormalize(out normal, 1e-8))
                                {
                                    Vector3D candidate = Vector3D.Cross(edge, axis.LengthSquared > 1e-16 ? axis : Vector3D.UnitY);
                                    if (!candidate.TryNormalize(out normal))
                                        normal = Vector3D.Cross(edge, Vector3D.UnitZ).TryNormalize(out Vector3D alternative) ? alternative : Vector3D.UnitX;
                                }
                            }
                            Vector3D correction = normal * ((radius - distance) / denominator);
                            double maximumEndpointMove = Math.Min(radius * 0.5,
                                Vector3D.Distance(s.Position[link.First], s.Position[link.Second]) * 0.25);
                            double requestedMove = correction.Length * Math.Max(massA * a, massB * t);
                            if (requestedMove > maximumEndpointMove && requestedMove > 1e-12)
                                correction *= maximumEndpointMove / requestedMove;
                            s.Position[link.First] += correction * (massA * a);
                            s.Position[link.Second] += correction * (massB * t);
                            if (massA > 0) { collided[link.First] = true; contactNormals[link.First] += normal * a; }
                            if (massB > 0) { collided[link.Second] = true; contactNormals[link.Second] += normal * t; }
                        }
                    }
                }
            }
            for (int i = 0; i < old.Length; i++)
            {
                s.Velocity[i] = group.Particles[i].Fixed ? Vector3D.Zero : (s.Position[i] - old[i] - follow[i]) / dt;
                if (collided[i])
                {
                    // Positional contact correction is not elastic rebound energy. Removing its
                    // normal velocity prevents a deep initial contact from launching the garment.
                    // Moving colliders continue to displace the contact every fixed step.
                    if (contactNormals[i].TryNormalize(out Vector3D normal))
                        s.Velocity[i] -= normal * Vector3D.Dot(s.Velocity[i], normal);
                    s.Velocity[i] *= 1 - settings.CollisionFriction;
                }
                if (!s.Position[i].IsFinite || !s.Velocity[i].IsFinite)
                    throw new InvalidOperationException($"Secondary preview '{group.Name}' became non-finite; reset and inspect its constraints.");
            }
            s.PreviousTargets = targets;
        }
    }

    private static double ClosestSegmentParameter(Vector3D start, Vector3D end, Vector3D capsuleStart, Vector3D capsuleEnd)
    {
        Vector3D edge = end - start, axis = capsuleEnd - capsuleStart, relative = start - capsuleStart;
        double a = edge.LengthSquared, e = axis.LengthSquared, c = Vector3D.Dot(edge, relative);
        if (a <= 1e-16) return 0;
        if (e <= 1e-16) return Math.Clamp(-c / a, 0, 1);
        double b = Vector3D.Dot(edge, axis), f = Vector3D.Dot(axis, relative), denominator = a * e - b * b;
        double s = denominator > 1e-16 ? Math.Clamp((b * f - c * e) / denominator, 0, 1) : 0;
        double t = (b * s + f) / e;
        if (t < 0) s = Math.Clamp(-c / a, 0, 1);
        else if (t > 1) s = Math.Clamp((b - c) / a, 0, 1);
        return s;
    }

    private static bool SegmentBoundsOverlap(Vector3D a, Vector3D b, Vector3D c, Vector3D d, double radius) =>
        Math.Max(a.X, b.X) >= Math.Min(c.X, d.X) - radius && Math.Min(a.X, b.X) <= Math.Max(c.X, d.X) + radius &&
        Math.Max(a.Y, b.Y) >= Math.Min(c.Y, d.Y) - radius && Math.Min(a.Y, b.Y) <= Math.Max(c.Y, d.Y) + radius &&
        Math.Max(a.Z, b.Z) >= Math.Min(c.Z, d.Z) - radius && Math.Min(a.Z, b.Z) <= Math.Max(c.Z, d.Z) + radius;

    private static double MaxScale(TransformMatrix matrix) => Math.Max(
        matrix.TransformDirection(Vector3D.UnitX).Length,
        Math.Max(matrix.TransformDirection(Vector3D.UnitY).Length, matrix.TransformDirection(Vector3D.UnitZ).Length));

    private sealed class GroupState(SecondaryMotionGroup group, Vector3D[] position, Vector3D[] velocity, Vector3D[] previousTargets, double[] lengths)
    {
        public SecondaryMotionGroup Group { get; } = group;
        public Vector3D[] Position { get; } = position;
        public Vector3D[] Velocity { get; } = velocity;
        public Vector3D[] PreviousTargets { get; set; } = previousTargets;
        public double[] Lengths { get; } = lengths;
        public GroupState Copy() => new(Group, (Vector3D[])Position.Clone(), (Vector3D[])Velocity.Clone(), (Vector3D[])PreviousTargets.Clone(), Lengths);
    }
}

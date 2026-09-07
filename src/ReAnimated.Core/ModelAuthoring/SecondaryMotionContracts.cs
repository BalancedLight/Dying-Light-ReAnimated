using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum SecondaryConstraintKind { Structural, Shear, Bend }
public enum NativeClothSourceKind { Phx, MpCloth }

/// <summary>Lossless native text, independent of the editor's approximate simulation settings.</summary>
public sealed record NativeClothSource
{
    public NativeClothSourceKind Kind { get; init; }
    public string ResourceName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
}

/// <summary>Explicit editor tuning; these values are NOT native MPC spring coefficients.</summary>
public sealed record SecondaryPreviewSettings
{
    public Vector3D Gravity { get; init; } = new(0, -9.81, 0);
    public double Damping { get; init; } = 3;
    public double StructuralStiffness { get; init; } = 0.95;
    public double ShearStiffness { get; init; } = 0.65;
    public double BendStiffness { get; init; } = 0.3;
    public double AnimationFollow { get; init; } = 0.65;
    public double CollisionFriction { get; init; } = 0.2;
    /// <summary>Preview spring acceleration toward the animated shape, in inverse seconds squared. Zero disables it.</summary>
    public double RestShapeStiffness { get; init; }
}

public sealed record SecondaryParticle
{
    public string ReferenceBoneName { get; init; } = string.Empty;
    public Vector3D LocalPosition { get; init; }
    public bool Fixed { get; init; }
    public double Radius { get; init; } = 0.005;
    /// <summary>Only this explicitly declared bone can be changed; null denotes a virtual particle.</summary>
    public string? DrivenBoneName { get; init; }
    /// <summary>Optional particle used to rotate the driven bone from its animated direction.</summary>
    public int? AimParticleIndex { get; init; }
}

public sealed record SecondaryDistanceConstraint
{
    public int First { get; init; }
    public int Second { get; init; }
    public SecondaryConstraintKind Kind { get; init; }
    /// <summary>Null derives length from the first evaluated frame. Units match the model.</summary>
    public double? RestLength { get; init; }
}

/// <summary>A sphere when EndBoneName is null, otherwise a capsule between animated endpoints.</summary>
public sealed record SecondaryCollider
{
    public string BoneName { get; init; } = string.Empty;
    public Vector3D LocalPosition { get; init; }
    public string? EndBoneName { get; init; }
    public Vector3D EndLocalPosition { get; init; }
    public double Radius { get; init; } = 0.05;
}

public sealed record SecondaryMotionGroup
{
    public string Name { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public ImmutableArray<SecondaryParticle> Particles { get; init; } = [];
    public ImmutableArray<SecondaryDistanceConstraint> Constraints { get; init; } = [];
    public ImmutableArray<SecondaryCollider> Colliders { get; init; } = [];
    public SecondaryPreviewSettings Preview { get; init; } = new();
}

public sealed record SecondaryMotionDefinition
{
    public ImmutableArray<SecondaryMotionGroup> Groups { get; init; } = [];
    public ImmutableArray<NativeClothSource> NativeSources { get; init; } = [];

    public void Validate(IEnumerable<string>? boneNames = null)
    {
        if (Groups.IsDefault || NativeSources.IsDefault || Groups.Length > 256 || NativeSources.Length > 512)
            throw new ArgumentException("Secondary-motion collections must be initialized and bounded.");
        HashSet<string>? names = boneNames?.ToHashSet(StringComparer.Ordinal);
        var driven = new HashSet<string>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SecondaryMotionGroup group in Groups)
        {
            ArgumentNullException.ThrowIfNull(group);
            ArgumentException.ThrowIfNullOrWhiteSpace(group.Name);
            if (!groups.Add(group.Name) || group.Particles.IsDefaultOrEmpty || group.Particles.Length > 4096 ||
                group.Constraints.IsDefault || group.Constraints.Length > 65536 || group.Colliders.IsDefault ||
                group.Colliders.Length > 1024 || group.Particles.Any(p => p is null) || !group.Particles.Any(p => p.Fixed))
                throw new ArgumentException("Secondary groups require unique names, bounded data and a fixed anchor.");
            ArgumentNullException.ThrowIfNull(group.Preview);
            SecondaryPreviewSettings s = group.Preview;
            if (!s.Gravity.IsFinite || !double.IsFinite(s.Damping) || s.Damping < 0 || s.Damping > 1000 ||
                !double.IsFinite(s.RestShapeStiffness) || s.RestShapeStiffness < 0 || s.RestShapeStiffness > 1000 ||
                new[] { s.StructuralStiffness, s.ShearStiffness, s.BendStiffness, s.AnimationFollow, s.CollisionFriction }
                .Any(v => !double.IsFinite(v) || v < 0 || v > 1))
                throw new ArgumentException("Secondary preview settings are outside their finite ranges.");
            for (int index = 0; index < group.Particles.Length; index++)
            {
                SecondaryParticle p = group.Particles[index];
                Bone(p.ReferenceBoneName);
                if (!p.LocalPosition.IsFinite || !double.IsFinite(p.Radius) || p.Radius < 0 ||
                    p.AimParticleIndex is int aim && (aim < 0 || aim >= group.Particles.Length || aim == index))
                    throw new ArgumentException("Secondary particle geometry is invalid.");
                if (p.DrivenBoneName is { } output)
                {
                    Bone(output);
                    if (!driven.Add(output)) throw new ArgumentException($"Secondary bone '{output}' is driven more than once.");
                }
            }
            var links = new HashSet<(int, int)>();
            foreach (SecondaryDistanceConstraint c in group.Constraints)
            {
                ArgumentNullException.ThrowIfNull(c);
                if (c.First < 0 || c.Second < 0 || c.First >= group.Particles.Length || c.Second >= group.Particles.Length ||
                    c.First == c.Second || !Enum.IsDefined(c.Kind) ||
                    c.RestLength is double length && (!double.IsFinite(length) || length <= 0) ||
                    !links.Add((Math.Min(c.First, c.Second), Math.Max(c.First, c.Second))))
                    throw new ArgumentException("Secondary constraints contain invalid or duplicate links.");
            }
            // Every free particle must be connected to an anchor; floating unbound islands are not a cloth definition.
            var reached = group.Particles.Select(p => p.Fixed).ToArray();
            bool changed;
            do
            {
                changed = false;
                foreach (SecondaryDistanceConstraint c in group.Constraints)
                    if (reached[c.First] != reached[c.Second]) { reached[c.First] = reached[c.Second] = true; changed = true; }
            } while (changed);
            if (reached.Any(value => !value)) throw new ArgumentException("Every secondary particle must connect to an anchor.");
            foreach (SecondaryCollider c in group.Colliders)
            {
                ArgumentNullException.ThrowIfNull(c);
                Bone(c.BoneName);
                if (c.EndBoneName is { } end) Bone(end);
                if (!c.LocalPosition.IsFinite || !c.EndLocalPosition.IsFinite || !double.IsFinite(c.Radius) || c.Radius <= 0)
                    throw new ArgumentException("Secondary collision geometry is invalid.");
            }
        }
        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NativeClothSource source in NativeSources)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!Enum.IsDefined(source.Kind) || source.Text is null || source.Text.Length > 4 * 1024 * 1024)
                throw new ArgumentException("Native cloth source is invalid or exceeds the text limit.");
            CustomModelSourceIdentity.ValidatePackageEntryPath(source.ResourceName, nameof(NativeSources));
            if (!sourceNames.Add(source.ResourceName.Replace('\\', '/')) ||
                !source.ResourceName.EndsWith(source.Kind == NativeClothSourceKind.Phx ? ".phx" : ".mpcloth", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Native cloth resource names must be unique and match their source kind.");
        }
        void Bone(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (names is not null && !names.Contains(name)) throw new ArgumentException($"Secondary-motion bone '{name}' is absent.");
        }
    }
}

using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public enum Dl1NativeClothRadiusScale { ModelRoot }

/// <summary>Resolved native model-space shape; its radius uses the native cloth model-root scale.</summary>
public sealed record Dl1NativeClothCollisionOverlay(
    string ResourceName,
    string CommandName,
    string BoneName,
    Vector3D LocalPosition,
    string? EndBoneName,
    Vector3D EndLocalPosition,
    double Radius,
    Dl1NativeClothRadiusScale RadiusScale);

public sealed record Dl1NativeClothCollisionOverlayResolution(
    ImmutableArray<Dl1NativeClothCollisionOverlay> Overlays,
    ImmutableArray<string> Diagnostics);

/// <summary>Resolves native cloth declarations only where source attachment data is measurable.</summary>
public static class Dl1NativeClothCollisionOverlayResolver
{
    public static Dl1NativeClothCollisionOverlayResolution Resolve(
        SecondaryMotionDefinition definition,
        ImmutableArray<CustomModelBone> bones,
        IReadOnlyDictionary<string, Dl1AuthoredBoneBounds>? compiledBounds = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (bones.IsDefault) throw new ArgumentException("A resolved hierarchy is required.", nameof(bones));
        definition.Validate(bones.Select(bone => bone.Name));
        var overlays = ImmutableArray.CreateBuilder<Dl1NativeClothCollisionOverlay>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        NativeClothSource[] physics = definition.NativeSources
            .Where(source => source.Kind == NativeClothSourceKind.Phx).ToArray();
        NativeClothSource[] wrappers = definition.NativeSources
            .Where(source => source.Kind == NativeClothSourceKind.MpCloth).ToArray();
        if (physics.Length == 0 || wrappers.Length != 1)
        {
            if (physics.Length != 0)
                diagnostics.Add("Import one MPCloth wrapper to determine which native cloth sources are active.");
            return new([], diagnostics.ToImmutable());
        }

        Dictionary<string, NativeClothSource> physicsByName = physics.ToDictionary(
            source => Normalize(source.ResourceName), StringComparer.OrdinalIgnoreCase);
        NativeMpClothDocument wrapper = Dl1ClothCodec.ReadMpCloth(
            wrappers[0].Text, physics.Select(source => source.ResourceName));
        diagnostics.AddRange(wrapper.Diagnostics
            .Where(diagnostic => diagnostic.Code != "native_statement_unsupported")
            .Select(diagnostic => $"{wrappers[0].ResourceName}: {diagnostic.Message}"));

        foreach (NativeClothBinding binding in wrapper.Bindings.Where(binding => binding.Enabled == 1))
        {
            string resourceName = Normalize(binding.ResourceName);
            if (!physicsByName.TryGetValue(resourceName, out NativeClothSource? source))
            {
                diagnostics.Add($"Native cloth binding '{binding.ResourceName}' has no imported PHX source.");
                continue;
            }

            NativePhxDocument phx = Dl1ClothCodec.ReadPhx(source.Text, bones.Select(bone => bone.Name));
            diagnostics.AddRange(phx.Diagnostics
                .Where(diagnostic => diagnostic.Code != "native_collision_bounds_required")
                .Select(diagnostic => $"{source.ResourceName}: {diagnostic.Message}"));
            foreach (NativeClothCollision collision in phx.Collisions)
            {
                try
                {
                    Dl1NativeClothCollisionOverlay? overlay = ResolveCollision(
                        source.ResourceName, collision, bones, compiledBounds, out string? detail);
                    if (overlay is not null) overlays.Add(overlay);
                    if (detail is not null) diagnostics.Add($"{source.ResourceName}: {detail}");
                }
                catch (Exception error) when (error is FormatException or ArgumentException or InvalidDataException or OverflowException)
                {
                    diagnostics.Add($"{source.ResourceName}: '{collision.Command}' was not shown: {error.Message}");
                }
            }
        }

        foreach (NativeClothBinding binding in wrapper.Bindings.Where(binding => binding.Enabled == 0))
            diagnostics.Add($"Native cloth binding '{binding.ResourceName}' is disabled.");
        foreach (NativeClothDiagnostic diagnostic in wrapper.Diagnostics.Where(diagnostic => diagnostic.Code == "native_statement_unsupported"))
            diagnostics.Add($"{wrappers[0].ResourceName}: {diagnostic.Message}");

        return new(overlays.ToImmutable(), diagnostics.Distinct(StringComparer.Ordinal).ToImmutableArray());
    }

    private static Dl1NativeClothCollisionOverlay? ResolveCollision(
        string resourceName,
        NativeClothCollision collision,
        ImmutableArray<CustomModelBone> bones,
        IReadOnlyDictionary<string, Dl1AuthoredBoneBounds>? compiledBounds,
        out string? detail)
    {
        detail = null;
        ImmutableArray<string> arguments = collision.Arguments;
        switch (collision.Command)
        {
            case "CollisionCapsuleBetween":
            {
                int startMode = Integer(arguments[1]);
                int endMode = Integer(arguments[3]);
                Vector3D start = Endpoint(arguments[0], startMode);
                Vector3D end = Endpoint(arguments[2], endMode);
                double radius = Number(arguments[4]);
                if (radius <= 0) throw new FormatException("A positive explicit capsule radius is required.");
                detail = $"{collision.Command} uses animated bone pivots and retained bounds where its attachment flag is 1.";
                return new(resourceName, collision.Command, Quoted(arguments[0]), start,
                    Quoted(arguments[2]), end, radius, Dl1NativeClothRadiusScale.ModelRoot);
            }
            case "CollisionSphere":
            case "CollisionSphereShift":
            {
                string boneName = Quoted(arguments[0]);
                Dl1AuthoredBoneBounds bounds = RequireBounds(boneName);
                Vector3D shift = collision.Command == "CollisionSphereShift" ? Vector(arguments[1]) : Vector3D.Zero;
                double authoredRadius = Number(arguments[^1]);
                double radius = authoredRadius > 0
                    ? authoredRadius
                    : authoredRadius < 0
                        ? Math.Max(bounds.HalfExtents.X * 2, Math.Max(bounds.HalfExtents.Y * 2, bounds.HalfExtents.Z * 2)) * -authoredRadius * 0.5
                        : 0;
                if (radius <= 0) throw new FormatException("The sphere radius and retained bone bounds do not produce a positive radius.");
                detail = $"{collision.Command} is centered on the retained element bounds plus its authored shift.";
                return new(resourceName, collision.Command, boneName, bounds.Center + shift,
                    null, Vector3D.Zero, radius, Dl1NativeClothRadiusScale.ModelRoot);
            }
            default:
                detail = $"{collision.Command} is retained in source and has no preview overlay.";
                return null;
        }

        Vector3D Endpoint(string boneArgument, int mode)
        {
            string name = Quoted(boneArgument);
            if (mode == 0) return Vector3D.Zero;
            if (mode != 1) throw new FormatException("Unknown capsule attachment type.");
            return RequireBounds(name).Center;
        }

        Dl1AuthoredBoneBounds RequireBounds(string name)
        {
            if (compiledBounds is not null && compiledBounds.TryGetValue(name, out Dl1AuthoredBoneBounds compiled))
                return compiled;
            CustomModelBone? bone = bones.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.Ordinal));
            return bone?.LocalBounds ?? throw new InvalidDataException($"Element bounds for '{name}' are not available.");
        }
    }

    private static string Normalize(string name) => name.Replace('\\', '/');

    private static string Quoted(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            throw new FormatException("A quoted bone or resource name was expected.");
        return value[1..^1];
    }

    private static int Integer(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Vector3D Vector(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']')
            throw new FormatException("A bracketed collision shift was expected.");
        string[] parts = trimmed[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3) throw new FormatException("A collision shift needs three values.");
        var result = new Vector3D(Number(parts[0]), Number(parts[1]), Number(parts[2]));
        if (!result.IsFinite) throw new FormatException("The collision shift is not finite.");
        return result;
    }
}

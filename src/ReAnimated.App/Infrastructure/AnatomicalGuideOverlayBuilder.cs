using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.App.Infrastructure;

/// <summary>Visual-only anatomical guides; never creates a skeleton or assigns bone/palette indexes.</summary>
public static class AnatomicalGuideOverlayBuilder
{
    public static ImmutableArray<GizmoRenderData> BuildStored(IReadOnlyCollection<AnatomicalJointProposal> guides, string? selectedRole = null, Guid? editableGuideId = null)
    {
        ArgumentNullException.ThrowIfNull(guides);
        double size = guides.Count > 1 ? Math.Max(guides.Max(static g => g.Position.Z) - guides.Min(static g => g.Position.Z),
            Math.Max(guides.Max(static g => g.Position.Y) - guides.Min(static g => g.Position.Y),
            guides.Max(static g => g.Position.X) - guides.Min(static g => g.Position.X))) * .01 : .02;
        var lines = ImmutableArray.CreateBuilder<GizmoRenderData>();
        AddPoints(lines, guides, Math.Max(1e-6, size), selectedRole);
        if (editableGuideId is { } id && guides.FirstOrDefault(g => g.Role == selectedRole) is { Locked: false } selected)
        {
            float length = (float)Math.Max(1e-5, size * 9);
            Vector3 origin = ToVector(selected.Position);
            AddHandle(Vector3.UnitX, TranslationGizmoAxis.X, new(1, .25f, .2f, 1));
            AddHandle(Vector3.UnitY, TranslationGizmoAxis.Y, new(.2f, 1, .3f, 1));
            AddHandle(Vector3.UnitZ, TranslationGizmoAxis.Z, new(.3f, .55f, 1, 1));
            void AddHandle(Vector3 axis, TranslationGizmoAxis bindingAxis, Vector4 color) => lines.Add(new(
                GizmoKind.TranslationHandle, origin, origin + axis * length, color, 3,
                TranslationGizmoBinding.ForTarget(id, bindingAxis, RenderGizmoSpace.Global), InteractionAxisWorld: axis));
        }
        return lines.ToImmutable();
    }
    public static ImmutableArray<GizmoRenderData> Build(AnatomicalDetectionResult result, string? selectedRole = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lines = ImmutableArray.CreateBuilder<GizmoRenderData>();
        var points = result.Joints.ToDictionary(static p => p.Role, static p => p.Position, StringComparer.Ordinal);
        foreach (var connection in result.Connections)
            if (points.TryGetValue(connection.ParentRole, out var parent) && points.TryGetValue(connection.ChildRole, out var child))
                lines.Add(new(GizmoKind.Line, ToVector(parent), ToVector(child), new(.3f, .65f, .85f, .85f), 1.5f));
        double radius = Math.Max(1e-6, result.ResolutionMeters * .55);
        AddPoints(lines, result.Joints, radius, selectedRole);
        return lines.ToImmutable();
    }
    private static void AddPoints(ImmutableArray<GizmoRenderData>.Builder lines, IEnumerable<AnatomicalJointProposal> joints, double radius, string? selectedRole)
    {
        foreach (var joint in joints)
        {
            Vector4 color = joint.Role == selectedRole ? new(1, .8f, .15f, 1) : joint.Locked ? new(1, .45f, .2f, 1) : new(.1f, .9f, .9f, 1);
            foreach (var axis in new[] { Vector3D.UnitX, Vector3D.UnitY, Vector3D.UnitZ })
                lines.Add(new(GizmoKind.Line, ToVector(joint.Position - axis * radius), ToVector(joint.Position + axis * radius), color, 2));
        }
    }
    private static Vector3 ToVector(Vector3D point)
    {
        var result = new Vector3((float)point.X, (float)point.Y, (float)point.Z);
        if (!float.IsFinite(result.X) || !float.IsFinite(result.Y) || !float.IsFinite(result.Z)) throw new ArgumentException("Guide position exceeds the viewport's finite range.");
        return result;
    }
}

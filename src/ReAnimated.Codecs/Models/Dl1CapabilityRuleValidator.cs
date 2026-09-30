using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public sealed record RigRoleCheckedRule(string RoleId, Guid EntityId, string Check, string Observation);
public sealed record RigRoleRuleAssessment(ImmutableArray<RigProfileDiagnostic> Diagnostics, ImmutableArray<RigRoleCheckedRule> PassedChecks);

/// <summary>Checks declared rules against one prepared contract, independently of native acceptance.</summary>
public static class Dl1CapabilityRuleValidator
{
    public static RigRoleRuleAssessment Assess(CustomModelDocument document, Dl1AuthoredRigContract contract, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate(); ArgumentNullException.ThrowIfNull(contract);
        token.ThrowIfCancellationRequested();
        if (!string.Equals(document.Source.ContentSha256, contract.SourceFbxSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The prepared contract belongs to a different source asset.");
        var recipe = document.RiggingSession?.Recipe ?? throw new InvalidOperationException("No Studio session is available.");
        var profile = recipe.ProfileSnapshot ?? throw new InvalidOperationException("No capability profile definition is available.");
        var closure = RigProfileResolver.Resolve(profile, recipe.SelectedCapabilityIds, recipe.Assignments.Select(a => a.RoleId));
        var nodes = contract.Nodes.Where(n => n.SemanticEntityId is not null).ToDictionary(n => n.SemanticEntityId!.Value);
        var observed = RiggingSessions.ObserveSourceHierarchy(document); var bones = document.CreateEffectiveBones();
        var sourceGlobals = new TransformMatrix[bones.Length];
        foreach (var bone in bones) sourceGlobals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : sourceGlobals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        var sourceById = observed.Select((o, i) => (o.EntityId, Index: i)).ToDictionary(p => p.EntityId, p => p.Index);
        var diagnostics = ImmutableArray.CreateBuilder<RigProfileDiagnostic>(); var passed = ImmutableArray.CreateBuilder<RigRoleCheckedRule>();
        foreach (var resolved in closure.Roles)
        {
            token.ThrowIfCancellationRequested();
            var role = resolved.Role; var rules = role.ValidationRules;
            foreach (var assignment in recipe.Assignments.Where(a => a.RoleId == role.Id))
            {
                Guid id = assignment.EntityId;
                nodes.TryGetValue(id, out var node);
                if (role.EntityKind is not (RigNativeEntityKind.Bone or RigNativeEntityKind.Helper))
                { Add("role-specialized-rules-unverified", RigValidationStatus.Unverified, "The role's representation needs its specialized resource validator.", "Inspect the owning mesh, morph, cloth, physics or damage resource."); continue; }
                if (node is null)
                {
                    bool owned = recipe.Entities.Any(e => e.EntityId == id && e.OwnerAssetId == document.ModelId);
                    if (owned && rules?.Retention is { MustEmit: true })
                        Fail("retention", "The owned entity is absent from the prepared contract.", "Materialize the assigned node before export.");
                    else Add("role-emission-unobserved", RigValidationStatus.Unverified, "The assigned entity was not observed in this model's prepared contract.", "Inspect the owning asset before retention acceptance.");
                    continue;
                }

                if (rules?.Edits is null) Missing("edit-permission");
                if (rules?.Frame is not { } frameRule) Missing("frame");
                else
                {
                    int before = diagnostics.Count;
                    if (node.FramePolicy is not { } policy || !frameRule.AllowedPolicies.Contains(policy))
                        Fail("frame-policy", $"Prepared frame policy {node.FramePolicy} is not allowed by the role.", "Review the helper's saved frame policy.");
                    if (frameRule.PreserveSourceGlobal && (!sourceById.TryGetValue(id, out int index) ||
                        Vector3D.Distance(node.GlobalBindMatrix.Translation, sourceGlobals[index].Translation) > frameRule.PositionToleranceMetres ||
                        !(node.GlobalBindMatrix with { M14 = 0, M24 = 0, M34 = 0 }).NearlyEquals(sourceGlobals[index] with { M14 = 0, M24 = 0, M34 = 0 }, frameRule.MatrixTolerance)))
                        Fail("source-frame", "The prepared frame differs from the source frame beyond the declared tolerance.", "Restore the source frame or review the selected profile rule.");
                    if (frameRule.RequireOrthonormal && !Orthonormal(node.GlobalBindMatrix, frameRule.MatrixTolerance))
                        Fail("frame-orthonormal", "The prepared frame is scaled, sheared or reflected beyond the declared tolerance.", "Review the frame axes without altering unrelated anatomy.");
                    if (frameRule.OriginRoleId is { } originId && Reference(originId) is { } origin)
                    {
                        double error = Vector3D.Distance(node.GlobalBindMatrix.Translation, origin.GlobalBindMatrix.TransformPoint(frameRule.OriginOffset));
                        if (!double.IsFinite(error) || error > frameRule.PositionToleranceMetres)
                            Fail("frame-origin", $"Origin error {error:G6} m exceeds {frameRule.PositionToleranceMetres:G6} m.", "Review the helper position against its reference role.");
                    }
                    if (frameRule.DirectionRoleId is { } directionId && Reference(directionId) is { } target)
                    {
                        Vector3D axis = node.GlobalBindMatrix.TransformDirection(frameRule.LocalAxis);
                        Vector3D direction = target.GlobalBindMatrix.Translation - node.GlobalBindMatrix.Translation;
                        if (!axis.TryNormalize(out axis) || !direction.TryNormalize(out direction))
                            Fail("frame-direction", "The role's direction has a zero-length or non-finite axis/target vector.", "Separate the direction target and review the helper axes.");
                        else
                        {
                            double degrees = Math.Acos(Math.Clamp(Vector3D.Dot(axis, direction), -1, 1)) * 180 / Math.PI;
                            if (degrees > frameRule.AngularToleranceDegrees)
                                Fail("frame-direction", $"Axis error {degrees:G6} degrees exceeds {frameRule.AngularToleranceDegrees:G6} degrees.", "Review the helper orientation toward its reference role.");
                        }
                    }
                    if (diagnostics.Count == before) Pass("frame", "Prepared policy checked" +
                        (frameRule.PreserveSourceGlobal ? "; source frame preserved" : string.Empty) +
                        (frameRule.RequireOrthonormal ? "; orthonormal axes checked" : string.Empty) +
                        (frameRule.OriginRoleId is not null ? "; relative origin checked" : string.Empty) +
                        (frameRule.DirectionRoleId is not null ? "; target direction checked" : string.Empty) + ".");
                }
                if (rules?.Bounds is not { } boundsRule) Missing("bounds");
                else
                {
                    var half = node.Bounds.HalfExtents; var min = boundsRule.MinimumHalfExtents; double tolerance = boundsRule.ToleranceMetres;
                    if (node.BoundsPolicy is not { } policy || !boundsRule.AllowedPolicies.Contains(policy) ||
                        half.X + tolerance < min.X || half.Y + tolerance < min.Y || half.Z + tolerance < min.Z ||
                        boundsRule.MaximumHalfExtents is { } max && (half.X - tolerance > max.X || half.Y - tolerance > max.Y || half.Z - tolerance > max.Z))
                        Fail("bounds", $"Prepared {node.BoundsPolicy} half-extents ({half.X:G6}, {half.Y:G6}, {half.Z:G6}) do not meet the declared policy/range.", "Review the fitted bounds; a generic segment proxy may not satisfy a helper footprint.");
                    else Pass("bounds", "Prepared bounds policy and extent range passed.");
                }
                var components = recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == id);
                if (rules?.Channels is not { } channelRule) Missing("channels");
                else if (components?.EmittedMask is null)
                    Add("role-channels-unobserved", RigValidationStatus.Unverified, "The entity has no explicit emitted-channel decision.", "Set its channel ownership and emission policy.");
                else if (components.EmittedMask != channelRule.EmittedMask || !components.Position.Owners.SequenceEqual(channelRule.PositionOwners) ||
                    !components.Rotation.Owners.SequenceEqual(channelRule.RotationOwners) || !components.Scale.Owners.SequenceEqual(channelRule.ScaleOwners))
                    Fail("channels", "The saved component mask or ordered owners differ from the declared role rule.", "Review position, rotation and scale ownership without replacing animation samples.");
                else Pass("channels", "Declared emitted mask and ordered component owners passed.");

                if (rules?.Retention is not { } retention) Missing("retention");
                else if (components?.AnimationLod is null)
                    Add("role-lod-unobserved", RigValidationStatus.Unverified, "Animation LOD retention was not declared for the entity.", "Set the entity's animation LOD and inspect compiled retention.");
                else if (components.AnimationLod != retention.AnimationLod)
                    Fail("retention", $"Animation LOD {components.AnimationLod} differs from required {retention.AnimationLod}.", "Review the entity's animation LOD choice.");
                else Pass("retention", "Entity is present in the prepared contract and its animation LOD declaration matches. Compiled/runtime retention remains separate.");
                if (!role.RequiredVariantIds.IsEmpty || !role.RequiredLods.IsEmpty)
                    Add("role-variant-retention-unverified", RigValidationStatus.Unverified, "The role declares variant or resource-LOD coverage beyond this prepared hierarchy.", "Inspect each required compiled variant and resource LOD; an animation LOD token is not that inventory.");

                Dl1AuthoredRigNode? Reference(string roleId)
                {
                    var matches = recipe.Assignments.Where(a => a.RoleId == roleId).Select(a => nodes.GetValueOrDefault(a.EntityId)).ToArray();
                    if (matches.Length == 1 && matches[0] is not null) return matches[0];
                    Add("role-frame-reference-unverified", RigValidationStatus.Unverified, $"Reference role '{roleId}' has no unique observed prepared frame.", "Resolve the reference assignment and owning asset before frame review."); return null;
                }
                void Missing(string check) => Add("role-" + check + "-rule-unverified", RigValidationStatus.Unverified,
                    $"The role names no executable {check} check.", $"Supply a reviewed {check} definition; a rule ID alone is insufficient.");
                void Fail(string check, string message, string correction) => Add("role-" + check + "-conflict", RigValidationStatus.Failed, message, correction);
                void Add(string code, RigValidationStatus status, string message, string correction) => diagnostics.Add(new(code, status,
                    $"{role.Id} / {node?.Name ?? id.ToString()}: {message}", correction, RoleId: role.Id, EntityId: id, ConsumerIds: resolved.ConsumerIds));
                void Pass(string check, string observation) => passed.Add(new(role.Id, id, check, observation));
            }
        }
        return new(diagnostics.ToImmutable(), passed.ToImmutable());
    }

    private static bool Orthonormal(TransformMatrix frame, double tolerance)
    {
        Vector3D x = frame.TransformDirection(Vector3D.UnitX), y = frame.TransformDirection(Vector3D.UnitY), z = frame.TransformDirection(Vector3D.UnitZ);
        return Math.Abs(x.LengthSquared - 1) <= tolerance && Math.Abs(y.LengthSquared - 1) <= tolerance && Math.Abs(z.LengthSquared - 1) <= tolerance &&
            Math.Abs(Vector3D.Dot(x, y)) <= tolerance && Math.Abs(Vector3D.Dot(x, z)) <= tolerance && Math.Abs(Vector3D.Dot(y, z)) <= tolerance &&
            Math.Abs(Vector3D.Dot(Vector3D.Cross(x, y), z) - 1) <= tolerance;
    }
}

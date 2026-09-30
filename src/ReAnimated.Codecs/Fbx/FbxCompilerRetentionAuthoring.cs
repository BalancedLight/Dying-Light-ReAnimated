using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Explicit authored helper dependency; compilation, not this proposal, proves retention.</summary>
public static partial class FbxCompilerRetentionAuthoring
{
    public const string RoleId = "compiler.retention";
    public const string ReviewDiagnosticCode = "compiler_retention_review";

    public static StructuralHelperPreview Preview(FbxModelAuthoringImportResult model, Guid parentEntityId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        var doc = model.Package.Document;
        _ = doc.RiggingSession ?? throw new InvalidOperationException("Start a studio session before proposing compiler retention.");
        var prepared = Dl1CustomModelRigPreparer.Prepare(model, cancellationToken);
        var row = FbxStructuralHelperAuthoring.Inspect(model, prepared, cancellationToken).SingleOrDefault(r => r.EntityId == parentEntityId)
            ?? throw new InvalidOperationException("The selected retention parent is no longer present.");
        if (!row.CanAddRetentionHelper)
            throw new InvalidOperationException("Select a prepared bone branch without mesh influences or an existing helper dependency. Compile the current rig to inspect any remaining retention failure.");
        string label = new(row.Name.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(20).ToArray());
        string stem = "retain_" + (label.Length == 0 ? "bone" : label) + "_" + parentEntityId.ToString("N")[..8];
        string name = stem;
        var names = doc.CreateEffectiveBones().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int suffix = 2; names.Contains(name); suffix++) name = stem + "_" + suffix;
        var updated = CustomModelHelperAuthoring.DuplicateAsHelper(doc, row.SourceIndex,
            CustomModelAuthoredHelperKind.Helper, name);
        var helper = updated.AuthoredHelpers[^1];
        var session = updated.RiggingSession!;
        var evidence = new RigEvidenceReference
        {
            Id = "retention-proposal:" + helper.Id.ToString("N"), Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = doc.Source.ContentSha256,
            Description = "Explicit helper dependency proposed to retain its parent in compiled output. The current candidate still requires official compiler readback and native scenario validation.",
        };
        var recipe = new HelperRecipe
        {
            EntityId = helper.Id, OwnerAssetId = doc.ModelId, ParentEntityId = parentEntityId,
            RoleId = RoleId, LocalFrame = TransformMatrix.Identity, FollowPreparedParent = true,
            FramePolicy = RigFramePolicy.Manual, BoundsCenter = Vector3D.Zero, BoundsHalfExtents = new(.005, .005, .005),
            PlacementProvenance = RigEvidenceKind.UserOverride, UserApproved = true, Evidence = [evidence],
        };
        var inherited = new RigChannelOwnership { Owners = [RigComponentOwner.BindInherited], Evidence = [evidence] };
        var channel = new AnimationComponentPolicy
        {
            EntityId = helper.Id, Position = inherited, Rotation = inherited, Scale = inherited,
            EmittedMask = RigAnimationComponents.None, AnimationLod = RigAnimationLod.Off,
            LodRuleId = "retention-helper-bind-only", LodEvidence = [evidence],
        };
        var changed = session with { Recipe = session.Recipe with
        {
            Entities = session.Recipe.Entities.Select(e => e.EntityId == parentEntityId && e.Kind == RigNativeEntityKind.Unknown
                ? e with { Kind = RigNativeEntityKind.Bone } : e).ToImmutableArray(),
            Helpers = session.Recipe.Helpers.Add(recipe),
            FramePolicies = session.Recipe.FramePolicies.Where(p => p.EntityId != helper.Id).ToImmutableArray(),
            ComponentPolicies = session.Recipe.ComponentPolicies.Add(channel),
        } };
        updated = updated with
        {
            RiggingSession = RiggingSessions.Change(session, changed, RiggingEditKind.Helpers), LastBuildReceipt = null,
            Diagnostics = updated.Diagnostics.Add(new() { Code = ReviewDiagnosticCode, Severity = CustomModelImportSeverity.Warning,
                Message = $"Retention helper '{name}' was authored under '{row.Name}'. Existing node types, frames, weights and animation policies were preserved. Compiled retention and native behavior remain unverified." }),
        };
        updated.Validate();
        var candidate = model with { Package = model.Package with { Document = updated }, Rig = updated.CreateRigDefinition() };
        FbxProfileEditGuard.RequireAllowed(model, candidate, cancellationToken);
        var candidatePrepared = Dl1CustomModelRigPreparer.Prepare(candidate, cancellationToken);
        foreach (var before in prepared.Contract.Nodes)
        {
            var after = candidatePrepared.Contract.Nodes.Single(n => n.SourceBoneIndex == before.SourceBoneIndex);
            if (before.Name != after.Name || before.IsDeform != after.IsDeform ||
                !before.GlobalBindMatrix.NearlyEquals(after.GlobalBindMatrix, 1e-9) ||
                !before.InverseGlobalReferenceMatrix.NearlyEquals(after.InverseGlobalReferenceMatrix, 1e-9) || before.Bounds != after.Bounds)
                throw new InvalidDataException($"The retention proposal changed prepared node '{before.Name}'. Resolve that frame/bounds interaction before applying.");
        }
        return new(model, candidate, parentEntityId,
            $"Preview only: add helper '{name}' beneath '{row.Name}'. It has no mesh weights or authored animation tracks. Existing bones, frames and weights stay unchanged. Compile this candidate to verify retention, then validate native behavior.")
            { AddedHelperId = helper.Id };
    }
}

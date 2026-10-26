using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public sealed record RigGripHelperPlacement(
    Guid ParentEntityId,
    Guid? HelperEntityId,
    string Name,
    string RoleId,
    RigGripPlacementSolution Placement,
    Vector3D BoundsCenter,
    Vector3D BoundsHalfExtents);

/// <summary>Saves palm-derived grip frames through the contact-helper authoring workflow.</summary>
public static class RigGripHelperAuthoring
{
    public static CustomModelDocument Apply(
        CustomModelDocument document,
        RiggingJobToken token,
        IEnumerable<RigGripHelperPlacement> placements,
        bool recalibrateManualPlacements = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(placements);
        document.Validate();
        RiggingSession session = document.RiggingSession
            ?? throw new InvalidOperationException("Start the model's rig setup before placing hand grips.");
        if (!session.Matches(token))
            throw new InvalidOperationException("The hand setup changed; refresh it before placing grips.");
        RigGripHelperPlacement[] requested = placements.ToArray();
        if (requested.Any(static placement => placement is null))
            throw new ArgumentException("Grip placements cannot contain empty entries.", nameof(placements));
        if (requested.Select(static placement => placement.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Length)
            throw new ArgumentException("Place each grip helper only once in a batch.", nameof(placements));

        CustomModelDocument result = document;
        foreach (RigGripHelperPlacement requestedPlacement in requested)
        {
            ArgumentNullException.ThrowIfNull(requestedPlacement.Placement);
            RiggingSession current = result.RiggingSession!;
            Guid? helperId = ResolveExistingId(current, requestedPlacement);
            bool nativePosition = KeepsOriginalGripFrame(result, helperId, requestedPlacement.Placement);
            HelperRecipe? existing = helperId is { } id
                ? current.Recipe.Helpers.SingleOrDefault(helper => helper.EntityId == id) : null;
            if (!recalibrateManualPlacements && existing is not null &&
                (existing.FramePolicy == RigFramePolicy.Manual ||
                 existing.PlacementProvenance == RigEvidenceKind.UserOverride ||
                 existing.LockedFields != RigHelperEditFields.None))
                continue;
            if (existing is not null && existing.FramePolicy == RigFramePolicy.Socket &&
                existing.PlacementProvenance == RigEvidenceKind.GeometryInference &&
                existing.ParentEntityId == requestedPlacement.ParentEntityId &&
                existing.RoleId == requestedPlacement.RoleId &&
                existing.LocalFrame.NearlyEquals(requestedPlacement.Placement.LocalFrame, 1e-12) &&
                existing.BoundsCenter == requestedPlacement.BoundsCenter &&
                existing.BoundsHalfExtents == requestedPlacement.BoundsHalfExtents)
            {
                result = ApplyBankChannels(result, requestedPlacement.Name, nativePosition);
                continue;
            }
            result = RigContactHelperAuthoring.Apply(result, current.CreateJobToken(),
                requestedPlacement.ParentEntityId, helperId, requestedPlacement.Name, requestedPlacement.RoleId,
                requestedPlacement.Placement.LocalFrame, requestedPlacement.BoundsCenter, requestedPlacement.BoundsHalfExtents,
                RigEvidenceKind.GeometryInference,
                "Grip position and orientation follow the model's palm landmarks and hand size.");
            result = UseSocketPolicy(result, requestedPlacement.Name);
            result = ApplyBankChannels(result, requestedPlacement.Name, nativePosition);
        }
        return RefreshAppliedFit(document, result);
    }

    public static CustomModelDocument SetLocalFrame(
        CustomModelDocument document,
        RiggingJobToken token,
        Guid helperEntityId,
        TransformMatrix localFrame)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(token);
        document.Validate();
        RiggingSession session = document.RiggingSession
            ?? throw new InvalidOperationException("Start the model's rig setup before editing hand grips.");
        if (!session.Matches(token))
            throw new InvalidOperationException("The hand setup changed; refresh it before editing the grip.");
        HelperRecipe recipe = session.Recipe.Helpers.SingleOrDefault(helper => helper.EntityId == helperEntityId)
            ?? throw new InvalidOperationException("Place this grip before editing its local frame.");
        RigEntityBinding entity = session.Recipe.Entities.Single(entity => entity.EntityId == helperEntityId);
        if (recipe.FramePolicy != RigFramePolicy.Socket)
            throw new InvalidOperationException("The selected helper is not a hand-grip socket.");
        CustomModelDocument result = RigContactHelperAuthoring.Apply(document, token,
            recipe.ParentEntityId, helperEntityId, entity.NativeName, recipe.RoleId, localFrame,
            recipe.BoundsCenter ?? Vector3D.Zero, recipe.BoundsHalfExtents ?? new Vector3D(.005, .005, .005),
            RigEvidenceKind.UserOverride, "Grip position and orientation were adjusted by the user.");
        result = UseSocketPolicy(result, entity.NativeName);
        result = ApplyBankChannels(result, entity.NativeName, preferNativePositionTracks: false);
        return RefreshAppliedFit(document, result);
    }

    private static CustomModelDocument UseSocketPolicy(CustomModelDocument document, string name)
    {
        RiggingSession session = document.RiggingSession!;
        Guid id = session.Recipe.Entities.Single(entity => entity.NativeName == name).EntityId;
        if (session.Recipe.Helpers.Single(helper => helper.EntityId == id).FramePolicy == RigFramePolicy.Socket)
            return document;
        RiggingSession updated = session with
        {
            Recipe = session.Recipe with
            {
                Helpers = session.Recipe.Helpers.Select(helper => helper.EntityId == id
                    ? helper with { FramePolicy = RigFramePolicy.Socket } : helper).ToImmutableArray(),
            },
        };
        updated = RiggingSessions.Change(session, updated, RiggingEditKind.Helpers);
        CustomModelDocument result = document with
        {
            RiggingSession = updated,
            AuthoredHelpers = document.AuthoredHelpers.Select(helper => helper.Id == id &&
                helper.Kind == CustomModelAuthoredHelperKind.Helper
                    ? helper with { Kind = CustomModelAuthoredHelperKind.Prop } : helper).ToImmutableArray(),
            LastBuildReceipt = null,
        };
        result = result with { RigSignature = CustomModelContractSignatures.ComputeRig(result.CreateEffectiveBones()) };
        result.Validate();
        return result;
    }

    private static bool KeepsOriginalGripFrame(CustomModelDocument document, Guid? helperId, RigGripPlacementSolution placement)
    {
        if (helperId is not { } id || Math.Abs(placement.HandScale - 1) > RigGripChannelDefaults.NativeFrameTolerance)
            return false;
        var observed = RiggingSessions.ObserveSourceHierarchy(document);
        int index = -1;
        for (int i = 0; i < observed.Length; i++) if (observed[i].EntityId == id) { index = i; break; }
        AnimationComponentPolicy? saved = document.RiggingSession!.Recipe.ComponentPolicies.FirstOrDefault(policy => policy.EntityId == id);
        if (index >= document.Bones.Length && saved?.EmittedMask == RigAnimationComponents.Rotation &&
            saved.LodEvidence.Any(evidence => evidence.Id == RigGripChannelDefaults.EvidenceId) &&
            document.RiggingSession.Recipe.Helpers.Any(helper => helper.EntityId == id))
            return false;
        return index >= 0 && document.CreateEffectiveBones()[index].ExactLocalBindMatrix
            .NearlyEquals(placement.LocalFrame, RigGripChannelDefaults.NativeFrameTolerance);
    }

    private static CustomModelDocument ApplyBankChannels(CustomModelDocument document, string name, bool preferNativePositionTracks)
    {
        if (!RigGripChannelDefaults.IsNativeHandHolder(name)) return document;
        RiggingSession session = document.RiggingSession!;
        Guid id = session.Recipe.Entities.Single(entity => entity.NativeName == name).EntityId;
        return RigGripChannelDefaults.Apply(document, session.CreateJobToken(), [id], preferNativePositionTracks);
    }

    private static CustomModelDocument RefreshAppliedFit(CustomModelDocument before, CustomModelDocument after)
    {
        if (ReferenceEquals(before, after) || before.RigConformance is not { } fit ||
            !fit.MatchesAppliedOutputRig(before.CreateRigDefinition(), before.Source.ContentSha256))
            return after;
        return after with
        {
            RigConformance = fit with { AppliedOutputRigSignature = RigSignature.Compute(after.CreateRigDefinition()) },
        };
    }

    private static Guid? ResolveExistingId(RiggingSession session, RigGripHelperPlacement placement)
    {
        if (placement.HelperEntityId is { } supplied) return supplied;
        RigEntityBinding[] matching = session.Recipe.Entities.Where(entity =>
            entity.NativeName.Equals(placement.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matching.Length > 1)
            throw new InvalidOperationException("The grip name matches more than one helper; select one explicitly.");
        return matching.Length == 1 ? matching[0].EntityId : null;
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigStudioStage { Import, Detect, Fit, HelpersAndHooks, Skin, Animate, VerifyAndExport }
public enum RigGeometryComponentKind { Unknown, Body, Clothing, Accessory }
public enum RigComponentBindingMode { KeepSource, Automatic, Rigid }
public enum RiggingEditKind { Source, Components, Coordinates, Detection, Anatomy, Helpers, Skinning, Motion, Profile, Verification }

public sealed record RigGeometryComponent
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public RigGeometryComponentKind Kind { get; init; }
    public bool Included { get; init; } = true;
    public bool UseForAnatomy { get; init; } = true;
    public RigComponentBindingMode BindingMode { get; init; } = RigComponentBindingMode.KeepSource;
    public string? RigidBoneRoleId { get; init; }
    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigContractRules.Text(DisplayName, nameof(DisplayName));
        RigContractRules.Defined(Kind, nameof(Kind));
        RigContractRules.Defined(BindingMode, nameof(BindingMode));
        RigContractRules.OptionalText(RigidBoneRoleId, nameof(RigidBoneRoleId));
        if (BindingMode == RigComponentBindingMode.Rigid && RigidBoneRoleId is null)
            throw new ArgumentException("Rigid component binding requires an explicit bone role.");
        if (!Included && UseForAnatomy) throw new ArgumentException("Excluded components cannot contribute anatomical evidence.");
    }
}

public sealed record RiggingBackendReference
{
    public string Id { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SettingsSha256 { get; init; } = string.Empty;
    public long? DeterministicSeed { get; init; }
    public void Validate()
    {
        RigContractRules.Text(Id, nameof(Id));
        RigContractRules.Text(Version, nameof(Version));
        RigContractRules.Hash(SettingsSha256, nameof(SettingsSha256));
    }
}

/// <summary>An anatomical guide/proposal in authoring-model space, not another emitted skeleton.</summary>
public sealed record RigLandmark
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string RoleId { get; init; } = string.Empty;
    public Vector3D Position { get; init; }
    public bool Locked { get; init; }
    public bool UserApproved { get; init; }
    public Guid? MirrorPartnerId { get; init; }
    public RigEvidenceKind Provenance { get; init; } = RigEvidenceKind.GeometryInference;
    public double? Confidence { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];
    public void Validate()
    {
        RigContractRules.Identifier(Id, nameof(Id));
        RigContractRules.Text(RoleId, nameof(RoleId));
        RigContractRules.Defined(Provenance, nameof(Provenance));
        if (!Position.IsFinite) throw new ArgumentException("Guide positions must be finite authoring-model coordinates.");
        if (MirrorPartnerId is { } partner)
        {
            RigContractRules.Identifier(partner, nameof(MirrorPartnerId));
            if (partner == Id) throw new ArgumentException("A guide cannot mirror itself.");
        }
        if (Confidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1))
            throw new ArgumentException("Anatomical confidence must be finite and within 0..1.");
        RigRecipeRules.Evidence(Evidence);
    }
}

public sealed record RigStageState
{
    public RigStudioStage Stage { get; init; }
    public long Revision { get; init; }
    public string? ReviewedInputFingerprint { get; init; }
    public DateTimeOffset? ReviewedUtc { get; init; }
    public void Validate()
    {
        RigContractRules.Defined(Stage, nameof(Stage));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        RigContractRules.OptionalHash(ReviewedInputFingerprint, nameof(ReviewedInputFingerprint));
        if ((ReviewedUtc is null) != (ReviewedInputFingerprint is null) || ReviewedUtc == default(DateTimeOffset))
            throw new ArgumentException("Stage review requires a dated input fingerprint.");
    }
}

public sealed record RiggingJobToken(Guid SessionId, Guid Generation, long Revision, string InputFingerprint);

/// <summary>A source-point influence lock. Its current fraction, including zero, is preserved by weight edits.</summary>
public sealed record RigSkinWeightLock(string ComponentId, int ControlPointIndex, Guid EntityId);
public sealed record RigWeightMirrorPair(Guid EntityId, Guid CounterpartEntityId);

/// <summary>
/// Model-owned studio decisions. Render geometry and the frozen emitted rig
/// remain owned by the existing model document and authored-rig contract.
/// Review approvals never create validation evidence.
/// </summary>
public sealed record RiggingSession
{
    public const int CurrentVersion = 1;
    public int Version { get; init; } = CurrentVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid OwnerModelId { get; init; }
    public Guid Generation { get; init; } = Guid.NewGuid();
    public long Revision { get; init; }
    public string SourceSha256 { get; init; } = string.Empty;
    public string? PreviousSourceSha256 { get; init; }
    public bool RequiresSourceReview { get; init; }
    public RigStudioEntryPath EntryPath { get; init; }
    public RigStudioStage Stage { get; init; }
    public ImmutableArray<RigGeometryComponent> Components { get; init; } = [];
    public ImmutableArray<RigLandmark> Landmarks { get; init; } = [];
    /// <summary>Persisted hand/finger setup decisions; no native rig is implied.</summary>
    public ImmutableArray<RigHandSetup> Hands { get; init; } = [];
    /// <summary>Persisted eye source, pivot, gaze and mimic decisions; no native contract is implied.</summary>
    public ImmutableArray<RigEyeSetup> Eyes { get; init; } = [];
    /// <summary>Explicit reviewed parent choices checked against the current hierarchy.</summary>
    public ImmutableArray<RigParentDecision> ParentDecisions { get; init; } = [];
    public ImmutableArray<RigSkinWeightLock> WeightLocks { get; init; } = [];
    public ImmutableArray<RigWeightMirrorPair> WeightMirrorPairs { get; init; } = [];
    public bool MirrorWeightEdits { get; init; }
    public double WeightMirrorTolerance { get; init; } = .005;
    public RuntimeRigRecipe Recipe { get; init; } = new();
    public RiggingBackendReference? DetectionBackend { get; init; }
    public RiggingBackendReference? BindingBackend { get; init; }
    public Vector3D SymmetryOrigin { get; init; }
    public Vector3D SymmetryNormal { get; init; } = Vector3D.UnitX;
    public bool MirroringEnabled { get; init; }
    public ImmutableArray<RigStageState> Stages { get; init; } =
        Enum.GetValues<RigStudioStage>().Select(static stage => new RigStageState { Stage = stage }).ToImmutableArray();
    public ImmutableArray<CapabilityValidationReceipt> ValidationHistory { get; init; } = [];

    public void Validate()
    {
        if (Version != CurrentVersion) throw new ArgumentException("Unsupported rigging-session version.");
        RigContractRules.Identifier(Id, nameof(Id));
        RigContractRules.Identifier(OwnerModelId, nameof(OwnerModelId));
        RigContractRules.Identifier(Generation, nameof(Generation));
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        RigContractRules.Hash(SourceSha256, nameof(SourceSha256));
        RigContractRules.OptionalHash(PreviousSourceSha256, nameof(PreviousSourceSha256));
        RigContractRules.Defined(EntryPath, nameof(EntryPath));
        RigContractRules.Defined(Stage, nameof(Stage));
        RigContractRules.Array(Components, nameof(Components));
        RigContractRules.Array(Landmarks, nameof(Landmarks));
        RigContractRules.Array(Hands, nameof(Hands));
        RigContractRules.Array(Eyes, nameof(Eyes));
        RigContractRules.Array(ParentDecisions, nameof(ParentDecisions));
        RigContractRules.Array(WeightLocks, nameof(WeightLocks));
        RigContractRules.Array(WeightMirrorPairs, nameof(WeightMirrorPairs));
        RigContractRules.Array(Stages, nameof(Stages));
        RigContractRules.Array(ValidationHistory, nameof(ValidationHistory));
        ArgumentNullException.ThrowIfNull(Recipe); Recipe.Validate();
        DetectionBackend?.Validate(); BindingBackend?.Validate();
        if (!SymmetryOrigin.IsFinite || !SymmetryNormal.IsFinite ||
            !double.IsFinite(SymmetryNormal.LengthSquared) || SymmetryNormal.LengthSquared == 0)
            throw new ArgumentException("A symmetry plane requires a finite origin and nonzero finite normal.");
        foreach (RigGeometryComponent component in Components) component.Validate();
        foreach (RigLandmark landmark in Landmarks) landmark.Validate();
        var componentIds = Components.Select(static c => c.Id).ToHashSet(StringComparer.Ordinal);
        var entityIds = Recipe.Entities.Where(e => e.OwnerAssetId == OwnerModelId).Select(static e => e.EntityId).ToHashSet();
        if (WeightLocks.Length > 1_000_000 || WeightLocks.Distinct().Count() != WeightLocks.Length ||
            WeightLocks.Any(l => l is null || l.ControlPointIndex < 0 || !componentIds.Contains(l.ComponentId) || !entityIds.Contains(l.EntityId)))
            throw new ArgumentException("Weight locks require unique owned source-point and influence identities within the supported limit.");
        if (!double.IsFinite(WeightMirrorTolerance) || WeightMirrorTolerance <= 0) throw new ArgumentException("Weight mirror tolerance must be positive and finite.");
        var paired = new HashSet<Guid>();
        foreach (var pair in WeightMirrorPairs)
        {
            if (pair is null || !entityIds.Contains(pair.EntityId) || !entityIds.Contains(pair.CounterpartEntityId) || !paired.Add(pair.EntityId) ||
                pair.EntityId != pair.CounterpartEntityId && !paired.Add(pair.CounterpartEntityId))
                throw new ArgumentException("Weight mirror pairings must be unambiguous owned influence identities.");
        }
        foreach (RigStageState state in Stages) state.Validate();
        foreach (CapabilityValidationReceipt receipt in ValidationHistory) receipt.Validate();
        if (Components.Select(static c => c.Id).Distinct(StringComparer.Ordinal).Count() != Components.Length ||
            Landmarks.Select(static l => l.Id).Distinct().Count() != Landmarks.Length ||
            ValidationHistory.Select(static r => r.Id).Distinct().Count() != ValidationHistory.Length)
            throw new ArgumentException("Session component, landmark and receipt identities must be unique.");
        var guidesById = Landmarks.ToDictionary(static landmark => landmark.Id);
        if (Hands.Select(static hand => hand.Side).Distinct().Count() != Hands.Length)
            throw new ArgumentException("Only one hand setup may be stored per side.", nameof(Hands));
        var usedHandGuides = new HashSet<Guid>();
        foreach (RigHandSetup hand in Hands)
        {
            hand.Validate(guidesById);
            if (!usedHandGuides.Add(hand.WristGuideId))
                throw new ArgumentException("A wrist guide cannot be reused by multiple hand setups.", nameof(Hands));
            foreach (RigFingerDeclaration finger in hand.Fingers)
                foreach (Guid guideId in finger.JointGuideIds)
                    if (!usedHandGuides.Add(guideId))
                        throw new ArgumentException("A finger guide cannot be reused by multiple declarations.", nameof(Hands));
        }
        if (Eyes.Select(static eye => (eye.Side, eye.Mode)).Distinct().Count() != Eyes.Length)
            throw new ArgumentException("An eye setup cannot duplicate a side and mode pair.", nameof(Eyes));
        foreach (RigEyeSetup eye in Eyes) eye.Validate(entityIds, componentIds);
        var parentDecisionIds = new HashSet<Guid>();
        foreach (RigParentDecision decision in ParentDecisions)
        {
            decision.Validate();
            if (!parentDecisionIds.Add(decision.EntityId) || !entityIds.Contains(decision.EntityId) ||
                decision.ParentEntityId is { } parent && !entityIds.Contains(parent) ||
                !RigContractRules.SameHash(decision.SourceSha256, SourceSha256))
                throw new ArgumentException("Parent decisions must be unique, current-source, and reference owned entities.", nameof(ParentDecisions));
        }
        var landmarks = Landmarks.Select(static l => l.Id).ToHashSet();
        if (Landmarks.Any(l => l.MirrorPartnerId is { } partner && !landmarks.Contains(partner)))
            throw new ArgumentException("A mirror partner must identify a retained guide.");
        if (Stages.Length != Enum.GetValues<RigStudioStage>().Length || Stages.Select(static s => s.Stage).Distinct().Count() != Stages.Length)
            throw new ArgumentException("A rigging session must retain all seven stage states.");
    }

    public string ComputeInputFingerprint()
    {
        Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version, OwnerModelId, SourceSha256, EntryPath, Components, Landmarks, Hands, Eyes, ParentDecisions, WeightLocks, Recipe,
            DetectionBackend, BindingBackend, SymmetryOrigin, SymmetryNormal, MirroringEnabled, WeightMirrorPairs, MirrorWeightEdits, WeightMirrorTolerance,
        }, CustomModelPackageSerializer.CreateSerializerOptions());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public RiggingJobToken CreateJobToken() => new(Id, Generation, Revision, ComputeInputFingerprint());

    public bool Matches(RiggingJobToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.SessionId == Id && token.Generation == Generation && token.Revision == Revision &&
            RigContractRules.SameHash(token.InputFingerprint, ComputeInputFingerprint());
    }

    public bool MatchesSource(string sourceSha256)
    {
        RigContractRules.Hash(sourceSha256, nameof(sourceSha256));
        return !RequiresSourceReview && RigContractRules.SameHash(SourceSha256, sourceSha256);
    }
}

public static class RiggingSessions
{
    /// <summary>Navigation does not alter authoring inputs, invalidate review records or impersonate a completed stage.</summary>
    public static RiggingSession Navigate(RiggingSession session, RigStudioStage stage)
    {
        ArgumentNullException.ThrowIfNull(session); session.Validate(); RigContractRules.Defined(stage, nameof(stage));
        return session.Stage == stage ? session : session with { Stage = stage };
    }

    public static RiggingSession Create(CustomModelDocument document, RigStudioEntryPath entryPath)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        RigContractRules.Defined(entryPath, nameof(entryPath));
        if (entryPath == RigStudioEntryPath.AutoRigBiped && !document.Bones.IsEmpty)
            throw new ArgumentException("The unrigged entry path requires an unrigged source.", nameof(entryPath));
        if (entryPath != RigStudioEntryPath.AutoRigBiped && document.Bones.IsEmpty)
            throw new ArgumentException("A rigged entry path requires source bones.", nameof(entryPath));
        var entities = document.Bones.Select(bone =>
        {
            string sourceId = SourceIdentity(bone);
            return new RigEntityBinding
            {
                EntityId = StableId(document.ModelId, sourceId), OwnerAssetId = document.ModelId,
                SourceEntityId = sourceId, NativeName = bone.Name, Kind = RigNativeEntityKind.Unknown,
            };
        }).Concat(document.AuthoredHelpers.Select(helper => new RigEntityBinding
        {
            EntityId = helper.Id, OwnerAssetId = document.ModelId, SourceEntityId = "authored:" + helper.Id.ToString("N"),
            NativeName = helper.Name, Kind = RigNativeEntityKind.Helper, Imported = false,
        })).ToImmutableArray();
        var session = new RiggingSession
        {
            OwnerModelId = document.ModelId, SourceSha256 = document.Source.ContentSha256, EntryPath = entryPath,
            Components = document.Meshes.Select(mesh => new RigGeometryComponent
            {
                Id = "fbx:" + mesh.ModelObjectId.ToString(CultureInfo.InvariantCulture) + ":" + mesh.GeometryObjectId.ToString(CultureInfo.InvariantCulture),
                DisplayName = mesh.Name,
                BindingMode = entryPath == RigStudioEntryPath.AutoRigBiped ? RigComponentBindingMode.Automatic : RigComponentBindingMode.KeepSource,
            }).ToImmutableArray(),
            Recipe = new RuntimeRigRecipe
            {
                Entities = entities,
                AssetRoles = [new RigAssetRoleBinding("character", document.ModelId)],
                MotionStrategy = entryPath == RigStudioEntryPath.RepairExistingRig ? RigMotionStrategy.RepairExistingRig : RigMotionStrategy.PreserveAnatomyMapped,
            },
        };
        session.Validate(); return session;
    }

    public static RiggingSession Change(RiggingSession current, RiggingSession replacement, RiggingEditKind kind, bool allowLockedChanges = false)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(replacement);
        current.Validate(); replacement.Validate(); RigContractRules.Defined(kind, nameof(kind));
        if (current.Id != replacement.Id || current.OwnerModelId != replacement.OwnerModelId)
            throw new ArgumentException("An edit cannot switch the owning model or session identity.");
        foreach (RigEntityBinding entity in current.Recipe.Entities)
        {
            RigEntityBinding? next = replacement.Recipe.Entities.FirstOrDefault(e => e.EntityId == entity.EntityId);
            if (next is not null && next.OwnerAssetId != entity.OwnerAssetId)
                throw new ArgumentException("Stable entity identities cannot be reassigned to a different owning asset.");
        }
        if (!Equal(current.Landmarks, replacement.Landmarks))
        {
            var nextGuides = replacement.Landmarks.ToDictionary(static g => g.Id);
            var changedGuides = current.Landmarks.Where(g => !nextGuides.TryGetValue(g.Id, out var next) || next != g).Select(static g => g.Id).ToHashSet();
            replacement = replacement with
            {
                Hands = replacement.Hands.Select(hand => changedGuides.Contains(hand.WristGuideId) || hand.Fingers.Any(f => f.JointGuideIds.Any(changedGuides.Contains)) ? hand with
                {
                    UserApproved = false,
                    Fingers = hand.Fingers.Select(static finger => finger with { UserApproved = false }).ToImmutableArray(),
                } : hand).ToImmutableArray(),
            };
            replacement.Validate();
        }
        if (!allowLockedChanges) RequireLocksPreserved(current, replacement);
        RigStudioStage first = kind switch
        {
            RiggingEditKind.Source or RiggingEditKind.Components or RiggingEditKind.Coordinates => RigStudioStage.Import,
            RiggingEditKind.Detection or RiggingEditKind.Profile => RigStudioStage.Detect,
            RiggingEditKind.Anatomy => RigStudioStage.Fit,
            RiggingEditKind.Helpers => RigStudioStage.HelpersAndHooks,
            RiggingEditKind.Skinning => RigStudioStage.Skin,
            RiggingEditKind.Motion => RigStudioStage.Animate,
            RiggingEditKind.Verification => RigStudioStage.VerifyAndExport,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        // The caller's edit label cannot hide a source/profile change and keep
        // earlier approvals current. Inspect the actual dependency-bearing data.
        if (!RigContractRules.SameHash(current.SourceSha256, replacement.SourceSha256) || !Equal(current.Components, replacement.Components) ||
            current.Recipe.ScalePolicy.SourceMetersPerUnit != replacement.Recipe.ScalePolicy.SourceMetersPerUnit ||
            current.Recipe.ScalePolicy.SourceToAuthoring != replacement.Recipe.ScalePolicy.SourceToAuthoring ||
            current.Recipe.ScalePolicy.ImportConversionAlreadyApplied != replacement.Recipe.ScalePolicy.ImportConversionAlreadyApplied)
            first = RigStudioStage.Import;
        if (current.EntryPath != replacement.EntryPath || !Equal(current.Recipe.Profile, replacement.Recipe.Profile) ||
            !Equal(current.Recipe.SelectedCapabilityIds, replacement.Recipe.SelectedCapabilityIds) || !Equal(current.DetectionBackend, replacement.DetectionBackend) ||
            current.SymmetryOrigin != replacement.SymmetryOrigin || current.SymmetryNormal != replacement.SymmetryNormal || current.MirroringEnabled != replacement.MirroringEnabled)
            first = Earlier(first, RigStudioStage.Detect);
        if (!Equal(current.Landmarks, replacement.Landmarks) || !Equal(current.Recipe.Entities, replacement.Recipe.Entities) ||
            !Equal(current.Recipe.Assignments, replacement.Recipe.Assignments) || !Equal(current.Recipe.AssetRoles, replacement.Recipe.AssetRoles) ||
            !Equal(current.Recipe.ScalePolicy, replacement.Recipe.ScalePolicy) ||
            current.Recipe.MotionStrategy != replacement.Recipe.MotionStrategy)
            first = Earlier(first, RigStudioStage.Fit);
        if (!Equal(current.Hands, replacement.Hands)) first = Earlier(first, RigStudioStage.Fit);
        if (!Equal(current.Eyes, replacement.Eyes)) first = Earlier(first, RigStudioStage.Fit);
        if (!Equal(current.ParentDecisions, replacement.ParentDecisions)) first = Earlier(first, RigStudioStage.Fit);
        if (!Equal(current.Recipe.Helpers, replacement.Recipe.Helpers) || !Equal(current.Recipe.ComponentPolicies, replacement.Recipe.ComponentPolicies) ||
            !Equal(current.Recipe.FramePolicies, replacement.Recipe.FramePolicies))
            first = Earlier(first, RigStudioStage.HelpersAndHooks);
        if (!Equal(current.BindingBackend, replacement.BindingBackend) || !Equal(current.WeightLocks, replacement.WeightLocks) ||
            !Equal(current.WeightMirrorPairs, replacement.WeightMirrorPairs) || current.MirrorWeightEdits != replacement.MirrorWeightEdits ||
            current.WeightMirrorTolerance != replacement.WeightMirrorTolerance) first = Earlier(first, RigStudioStage.Skin);
        RiggingSession changed = replacement with
        {
            Revision = checked(current.Revision + 1), Generation = Guid.NewGuid(),
            Stages = current.Stages.Select(state => state.Stage < first ? state : state with
            {
                Revision = checked(state.Revision + 1), ReviewedInputFingerprint = null, ReviewedUtc = null,
            }).ToImmutableArray(),
            // Evidence remains historical. Its scope determines whether it can support the new inputs.
            ValidationHistory = current.ValidationHistory,
        };
        changed.Validate(); return changed;
        static RigStudioStage Earlier(RigStudioStage left, RigStudioStage right) => left < right ? left : right;
        static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left, CustomModelPackageSerializer.CreateSerializerOptions()) ==
            JsonSerializer.Serialize(right, CustomModelPackageSerializer.CreateSerializerOptions());
    }

    public static RiggingSession RecordReview(RiggingSession session, RigStudioStage stage, string expectedInputFingerprint, DateTimeOffset observedUtc)
    {
        ArgumentNullException.ThrowIfNull(session); session.Validate(); RigContractRules.Defined(stage, nameof(stage));
        if (observedUtc == default || !RigContractRules.SameHash(expectedInputFingerprint, session.ComputeInputFingerprint()))
            throw new InvalidOperationException("The reviewed inputs changed before approval was recorded.");
        return session with { RequiresSourceReview = stage != RigStudioStage.Import && session.RequiresSourceReview, Stages = session.Stages.Select(s => s.Stage != stage ? s : s with
        {
            ReviewedInputFingerprint = expectedInputFingerprint, ReviewedUtc = observedUtc,
        }).ToImmutableArray() };
    }

    public static bool TryAcceptLandmarks(RiggingSession current, RiggingJobToken token, ImmutableArray<RigLandmark> proposals, out RiggingSession result)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(token);
        result = current;
        if (!current.Matches(token)) return false;
        RigContractRules.Array(proposals, nameof(proposals));
        RigLandmark[] locked = current.Landmarks.Where(static l => l.Locked).ToArray();
        var retainedHandGuideIds = current.Hands.SelectMany(static hand =>
            new[] { hand.WristGuideId }.Concat(hand.Fingers.SelectMany(static finger => finger.JointGuideIds))).ToHashSet();
        ImmutableArray<RigLandmark> retained = current.Landmarks.Where(landmark =>
            landmark.Locked || retainedHandGuideIds.Contains(landmark.Id)).ToImmutableArray();
        var retainedIds = retained.Select(static landmark => landmark.Id).ToHashSet();
        var retainedRoles = retained.Select(static landmark => landmark.RoleId).ToHashSet(StringComparer.Ordinal);
        ImmutableArray<RigLandmark> merged = retained.Concat(proposals.Where(proposal =>
            !retainedIds.Contains(proposal.Id) && !retainedRoles.Contains(proposal.RoleId))).ToImmutableArray();
        result = Change(current, current with { Landmarks = merged }, RiggingEditKind.Detection);
        return true;
    }

    public static RiggingSession ReconcileSource(RiggingSession session, string sourceSha256)
    {
        ArgumentNullException.ThrowIfNull(session); session.Validate(); RigContractRules.Hash(sourceSha256, nameof(sourceSha256));
        if (RigContractRules.SameHash(session.SourceSha256, sourceSha256)) return session;
        ImmutableArray<RigHandSetup> invalidatedHands = session.Hands.Select(static hand => hand with
        {
            UserApproved = false,
            Fingers = hand.Fingers.Select(static finger => finger with { UserApproved = false }).ToImmutableArray(),
        }).ToImmutableArray();
        ImmutableArray<RigEyeSetup> invalidatedEyes = session.Eyes.Select(static eye => eye with { UserApproved = false }).ToImmutableArray();
        ImmutableArray<RigParentDecision> invalidatedParents = session.ParentDecisions.Select(decision => decision with
        {
            SourceSha256 = sourceSha256,
            UserApproved = false,
        }).ToImmutableArray();
        return Change(session, session with
        {
            PreviousSourceSha256 = session.SourceSha256, SourceSha256 = sourceSha256, RequiresSourceReview = true,
            Hands = invalidatedHands,
            Eyes = invalidatedEyes,
            ParentDecisions = invalidatedParents,
        }, RiggingEditKind.Source);
    }

    /// <summary>Undo restores decisions but advances generation, preventing stale-result ABA reuse.</summary>
    public static RiggingSession RestoreForUndo(RiggingSession current, RiggingSession snapshot)
    {
        ArgumentNullException.ThrowIfNull(current); ArgumentNullException.ThrowIfNull(snapshot);
        current.Validate(); snapshot.Validate();
        if (current.Id != snapshot.Id || current.OwnerModelId != snapshot.OwnerModelId)
            throw new ArgumentException("Undo cannot restore another model's session.");
        return snapshot with { Revision = checked(current.Revision + 1), Generation = Guid.NewGuid() };
    }

    public static RiggingSession RecordValidation(RiggingSession session, CapabilityValidationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(receipt);
        session.Validate(); receipt.Validate();
        if (session.ValidationHistory.Any(r => r.Id == receipt.Id)) throw new ArgumentException("Receipt identity already exists.", nameof(receipt));
        return session with { ValidationHistory = session.ValidationHistory.Add(receipt) };
    }

    private static void RequireLocksPreserved(RiggingSession current, RiggingSession replacement)
    {
        foreach (RigLandmark landmark in current.Landmarks.Where(static l => l.Locked))
        {
            RigLandmark? next = replacement.Landmarks.FirstOrDefault(l => l.Id == landmark.Id);
            if (next is null || next.Position != landmark.Position || next.RoleId != landmark.RoleId || !next.Locked)
                throw new InvalidOperationException("Locked landmarks require an explicit unlock before changes.");
        }
        foreach (HelperRecipe helper in current.Recipe.Helpers.Where(static h => h.LockedFields != RigHelperEditFields.None))
        {
            HelperRecipe? next = replacement.Recipe.Helpers.FirstOrDefault(h => h.EntityId == helper.EntityId);
            if (next is null) throw new InvalidOperationException("A locked helper cannot be removed implicitly.");
            RigHelperEditFields fields = helper.LockedFields;
            if ((next.LockedFields & fields) != fields ||
                fields.HasFlag(RigHelperEditFields.Parent) && next.ParentEntityId != helper.ParentEntityId ||
                fields.HasFlag(RigHelperEditFields.Position) && next.LocalFrame.Translation != helper.LocalFrame.Translation ||
                fields.HasFlag(RigHelperEditFields.Orientation) && WithoutTranslation(next.LocalFrame) != WithoutTranslation(helper.LocalFrame) ||
                fields.HasFlag(RigHelperEditFields.Extents) && (next.BoundsCenter != helper.BoundsCenter || next.BoundsHalfExtents != helper.BoundsHalfExtents) ||
                fields.HasFlag(RigHelperEditFields.Name) && Name(replacement, helper.EntityId) != Name(current, helper.EntityId) ||
                fields.HasFlag(RigHelperEditFields.Channels) && !SamePolicy(current, replacement, helper.EntityId))
                throw new InvalidOperationException("A locked helper field requires an explicit unlock before changes.");
        }
        static string Name(RiggingSession s, Guid id) => s.Recipe.Entities.Single(e => e.EntityId == id).NativeName;
        static bool SamePolicy(RiggingSession before, RiggingSession after, Guid id) =>
            JsonSerializer.Serialize(before.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == id)) ==
            JsonSerializer.Serialize(after.Recipe.ComponentPolicies.FirstOrDefault(p => p.EntityId == id));
        static TransformMatrix WithoutTranslation(TransformMatrix m) => m with { M14 = 0, M24 = 0, M34 = 0 };
    }

    /// <summary>
    /// Reads parentage from the actual current document. Recipe row order and
    /// planned helper parent overrides cannot masquerade as source observations.
    /// </summary>
    public static ImmutableArray<RigParentObservation> ObserveSourceHierarchy(CustomModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document); document.Validate();
        RiggingSession session = document.RiggingSession ?? throw new InvalidOperationException("The model has no rigging session.");
        if (!session.MatchesSource(document.Source.ContentSha256))
            throw new InvalidOperationException("Review and reconcile the changed source before inspecting its role assignments.");
        var imported = session.Recipe.Entities.Where(e => e.OwnerAssetId == document.ModelId && e.SourceEntityId is not null)
            .ToDictionary(static e => e.SourceEntityId!, StringComparer.Ordinal);
        bool generatedBody = GeneratedBodyRig.IsGenerated(document);
        bool hasGeneratedEyeRows = session.Recipe.Entities.Any(entity =>
            entity.SourceEntityId?.StartsWith("generated-eye:", StringComparison.Ordinal) == true);
        bool generatedEyes = hasGeneratedEyeRows && GeneratedEyeRig.ExtensionsValid(document);
        var generated = generatedBody || generatedEyes
            ? session.Recipe.Entities.Where(e => e.OwnerAssetId == document.ModelId && !e.Imported &&
                    ((generatedBody && e.SourceEntityId?.StartsWith("generated-body:", StringComparison.Ordinal) == true) ||
                     (generatedEyes && e.SourceEntityId?.StartsWith("generated-eye:", StringComparison.Ordinal) == true)))
                .ToDictionary(static e => e.NativeName, StringComparer.Ordinal)
            : new Dictionary<string, RigEntityBinding>(StringComparer.Ordinal);
        var nodeIds = new List<Guid>(document.Bones.Length + document.AuthoredHelpers.Length);
        foreach (CustomModelBone bone in document.Bones)
        {
            if (!imported.TryGetValue(SourceIdentity(bone), out RigEntityBinding? entity) && !generated.TryGetValue(bone.Name, out entity))
                throw new InvalidOperationException($"Source entity '{bone.Name}' has no reconciled stable studio identity.");
            nodeIds.Add(entity.EntityId);
        }
        foreach (CustomModelAuthoredHelper helper in document.AuthoredHelpers)
        {
            if (!session.Recipe.Entities.Any(e => e.EntityId == helper.Id && e.OwnerAssetId == document.ModelId))
                throw new InvalidOperationException($"Authored helper '{helper.Name}' has no reconciled stable studio identity.");
            nodeIds.Add(helper.Id);
        }
        var result = ImmutableArray.CreateBuilder<RigParentObservation>(nodeIds.Count);
        foreach (CustomModelBone bone in document.Bones)
            result.Add(new(nodeIds[bone.Index], bone.ParentIndex < 0 ? null : nodeIds[bone.ParentIndex]));
        for (int i = 0; i < document.AuthoredHelpers.Length; i++)
        {
            CustomModelAuthoredHelper helper = document.AuthoredHelpers[i];
            result.Add(new(helper.Id, helper.ParentNodeIndex < 0 ? null : nodeIds[helper.ParentNodeIndex]));
        }
        ImmutableArray<RigParentObservation> orderedObservations = result.ToImmutable();
        var actualParents = orderedObservations.ToDictionary(static observation => observation.EntityId);
        foreach (RigParentDecision decision in session.ParentDecisions)
        {
            if (!actualParents.TryGetValue(decision.EntityId, out RigParentObservation? actual) ||
                actual.ParentEntityId != decision.ParentEntityId)
                throw new InvalidOperationException("A persisted parent decision does not match the current observed hierarchy.");
        }
        return orderedObservations;
    }

    private static string SourceIdentity(CustomModelBone bone) => bone.FbxObjectId == 0
        ? "source-name:" + bone.Name : "fbx:" + bone.FbxObjectId.ToString(CultureInfo.InvariantCulture);

    private static Guid StableId(Guid owner, string sourceId) => new(SHA256.HashData(Encoding.UTF8.GetBytes(owner.ToString("N") + ":" + sourceId)).AsSpan(0, 16));
}

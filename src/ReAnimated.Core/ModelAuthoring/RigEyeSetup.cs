using System.Collections.Immutable;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Core.ModelAuthoring;

public enum RigEyeSide { Left, Right, Shared }
public enum RigEyeSetupMode { SourceEye, GeometryPivot, GazeReference, Mimic }
public enum RigEyeGeometryKind { Unspecified, GlobeCandidate, ManualPivot, Painted }

/// <summary>
/// A persisted eye authoring decision. This is source-linked evidence and
/// review state; it does not assert a native camera, skeleton, or mimic
/// contract.
/// </summary>
public sealed record RigEyeSetup
{
    public RigEyeSide Side { get; init; }
    public RigEyeSetupMode Mode { get; init; }
    public RigEyeGeometryKind GeometryKind { get; init; }
    public Guid? SourceEntityId { get; init; }
    public Guid? ParentEntityId { get; init; }
    public Guid? HelperEntityId { get; init; }
    public Guid? DeformEntityId { get; init; }
    public TransformMatrix GlobalFrame { get; init; } = TransformMatrix.Identity;
    public string? ComponentId { get; init; }
    public int? IslandIndex { get; init; }
    public ImmutableArray<int> SourceControlPointIds { get; init; } = [];
    public double? GlobeRadius { get; init; }
    public ImmutableArray<uint> MorphDescriptors { get; init; } = [];
    public bool UserApproved { get; init; }
    public ImmutableArray<RigEvidenceReference> Evidence { get; init; } = [];

    /// <summary>Validates fields that do not require a document's entity map.</summary>
    public void Validate() => Validate(null, null);

    /// <summary>
    /// Validates this setup and, when supplied, its references into the owning
    /// session. Component validation is deliberately separate from entity
    /// validation because a standalone proposal has no source component map.
    /// </summary>
    internal void Validate(IReadOnlySet<Guid>? ownedEntityIds, IReadOnlySet<string>? componentIds)
    {
        if (!Enum.IsDefined(Side)) throw new ArgumentOutOfRangeException(null, "Eye side must be a defined value.");
        if (!Enum.IsDefined(Mode)) throw new ArgumentOutOfRangeException(null, "Eye setup mode must be a defined value.");
        if (!Enum.IsDefined(GeometryKind)) throw new ArgumentOutOfRangeException(null, "Eye geometry kind must be a defined value.");
        if (!GlobalFrame.IsFinite || GlobalFrame.M41 != 0 || GlobalFrame.M42 != 0 || GlobalFrame.M43 != 0 || GlobalFrame.M44 != 1 ||
            !double.IsFinite(GlobalFrame.LinearDeterminant) || GlobalFrame.LinearDeterminant == 0)
            throw new ArgumentException("Eye frames must be finite, affine and nonsingular.");
        if ((Mode is RigEyeSetupMode.GeometryPivot or RigEyeSetupMode.GazeReference) && GlobalFrame.LinearDeterminant <= 0)
            throw new ArgumentException("Generated eye pivot and gaze frames must have positive orientation.");

        ValidateEntityReference(SourceEntityId, nameof(SourceEntityId), ownedEntityIds);
        ValidateEntityReference(ParentEntityId, nameof(ParentEntityId), ownedEntityIds);
        ValidateEntityReference(HelperEntityId, nameof(HelperEntityId), ownedEntityIds);
        ValidateEntityReference(DeformEntityId, nameof(DeformEntityId), ownedEntityIds);
        if (DeformEntityId is not null && Mode != RigEyeSetupMode.GeometryPivot)
            throw new ArgumentException("Only a geometry-pivot setup may own a generated deform entity.");

        if (ComponentId is { } componentId)
        {
            RigContractRules.Text(componentId, nameof(ComponentId));
            if (componentIds is not null && !componentIds.Contains(componentId))
                throw new ArgumentException("The eye setup references a component that is not owned by the session.");
        }
        if (IslandIndex is < 0) throw new ArgumentOutOfRangeException(null, "Island indices must be nonnegative.");

        RigContractRules.Array(SourceControlPointIds, nameof(SourceControlPointIds));
        if (SourceControlPointIds.Any(static id => id < 0) ||
            SourceControlPointIds.Distinct().Count() != SourceControlPointIds.Length)
            throw new ArgumentException("Source control-point identities must be distinct and nonnegative.");

        if (GlobeRadius is { } radius && (!double.IsFinite(radius) || radius <= 0))
            throw new ArgumentOutOfRangeException(null, "A globe radius must be finite and positive.");
        RigContractRules.Array(MorphDescriptors, nameof(MorphDescriptors));
        if (MorphDescriptors.Distinct().Count() != MorphDescriptors.Length)
            throw new ArgumentException("Morph descriptor identities must be distinct.");
        RigRecipeRules.Evidence(Evidence);

        if (Mode == RigEyeSetupMode.SourceEye && SourceEntityId is null)
            throw new ArgumentException("A source-eye setup requires a source entity identity.");

        if (GeometryKind == RigEyeGeometryKind.GlobeCandidate)
        {
            if (Mode != RigEyeSetupMode.GeometryPivot)
                throw new ArgumentException("A globe candidate can only describe a geometry pivot.");
            if (ComponentId is null || IslandIndex is null || SourceControlPointIds.IsEmpty || GlobeRadius is null || Evidence.IsEmpty)
                throw new ArgumentException("A globe candidate requires a component, island, support control points, positive radius and evidence.");
        }

        if (GeometryKind == RigEyeGeometryKind.Painted)
        {
            if (Mode is not (RigEyeSetupMode.SourceEye or RigEyeSetupMode.GazeReference))
                throw new ArgumentException("Painted eye data may describe an observed source eye or gaze reference, but cannot claim generated geometry-pivot ownership.");
            if (GlobeRadius is not null)
                throw new ArgumentException("Painted eye data cannot claim a fitted globe radius.");
        }

        if (Mode == RigEyeSetupMode.Mimic)
        {
            if (GeometryKind != RigEyeGeometryKind.Unspecified || SourceEntityId is not null || ParentEntityId is not null ||
                HelperEntityId is not null || ComponentId is not null || IslandIndex is not null ||
                DeformEntityId is not null || !SourceControlPointIds.IsEmpty || GlobeRadius is not null || !GlobalFrame.NearlyEquals(TransformMatrix.Identity, 0))
                throw new ArgumentException("Mimic setup stores only its descriptor inventory and review evidence; it cannot claim generated geometry or entity bindings.");
        }

        static void ValidateEntityReference(Guid? value, string parameterName, IReadOnlySet<Guid>? ownedEntityIds)
        {
            if (value is not { } id) return;
            RigContractRules.Identifier(id, parameterName);
            if (ownedEntityIds is not null && !ownedEntityIds.Contains(id))
                throw new ArgumentException("The eye setup references an entity that is not owned by the session.", parameterName);
        }
    }
}

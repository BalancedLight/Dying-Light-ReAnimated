using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.DL1.Assets.Materials;

namespace ReAnimated.DL1.Assets.Meshes;

public enum Dl1MeshDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public enum Dl1MeshBufferRole
{
    CompactMetadata,
    VariantDefinitions,
    ResolverData,
    SecondaryData,
    VertexData,
    IndexData,
    AuxiliaryGpuData,
    Unknown,
}

public enum Dl1MeshContainerLayout
{
    ThreeItemMetadataOnly,
    FiveItemSplitGpu,
    ExtendedSplitGpu,
}

public enum Dl1VertexSemantic
{
    Position,
    Normal,
    Tangent,
    TextureCoordinate,
    BlendIndices,
    BlendWeights,
    MorphDelta,
    Unknown,
}

public enum Dl1VertexElementFormat
{
    Float1,
    Float2,
    Float3,
    Float4,
    Half2,
    Half4,
    Byte4,
    Byte4Normalized,
    UShort2,
    UShort4,
    Unknown,
}

public enum Dl1MaterialBindingStatus
{
    Resolved,
    DatabaseNameDecoded,
    DeclaredSlotNameUnresolved,
    SyntheticSurfaceSlot,
}

public enum Dl1MorphPayloadStatus
{
    ChannelOnly,
    NodeLodBindingDecoded,
    VertexDeltasUnresolved,
    VertexDeltasDecoded,
}

public enum Dl1MorphDeltaEncoding
{
    SignedShort4Scale16384,
}

public sealed record Dl1MeshDiagnostic(
    string Code,
    Dl1MeshDiagnosticSeverity Severity,
    string Message,
    int? EntityIndex = null);

public sealed record Dl1MeshBufferReference(
    int ItemIndex,
    int ResourceItemSlot,
    short StorageGroupId,
    Dl1MeshBufferRole Role,
    int LogicalLength);

public sealed record Dl1VertexElement(
    Dl1VertexSemantic Semantic,
    int SemanticIndex,
    Dl1VertexElementFormat Format,
    int StreamIndex,
    int ByteOffset);

public sealed record Dl1VertexLayout(
    int Stride,
    IReadOnlyList<Dl1VertexElement> Elements);

public sealed record Dl1MeshBufferSlice(
    int BufferItemIndex,
    int ByteOffset,
    int ByteLength,
    int Stride);

public readonly record struct Dl1BoneIndex4(
    byte X,
    byte Y,
    byte Z,
    byte W);

public sealed record Dl1MeshVertex(
    System.Numerics.Vector3 Position,
    System.Numerics.Vector3 Normal,
    System.Numerics.Vector4 Tangent,
    System.Numerics.Vector2 TextureCoordinate0,
    System.Numerics.Vector2 TextureCoordinate1,
    System.Numerics.Vector4 Color,
    System.Numerics.Vector4 BlendWeights,
    Dl1BoneIndex4 LocalBlendIndices);

/// <summary>
/// Describes how a submesh binds its vertices to the decoded entity palette.
/// The raw vertex payload remains unchanged; consumers may materialize the
/// retail rigid encodings at their publication boundary.
/// DL1's named runtime identifies feature bit 0x200 as
/// <c>SKINNING_ONE_BONE</c>.
/// </summary>
public enum Dl1SkinBindingMode
{
    None,
    ExplicitVertexWeights,
    /// <summary>
    /// Every referenced vertex has an exact serialized zero weight vector,
    /// local Y/Z/W indexes are zero, and local X selects a valid palette
    /// entity. The implicit unit X weight is corpus-inferred from the
    /// validated Windows 1.55 retail corpus and is materialized only by
    /// consumers; decoded vertex bytes remain unchanged.
    /// </summary>
    RigidIndexedPalette,
    /// <summary>
    /// The vertex declaration contains neither blend weights nor blend
    /// indices while retaining a serialized palette. The named DL1 runtime
    /// does not enable its skinning feature for this declaration: it ignores
    /// the palette and submits the hierarchy element's world transform as an
    /// ordinary non-skinned draw. This mode requires a finite reconstructed
    /// entity world matrix and is previewable but never authorable skinning.
    /// </summary>
    StaticEntityTransformIgnoredPalette,
    UnresolvedMissingBlendStreams,
}

public sealed record Dl1MeshSubmesh(
    int Index,
    int FirstIndex,
    int IndexCount,
    int MaterialSlotIndex,
    IReadOnlyList<short> BonePaletteEntityIndexes)
{
    public Dl1SkinBindingMode SkinBindingMode { get; init; }
}

public sealed record Dl1MeshSurface(
    string Name,
    int EntityIndex,
    int LodIndex,
    int MaterialSlotIndex,
    Dl1VertexLayout VertexLayout,
    Dl1MeshBufferSlice VertexBuffer,
    Dl1MeshBufferSlice IndexBuffer,
    int VertexCount,
    int IndexCount,
    IReadOnlyList<Dl1MeshVertex> Vertices,
    IReadOnlyList<ushort> Indices,
    IReadOnlyList<Dl1MeshSubmesh> Submeshes);

public sealed record Dl1MaterialSlot(
    int Index,
    string DatabaseName,
    uint? RawDatabaseLoadValue,
    string? MaterialResourceName,
    Dl1MaterialBindingStatus BindingStatus)
{
    /// <summary>
    /// The material-database name declared by the mesh before the active
    /// retail skin replaced this slot. This remains null when the declared
    /// database row did not decode.
    /// </summary>
    public string? DeclaredDatabaseName { get; init; }

    /// <summary>
    /// Evidence-backed retail material and texture identities. This remains
    /// null when the named material is absent, ambiguous, or malformed.
    /// </summary>
    public Dl1ResolvedMaterial? ResolvedMaterial { get; init; }

    /// <summary>
    /// The material-database inventory row selected by the active retail skin.
    /// Null means the declared slot row remains active.
    /// </summary>
    public int? SkinReplacementDatabaseEntryIndex { get; init; }

    public string? AppliedSkinName { get; init; }
}

public sealed record Dl1MorphBinding(
    int EntityIndex,
    int LodIndex,
    int VertexCount,
    int DeltaByteStride,
    int PayloadByteOffset,
    Dl1MorphDeltaEncoding DeltaEncoding,
    IReadOnlyList<int> LocalTargetIndexes,
    IReadOnlyList<Dl1MorphPositionDeltaSet> PositionDeltaSets);

public sealed record Dl1MorphPositionDeltaSet(
    int LocalTargetIndex,
    IReadOnlyList<System.Numerics.Vector3> PositionDeltas);

public sealed record Dl1MorphTarget(
    int Index,
    string Name,
    IReadOnlyList<int> EntityIndexes,
    IReadOnlyList<Dl1MeshBufferSlice> DeltaBuffers,
    IReadOnlyList<Dl1MorphBinding> Bindings,
    Dl1MorphPayloadStatus PayloadStatus);

public sealed record Dl1MeshData(
    string ResourceName,
    Dl1MeshContainerLayout ContainerLayout,
    CompactMeshDocument Hierarchy,
    RigDefinition? Rig,
    IReadOnlyList<Dl1MeshBufferReference> Buffers,
    IReadOnlyList<Dl1MeshSurface> Surfaces,
    IReadOnlyList<Dl1MaterialSlot> MaterialSlots,
    IReadOnlyList<Dl1MorphTarget> MorphTargets,
    IReadOnlyList<string> VariantNames,
    IReadOnlyList<Dl1MeshDiagnostic> Diagnostics)
{
    /// <summary>
    /// Exact raw item 0/1/3/4 provenance when split GPU geometry was decoded.
    /// Metadata-only containers intentionally leave this null.
    /// </summary>
    public Dl1MeshGeometryProvenance? GeometryProvenance { get; init; }

    /// <summary>
    /// The exact retail skin selected during decode. DL1 falls back to
    /// "Default" when no explicit skin is requested.
    /// </summary>
    public string? AppliedSkinName { get; init; }

    /// <summary>
    /// Direct hierarchy entities hidden by the applied skin's serialized sign
    /// bit. Raw surface/random skin records remain outside this contract.
    /// </summary>
    public IReadOnlyList<int> SkinHiddenEntityIndexes
    {
        get;
        init;
    } = Array.Empty<int>();

    public bool IsStructurallyValid =>
        Hierarchy.IsStructurallyValid &&
        Diagnostics.All(static diagnostic =>
            diagnostic.Severity != Dl1MeshDiagnosticSeverity.Error);

    public bool HasDecodedGeometry => Surfaces.Count > 0;

    public bool HasDecodedMaterials => HasDecodedMaterialSlotNames;

    public bool HasDecodedMaterialSlotNames =>
        MaterialSlots.Any(static slot =>
            slot.BindingStatus is
                Dl1MaterialBindingStatus.DatabaseNameDecoded or
                Dl1MaterialBindingStatus.Resolved);

    public bool HasResolvedMaterialResources =>
        MaterialSlots.Any(static slot =>
            slot.BindingStatus == Dl1MaterialBindingStatus.Resolved);

    public bool HasDecodedMorphTargets => MorphTargets.Count > 0;

    public bool IsSkinned =>
        Surfaces.Any(static surface =>
            surface.Submeshes.Any(static submesh =>
                submesh.BonePaletteEntityIndexes.Count > 0));
}

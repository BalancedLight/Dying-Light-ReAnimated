using System.Numerics;

namespace ReAnimated.Codecs.CompactMesh;

public enum CompiledVertexFormat : byte
{
    Float3 = 2,
    Byte4 = 4,
    Half2 = 15,
    Half4 = 16,
    SignedNormalizedByte4 = 31,
}

public enum CompiledVertexSemantic : byte
{
    Position = 0,
    BlendWeights = 1,
    BlendIndices = 2,
    Normal = 3,
    TextureCoordinate = 5,
    Tangent = 6,
    Color = 10,
}

public sealed record CompiledMeshDecodeLimits
{
    public static CompiledMeshDecodeLimits Default { get; } = new();

    public int MaximumDeclarationGroups { get; init; } = 4_096;

    public int MaximumElementsPerDeclaration { get; init; } = 64;

    public int MaximumVerticesPerSurface { get; init; } = 16_000_000;

    public int MaximumIndicesPerSurface { get; init; } = 48_000_000;

    public int MaximumSubmeshesPerSurface { get; init; } = 65_535;

    public int MaximumMorphChannels { get; init; } = 65_535;

    public long MaximumDecodedMorphDeltaBytes { get; init; } =
        256L * 1024 * 1024;

    public int MaximumMaterialDatabaseEntries { get; init; } = 65_535;

    public int MaximumVariantNames { get; init; } = 65_535;

    public int MaximumSkinDefinitions { get; init; } = 65_535;

    public int MaximumSkinOverrides { get; init; } = 1_000_000;

    internal void Validate()
    {
        if (MaximumDeclarationGroups <= 0 ||
            MaximumElementsPerDeclaration <= 0 ||
            MaximumVerticesPerSurface <= 0 ||
            MaximumIndicesPerSurface <= 0 ||
            MaximumSubmeshesPerSurface <= 0 ||
            MaximumMorphChannels <= 0 ||
            MaximumDecodedMorphDeltaBytes <= 0 ||
            MaximumMaterialDatabaseEntries <= 0 ||
            MaximumVariantNames <= 0 ||
            MaximumSkinDefinitions <= 0 ||
            MaximumSkinOverrides <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CompiledMeshDecodeLimits),
                "Compiled-mesh decode limits must be positive.");
        }
    }
}

public sealed record CompiledVertexElement(
    byte RawFormat,
    byte RawSemantic,
    byte Channel,
    int ByteOffset,
    int ByteSize)
{
    public CompiledVertexFormat? Format =>
        Enum.IsDefined(typeof(CompiledVertexFormat), RawFormat)
            ? (CompiledVertexFormat)RawFormat
            : null;

    public CompiledVertexSemantic? Semantic =>
        Enum.IsDefined(typeof(CompiledVertexSemantic), RawSemantic)
            ? (CompiledVertexSemantic)RawSemantic
            : null;
}

public sealed record CompiledVertexLayout(
    int Index,
    int Stride,
    IReadOnlyList<CompiledVertexElement> Elements);

public readonly record struct CompiledBoneIndex4(
    byte X,
    byte Y,
    byte Z,
    byte W);

public sealed record CompiledVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector4 Tangent,
    Vector2 TextureCoordinate0,
    Vector2 TextureCoordinate1,
    Vector4 Color,
    Vector4 BlendWeights,
    CompiledBoneIndex4 LocalBlendIndices);

public sealed record CompiledMeshSubmesh(
    int Index,
    int FirstIndex,
    int IndexCount,
    ushort? DeclaredMaterialSlotIndex,
    IReadOnlyList<short> BonePaletteEntityIndexes);

public sealed record CompiledMeshSurface(
    int EntityIndex,
    string Name,
    int LodIndex,
    int DeclarationGroupIndex,
    int VertexByteOffset,
    int IndexByteOffset,
    CompiledVertexLayout VertexLayout,
    IReadOnlyList<CompiledVertex> Vertices,
    IReadOnlyList<ushort> Indices,
    IReadOnlyList<CompiledMeshSubmesh> Submeshes);

public sealed record CompiledMorphChannel(
    int Index,
    string Name);

public enum CompiledMorphDeltaFormat
{
    SignedShort4Scale16384,
}

public sealed record CompiledMorphTargetDeltas(
    int LocalTargetIndex,
    ushort MorphChannelIndex,
    IReadOnlyList<Vector3> PositionDeltas);

public sealed record CompiledNodeMorphBinding(
    int EntityIndex,
    int LodIndex,
    int VertexCount,
    int DeltaByteStride,
    int PayloadByteOffset,
    CompiledMorphDeltaFormat DeltaFormat,
    IReadOnlyList<ushort> MorphChannelIndexes,
    IReadOnlyList<CompiledMorphTargetDeltas> TargetDeltas);

/// <summary>
/// One serialized DL1 compact-mesh material-database row. The raw load value
/// is retained because the runtime forwards it to the material manager, but
/// its narrower flag/version semantics are not yet proven.
/// </summary>
public sealed record CompiledMaterialDatabaseEntry(
    int Index,
    string DatabaseName,
    uint RawLoadValue);

/// <summary>
/// The compact-mesh material-database holder. Declared slots occupy the first
/// <see cref="DeclaredSlotCount"/> rows; additional rows remain database
/// inventory and are not reported as mesh slots.
/// </summary>
public sealed record CompiledMaterialDatabase(
    int DeclaredSlotCount,
    int DeclaredEntryCount,
    IReadOnlyList<CompiledMaterialDatabaseEntry> Entries)
{
    public static CompiledMaterialDatabase Empty { get; } =
        new(0, 0, Array.Empty<CompiledMaterialDatabaseEntry>());

    public bool HasCompleteSlotNames =>
        DeclaredSlotCount == 0 ||
        Entries.Count >= DeclaredSlotCount &&
        Entries
            .Take(DeclaredSlotCount)
            .Select(static entry => entry.Index)
            .SequenceEqual(Enumerable.Range(0, DeclaredSlotCount));
}

/// <summary>
/// One target-slot to material-database-inventory substitution serialized by a
/// compact-mesh skin definition.
/// </summary>
public sealed record CompiledMeshSkinMaterialOverride(
    int TargetMaterialSlotIndex,
    int ReplacementMaterialDatabaseEntryIndex);

/// <summary>
/// One compact hierarchy entity flag override serialized by a mesh skin.
/// The runtime uses the low 14 bits as the entity index. Stock DL1 1.55
/// Default/Unturned controls establish that the sign bit hides/enables the
/// mutually exclusive entity respectively; bit 14 is retained independently.
/// </summary>
public sealed record CompiledMeshSkinEntityOverride(
    int EntityIndex,
    ushort RawValue)
{
    public bool IsHidden => (RawValue & 0x8000) != 0;

    public bool HasRuntimeFlag4000 => (RawValue & 0x4000) != 0;
}

/// <summary>
/// One exact 48-byte DL1 compact-mesh skin descriptor. Surface/random records
/// are not interpreted yet, but their bounded counts are retained so consumers
/// do not mistake the decoded subset for complete runtime skin emulation.
/// </summary>
public sealed record CompiledMeshSkinDefinition(
    int Index,
    string Name,
    ushort RawFeatures,
    IReadOnlyList<CompiledMeshSkinMaterialOverride> MaterialOverrides,
    IReadOnlyList<CompiledMeshSkinEntityOverride> EntityOverrides,
    int SurfaceOverrideCount,
    int RandomizedChildCount);

public sealed record CompiledMeshGeometryDocument(
    IReadOnlyList<CompiledVertexLayout> VertexLayouts,
    IReadOnlyList<CompiledMeshSurface> Surfaces,
    IReadOnlyList<string> VariantNames,
    CompiledMaterialDatabase MaterialDatabase,
    IReadOnlyList<CompiledMorphChannel> MorphChannels,
    IReadOnlyList<CompiledNodeMorphBinding> MorphBindings,
    IReadOnlyList<CompactMeshDiagnostic> Diagnostics)
{
    public IReadOnlyList<CompiledMeshSkinDefinition> SkinDefinitions
    {
        get;
        init;
    } = Array.Empty<CompiledMeshSkinDefinition>();

    public int DeclaredMaterialSlotCount =>
        MaterialDatabase.DeclaredSlotCount;

    public int VertexCount =>
        Surfaces.Sum(static surface => surface.Vertices.Count);

    public int IndexCount =>
        Surfaces.Sum(static surface => surface.Indices.Count);
}

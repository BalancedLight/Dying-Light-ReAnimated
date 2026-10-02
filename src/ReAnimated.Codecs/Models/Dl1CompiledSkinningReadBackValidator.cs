using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.CompactMesh;

namespace ReAnimated.Codecs.Models;

/// <summary>A prepared source-MSH vertex used for compiled skinning read-back.</summary>
public sealed record Dl1PreparedSkinVertexExpectation(
    Vector3 Position,
    ImmutableArray<Dl1PreparedSkinInfluenceExpectation> Influences)
{
    public Vector3? Normal { get; init; }
    public Vector2? TextureCoordinate0 { get; init; }
}

/// <summary>A source palette influence after the writer's 15-bit weight quantization.</summary>
public sealed record Dl1PreparedSkinInfluenceExpectation(
    int EntityIndex,
    double Weight);

/// <summary>A source-MSH subset emitted for one prepared geometry LOD.</summary>
public sealed record Dl1PreparedSkinSubsetExpectation(
    ImmutableArray<int> PaletteEntityIndexes,
    int IndexCount)
{
    public ushort? DeclaredMaterialSlotIndex { get; init; }
    public int? FirstIndex { get; init; }

    /// <summary>Exact source material reference; compiler slot numbering may be rebuilt per draw format.</summary>
    public string? DeclaredMaterialReference { get; init; }
}

/// <summary>
/// Expected compact geometry projected from the source writer's prepared MSH,
/// not from the importer surface palette. Each LOD retains all emitted draw
/// subsets, their material slots, and their independent physical skin palettes.
/// </summary>
public sealed record Dl1PreparedSkinningSurfaceExpectation(
    string NodeName,
    int LodIndex,
    bool IsSkinned,
    int VertexCount,
    ImmutableArray<int> UsedVertexIndexes,
    ImmutableArray<Dl1PreparedSkinSubsetExpectation> Subsets,
    ImmutableArray<Dl1PreparedSkinVertexExpectation> Vertices)
{
    public ImmutableArray<uint> IndexBuffer { get; init; } = [];
}

public sealed record Dl1CompiledSkinSubsetMaterialReadBack(
    string NodeName, int LodIndex, int SubsetIndex,
    ushort? SourceSlotIndex, ushort? CompiledSlotIndex,
    string? SourceReference, string? CompiledReference,
    uint? RawCompiledLoadValue, bool MatchedByReference);

public sealed record Dl1CompiledSkinVertexCorrespondence(string NodeName, int LodIndex, int SourceVertexIndex, int CompiledVertexIndex);

public sealed record Dl1CompiledSkinningReadBackEvidence(
    string ContractFingerprint,
    int VerifiedSurfaceCount,
    int VerifiedSubsetCount,
    int VerifiedVertexCount,
    int VerifiedInfluenceCount)
{
    public ImmutableArray<Dl1CompiledSkinSubsetMaterialReadBack> MaterialSlots { get; init; } = [];
    public ImmutableArray<Dl1CompiledSkinVertexCorrespondence> VertexCorrespondence { get; init; } = [];
    public bool TriangleAssociationsVerified { get; init; }
}

/// <summary>
/// Compares decoded official compiler palettes and blend weights with the
/// quantized geometry the source-MSH writer actually prepared. The compact
/// output does not preserve source vertex indexes, so vertices are matched by
/// the decoded position signature and then by semantic entity weights.
/// </summary>
public static class Dl1CompiledSkinningReadBackValidator
{
    private const double WeightTolerance = (1.0 / 255.0) + (1.0 / 32767.0) + 1e-7;
    private const double NormalizedWeightSumTolerance = (4.0 / 255.0) + 1e-7;
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true,
    };

    public static Dl1CompiledSkinningReadBackEvidence Validate(
        ImmutableArray<Dl1PreparedSkinningSurfaceExpectation> expected,
        CompiledMeshGeometryDocument compiled,
        int compiledEntityCount,
        ImmutableArray<Dl1PreparedMorphSurfaceExpectation> morphExpectations = default)
    {
        if (expected.IsDefault)
            throw new ArgumentException("Prepared skinning expectations are required.", nameof(expected));
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(compiledEntityCount);

        var expectedByName = new Dictionary<string, Dictionary<int, Dl1PreparedSkinningSurfaceExpectation>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Dl1PreparedSkinningSurfaceExpectation surface in expected)
        {
            if (string.IsNullOrWhiteSpace(surface.NodeName) || surface.LodIndex < 0 ||
                surface.VertexCount < 0 || surface.Vertices.Length != surface.VertexCount ||
                surface.Subsets.IsDefaultOrEmpty)
            {
                throw new InvalidDataException("Prepared skinning surface identities or dimensions are invalid.");
            }

            if (!expectedByName.TryGetValue(surface.NodeName, out Dictionary<int, Dl1PreparedSkinningSurfaceExpectation>? byLod))
                expectedByName.Add(surface.NodeName, byLod = []);
            if (!byLod.TryAdd(surface.LodIndex, surface))
                throw new InvalidDataException("Prepared skinning surface identities are duplicated.");

            ValidateExpectedSurface(surface, compiledEntityCount);
        }

        if (compiled.Surfaces.Count != expected.Length)
        {
            throw new InvalidDataException(
                $"The official compiler emitted {compiled.Surfaces.Count} geometry surface/LOD rows; " +
                $"the prepared MSH contract has {expected.Length}. No model RPack was published.");
        }

        var materialSlots = ImmutableArray.CreateBuilder<Dl1CompiledSkinSubsetMaterialReadBack>();
        var correspondence = ImmutableArray.CreateBuilder<Dl1CompiledSkinVertexCorrespondence>();
        var matched = new HashSet<(string Name, int Lod)>(SurfaceKeyComparer.Instance);
        int subsetCount = 0;
        int vertexCount = 0;
        int influenceCount = 0;
        foreach (CompiledMeshSurface actual in compiled.Surfaces)
        {
            if ((uint)actual.EntityIndex >= (uint)compiledEntityCount ||
                !expectedByName.TryGetValue(actual.Name, out Dictionary<int, Dl1PreparedSkinningSurfaceExpectation>? byLod) ||
                !byLod.TryGetValue(actual.LodIndex, out Dl1PreparedSkinningSurfaceExpectation? source) ||
                !matched.Add((actual.Name, actual.LodIndex)))
            {
                throw new InvalidDataException(
                    $"The official compiler emitted an unexpected or duplicate geometry surface/LOD '{actual.Name}'/{actual.LodIndex}. " +
                    "No model RPack was published.");
            }

            ValidateSurface(source, actual, compiled, compiledEntityCount, materialSlots, correspondence, morphExpectations, ref subsetCount, ref vertexCount, ref influenceCount);
        }

        if (matched.Count != expected.Length)
            throw new InvalidDataException("The official compiler omitted a prepared geometry surface or LOD. No model RPack was published.");

        byte[] fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(expected, FingerprintJsonOptions);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(fingerprintBytes));
        return new(fingerprint, expected.Length, subsetCount, vertexCount, influenceCount) { MaterialSlots = materialSlots.ToImmutable(), VertexCorrespondence = correspondence.ToImmutable(), TriangleAssociationsVerified = expected.All(s => !s.IndexBuffer.IsDefaultOrEmpty) };
    }

    private static void ValidateExpectedSurface(
        Dl1PreparedSkinningSurfaceExpectation surface,
        int compiledEntityCount)
    {
        if (surface.IsSkinned && surface.Subsets.Any(static subset => subset.PaletteEntityIndexes.IsEmpty))
            throw new InvalidDataException($"Prepared skinned surface '{surface.NodeName}' contains an empty subset palette.");

        foreach (int vertexIndex in surface.UsedVertexIndexes)
        {
            if ((uint)vertexIndex >= (uint)surface.Vertices.Length)
                throw new InvalidDataException($"Prepared surface '{surface.NodeName}' references an out-of-range source vertex.");
        }

        foreach (Dl1PreparedSkinSubsetExpectation subset in surface.Subsets)
        {
            if (subset.IndexCount <= 0 ||
                subset.PaletteEntityIndexes.Any(index => index < 0 || index >= compiledEntityCount))
            {
                throw new InvalidDataException($"Prepared surface '{surface.NodeName}' contains an invalid subset or palette entry.");
            }
        }

        foreach (Dl1PreparedSkinVertexExpectation vertex in surface.Vertices)
        {
            if (!IsFinite(vertex.Position) ||
                vertex.Influences.IsDefault ||
                vertex.Influences.Any(influence => influence.EntityIndex < 0 || influence.EntityIndex >= compiledEntityCount ||
                    !double.IsFinite(influence.Weight) || influence.Weight <= 0.0 || influence.Weight > 1.0))
            {
                throw new InvalidDataException($"Prepared surface '{surface.NodeName}' contains invalid vertex skin expectations.");
            }

            double sum = vertex.Influences.Sum(static influence => influence.Weight);
            if (surface.IsSkinned && Math.Abs(sum - 1.0) > 1e-9 ||
                !surface.IsSkinned && vertex.Influences.Length != 0)
            {
                throw new InvalidDataException($"Prepared surface '{surface.NodeName}' contains non-normalized or unexpected skin influences.");
            }
        }
    }

    private static void ValidateSurface(
        Dl1PreparedSkinningSurfaceExpectation expected,
        CompiledMeshSurface actual,
        CompiledMeshGeometryDocument geometry,
        int compiledEntityCount,
        ImmutableArray<Dl1CompiledSkinSubsetMaterialReadBack>.Builder materialSlots,
        ImmutableArray<Dl1CompiledSkinVertexCorrespondence>.Builder correspondence,
        ImmutableArray<Dl1PreparedMorphSurfaceExpectation> morphExpectations,
        ref int subsetCount,
        ref int vertexCount,
        ref int influenceCount)
    {
        if (actual.Vertices.Count != expected.VertexCount || actual.Submeshes.Count != expected.Subsets.Length)
        {
            throw new InvalidDataException(
                $"The official compiler changed vertex or subset counts for '{actual.Name}' LOD {actual.LodIndex}. " +
                "No model RPack was published.");
        }

        CompiledVertexElement positionElement = RequireElement(actual, CompiledVertexSemantic.Position, channel: 0);
        if (positionElement.Format is not (CompiledVertexFormat.Float3 or CompiledVertexFormat.Half4) ||
            positionElement.Channel != 0)
        {
            throw new InvalidDataException($"Compiled surface '{actual.Name}' uses an unsupported position layout for skinning read-back.");
        }

        CompiledVertexElement? weightsElement = FindElement(actual, CompiledVertexSemantic.BlendWeights, channel: 0);
        CompiledVertexElement? indicesElement = FindElement(actual, CompiledVertexSemantic.BlendIndices, channel: 0);
        if (expected.IsSkinned &&
            (weightsElement?.Format != CompiledVertexFormat.Byte4 || indicesElement?.Format != CompiledVertexFormat.Byte4))
        {
            throw new InvalidDataException($"Compiled skinned surface '{actual.Name}' lacks decodable blend weights or indices.");
        }

        if (!expected.IsSkinned && (weightsElement is not null || indicesElement is not null))
        {
            foreach (CompiledVertex vertex in actual.Vertices)
            {
                if (HasPositiveWeight(vertex.BlendWeights))
                    throw new InvalidDataException($"Compiled unskinned surface '{actual.Name}' contains active blend weights.");
            }
        }

        if (actual.Indices.Count != expected.Subsets.Sum(static subset => subset.IndexCount))
        {
            throw new InvalidDataException($"The official compiler changed the total index count for '{actual.Name}' LOD {actual.LodIndex}.");
        }

        var actualInfluencesByVertex = new Dictionary<int, Dictionary<int, double>>();
        for (int submeshIndex = 0; submeshIndex < actual.Submeshes.Count; submeshIndex++)
        {
            CompiledMeshSubmesh submesh = actual.Submeshes[submeshIndex];
            Dl1PreparedSkinSubsetExpectation expectedSubset = expected.Subsets[submeshIndex];
            if (submesh.Index != submeshIndex || submesh.IndexCount != expectedSubset.IndexCount || submesh.FirstIndex < 0 ||
                submesh.FirstIndex > actual.Indices.Count - submesh.IndexCount)
            {
                throw new InvalidDataException($"The official compiler changed subset/material/index ranges for '{actual.Name}'.");
            }

            materialSlots.Add(ValidateSubsetMaterial(expectedSubset, submesh, geometry.MaterialDatabase, actual.Name, actual.LodIndex));
            ValidatePalette(actual.Name, submesh.BonePaletteEntityIndexes, expectedSubset.PaletteEntityIndexes, compiledEntityCount);
            subsetCount++;
            for (int offset = submesh.FirstIndex; offset < submesh.FirstIndex + submesh.IndexCount; offset++)
            {
                int vertexIndex = actual.Indices[offset];
                if ((uint)vertexIndex >= (uint)actual.Vertices.Count)
                    throw new InvalidDataException($"Compiled surface '{actual.Name}' subset references an out-of-range vertex.");

                if (!expected.IsSkinned)
                    continue;

                Dictionary<int, double> resolved = ResolveInfluences(
                    actual.Vertices[vertexIndex],
                    submesh.BonePaletteEntityIndexes,
                    actual.Name);
                if (actualInfluencesByVertex.TryGetValue(vertexIndex, out Dictionary<int, double>? prior))
                {
                    if (!InfluencesEquivalent(prior, resolved))
                        throw new InvalidDataException($"Compiled shared vertex {vertexIndex} in '{actual.Name}' resolves to different entity influences across subsets.");
                }
                else
                {
                    actualInfluencesByVertex.Add(vertexIndex, resolved);
                }
            }
        }

        int[] expectedUsed = expected.IsSkinned
            ? expected.UsedVertexIndexes.Distinct().Order().ToArray()
            : Enumerable.Range(0, expected.VertexCount).ToArray();
        int[] actualUsed = actualInfluencesByVertex.Keys.Order().ToArray();
        if (expected.IsSkinned && actualUsed.Length != expectedUsed.Length)
        {
            throw new InvalidDataException($"The official compiler changed the number of referenced skinned vertices for '{actual.Name}'.");
        }

        var expectedBySignature = expectedUsed.GroupBy(index => ExpectedSignature(expected.Vertices[index], positionElement, actual.Name))
            .ToDictionary(group => group.Key, group => group.ToArray());
        var actualBySignature = (expected.IsSkinned ? actualUsed : Enumerable.Range(0, actual.Vertices.Count))
            .GroupBy(index => ActualSignature(actual.Vertices[index])).ToDictionary(group => group.Key, group => group.ToArray());
        if (expectedBySignature.Count != actualBySignature.Count)
            throw new InvalidDataException($"The official compiler changed the prepared vertex position inventory for '{actual.Name}'.");
        Dl1PreparedMorphSurfaceExpectation? sourceMorph = morphExpectations.IsDefault ? null : morphExpectations.SingleOrDefault(
            item => item.NodeName == expected.NodeName && item.LodIndex == expected.LodIndex);
        CompiledNodeMorphBinding? compiledMorph = geometry.MorphBindings.SingleOrDefault(
            item => item.EntityIndex == actual.EntityIndex && item.LodIndex == actual.LodIndex);
        bool Compatible(int sourceIndex, int compiledIndex)
        {
            var source = expected.Vertices[sourceIndex];
            var vertex = actual.Vertices[compiledIndex];
            if (expected.IsSkinned && !InfluencesEquivalent(source.Influences, actualInfluencesByVertex[compiledIndex],
                FindElement(actual, CompiledVertexSemantic.BlendWeights, 0)?.Format == CompiledVertexFormat.Byte4 && vertex.BlendWeights != Vector4.Zero))
                return false;
            if (!AttributesCompatible(source, vertex, actual.VertexLayout))
                return false;
            if (sourceMorph is not null && !sourceMorph.MorphTargets.IsEmpty)
            {
                if (compiledMorph is null) return false;
                foreach (Dl1PreparedMorphTargetExpectation target in sourceMorph.MorphTargets)
                {
                    CompiledMorphTargetDeltas? match = compiledMorph.TargetDeltas.SingleOrDefault(
                        row => geometry.MorphChannels[row.MorphChannelIndex].Name == target.Name);
                    if (match is null || sourceIndex >= target.PositionDeltas.Length || compiledIndex >= match.PositionDeltas.Count)
                        return false;
                    Vector3 value = match.PositionDeltas[compiledIndex];
                    var wanted = target.PositionDeltas[sourceIndex];
                    try
                    {
                        Dl1OfficialModelCompiler.ValidateHalfMorphComponent(value.X, wanted.X, target.Name, compiledIndex, "X");
                        Dl1OfficialModelCompiler.ValidateHalfMorphComponent(value.Y, wanted.Y, target.Name, compiledIndex, "Y");
                        Dl1OfficialModelCompiler.ValidateHalfMorphComponent(value.Z, wanted.Z, target.Name, compiledIndex, "Z");
                    }
                    catch (InvalidDataException) { return false; }
                }
            }
            return true;
        }
        foreach ((VertexSignature signature, int[] sourceGroup) in expectedBySignature)
        {
            if (!actualBySignature.TryGetValue(signature, out int[]? compiledGroup) || sourceGroup.Length != compiledGroup.Length)
                throw new InvalidDataException($"The official compiler changed prepared vertex identities for '{actual.Name}'.");
            bool mixed = expected.IsSkinned && sourceGroup.Skip(1).Any(index =>
                !InfluencesEquivalent(expected.Vertices[sourceGroup[0]].Influences, ToInfluenceMap(expected.Vertices[index].Influences)));
            if (mixed && expected.IndexBuffer.IsDefaultOrEmpty)
                throw new InvalidDataException($"Compiled vertex order is ambiguous for duplicate-position rows with different skinning in '{actual.Name}'. No source triangle association was supplied.");
            int[] mapping = PerfectMatch(sourceGroup.Length, compiledGroup.Length,
                (source, compiled) => Compatible(sourceGroup[source], compiledGroup[compiled]));
            if (mapping.Any(index => index < 0))
                throw new InvalidDataException($"The official compiler changed entity-indexed skin weights or attributed vertex records for '{actual.Name}'. No model RPack was published.");
            for (int i = 0; i < mapping.Length; i++)
                correspondence.Add(new(actual.Name, actual.LodIndex, sourceGroup[i], compiledGroup[mapping[i]]));
            influenceCount += sourceGroup.Sum(index => expected.Vertices[index].Influences.Length);
        }
        if (!expected.IndexBuffer.IsDefaultOrEmpty)
            ValidateTriangleAssociations(expected, actual, positionElement, Compatible);

        vertexCount += expectedUsed.Length;
    }

    private static int[] PerfectMatch(int sourceCount, int compiledCount, Func<int, int, bool> compatible)
    {
        int[] owner = Enumerable.Repeat(-1, compiledCount).ToArray();
        bool Augment(int source, bool[] seen)
        {
            for (int compiled = 0; compiled < compiledCount; compiled++)
            {
                if (seen[compiled] || !compatible(source, compiled)) continue;
                seen[compiled] = true;
                if (owner[compiled] < 0 || Augment(owner[compiled], seen))
                {
                    owner[compiled] = source;
                    return true;
                }
            }
            return false;
        }
        for (int source = 0; source < sourceCount; source++)
            if (!Augment(source, new bool[compiledCount])) return Enumerable.Repeat(-1, sourceCount).ToArray();
        int[] result = Enumerable.Repeat(-1, sourceCount).ToArray();
        for (int compiled = 0; compiled < owner.Length; compiled++)
            if (owner[compiled] >= 0) result[owner[compiled]] = compiled;
        return result;
    }

    private static bool AttributesCompatible(Dl1PreparedSkinVertexExpectation source, CompiledVertex actual,
        CompiledVertexLayout layout)
    {
        if (source.TextureCoordinate0 is { } uv)
        {
            var element = layout.Elements.SingleOrDefault(e => e.Semantic == CompiledVertexSemantic.TextureCoordinate && e.Channel == 0);
            if (element?.Format == CompiledVertexFormat.Half2)
            {
                try
                {
                    Dl1OfficialModelCompiler.ValidateHalfMorphComponent(actual.TextureCoordinate0.X, uv.X, "UV", 0, "U");
                    Dl1OfficialModelCompiler.ValidateHalfMorphComponent(actual.TextureCoordinate0.Y, uv.Y, "UV", 0, "V");
                }
                catch (InvalidDataException) { return false; }
            }
            else if (element?.Format != CompiledVertexFormat.Float3 || actual.TextureCoordinate0 != uv) return false;
        }
        if (source.Normal is { } normal)
        {
            // Quantized native normals have a signed-byte step. The existing
            // shading read-back remains authoritative for layout and tangent data.
            const float signedByteStep = 1f / 127f + 1e-6f;
            if (Vector3.Distance(normal, actual.Normal) > 2 * signedByteStep) return false;
        }
        return true;
    }

    private static void ValidateTriangleAssociations(Dl1PreparedSkinningSurfaceExpectation expected,
        CompiledMeshSurface actual, CompiledVertexElement positionElement, Func<int, int, bool> compatible)
    {
        if (expected.IndexBuffer.Length % 3 != 0 || expected.IndexBuffer.Any(index => index >= expected.VertexCount))
            throw new InvalidDataException("Prepared triangle indices are malformed.");
        int sourceCursor = 0;
        for (int subsetIndex = 0; subsetIndex < expected.Subsets.Length; subsetIndex++)
        {
            var sourceSubset = expected.Subsets[subsetIndex];
            var compiledSubset = actual.Submeshes[subsetIndex];
            int first = sourceSubset.FirstIndex ?? sourceCursor;
            int count = sourceSubset.IndexCount;
            sourceCursor = checked(first + count);
            if (first < 0 || count % 3 != 0 || first > expected.IndexBuffer.Length - count)
                throw new InvalidDataException("Prepared triangle subset ranges are malformed.");
            var sourceTriangles = Enumerable.Range(0, count / 3).Select(t =>
                Enumerable.Range(0, 3).Select(c => checked((int)expected.IndexBuffer[first + t * 3 + c])).ToArray()).ToArray();
            var compiledTriangles = Enumerable.Range(0, count / 3).Select(t =>
                Enumerable.Range(0, 3).Select(c => (int)actual.Indices[compiledSubset.FirstIndex + t * 3 + c]).ToArray()).ToArray();
            var sourceGroups = sourceTriangles.GroupBy(triangle => TriangleKey(triangle.Select(i =>
                ExpectedSignature(expected.Vertices[i], positionElement, actual.Name).Position).ToArray()))
                .ToDictionary(g => g.Key, g => g.ToArray());
            var compiledGroups = compiledTriangles.GroupBy(triangle => TriangleKey(triangle.Select(i =>
                actual.Vertices[i].Position).ToArray())).ToDictionary(g => g.Key, g => g.ToArray());
            if (sourceGroups.Count != compiledGroups.Count)
                throw new InvalidDataException($"The official compiler changed triangle associations for '{actual.Name}'.");
            foreach (var (key, sourceGroup) in sourceGroups)
            {
                if (!compiledGroups.TryGetValue(key, out var compiledGroup) || sourceGroup.Length != compiledGroup.Length)
                    throw new InvalidDataException($"The official compiler changed triangle associations for '{actual.Name}'.");
                bool TriangleCompatible(int source, int compiled) => Enumerable.Range(0, 3).Any(rotation =>
                    Enumerable.Range(0, 3).All(c =>
                        ExpectedSignature(expected.Vertices[sourceGroup[source][c]], positionElement, actual.Name) ==
                        ActualSignature(actual.Vertices[compiledGroup[compiled][(c + rotation) % 3]]) &&
                        compatible(sourceGroup[source][c], compiledGroup[compiled][(c + rotation) % 3])));
                if (PerfectMatch(sourceGroup.Length, compiledGroup.Length, TriangleCompatible).Any(index => index < 0))
                    throw new InvalidDataException($"The official compiler changed attributed triangle corners for '{actual.Name}'.");
            }
        }
    }

    private static (Vector3, Vector3, Vector3) TriangleKey(Vector3[] points)
    {
        int Compare(Vector3 a, Vector3 b)
        {
            int x = a.X.CompareTo(b.X); if (x != 0) return x;
            int y = a.Y.CompareTo(b.Y); return y != 0 ? y : a.Z.CompareTo(b.Z);
        }
        int best = 0;
        for (int rotation = 1; rotation < 3; rotation++)
            for (int corner = 0; corner < 3; corner++)
            {
                int comparison = Compare(points[(rotation + corner) % 3], points[(best + corner) % 3]);
                if (comparison < 0) { best = rotation; break; }
                if (comparison > 0) break;
            }
        return (points[best], points[(best + 1) % 3], points[(best + 2) % 3]);
    }

    private static Dl1CompiledSkinSubsetMaterialReadBack ValidateSubsetMaterial(
        Dl1PreparedSkinSubsetExpectation expected, CompiledMeshSubmesh actual,
        CompiledMaterialDatabase database, string nodeName, int lodIndex)
    {
        if (expected.DeclaredMaterialReference is { } source && string.IsNullOrWhiteSpace(source))
            throw new InvalidDataException("A prepared material reference cannot be empty.");
        CompiledMaterialDatabaseEntry? entry = null;
        if (actual.DeclaredMaterialSlotIndex is { } slot && database.DeclaredSlotCount > 0 && database.HasCompleteSlotNames)
        {
            if (slot >= database.DeclaredSlotCount)
                throw new InvalidDataException($"Compiled subset material slot {slot} is outside its declared table.");
            CompiledMaterialDatabaseEntry[] matches = database.Entries.Where(row => row.Index == slot).ToArray();
            if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].DatabaseName))
                throw new InvalidDataException($"Compiled subset material slot {slot} has no unique material identity.");
            entry = matches[0];
        }
        bool named = expected.DeclaredMaterialReference is not null && entry is not null;
        if (named)
        {
            string sourceReference = expected.DeclaredMaterialReference!.Replace('\\', '/');
            string compiledReference = entry!.DatabaseName.Replace('\\', '/');
            if (!string.Equals(sourceReference, compiledReference, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The official compiler changed subset material '{sourceReference}' to '{compiledReference}' for '{nodeName}' LOD {lodIndex}.");
        }
        else if (expected.DeclaredMaterialSlotIndex is { } sourceSlot && actual.DeclaredMaterialSlotIndex != sourceSlot ||
                 expected.DeclaredMaterialReference is not null && expected.DeclaredMaterialSlotIndex is null)
        {
            throw new InvalidDataException($"The official compiler changed or omitted subset material {expected.DeclaredMaterialSlotIndex} for '{nodeName}' LOD {lodIndex}; material identity was unavailable.");
        }
        return new(nodeName, lodIndex, actual.Index,
            expected.DeclaredMaterialSlotIndex, actual.DeclaredMaterialSlotIndex,
            expected.DeclaredMaterialReference, entry?.DatabaseName, entry?.RawLoadValue, named);
    }

    private static void ValidatePalette(
        string surfaceName,
        IReadOnlyList<short> actual,
        ImmutableArray<int> expected,
        int compiledEntityCount)
    {
        if (actual.Any(index => index < 0 || index >= compiledEntityCount))
            throw new InvalidDataException($"Compiled surface '{surfaceName}' has a palette entity outside the decoded hierarchy.");
        if (!actual.Select(static index => (int)index).Order().SequenceEqual(expected.Order()))
            throw new InvalidDataException($"The official compiler changed the entity palette for '{surfaceName}'. No model RPack was published.");
    }

    private static Dictionary<int, double> ResolveInfluences(
        CompiledVertex vertex,
        IReadOnlyList<short> palette,
        string surfaceName)
    {
        Span<byte> localIndexes = [vertex.LocalBlendIndices.X, vertex.LocalBlendIndices.Y,
            vertex.LocalBlendIndices.Z, vertex.LocalBlendIndices.W];
        Span<float> weights = [vertex.BlendWeights.X, vertex.BlendWeights.Y,
            vertex.BlendWeights.Z, vertex.BlendWeights.W];

        // The exact zero-weight/X-only pattern is the corpus-validated DL1
        // RigidIndexedPalette binding mode from Dl1SkinBindingPolicy. That
        // helper lives in DL1.Assets, which references Codecs, so this assembly
        // cannot call it without a dependency cycle. Mirror only its exact
        // predicate; the preview adapter treats X as an implicit full influence.
        // Preserve the raw zero bytes and do not normalize other rows.
        if (vertex.BlendWeights == Vector4.Zero)
        {
            if (localIndexes[0] >= palette.Count ||
                localIndexes[1] != 0 || localIndexes[2] != 0 || localIndexes[3] != 0)
            {
                throw new InvalidDataException(
                    $"Compiled surface '{surfaceName}' has a zero-weight row outside the supported rigid indexed-palette pattern.");
            }

            int rigidEntity = palette[localIndexes[0]];
            if (rigidEntity < 0)
                throw new InvalidDataException($"Compiled surface '{surfaceName}' has a rigid palette entry outside the decoded hierarchy.");
            return new Dictionary<int, double> { [rigidEntity] = 1.0 };
        }

        double total = 0.0;
        var resolved = new Dictionary<int, double>();
        for (int lane = 0; lane < 4; lane++)
        {
            double weight = weights[lane];
            if (!double.IsFinite(weight) || weight < 0.0 || weight > 1.0)
                throw new InvalidDataException($"Compiled surface '{surfaceName}' has a non-finite or out-of-range blend weight.");
            total += weight;
            byte localIndex = localIndexes[lane];
            if (localIndex >= palette.Count)
                throw new InvalidDataException($"Compiled surface '{surfaceName}' has a local blend index outside its subset palette.");
            if (weight <= 0.0)
                continue;
            int entityIndex = palette[localIndex];
            if (entityIndex < 0)
                throw new InvalidDataException($"Compiled surface '{surfaceName}' has an active palette entry outside the decoded hierarchy.");
            resolved[entityIndex] = resolved.GetValueOrDefault(entityIndex) + weight;
        }

        if (resolved.Count == 0 || Math.Abs(total - 1.0) > NormalizedWeightSumTolerance)
            throw new InvalidDataException($"Compiled surface '{surfaceName}' has non-normalized or empty active blend weights.");
        return resolved;
    }

    private static bool InfluencesEquivalent(
        ImmutableArray<Dl1PreparedSkinInfluenceExpectation> expected,
        Dictionary<int, double> actual, bool allowByteZeroLanes = false) => InfluencesEquivalent(ToInfluenceMap(expected), actual, allowByteZeroLanes);

    private static Dictionary<int, double> ToInfluenceMap(
        ImmutableArray<Dl1PreparedSkinInfluenceExpectation> influences)
    {
        var result = new Dictionary<int, double>();
        foreach (Dl1PreparedSkinInfluenceExpectation influence in influences)
            result[influence.EntityIndex] = result.GetValueOrDefault(influence.EntityIndex) + influence.Weight;
        return result;
    }

    private static bool InfluencesEquivalent(
        Dictionary<int, double> first,
        Dictionary<int, double> second, bool allowByteZeroLanes = false)
    {
        if (first.Count == 0 || second.Count == 0) return first.Count == second.Count;
        if (first.Count > 4 || second.Count > 4 || second.Keys.Any(entity => !first.ContainsKey(entity)))
            return false;
        if (!allowByteZeroLanes && first.Count != second.Count) return false;
        double missingMass = 0;
        int missingLanes = 0;
        double residualBound = (Math.Max(1, first.Count - 1) / 255.0) + (1.0 / 32767.0) + 1e-7;
        int residualCandidates = 0;
        foreach ((int entity, double weight) in first)
        {
            if (!second.TryGetValue(entity, out double other))
            {
                if (!allowByteZeroLanes || weight > WeightTolerance) return false;
                missingMass += weight;
                missingLanes++;
                continue;
            }
            double error = Math.Abs(weight - other);
            if (error > WeightTolerance && (++residualCandidates > 1 || error > residualBound))
                return false;
        }
        // Native byte conversion conserves the sum by assigning truncation residue
        // to one lane. Preserve entity identity and require the same normalized sum;
        // do not apply a larger blanket tolerance to every influence.
        return missingMass <= missingLanes * WeightTolerance &&
            Math.Abs(first.Values.Sum() - second.Values.Sum()) <= (1.0 / 32767.0) + 1e-6;
    }

    private static bool HasPositiveWeight(Vector4 weights) =>
        weights.X > 0 || weights.Y > 0 || weights.Z > 0 || weights.W > 0;

    private static CompiledVertexElement RequireElement(
        CompiledMeshSurface surface,
        CompiledVertexSemantic semantic,
        byte channel)
    {
        CompiledVertexElement[] matches = surface.VertexLayout.Elements
            .Where(element => element.RawSemantic == (byte)semantic && element.Channel == channel)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"Compiled surface '{surface.Name}' lacks one unique {semantic} channel {channel} element.");
    }

    private static CompiledVertexElement? FindElement(
        CompiledMeshSurface surface,
        CompiledVertexSemantic semantic,
        byte channel)
    {
        CompiledVertexElement[] matches = surface.VertexLayout.Elements
            .Where(element => element.RawSemantic == (byte)semantic && element.Channel == channel)
            .ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"Compiled surface '{surface.Name}' has ambiguous {semantic} channel {channel} elements."),
        };
    }

    private static VertexSignature ExpectedSignature(
        Dl1PreparedSkinVertexExpectation vertex,
        CompiledVertexElement position,
        string surfaceName)
    {
        Vector3 actualPosition = position.Format switch
        {
            CompiledVertexFormat.Float3 => vertex.Position,
            CompiledVertexFormat.Half4 => new(
                (float)(Half)vertex.Position.X,
                (float)(Half)vertex.Position.Y,
                (float)(Half)vertex.Position.Z),
            _ => throw new InvalidDataException($"Compiled surface '{surfaceName}' uses unsupported position encoding."),
        };
        if (!IsFinite(actualPosition))
            throw new InvalidDataException($"Prepared surface '{surfaceName}' exceeds the compiled vertex range.");
        return new(actualPosition);
    }

    private static VertexSignature ActualSignature(CompiledVertex vertex) =>
        new(vertex.Position);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct VertexSignature(Vector3 Position);

    private sealed class SurfaceKeyComparer : IEqualityComparer<(string Name, int Lod)>
    {
        public static SurfaceKeyComparer Instance { get; } = new();

        public bool Equals((string Name, int Lod) left, (string Name, int Lod) right) =>
            left.Lod == right.Lod && string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, int Lod) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name), value.Lod);
    }
}

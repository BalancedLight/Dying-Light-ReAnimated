using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>The editable geometry contract shared by native and FBX imports; no interchange conversion is involved.</summary>
public sealed record DecodedCharacterSnapshot(CustomModelDocument SourceDocument, ImmutableArray<FbxModelSurface> Surfaces,
    ImmutableArray<FbxLodGroupEvidence> LodGroups)
{
    public ImmutableArray<CharacterLodNode> CharacterLods { get; init; } = [];
}

public static class DecodedCharacterSnapshotCodec
{
    public static ImmutableArray<byte> Encode(DecodedCharacterSnapshot snapshot)
    {
        snapshot.SourceDocument.Validate();
        return ImmutableArray.Create(JsonSerializer.SerializeToUtf8Bytes(snapshot, CustomModelPackageSerializer.CreateSerializerOptions()));
    }

    public static FbxModelAuthoringImportResult DecodeSource(CustomModelPackage package, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inventory = package.Document.CharacterResources ?? throw new InvalidDataException("Native source inventory is missing.");
        if (package.DecodedCharacterPayload.Length != inventory.DecodedByteLength ||
            !Convert.ToHexString(SHA256.HashData(package.DecodedCharacterPayload.AsSpan())).Equals(inventory.DecodedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The decoded character snapshot has changed.");
        var snapshot = JsonSerializer.Deserialize<DecodedCharacterSnapshot>(package.DecodedCharacterPayload.AsSpan(),
            CustomModelPackageSerializer.CreateSerializerOptions()) ?? throw new InvalidDataException("The decoded character snapshot is empty.");
        var doc = snapshot.SourceDocument;
        if (doc.Source.ContentSha256 != package.Document.Source.ContentSha256)
            throw new InvalidDataException("The decoded character snapshot belongs to another source.");
        // The snapshot is a baseline, not another mutable source document.
        doc = doc with { ModelId=package.Document.ModelId, Source=package.Document.Source, CharacterResources = inventory, AuthoredLayer = null, LastBuildReceipt = null };
        doc.Validate();
        if (snapshot.Surfaces.IsDefaultOrEmpty || snapshot.Surfaces.Sum(s => (long)s.Vertices.Length) > 20_000_000)
            throw new InvalidDataException("Decoded character geometry is missing or oversized.");
        foreach (var s in snapshot.Surfaces)
        {
            if (s.SourceGeometry is null || s.SourceCorners.Length != s.Vertices.Length || s.Indices.Length % 3 != 0 ||
                s.Indices.Any(i => i >= s.Vertices.Length) || s.PaletteBoneIndices.Length != s.InverseBindMatrices.Length ||
                s.Vertices.Any(v => !v.Position.IsFinite || !v.Normal.IsFinite || v.BoneIndices.Length != v.BoneWeights.Length || v.BoneIndices.Any(i=>i<0 || i>=s.PaletteBoneIndices.Length)) ||
                s.MorphTargets.Any(m => m.PositionDeltas.Length != s.Vertices.Length || m.PositionDeltas.Any(d => !d.IsFinite)))
                throw new InvalidDataException($"Invalid decoded character surface '{s.Id}'.");
        }
        var emptyInspection = new FbxStrictExportInspection([], ImmutableDictionary<string, FbxAnimationStackInspection>.Empty,
            ImmutableDictionary<string, long>.Empty, ImmutableDictionary<string, long?>.Empty, [], snapshot.Surfaces.Length,
            snapshot.Surfaces.Length, snapshot.Surfaces.Select(s => s.MeshName).ToImmutableHashSet(),
            snapshot.Surfaces.Select(s => s.MeshName).ToImmutableHashSet(), ImmutableDictionary<string, FbxMeshGeometryInspection>.Empty,
            0, 0, [], [], ImmutableHashSet<string>.Empty);
        return new(package with { Document = doc, AuthoredLayerPayload = [] }, doc.Bones.IsEmpty ? null : doc.CreateRigDefinition(),
            snapshot.Surfaces, ImmutableDictionary<Guid, ReAnimated.Core.Domain.AnimationClip>.Empty, emptyInspection) { SourceLodGroups = snapshot.LodGroups, SourceCharacterLods = snapshot.CharacterLods };
    }
}

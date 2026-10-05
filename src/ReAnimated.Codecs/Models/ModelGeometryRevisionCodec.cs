using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class ModelGeometryRevisionCodec
{
    public static FbxModelAuthoringImportResult Capture(FbxModelAuthoringImportResult model)
    {
        model = CharacterBodyRegionAuthoring.Reconcile(MorphAuthoringEvidence.ReconcileTarget(model));
        var doc = model.Package.Document with { GeometryRevision = null, AuthoredLayer = null, LastBuildReceipt = null };
        if (doc.CharacterResources is { } c) doc = doc with { CharacterResources = c with
            { CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [] } };
        var payload = DecodedCharacterSnapshotCodec.Encode(new(doc, model.Surfaces, model.SourceLodGroups) { CharacterLods = model.SourceCharacterLods });
        var reference = new ModelGeometryRevisionReference { SourceSha256 = doc.Source.ContentSha256,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(payload.AsSpan())), PayloadLength = payload.Length };
        return model with { Package = model.Package with { Document = doc with { GeometryRevision = reference },
            GeometryRevisionPayload = payload, AuthoredLayerPayload = [] } };
    }
    public static FbxModelAuthoringImportResult Replay(FbxModelAuthoringImportResult source, CustomModelPackage saved)
    {
        var reference = saved.Document.GeometryRevision ?? throw new InvalidDataException("Geometry revision reference missing.");
        reference.Validate();
        if (reference.SourceSha256 != saved.Document.Source.ContentSha256 || reference.PayloadLength != saved.GeometryRevisionPayload.Length ||
            !reference.ContentSha256.Equals(Convert.ToHexStringLower(SHA256.HashData(saved.GeometryRevisionPayload.AsSpan())), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Geometry revision changed or belongs to another source.");
        var snapshot = JsonSerializer.Deserialize<DecodedCharacterSnapshot>(saved.GeometryRevisionPayload.AsSpan(),
            CustomModelPackageSerializer.CreateSerializerOptions()) ?? throw new InvalidDataException("Geometry revision is empty.");
        if (snapshot.SourceDocument.Source.ContentSha256 != saved.Document.Source.ContentSha256)
            throw new InvalidDataException("Geometry revision identity differs from its immutable source.");
        if (snapshot.Surfaces.IsDefaultOrEmpty || snapshot.Surfaces.Sum(s => (long)s.Vertices.Length) > 20_000_000 ||
            snapshot.Surfaces.Any(s => s.Indices.Length % 3 != 0 || s.Indices.Any(i => i >= s.Vertices.Length) ||
                s.Vertices.Any(v => !v.Position.IsFinite || !v.Normal.IsFinite || v.BoneIndices.Any(i => i < 0 || i >= s.PaletteBoneIndices.Length)) ||
                s.MorphTargets.Any(m => m.PositionDeltas.Length != s.Vertices.Length || m.PositionDeltas.Any(d => !d.IsFinite))))
            throw new InvalidDataException("Geometry revision contains invalid topology or binding.");
        saved.Document.Validate();
        if (saved.Document.MorphSignature!=FbxModelAuthoringImporter.ComputeMorphSignature(saved.Document.MorphChannels,snapshot.Surfaces) ||
            snapshot.Surfaces.Any(s=>!saved.Document.Materials.Any(m=>m.Id==s.MaterialId) ||
                s.MorphTargets.Any(m=>!saved.Document.MorphChannels.Any(c=>c.Name==m.Name && c.DescriptorHash==m.DescriptorHash))))
            throw new InvalidDataException("Geometry revision materials or morph inventory differ from the current document.");
        return CharacterBodyRegionAuthoring.Reconcile(MorphAuthoringEvidence.ReconcileTarget(source with { Package = saved, Surfaces = snapshot.Surfaces, SourceLodGroups = snapshot.LodGroups, SourceCharacterLods = snapshot.CharacterLods,
            Rig = saved.Document.Bones.IsEmpty ? null : saved.Document.CreateRigDefinition() }));
    }
}

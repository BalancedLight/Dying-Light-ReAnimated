using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

/// <summary>Preserves expression receipts while invalidating review after neutral, topology, deformation or channel-mapping changes.</summary>
public static class MorphAuthoringEvidence
{
    public static FbxModelAuthoringImportResult ReconcileTarget(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var original = model.Package.Document.MorphAuthoringRecords;
        if (original.IsEmpty) return model;
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        var updated = original.Select(record => record.Accepted && !MatchesTarget(model, record, fingerprints)
            ? record with { Accepted = false } : record).ToImmutableArray();
        if (original.SequenceEqual(updated)) return model;
        return model with { Package = model.Package with { Document = model.Package.Document with
        {
            MorphAuthoringRecords = updated, LastBuildReceipt = null,
            CharacterResources = model.Package.Document.CharacterResources is { } inventory
                ? inventory with { CompiledSemanticSha256 = null, LoadedResourceSha256 = null, VerifiedPlayerScenarios = [] }
                : null,
        } } };
    }

    public static ImmutableArray<string> ExportBlockers(FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
        return model.Package.Document.MorphAuthoringRecords.Where(record => !record.Accepted || !MatchesTarget(model, record, fingerprints))
            .Select(record => $"Expression '{record.Name}' on '{record.TargetSurfaceId}' requires review against the current target neutral, topology, deformation and channel mapping.")
            .ToImmutableArray();
    }

    public static string ExpressionFingerprint(FbxModelMorphTarget expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, expression.PositionDeltas.Length);
        hash.AppendData(bytes[..4]);
        foreach(var delta in expression.PositionDeltas)
        {
            BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(delta.X));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], BitConverter.DoubleToInt64Bits(delta.Y));
            BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], BitConverter.DoubleToInt64Bits(delta.Z));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool MatchesTarget(FbxModelAuthoringImportResult model, MorphAuthoringRecord record, Dictionary<string, string> fingerprints)
    {
        FbxModelSurface? surface = model.Surfaces.SingleOrDefault(candidate => candidate.Id == record.TargetSurfaceId);
        if (surface is null) return false;
        FbxModelMorphTarget? expression = surface.MorphTargets.SingleOrDefault(target => target.Name == record.Name && target.DescriptorHash == record.DescriptorHash);
        if (expression is null || expression.PositionDeltas.All(delta => delta.LengthSquared == 0) ||
            expression.PositionDeltas.Length != surface.Vertices.Length || expression.PositionDeltas.Any(delta => !delta.IsFinite) ||
            record.AuthoredExpressionSha256 is null ||
            !string.Equals(ExpressionFingerprint(expression), record.AuthoredExpressionSha256, StringComparison.OrdinalIgnoreCase)) return false;
        var channel = model.Package.Document.MorphChannels.SingleOrDefault(channel => channel.Name == record.Name);
        if(channel is null || channel.Index != record.TargetChannelSlot || channel.DescriptorHash != record.DescriptorHash) return false;
        if (!fingerprints.TryGetValue(surface.Id, out string? fingerprint))
            fingerprints.Add(surface.Id, fingerprint = CharacterGeometryAuthoring.CreateMorphProfile(model, surface.Id).TopologyFingerprint);
        return string.Equals(fingerprint, record.TargetNeutralFingerprint, StringComparison.OrdinalIgnoreCase) &&
            record.ReviewedTargetRegion.All(index => index < surface.Vertices.Length) &&
            record.LockedTargetVertices.All(index => index < surface.Vertices.Length) &&
            record.ReviewedLandmarks.All(link => link.TargetGeometryIdentity == surface.Id && link.TargetVertexIndex < surface.Vertices.Length) &&
            record.ReviewedTriangles.All(link => link.TargetGeometryIdentity == surface.Id && link.TargetTriangleIndex < surface.Indices.Length / 3);
    }
}

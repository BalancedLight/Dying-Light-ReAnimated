using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Models;

public static class CharacterManualExpressionAuthoring
{
    public static async Task<ImmutableArray<Vector3D>> ReadDeltasAsync(
        FbxModelAuthoringImportResult target, string targetSurfaceId, string sculptPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSurfaceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sculptPath);
        var surface = target.Surfaces.SingleOrDefault(surface => surface.Id == targetSurfaceId)
            ?? throw new InvalidDataException("Target face surface was not found.");
        cancellationToken.ThrowIfCancellationRequested();
        string extension = Path.GetExtension(sculptPath).ToLowerInvariant();
        if (extension == ".obj")
        {
            await using var stream = new FileStream(sculptPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > ManualMorphObjCodec.MaximumObjBytes)
                throw new InvalidDataException("The sculpt is empty or exceeds the supported file size.");
            byte[] bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != length) throw new IOException("The sculpt changed while reading.");
            cancellationToken.ThrowIfCancellationRequested();
            return ManualMorphObjCodec.ComputePositionDeltas(surface, bytes);
        }
        if (extension != ".fbx") throw new InvalidDataException("Use an OBJ or binary FBX sculpt.");
        byte[] fbxBytes;
        await using (var stream = new FileStream(sculptPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            long length = stream.Length;
            if (length <= 0 || length > CustomModelPackageSerializer.MaximumSourceFbxBytes)
                throw new InvalidDataException("The sculpt is empty or exceeds the supported file size.");
            fbxBytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(fbxBytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != length) throw new IOException("The sculpt changed while reading.");
        }
        var sculpt = await Task.Run(() => FbxModelAuthoringImporter.Import(fbxBytes, Path.GetFileName(sculptPath),
            new() { RigMode = CustomModelRigMode.Auto, DecodeAnimationClips = false }, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (sculpt.Surfaces.Length != 1)
            throw new InvalidDataException("The sculpt must contain one target face surface.");
        cancellationToken.ThrowIfCancellationRequested();
        return ManualMorphShapeAuthoring.ComputePositionDeltas(surface, sculpt.Surfaces[0]);
    }

    public static FbxModelAuthoringImportResult Apply(
        FbxModelAuthoringImportResult target, FbxModelAuthoringImportResult reference,
        string targetSurfaceId, string referenceSurfaceId, string expressionName,
        ImmutableArray<Vector3D> deltas, MorphTransferConflict conflict, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(reference);
        if (!reviewed) throw new InvalidOperationException("Review the sculpt before importing.");
        var channel = reference.Package.Document.MorphChannels.SingleOrDefault(channel => channel.Name == expressionName)
            ?? throw new InvalidDataException("The expression name was not found in the reference.");
        var referenceSurface = reference.Surfaces.SingleOrDefault(surface => surface.Id == referenceSurfaceId)
            ?? throw new InvalidDataException("Reference face surface was not found.");
        if (!referenceSurface.MorphTargets.Any(shape => shape.Name == channel.Name && shape.DescriptorHash == channel.DescriptorHash))
            throw new InvalidDataException("The expression is not bound to the selected reference face.");
        var sourceProfile = CharacterGeometryAuthoring.CreateMorphProfile(reference, referenceSurfaceId);
        var targetProfile = CharacterGeometryAuthoring.CreateMorphProfile(target, targetSurfaceId);
        var revised = CharacterGeometryAuthoring.SetExpression(target, targetSurfaceId, channel.Name,
            channel.DescriptorHash, deltas, conflict, reviewed);
        if (ReferenceEquals(revised, target)) return target;
        var targetChannel = revised.Package.Document.MorphChannels.Single(channel => channel.Name == expressionName);
        var sourceBinding = reference.Package.Document.CharacterResources?.MorphBindings.FirstOrDefault(binding =>
            binding.Name == channel.Name && binding.DescriptorHash == channel.DescriptorHash && binding.SurfaceId == referenceSurfaceId);
        var receipt = new MorphAuthoringRecord
        {
            Name = channel.Name, DescriptorHash = channel.DescriptorHash,
            ReferenceSourceSha256 = sourceProfile.SourceSha256,
            OriginalSourceChannelIndex = sourceBinding?.SourceChannelIndex ?? channel.Index,
            TargetChannelSlot = targetChannel.Index,
            ReferenceSurfaceId = referenceSurfaceId, TargetSurfaceId = targetSurfaceId,
            ReferenceTopologyFingerprint = sourceProfile.TopologyFingerprint,
            TargetNeutralFingerprint = targetProfile.TopologyFingerprint,
            AuthoredExpressionSha256 = MorphAuthoringEvidence.ExpressionFingerprint(revised.Surfaces
                .Single(surface => surface.Id == targetSurfaceId).MorphTargets.Single(shape => shape.Name == expressionName)),
            Method = MorphAuthoringMethod.ManualSculpt, ConflictChoice = conflict, Accepted = true,
        };
        receipt.Validate();
        var records = revised.Package.Document.MorphAuthoringRecords.Where(record =>
            record.Name != expressionName || record.TargetSurfaceId != targetSurfaceId).Append(receipt).ToImmutableArray();
        return ModelGeometryRevisionCodec.Capture(revised with { Package = revised.Package with
        { Document = revised.Package.Document with { MorphAuthoringRecords = records } } });
    }
}
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>Appends one reviewed geometry-pivot eye deform node while preserving source surfaces and authored data.</summary>
public static class FbxGeneratedEyeAuthoring
{
    public static FbxModelAuthoringImportResult Append(FbxModelAuthoringImportResult model,
        RigEyeSide side, string boneName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        CustomModelDocument document = model.Package.Document;
        CustomModelDocument updated = GeneratedEyeRig.Append(document, side, boneName);
        if (ReferenceEquals(updated, document)) return model;
        FbxModelAuthoringImportResult candidate = model with
        {
            Package = model.Package with { Document = updated },
            Rig = updated.CreateRigDefinition(),
        };
        cancellationToken.ThrowIfCancellationRequested();
        return FbxAuthoredModelLayer.Capture(candidate, cancellationToken);
    }
}

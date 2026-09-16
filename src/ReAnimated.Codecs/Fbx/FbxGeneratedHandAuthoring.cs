using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Codecs.Fbx;

/// <summary>
/// Extends an owned generated body with a reviewed hand extension while
/// preserving the decoded surfaces and source-linked authored layer.
/// </summary>
public static class FbxGeneratedHandAuthoring
{
    public static FbxModelAuthoringImportResult Append(
        FbxModelAuthoringImportResult model,
        RigHandSide side,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        CustomModelDocument document = model.Package.Document;
        CustomModelDocument updated = GeneratedHandRig.Append(document, side);
        if (ReferenceEquals(updated, document)) return model;

        // Appending bones does not rewrite render vertices. Capture updates the
        // source-linked target-bone contract while retaining the existing
        // surface, weight, morph and expert inverse-bind data.
        FbxModelAuthoringImportResult candidate = model with
        {
            Package = model.Package with { Document = updated },
            Rig = updated.CreateRigDefinition(),
        };
        cancellationToken.ThrowIfCancellationRequested();
        return FbxAuthoredModelLayer.Capture(candidate, cancellationToken);
    }
}

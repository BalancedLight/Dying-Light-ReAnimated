using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

internal enum PersistedSecondaryMotionAdoption
{
    NotCurrentModel,
    Adopted,
    Conflict,
}

public sealed partial class ModelsWorkspaceViewModel
{
    internal PersistedSecondaryMotionAdoption AdoptPersistedSecondaryMotion(
        PendingSecondaryMotionEdit edit,
        long expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(edit);
        edit.Validate();
        if (expectedRevision < 0 || expectedRevision > PersistenceRevision)
            return PersistedSecondaryMotionAdoption.Conflict;
        if (_model is not { } current || current.Package.Document.ModelId != edit.ModelId)
            return PersistedSecondaryMotionAdoption.NotCurrentModel;
        if (!current.Package.Document.Source.ContentSha256.Equals(edit.SourceHash, StringComparison.OrdinalIgnoreCase) ||
            !CustomModelContractSignatures.ComputeRig(current.Package.Document.CreateEffectiveBones())
                .Equals(edit.RigSignature, StringComparison.OrdinalIgnoreCase))
            return PersistedSecondaryMotionAdoption.Conflict;

        string currentSecondaryHash = PendingSecondaryMotionEdit.ComputeDefinitionSha256(
            current.Package.Document.SecondaryMotion);
        if (!currentSecondaryHash.Equals(edit.BaselineDefinitionSha256, StringComparison.OrdinalIgnoreCase) &&
            !currentSecondaryHash.Equals(edit.DefinitionSha256, StringComparison.OrdinalIgnoreCase))
            return PersistedSecondaryMotionAdoption.Conflict;

        CustomModelDocument document = current.Package.Document with
        {
            SecondaryMotion = edit.Definition,
            LastBuildReceipt = null,
        };
        document.Validate();
        if (document == current.Package.Document)
            return PersistedSecondaryMotionAdoption.Adopted;
        FbxModelAuthoringImportResult updated = current with { Package = current.Package with { Document = document } };
        _model = updated;
        Conformance.RefreshMetadataSnapshot(current, updated);
        InvalidateDeploymentInspection();
        InvalidateDeploymentPreflight();
        return PersistedSecondaryMotionAdoption.Adopted;
    }
}

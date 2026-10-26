using ReAnimated.App.Infrastructure;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class ModelsWorkspaceViewModel
{
    private bool _existingBankDefaultsPending;

    private CustomModelDocument ApplyPendingExistingBankDefaults(CustomModelDocument document)
    {
        if (document.RigConformance?.FitStrategy == CustomModelRigFitStrategy.StockFppRegional)
        {
            _existingBankDefaultsPending = false;
            return document;
        }
        if (!_existingBankDefaultsPending || !ReferenceExistingAnimationLibrary ||
            Conformance.Template is not { } template || document.RigConformance is null)
            return document;
        CustomModelDocument updated = ExistingAnimationBankDefaults.Apply(document, template,
            Conformance.StockHumanoidRootPosition?.Value ?? StockHumanoidRootChannel.Clip,
            Conformance.StockHumanoidRootRotation?.Value ?? StockHumanoidRootChannel.Clip,
            Conformance.StockHumanoidRootScale?.Value ?? StockHumanoidRootChannel.Bind);
        if (ReferenceEquals(updated, document) && !ExistingAnimationBankDefaults.IsSelected(document)) return document;
        _existingBankDefaultsPending = false;
        return updated;
    }

    private void SyncPendingExistingBankDefaults()
    {
        if (_suppressPersistenceNotifications || !_existingBankDefaultsPending ||
            !ReferenceExistingAnimationLibrary || _model is null || string.IsNullOrWhiteSpace(AnimationScriptAlias)) return;
        SyncDocument();
        _previewSession = null;
        if (ExistingAnimationBankDefaults.IsSelected(_model.Package.Document))
        {
            _useReviewedPolicyPreview = SelectedPreviewMode.Mode == CustomModelPreviewMode.Dl1Output;
            OnPropertyChanged(nameof(UseReviewedPolicyPreview));
        }
        RefreshPreview();
    }
}

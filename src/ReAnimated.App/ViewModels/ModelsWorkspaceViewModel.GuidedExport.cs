using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

internal sealed record GuidedChannelPolicyReviewState(bool IsReady, int UnresolvedNodeCount, string Summary);

public sealed partial class ModelsWorkspaceViewModel
{
    public bool HasGuidedAnimationTakes => Animations.Count > 0;

    public bool CanSelectGuidedLocalAnimationTakes => !ReferenceExistingAnimationLibrary;

    public bool ShowGuidedAnimationAliasInput => ReferenceExistingAnimationLibrary || HasGuidedIncludedAnimationTakes;

    public int GuidedIncludedPoseOnlyTakeCount => CountIncludedPoseOnlyTakes(Animations, ReferenceExistingAnimationLibrary);

    public bool HasGuidedIncludedPoseOnlyTakes => GuidedIncludedPoseOnlyTakeCount > 0;

    public bool HasGuidedIncludedMotionTakes => HasIncludedMotionOrUnknownTakes(
        _model, Animations.Select(static animation => animation.ToContract()));

    public bool HasGuidedIncludedAnimationTakes => Animations.Any(static animation => animation.Included);

    private string GuidedDefaultAnimationAlias => Dl1SourceModelWriter.SanitizeName(
        string.IsNullOrWhiteSpace(ResourceName) ? _model?.Package.Document.Name ?? "custom_model" : ResourceName,
        55);

    public string GuidedAnimationPackageSummary =>
        ReferenceExistingAnimationLibrary
            ? string.IsNullOrWhiteSpace(AnimationScriptAlias)
                ? "An existing animation bank is selected; set its script alias before building. Local take checkboxes do not change that external bank."
                : $"References the existing {AnimationScriptAlias.Trim()}.scr bank; local takes and previewed stock motion are not added to this package."
            : HasGuidedIncludedPoseOnlyTakes
                ? $"{GuidedIncludedPoseOnlyTakeCount} checked take(s) are pose-only and remain selected for this package. Uncheck them for a model-only build. " +
                  (string.IsNullOrWhiteSpace(AnimationScriptAlias)
                      ? $"If you keep them selected, {GuidedDefaultAnimationAlias} will identify the local animation library."
                      : $"If you keep them selected, the saved {AnimationScriptAlias.Trim()}.scr identity will be used.")
                : HasGuidedIncludedAnimationTakes
                    ? string.IsNullOrWhiteSpace(AnimationScriptAlias)
                        ? $"Included moving takes will use {GuidedDefaultAnimationAlias} as the animation-script identity for this package."
                        : $"Included moving takes will use the saved {AnimationScriptAlias.Trim()}.scr animation-script identity."
                    : "No moving takes are selected for export. The model package will not include an animation library.";

    public bool GuidedChannelPolicyReviewRequired => !GetGuidedChannelPolicyReviewState(_model).IsReady;

    public int GuidedUnresolvedChannelDecisionCount =>
        GetGuidedChannelPolicyReviewState(_model).UnresolvedNodeCount;

    public string GuidedChannelPolicyReviewSummary => GetGuidedChannelPolicyReviewState(_model).Summary;

    private void InitializeGuidedExport()
    {
        InitializeModelPerspectives();
        Animations.CollectionChanged += (_, _) => NotifyGuidedExportState();
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(HasModel) or nameof(PersistenceRevision) or nameof(ResourceName) or
                nameof(AnimationScriptAlias) or nameof(ReferenceExistingAnimationLibrary))
                NotifyGuidedExportState();
        };
        Conformance.ChannelPolicies.CollectionChanged += (_, _) => NotifyGuidedExportState();
    }

    private void NotifyGuidedExportState()
    {
        OnPropertyChanged(nameof(HasGuidedAnimationTakes));
        OnPropertyChanged(nameof(CanSelectGuidedLocalAnimationTakes));
        OnPropertyChanged(nameof(ShowGuidedAnimationAliasInput));
        OnPropertyChanged(nameof(GuidedIncludedPoseOnlyTakeCount));
        OnPropertyChanged(nameof(HasGuidedIncludedPoseOnlyTakes));
        OnPropertyChanged(nameof(HasGuidedIncludedMotionTakes));
        OnPropertyChanged(nameof(HasGuidedIncludedAnimationTakes));
        OnPropertyChanged(nameof(GuidedAnimationPackageSummary));
        OnPropertyChanged(nameof(GuidedChannelPolicyReviewRequired));
        OnPropertyChanged(nameof(GuidedUnresolvedChannelDecisionCount));
        OnPropertyChanged(nameof(GuidedChannelPolicyReviewSummary));
        _guidedPrimaryCommand?.NotifyCanExecuteChanged();
    }

    internal static FbxModelAuthoringImportResult ApplyGuidedImportAnimationDefaults(
        FbxModelAuthoringImportResult model,
        bool isNewGuidedImport)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!isNewGuidedImport || model.Package.Document.AnimationClips.IsEmpty)
            return model;

        bool changed = false;
        var selections = model.Package.Document.AnimationClips.Select(selection =>
        {
            if (!selection.Included || !model.AnimationClips.TryGetValue(selection.Id, out var decoded) ||
                MainWindowViewModel.AnimationContainsTemporalMovement([decoded]))
                return selection;

            changed = true;
            return selection with { Included = false };
        }).ToImmutableArray();

        return changed
            ? model with { Package = model.Package with { Document = model.Package.Document with { AnimationClips = selections } } }
            : model;
    }

    internal static int CountIncludedPoseOnlyTakes(
        IEnumerable<CustomModelAnimationClipItemViewModel> animations,
        bool referenceExistingAnimationLibrary = false)
    {
        ArgumentNullException.ThrowIfNull(animations);
        if (referenceExistingAnimationLibrary) return 0;
        return animations.Count(animation => animation.Included && animation.DecodedClip is { } decoded &&
            !MainWindowViewModel.AnimationContainsTemporalMovement([decoded]));
    }

    internal static ImmutableArray<CustomModelAnimationClip> GetGuidedPackageAnimationSelections(
        FbxModelAuthoringImportResult model)
    {
        ArgumentNullException.ThrowIfNull(model);
        // Keep explicit and saved document choices intact. A new guided FBX's
        // pose-only defaults are seeded off during import; older saved choices
        // are surfaced in the Export panel for an explicit user decision.
        return model.Package.Document.AnimationClips;
    }

    internal static string? ResolveGuidedPackageAnimationAlias(
        ImmutableArray<CustomModelAnimationClip> selections,
        string? savedAlias,
        bool referenceExistingAnimationLibrary,
        string resourceName)
    {
        if (referenceExistingAnimationLibrary)
            return string.IsNullOrWhiteSpace(savedAlias) ? null : savedAlias.Trim();
        if (!selections.Any(static selection => selection.Included)) return null;
        if (!string.IsNullOrWhiteSpace(savedAlias)) return savedAlias.Trim();
        return Dl1SourceModelWriter.SanitizeName(resourceName, 55);
    }

    internal static bool HasIncludedMotionOrUnknownTakes(
        FbxModelAuthoringImportResult? model,
        IEnumerable<CustomModelAnimationClip> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (model is null) return false;
        foreach (CustomModelAnimationClip selection in selections)
        {
            if (!selection.Included) continue;
            if (!model.AnimationClips.TryGetValue(selection.Id, out var decoded) ||
                MainWindowViewModel.AnimationContainsTemporalMovement([decoded]))
                return true;
        }
        return false;
    }

    internal static GuidedChannelPolicyReviewState GetGuidedChannelPolicyReviewState(
        FbxModelAuthoringImportResult? model)
    {
        if (model?.Package.Document.RiggingSession is null || model.Rig is null)
            return new(true, 0, "This model has no saved studio channel policy to review.");

        try
        {
            Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(model);
            Dl1BoneScriptPolicyResolver.Resolve(model.Package.Document, prepared.Contract);
            return new(true, 0, "All emitted nodes have explicit channel/LOD decisions with evidence.");
        }
        catch (InvalidDataException exception)
        {
            int unresolved = ParseLeadingUnresolvedNodeCount(exception.Message);
            return new(false, unresolved, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return new(false, 0, "Channel-policy review is required before package export: " + exception.Message);
        }
    }

    private static int ParseLeadingUnresolvedNodeCount(string message)
    {
        int separator = message.IndexOf(' ');
        return separator > 0 && int.TryParse(message.AsSpan(0, separator), NumberStyles.None,
            CultureInfo.InvariantCulture, out int count) ? count : 0;
    }
}

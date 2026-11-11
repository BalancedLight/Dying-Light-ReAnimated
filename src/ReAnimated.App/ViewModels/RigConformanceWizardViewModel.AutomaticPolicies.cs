using System.Collections.Immutable;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    public IAsyncRelayCommand ApplyAutomaticChannelPoliciesCommand { get; private set; } = null!;

    private void InitializeAutomaticChannelPolicies() => ApplyAutomaticChannelPoliciesCommand =
        new AsyncRelayCommand(ApplyAutomaticChannelPoliciesAsync, () => CanEditChannelPolicies);

    internal async Task ApplyAutomaticChannelPoliciesAsync(CancellationToken cancellationToken)
    {
        if (_model is not { } model || model.Package.Document.RiggingSession is not { } session) return;
        string requestedProfile = TemplateProfileName;
        string? stockWarning = "No exact reference was available; source-preserving defaults were used.";
        if (_stockPolicySourcePicker is not null && model.Package.Document.RigConformance is not null &&
            (_template is null || _template.ProfileName != requestedProfile))
        {
            try { await ResolveTemplateAsync(cancellationToken).ConfigureAwait(true); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or UnauthorizedAccessException or NotSupportedException)
            { stockWarning = "Stock defaults were unavailable: " + error.Message; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_model, model) || TemplateProfileName != requestedProfile) return;
        var selectedTemplate = _template;
        var token = session.CreateJobToken();
        var document = model.Package.Document;
        Dl1StockBoneScriptPolicyProposal? proposal = null;
        IsBusy = true;
        NotifyChannelPolicies();
        try
        {
            if (_template is { } template && _stockPolicySourcePicker is { } picker && document.RigConformance is not null)
            {
                try
                {
                    var payload = await picker(template, cancellationToken).ConfigureAwait(true);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (payload is not null && payload.Source.ResourceName == template.SourceResourceName)
                        proposal = Dl1StockBoneScriptPolicyProposalService.Propose(template, payload.Source.Hierarchy,
                            payload.ResourceSha256!, document, Dl1CustomModelRigPreparer.Prepare(model, cancellationToken));
                    else stockWarning = "The exact reference was unavailable; source-preserving defaults were used.";
                    if (proposal is not null) stockWarning = null;
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
                { stockWarning = "Stock defaults were unavailable: " + error.Message; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_model, model) || _model.Package.Document.RiggingSession is not { } current ||
                !current.Matches(token) || !ReferenceEquals(_template, selectedTemplate) || TemplateProfileName != requestedProfile)
            { ChannelPolicyStatus = "The model or reference changed; automatic defaults were not applied. Retry for the current reference."; return; }
            var defaults = proposal?.Rows.Where(row => row.Status == Dl1StockPolicyProposalStatus.Proposed)
                .ToDictionary(row => row.DestinationEntityId!.Value, row => (row.ProposedMask!.Value, row.ProposedLod!.Value));
            var updated = RigAnimationPolicyDefaults.Apply(document, defaults);
            if (ReferenceEquals(updated, document))
            { ChannelPolicyStatus = "Every node already has saved choices. Automatic defaults kept them unchanged."; return; }
            if (proposal is not null)
            {
                var original = session.Recipe.ComponentPolicies.ToDictionary(policy => policy.EntityId);
                var observed = proposal.Rows.Where(row => row.Status == Dl1StockPolicyProposalStatus.Proposed)
                    .ToDictionary(row => row.DestinationEntityId!.Value);
                updated = updated with { RiggingSession = updated.RiggingSession! with
                { Recipe = updated.RiggingSession.Recipe with
                { ComponentPolicies = updated.RiggingSession.Recipe.ComponentPolicies.Select(policy =>
                {
                    if (!observed.TryGetValue(policy.EntityId, out var row)) return policy;
                    original.TryGetValue(policy.EntityId, out var previous);
                    var evidence = new RigEvidenceReference
                    {
                        Id = $"automatic-stock-policy:{row.SourceIndex}", Kind = RigEvidenceKind.ImportedSource,
                        ArtifactSha256 = proposal.SourceSha256,
                        Description = $"Exact reference name, parent and kind match; observed flags {row.RawFlags:X8}. Channel ownership is an authoring choice; runtime behavior remains unverified.",
                    };
                    return policy with
                    {
                        Position = previous?.EmittedMask is null ? policy.Position with { Evidence = policy.Position.Evidence.Add(evidence) } : policy.Position,
                        Rotation = previous?.EmittedMask is null ? policy.Rotation with { Evidence = policy.Rotation.Evidence.Add(evidence) } : policy.Rotation,
                        Scale = previous?.EmittedMask is null ? policy.Scale with { Evidence = policy.Scale.Evidence.Add(evidence) } : policy.Scale,
                        LodEvidence = previous?.AnimationLod is null ? policy.LodEvidence.Add(evidence) : policy.LodEvidence,
                    };
                }).ToImmutableArray() } } };
            }
            updated.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            if (RigPoliciesApplyRequested is null)
            { ChannelPolicyStatus = "A workspace is required to save automatic defaults."; return; }
            RigPoliciesApplyRequested.Invoke(this, new(model, token, updated.RiggingSession!));
            ChannelPolicyStatus = "Saved automatic defaults as one undoable edit; existing choices were retained. " +
                (stockWarning ?? "Exact stock matches use reference channels; unmatched extras retain all tracks with LOD_OFF. Runtime behavior requires testing.");
        }
        finally { IsBusy = false; NotifyChannelPolicies(); }
    }
}

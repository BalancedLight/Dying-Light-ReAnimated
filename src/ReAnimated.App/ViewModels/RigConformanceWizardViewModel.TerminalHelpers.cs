using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.App.ViewModels;

public sealed record RigTerminalHelperPolicyPreviewRow(
    string Name, string Current, string Candidate, string Evidence, string Status, string Reason);

public sealed partial class RigConformanceWizardViewModel
{
    [ObservableProperty] private RigChannelLodChoice? _terminalHelperLodChoice;
    [ObservableProperty] private string _terminalHelperPolicyStatus =
        "Choose a retained LOD, then preview unmatched terminal helpers against the exact stock reference.";
    [ObservableProperty] private ImmutableArray<RigTerminalHelperPolicyPreviewRow> _terminalHelperPolicyRows = [];
    [ObservableProperty] private bool _terminalHelperPolicyReviewed;

    private TerminalHelperChannelPolicyProposal? _terminalHelperProposal;
    private HashSet<Guid>? _terminalUnmatchedIds;

    public IAsyncRelayCommand PreviewTerminalHelperPolicyCommand { get; private set; } = null!;
    public IRelayCommand ApplyTerminalHelperPolicyCommand { get; private set; } = null!;

    public bool CanPreviewTerminalHelperPolicy =>
        !IsBusy && !IsStockPolicyPreviewRunning &&
        TerminalHelperLodChoice is { Lod: not RigAnimationLod.Off } &&
        _model is { Rig: not null } &&
        _model.Package.Document.RiggingSession is { } session &&
        session.MatchesSource(_model.Package.Document.Source.ContentSha256) &&
        (_stockPolicyProposal is not null && _stockPolicyPreviewToken is { } token && session.Matches(token) ||
         CanPreviewStockChannelPolicies);

    public bool CanApplyTerminalHelperPolicy =>
        !IsBusy && !IsStockPolicyPreviewRunning && TerminalHelperPolicyReviewed &&
        _terminalHelperProposal is { Edits: { IsEmpty: false } } proposal &&
        _terminalUnmatchedIds is not null &&
        _model?.Package.Document.RiggingSession is { } session && session.Matches(proposal.Token);

    private void InitializeTerminalHelperPolicies()
    {
        PreviewTerminalHelperPolicyCommand = new AsyncRelayCommand(
            PreviewTerminalHelperPolicyAsync, () => CanPreviewTerminalHelperPolicy);
        ApplyTerminalHelperPolicyCommand = new RelayCommand(
            ApplyTerminalHelperPolicy, () => CanApplyTerminalHelperPolicy);
    }

    partial void OnTerminalHelperLodChoiceChanged(RigChannelLodChoice? value) =>
        InvalidateTerminalHelperPolicy("The retained LOD choice changed. Preview the helper evidence again.");

    partial void OnTerminalHelperPolicyReviewedChanged(bool value) => NotifyTerminalHelperPolicyState();

    private void NotifyTerminalHelperPolicyState()
    {
        OnPropertyChanged(nameof(CanPreviewTerminalHelperPolicy));
        OnPropertyChanged(nameof(CanApplyTerminalHelperPolicy));
        PreviewTerminalHelperPolicyCommand?.NotifyCanExecuteChanged();
        ApplyTerminalHelperPolicyCommand?.NotifyCanExecuteChanged();
    }

    private void InvalidateTerminalHelperPolicy(string message)
    {
        _terminalHelperProposal = null;
        _terminalUnmatchedIds = null;
        TerminalHelperPolicyRows = [];
        TerminalHelperPolicyReviewed = false;
        TerminalHelperPolicyStatus = message;
        NotifyTerminalHelperPolicyState();
    }

    private async Task PreviewTerminalHelperPolicyAsync(CancellationToken cancellationToken)
    {
        if (!CanPreviewTerminalHelperPolicy || TerminalHelperLodChoice is not { } lod)
            return;
        InvalidateTerminalHelperPolicy("Comparing the fitted helpers, embedded tracks, and exact stock hierarchy…");
        if (_stockPolicyProposal is null)
            await PreviewStockChannelPoliciesAsync(cancellationToken).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        if (_stockPolicyProposal is not { } stock || _stockPolicyPreviewToken is not { } stockToken ||
            _model is not { } model || model.Package.Document.RiggingSession is not { } session ||
            !session.Matches(stockToken))
        {
            TerminalHelperPolicyStatus = "The exact stock comparison is unavailable or changed. Preview its channel/LOD rows first.";
            return;
        }

        try
        {
            HashSet<Guid> unmatched = stock.Rows.Where(static row =>
                    row.Status == Dl1StockPolicyProposalStatus.UnresolvedExtraDestination &&
                    row.DestinationEntityId is not null)
                .Select(static row => row.DestinationEntityId!.Value).ToHashSet();
            ImmutableArray<TerminalHelperFitObservation> observations =
                TerminalHelperChannelPolicyAuthoring.Observe(model, unmatched, lod.Lod);
            TerminalHelperChannelPolicyProposal proposal =
                TerminalHelperChannelPolicyAuthoring.Propose(model.Package.Document, observations);
            if (!session.Matches(proposal.Token) || !ReferenceEquals(_model, model))
            {
                InvalidateTerminalHelperPolicy("The source or rig changed during the helper preview. Preview again.");
                return;
            }
            _terminalUnmatchedIds = unmatched;
            _terminalHelperProposal = proposal;
            TerminalHelperPolicyRows = proposal.Rows.Select(static row => new RigTerminalHelperPolicyPreviewRow(
                row.Name,
                Format(row.CurrentMask, row.CurrentLod),
                Format(row.ProposedMask, row.ProposedLod),
                row.Evidence,
                row.Status.ToString(),
                row.Status switch
                {
                    TerminalHelperPolicyRowStatus.Proposed => "Review this bind-inherited candidate before applying.",
                    TerminalHelperPolicyRowStatus.ExistingDecision => "Saved decision retained; this action will not overwrite it.",
                    _ => "No candidate is offered for this node.",
                })).ToImmutableArray();
            int count = proposal.Edits.Length;
            TerminalHelperPolicyStatus = $"Read-only preview: {count} candidate(s) from " +
                $"{unmatched.Count} unmatched stock-comparison node(s). " +
                "Constant embedded tracks may differ from fitted bind; review every proposed row. No edit was applied.";
            NotifyTerminalHelperPolicyState();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            InvalidateTerminalHelperPolicy("Terminal-helper preview failed: " + error.Message);
        }
    }

    private void ApplyTerminalHelperPolicy()
    {
        if (!CanApplyTerminalHelperPolicy || _model is not { } model ||
            _terminalHelperProposal is not { } proposal || _terminalUnmatchedIds is not { } unmatched ||
            TerminalHelperLodChoice is not { } lod)
            return;
        if (RigPoliciesApplyRequested is null)
        {
            TerminalHelperPolicyStatus = "No workspace commit handler is available.";
            return;
        }
        try
        {
            ImmutableArray<TerminalHelperFitObservation> observations =
                TerminalHelperChannelPolicyAuthoring.Observe(model, unmatched, lod.Lod);
            if (!TerminalHelperChannelPolicyAuthoring.TryApply(model.Package.Document, observations,
                    proposal, TerminalHelperPolicyReviewed, out RiggingSession updated))
            {
                InvalidateTerminalHelperPolicy("The source, fit, or saved policy changed. Preview terminal helpers again.");
                return;
            }
            RigPoliciesApplyRequested.Invoke(this, new(model, proposal.Token, updated));
            TerminalHelperPolicyStatus = $"Saved {proposal.Edits.Length} reviewed bind-inherited terminal-helper " +
                "candidate(s) as one undoable edit. Verify the exact compiled resource and native animation behavior.";
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            TerminalHelperPolicyStatus = "Terminal-helper decisions were not applied: " + error.Message;
        }
    }

    private static string Format(RigAnimationComponents? mask, RigAnimationLod? lod) =>
        mask is { } components && lod is { } value
            ? $"{Dl1BoneScriptPolicyResolver.FormatComponents(components)} · {Dl1BoneScriptPolicyResolver.FormatLod(value)}"
            : "Unset";
}

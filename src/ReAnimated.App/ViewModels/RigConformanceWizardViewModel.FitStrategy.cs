using System.Collections.Immutable;
using System.IO;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

public sealed record PlayerRigFitChoice(CustomModelRigFitStrategy Strategy, string Label);

public sealed partial class RigConformanceWizardViewModel
{
    private CustomModelRigFitStrategy _fitStrategy;
    private LegacyFitDraft? _legacyFitDraft;
    private ImmutableHashSet<string> _reviewedRoleOverrides = ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private sealed record LegacyFitDraft(string Profile, CustomModelConformanceScaleMode ScaleMode,
        double ManualScale, double Strength, ImmutableDictionary<string, string> Roles,
        ImmutableHashSet<string> ReviewedRoles);

    public ImmutableArray<PlayerRigFitChoice> PlayerRigFitChoices { get; } =
    [
        new(CustomModelRigFitStrategy.Legacy, "TPP – Keep proportions"),
        new(CustomModelRigFitStrategy.StockFppRegional, "FPP – Stock player rig"),
    ];
    public CustomModelRigFitStrategy FitStrategy => _fitStrategy;
    public bool IsStockFppRig => FitStrategy == CustomModelRigFitStrategy.StockFppRegional;
    public bool CanChangePlayerRigFit => HasModel && !IsBusy;
    public bool CanEditConformanceScale => !IsStockFppRig;
    public string PlayerRigFitSummary => IsStockFppRig
        ? "Fits the body and hands around the stock first-person skeleton."
        : ConformanceStrength <= .001 ? "Keeps this model's proportions for third-person fitting."
            : "Retains the saved scale and proportion settings.";
    public PlayerRigFitChoice SelectedPlayerRigFit
    {
        get => PlayerRigFitChoices.Single(choice => choice.Strategy == FitStrategy);
        set
        {
            if (value is null || value.Strategy == FitStrategy || IsBusy) return;
            _ = ChangePlayerRigFitAsync(value.Strategy);
        }
    }

    private void SetFitStrategy(CustomModelRigFitStrategy strategy)
    {
        if (!Enum.IsDefined(strategy)) throw new ArgumentOutOfRangeException(nameof(strategy));
        SetProperty(ref _fitStrategy, strategy, nameof(FitStrategy));
        NotifyFitStrategyState();
    }

    private void NotifyFitStrategyState()
    {
        OnPropertyChanged(nameof(FitStrategy));
        OnPropertyChanged(nameof(IsStockFppRig));
        OnPropertyChanged(nameof(SelectedPlayerRigFit));
        OnPropertyChanged(nameof(CanChangePlayerRigFit));
        OnPropertyChanged(nameof(CanEditConformanceScale));
        OnPropertyChanged(nameof(PlayerRigFitSummary));
    }

    private async Task ChangePlayerRigFitAsync(CustomModelRigFitStrategy strategy)
    {
        if (_model is null) return;
        if (strategy == CustomModelRigFitStrategy.StockFppRegional)
            _legacyFitDraft = new(TemplateProfileName, ScaleMode, ManualScale, ConformanceStrength,
                _roleOverrides, _reviewedRoleOverrides);
        bool setting = _settingModel;
        _settingModel = true;
        try
        {
            SetFitStrategy(strategy);
            if (IsStockFppRig)
            {
                TemplateProfileName = "player_fpp";
                ScaleMode = CustomModelConformanceScaleMode.Manual;
                ManualScale = 1;
                ConformanceStrength = 1;
                SeedStockSourceRoles();
            }
            else
            {
                TemplateProfileName = _legacyFitDraft?.Profile ?? "player";
                ScaleMode = _legacyFitDraft?.ScaleMode ?? CustomModelConformanceScaleMode.Automatic;
                ManualScale = _legacyFitDraft?.ManualScale ?? 1;
                ConformanceStrength = _legacyFitDraft?.Strength ?? 0;
                if (_legacyFitDraft is { } legacy)
                {
                    _roleOverrides = legacy.Roles;
                    _reviewedRoleOverrides = legacy.ReviewedRoles;
                }
            }
            _template = null;
            Fit = null;
            CancelPendingFitSolve();
            Interlocked.Increment(ref _fitInputRevision);
            Interlocked.Exchange(ref _publishedFitRevision, -1);
        }
        finally { _settingModel = setting; }
        NotifyStateChanged();
        FitChanged?.Invoke(this, EventArgs.Empty);
        await ResolvePlayerRigFitAsync();
    }

    private async Task ResolvePlayerRigFitAsync()
    {
        var source = _model;
        CustomModelRigFitStrategy strategy = FitStrategy;
        string profile = TemplateProfileName;
        try
        {
            await ResolveTemplateAsync(CancellationToken.None);
            if (ReferenceEquals(source, _model) && strategy == FitStrategy && profile == TemplateProfileName &&
                !_sourceConformanceReviewRequired && HasPendingMappingReview)
            {
                AcceptMappingProposals(markReviewed: false);
                await StartSolveAsync(debounce: false, CancellationToken.None);
            }
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException)
        {
            SolveStatus = "The player rig could not be loaded: " + error.Message;
            NotifyStateChanged();
        }
    }

    private bool IsStockNativeNode(string name) => IsStockFppRig &&
        _template?.Entities.Any(node => node.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == true;

    private bool IsRequiredFitRole(string role) => CoreRoles.Contains(role, StringComparer.Ordinal) &&
        (!IsStockFppRig || role.StartsWith("arm.", StringComparison.Ordinal) ||
            role.StartsWith("hand.", StringComparison.Ordinal));

    private void SeedStockSourceRoles()
    {
        if (!IsStockFppRig || _model is null || _sourceConformanceReviewRequired) return;
        var proposals = Dl1StockFppModelFitter.ProposeSourceRoleOverrides(_model.Package.Document);
        bool Reviewed(string role) => _reviewedRoleOverrides.Contains(role) || _pendingRoleOverride?.Role == role;
        foreach (var proposal in proposals)
        {
            if (Reviewed(proposal.Key)) continue;
            if (_roleOverrides.Any(pair => pair.Key != proposal.Key && Reviewed(pair.Key) &&
                pair.Value.Equals(proposal.Value, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (string role in _roleOverrides.Where(pair => pair.Key != proposal.Key && !Reviewed(pair.Key) &&
                pair.Value.Equals(proposal.Value, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key).ToArray())
                _roleOverrides = _roleOverrides.Remove(role);
            _roleOverrides = _roleOverrides.SetItem(proposal.Key, proposal.Value);
        }
    }

    private static ImmutableDictionary<string, Vector3D> EffectivePositionOverrides(Dl1RigTemplate template,
        CustomModelRigFitStrategy strategy, ImmutableDictionary<string, Vector3D> overrides) =>
        strategy == CustomModelRigFitStrategy.StockFppRegional
            ? overrides.Where(pair => !template.Entities.Any(node =>
                node.Name.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                .ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : overrides;

    private static RigLandmarkOptions CreateLandmarkOptions(Dl1RigTemplate template,
        CustomModelRigFitStrategy strategy, CustomModelConformanceScaleMode scaleMode, double manualScale)
    {
        if (strategy == CustomModelRigFitStrategy.StockFppRegional)
        {
            int pelvis = template.TryFindByRole("body.pelvis") ??
                throw new InvalidDataException("The stock first-person reference has no pelvis anchor.");
            return new() { ScaleOverride = 1, PelvisAnchorOverride = template[pelvis].GlobalRestMatrix.Translation };
        }
        return new()
        {
            ScaleOverride = scaleMode == CustomModelConformanceScaleMode.Manual ? manualScale : null,
            RestrictScaleToRegion = scaleMode switch
            {
                CustomModelConformanceScaleMode.Leg => RigScaleRegion.Leg,
                CustomModelConformanceScaleMode.Torso => RigScaleRegion.Torso,
                CustomModelConformanceScaleMode.Arm => RigScaleRegion.Arm,
                _ => null,
            },
        };
    }

    private CustomModelRigConformance ApplyStrategySettings(CustomModelRigConformance settings)
    {
        CustomModelRigConformance? saved = _model?.Package.Document.RigConformance;
        return settings with
        {
            FitStrategy = FitStrategy,
            ScaleMode = IsStockFppRig ? CustomModelConformanceScaleMode.Manual : settings.ScaleMode,
            ManualScale = IsStockFppRig ? 1 : settings.ManualScale,
            ConformanceStrength = IsStockFppRig ? 1 : settings.ConformanceStrength,
            StockFppNativeBoneCount = IsStockFppRig && saved is { FitStrategy: CustomModelRigFitStrategy.StockFppRegional }
                ? saved.StockFppNativeBoneCount : 0,
            StockFppNativeFrameSignature = IsStockFppRig && saved is { FitStrategy: CustomModelRigFitStrategy.StockFppRegional }
                ? saved.StockFppNativeFrameSignature : null,
            PositionOverrides = IsStockFppRig && _template is { } template
                ? settings.PositionOverrides.Where(row => !template.Entities.Any(node =>
                    node.Name.Equals(row.BoneName, StringComparison.OrdinalIgnoreCase))).ToImmutableArray()
                : settings.PositionOverrides,
        };
    }
}

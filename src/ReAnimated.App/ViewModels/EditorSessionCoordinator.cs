using ReAnimated.Core.Project;

namespace ReAnimated.App.ViewModels;

public enum EditorWorkspaceMode
{
    Browse,
    Animate,
    RetargetEdit,
    Face,
    Fpp,
}

public enum PreviewLayoutMode
{
    IsolatedBrowse,
    SingleAuthoritative,
    RetargetComparison,
    FacialComparison,
    FppDualView,
}

public readonly record struct TargetTransitionToken(
    long Generation,
    Guid? AnimationId,
    int FrozenFrame);

public readonly record struct PreviewPublicationToken(
    long Sequence,
    long SessionGeneration,
    Guid AnimationId,
    string SourceFingerprint,
    string TargetFingerprint,
    string? MappingFingerprint,
    int Frame);

public sealed record PreviewFramePair(
    PreviewPublicationToken Token,
    long SourceSceneGeneration,
    long TargetSceneGeneration);

public sealed record EditorSessionBinding(
    string SourceFingerprint,
    string TargetFingerprint,
    string? MappingFingerprint)
{
    public EditorSessionBinding Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetFingerprint);
        return this;
    }
}

public sealed record EditorSessionState(
    long Generation,
    Guid? ActiveAnimationId,
    AnimationVariantKey? ActiveVariant,
    TargetBindingStatus TargetStatus,
    EditorSessionBinding? Binding,
    int Frame,
    bool IsPlaying,
    bool IsTargetTransitioning,
    PreviewFramePair? LastPublishedFrame)
{
    public static EditorSessionState Empty { get; } = new(
        0,
        null,
        null,
        TargetBindingStatus.Invalid,
        null,
        0,
        false,
        false,
        null);
}

/// <summary>
/// Small thread-safe state machine which makes target activation and preview
/// publication generation checked. The WPF view model still owns presentation;
/// this coordinator owns admission and rejects stale async completions.
/// </summary>
public sealed class EditorSessionCoordinator
{
    private readonly object _gate = new();
    private EditorSessionState _state = EditorSessionState.Empty;
    private long _previewSequence;

    public EditorSessionState Current
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public TargetTransitionToken BeginTargetTransition(
        Guid? animationId,
        int frozenFrame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frozenFrame);
        lock (_gate)
        {
            long generation = checked(_state.Generation + 1);
            _state = _state with
            {
                Generation = generation,
                ActiveAnimationId = animationId,
                Frame = frozenFrame,
                IsPlaying = false,
                IsTargetTransitioning = true,
            };
            return new TargetTransitionToken(
                generation,
                animationId,
                frozenFrame);
        }
    }

    public bool TryCommitTargetTransition(
        TargetTransitionToken token,
        Guid? animationId,
        AnimationVariantKey? variant,
        TargetBindingStatus status) =>
        TryCommitTargetTransition(
            token,
            animationId,
            variant,
            status,
            commitAction: null);

    public bool TryCommitTargetTransition(
        TargetTransitionToken token,
        Guid? animationId,
        AnimationVariantKey? variant,
        TargetBindingStatus status,
        Action? commitAction,
        EditorSessionBinding? binding = null,
        bool isPlaying = false)
    {
        lock (_gate)
        {
            if (!IsCurrent(token))
            {
                return false;
            }

            // The caller publishes decoded UI state and commits the immutable
            // project inside this generation gate. If it throws, the session
            // remains in the transitioning state so the caller can cancel it
            // without admitting a partial coordinator commit.
            commitAction?.Invoke();
            binding?.Validate();
            _state = _state with
            {
                ActiveAnimationId = animationId,
                ActiveVariant = variant,
                TargetStatus = status,
                Binding = binding,
                IsPlaying = isPlaying,
                IsTargetTransitioning = false,
            };
            return true;
        }
    }

    public bool TryCancelTargetTransition(TargetTransitionToken token)
    {
        lock (_gate)
        {
            if (!IsCurrent(token))
            {
                return false;
            }

            _state = _state with
            {
                IsTargetTransitioning = false,
            };
            return true;
        }
    }

    public void Reset(
        Guid? animationId = null,
        int frame = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        lock (_gate)
        {
            _state = EditorSessionState.Empty with
            {
                Generation = checked(_state.Generation + 1),
                ActiveAnimationId = animationId,
                Frame = frame,
            };
        }
    }

    public void SynchronizeSession(
        Guid? animationId,
        AnimationVariantKey? variant,
        EditorSessionBinding? binding,
        TargetBindingStatus status,
        int frame,
        bool isPlaying)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        binding?.Validate();
        lock (_gate)
        {
            if (_state.IsTargetTransitioning)
            {
                return;
            }

            bool identityChanged =
                _state.ActiveAnimationId != animationId ||
                _state.ActiveVariant != variant ||
                _state.Binding != binding;
            _state = _state with
            {
                Generation = identityChanged
                    ? checked(_state.Generation + 1)
                    : _state.Generation,
                ActiveAnimationId = animationId,
                ActiveVariant = variant,
                Binding = binding,
                TargetStatus = status,
                Frame = frame,
                IsPlaying = isPlaying,
                LastPublishedFrame = identityChanged
                    ? null
                    : _state.LastPublishedFrame,
            };
        }
    }

    public void SynchronizeTimeline(
        Guid? animationId,
        int frame,
        bool isPlaying)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        lock (_gate)
        {
            if (_state.IsTargetTransitioning ||
                _state.ActiveAnimationId != animationId)
            {
                return;
            }

            _state = _state with
            {
                Frame = frame,
                IsPlaying = isPlaying,
            };
        }
    }

    public void UpdateTargetStatus(TargetBindingStatus status)
    {
        lock (_gate)
        {
            _state = _state with
            {
                TargetStatus = status,
            };
        }
    }

    public PreviewPublicationToken CreatePublicationToken(
        Guid animationId,
        string sourceFingerprint,
        string targetFingerprint,
        string? mappingFingerprint,
        int frame)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFingerprint);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        lock (_gate)
        {
            return new PreviewPublicationToken(
                checked(++_previewSequence),
                _state.Generation,
                animationId,
                sourceFingerprint,
                targetFingerprint,
                mappingFingerprint,
                frame);
        }
    }

    public bool TryPublishFrame(PreviewFramePair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        lock (_gate)
        {
            if (pair.Token.SessionGeneration != _state.Generation ||
                pair.Token.AnimationId != _state.ActiveAnimationId ||
                pair.Token.Sequence <
                    (_state.LastPublishedFrame?.Token.Sequence ?? 0))
            {
                return false;
            }

            _state = _state with
            {
                Frame = pair.Token.Frame,
                LastPublishedFrame = pair,
            };
            return true;
        }
    }

    private bool IsCurrent(TargetTransitionToken token) =>
        token.Generation == _state.Generation &&
        token.AnimationId == _state.ActiveAnimationId &&
        _state.IsTargetTransitioning;
}

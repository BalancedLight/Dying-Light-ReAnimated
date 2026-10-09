using CommunityToolkit.Mvvm.Input;
using System.Collections.Immutable;
using System.IO;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.App.Infrastructure;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private AsyncRelayCommand? _saveAnimationToModelCommand;
    internal Func<Func<AnimationClip>, CancellationToken, Task<AnimationClip>> ModelAnimationCaptureScheduler { get; set; } =
        static (work, token) => Task.Run(work, token);
    public IAsyncRelayCommand SaveAnimationToModelCommand => _saveAnimationToModelCommand ??=
        new AsyncRelayCommand(SaveAnimationToModelAsync,
            () => !IsBusy && !Models.IsBusy && TryGetModelAnimationCapture(out _, out _, out _));

    public bool HasModelAnimationCapture => TryGetModelAnimationCapture(out _, out _, out _);

    private void InitializeModelAnimationCapture()
    {
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IsBusy) or nameof(ActiveAnimationLabel) or nameof(ActiveTargetModelLabel))
                NotifyModelAnimationCapture();
        };
        Models.PersistenceStateChanged += (_, _) => NotifyModelAnimationCapture();
    }

    private void NotifyModelAnimationCapture()
    {
        OnPropertyChanged(nameof(HasModelAnimationCapture));
        _saveAnimationToModelCommand?.NotifyCanExecuteChanged();
    }

    private bool TryGetModelAnimationCapture(out FbxModelAuthoringImportResult? model,
        out ProjectAnimation? animation, out Guid clipId)
    {
        model = Models.CaptureProjectSession().Model;
        animation = GetActiveAnimation();
        clipId = Guid.Empty;
        if (model?.Rig is null || animation is null || _sourceAnimation is null || _targetRig is null ||
            _targetProjectAsset?.ResourceId is not { } targetIdentity ||
            !TryParseCustomModelResourceId(targetIdentity, out Guid ownerId) || ownerId != model.Package.Document.ModelId ||
            !HasSameRigContract(model.Rig, _targetRig)) return false;
        Guid animationId = animation.Id;
        ProjectAnimationVariant? variant = _project.AnimationVariants.FirstOrDefault(item => item.Id == animationId);
        ProjectAnimationSource? source = variant is null ? null : _project.AnimationSources.FirstOrDefault(item => item.Id == variant.SourceId);
        if (source?.EmbeddedCustomModelStack is not { } embedded) return false;
        ProjectAssetReference? sourceAsset = FindProjectAsset(source.SourceAssetId);
        if (sourceAsset?.ResourceId is not { } sourceIdentity ||
            !TryParseCustomModelResourceId(sourceIdentity, out Guid sourceOwnerId) || sourceOwnerId != ownerId)
            return false;
        clipId = embedded.ClipId;
        Guid selectedClipId = clipId;
        if (_sourceModelContext?.CustomPreviewSession?.Document.AnimationClips.Any(clip =>
            clip.Id == selectedClipId && clip.FbxObjectId == embedded.FbxObjectId &&
            string.Equals(clip.SourceFingerprint, embedded.StackFingerprint, StringComparison.OrdinalIgnoreCase)) != true)
            return false;
        return model.Package.Document.AnimationClips.Any(clip => clip.Id == selectedClipId && clip.AuthoredAnimation is not null &&
            clip.FbxObjectId == embedded.FbxObjectId &&
            string.Equals(clip.SourceFingerprint, embedded.StackFingerprint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task SaveAnimationToModelAsync()
    {
        if (!TryGetModelAnimationCapture(out var model, out var animation, out Guid originalClipId) || IsBusy || Models.IsBusy)
            return;
        DlraProject project = _project;
        long revision = Models.PersistenceRevision;
        string baseName = animation!.Name + " edited";
        string name = baseName;
        for (int suffix = 2; model!.Package.Document.AnimationClips.Any(clip =>
            string.Equals(clip.DisplayName, name, StringComparison.OrdinalIgnoreCase)); suffix++) name = baseName + " " + suffix;
        JobViewModel job = AddJob("Save animation to model", "Animation", "Sampling authored keys");
        IsBusy = true;
        Timeline.IsPlaying = false;
        try
        {
            EvaluationRequest template = CreateEvaluationRequest(animation, 0, _project.PreviewProfile,
                PlaybackMode.Clamp, EvaluationPurpose.Export);
            AnimationClip samples = await ModelAnimationCaptureScheduler(
                () => CaptureModelAnimation(template, animation, name, job.CancellationToken), job.CancellationToken);
            job.CancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(project, _project) || revision != Models.PersistenceRevision ||
                !ReferenceEquals(model, Models.CaptureProjectSession().Model) || _activeAnimationId != animation.Id)
            {
                job.Complete("Superseded");
                StatusText = "The animation or model changed. Save it again when ready.";
                return;
            }
            Models.CaptureEditedAnimation(model!, originalClipId, name, samples);
            job.Progress = 100;
            job.Complete("Complete");
            StatusText = $"Saved {name} to Models.";
        }
        catch (OperationCanceledException) { job.Complete("Canceled"); StatusText = "Animation save canceled."; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or OverflowException)
        { job.Complete("Failed"); StatusText = $"Animation could not be saved to the model: {error.Message}"; }
        finally { IsBusy = false; NotifyModelAnimationCapture(); }
    }

    private static AnimationClip CaptureModelAnimation(EvaluationRequest template, ProjectAnimation animation,
        string name, CancellationToken cancellationToken)
    {
        int count = checked((int)animation.FrameCount);
        long auxiliaryKeys = template.Clip.AuxiliaryTransformTracks.Sum(static track => (long)track.Keyframes.Length);
        long total = checked((long)count * (template.TargetRig.BoneCount + template.TargetRig.MorphChannels.Length) + auxiliaryKeys);
        if (count < 2 || count > ushort.MaxValue || total > FbxAuthoredAnimationAuthoring.MaximumCapturedKeyframeCount)
            throw new InvalidOperationException("This animation exceeds the model clip sample limit.");
        var transforms = Enumerable.Range(0, template.TargetRig.BoneCount)
            .Select(_ => ImmutableArray.CreateBuilder<TransformKeyframe>(count)).ToArray();
        var morphs = template.TargetRig.MorphChannels.Select(channel =>
            (channel.Name, Keys: ImmutableArray.CreateBuilder<ScalarKeyframe>(count))).ToArray();
        var evaluator = new AnimationEvaluator();
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new EvaluationRequest(template.SourceRig, template.TargetRig, template.Clip,
                animation.FrameRate.SecondsForFrame(index), template.PreviewProfile, template.RetargetMap,
                template.EditLayers, template.IkConstraints, PlaybackMode.Clamp, EvaluationPurpose.Export,
                template.Attachments, template.Dl1AuthoringPolicy, template.MorphBindings, template.MorphEditLayers,
                template.IkLayers, template.Dl1PreviewInputs, directRigBinding: template.DirectRigBinding);
            EvaluationFrame frame = evaluator.Evaluate(request);
            for (int bone = 0; bone < transforms.Length; bone++)
                transforms[bone].Add(new(index, frame.AuthoredPose.LocalTransforms[bone]));
            foreach (var morph in morphs) morph.Keys.Add(new(index, frame.AuthoredMorphWeights.GetValueOrDefault(morph.Name)));
        }
        return new AnimationClip(name, animation.FrameRate, count,
            transforms.Select((keys, bone) => new TransformTrack(bone, keys.MoveToImmutable())),
            morphs.Select(morph => new ScalarTrack(morph.Name, morph.Keys.MoveToImmutable())),
            template.Clip.AuxiliaryTransformTracks);
    }
}

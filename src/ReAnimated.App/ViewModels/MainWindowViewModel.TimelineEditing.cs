using System.Collections.Immutable;
using System.Globalization;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;
using ReAnimated.Retargeting.Mapping;

namespace ReAnimated.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _handlingTimelineTrackSelection;
    private bool _timelineLayerCreationPending;

    private void InitializeTimelineEditing()
    {
        Timeline.SelectedTrackChanged += OnTimelineSelectedTrackChanged;
        Timeline.KeyEditRequested += OnTimelineKeyEditRequested;
        Timeline.EditLayerRequested += OnTimelineEditLayerRequested;
        UpdateTimelineAuthoringAvailability();
    }

    private void OnTimelineSelectedTrackChanged(object? sender, EventArgs args)
    {
        if (_handlingTimelineTrackSelection)
            return;

        _handlingTimelineTrackSelection = true;
        try
        {
            if (TryGetSelectedTargetBone(out SkeletonNodeViewModel? bone))
            {
                if (bone is not null && !ReferenceEquals(SelectedBone, bone))
                    SelectedBone = bone;
            }
            UpdateTimelineAuthoringAvailability();
        }
        finally
        {
            _handlingTimelineTrackSelection = false;
        }
    }

    private void UpdateTimelineAuthoringAvailability()
    {
        bool canAdd = !IsBusy && !Models.IsBusy && GetActiveAnimation() is not null && (TryResolveTimelineBone(Timeline.SelectedTrackId, out _) ||
            TryResolveTimelineMorph(Timeline.SelectedTrackId, out _));
        bool canCreateLayer = !_timelineLayerCreationPending && !IsBusy && !Models.IsBusy && GetActiveAnimation() is not null && (
            TryResolveSourceTransformTrack(Timeline.SelectedTrackId, out _, out _) ||
            TryResolveSourceScalarTrack(Timeline.SelectedTrackId, out _, out _));
        Timeline.SetAuthoringAvailability(canAdd, canCreateLayer);
    }

    private void OnTimelineKeyEditRequested(object? sender, TimelineKeyEditRequestEventArgs request)
    {
        request.Handled = true;
        try
        {
            ProjectAnimation? active = GetActiveAnimation();
            if (active is null || !double.IsFinite(request.OriginalFrame) || !double.IsFinite(request.Frame) ||
                request.Frame < Timeline.StartFrame + Timeline.SourceFrameOffset ||
                request.Frame > Timeline.EndFrame + Timeline.SourceFrameOffset)
                throw new InvalidOperationException("Choose a frame inside the active clip.");

            if (TryParseAuthoredBoneTrack(request.TrackId, out Guid layerId, out int boneIndex))
                EditBoneCurveKey(request, layerId, boneIndex);
            else if (TryParseMorphTrack(request.TrackId, out layerId, out string? morphName))
                EditMorphCurveKey(request, layerId, morphName!);
            else
                throw new InvalidOperationException("Select an authored bone or morph curve to edit its keys.");

            request.Success = true;
            Timeline.SetKeyEditFeedback(request.Delete ? "Key deleted" : "Key updated");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            request.Success = false;
            request.Error = exception.Message;
            Timeline.SetKeyEditFeedback(exception.Message);
        }
    }

    private void EditBoneCurveKey(TimelineKeyEditRequestEventArgs request, Guid layerId, int boneIndex)
    {
        if (!TryGetActiveAnimation(out ProjectAnimation animation, out int animationIndex))
            throw new InvalidOperationException("No project animation is active.");
        int layerIndex = IndexOf(animation.EditLayers, layerId);
        if (layerIndex < 0)
            throw new InvalidOperationException("The selected edit layer is no longer available.");
        BoneEditLayer layer = animation.EditLayers[layerIndex];
        if (!layer.Enabled || layer.Scope != BoneEditLayerScope.AuthoredExportable)
            throw new InvalidOperationException("Enable an authored edit layer before changing its keys.");
        int trackIndex = FindBoneTrack(layer.Tracks, boneIndex);
        if (trackIndex < 0)
            throw new InvalidOperationException("The selected bone curve is no longer available.");
        BoneEditTrack track = layer.Tracks[trackIndex];
        int keyIndex = FindTransformKey(track.Keyframes, request.OriginalFrame);
        if (keyIndex < 0)
            throw new InvalidOperationException("The key changed before the edit was committed.");

        ImmutableArray<TransformKeyframe> keys = track.Keyframes;
        ImmutableArray<BoneEditTrack> tracks;
        if (request.Delete)
        {
            tracks = RemoveBoneKey(layer.Tracks, trackIndex, keyIndex);
        }
        else
        {
            TransformTRS value = ApplyTransformComponent(keys[keyIndex].Value, request.Component, request.Value);
            ValidateEditedTransform(value);
            RejectFrameCollision(keys.Select(static key => key.Frame), keyIndex, request.Frame);
            keys = keys.SetItem(keyIndex, new TransformKeyframe(request.Frame, value))
                .OrderBy(static key => key.Frame).ToImmutableArray();
            tracks = layer.Tracks.SetItem(trackIndex, new BoneEditTrack(boneIndex, keys, track.Interpolation));
        }
        BoneEditLayer updatedLayer = new(layer.Id, layer.Name, layer.BlendMode, layer.Scope,
            layer.Weight, tracks, layer.Enabled, layer.BoneMask);
        CommitProject(WithUpdatedActiveAnimation(_project, animation with
        {
            EditLayers = animation.EditLayers.SetItem(layerIndex, updatedLayer),
        }, animationIndex));
        RefreshAnimationPreview();
    }

    private void EditMorphCurveKey(TimelineKeyEditRequestEventArgs request, Guid layerId, string morphName)
    {
        if (!TryGetActiveAnimation(out ProjectAnimation animation, out int animationIndex))
            throw new InvalidOperationException("No project animation is active.");
        int layerIndex = IndexOf(animation.MorphEditLayers, layerId);
        if (layerIndex < 0)
            throw new InvalidOperationException("The selected facial layer is no longer available.");
        MorphEditLayer layer = animation.MorphEditLayers[layerIndex];
        if (!layer.Enabled || layer.Scope != MorphEditLayerScope.AuthoredExportable)
            throw new InvalidOperationException("Enable an authored facial layer before changing its keys.");
        int trackIndex = FindMorphTrack(layer.Tracks, morphName);
        if (trackIndex < 0)
            throw new InvalidOperationException("The selected morph curve is no longer available.");
        MorphEditTrack track = layer.Tracks[trackIndex];
        int keyIndex = FindScalarKey(track.Keyframes, request.OriginalFrame);
        if (keyIndex < 0)
            throw new InvalidOperationException("The key changed before the edit was committed.");

        ImmutableArray<ScalarKeyframe> keys = track.Keyframes;
        ImmutableArray<MorphEditTrack> tracks;
        if (request.Delete)
        {
            tracks = RemoveMorphKey(layer.Tracks, trackIndex, keyIndex);
        }
        else
        {
            double value = request.Value ?? keys[keyIndex].Value;
            if (!double.IsFinite(value))
                throw new InvalidOperationException("The morph value must be finite.");
            RejectFrameCollision(keys.Select(static key => key.Frame), keyIndex, request.Frame);
            keys = keys.SetItem(keyIndex, new ScalarKeyframe(request.Frame, value))
                .OrderBy(static key => key.Frame).ToImmutableArray();
            tracks = layer.Tracks.SetItem(trackIndex, new MorphEditTrack(morphName, keys));
        }
        MorphEditLayer updatedLayer = new(layer.Id, layer.Name, layer.BlendMode, layer.Scope,
            layer.Weight, tracks, layer.Enabled);
        CommitProject(WithUpdatedActiveAnimation(_project, animation with
        {
            MorphEditLayers = animation.MorphEditLayers.SetItem(layerIndex, updatedLayer),
        }, animationIndex));
        RefreshAnimationPreview();
    }

    private async void OnTimelineEditLayerRequested(object? sender, EventArgs args)
    {
        if (_timelineLayerCreationPending || IsBusy || Models.IsBusy)
            return;

        _timelineLayerCreationPending = true;
        JobViewModel? job = null;
        IsBusy = true;
        Timeline.SetKeyEditFeedback("Creating edit layer…");
        StatusText = "Creating timeline edit layer…";
        UpdateTimelineAuthoringAvailability();
        try
        {
            if (!TryGetActiveAnimation(out ProjectAnimation animation, out int animationIndex) ||
                _sourceAnimation is not { } source || _targetRig is not { } target ||
                Timeline.SelectedTrackId is not { } selectedTrackId)
                throw new InvalidOperationException("Select a source track with a valid target mapping.");

            DlraProject projectSnapshot = _project;
            Guid? animationId = _activeAnimationId;
            long persistenceRevision = Models.PersistenceRevision;
            TransformTrack? transformTrack = null;
            int targetBone = -1;
            ScalarTrack? scalarTrack = null;
            (string Morph, double Weight, double Bias) morphMapping = default;
            bool isTransform = TryResolveSourceTransformTrack(selectedTrackId, out transformTrack, out targetBone);
            bool isScalar = !isTransform && TryResolveSourceScalarTrack(selectedTrackId, out scalarTrack, out morphMapping);
            if (!isTransform && !isScalar)
                throw new InvalidOperationException("Select a source track with a valid target mapping.");

            EvaluationRequest evaluation = CreateEvaluationRequest(animation, 0, _project.PreviewProfile,
                PlaybackMode.Clamp, EvaluationPurpose.Export);
            (double rangeStart, double rangeEnd) = GetMotionCopyBounds(evaluation);
            double currentFrame = Math.Clamp(Timeline.SourceFrameOffset + Timeline.PositionFrame,
                rangeStart, rangeEnd);
            Guid? selectedSequenceId = Timeline.Events.SelectedSequence?.Id;
            double sourceFrameOffset = Timeline.SourceFrameOffset;
            double timelineFrameRate = Timeline.FramesPerSecond;
            FrameRate frameRate = evaluation.Clip.FrameRate;
            Guid layerId = Guid.NewGuid();
            job = AddJob("Create edit layer", "Animation", "Evaluating source track");
            CancellationToken cancellationToken = job.CancellationToken;

            object createdLayer;
            if (isTransform)
            {
                BoneEditLayer built = await Task.Run(() => BuildBoneEditLayer(
                    transformTrack!, targetBone, evaluation, frameRate, rangeStart, rangeEnd, layerId,
                    cancellationToken, includeCurrentFrame: true, currentFrame: currentFrame), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                createdLayer = built;
            }
            else
            {
                MorphEditLayer built = await Task.Run(() => BuildMorphEditLayer(
                    scalarTrack!, morphMapping, rangeStart, rangeEnd, layerId, cancellationToken,
                    includeCurrentFrame: true, currentFrame: currentFrame, request: evaluation, frameRate: frameRate), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                createdLayer = built;
            }

            if (!ReferenceEquals(projectSnapshot, _project) || animationId != _activeAnimationId ||
                !ReferenceEquals(source, _sourceAnimation) || !ReferenceEquals(target, _targetRig) ||
                persistenceRevision != Models.PersistenceRevision ||
                Timeline.Events.SelectedSequence?.Id != selectedSequenceId ||
                Timeline.SourceFrameOffset != sourceFrameOffset ||
                Timeline.FramesPerSecond != timelineFrameRate ||
                !string.Equals(selectedTrackId, Timeline.SelectedTrackId, StringComparison.Ordinal))
            {
                job.Complete("Superseded");
                Timeline.SetKeyEditFeedback("The animation or target changed. Select the source track and try again.");
                StatusText = "Edit layer creation canceled because the animation changed.";
                return;
            }

            ProjectAnimation updated = isTransform
                ? animation with { EditLayers = animation.EditLayers.Add((BoneEditLayer)createdLayer) }
                : animation with { MorphEditLayers = animation.MorphEditLayers.Add((MorphEditLayer)createdLayer) };
            CommitProject(WithUpdatedActiveAnimation(_project, updated, animationIndex));
            RefreshAnimationPreview();
            string authoredTrackId = isTransform
                ? $"edit:{layerId:N}:{targetBone}"
                : $"morph:{layerId:N}:{morphMapping.Morph}";
            string component = isTransform ? "Translation X" : "Value";
            FocusTimelineTrack(authoredTrackId, currentFrame, component);
            job.Progress = 100;
            job.Complete("Complete");
            Timeline.SetKeyEditFeedback("Edit layer created");
            StatusText = "Timeline edit layer created.";
        }
        catch (OperationCanceledException)
        {
            job?.Complete("Canceled");
            Timeline.SetKeyEditFeedback("Layer creation canceled");
            StatusText = "Timeline edit layer creation canceled.";
        }
        catch (Exception exception)
        {
            job?.Complete("Failed");
            Timeline.SetKeyEditFeedback(exception.Message);
            StatusText = $"Edit layer could not be created: {exception.Message}";
        }
        finally
        {
            _timelineLayerCreationPending = false;
            if (job is { IsFinished: false }) job.Complete("Canceled");
            IsBusy = false;
            UpdateTimelineAuthoringAvailability();
        }
    }

    private Task CreateBoneLayerFromSource(TransformTrack sourceTrack, int targetBone)
    {
        return CreateSourceTimelineLayerAsync();
    }

    internal static BoneEditLayer BuildBoneEditLayer(
        TransformTrack sourceTrack,
        int targetBone,
        EvaluationRequest request,
        FrameRate frameRate,
        double rangeStart,
        double rangeEnd,
        Guid layerId,
        CancellationToken cancellationToken,
        bool includeCurrentFrame = false,
        double currentFrame = 0)
    {
        if ((uint)targetBone >= (uint)request.TargetRig.BoneCount)
            throw new InvalidOperationException("The mapped target bone is no longer available.");
        IEnumerable<double> frames = BuildMotionCopyFrames(
            request, sourceTrack.Keyframes.Select(static key => key.Frame),
            rangeStart, rangeEnd, includeCurrentFrame ? currentFrame : null);
        BoneEditInterpolation interpolation = GetMotionCopyInterpolation(request, targetBone);
        var sampled = new SortedDictionary<double, TransformTRS>();
        TransformTRS Sample(double frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvaluationRequest atFrame = new(request.SourceRig, request.TargetRig, request.Clip,
                frameRate.SecondsForFrame(frame), request.PreviewProfile, request.RetargetMap,
                request.EditLayers, request.IkConstraints, PlaybackMode.Clamp, EvaluationPurpose.Export,
                request.Attachments, request.Dl1AuthoringPolicy, request.MorphBindings, request.MorphEditLayers,
                request.IkLayers, request.Dl1PreviewInputs,
                previewMotionAccumulationEnabled: false,
                directRigBinding: request.DirectRigBinding);
            TransformTRS value = AnimationEvaluator.EvaluateAuthoredPoseBeforePostProcessing(atFrame)
                .LocalTransforms[targetBone];
            if (!value.IsFinite || value.Rotation.LengthSquared <= 1.0e-12 ||
                !BoneTransformAuthoringPolicy.IsValidScale(value.Scale))
                throw new InvalidOperationException("The evaluated target pose contains a transform that cannot be authored.");
            return value;
        }
        foreach (double frame in frames)
            sampled[frame] = Sample(frame);
        if (sampled.Count == 0)
            throw new InvalidOperationException("The selected source track has no keys in the active clip range.");

        RefineTransformSamples(sampled, Sample, interpolation, cancellationToken);
        ImmutableArray<TransformKeyframe> keys = sampled
            .Select(static pair => new TransformKeyframe(pair.Key, pair.Value))
            .ToImmutableArray();

        return new BoneEditLayer(layerId, "Timeline edit", BoneEditBlendMode.Override,
            BoneEditLayerScope.AuthoredExportable, 1, [new BoneEditTrack(targetBone, keys, interpolation)]);
    }

    internal static MorphEditLayer BuildMorphEditLayer(
        ScalarTrack sourceTrack,
        (string Morph, double Weight, double Bias) mapping,
        double rangeStart,
        double rangeEnd,
        Guid layerId,
        CancellationToken cancellationToken,
        bool includeCurrentFrame = false,
        double currentFrame = 0,
        EvaluationRequest? request = null,
        FrameRate? frameRate = null)
    {
        IEnumerable<double> frames = BuildMotionCopyFrames(
            request, sourceTrack.Keyframes.Select(static key => key.Frame),
            rangeStart, rangeEnd, includeCurrentFrame ? currentFrame : null);
        var sampled = new SortedDictionary<double, double>();
        AnimationEvaluator? evaluator = request is null ? null : new AnimationEvaluator();
        double Sample(double frame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double value;
            if (request is not null && evaluator is not null && frameRate is { } rate)
            {
                EvaluationRequest atFrame = new(request.SourceRig, request.TargetRig, request.Clip,
                    rate.SecondsForFrame(frame), request.PreviewProfile, request.RetargetMap,
                    request.EditLayers, request.IkConstraints, PlaybackMode.Clamp, EvaluationPurpose.Export,
                    request.Attachments, request.Dl1AuthoringPolicy, request.MorphBindings,
                    request.MorphEditLayers, request.IkLayers, request.Dl1PreviewInputs,
                    previewMotionAccumulationEnabled: false,
                    directRigBinding: request.DirectRigBinding);
                EvaluationFrame evaluated = evaluator.Evaluate(atFrame);
                if (!evaluated.AuthoredMorphWeights.TryGetValue(mapping.Morph, out value))
                    value = 0;
            }
            else
            {
                value = sourceTrack.Sample(frame) * mapping.Weight + mapping.Bias;
            }
            if (!double.IsFinite(value))
                throw new InvalidOperationException("The evaluated facial weight cannot be authored.");
            return value;
        }
        foreach (double frame in frames)
            sampled[frame] = Sample(frame);
        if (sampled.Count == 0)
            throw new InvalidOperationException("The selected source track has no keys in the active clip range.");

        RefineScalarSamples(sampled, Sample, cancellationToken);
        ImmutableArray<ScalarKeyframe> keys = sampled
            .Select(static pair => new ScalarKeyframe(pair.Key, pair.Value))
            .ToImmutableArray();

        return new MorphEditLayer(layerId, "Timeline edit", MorphEditBlendMode.Override,
            MorphEditLayerScope.AuthoredExportable, 1, [new MorphEditTrack(mapping.Morph, keys)]);
    }

    private static void RefineTransformSamples(
        SortedDictionary<double, TransformTRS> samples,
        Func<double, TransformTRS> evaluate,
        BoneEditInterpolation interpolation,
        CancellationToken cancellationToken)
    {
        const int maximumKeys = 65_536;
        const int maximumDepth = 16;
        void Refine(double leftFrame, double rightFrame, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rightFrame - leftFrame <= 1.0e-6)
                return;
            double middleFrame = leftFrame + ((rightFrame - leftFrame) * 0.5);
            if (middleFrame <= leftFrame || middleFrame >= rightFrame)
                return;
            var current = new BoneEditTrack(0,
                new TransformKeyframe[]
                {
                    new(leftFrame, samples[leftFrame]),
                    new(rightFrame, samples[rightFrame]),
                }, interpolation);
            double span = rightFrame - leftFrame;
            var additions = new List<(double Frame, TransformTRS Value)>();
            TransformTRS actualMiddle = evaluate(middleFrame);
            if (TransformCopyError(current.Sample(middleFrame), actualMiddle) > 1.0e-6)
            {
                additions.Add((middleFrame, actualMiddle));
            }
            else
            {
                double quarterFrame = leftFrame + (span * 0.25);
                double threeQuarterFrame = leftFrame + (span * 0.75);
                TransformTRS actualQuarter = evaluate(quarterFrame);
                TransformTRS actualThreeQuarter = evaluate(threeQuarterFrame);
                if (TransformCopyError(current.Sample(quarterFrame), actualQuarter) > 1.0e-6)
                    additions.Add((quarterFrame, actualQuarter));
                if (TransformCopyError(current.Sample(threeQuarterFrame), actualThreeQuarter) > 1.0e-6)
                    additions.Add((threeQuarterFrame, actualThreeQuarter));
            }
            if (additions.Count == 0)
                return;
            if (depth >= maximumDepth || samples.Count + additions.Count > maximumKeys)
                throw new InvalidOperationException("This motion cannot be represented within the curve sampling limit.");
            foreach ((double frame, TransformTRS value) in additions)
                samples[frame] = value;
            double[] splitPoints = [leftFrame, .. additions.Select(static item => item.Frame).Order(), rightFrame];
            for (int index = 0; index + 1 < splitPoints.Length; index++)
                Refine(splitPoints[index], splitPoints[index + 1], depth + 1);
        }

        double[] initial = samples.Keys.ToArray();
        for (int index = 0; index + 1 < initial.Length; index++)
            Refine(initial[index], initial[index + 1], 0);
    }

    private static double TransformCopyError(TransformTRS predicted, TransformTRS actual)
    {
        double translation = Math.Max(
            Math.Max(Math.Abs(predicted.Translation.X - actual.Translation.X), Math.Abs(predicted.Translation.Y - actual.Translation.Y)),
            Math.Abs(predicted.Translation.Z - actual.Translation.Z));
        double scale = Math.Max(
            Math.Max(Math.Abs(predicted.Scale.X - actual.Scale.X), Math.Abs(predicted.Scale.Y - actual.Scale.Y)),
            Math.Abs(predicted.Scale.Z - actual.Scale.Z));
        double rotation = QuaternionAngle(predicted.Rotation, actual.Rotation);
        return Math.Max(translation, Math.Max(scale, rotation));
    }

    private static double QuaternionAngle(QuaternionD left, QuaternionD right)
    {
        QuaternionD a = left.Normalized();
        QuaternionD b = right.Normalized();
        double dot = Math.Clamp(Math.Abs((a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z) + (a.W * b.W)), 0, 1);
        return 2 * Math.Acos(dot);
    }

    internal static void RefineScalarSamples(
        SortedDictionary<double, double> samples,
        Func<double, double> evaluate,
        CancellationToken cancellationToken)
    {
        const int maximumKeys = 65_536;
        const int maximumDepth = 16;
        void Refine(double leftFrame, double rightFrame, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double middleFrame = leftFrame + ((rightFrame - leftFrame) * 0.5);
            if (middleFrame <= leftFrame || middleFrame >= rightFrame)
                return;
            double span = rightFrame - leftFrame;
            var additions = new List<(double Frame, double Value)>();
            double actualMiddle = evaluate(middleFrame);
            double midpointPrediction = samples[leftFrame] +
                ((samples[rightFrame] - samples[leftFrame]) * 0.5);
            if (Math.Abs(midpointPrediction - actualMiddle) > 1.0e-6)
            {
                additions.Add((middleFrame, actualMiddle));
            }
            else
            {
                double quarterFrame = leftFrame + (span * 0.25);
                double threeQuarterFrame = leftFrame + (span * 0.75);
                double actualQuarter = evaluate(quarterFrame);
                double actualThreeQuarter = evaluate(threeQuarterFrame);
                double quarterPrediction = samples[leftFrame] +
                    ((samples[rightFrame] - samples[leftFrame]) * 0.25);
                double threeQuarterPrediction = samples[leftFrame] +
                    ((samples[rightFrame] - samples[leftFrame]) * 0.75);
                if (Math.Abs(quarterPrediction - actualQuarter) > 1.0e-6)
                    additions.Add((quarterFrame, actualQuarter));
                if (Math.Abs(threeQuarterPrediction - actualThreeQuarter) > 1.0e-6)
                    additions.Add((threeQuarterFrame, actualThreeQuarter));
            }
            if (additions.Count == 0)
                return;
            if (depth >= maximumDepth || samples.Count + additions.Count > maximumKeys)
                throw new InvalidOperationException("This facial motion cannot be represented within the curve sampling limit.");
            foreach ((double frame, double value) in additions)
                samples[frame] = value;
            double[] splitPoints = [leftFrame, .. additions.Select(static item => item.Frame).Order(), rightFrame];
            for (int index = 0; index + 1 < splitPoints.Length; index++)
                Refine(splitPoints[index], splitPoints[index + 1], depth + 1);
        }

        double[] initial = samples.Keys.ToArray();
        for (int index = 0; index + 1 < initial.Length; index++)
            Refine(initial[index], initial[index + 1], 0);
    }

    private Task CreateMorphLayerFromSource(ScalarTrack sourceTrack, (string Morph, double Weight, double Bias) mapping)
    {
        return CreateSourceTimelineLayerAsync();
    }

    private async Task CreateSourceTimelineLayerAsync()
    {
        if (_timelineLayerCreationPending || IsBusy || Models.IsBusy)
            return;

        _timelineLayerCreationPending = true;
        JobViewModel? job = null;
        IsBusy = true;
        Timeline.SetKeyEditFeedback("Copying evaluated motion…");
        StatusText = "Copying evaluated motion to an edit layer…";
        UpdateTimelineAuthoringAvailability();
        try
        {
            if (!TryGetActiveAnimation(out ProjectAnimation animation, out int animationIndex) ||
                _sourceAnimation is not { } source || _targetRig is not { } target ||
                Timeline.SelectedTrackId is not { } selectedTrackId)
                throw new InvalidOperationException("Select a source track with a valid target mapping.");

            DlraProject projectSnapshot = _project;
            Guid? animationId = _activeAnimationId;
            long persistenceRevision = Models.PersistenceRevision;
            bool isTransform = TryResolveSourceTransformTrack(selectedTrackId,
                out TransformTrack? sourceTransform, out int targetBone);
            ScalarTrack? sourceScalar = null;
            (string Morph, double Weight, double Bias) mapping = default;
            bool isMorph = !isTransform && TryResolveSourceScalarTrack(selectedTrackId,
                out sourceScalar, out mapping);
            if (!isTransform && !isMorph)
                throw new InvalidOperationException("Select a source track with a valid target mapping.");

            EvaluationRequest request = CreateEvaluationRequest(animation, 0, _project.PreviewProfile,
                PlaybackMode.Clamp, EvaluationPurpose.Export);
            (double rangeStart, double rangeEnd) = GetMotionCopyBounds(request);
            double currentFrame = Math.Clamp(Timeline.SourceFrameOffset + Timeline.PositionFrame,
                rangeStart, rangeEnd);
            Guid? selectedSequenceId = Timeline.Events.SelectedSequence?.Id;
            double sourceFrameOffset = Timeline.SourceFrameOffset;
            double timelineFrameRate = Timeline.FramesPerSecond;
            Guid layerId = Guid.NewGuid();
            job = AddJob("Copy evaluated motion", "Animation", "Sampling the clip");
            CancellationToken cancellationToken = job.CancellationToken;

            object layer = isTransform
                ? await Task.Run(() => BuildBoneEditLayer(sourceTransform!, targetBone, request,
                    request.Clip.FrameRate, rangeStart, rangeEnd, layerId, cancellationToken,
                    includeCurrentFrame: true, currentFrame: currentFrame), cancellationToken)
                : await Task.Run(() => BuildMorphEditLayer(sourceScalar!, mapping, rangeStart,
                    rangeEnd, layerId, cancellationToken, includeCurrentFrame: true, currentFrame: currentFrame,
                    request: request, frameRate: request.Clip.FrameRate), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(projectSnapshot, _project) || animationId != _activeAnimationId ||
                !ReferenceEquals(source, _sourceAnimation) || !ReferenceEquals(target, _targetRig) ||
                persistenceRevision != Models.PersistenceRevision ||
                Timeline.Events.SelectedSequence?.Id != selectedSequenceId ||
                Timeline.SourceFrameOffset != sourceFrameOffset ||
                Timeline.FramesPerSecond != timelineFrameRate ||
                !string.Equals(selectedTrackId, Timeline.SelectedTrackId, StringComparison.Ordinal))
            {
                job.Complete("Superseded");
                Timeline.SetKeyEditFeedback("The animation or target changed. Select the source track and try again.");
                StatusText = "Motion copy canceled because the animation changed.";
                return;
            }

            ProjectAnimation updated = isTransform
                ? animation with { EditLayers = animation.EditLayers.Add((BoneEditLayer)layer) }
                : animation with { MorphEditLayers = animation.MorphEditLayers.Add((MorphEditLayer)layer) };
            CommitProject(WithUpdatedActiveAnimation(_project, updated, animationIndex));
            RefreshAnimationPreview();
            string authoredTrackId = isTransform
                ? $"edit:{layerId:N}:{targetBone}"
                : $"morph:{layerId:N}:{mapping.Morph}";
            FocusTimelineTrack(authoredTrackId, currentFrame, isTransform ? "Translation X" : "Value");
            job.Progress = 100;
            job.Complete("Complete");
            Timeline.SetKeyEditFeedback("Edit layer created");
            StatusText = "Timeline edit layer created.";
        }
        catch (OperationCanceledException)
        {
            job?.Complete("Canceled");
            Timeline.SetKeyEditFeedback("Motion copy canceled");
            StatusText = "Timeline motion copy canceled.";
        }
        catch (Exception exception)
        {
            job?.Complete("Failed");
            Timeline.SetKeyEditFeedback(exception.Message);
            StatusText = $"Motion could not be copied: {exception.Message}";
        }
        finally
        {
            _timelineLayerCreationPending = false;
            if (job is { IsFinished: false }) job.Complete("Canceled");
            IsBusy = false;
            UpdateTimelineAuthoringAvailability();
        }
    }

    internal static ImmutableArray<double> BuildMotionCopyFrames(
        EvaluationRequest? request,
        IEnumerable<double> sourceFrames,
        double rangeStart,
        double rangeEnd,
        double? currentFrame = null)
    {
        if (!double.IsFinite(rangeStart) || !double.IsFinite(rangeEnd) || rangeEnd < rangeStart)
            throw new InvalidOperationException("The selected clip range is invalid.");

        var frames = new SortedSet<double> { rangeStart, rangeEnd };
        long firstGridFrame = checked((long)Math.Ceiling(rangeStart));
        long lastGridFrame = checked((long)Math.Floor(rangeEnd));
        if (lastGridFrame - firstGridFrame > 1_000_000)
            throw new InvalidOperationException("The selected range is too long to copy in one operation.");
        for (long frame = firstGridFrame; frame <= lastGridFrame; frame++)
            frames.Add(frame);

        foreach (double frame in sourceFrames)
            if (double.IsFinite(frame) && frame >= rangeStart && frame <= rangeEnd)
                frames.Add(frame);
        if (currentFrame is { } current && double.IsFinite(current) && current >= rangeStart && current <= rangeEnd)
            frames.Add(current);
        if (request is not null)
        {
            foreach (TransformTrack track in request.Clip.TransformTracks)
                foreach (TransformKeyframe key in track.Keyframes)
                    if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
            foreach (ScalarTrack track in request.Clip.ScalarTracks)
                foreach (ScalarKeyframe key in track.Keyframes)
                    if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
            foreach (AuxiliaryTransformTrack track in request.Clip.AuxiliaryTransformTracks)
                foreach (TransformKeyframe key in track.Keyframes)
                    if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
            foreach (BoneEditLayer layer in request.EditLayers.Where(static layer =>
                         layer.Enabled && layer.Scope == BoneEditLayerScope.AuthoredExportable && layer.Weight > 0))
                foreach (BoneEditTrack track in layer.Tracks)
                    foreach (TransformKeyframe key in track.Keyframes)
                        if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
            foreach (MorphEditLayer layer in request.MorphEditLayers.Where(static layer =>
                         layer.Enabled && layer.Scope == MorphEditLayerScope.AuthoredExportable && layer.Weight > 0))
                foreach (MorphEditTrack track in layer.Tracks)
                    foreach (ScalarKeyframe key in track.Keyframes)
                        if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
            foreach (ReAnimated.Retargeting.Ik.IkConstraintLayer layer in request.IkLayers.Where(static layer =>
                         layer.Enabled && layer.Scope == ReAnimated.Retargeting.Ik.IkConstraintScope.AuthoredExportable && layer.Weight > 0))
                foreach (ReAnimated.Retargeting.Ik.IkConstraintKeyframe key in layer.Keyframes)
                    if (key.Frame >= rangeStart && key.Frame <= rangeEnd) frames.Add(key.Frame);
        }
        return frames.ToImmutableArray();
    }

    internal static (double Start, double End) GetMotionCopyBounds(EvaluationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Clip.FrameCount <= 0)
            throw new InvalidOperationException("The active clip has no frames to copy.");
        return (0, request.Clip.FrameCount - 1);
    }

    private static BoneEditInterpolation GetMotionCopyInterpolation(EvaluationRequest request, int targetBone)
    {
        (BoneEditLayer Layer, BoneEditTrack Track, double EffectiveWeight)[] targetTracks = request.EditLayers
            .Where(static layer => layer.Enabled && layer.Scope == BoneEditLayerScope.AuthoredExportable && layer.Weight > 0)
            .SelectMany(layer => layer.Tracks
                .Where(track => track.BoneIndex == targetBone)
                .Select(track => (Layer: layer, Track: track,
                    EffectiveWeight: layer.Weight * (layer.BoneMask.TryGetValue(track.BoneIndex, out double maskWeight) ? maskWeight : 1))))
            .Where(static item => item.EffectiveWeight > 0)
            .ToArray();
        if (targetTracks.All(static item => item.Track.Interpolation == BoneEditInterpolation.Linear))
            return BoneEditInterpolation.Linear;
        if (targetTracks.Length == 1 &&
            targetTracks[0].Track.Interpolation == BoneEditInterpolation.Step &&
            targetTracks[0].Layer.BlendMode == BoneEditBlendMode.Override &&
            targetTracks[0].EffectiveWeight == 1)
            return BoneEditInterpolation.Step;
        throw new InvalidOperationException("This motion combines Step and continuous edits and cannot be copied as one curve.");
    }

    private void KeySelectedTimelineMorph(string morphName)
    {
        if (!TryGetActiveAnimation(out ProjectAnimation animation, out int animationIndex) || _targetRig is null)
            return;
        if (!TryParseMorphTrack(Timeline.SelectedTrackId, out Guid layerId, out _))
            return;
        int layerIndex = IndexOf(animation.MorphEditLayers, layerId);
        if (layerIndex < 0) return;
        MorphEditLayer layer = animation.MorphEditLayers[layerIndex];
        int trackIndex = FindMorphTrack(layer.Tracks, morphName);
        if (trackIndex < 0) return;
        MorphEditTrack track = layer.Tracks[trackIndex];
        double frame = Math.Clamp(Timeline.SourceFrameOffset + Timeline.PositionFrame,
            Timeline.StartFrame + Timeline.SourceFrameOffset, Timeline.EndFrame + Timeline.SourceFrameOffset);
        double value = track.Sample(frame);
        ImmutableArray<MorphEditTrack> tracks = layer.Tracks;
        ImmutableArray<ScalarKeyframe> keys = trackIndex >= 0 ? tracks[trackIndex].Keyframes : [];
        int keyIndex = FindScalarKey(keys, frame);
        var key = new ScalarKeyframe(frame, value);
        keys = keyIndex >= 0 ? keys.SetItem(keyIndex, key) : keys.Add(key).OrderBy(static item => item.Frame).ToImmutableArray();
        MorphEditTrack updatedTrack = new(morphName, keys);
        tracks = trackIndex >= 0 ? tracks.SetItem(trackIndex, updatedTrack) : tracks.Add(updatedTrack);
        MorphEditLayer updatedLayer = new(layer.Id, layer.Name, layer.BlendMode, layer.Scope,
            layer.Weight, tracks, layer.Enabled);
        CommitProject(WithUpdatedActiveAnimation(_project, animation with
        {
            MorphEditLayers = animation.MorphEditLayers.SetItem(layerIndex, updatedLayer),
        }, animationIndex));
        RefreshAnimationPreview();
    }

    private bool TryGetSelectedTargetBone(out SkeletonNodeViewModel? bone)
    {
        bone = null;
        if (TryResolveTimelineBone(Timeline.SelectedTrackId, out int targetIndex))
        {
            bone = FindBone(targetIndex);
            return bone is not null;
        }
        if (TryResolveSourceTransformTrack(Timeline.SelectedTrackId, out _, out targetIndex))
        {
            bone = FindBone(targetIndex);
            return bone is not null;
        }
        return false;
    }

    private bool TryResolveTimelineBone(string? trackId, out int targetIndex)
    {
        targetIndex = -1;
        if (TryParseAuthoredBoneTrack(trackId, out Guid layerId, out targetIndex))
        {
            int boneIndex = targetIndex;
            return GetActiveAnimation()?.EditLayers.Any(layer => layer.Id == layerId && layer.Enabled &&
                layer.Scope == BoneEditLayerScope.AuthoredExportable &&
                layer.Tracks.Any(track => track.BoneIndex == boneIndex)) == true &&
                _targetRig is { } target && (uint)boneIndex < (uint)target.BoneCount;
        }
        return TryResolveSourceTransformTrack(trackId, out _, out targetIndex);
    }

    private bool TryResolveSourceTransformTrack(string? trackId, out TransformTrack? sourceTrack, out int targetBone)
    {
        sourceTrack = null;
        targetBone = -1;
        if (GetActiveAnimation() is null || _sourceAnimation is not { } source || _targetRig is not { } target ||
            !TryParseIndex(trackId, "source-transform:", out int sourceBone)) return false;
        sourceTrack = source.Clip.TransformTracks.FirstOrDefault(track => track.BoneIndex == sourceBone);
        if (sourceTrack is null) return false;
        if (HasSameRigContract(source.Rig, target))
        {
            targetBone = sourceBone;
            return (uint)targetBone < (uint)target.BoneCount;
        }
        int[] mapped = _activeRetargetMap?.Entries.Where(entry => entry.SourceBoneIndex == sourceBone &&
                entry.MappingKind == RetargetMappingKind.Bone).Select(static entry => entry.TargetBoneIndex).Distinct().ToArray() ?? [];
        if (mapped.Length != 1) return false;
        targetBone = mapped[0];
        return (uint)targetBone < (uint)target.BoneCount;
    }

    private bool TryResolveSourceScalarTrack(string? trackId, out ScalarTrack? sourceTrack,
        out (string Morph, double Weight, double Bias) mapping)
    {
        sourceTrack = null;
        mapping = default;
        if (GetActiveAnimation() is null || _sourceAnimation is not { } source || _targetRig is not { } target || trackId is null ||
            !trackId.StartsWith("source-scalar:", StringComparison.Ordinal)) return false;
        string channel = trackId["source-scalar:".Length..];
        sourceTrack = source.Clip.ScalarTracks.FirstOrDefault(track =>
            string.Equals(track.ChannelName, channel, StringComparison.OrdinalIgnoreCase));
        if (sourceTrack is null) return false;
        ProjectMorphBinding[] explicitBindings = GetActiveAnimation()!.MorphBindings.Where(binding =>
            string.Equals(binding.SourceChannel, channel, StringComparison.OrdinalIgnoreCase)).ToArray();
        ProjectMorphBinding[] bindings = explicitBindings.Where(binding => binding.Enabled &&
            string.Equals(binding.SourceChannel, channel, StringComparison.OrdinalIgnoreCase) &&
            target.MorphChannels.Any(morph => string.Equals(morph.Name, binding.TargetMorph, StringComparison.OrdinalIgnoreCase))).ToArray() ?? [];
        if (bindings.Length == 1)
        {
            double unitScale = bindings[0].SourceValueUnit == ProjectMorphSourceValueUnit.Percent ? 0.01 : 1.0;
            mapping = (bindings[0].TargetMorph, bindings[0].Weight * unitScale, bindings[0].Bias);
            return true;
        }
        if (explicitBindings.Length > 0 || GetActiveAnimation()!.MorphBindings.Length > 0) return false;
        string[] exact = target.MorphChannels.Where(morph => string.Equals(morph.Name, channel, StringComparison.OrdinalIgnoreCase))
            .Select(static morph => morph.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (exact.Length != 1) return false;
        double directUnitScale = source.FacialSourceValueUnit == ProjectMorphSourceValueUnit.Percent ? 0.01 : 1.0;
        mapping = (exact[0], directUnitScale, 0);
        return true;
    }

    private bool TryResolveTimelineMorph(string? trackId, out string? morph)
    {
        morph = null;
        if (!TryParseMorphTrack(trackId, out Guid layerId, out morph) || morph is null) return false;
        string morphName = morph;
        return GetActiveAnimation()?.MorphEditLayers.Any(layer =>
            layer.Id == layerId && layer.Enabled && layer.Scope == MorphEditLayerScope.AuthoredExportable && layer.Tracks.Any(track =>
                string.Equals(track.MorphName, morphName, StringComparison.OrdinalIgnoreCase))) == true;
    }

    private void FocusTimelineTrack(string trackId, double frame, string component)
    {
        Timeline.SelectTrack(trackId);
        if (Timeline.SelectedTrackId != trackId)
        {
            Timeline.TrackSearchText = string.Empty;
            Timeline.SelectedTrackScope = "All";
            Timeline.SelectTrack(trackId);
        }
        Timeline.SelectKey(trackId, frame, component);
    }

    private static bool TryParseAuthoredBoneTrack(string? trackId, out Guid layerId, out int boneIndex)
    {
        layerId = Guid.Empty;
        boneIndex = -1;
        if (TryParseLayeredTrack(trackId, "bone:", out layerId, out string? index) ||
            TryParseLayeredTrack(trackId, "edit:", out layerId, out index))
            return int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out boneIndex) && boneIndex >= 0;
        return false;
    }

    private static bool TryParseMorphTrack(string? trackId, out Guid layerId, out string? morph)
    {
        morph = null;
        if (!TryParseLayeredTrack(trackId, "morph:", out layerId, out string? value) || string.IsNullOrWhiteSpace(value))
            return false;
        morph = value;
        return true;
    }

    private static bool TryParseLayeredTrack(string? trackId, string prefix, out Guid layerId, out string? remainder)
    {
        layerId = Guid.Empty;
        remainder = null;
        if (trackId is null || !trackId.StartsWith(prefix, StringComparison.Ordinal)) return false;
        int separator = trackId.IndexOf(':', prefix.Length);
        if (separator < 0 || !Guid.TryParseExact(trackId[prefix.Length..separator], "N", out layerId)) return false;
        remainder = trackId[(separator + 1)..];
        return true;
    }

    private static bool TryParseIndex(string? value, string prefix, out int index)
    {
        index = -1;
        return value is not null && value.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(value[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;
    }

    internal static TransformTRS ApplyTransformComponent(TransformTRS transform, string? component, double? value)
    {
        if (value is null) return transform;
        double v = value.Value;
        Vector3D t = transform.Translation, s = transform.Scale;
        QuaternionD q = transform.Rotation;
        switch (component)
        {
            case "Translation X": t = t with { X = v }; break;
            case "Translation Y": t = t with { Y = v }; break;
            case "Translation Z": t = t with { Z = v }; break;
            case "Scale X": s = s with { X = v }; break;
            case "Scale Y": s = s with { Y = v }; break;
            case "Scale Z": s = s with { Z = v }; break;
            case "Rotation X": q = q with { X = v }; break;
            case "Rotation Y": q = q with { Y = v }; break;
            case "Rotation Z": q = q with { Z = v }; break;
            case "Rotation W": q = q with { W = v }; break;
            default: throw new InvalidOperationException("Select a transform component curve.");
        }
        return new TransformTRS(t, q, s);
    }

    internal static void ValidateEditedTransform(TransformTRS value)
    {
        if (!value.IsFinite || value.Rotation.LengthSquared <= 1.0e-12 ||
            !BoneTransformAuthoringPolicy.IsValidScale(value.Scale))
            throw new InvalidOperationException("The transform must have finite values, a valid rotation, and positive scale.");
    }

    internal static ImmutableArray<BoneEditTrack> RemoveBoneKey(
        ImmutableArray<BoneEditTrack> tracks, int trackIndex, int keyIndex)
    {
        BoneEditTrack track = tracks[trackIndex];
        return track.Keyframes.Length == 1
            ? tracks.RemoveAt(trackIndex)
            : tracks.SetItem(trackIndex, new BoneEditTrack(track.BoneIndex,
                track.Keyframes.RemoveAt(keyIndex), track.Interpolation));
    }

    internal static ImmutableArray<MorphEditTrack> RemoveMorphKey(
        ImmutableArray<MorphEditTrack> tracks, int trackIndex, int keyIndex)
    {
        MorphEditTrack track = tracks[trackIndex];
        return track.Keyframes.Length == 1
            ? tracks.RemoveAt(trackIndex)
            : tracks.SetItem(trackIndex, new MorphEditTrack(track.MorphName,
                track.Keyframes.RemoveAt(keyIndex)));
    }

    internal static void RejectFrameCollision(IEnumerable<double> frames, int movingIndex, double destination)
    {
        int index = 0;
        foreach (double frame in frames)
        {
            if (index != movingIndex && Math.Abs(frame - destination) <= 1.0e-9)
                throw new InvalidOperationException("A key already exists at that frame.");
            index++;
        }
    }

    private static int FindTransformKey(ImmutableArray<TransformKeyframe> keys, double frame) =>
        FindNearestKey(keys.Select(static key => key.Frame), frame);

    private static int FindScalarKey(ImmutableArray<ScalarKeyframe> keys, double frame) =>
        FindNearestKey(keys.Select(static key => key.Frame), frame);

    private static int FindNearestKey(IEnumerable<double> frames, double frame)
    {
        int index = 0;
        foreach (double candidate in frames)
        {
            if (Math.Abs(candidate - frame) <= 1.0e-7) return index;
            index++;
        }
        return -1;
    }

    private static int FindBoneTrack(ImmutableArray<BoneEditTrack> tracks, int bone)
    {
        for (int index = 0; index < tracks.Length; index++) if (tracks[index].BoneIndex == bone) return index;
        return -1;
    }

    private static int FindMorphTrack(ImmutableArray<MorphEditTrack> tracks, string morph)
    {
        for (int index = 0; index < tracks.Length; index++)
            if (string.Equals(tracks[index].MorphName, morph, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }

    private static int IndexOf(ImmutableArray<BoneEditLayer> layers, Guid id)
    {
        for (int index = 0; index < layers.Length; index++) if (layers[index].Id == id) return index;
        return -1;
    }

    private static int IndexOf(ImmutableArray<MorphEditLayer> layers, Guid id)
    {
        for (int index = 0; index < layers.Length; index++) if (layers[index].Id == id) return index;
        return -1;
    }
}

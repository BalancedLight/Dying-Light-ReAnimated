using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Evaluation;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.App.ViewModels;

public enum ViewportSide
{
    Source,
    Target,
}

public sealed record ViewportOrbitCameraPair(
    RenderCamera Source,
    RenderCamera Target);

public sealed class LinkedViewportCoordinator
{
    private readonly object _gate = new();
    private RenderCamera _sourceCamera = RenderCamera.Default;
    private RenderCamera _targetCamera = RenderCamera.Default;
    private RenderCamera? _targetPreviewCameraOverride;
    private bool _isTargetPreviewCameraOverrideActive;
    private bool _isLinked = true;

    public bool IsLinked
    {
        get
        {
            lock (_gate)
            {
                return _isLinked;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_isLinked == value)
                {
                    return;
                }

                _isLinked = value;
            }
        }
    }

    public RenderCamera GetCamera(ViewportSide side)
    {
        lock (_gate)
        {
            return side == ViewportSide.Source
                ? _sourceCamera
                : GetEffectiveTargetCameraCore();
        }
    }

    /// <summary>
    /// Captures the two editor orbit cameras without substituting an active
    /// EyeCamera or movie-camera preview override.
    /// </summary>
    public ViewportOrbitCameraPair CaptureOrbitCameras()
    {
        lock (_gate)
        {
            return new ViewportOrbitCameraPair(
                _sourceCamera,
                _targetCamera);
        }
    }

    /// <summary>
    /// Restores both editor orbit cameras exactly. Workspace presentation
    /// changes use this instead of linked navigation so an isolated Browse
    /// framing operation cannot move the Animate or Retarget cameras.
    /// </summary>
    public void RestoreOrbitCameras(ViewportOrbitCameraPair cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        lock (_gate)
        {
            _sourceCamera = cameras.Source;
            _targetCamera = cameras.Target;
        }
    }

    /// <summary>
    /// Captures a scene and its matching camera while excluding a paired
    /// source/target publication. A render thread therefore observes either
    /// the complete older pair or the complete newer pair, never the write in
    /// between them.
    /// </summary>
    internal RenderFrameSnapshot CaptureScene(
        ViewportSide side,
        RenderSceneBuffer sceneBuffer)
    {
        ArgumentNullException.ThrowIfNull(sceneBuffer);
        lock (_gate)
        {
            RenderCamera camera = side == ViewportSide.Source
                ? _sourceCamera
                : GetEffectiveTargetCameraCore();
            return sceneBuffer.Capture(camera);
        }
    }

    /// <summary>
    /// Publishes the two authoritative panes as one generation transaction.
    /// Scene sources also capture under this gate, so rapid scrubbing cannot
    /// sample only one half of the transaction.
    /// </summary>
    internal void PublishScenePair(Action publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        lock (_gate)
        {
            publish();
        }
    }

    public bool HasTargetPreviewCameraOverride
    {
        get
        {
            lock (_gate)
            {
                return _isTargetPreviewCameraOverrideActive &&
                       _targetPreviewCameraOverride is not null;
            }
        }
    }

    /// <summary>
    /// Publishes an evaluated FPP/movie camera without destroying either
    /// editor orbit camera. Passing <see langword="null"/> restores the target
    /// orbit camera immediately.
    /// </summary>
    public void SetTargetPreviewCameraOverride(RenderCamera? camera)
    {
        lock (_gate)
        {
            _targetPreviewCameraOverride = camera;
            _isTargetPreviewCameraOverrideActive = camera is not null;
        }
    }

    /// <summary>
    /// Temporarily selects the editor orbit camera without discarding a valid
    /// evaluated EyeCamera/movie camera. This is used by presentation-only
    /// workspace changes so returning to FPP does not require a fresh sample.
    /// Returns whether an evaluated camera is active after the request.
    /// </summary>
    public bool SetTargetPreviewCameraOverrideActive(bool isActive)
    {
        lock (_gate)
        {
            _isTargetPreviewCameraOverrideActive =
                isActive && _targetPreviewCameraOverride is not null;
            return _isTargetPreviewCameraOverrideActive;
        }
    }

    public void UpdateCamera(ViewportSide side, RenderCamera camera)
    {
        lock (_gate)
        {
            UpdateCameraCore(side, camera);
        }
    }

    public RenderCameraNavigationResult NavigateCamera(
        ViewportSide side,
        RenderCameraNavigationInput input)
    {
        lock (_gate)
        {
            if (side == ViewportSide.Target &&
                _isTargetPreviewCameraOverrideActive &&
                _targetPreviewCameraOverride is not null)
            {
                return RenderCameraNavigationResult
                    .PreviewCameraLocked;
            }

            RenderCamera current = side == ViewportSide.Source
                ? _sourceCamera
                : _targetCamera;
            if (!RenderCameraNavigation.TryApply(
                    current,
                    input,
                    out RenderCamera updated))
            {
                return RenderCameraNavigationResult.InvalidCamera;
            }

            if (updated == current)
            {
                return RenderCameraNavigationResult.NoChange;
            }

            if (_isLinked)
            {
                RenderCamera other = side == ViewportSide.Source
                    ? _targetCamera
                    : _sourceCamera;
                if (!RenderCameraNavigation.TryApply(
                        other,
                        input,
                        out RenderCamera updatedOther))
                {
                    return RenderCameraNavigationResult.InvalidCamera;
                }

                if (side == ViewportSide.Source)
                {
                    _sourceCamera = updated;
                    _targetCamera = updatedOther;
                }
                else
                {
                    _targetCamera = updated;
                    _sourceCamera = updatedOther;
                }
            }
            else
            {
                UpdateCameraCore(side, updated);
            }
            return RenderCameraNavigationResult.Applied;
        }
    }

    private RenderCamera GetEffectiveTargetCameraCore() =>
        _isTargetPreviewCameraOverrideActive
            ? _targetPreviewCameraOverride ?? _targetCamera
            : _targetCamera;

    public void UpdateLens(float fieldOfViewDegrees, float nearPlane)
    {
        lock (_gate)
        {
            _sourceCamera = _sourceCamera with
            {
                VerticalFieldOfViewDegrees = fieldOfViewDegrees,
                NearPlane = nearPlane,
            };
            if (_isLinked)
            {
                _targetCamera = _targetCamera with
                {
                    VerticalFieldOfViewDegrees = fieldOfViewDegrees,
                    NearPlane = nearPlane,
                };
            }
            else
            {
                _targetCamera = _targetCamera with
                {
                    VerticalFieldOfViewDegrees = fieldOfViewDegrees,
                    NearPlane = nearPlane,
                };
            }
        }
    }

    private void UpdateCameraCore(
        ViewportSide side,
        RenderCamera camera)
    {
        if (side == ViewportSide.Source)
        {
            _sourceCamera = camera;
            if (_isLinked)
            {
                _targetCamera = camera;
            }
        }
        else
        {
            _targetCamera = camera;
            if (_isLinked)
            {
                _sourceCamera = camera;
            }
        }
    }
}

public static class Dl1PreviewCameraAdapter
{
    private static readonly Vector3D Dl1CameraForward = new(0.0, 0.0, 1.0);
    private static readonly Vector3D Dl1CameraUp = new(0.0, -1.0, 0.0);

    /// <summary>
    /// Converts Chrome Engine's mtx34 camera basis to the renderer's look-at
    /// camera. DL1 reads direction from the third matrix column and up from
    /// the negated second column in the inspected PlayerFppVis paths.
    /// </summary>
    public static RenderCamera ToRenderCamera(
        EvaluatedCamera camera,
        bool preserveLensAspectRatio = false)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Vector3 eye = ToFiniteVector(camera.WorldTransform.Translation);
        Vector3 forward = ToFiniteVector(
            camera.WorldTransform.TransformDirection(Dl1CameraForward));
        Vector3 up = ToFiniteVector(
            camera.WorldTransform.TransformDirection(Dl1CameraUp));
        if (forward.LengthSquared() <= 1.0e-8f)
        {
            throw new InvalidOperationException(
                "The evaluated DL1 camera has no usable forward direction.");
        }

        if (up.LengthSquared() <= 1.0e-8f)
        {
            throw new InvalidOperationException(
                "The evaluated DL1 camera has no usable up direction.");
        }

        CameraLens lens = camera.Lens;
        RenderCamera result = new(
            eye,
            eye + Vector3.Normalize(forward),
            Vector3.Normalize(up),
            checked((float)lens.VerticalFieldOfViewDegrees),
            checked((float)lens.NearClipMeters),
            checked((float)lens.FarClipMeters));
        return preserveLensAspectRatio
            ? result with
            {
                ProjectionAspectRatio =
                    checked((float)lens.AspectRatio),
            }
            : result;
    }

    public static RenderProjectionParameters ToRenderProjection(
        Dl1ProjectionParameters projection)
    {
        return new RenderProjectionParameters(
            checked((float)projection.FieldOfViewDegrees),
            projection.FieldOfViewAxis switch
            {
                Dl1ProjectionFovAxis.Horizontal =>
                    RenderProjectionFovAxis.Horizontal,
                Dl1ProjectionFovAxis.Vertical =>
                    RenderProjectionFovAxis.Vertical,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(projection),
                    "The DL1 projection FOV axis is unknown."),
            },
            checked((float)projection.AspectRatio),
            checked((float)projection.NearClipMeters),
            projection.FarPlane switch
            {
                Dl1ProjectionFarPlane.Finite =>
                    RenderProjectionFarPlane.Finite,
                Dl1ProjectionFarPlane.Infinite =>
                    RenderProjectionFarPlane.Infinite,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(projection),
                    "The DL1 projection far-plane mode is unknown."),
            },
            projection.FarClipMeters is double farClip
                ? checked((float)farClip)
                : null);
    }

    private static Vector3 ToFiniteVector(Vector3D value)
    {
        var result = new Vector3(
            checked((float)value.X),
            checked((float)value.Y),
            checked((float)value.Z));
        if (!float.IsFinite(result.X) ||
            !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z))
        {
            throw new InvalidOperationException(
                "The evaluated DL1 camera transform is not finite.");
        }

        return result;
    }
}

public sealed class ViewportSceneSource :
    IRenderSceneSource,
    IRenderCameraNavigationTarget,
    IRenderTransformGizmoTarget,
    IRenderTranslationGizmoTarget
{
    private sealed record SkeletonVisibilityState(
        bool ShowDeformBones,
        bool ShowHelpers,
        bool ShowCameraHelpers,
        bool ShowProps);

    private sealed record DeformedBoundsCache(
        IReadOnlyList<MeshRenderData> Meshes,
        SkeletonRenderData? Skeleton,
        IReadOnlyList<MorphWeight> MorphWeights,
        ImmutableArray<DeformedMeshBoundsRenderData> Bounds);

    private readonly LinkedViewportCoordinator _cameraCoordinator;
    private readonly ViewportSide _side;
    private readonly RenderSceneBuffer _sceneBuffer;
    private IRenderTransformGizmoTarget? _transformGizmoTarget;
    private IRenderTranslationGizmoTarget? _translationGizmoTarget;
    private RenderFrameSnapshot? _externalPreviewScene;
    private SkeletonVisibilityState? _skeletonVisibility;
    private DeformedBoundsCache? _deformedBoundsCache;
    private DeformedBoundsCache? _externalPreviewDeformedBoundsCache;
    private int _showMeshes = 1;

    public ViewportSceneSource(
        LinkedViewportCoordinator cameraCoordinator,
        ViewportSide side,
        Vector4 clearColor)
    {
        _cameraCoordinator = cameraCoordinator
            ?? throw new ArgumentNullException(nameof(cameraCoordinator));
        _side = side;
        _sceneBuffer = new RenderSceneBuffer(clearColor);
    }

    public RenderFrameSnapshot CaptureFrame()
    {
        RenderFrameSnapshot authored =
            _cameraCoordinator.CaptureScene(
                _side,
                _sceneBuffer);
        RenderCamera camera = authored.Camera;
        RenderFrameSnapshot? externalPreview =
            Volatile.Read(ref _externalPreviewScene);
        RenderFrameSnapshot presented = externalPreview is null
            ? authored
            : externalPreview with
            {
                ClearColor = authored.ClearColor,
                Camera = camera,
                FppProjectionState = null,
            };
        return Volatile.Read(ref _showMeshes) != 0
            ? presented
            : presented with
            {
                Meshes = Array.Empty<MeshRenderData>(),
                AuthoringOverlays =
                    presented.AuthoringOverlays with
                    {
                        DeformedMeshBounds = [],
                    },
            };
    }

    public bool HasExternalPreviewScene =>
        Volatile.Read(ref _externalPreviewScene) is not null;

    /// <summary>
    /// Shows an isolated presentation scene through this pane's ordinary
    /// orbit camera. The authoritative scene buffer remains live behind this
    /// display-only override, so passing <see langword="null"/> restores it
    /// without reconstructing or approximating any scene state. The source
    /// pane uses this for the linked FPP external view; the target pane uses
    /// it for Browse asset inspection.
    /// </summary>
    public void SetExternalPreviewScene(RenderFrameSnapshot? targetFrame)
    {
        RenderFrameSnapshot? stableFrame = targetFrame is null
            ? null
            : targetFrame with
            {
                Meshes = targetFrame.Meshes.ToArray(),
                Skeleton = targetFrame.Skeleton is { } skeleton
                    ? ApplySkeletonVisibility(
                        skeleton with
                        {
                            Bones = skeleton.Bones.ToArray(),
                        })
                    : null,
                Gizmos = targetFrame.Gizmos.ToArray(),
                MorphWeights = targetFrame.MorphWeights.ToArray(),
                FppProjectionState = null,
            };
        Volatile.Write(ref _externalPreviewScene, stableFrame);
        Volatile.Write(
            ref _externalPreviewDeformedBoundsCache,
            null);
    }

    public RenderCameraNavigationResult NavigateCamera(
        RenderCameraNavigationInput input) =>
        _cameraCoordinator.NavigateCamera(_side, input);

    public void SetTranslationGizmoTarget(
        IRenderTranslationGizmoTarget? target)
    {
        Volatile.Write(
            ref _translationGizmoTarget,
            target);
    }

    public void SetTransformGizmoTarget(
        IRenderTransformGizmoTarget? target)
    {
        Volatile.Write(
            ref _transformGizmoTarget,
            target);
    }

    public bool TryBeginTransformGizmoDrag(
        RenderTransformGizmoDragStart start)
    {
        if (IsTransformGizmoBlocked())
        {
            return false;
        }

        return Volatile.Read(
                ref _transformGizmoTarget)
            ?.TryBeginTransformGizmoDrag(start) == true;
    }

    public bool UpdateTransformGizmoDrag(
        RenderTransformGizmoDragUpdate update)
    {
        if (IsTransformGizmoBlocked())
        {
            return false;
        }

        return Volatile.Read(
                ref _transformGizmoTarget)
            ?.UpdateTransformGizmoDrag(update) == true;
    }

    public void CompleteTransformGizmoDrag(bool commit)
    {
        Volatile.Read(ref _transformGizmoTarget)
            ?.CompleteTransformGizmoDrag(
                commit && !IsTransformGizmoBlocked());
    }

    public bool TryBeginTranslationGizmoDrag(
        RenderTranslationGizmoDragStart start)
    {
        if (IsTranslationGizmoBlocked())
        {
            return false;
        }

        return Volatile.Read(
                ref _translationGizmoTarget)
            ?.TryBeginTranslationGizmoDrag(start) == true;
    }

    public bool UpdateTranslationGizmoDrag(
        RenderTranslationGizmoDragUpdate update)
    {
        if (IsTranslationGizmoBlocked())
        {
            return false;
        }

        return Volatile.Read(
                ref _translationGizmoTarget)
            ?.UpdateTranslationGizmoDrag(update) == true;
    }

    public void CompleteTranslationGizmoDrag(bool commit)
    {
        Volatile.Read(ref _translationGizmoTarget)
            ?.CompleteTranslationGizmoDrag(
                commit && !IsTranslationGizmoBlocked());
    }

    public void SetSkeleton(SkeletonRenderData? skeleton)
    {
        _sceneBuffer.SetSkeleton(ApplySkeletonVisibility(skeleton));
    }

    public void SetSkeletonVisibility(
        bool showDeformBones,
        bool showHelpers,
        bool showCameraHelpers,
        bool showProps)
    {
        Volatile.Write(
            ref _skeletonVisibility,
            new SkeletonVisibilityState(
                showDeformBones,
                showHelpers,
                showCameraHelpers,
                showProps));
        RenderFrameSnapshot authored = _sceneBuffer.Capture(
            _cameraCoordinator.GetCamera(_side));
        if (authored.Skeleton is { } skeleton)
        {
            _sceneBuffer.SetSkeleton(
                ApplySkeletonVisibility(skeleton));
        }

        UpdateExternalPreviewScene(frame =>
            frame.Skeleton is not { } externalSkeleton
                ? frame
                : frame with
                {
                    Skeleton =
                        ApplySkeletonVisibility(externalSkeleton),
                });
    }

    public void SetMeshes(IReadOnlyList<MeshRenderData> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        _sceneBuffer.SetMeshes(meshes);
    }

    /// <summary>
    /// Controls presentation without discarding decoded mesh state. Restoring
    /// visibility therefore does not trigger a retail-asset decode or alter
    /// the skeleton, morph weights, attachments, or animation evaluation.
    /// </summary>
    public void SetMeshVisibility(bool showMeshes)
    {
        Volatile.Write(
            ref _showMeshes,
            showMeshes ? 1 : 0);
    }

    public void SetGizmos(IReadOnlyList<GizmoRenderData> gizmos)
    {
        ArgumentNullException.ThrowIfNull(gizmos);
        _sceneBuffer.SetGizmos(gizmos);
    }

    public void SetMorphWeights(IReadOnlyList<MorphWeight> morphWeights)
    {
        ArgumentNullException.ThrowIfNull(morphWeights);
        _sceneBuffer.SetMorphWeights(morphWeights);
    }

    public void SetFppProjectionState(
        RenderFppProjectionState? projectionState)
    {
        _sceneBuffer.SetFppProjectionState(projectionState);
    }

    public void SetAuthoringOverlays(
        RenderAuthoringOverlayState? overlayState)
    {
        RenderAuthoringOverlayState requested =
            overlayState ?? RenderAuthoringOverlayState.Disabled;
        RenderFrameSnapshot authored = _sceneBuffer.Capture(
            _cameraCoordinator.GetCamera(_side));
        _sceneBuffer.SetAuthoringOverlays(
            PrepareAuthoringOverlays(
                authored,
                requested,
                ref _deformedBoundsCache));
        UpdateExternalPreviewScene(frame => frame with
        {
            AuthoringOverlays =
                PrepareAuthoringOverlays(
                    frame,
                    requested,
                    ref _externalPreviewDeformedBoundsCache),
        });
    }

    public void SetScene(
        IReadOnlyList<MeshRenderData> meshes,
        SkeletonRenderData? skeleton,
        IReadOnlyList<GizmoRenderData> gizmos,
        IReadOnlyList<MorphWeight>? morphWeights = null,
        long? generation = null)
    {
        _sceneBuffer.SetScene(
            meshes,
            ApplySkeletonVisibility(skeleton),
            gizmos,
            morphWeights,
            generation);
    }

    public void SelectBone(int? boneIndex)
    {
        RenderFrameSnapshot authored = _sceneBuffer.Capture(
            _cameraCoordinator.GetCamera(_side));
        if (authored.Skeleton is { } skeleton)
        {
            _sceneBuffer.SetSkeleton(
                SelectBone(skeleton, boneIndex));
        }

        UpdateExternalPreviewScene(frame =>
            frame.Skeleton is not { } externalSkeleton
                ? frame
                : frame with
                {
                    Skeleton = SelectBone(
                        externalSkeleton,
                        boneIndex),
                });
    }

    private static SkeletonRenderData SelectBone(
        SkeletonRenderData skeleton,
        int? boneIndex)
    {
        BoneRenderData[] bones = skeleton.Bones.ToArray();
        for (int index = 0; index < bones.Length; index++)
        {
            bones[index] = bones[index] with
            {
                IsSelected = index == boneIndex,
            };
        }

        return skeleton with
        {
            Bones = bones,
        };
    }

    private bool IsTransformGizmoBlocked() =>
        _side == ViewportSide.Target &&
        _cameraCoordinator.HasTargetPreviewCameraOverride;

    private bool IsTranslationGizmoBlocked() =>
        IsTransformGizmoBlocked();

    private static RenderAuthoringOverlayState
        PrepareAuthoringOverlays(
            RenderFrameSnapshot frame,
            RenderAuthoringOverlayState requested,
            ref DeformedBoundsCache? cache)
    {
        RenderAuthoringOverlayState prepared = requested;
        if (prepared.Options.ShowDeformedBounds ||
            prepared.Options.HighlightSelectedMeshes)
        {
            if (cache is null ||
                !ReferenceEquals(cache.Meshes, frame.Meshes) ||
                !ReferenceEquals(cache.Skeleton, frame.Skeleton) ||
                !ReferenceEquals(
                    cache.MorphWeights,
                    frame.MorphWeights))
            {
                cache = new DeformedBoundsCache(
                    frame.Meshes,
                    frame.Skeleton,
                    frame.MorphWeights,
                    AuthoringOverlayBoundsPrecomputer.Measure(frame));
            }

            return prepared with
            {
                DeformedMeshBounds = cache.Bounds,
            };
        }

        return prepared with
        {
            DeformedMeshBounds = [],
        };
    }

    private void UpdateExternalPreviewScene(
        Func<RenderFrameSnapshot, RenderFrameSnapshot> transform)
    {
        while (true)
        {
            RenderFrameSnapshot? current =
                Volatile.Read(ref _externalPreviewScene);
            if (current is null)
            {
                return;
            }

            RenderFrameSnapshot next = transform(current);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _externalPreviewScene,
                        next,
                        current),
                    current))
            {
                return;
            }
        }
    }

    private SkeletonRenderData? ApplySkeletonVisibility(
        SkeletonRenderData? skeleton)
    {
        if (skeleton is null)
        {
            return null;
        }

        SkeletonVisibilityState? visibility =
            Volatile.Read(ref _skeletonVisibility);
        if (visibility is null)
        {
            return skeleton;
        }

        return skeleton with
        {
            ShowDeformBones = visibility.ShowDeformBones,
            ShowHelpers = visibility.ShowHelpers,
            ShowCameraHelpers = visibility.ShowCameraHelpers,
            ShowProps = visibility.ShowProps,
        };
    }
}

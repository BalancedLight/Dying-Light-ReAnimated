using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.Core.Mathematics;
using ReAnimated.Renderer.D3D11;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.App.ViewModels;

public sealed partial class RigConformanceWizardViewModel
{
    private WizardGizmoTarget? _gizmoTarget;
    private ActiveDrag? _drag;

    /// <summary>
    /// The scene-source gizmo target for the refine stage. Register it while
    /// the wizard is showing and clear it otherwise, so a stray drag cannot
    /// move a joint from another workspace tab.
    /// </summary>
    public IRenderTranslationGizmoTarget GizmoTarget =>
        _gizmoTarget ??= new WizardGizmoTarget(this);

    /// <summary>
    /// Index of the joint the guided sequence is on, in the conformed rig's own
    /// ordering, so the viewport draws its gizmo on the right bone.
    /// </summary>
    public int SelectedBoneIndex =>
        SelectedLandmark is { } landmark && Fit is { } fit
            ? IndexOf(fit, landmark.BoneName)
            : -1;

    private static int IndexOf(RigConformanceResult fit, string boneName)
    {
        foreach (RigConformedBone bone in fit.Bones)
        {
            if (string.Equals(bone.Name, boneName, StringComparison.OrdinalIgnoreCase))
            {
                return bone.Index;
            }
        }

        return -1;
    }

    private bool TryBeginGizmoDrag(int boneIndex)
    {
        if (IsBusy ||
            _drag is not null ||
            Fit is not { } fit ||
            (uint)boneIndex >= (uint)fit.Bones.Length)
        {
            return false;
        }

        RigConformedBone bone = fit.Bones[boneIndex];

        // Only the guided sequence's joints are draggable. A synthesized DL1
        // helper takes its placement from the template by construction, and
        // letting it drift would silently break the structure stock clips
        // expect.
        if (!IsLandmarkBone(bone.Name))
        {
            return false;
        }

        _drag = new ActiveDrag(bone.Name, bone.Position, _positionOverrides);
        return true;
    }

    private bool UpdateGizmoDrag(int boneIndex, Vector3 worldDelta)
    {
        if (_drag is not { } drag ||
            Fit is not { } fit ||
            (uint)boneIndex >= (uint)fit.Bones.Length ||
            !string.Equals(
                fit.Bones[boneIndex].Name,
                drag.BoneName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!float.IsFinite(worldDelta.X) ||
            !float.IsFinite(worldDelta.Y) ||
            !float.IsFinite(worldDelta.Z))
        {
            return false;
        }

        var moved = new Vector3D(
            drag.StartPosition.X + worldDelta.X,
            drag.StartPosition.Y + worldDelta.Y,
            drag.StartPosition.Z + worldDelta.Z);

        // Preview stays transient: the pre-drag overrides are restored on
        // cancel, and only a committed drag survives.
        ApplyPlacement(drag.BoneName, moved);
        return true;
    }

    private void CompleteGizmoDrag(bool commit)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (commit)
        {
            Solve();
            return;
        }

        _positionOverrides = drag.OriginalOverrides;
        Solve();
    }

    private static bool IsLandmarkBone(string boneName)
    {
        foreach ((string bone, _, _, _) in LandmarkSequence)
        {
            if (string.Equals(bone, boneName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records a placement and its mirror without re-solving, so a drag can
    /// update several rows before one solve.
    /// </summary>
    private void ApplyPlacement(string boneName, Vector3D position)
    {
        if (!position.IsFinite)
        {
            return;
        }

        _positionOverrides = _positionOverrides.SetItem(boneName, position);
        if (MirrorEdits && TryFindMirror(boneName) is { } mirror)
        {
            _positionOverrides = _positionOverrides.SetItem(
                mirror,
                new Vector3D(-position.X, position.Y, position.Z));
        }

        Solve();
    }

    private sealed record ActiveDrag(
        string BoneName,
        Vector3D StartPosition,
        ImmutableDictionary<string, Vector3D> OriginalOverrides);

    /// <summary>
    /// Adapts the renderer's two gizmo contracts onto the wizard. Rotation and
    /// scale are refused: a conformance places joints, and the Chrome local
    /// frames are authored downstream from those positions.
    /// </summary>
    private sealed class WizardGizmoTarget :
        IRenderTranslationGizmoTarget,
        IRenderTransformGizmoTarget
    {
        private readonly RigConformanceWizardViewModel _owner;

        public WizardGizmoTarget(RigConformanceWizardViewModel owner) =>
            _owner = owner;

        public bool TryBeginTranslationGizmoDrag(
            RenderTranslationGizmoDragStart start) =>
            Enum.IsDefined(start.Binding.Axis) &&
            Enum.IsDefined(start.Binding.Space) &&
            _owner.TryBeginGizmoDrag(start.Binding.BoneIndex);

        public bool UpdateTranslationGizmoDrag(
            RenderTranslationGizmoDragUpdate update) =>
            _owner.UpdateGizmoDrag(update.Binding.BoneIndex, update.WorldDelta);

        public void CompleteTranslationGizmoDrag(bool commit) =>
            _owner.CompleteGizmoDrag(commit);

        public bool TryBeginTransformGizmoDrag(
            RenderTransformGizmoDragStart start) =>
            start.Binding.Mode == RenderTransformGizmoMode.Translate &&
            Enum.IsDefined(start.Binding.Axis) &&
            Enum.IsDefined(start.Binding.Space) &&
            _owner.TryBeginGizmoDrag(start.Binding.BoneIndex);

        public bool UpdateTransformGizmoDrag(
            RenderTransformGizmoDragUpdate update) =>
            update.Binding.Mode == RenderTransformGizmoMode.Translate &&
            _owner.UpdateGizmoDrag(update.Binding.BoneIndex, update.WorldDelta);

        public void CompleteTransformGizmoDrag(bool commit) =>
            _owner.CompleteGizmoDrag(commit);
    }
}

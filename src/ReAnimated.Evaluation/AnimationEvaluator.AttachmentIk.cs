using System.Collections.Immutable;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Ik;

namespace ReAnimated.Evaluation;

public sealed record AttachmentIkReport(Guid BindingId, bool DisplayEvaluation, bool Applied, bool Clamped, string Message);

public sealed partial class AnimationEvaluator
{
    private static SkeletonPose ApplyAttachmentIk(SkeletonPose input, IEnumerable<AttachmentBinding> bindings, bool display,
        ImmutableArray<EvaluationDiagnostic>.Builder diagnostics, ImmutableArray<AttachmentIkReport>.Builder reports)
    {
        var plans = new List<(AttachmentBinding Binding, AttachmentSecondaryGrip Secondary, AttachmentSecondaryIk Driver)>();
        foreach (var binding in bindings)
        {
            if (binding.GripCalibration?.Secondary is not { Ik: { } driver } secondary) continue;
            if (driver.Weight == 0) { reports.Add(new(binding.Id, display, false, false, "Secondary IK weight is zero.")); continue; }
            try
            {
                RequireName(input.Rig, binding.ParentBoneIndex, binding.ParentBoneName);
                RequireName(input.Rig, secondary.CharacterBoneIndex, secondary.CharacterBoneName);
                RequireName(input.Rig, driver.RootBoneIndex, driver.RootBoneName);
                RequireName(input.Rig, driver.JointBoneIndex, driver.JointBoneName);
                RequireName(input.Rig, driver.EndBoneIndex, driver.EndBoneName);
                if (input.Rig.Bones[driver.JointBoneIndex].ParentIndex != driver.RootBoneIndex || input.Rig.Bones[driver.EndBoneIndex].ParentIndex != driver.JointBoneIndex)
                    throw new InvalidOperationException("Select a direct upper/joint/end chain.");
                if (!DescendsFrom(input.Rig, secondary.CharacterBoneIndex, driver.EndBoneIndex))
                    throw new InvalidOperationException("The secondary character frame must be the driven end or its descendant.");
                if (DescendsFrom(input.Rig, binding.ParentBoneIndex, driver.RootBoneIndex))
                    throw new InvalidOperationException("The driven chain would move its own primary prop parent, creating feedback.");
                if (input.Rig.Bones.Any(b => b.Kind == BoneKind.Camera && DescendsFrom(input.Rig, b.Index, driver.RootBoneIndex)))
                    throw new InvalidOperationException("This driver would move a camera helper; camera/IK composition is not supported by this path.");
                for (int bone = driver.EndBoneIndex; bone >= 0; bone = input.Rig.Bones[bone].ParentIndex)
                {
                    var transform = ExactTrs(input.LocalMatrices[bone]); var scale = transform.Scale;
                    if (scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0 || Math.Abs(scale.X - scale.Y) > 1e-7 * Math.Max(1, scale.X) || Math.Abs(scale.X - scale.Z) > 1e-7 * Math.Max(1, scale.X))
                        throw new InvalidOperationException("Secondary IK requires positive uniform scale along its driven ancestry; the original pose was retained.");
                }
                plans.Add((binding, secondary, driver));
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { Reject(binding, error.Message); }
        }
        var blocked = new HashSet<Guid>();
        for (int i = 0; i < plans.Count; i++) for (int j = i + 1; j < plans.Count; j++)
        {
            var a = plans[i]; var b = plans[j];
            if (DescendsFrom(input.Rig, a.Driver.RootBoneIndex, b.Driver.RootBoneIndex) || DescendsFrom(input.Rig, b.Driver.RootBoneIndex, a.Driver.RootBoneIndex) ||
                DescendsFrom(input.Rig, a.Binding.ParentBoneIndex, b.Driver.RootBoneIndex) || DescendsFrom(input.Rig, b.Binding.ParentBoneIndex, a.Driver.RootBoneIndex))
            { blocked.Add(a.Binding.Id); blocked.Add(b.Binding.Id); }
        }
        SkeletonPose pose = input;
        foreach (var plan in plans)
        {
            if (blocked.Contains(plan.Binding.Id)) { Reject(plan.Binding, "Secondary attachment drivers overlap or depend on each other's primary parents. Resolve the ownership conflict."); continue; }
            try
            {
                var grip = plan.Binding.GripCalibration!; var driver = plan.Driver;
                TransformMatrix primary = pose.GlobalMatrices[plan.Binding.ParentBoneIndex] * plan.Binding.LocalOffset.ToMatrix();
                TransformMatrix prop = primary * grip.PrimaryPropFrame.Matrix.InvertedAffine();
                TransformMatrix targetContact = prop * plan.Secondary.PropFrame.Matrix;
                TransformMatrix end = pose.GlobalMatrices[driver.EndBoneIndex];
                TransformMatrix endToContact = end.InvertedAffine() * pose.GlobalMatrices[plan.Secondary.CharacterBoneIndex] * plan.Secondary.CharacterLocalOffset.ToMatrix();
                var currentEnd = ExactTrs(end); var relative = ExactTrs(endToContact); var contact = ExactTrs(targetContact);
                QuaternionD rotation = driver.MatchOrientation ? (contact.Rotation * relative.Rotation.Inverse()).Normalized() : currentEnd.Rotation;
                Vector3D offset = new TransformTRS(Vector3D.Zero, rotation, currentEnd.Scale).ToMatrix().TransformDirection(relative.Translation);
                Vector3D target = targetContact.Translation - offset;
                Vector3D pole = driver.PoleInPrimaryGripSpace ? primary.TransformPoint(driver.PolePoint) : driver.PolePoint;
                var constraint = new TwoBoneIkConstraint(driver.RootBoneIndex, driver.JointBoneIndex, driver.EndBoneIndex, target, pole, driver.Weight,
                    plan.Binding.Scope == AttachmentScope.PreviewOnly ? IkConstraintScope.PreviewOnly : IkConstraintScope.AuthoredExportable, rotation);
                pose = ApplyIkConstraint(pose, constraint, out var solution);
                bool clamped = solution?.WasClamped == true;
                reports.Add(new(plan.Binding.Id, display, true, clamped, clamped ? "Secondary target exceeds chain reach; the remaining contact gap is reported." : "Secondary hand solved after pose edits; source animation samples were retained."));
                if (clamped) diagnostics.Add(new("attachment_secondary_ik_clamped", EvaluationDiagnosticSeverity.Warning, $"Attachment '{plan.Binding.Name}' has an unreachable secondary target."));
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            { Reject(plan.Binding, error.Message); }
        }
        return pose;

        void Reject(AttachmentBinding binding, string reason)
        {
            reports.Add(new(binding.Id, display, false, false, reason));
            diagnostics.Add(new("attachment_secondary_ik_rejected", EvaluationDiagnosticSeverity.Error, $"Attachment '{binding.Name}': {reason}"));
        }
    }

    private static TransformTRS ExactTrs(TransformMatrix matrix)
    {
        var value = matrix.Decompose(1e-7);
        if (!value.ToMatrix().NearlyEquals(matrix, 1e-7)) throw new InvalidOperationException("The grip IK frame contains an affine residual that cannot be safely solved as TRS.");
        return value;
    }
    private static void RequireName(RigDefinition rig, int index, string? name)
    {
        if ((uint)index >= (uint)rig.BoneCount || string.IsNullOrWhiteSpace(name) || !rig.Bones[index].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A selected attachment/IK node is missing or reordered. Rebind the named chain explicitly.");
    }
    private static bool DescendsFrom(RigDefinition rig, int node, int ancestor)
    {
        while (node >= 0) { if (node == ancestor) return true; node = rig.Bones[node].ParentIndex; }
        return false;
    }
}

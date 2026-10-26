using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed partial class RigChannelPolicyWorkflowTests
{
    [Fact]
    public void MatchingNativeGripKeepsPositionAndRotationThroughAutomaticPlacement()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = CustomModelHelperAuthoring.DuplicateAsHelper(
            fixture.Model.Package.Document, 0, CustomModelAuthoredHelperKind.Prop, "l_handholder");
        var bones = document.CreateEffectiveBones();
        var globals = new TransformMatrix[bones.Length];
        foreach (CustomModelBone bone in bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        Dl1RigTemplate reference = new("generic-native-target", "generic-native-reference", new string('d', 64),
            bones.Select(bone => new Dl1RigTemplateEntity
            {
                Index = bone.Index, Name = bone.Name, ParentIndex = bone.ParentIndex,
                Kind = bone.Kind, IsDeform = bone.IsWeighted,
                LocalRestMatrix = bone.ExactLocalBindMatrix, GlobalRestMatrix = globals[bone.Index],
            }));
        RiggingSession session = document.RiggingSession!;
        document = document with
        {
            RiggingSession = session with
            {
                Recipe = session.Recipe with
                {
                    Entities = session.Recipe.Entities.SetItem(0,
                        session.Recipe.Entities[0] with { Kind = RigNativeEntityKind.Bone }),
                },
            },
            RigConformance = document.RigConformance! with
            {
                TemplateId = reference.TemplateId, TemplateProfileName = reference.ProfileName,
                TemplateSourceResourceName = reference.SourceResourceName, TemplateFingerprint = reference.SourceFingerprint,
                AppliedOutputRigSignature = RigSignature.Compute(document.CreateRigDefinition()),
            },
        };
        Assert.True(RigGripChannelDefaults.MatchesNativeFrames(document, reference));
        CustomModelDocument selected = ExistingAnimationBankDefaults.Apply(document, reference);
        session = selected.RiggingSession!;
        Guid helper = session.Recipe.Entities.Single(entity => entity.NativeName == "l_handholder").EntityId;
        AnimationComponentPolicy before = session.Recipe.ComponentPolicies.Single(policy => policy.EntityId == helper);
        Assert.Equal(RigAnimationComponents.Position | RigAnimationComponents.Rotation, before.EmittedMask);
        Assert.Equal(RigComponentOwner.Clip, Assert.Single(before.Position.Owners));

        int helperIndex = bones.Length - 1;
        RigPalmLandmarks palm = new(Vector3D.Zero, new(1, .3, 0), new(1, 0, 0), new(1, -.3, 0), new(1, -.6, 0));
        RigGripPlacementSolution solution = RigGripPlacementSolver.Solve(palm, palm,
            globals[helperIndex], globals[bones[helperIndex].ParentIndex]);
        CustomModelDocument placed = RigGripHelperAuthoring.Apply(selected, session.CreateJobToken(),
            [new(session.Recipe.Entities[0].EntityId, helper, "l_handholder", "grip.left", solution,
                Vector3D.Zero, new(.01, .01, .01))]);
        AnimationComponentPolicy after = placed.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == helper);
        Assert.Same(before, after);
        Assert.Equal(RigAnimationComponents.Position | RigAnimationComponents.Rotation, after.EmittedMask);
        foreach (AnimationComponentPolicy policy in session.Recipe.ComponentPolicies.Where(policy => policy.EntityId != helper))
            Assert.Same(policy, placed.RiggingSession.Recipe.ComponentPolicies.Single(next => next.EntityId == policy.EntityId));

        TransformMatrix offset = solution.LocalFrame with { M14 = solution.LocalFrame.M14 + .02 };
        CustomModelDocument adjusted = RigGripHelperAuthoring.SetLocalFrame(placed,
            placed.RiggingSession!.CreateJobToken(), helper, offset);
        AnimationComponentPolicy adjustedPolicy = adjusted.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == helper);
        Assert.Equal(RigAnimationComponents.Rotation, adjustedPolicy.EmittedMask);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(adjustedPolicy.Position.Owners));
        Dl1AuthoredRigContract contract = Dl1CustomModelRigPreparer.Prepare(fixture.Model with
        {
            Package = fixture.Model.Package with { Document = adjusted },
            Rig = adjusted.CreateRigDefinition(),
        }).Contract;
        Assert.Equal(RigAnimationComponents.Rotation,
            Dl1BoneScriptPolicyResolver.Resolve(adjusted, contract).Single(policy => policy.Name == "l_handholder").Mask);

        RigComponentPolicyEdit explicitChannels = new(helper, RigAnimationComponents.Position | RigAnimationComponents.Rotation,
            RigAnimationLod.Off, RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.BindInherited);
        Assert.True(RigComponentPolicyAuthoring.TryApply(adjusted, adjusted.RiggingSession.CreateJobToken(), [explicitChannels], out RiggingSession authored));
        CustomModelDocument manual = adjusted with { RiggingSession = authored };
        CustomModelDocument adjustedManual = RigGripHelperAuthoring.SetLocalFrame(manual, authored.CreateJobToken(), helper, offset);
        Assert.Same(authored.Recipe.ComponentPolicies.Single(policy => policy.EntityId == helper),
            adjustedManual.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == helper));
    }

    [Fact]
    public void NativeGripDefaultsKeepBindPositionAndRefreshRotationWithoutOverwritingUserChoices()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = CustomModelHelperAuthoring.DuplicateAsHelper(
            fixture.Model.Package.Document, 0, CustomModelAuthoredHelperKind.Prop, "l_handholder");
        document = CustomModelHelperAuthoring.DuplicateAsHelper(
            document, 0, CustomModelAuthoredHelperKind.Prop, "custom_grip");
        document = document with
        {
            RigConformance = document.RigConformance! with
            {
                AppliedOutputRigSignature = RigSignature.Compute(document.CreateRigDefinition()),
            },
        };

        CustomModelDocument selected = ExistingAnimationBankDefaults.Apply(document, fixture.Template);
        RiggingSession session = selected.RiggingSession!;
        Guid gripId = session.Recipe.Entities.Single(entity => entity.NativeName == "l_handholder").EntityId;
        Guid customId = session.Recipe.Entities.Single(entity => entity.NativeName == "custom_grip").EntityId;
        AnimationComponentPolicy grip = session.Recipe.ComponentPolicies.Single(policy => policy.EntityId == gripId);
        Assert.Equal(RigAnimationComponents.Rotation, grip.EmittedMask);
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(grip.Position.Owners));
        Assert.Equal(RigComponentOwner.Clip, Assert.Single(grip.Rotation.Owners));
        Assert.Equal(RigComponentOwner.BindInherited, Assert.Single(grip.Scale.Owners));
        Assert.Contains(grip.LodEvidence, evidence => evidence.Id == RigGripChannelDefaults.EvidenceId);
        Assert.Equal(RigAnimationComponents.Position | RigAnimationComponents.Rotation,
            session.Recipe.ComponentPolicies.Single(policy => policy.EntityId == customId).EmittedMask);
        Assert.All(session.Recipe.ComponentPolicies, policy =>
            Assert.All(policy.Position.Evidence.Concat(policy.Rotation.Evidence).Concat(policy.Scale.Evidence)
                .Concat(policy.LodEvidence), evidence => Assert.Equal(RigEvidenceKind.ProfileRule, evidence.Kind)));

        RigComponentPolicyEdit edit = new(gripId, RigAnimationComponents.Position | RigAnimationComponents.Rotation,
            RigAnimationLod.Off, RigComponentOwner.Clip, RigComponentOwner.Clip, RigComponentOwner.BindInherited);
        Assert.True(RigComponentPolicyAuthoring.TryApply(selected, session.CreateJobToken(), [edit], out RiggingSession authored));
        CustomModelDocument manual = selected with { RiggingSession = authored };
        Assert.Same(manual, RigGripChannelDefaults.Apply(manual, authored.CreateJobToken(), [gripId]));

        AnimationComponentPolicy bindOnly = grip with
        {
            EmittedMask = RigAnimationComponents.None,
            Rotation = grip.Rotation with { Owners = [RigComponentOwner.BindInherited] },
        };
        var lockedHelpers = session.Recipe.Helpers;
        if (!lockedHelpers.Any(helper => helper.EntityId == gripId))
            lockedHelpers = lockedHelpers.Add(new HelperRecipe
            {
                EntityId = gripId,
                OwnerAssetId = selected.ModelId,
                ParentEntityId = session.Recipe.Entities[0].EntityId,
                RoleId = "grip.left",
                FramePolicy = RigFramePolicy.Socket,
            });
        RiggingSession lockedSession = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.SetItem(0,
                    session.Recipe.Entities[0] with { Kind = RigNativeEntityKind.Bone }),
                FramePolicies = session.Recipe.FramePolicies.Where(policy => policy.EntityId != gripId).ToImmutableArray(),
                ComponentPolicies = session.Recipe.ComponentPolicies.Select(policy => policy.EntityId == gripId
                    ? bindOnly : policy).ToImmutableArray(),
                Helpers = lockedHelpers.Select(helper => helper.EntityId == gripId
                    ? helper with { LockedFields = RigHelperEditFields.Channels } : helper).ToImmutableArray(),
            },
        };
        CustomModelDocument locked = selected with { RiggingSession = lockedSession };
        Assert.Same(locked, RigGripChannelDefaults.Apply(locked, lockedSession.CreateJobToken(), [gripId]));
    }
}

using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed partial class RigChannelPolicyWorkflowTests
{
    [Fact]
    public void ExistingBankDefaultsPreserveManualOwnersAndAreIdempotent()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = fixture.Model.Package.Document;
        RiggingSession session = document.RiggingSession!;
        Guid retained = session.Recipe.Entities.Last().EntityId;
        var edit = new RigComponentPolicyEdit(retained, RigAnimationComponents.Position | RigAnimationComponents.Scale,
            RigAnimationLod.Lod2, RigComponentOwner.Procedural, RigComponentOwner.Attachment, RigComponentOwner.RuntimeBodyScale);
        Assert.True(RigComponentPolicyAuthoring.TryApply(document, session.CreateJobToken(), [edit], out RiggingSession authored));
        AnimationComponentPolicy original = authored.Recipe.ComponentPolicies.Single();
        document = document with { RiggingSession = authored };

        CustomModelDocument updated = ExistingAnimationBankDefaults.Apply(document, fixture.Template);

        Assert.Same(original, updated.RiggingSession!.Recipe.ComponentPolicies.Single(policy => policy.EntityId == retained));
        Assert.True(ExistingAnimationBankDefaults.IsSelected(updated));
        Assert.Same(updated, ExistingAnimationBankDefaults.Apply(updated, fixture.Template));
        Assert.Equal(updated.CreateEffectiveBones().Length, updated.RiggingSession.Recipe.ComponentPolicies.Length);
    }

    [Fact]
    public async Task ExistingBankDefaultsKeepDistancesAndRootMotionThroughReopenAndSourceExport()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument document = fixture.Model.Package.Document;
        var bones = document.Bones;
        int child = Enumerable.Range(0, bones.Length).First(index => bones[index].ParentIndex >= 0 && bones[index].Kind == BoneKind.Deform);
        CustomModelBone joint = bones[child];
        Vector3D authoredDistance = joint.LocalBindTransform.Translation + new Vector3D(0.25, 0.7, -0.15);
        TransformTRS jointBind = joint.LocalBindTransform with { Translation = authoredDistance };
        document = document with { Bones = bones.SetItem(child, joint with { LocalBindTransform = jointBind, ExactLocalBindMatrix = jointBind.ToMatrix() }) };
        document = document with { RigSignature = CustomModelContractSignatures.ComputeRig(document.Bones), RigConformance = document.RigConformance! with { AppliedOutputRigSignature = RigSignature.Compute(document.CreateRigDefinition()) } };
        document = ExistingAnimationBankDefaults.Apply(document, fixture.Template);
        var model = fixture.Model with { Package = fixture.Model.Package with { Document = document }, Rig = document.CreateRigDefinition() };
        model = FbxAuthoredModelLayer.Capture(model);
        string packagePath = Path.Combine(_directory, "authored.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(model.Package, packagePath);
        var reopened = FbxModelAuthoringImporter.ImportPackage(CustomModelPackageSerializer.Load(packagePath));
        Assert.True(ExistingAnimationBankDefaults.IsSelected(reopened.Package.Document));
        var session = CustomModelPreviewAdapter.CreateSession(reopened, CustomModelPreviewMode.Dl1Output);
        RigDefinition rig = reopened.Rig!;
        int root = rig.Bones.Single(bone => bone.ParentIndex < 0).Index;
        TransformTRS startRoot = rig.Bones[root].LocalBindPose;
        TransformTRS movingRoot = startRoot with { Translation = startRoot.Translation + new Vector3D(0.4, 0.2, -0.1), Rotation = QuaternionD.FromAxisAngle(new Vector3D(0, 1, 0), 0.3) };
        TransformTRS keyedJoint = rig.Bones[child].LocalBindPose with { Translation = authoredDistance * 2.5, Scale = new Vector3D(1.4, 0.8, 1.2) };
        var clip = new AnimationClip("joint motion", new FrameRate(30, 1), 2,
            [new TransformTrack(root, [new TransformKeyframe(0, startRoot), new TransformKeyframe(1, movingRoot)]),
             new TransformTrack(child, [new TransformKeyframe(0, keyedJoint), new TransformKeyframe(1, keyedJoint with { Rotation = QuaternionD.FromAxisAngle(new Vector3D(0, 0, 1), 0.7) })])]);
        SkeletonRenderData bind = session.CreateSkeleton(null, 0, null)!;
        foreach (int frame in new[] { 0, 1 })
        {
            SkeletonRenderData raw = session.CreateSkeleton(clip, frame, null)!;
            SkeletonRenderData saved = session.CreateSkeleton(clip, frame, null, reviewedPolicy: true)!;
            Assert.True(SkeletonSegmentLengthDriftEvaluator.Compare(bind, raw).MaximumAbsoluteDriftPercent > 20);
            Assert.InRange(SkeletonSegmentLengthDriftEvaluator.Compare(bind, saved).MaximumAbsoluteDriftPercent, 0, 0.001);
            Assert.True(Vector3.Distance(raw.Bones[root].WorldTransform.Translation, saved.Bones[root].WorldTransform.Translation) < 1e-5f);
        }
        Dl1SourceModelBuildResult output = await Dl1SourceModelWriter.WriteAsync(new() { Model = reopened, OutputDirectory = Path.Combine(_directory, "source"), ResourceName = "authored_model" });
        string script = File.ReadAllText(output.BoneScriptPath);
        Assert.Contains($"SetBoneAnimTrans(\"{rig.Bones[root].Name}\", POS | ROT, LOD_OFF)", script);
        Assert.Contains($"SetBoneAnimTrans(\"{rig.Bones[child].Name}\", ROT, LOD_OFF)", script);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingBankSelectionBeforeOrAfterFitSeedsOnceAndUndoes(bool chooseBeforeFit)
    {
        var fixture = StockPreviewFixture();
        FbxModelAuthoringImportResult input = chooseBeforeFit
            ? fixture.Model with { Package = fixture.Model.Package with { Document = fixture.Model.Package.Document with { RigConformance = null } } }
            : fixture.Model;
        using var workspace = new ModelsWorkspaceViewModel(new NoDialogs(), static _ => { }, static _ => Task.CompletedTask, static () => null,
            resolveRigTemplate: (_, _) => Task.FromResult(new Dl1RigTemplateResolution(fixture.Template,
                fixture.Template.ProfileName, fixture.Template.SourceResourceName, fixture.Template.SourceFingerprint, "Generic target")));
        workspace.CommitProjectRestore(new(input, "generic.dlrmodel", new ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        workspace.AnimationScriptAlias = "existing_bank";
        if (chooseBeforeFit) workspace.ReferenceExistingAnimationLibrary = true;
        workspace.Conformance.UseGeometryCorrespondence = false;
        await workspace.Conformance.ResolveTemplateCommand.ExecuteAsync(null);
        if (chooseBeforeFit)
        {
            Assert.True(workspace.Conformance.ApplyConformanceCommand.CanExecute(null), workspace.Conformance.SolveStatus);
            workspace.Conformance.ApplyConformanceCommand.Execute(null);
        }
        else workspace.ReferenceExistingAnimationLibrary = true;
        CustomModelDocument selected = Current(workspace);
        Assert.True(ExistingAnimationBankDefaults.IsSelected(selected));
        Assert.True(workspace.UseReviewedPolicyPreview);
        Assert.Equal(selected.CreateEffectiveBones().Length, selected.RiggingSession!.Recipe.ComponentPolicies.Length);
        workspace.UndoHelperEditCommand.Execute(null);
        Assert.Empty(Current(workspace).RiggingSession!.Recipe.ComponentPolicies);
        Assert.False(workspace.UseReviewedPolicyPreview);
        workspace.RedoHelperEditCommand.Execute(null);
        Assert.True(ExistingAnimationBankDefaults.IsSelected(Current(workspace)));
        Assert.True(workspace.UseReviewedPolicyPreview);
    }

    [Fact]
    public void ExistingBankRestoreDoesNotFillLegacyPoliciesAndUsesSavedDefaultsWithoutEditing()
    {
        var fixture = StockPreviewFixture();
        CustomModelDocument legacy = fixture.Model.Package.Document;
        legacy = legacy with { BuildSettings = legacy.BuildSettings with { ReferenceExistingAnimationLibrary = true, AnimationScriptAlias = "existing_bank" } };
        using var unchanged = Workspace(fixture.Model with { Package = fixture.Model.Package with { Document = legacy } });
        Assert.Empty(Current(unchanged).RiggingSession!.Recipe.ComponentPolicies);
        Assert.False(unchanged.UseReviewedPolicyPreview);

        CustomModelDocument selected = ExistingAnimationBankDefaults.Apply(legacy, fixture.Template);
        using var restored = Workspace(fixture.Model with { Package = fixture.Model.Package with { Document = selected } });
        Assert.Equal(selected.RiggingSession!.Recipe.ComponentPolicies, Current(restored).RiggingSession!.Recipe.ComponentPolicies);
        Assert.True(restored.UseReviewedPolicyPreview);
        Assert.False(restored.UndoHelperEditCommand.CanExecute(null));
    }
}

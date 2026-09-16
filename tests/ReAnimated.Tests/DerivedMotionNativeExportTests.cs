using System.Collections.Immutable;
using ReAnimated.Codecs.Anm2;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class DerivedMotionNativeExportTests
{
    [Fact]
    public async Task DerivedClipPreparesToAnm2AndReadsBackRetimedMotion()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        FbxHierarchyPreview hierarchy = FbxHierarchyAuthoring.Preview(
            source,
            FbxHierarchyAuthoringTests.Entity(source, "Child"),
            FbxHierarchyAuthoringTests.Entity(source, "aux_eye"));
        Assert.True(FbxHierarchyAuthoring.TryApply(source, hierarchy, out FbxModelAuthoringImportResult target));
        CustomModelAnimationClip sourceSelection = target.Package.Document.AnimationClips.Single(clip => clip.DerivedMotion is null);

        FbxDerivedMotionPreview derivedPreview = FbxDerivedMotionAuthoring.Preview(target, sourceSelection.Id, "derived_export", 2);
        Assert.True(derivedPreview.CanApply, string.Join(";", derivedPreview.Report.Diagnostics));
        Assert.True(FbxDerivedMotionAuthoring.TryApply(target, derivedPreview, out FbxModelAuthoringImportResult derived));
        CustomModelAnimationClip derivedSelection = derived.Package.Document.AnimationClips.Single(clip => clip.DerivedMotion is not null);
        derived = WithExplicitMotionPolicies(derived);
        derivedSelection = derived.Package.Document.AnimationClips.Single(clip => clip.Id == derivedSelection.Id);
        CustomModelAnimationClip included = derivedSelection with { Included = true };

        PreparedCustomModelAnimationLibrary prepared = await CustomModelAnimationLibraryExporter.PrepareAsync(
            new CustomModelAnimationLibraryRequest
            {
                Model = derived,
                OutputPath = "generic-derived-output",
                AnimationScriptAlias = "generic_derived_library",
                Selections = [included],
            });

        PreparedCustomModelAnimation animation = Assert.Single(prepared.Animations);
        Assert.Equal(included.DisplayName, animation.Name);
        Assert.Equal(included.FrameCount, animation.FrameCount);
        Assert.Equal(included.FrameRate.FramesPerSecond, animation.FramesPerSecond, 6);
        Assert.Equal(included.FrameCount - 1, prepared.Sequences[0].EndFrame);
        Assert.Equal(animation.FramesPerSecond, prepared.Sequences[0].FramesPerSecond, 6);
        Assert.Contains(included.DisplayName, prepared.LooseScriptText, StringComparison.Ordinal);

        Anm2Clip encoded = Anm2Reader.Read(animation.Payload, animation.Anm2FileName);
        Assert.Equal(animation.FrameCount, encoded.Header.FrameCount);
        Dl1PreparedAuthoredRig preparedRig = Dl1CustomModelRigPreparer.Prepare(derived);
        Anm2DomainImportResult readback = Anm2DomainAdapter.ImportBody(
            encoded,
            preparedRig.PreviewRig,
            new FrameRate((int)Math.Round(animation.FramesPerSecond), 1));
        Assert.Equal(animation.FrameCount, readback.Clip.FrameCount);
        Assert.Equal(preparedRig.PreviewRig.BoneCount, encoded.Header.TrackCount);
        Assert.Empty(readback.UnmappedDescriptors);
        AnimationClip derivedClip = derived.AnimationClips[derivedSelection.Id];
        for (int frame = 0; frame < animation.FrameCount; frame++)
        {
            double seconds = readback.Clip.FrameRate.SecondsForFrame(frame);
            SkeletonPose expected = preparedRig.RebasePose(derivedClip.SamplePose(derived.Rig!, seconds));
            SkeletonPose actual = readback.Clip.SamplePose(preparedRig.PreviewRig, seconds);
            for (int bone = 0; bone < preparedRig.PreviewRig.BoneCount; bone++)
                Assert.True(expected.GlobalMatrices[bone].NearlyEquals(actual.GlobalMatrices[bone], 1e-4),
                    $"Prepared/readback global mismatch at frame {frame}, bone {bone}.");
        }
        TransformTrack exportedTrack = Assert.Single(readback.Clip.TransformTracks, track =>
            preparedRig.PreviewRig.Bones[track.BoneIndex].Name == "Child");
        Assert.Equal(0, exportedTrack.Keyframes[0].Frame);
        Assert.Equal(animation.FrameCount - 1, exportedTrack.Keyframes[^1].Frame);
        // The child retains its local pose while the animated root moves it globally.
        Assert.Contains(readback.Clip.TransformTracks, track => track.Keyframes.Any(key =>
            (track.Keyframes[0].Value.Translation - key.Value.Translation).Length > 1e-5 ||
            Math.Abs(QuaternionD.Dot(track.Keyframes[0].Value.Rotation, key.Value.Rotation)) < 1 - 1e-5 ||
            (track.Keyframes[0].Value.Scale - key.Value.Scale).Length > 1e-5));
    }

    private static FbxModelAuthoringImportResult WithExplicitMotionPolicies(FbxModelAuthoringImportResult model)
    {
        CustomModelDocument document = model.Package.Document;
        RiggingSession session = document.RiggingSession!;
        ImmutableArray<RigParentObservation> observed = RiggingSessions.ObserveSourceHierarchy(document);
        var evidence = new RigEvidenceReference
        {
            Id = "generic-clip-policy",
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = document.Source.ContentSha256,
            Description = "Generic explicit motion component policy for export verification.",
        };
        ImmutableArray<AnimationComponentPolicy> policies = document.CreateEffectiveBones()
            .Select((bone, index) => new AnimationComponentPolicy
            {
                EntityId = observed[index].EntityId,
                Position = new RigChannelOwnership { Owners = [RigComponentOwner.Clip], Evidence = [evidence] },
                Rotation = new RigChannelOwnership { Owners = [RigComponentOwner.Clip], Evidence = [evidence] },
                Scale = new RigChannelOwnership { Owners = [RigComponentOwner.Clip], Evidence = [evidence] },
                EmittedMask = RigAnimationComponents.Position | RigAnimationComponents.Rotation | RigAnimationComponents.Scale,
                LodRuleId = "generic-lod-rule",
                AnimationLod = RigAnimationLod.Lod0,
                LodEvidence = [evidence],
            }).ToImmutableArray();
        RiggingSession changed = RiggingSessions.Change(session,
            session with { Recipe = session.Recipe with { ComponentPolicies = policies } }, RiggingEditKind.Motion);
        CustomModelDocument updated = document with { RiggingSession = changed };
        updated.Validate();
        return model with { Package = model.Package with { Document = updated } };
    }
}

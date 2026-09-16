using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class FbxDerivedMotionAuthoringTests : IDisposable
{
    private readonly string _directory = RpackTestData.CreateTemporaryDirectory();

    [Fact]
    public void PreviewCreatesSeparateExcludedClipAndKeepsSourceImmutable()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip sourceClip = Assert.Single(source.Package.Document.AnimationClips);
        ImmutableArray<byte> sourceBytes = source.Package.SourceFbx;
        ImmutableArray<CustomModelAnimationClip> sourceMetadata = source.Package.Document.AnimationClips;

        FbxDerivedMotionPreview preview = FbxDerivedMotionAuthoring.Preview(
            source, sourceClip.Id, "derived_review", sampleMultiplier: 1);
        Assert.True(preview.CanApply, string.Join("; ", preview.Report.Diagnostics));
        FbxModelAuthoringImportResult candidate = preview.PreviewModel!;
        CustomModelAnimationClip derived = candidate.Package.Document.AnimationClips.Single(
            clip => clip.Id == preview.ClipId);

        Assert.NotEqual(sourceClip.Id, derived.Id);
        Assert.False(derived.Included);
        Assert.NotNull(derived.DerivedMotion);
        Assert.Equal(sourceClip.Id, derived.DerivedMotion!.SourceClipId);
        Assert.NotEqual(derived.Id, derived.DerivedMotion.SourceClipId);
        Assert.Equal(source.Package.Document.Source.ContentSha256, derived.DerivedMotion.SourceFileSha256);
        Assert.Equal(derived.Id, DerivedAnimationDataCodec.Deserialize(
            candidate.Package.DerivedAnimationPayloads[derived.Id].AsSpan()).ClipId);
        Assert.True(sourceBytes.AsSpan().SequenceEqual(source.Package.SourceFbx.AsSpan()));
        Assert.Equal<CustomModelAnimationClip>(sourceMetadata, source.Package.Document.AnimationClips);
        Assert.True(sourceBytes.AsSpan().SequenceEqual(candidate.Package.SourceFbx.AsSpan()));
    }

    [Fact]
    public void DerivedClipAndPayloadSurviveSaveAndReopen()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip sourceClip = Assert.Single(source.Package.Document.AnimationClips);
        FbxDerivedMotionPreview preview = FbxDerivedMotionAuthoring.Preview(
            source, sourceClip.Id, "derived_reopen", sampleMultiplier: 1);
        Assert.True(preview.CanApply, string.Join("; ", preview.Report.Diagnostics));
        Assert.True(FbxDerivedMotionAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult applied));
        CustomModelAnimationClip expected = applied.Package.Document.AnimationClips.Single(
            clip => clip.Id == preview.ClipId);
        ImmutableArray<byte> expectedPayload = applied.Package.DerivedAnimationPayloads[expected.Id];

        string path = Path.Combine(_directory, "derived-reopen.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(applied.Package, path);
        FbxModelAuthoringImportResult reopened = FbxModelAuthoringImporter.ImportPackage(
            CustomModelPackageSerializer.Load(path));
        CustomModelAnimationClip actual = reopened.Package.Document.AnimationClips.Single(
            clip => clip.Id == expected.Id);

        Assert.False(actual.Included);
        Assert.Equal(expected.DerivedMotion, actual.DerivedMotion);
        Assert.True(expectedPayload.AsSpan().SequenceEqual(reopened.Package.DerivedAnimationPayloads[actual.Id].AsSpan()));
        Assert.True(applied.Package.SourceFbx.AsSpan().SequenceEqual(reopened.Package.SourceFbx.AsSpan()));
        AnimationClip expectedDecoded = applied.AnimationClips[expected.Id];
        AnimationClip actualDecoded = reopened.AnimationClips[actual.Id];
        Assert.Equal(expectedDecoded.Name, actualDecoded.Name);
        Assert.Equal(expectedDecoded.FrameCount, actualDecoded.FrameCount);
        var expectedTransforms = expectedDecoded.TransformTracks.ToDictionary(track =>
            applied.Package.Document.CreateEffectiveBones()[track.BoneIndex].Name, StringComparer.Ordinal);
        var actualTransforms = actualDecoded.TransformTracks.ToDictionary(track =>
            reopened.Package.Document.CreateEffectiveBones()[track.BoneIndex].Name, StringComparer.Ordinal);
        Assert.Equal(expectedTransforms.Keys.Order(StringComparer.Ordinal), actualTransforms.Keys.Order(StringComparer.Ordinal));
        foreach (string name in expectedTransforms.Keys)
            Assert.Equal<TransformKeyframe>(expectedTransforms[name].Keyframes, actualTransforms[name].Keyframes);
        Assert.Equal(expectedDecoded.ScalarTracks.Select(track => track.ChannelName).Order(StringComparer.Ordinal),
            actualDecoded.ScalarTracks.Select(track => track.ChannelName).Order(StringComparer.Ordinal));
        foreach (ScalarTrack expectedTrack in expectedDecoded.ScalarTracks)
            Assert.Equal<ScalarKeyframe>(expectedTrack.Keyframes,
                actualDecoded.ScalarTracks.Single(track => track.ChannelName == expectedTrack.ChannelName).Keyframes);
    }

    [Fact]
    public void StaleTargetRemainsStoredButDecodedExportAvailabilityIsRemoved()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip sourceClip = Assert.Single(source.Package.Document.AnimationClips);
        FbxDerivedMotionPreview preview = FbxDerivedMotionAuthoring.Preview(
            source, sourceClip.Id, "derived_stale", sampleMultiplier: 1);
        Assert.True(preview.CanApply, string.Join("; ", preview.Report.Diagnostics));
        Assert.True(FbxDerivedMotionAuthoring.TryApply(source, preview, out FbxModelAuthoringImportResult applied));
        CustomModelAnimationClip derived = applied.Package.Document.AnimationClips.Single(
            clip => clip.Id == preview.ClipId);
        FbxHierarchyPreview hierarchy = FbxHierarchyAuthoring.Preview(
            applied, FbxHierarchyAuthoringTests.Entity(applied, "Child"),
            FbxHierarchyAuthoringTests.Entity(applied, "aux_eye"));
        Assert.True(hierarchy.HasChanges);
        Assert.True(FbxHierarchyAuthoring.TryApply(applied, hierarchy, out FbxModelAuthoringImportResult staleModel));
        string path = Path.Combine(_directory, "derived-stale.dlrmodel");
        CustomModelPackageSerializer.SaveAtomic(staleModel.Package, path);
        CustomModelPackage loaded = CustomModelPackageSerializer.Load(path);
        FbxModelAuthoringImportResult decoded = FbxModelAuthoringImporter.ImportPackage(loaded);

        Assert.Contains(decoded.Package.Document.AnimationClips, clip => clip.Id == derived.Id);
        Assert.True(decoded.Package.DerivedAnimationPayloads.ContainsKey(derived.Id));
        Assert.DoesNotContain(decoded.AnimationClips.Keys, id => id == derived.Id);
        Assert.Contains(decoded.Package.Document.Diagnostics, diagnostic => diagnostic.Code == FbxDerivedMotionAuthoring.StaleDiagnosticCode);
        Assert.Throws<InvalidOperationException>(() => FbxDerivedMotionAuthoring.ValidateExport(
            decoded, derived));
    }

    [Fact]
    public void DerivationCanFollowAnActualReparentedSourceRig()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        FbxHierarchyPreview hierarchy = FbxHierarchyAuthoring.Preview(
            source, FbxHierarchyAuthoringTests.Entity(source, "Child"),
            FbxHierarchyAuthoringTests.Entity(source, "aux_eye"));
        Assert.True(FbxHierarchyAuthoring.TryApply(source, hierarchy, out FbxModelAuthoringImportResult reparented));
        CustomModelAnimationClip original = reparented.Package.Document.AnimationClips.First(
            clip => clip.DerivedMotion is null);

        FbxDerivedMotionPreview preview = FbxDerivedMotionAuthoring.Preview(
            reparented, original.Id, "derived_after_reparent", sampleMultiplier: 1);

        Assert.True(preview.CanApply, string.Join("; ", preview.Report.Diagnostics));
        Assert.NotNull(preview.PreviewModel);
        Assert.NotEqual(original.Id, preview.ClipId);
    }

    [Fact]
    public void ComponentMaskRefusesDerivedMotionThatNeedsSuppressedChannels()
    {
        FbxModelAuthoringImportResult source = FbxHierarchyAuthoringTests.Source();
        CustomModelAnimationClip original = source.Package.Document.AnimationClips.First(
            clip => clip.DerivedMotion is null);
        FbxDerivedMotionPreview preview = FbxDerivedMotionAuthoring.Preview(
            source, original.Id, "derived_mask_review", sampleMultiplier: 1);
        Assert.True(preview.CanApply, string.Join("; ", preview.Report.Diagnostics));
        FbxModelAuthoringImportResult candidate = preview.PreviewModel!;
        AnimationClip decoded = candidate.AnimationClips[preview.ClipId];
        TransformTrack track = decoded.TransformTracks.First(track => track.Keyframes.Length > 1);
        CustomModelBone targetBone = candidate.Package.Document.CreateEffectiveBones()[track.BoneIndex];
        Guid targetEntity = RiggingSessions.ObserveSourceHierarchy(candidate.Package.Document)[track.BoneIndex].EntityId;
        RiggingSession session = candidate.Package.Document.RiggingSession!;
        CustomModelDocument blockedDocument = candidate.Package.Document with
        {
            RiggingSession = session with
            {
                Recipe = session.Recipe with
                {
                    ComponentPolicies = [new AnimationComponentPolicy
                    {
                        EntityId = targetEntity,
                        EmittedMask = RigAnimationComponents.None,
                    }],
                },
            },
        };
        blockedDocument.Validate();

        Assert.Throws<InvalidOperationException>(() => FbxDerivedMotionAuthoring.ValidateExport(
            candidate with { Package = candidate.Package with { Document = blockedDocument } },
            candidate.Package.Document.AnimationClips.Single(clip => clip.Id == preview.ClipId)));
        Assert.NotEmpty(targetBone.Name);
    }

    public void Dispose() => RpackTestData.DeleteTemporaryDirectory(_directory);

    [Fact]
    public void ChangedSourceRetainsDerivedHistoryWithoutExportingIt()
    {
        var source=FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(),"original.fbx");
        source=source with{Package=source.Package with{Document=source.Package.Document with
            {RiggingSession=RiggingSessions.Create(source.Package.Document,RigStudioEntryPath.RepairExistingRig)}}};
        var preview=FbxDerivedMotionAuthoring.Preview(source,source.Package.Document.AnimationClips[0].Id,"historical_motion",1);
        Assert.True(preview.CanApply,string.Join(";",preview.Report.Diagnostics));
        var replacement=FbxModelAuthoringImporter.PreviewReimport(preview.PreviewModel!.Package,
            BlenderFbxStrictValidationTests.CreateSourceWeightFixture(),"replacement.fbx").Replacement;
        Assert.Contains(replacement.Package.Document.AnimationClips,c=>c.Id==preview.ClipId);
        Assert.True(preview.PreviewModel.Package.DerivedAnimationPayloads[preview.ClipId].AsSpan().SequenceEqual(replacement.Package.DerivedAnimationPayloads[preview.ClipId].AsSpan()));
        Assert.False(replacement.AnimationClips.ContainsKey(preview.ClipId));
        Assert.Contains(replacement.Package.Document.Diagnostics,d=>d.Code==FbxDerivedMotionAuthoring.StaleDiagnosticCode);
    }
}

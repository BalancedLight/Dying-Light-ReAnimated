using ReAnimated.Codecs;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

/// <summary>
/// Optional exact controls supplied by an ignored local manifest. Retail or
/// third-party FBX bytes are never copied into the repository.
/// </summary>
public sealed class FbxAnimationDomainCompatibilityTests
{
    [ExternalCorpusFact(Timeout = 120_000)]
    [Trait("Gate", "ExternalFbxAnimationDomain")]
    public async Task ImportsConfiguredAnimationControlsWithoutMaterializingModelTopology()
    {
        ExternalCorpusManifest? manifest = ExternalCorpusManifest.LoadOptional();
        if (manifest is null)
        {
            throw new InvalidOperationException(
                "External animation controls were expected to be configured before execution.");
        }

        foreach (ExternalCorpusControl control in manifest.RequireControls("animation-domain"))
        {
            string path = control.RequireExistingFile();
            await control.VerifySha256Async(path);
            var file = new FileInfo(path);
            Assert.Equal(control.RequireInt64("fileBytes"), file.Length);

            var decoder = new FbxAnimationDecoder();
            FbxCoreAnimationImportResult result =
                await decoder.DecodeFileAsync(
                    path,
                    new FbxCoreAnimationImportOptions
                    {
                        SamplingFrameRate = new FrameRate(30, 1),
                    });

            Assert.Equal(FbxReadPurpose.Animation, result.Scene.Document.ReadPurpose);
            Assert.Equal(control.RequireString("animationStackName"), result.AnimationStack.Name);
            Assert.Equal(control.RequireInt32("rigBoneCount"), result.Rig.BoneCount);
            Assert.Equal(control.RequireInt32("frameCount"), result.Clip.FrameCount);
            FbxAnimationStackActivity selectedActivity = Assert.Single(
                result.AnimationStackActivities,
                activity => string.Equals(
                    activity.Stack.Name,
                    control.RequireString("animationStackName"),
                    StringComparison.Ordinal));
            Assert.True(selectedActivity.Usable);
            Assert.Equal(
                control.RequireInt32("changingCurveCount"),
                selectedActivity.ChangingSkeletalBindingCount);
            Assert.NotEmpty(result.SkippedModelDomainPayloads);
            Assert.All(
                result.SkippedModelDomainPayloads,
                static node =>
                {
                    Assert.True(node.ChildPayloadSkipped);
                    Assert.Empty(node.Children);
                });
            Assert.NotEmpty(result.SkippedGeometryPayloads);
            Assert.All(
                result.SkippedGeometryPayloads,
                static node => Assert.Equal("Geometry", node.Name));
            Assert.Equal(
                result.SkippedModelDomainPayloads.Length,
                Assert.Single(result.DomainNotices).AffectedObjectCount);

            int changingCurveCount = result.Scene
                .ReadAnimationBindings(result.AnimationStack)
                .Count(
                    static binding =>
                        binding.Curve.KeyValues.Length > 1 &&
                        binding.Curve.KeyValues.Max() -
                        binding.Curve.KeyValues.Min() > 1.0e-8);
            Assert.Equal(
                control.RequireInt32("changingCurveCount"),
                changingCurveCount);
        }
    }
}

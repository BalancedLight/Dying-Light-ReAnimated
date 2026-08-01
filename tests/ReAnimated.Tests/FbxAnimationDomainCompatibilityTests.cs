using System.Security.Cryptography;
using ReAnimated.Codecs;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;

namespace ReAnimated.Tests;

/// <summary>
/// Optional exact controls for the same external Mixamo compatibility corpus
/// used by the Python animation-domain regressions. Retail or third-party FBX
/// bytes are never copied into the repository.
/// </summary>
public sealed class FbxAnimationDomainCompatibilityTests
{
    private const string DefaultCorpusRoot =
        @"F:\Fbx\AnimationTests";
    private static readonly CorpusControl[] CorpusControls =
    [
        new("Standing Greeting.fbx", 2_184_368, "6630d8a502c078134ce1448ffe993774116bd20f8d33c70109af6bae2af74d6c", 154, 150),
        new("Taunt.fbx", 2_057_072, "8f00711acdcf8c89b0dad1632b940ced9d3b1666f3f194d268f394fce5df9d08", 86, 159),
        new("Right Turn - Binary.fbx", 1_868_080, "fee3ce0c62302b6c81a4e4d09b6fea405f88e2c19622e3e8e9c98da5eedff8ce", 36, 159),
        new("Hip Hop Dancing.fbx", 15_614_288, "92aa6b028f20c12c6f00f39b90e824f19ac93fadffc198deca9e249afd382129", 135, 69),
        new("Thriller Part 1.fbx", 4_234_672, "050989e65457db8c46804ee050517ac439ed8a05e0af9974a75be10c414871b8", 897, 159),
        new("Thriller Part 2.fbx", 3_341_328, "95e0969e255b3129bd5fef4647d00991579b80d181866e13e5b6f45727f1513a", 567, 159),
        new("Thriller Part 3.fbx", 3_880_688, "99c9b2ee13daa526e0ec46a40701f5172c7284363a8b4423be53bc428d0778c6", 769, 159),
        new("Thriller Part 4.fbx", 4_860_848, "4f05a1eff2ad4206db3710a9b91ae66cb0d45b86bf5af943df3567d799854666", 1113, 159),
        new("Walk Strafe Left.fbx", 1_854_576, "a927847a110e203821dfd9199e3a813cd7a40d27dfadc0295b6843b0ec8b9e9f", 45, 69),
        new("Crouch To Stand.fbx", 1_943_680, "7374bff742645b77ee798d9fc6526791f6ace7b840a40933d5a6184d45d960a2", 78, 69),
        new("T-Pose.fbx", 1_741_504, "0488d8e09a780b72413eba3160e45e28dbfa27a3dc98f88b4ab7441e15db4135", 2, 0),
    ];
    public static TheoryData<string, long, string, int, int>
        CompatibilityCorpus
    {
        get
        {
            var result =
                new TheoryData<string, long, string, int, int>();
            foreach (CorpusControl control in CorpusControls)
            {
                result.Add(
                    control.FileName,
                    control.ExpectedFileBytes,
                    control.ExpectedSha256,
                    control.ExpectedFrameCount,
                    control.ExpectedChangingCurveCount);
            }

            return result;
        }
    }

    [ExternalFbxAnimationDomainTheory(Timeout = 120_000)]
    [MemberData(nameof(CompatibilityCorpus))]
    [Trait("Gate", "ExternalFbxAnimationDomain")]
    public async Task ImportsAnimationWithoutMaterializingModelTopology(
        string fileName,
        long expectedFileBytes,
        string expectedSha256,
        int expectedFrameCount,
        int expectedChangingCurveCount)
    {
        string corpusRoot =
            Environment.GetEnvironmentVariable(
                "DLR_FBX_ANIMATION_CORPUS_ROOT")
            ?? DefaultCorpusRoot;
        string path = Path.Combine(corpusRoot, fileName);
        Assert.True(
            File.Exists(path),
            $"External FBX control disappeared after test discovery: {path}");

        var file = new FileInfo(path);
        Assert.Equal(expectedFileBytes, file.Length);
        using (FileStream stream = File.OpenRead(path))
        {
            string actualSha256 = Convert.ToHexString(
                    SHA256.HashData(stream))
                .ToLowerInvariant();
            Assert.Equal(expectedSha256, actualSha256);
        }

        var decoder = new FbxAnimationDecoder();
        FbxCoreAnimationImportResult result =
            await decoder.DecodeFileAsync(
                path,
                new FbxCoreAnimationImportOptions
                {
                    SamplingFrameRate = new FrameRate(30, 1),
                });

        Assert.Equal(FbxReadPurpose.Animation, result.Scene.Document.ReadPurpose);
        Assert.Equal("mixamo.com", result.AnimationStack.Name);
        Assert.Equal(65, result.Rig.BoneCount);
        Assert.Equal(expectedFrameCount, result.Clip.FrameCount);
        FbxAnimationStackActivity selectedActivity = Assert.Single(
            result.AnimationStackActivities,
            static activity =>
                activity.Stack.Name == "mixamo.com");
        Assert.True(selectedActivity.Usable);
        Assert.Equal(
            expectedChangingCurveCount,
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
            expectedChangingCurveCount,
            changingCurveCount);
    }

    private static string ResolveCorpusRoot() =>
        Environment.GetEnvironmentVariable(
            "DLR_FBX_ANIMATION_CORPUS_ROOT")
        ?? DefaultCorpusRoot;

    private sealed class ExternalFbxAnimationDomainTheoryAttribute :
        TheoryAttribute
    {
        public ExternalFbxAnimationDomainTheoryAttribute()
        {
            string root = ResolveCorpusRoot();
            string[] missing = CorpusControls
                .Select(static control => control.FileName)
                .Where(fileName =>
                    !File.Exists(Path.Combine(root, fileName)))
                .ToArray();
            if (missing.Length > 0)
            {
                Skip =
                    $"External FBX animation-domain corpus is unavailable or incomplete at '{root}' ({missing.Length:N0} of {CorpusControls.Length:N0} controls missing).";
            }
        }
    }

    private sealed record CorpusControl(
        string FileName,
        long ExpectedFileBytes,
        string ExpectedSha256,
        int ExpectedFrameCount,
        int ExpectedChangingCurveCount);
}

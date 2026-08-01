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
        @"[local external corpus]";
    private static readonly CorpusControl[] CorpusControls =
    [
        new("external-animation-control.fbx", 2_184_368, "local-corpus-sha256", 154, 150),
        new("external-animation-control.fbx", 2_057_072, "local-corpus-sha256", 86, 159),
        new("external-animation-control.fbx", 1_868_080, "local-corpus-sha256", 36, 159),
        new("external-animation-control.fbx", 15_614_288, "local-corpus-sha256", 135, 69),
        new("external-animation-control.fbx", 4_234_672, "local-corpus-sha256", 897, 159),
        new("external-animation-control.fbx", 3_341_328, "local-corpus-sha256", 567, 159),
        new("external-animation-control.fbx", 3_880_688, "local-corpus-sha256", 769, 159),
        new("external-animation-control.fbx", 4_860_848, "local-corpus-sha256", 1113, 159),
        new("external-animation-control.fbx", 1_854_576, "local-corpus-sha256", 45, 69),
        new("external-animation-control.fbx", 1_943_680, "local-corpus-sha256", 78, 69),
        new("external-animation-control.fbx", 1_741_504, "local-corpus-sha256", 2, 0),
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

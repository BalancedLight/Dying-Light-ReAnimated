using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.Project;
using ReAnimated.Evaluation;

namespace ReAnimated.Tests;

public sealed class ProjectMorphBindingResolverTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-MorphBindings-{Guid.NewGuid():N}");

    [Fact]
    public void ManySourceToOneMappingRoundTripsAndPreservesMetadata()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        Guid sourceAssetId = Guid.NewGuid();
        ProjectMorphBinding[] bindings =
        [
            new()
            {
                SourceChannel = "jaw_primary",
                SourceValueUnit = ProjectMorphSourceValueUnit.Percent,
                TargetMorph = "morph_jaw_open",
                TargetDescriptorHash = 0x11111111,
                Weight = 1,
                Bias = 0.1,
                Confidence = 0.92,
                Method = "semantic_alias",
                IsReviewed = true,
                IsLocked = true,
            },
            new()
            {
                SourceChannel = "jaw_secondary",
                TargetMorph = "morph_jaw_open",
                TargetDescriptorHash = 0x11111111,
                Weight = 0.5,
                Bias = -0.05,
                Confidence = 0.78,
                Method = "shape_alias:exact_alias",
                IsReviewed = true,
                IsLocked = true,
            },
        ];
        DlraProject project = DlraProject.Create("Facial mapping") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/body.fbx",
                    ContentSha256 = new string('A', 64),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "Face",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "retail:face",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                    MimicProfileId = "builtin:human_common46",
                    MimicMappingFingerprint = new string('B', 64),
                    MorphBindings = [.. bindings],
                },
            ],
        };
        string path = Path.Combine(
            _temporaryDirectory,
            "face.dlraproj");

        ProjectSerializer.SaveAtomic(project, path);
        ProjectAnimation reopened = Assert.Single(
            ProjectSerializer.Load(path).Animations);

        Assert.Equal(2, reopened.MorphBindings.Length);
        Assert.Equal(
            ProjectMorphSourceValueUnit.Percent,
            reopened.MorphBindings[0].SourceValueUnit);
        Assert.Equal(0.1, reopened.MorphBindings[0].Bias);
        Assert.Equal(
            "shape_alias:exact_alias",
            reopened.MorphBindings[1].Method);
        Assert.All(
            reopened.MorphBindings,
            static binding =>
            {
                Assert.True(binding.IsReviewed);
                Assert.True(binding.IsLocked);
            });
        RigDefinition rig = CreateRig();
        ImmutableArray<MorphChannelBinding> resolved =
            ProjectMorphBindingResolver.Resolve(
                reopened.MorphBindings,
                rig,
                ProjectMorphBindingResolutionMode.Export);
        MorphEvaluationResult result = MorphEvaluator.Evaluate(
            new Dictionary<string, double>
            {
                ["jaw_primary"] = 0.2,
                ["jaw_secondary"] = 0.4,
            },
            rig,
            0,
            PreviewProfile.RawAuthoring,
            EvaluationPurpose.Export,
            resolved);
        Assert.Equal(
            0.45,
            result.AuthoredWeights["morph_jaw_open"],
            12);
    }

    [Fact]
    public void DescriptorMismatchFailsBeforeEvaluation()
    {
        ProjectMorphBinding binding = new()
        {
            SourceChannel = "jaw",
            TargetMorph = "morph_jaw_open",
            TargetDescriptorHash = 0x22222222,
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProjectMorphBindingResolver.Resolve(
                [binding],
                CreateRig(),
                ProjectMorphBindingResolutionMode.Preview));

        Assert.Contains(
            "expects descriptor 0x22222222",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SuggestedBindingCanBePreviewedButCannotBeExported()
    {
        ProjectMorphBinding suggestion = new()
        {
            SourceChannel = "jaw",
            TargetMorph = "morph_jaw_open",
            TargetDescriptorHash = 0x11111111,
            Confidence = 0.84,
            Method = "semantic_alias",
        };

        MorphChannelBinding preview = Assert.Single(
            ProjectMorphBindingResolver.Resolve(
                [suggestion],
                CreateRig(),
                ProjectMorphBindingResolutionMode.Preview));
        Assert.True(preview.Enabled);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ProjectMorphBindingResolver.Resolve(
                [suggestion],
                CreateRig(),
                ProjectMorphBindingResolutionMode.Export));

        Assert.Contains(
            "not reviewed and locked for export",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledSuggestionDoesNotRequireExportApproval()
    {
        ProjectMorphBinding suggestion = new()
        {
            SourceChannel = "jaw",
            TargetMorph = "morph_jaw_open",
            TargetDescriptorHash = 0x11111111,
            Enabled = false,
            Confidence = 0.84,
            Method = "semantic_alias",
        };

        MorphChannelBinding resolved = Assert.Single(
            ProjectMorphBindingResolver.Resolve(
                [suggestion],
                CreateRig(),
                ProjectMorphBindingResolutionMode.Export));

        Assert.False(resolved.Enabled);
    }

    [Fact]
    public void MissingReviewFieldsFromEarlierFreshSchemaRemainUnapproved()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        Guid sourceAssetId = Guid.NewGuid();
        string path = Path.Combine(
            _temporaryDirectory,
            "pre-review-state.dlraproj");
        DlraProject project = DlraProject.Create("Earlier fresh schema") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/face.fbx",
                    ContentSha256 = new string('A', 64),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    Name = "Face",
                    SourceAssetId = sourceAssetId,
                    TargetRigId = "retail:face",
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                    MorphBindings =
                    [
                        new ProjectMorphBinding
                        {
                            SourceChannel = "jaw",
                            TargetMorph = "morph_jaw_open",
                            TargetDescriptorHash = 0x11111111,
                            Method = "semantic_alias",
                        },
                    ],
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, path);
        JsonObject root = JsonNode.Parse(
            File.ReadAllText(path))!.AsObject();
        JsonObject binding = root["animations"]!.AsArray()[0]!
            ["morphBindings"]!.AsArray()[0]!.AsObject();
        Assert.True(binding.Remove("isReviewed"));
        Assert.True(binding.Remove("isLocked"));
        File.WriteAllText(path, root.ToJsonString());

        ProjectMorphBinding reopened = Assert.Single(
            Assert.Single(
                ProjectSerializer.Load(path).Animations).MorphBindings);
        Assert.False(reopened.IsReviewed);
        Assert.False(reopened.IsLocked);
        Assert.Throws<InvalidDataException>(
            () => ProjectMorphBindingResolver.Resolve(
                [reopened],
                CreateRig(),
                ProjectMorphBindingResolutionMode.Export));
    }

    private static RigDefinition CreateRig() =>
        new(
            "retail:face",
            "Retail face",
            [
                new BoneDefinition(
                    0,
                    "root",
                    -1,
                    TransformTRS.Identity,
                    BoneKind.Root,
                    descriptorHash: 0xABCDEF01),
            ],
            [
                new MorphChannelDefinition(
                    0,
                    "morph_jaw_open",
                    0x11111111,
                    "face.jaw"),
            ]);

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

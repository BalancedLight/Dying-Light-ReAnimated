using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReAnimated.Codecs.Anm2;

namespace ReAnimated.Tests;

public sealed class Anm2ProvenanceCodecTests : IDisposable
{
    private static readonly string SourceFbxSha256 =
        new('A', 64);

    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Anm2Provenance-{Guid.NewGuid():N}");

    [Fact]
    public void ProvenanceIsDeterministicAndHashGated()
    {
        byte[] payload = "animation"u8.ToArray();
        string path = WriteSource("clip", payload);
        Anm2ProvenanceDocument document =
            CreateDocument(payload);

        string sidecar = Anm2ProvenanceCodec.Write(
            path,
            document);
        byte[] first = File.ReadAllBytes(sidecar);
        Anm2ProvenanceCodec.Write(path, document);

        Assert.Equal(first, File.ReadAllBytes(sidecar));
        Assert.Equal((byte)'\n', first[^1]);
        using (JsonDocument canonical =
               JsonDocument.Parse(first))
        {
            string[] properties = canonical.RootElement
                .EnumerateObject()
                .Select(static property => property.Name)
                .ToArray();
            Assert.Equal(
                properties.Order(
                        StringComparer.Ordinal)
                    .ToArray(),
                properties);
        }

        Anm2ProvenanceLoadResult loaded =
            Anm2ProvenanceCodec.Load(path);
        Assert.True(loaded.IsValid);
        Assert.Equal(
            "in_place",
            loaded.Document!.RootMotionMode);
        Assert.Equal(
            "lock_initial_heading",
            loaded.Document.RootHeadingMode);
        Assert.Equal(24.0, loaded.Document.SampleFps);
        Assert.Equal(24.0, loaded.Document.SourceFbxFps);

        File.WriteAllBytes(path, "changed"u8.ToArray());
        Anm2ProvenanceLoadResult mismatch =
            Anm2ProvenanceCodec.Load(path);
        Assert.Equal(
            Anm2ProvenanceStatus.HashMismatch,
            mismatch.Status);
        Assert.Equal("hash_mismatch", mismatch.StatusName);
        Assert.Null(mismatch.Document);
        Assert.Single(mismatch.Warnings);
    }

    [Fact]
    public void MissingAndMalformedProvenanceAreNonfatal()
    {
        string path = WriteSource(
            "clip",
            "animation"u8.ToArray());

        Anm2ProvenanceLoadResult missing =
            Anm2ProvenanceCodec.Load(path);
        Assert.Equal(
            Anm2ProvenanceStatus.Missing,
            missing.Status);
        Assert.Null(missing.Document);
        Assert.Empty(missing.Warnings);

        File.WriteAllText(
            Anm2ProvenanceCodec.GetSidecarPath(path),
            "{}",
            Encoding.UTF8);
        Anm2ProvenanceLoadResult invalid =
            Anm2ProvenanceCodec.Load(path);
        Assert.Equal(
            Anm2ProvenanceStatus.Invalid,
            invalid.Status);
        Assert.Equal("invalid", invalid.StatusName);
        Assert.Null(invalid.Document);
        Assert.Single(invalid.Warnings);
    }

    [Fact]
    public void OptionalSourceAnimationStackRoundTripsAndOldSidecarsRemainValid()
    {
        byte[] payload = "animation"u8.ToArray();
        string path = WriteSource("clip", payload);
        const string selectedTake =
            "Armature|Layer|Backflip";
        Anm2ProvenanceDocument selected =
            CreateDocument(payload) with
            {
                SourceAnimationStack = selectedTake,
            };

        Anm2ProvenanceCodec.Write(path, selected);
        Anm2ProvenanceLoadResult loaded =
            Anm2ProvenanceCodec.Load(path);
        Assert.True(loaded.IsValid);
        Assert.Equal(
            selectedTake,
            loaded.Document!.SourceAnimationStack);

        Anm2ProvenanceCodec.Write(
            path,
            CreateDocument(payload));
        Anm2ProvenanceLoadResult old =
            Anm2ProvenanceCodec.Load(path);
        Assert.True(old.IsValid);
        Assert.Null(old.Document!.SourceAnimationStack);
        string rendered = File.ReadAllText(
            old.SidecarPath,
            Encoding.UTF8);
        Assert.DoesNotContain(
            "\"source_animation_stack\"",
            rendered,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalExportContractProvenanceRoundTrips()
    {
        byte[] payload = "animation"u8.ToArray();
        string path = WriteSource("clip", payload);
        ImmutableArray<ImmutableArray<double>> matrix =
        [
            [1.0, 0.0, 0.0, 0.0],
            [0.0, 1.0, 0.0, 0.0],
            [0.0, 0.0, -1.0, 0.0],
            [0.0, 0.0, 0.0, 1.0],
        ];
        Anm2ProvenanceDocument document =
            CreateDocument(payload) with
            {
                FbxAnm2ExportBehavior = "legacy_5_0",
                SamplerContract =
                    "dlr_0_5_0_global_bind_basis_v1",
                SourceTargetCompatibilityClass =
                    "exact_target_subset",
                BindRetainedBones = ["optional_helper"],
                WrapperReflectionDetected = true,
                WrapperCanonicalized = true,
                WrapperMatrix = matrix,
                BilateralSemanticPolicy =
                    "preserve_source_names",
                BilateralSwapApplied = false,
                BilateralSwappedRowCount = 0,
                PostCanonicalizationMirrorConjugationApplied =
                    false,
            };

        Anm2ProvenanceCodec.Write(path, document);
        Anm2ProvenanceLoadResult loaded =
            Anm2ProvenanceCodec.Load(path);

        Assert.True(loaded.IsValid);
        Anm2ProvenanceDocument actual =
            loaded.Document!;
        Assert.Equal(
            "legacy_5_0",
            actual.FbxAnm2ExportBehavior);
        Assert.Equal(
            "dlr_0_5_0_global_bind_basis_v1",
            actual.SamplerContract);
        Assert.Equal(
            "exact_target_subset",
            actual.SourceTargetCompatibilityClass);
        Assert.Equal(
            ["optional_helper"],
            actual.BindRetainedBones.ToArray());
        Assert.True(actual.WrapperReflectionDetected);
        Assert.True(actual.WrapperCanonicalized);
        Assert.Equal(
            matrix.Length,
            actual.WrapperMatrix.Length);
        for (int row = 0; row < matrix.Length; row++)
        {
            Assert.Equal(
                matrix[row].ToArray(),
                actual.WrapperMatrix[row].ToArray());
        }
        Assert.Equal(
            "preserve_source_names",
            actual.BilateralSemanticPolicy);
        Assert.False(actual.BilateralSwapApplied);
        Assert.Equal(0, actual.BilateralSwappedRowCount);
        Assert.False(
            actual
                .PostCanonicalizationMirrorConjugationApplied);
    }

    [Theory]
    [MemberData(nameof(MalformedScalarMetadata))]
    public void MalformedScalarMetadataIsOneNonfatalAdvisory(
        string field,
        string rawJson)
    {
        byte[] payload = "animation"u8.ToArray();
        string path = WriteSource(field, payload);
        string sidecar = Anm2ProvenanceCodec.Write(
            path,
            CreateDocument(payload));
        JsonObject root = JsonNode.Parse(
                File.ReadAllText(sidecar, Encoding.UTF8))!
            .AsObject();
        root[field] = JsonNode.Parse(rawJson);
        File.WriteAllText(
            sidecar,
            root.ToJsonString(),
            Encoding.UTF8);

        Anm2ProvenanceLoadResult loaded =
            Anm2ProvenanceCodec.Load(path);

        Assert.Equal(
            Anm2ProvenanceStatus.Invalid,
            loaded.Status);
        Assert.Null(loaded.Document);
        Assert.Single(loaded.Warnings);
    }

    public static TheoryData<string, string>
        MalformedScalarMetadata =>
        new()
        {
            { "sample_fps", "true" },
            { "source_duration_seconds", "false" },
            { "frame_count", "true" },
            { "schema_version", "true" },
            { "source_animation_stack", "42" },
            { "sampler_contract", "42" },
            {
                "source_target_compatibility_class",
                "42"
            },
            { "bind_retained_bones", "[42]" },
            { "playback_fps", new string('1', 401) },
            {
                "source_duration_seconds",
                new string('1', 401)
            },
        };

    [Fact]
    public void FrameCountMismatchIsDistinctAndNonfatal()
    {
        byte[] payload = "animation"u8.ToArray();
        string path = WriteSource("frame-count", payload);
        Anm2ProvenanceCodec.Write(
            path,
            CreateDocument(payload));

        Anm2ProvenanceLoadResult mismatch =
            Anm2ProvenanceCodec.Load(
                path,
                knownAnm2Sha256:
                    CreateDocument(payload).Anm2Sha256,
                expectedFrameCount: 375);

        Assert.Equal(
            Anm2ProvenanceStatus.FrameCountMismatch,
            mismatch.Status);
        Assert.Equal(
            "frame_count_mismatch",
            mismatch.StatusName);
        Assert.Null(mismatch.Document);
        Assert.Single(mismatch.Warnings);
    }

    [Fact]
    public void ReaderRejectsOversizeAndExcessiveDepthWithinOneAdvisory()
    {
        string path = WriteSource(
            "bounded",
            "animation"u8.ToArray());
        string sidecar =
            Anm2ProvenanceCodec.GetSidecarPath(path);
        File.WriteAllText(
            sidecar,
            new string(
                'x',
                Anm2ProvenanceCodec.MaximumSidecarBytes +
                1),
            Encoding.UTF8);
        Anm2ProvenanceLoadResult oversized =
            Anm2ProvenanceCodec.Load(path);
        Assert.Equal(
            Anm2ProvenanceStatus.Invalid,
            oversized.Status);
        Assert.Single(oversized.Warnings);

        string nested = "{}";
        for (int index = 0;
             index <
             Anm2ProvenanceCodec.MaximumJsonDepth + 1;
             index++)
        {
            nested = $"{{\"nested\":{nested}}}";
        }

        File.WriteAllText(
            sidecar,
            nested,
            Encoding.UTF8);
        Anm2ProvenanceLoadResult tooDeep =
            Anm2ProvenanceCodec.Load(path);
        Assert.Equal(
            Anm2ProvenanceStatus.Invalid,
            tooDeep.Status);
        Assert.Single(tooDeep.Warnings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(
                _temporaryDirectory,
                recursive: true);
        }
    }

    private string WriteSource(
        string fileName,
        byte[] payload)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            fileName + ".anm2");
        File.WriteAllBytes(path, payload);
        return path;
    }

    private static Anm2ProvenanceDocument CreateDocument(
        byte[] payload) =>
        Anm2ProvenanceCodec.Create(
            payload,
            sourceFbx: "source.fbx",
            sourceFbxSha256: SourceFbxSha256,
            sourceFbxFps: 24.0,
            sampleFps: 24.0,
            playbackFps: 30.0,
            sourceDurationSeconds: 12.5,
            frameCount: 376,
            rootMotionMode: "in_place",
            rootHeadingMode: "lock_initial_heading");
}

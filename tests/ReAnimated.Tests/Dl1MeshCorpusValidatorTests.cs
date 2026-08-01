using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.DL1.Assets.Providers;

namespace ReAnimated.Tests;

public sealed class Dl1MeshCorpusValidatorTests
{
    [Fact]
    public async Task PresentationCallbackPublishesBoundedSuccessFacts()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Rp6lChunkCache cache =
                CreateCache(directory);
            int callbackCount = 0;
            var validator = new Dl1MeshCorpusValidator(
                cache,
                presentationValidator:
                    (resource, mesh, cancellationToken) =>
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();
                        callbackCount++;
                        Assert.Equal(
                            "presentation_success",
                            resource.Name);
                        Assert.True(mesh.HasDecodedGeometry);
                        return ValueTask.FromResult(
                            new Dl1MeshCorpusPresentationResult(
                                Dl1MeshCorpusPresentationDisposition
                                    .Renderable,
                                1,
                                0,
                                0,
                                []));
                    });
            Rp6lResourceDescriptor resource =
                CreateResource(
                    "presentation_success",
                    itemCount: 5);
            Dl1MeshData mesh = CreateTriangleMesh(
                resource.Name,
                CompactMeshEntityType.Mesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                []);

            Dl1MeshCorpusResourceResult result =
                await validator
                    .ValidateDecodedMeshPresentationAsync(
                        resource,
                        mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Equal(1, callbackCount);
            Dl1MeshCorpusPresentationResult presentation =
                Assert.IsType<
                    Dl1MeshCorpusPresentationResult>(
                    result.Presentation);
            Assert.True(presentation.Passed);
            Assert.Equal(1, presentation.RenderMeshCount);
            Assert.Empty(presentation.Issues);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task PresentationCallbackFailureBlocksOnlyItsResource()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(
                cache,
                presentationValidator:
                    static (_, _, _) =>
                        throw new InvalidDataException(
                            "Synthetic presentation failure."));
            Rp6lResourceDescriptor resource =
                CreateResource(
                    "presentation_failure",
                    itemCount: 5);
            Dl1MeshData mesh = CreateTriangleMesh(
                resource.Name,
                CompactMeshEntityType.Mesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                []);

            Dl1MeshCorpusResourceResult result =
                await validator
                    .ValidateDecodedMeshPresentationAsync(
                        resource,
                        mesh);

            Assert.False(result.Passed);
            Assert.Equal(
                Dl1MeshCorpusDisposition.Blocked,
                result.Disposition);
            Assert.True(result.HasDecodedGeometry);
            Assert.Equal(3, result.VertexCount);
            Assert.Equal(3, result.IndexCount);
            Dl1MeshCorpusPresentationResult presentation =
                Assert.IsType<
                    Dl1MeshCorpusPresentationResult>(
                    result.Presentation);
            Assert.False(presentation.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1PRESENT002" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error &&
                    issue.Message.Contains(
                        "Synthetic presentation failure",
                        StringComparison.Ordinal));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task ZeroDrawRequiresExplicitNonRenderableDisposition()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            await using Rp6lChunkCache cache =
                CreateCache(directory);
            Rp6lResourceDescriptor resource =
                CreateResource(
                    "zero_draw",
                    itemCount: 5);
            Dl1MeshData mesh = CreateTriangleMesh(
                resource.Name,
                CompactMeshEntityType.Mesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                []);
            var unknownZeroDraw =
                new Dl1MeshCorpusValidator(
                    cache,
                    presentationValidator:
                        static (_, _, _) =>
                            ValueTask.FromResult(
                                new Dl1MeshCorpusPresentationResult(
                                    Dl1MeshCorpusPresentationDisposition
                                        .Renderable,
                                    0,
                                    0,
                                    0,
                                    [])));
            var explicitNonDisplay =
                new Dl1MeshCorpusValidator(
                    cache,
                    presentationValidator:
                        static (_, _, _) =>
                            ValueTask.FromResult(
                                new Dl1MeshCorpusPresentationResult(
                                    Dl1MeshCorpusPresentationDisposition
                                        .ExplicitlyNonRenderable,
                                    0,
                                    0,
                                    0,
                                    [])));

            Dl1MeshCorpusResourceResult blocked =
                await unknownZeroDraw
                    .ValidateDecodedMeshPresentationAsync(
                        resource,
                        mesh);
            Dl1MeshCorpusResourceResult classified =
                await explicitNonDisplay
                    .ValidateDecodedMeshPresentationAsync(
                        resource,
                        mesh);

            Assert.False(blocked.Passed);
            Assert.Equal(
                Dl1MeshCorpusDisposition.Blocked,
                blocked.Disposition);
            Assert.Contains(
                blocked.Issues,
                static issue =>
                    issue.Code == "DL1PRESENT001");
            Assert.True(classified.Passed);
            Assert.Equal(
                Dl1MeshCorpusDisposition
                    .NonDisplayGeometry,
                classified.Disposition);
            Assert.Equal(
                Dl1MeshCorpusPresentationDisposition
                    .ExplicitlyNonRenderable,
                classified.Presentation?.Disposition);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task IsolatesMalformedResourceAndClassifiesKnownContainer()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            byte[] metadata =
                RpackTestData.BuildCompactMeshPayload();
            string goodDirectory =
                Path.Combine(directory, "good");
            string badDirectory =
                Path.Combine(directory, "bad");
            string good = await RpackTestData.WriteArchiveAsync(
                goodDirectory,
                "known_container",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(1, metadata),
                    new RpackTestItem(2, []),
                    new RpackTestItem(3, []),
                ],
                RpackTestCompression.None);
            string bad = await RpackTestData.WriteArchiveAsync(
                badDirectory,
                "unsupported_layout",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(1, metadata),
                    new RpackTestItem(2, []),
                    new RpackTestItem(3, []),
                    new RpackTestItem(4, []),
                ],
                RpackTestCompression.None);
            await using var cache = CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);

            IReadOnlyList<Dl1MeshCorpusPackResult> results =
                await validator.ValidateAsync(
                [
                    new RpackSource(good, 2),
                    new RpackSource(bad, 1),
                ]);

            Assert.Equal(2, results.Count);
            Dl1MeshCorpusResourceResult container =
                Assert.Single(results[0].MeshResources);
            Assert.True(container.Passed);
            Assert.Equal(
                Dl1MeshCorpusDisposition.MetadataOnlyContainer,
                container.Disposition);
            Assert.False(container.HasDecodedGeometry);
            Dl1MeshCorpusResourceResult blocked =
                Assert.Single(results[1].MeshResources);
            Assert.False(blocked.Passed);
            Assert.Equal(
                Dl1MeshCorpusDisposition.Blocked,
                blocked.Disposition);
            Assert.Contains(
                blocked.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS002" &&
                    issue.Message.Contains(
                        "4 RP6L items",
                        StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void AcceptsHalf4PositionTopologyAndDecodedMaterialSlot()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Rp6lResourceDescriptor resource =
                CreateResource("half4_static", itemCount: 5);
            Dl1MeshData mesh = CreateTriangleMesh(
                "half4_static",
                CompactMeshEntityType.Mesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                []);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(resource, mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Equal(
                Dl1MeshCorpusDisposition.GeometryDecoded,
                result.Disposition);
            Assert.True(result.HasDecodedGeometry);
            Assert.Equal(3, result.VertexCount);
            Assert.Equal(3, result.IndexCount);
            Assert.Equal(1, result.DecodedMaterialSlotCount);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void BlocksZeroWeightSkinningInsteadOfSilentlyAcceptingIt()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Rp6lResourceDescriptor resource =
                CreateResource("zero_weight_skin", itemCount: 5);
            Dl1MeshData mesh = CreateTriangleMesh(
                "zero_weight_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0],
                includeBone: true);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(resource, mesh);

            Assert.False(result.Passed);
            Assert.True(result.HasDecodedGeometry);
            Assert.True(result.IsSkinned);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS057" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void SinglePaletteWithoutBlendIndicesUsesStaticRuntimePath()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData mesh = CreateTriangleMesh(
                "rigid_palette_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0],
                includeBone: true,
                includeBlendStreams: false);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "rigid_palette_skin",
                        itemCount: 5),
                    mesh);

            Assert.True(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066" &&
                    issue.Message.Contains(
                        "ignores the palette",
                        StringComparison.OrdinalIgnoreCase) &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code is
                        "DL1CORPUS053" or
                        "DL1CORPUS054");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void AcceptsRuntimeIgnoredMultiPaletteAtExactRootStructure()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData mesh = CreateTriangleMesh(
                "ambiguous_palette_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0, 0],
                includeBone: true,
                includeBlendStreams: false);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "ambiguous_palette_skin",
                        itemCount: 5),
                    mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning &&
                    issue.Message.Contains(
                        "palette",
                        StringComparison.OrdinalIgnoreCase) &&
                    issue.Message.Contains(
                        "not authorable",
                        StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code is
                        "DL1CORPUS054" or
                        "DL1CORPUS055");
            Dl1MeshSubmesh submesh = Assert.Single(
                Assert.Single(mesh.Surfaces).Submeshes);
            Assert.Equal(
                Dl1SkinBindingMode
                    .StaticEntityTransformIgnoredPalette,
                submesh.SkinBindingMode);
            Dl1RigPromotionAnalysis analysis =
                Dl1RigPromotionPolicy.Analyze(
                    mesh.Hierarchy,
                    mesh.Surfaces);
            Assert.Empty(
                analysis.DeclaredPaletteEntityIndexes);
            Assert.Empty(
                analysis.EffectiveSkinEntityIndexes);
            Assert.False(analysis.HasUnresolvedSkinBindings);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Theory]
    [InlineData(false, 1UL, 0UL)]
    [InlineData(false, 0UL, 1UL)]
    [InlineData(true, 0UL, 0UL)]
    public void RuntimePathIgnoresParentageAndOpaqueBonePointers(
        bool parented,
        ulong rawBoneIndexPointer0,
        ulong rawBoneIndexPointer1)
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData mesh = CreateTriangleMesh(
                "unsafe_palette_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0, 0],
                includeBone: true,
                includeBlendStreams: false,
                meshParentIndex: parented ? (short)0 : (short)-1,
                rawBoneIndexPointer0:
                    rawBoneIndexPointer0,
                rawBoneIndexPointer1:
                    rawBoneIndexPointer1);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "unsafe_palette_skin",
                        itemCount: 5),
                    mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS055");
            Assert.Equal(
                Dl1SkinBindingMode
                    .StaticEntityTransformIgnoredPalette,
                Assert.Single(
                    Assert.Single(mesh.Surfaces)
                        .Submeshes)
                    .SkinBindingMode);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void KeepsNonFiniteEntityWorldTransformBlocked()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData source = CreateTriangleMesh(
                "non_finite_palette_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0],
                includeBone: true,
                includeBlendStreams: false);
            int entityIndex =
                Assert.Single(source.Surfaces).EntityIndex;
            CompactMeshEntity[] entities =
                source.Hierarchy.Entities.ToArray();
            entities[entityIndex] = entities[entityIndex] with
            {
                LocalMatrix = new CompactMatrix3x4(
                    1, 0, 0, float.NaN,
                    0, 1, 0, 0,
                    0, 0, 1, 0),
            };
            Dl1MeshSurface surface =
                Assert.Single(source.Surfaces);
            surface = surface with
            {
                Submeshes =
                [
                    Assert.Single(surface.Submeshes) with
                    {
                        SkinBindingMode =
                            Dl1SkinBindingMode
                                .UnresolvedMissingBlendStreams,
                    },
                ],
            };
            Dl1MeshData mesh = source with
            {
                Hierarchy = source.Hierarchy with
                {
                    Entities = entities,
                },
                Surfaces = [surface],
            };

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "non_finite_palette_skin",
                        itemCount: 5),
                    mesh);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS055" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void ResolvedZeroTechniqueNoBlendDrawIsNotAuthorableSkinning()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData source = CreateTriangleMesh(
                "zero_technique_palette_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0, 0],
                includeBone: true,
                includeBlendStreams: false);
            Dl1MaterialSlot sourceSlot =
                Assert.Single(source.MaterialSlots);
            Dl1MeshData mesh = source with
            {
                MaterialSlots =
                [
                    sourceSlot with
                    {
                        BindingStatus =
                            Dl1MaterialBindingStatus.Resolved,
                        ResolvedMaterial = new(
                            "custom_zero.mat",
                            0,
                            0,
                            []),
                    },
                ],
            };

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "zero_technique_palette_skin",
                        itemCount: 5),
                    mesh);

            Assert.True(
                result.Passed,
                string.Join(
                    Environment.NewLine,
                    result.Issues.Select(static issue =>
                        $"{issue.Code}: {issue.Message}")));
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS058" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning &&
                    issue.Message.Contains(
                        "non-display material",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code is
                        "DL1CORPUS055" or
                        "DL1CORPUS066");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void KeepsPartialBlendDeclarationBlocked()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator = new Dl1MeshCorpusValidator(cache);
            Dl1MeshData source = CreateTriangleMesh(
                "partial_blend_skin",
                CompactMeshEntityType.SkinnedMesh,
                Vector4.Zero,
                new Dl1BoneIndex4(0, 0, 0, 0),
                [0, 0],
                includeBone: true);
            Dl1MeshSurface sourceSurface =
                Assert.Single(source.Surfaces);
            Dl1MeshSurface surface = sourceSurface with
            {
                VertexLayout = sourceSurface.VertexLayout with
                {
                    Elements = sourceSurface.VertexLayout.Elements
                        .Where(static element =>
                            element.Semantic !=
                                Dl1VertexSemantic.BlendIndices)
                        .ToArray(),
                },
                Submeshes =
                [
                    Assert.Single(sourceSurface.Submeshes) with
                    {
                        SkinBindingMode =
                            Dl1SkinBindingMode
                                .UnresolvedMissingBlendStreams,
                    },
                ],
            };
            Dl1MeshData mesh = source with
            {
                Surfaces = [surface],
            };

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "partial_blend_skin",
                        itemCount: 5),
                    mesh);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS056" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS066");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void AcceptsRawBindPreviewWithoutFabricatingAuthoringTrs()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator =
                new Dl1MeshCorpusValidator(cache);
            Dl1MeshData mesh =
                CreateNonTrsRawBindMesh(
                    nonTrsEntityIndex: 1);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "non_trs_helper",
                        itemCount: 5),
                    mesh);

            Assert.True(result.Passed);
            Assert.Null(mesh.Rig);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1MESH014" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS043" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Warning &&
                    issue.Message.Contains(
                        "retargeting",
                        StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS041");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                directory);
        }
    }

    [Fact]
    public void KeepsWeightedNonTrsBoneBlocked()
    {
        string directory =
            RpackTestData.CreateTemporaryDirectory();
        try
        {
            using Rp6lChunkCache cache =
                CreateCache(directory);
            var validator =
                new Dl1MeshCorpusValidator(cache);
            Dl1MeshData mesh =
                CreateNonTrsRawBindMesh(
                    nonTrsEntityIndex: 0);

            Dl1MeshCorpusResourceResult result =
                validator.ValidateDecodedMesh(
                    CreateResource(
                        "weighted_non_trs",
                        itemCount: 5),
                    mesh);

            Assert.False(result.Passed);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1MESH014" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.Contains(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS041" &&
                    issue.Severity ==
                        Dl1MeshCorpusIssueSeverity.Error);
            Assert.DoesNotContain(
                result.Issues,
                static issue =>
                    issue.Code == "DL1CORPUS043");
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(
                directory);
        }
    }

    private static Rp6lChunkCache CreateCache(string directory) =>
        new(new Rp6lChunkCacheOptions
        {
            CacheDirectory = Path.Combine(directory, "cache"),
            MaximumMemoryBytes = 0,
            MaximumMemoryEntryBytes = 0,
            MaximumDiskBytes = 64 * 1024 * 1024,
        });

    private static Rp6lResourceDescriptor CreateResource(
        string name,
        int itemCount) =>
        new(
            0,
            name,
            Rp6lResourceTypes.Mesh,
            0,
            0,
            itemCount,
            []);

    private static Dl1MeshData CreateTriangleMesh(
        string name,
        CompactMeshEntityType meshType,
        Vector4 weights,
        Dl1BoneIndex4 boneIndexes,
        IReadOnlyList<short> palette,
        bool includeBone = false,
        bool includeBlendStreams = true,
        short meshParentIndex = -1,
        ulong rawBoneIndexPointer0 = 0,
        ulong rawBoneIndexPointer1 = 0)
    {
        List<CompactMeshEntity> entities = [];
        RigDefinition? rig = null;
        if (includeBone)
        {
            entities.Add(new CompactMeshEntity(
                0,
                "root",
                0,
                new CompactBounds(0, 0, 0, 1, 1, 1),
                -1,
                CompactMeshEntityType.Bone,
                0,
                1,
                CompactMatrix3x4.Identity,
                CompactMatrix3x4.Identity,
                0,
                0));
            rig = new RigDefinition(
                $"test:{name}",
                name,
                [
                    new BoneDefinition(
                        0,
                        "root",
                        -1,
                        TransformTRS.Identity,
                        BoneKind.Root,
                        requiredForExport: true),
                ]);
        }

        int meshEntityIndex = entities.Count;
        CompactMeshEntity meshEntity = new CompactMeshEntity(
            meshEntityIndex,
            "surface",
            0,
            new CompactBounds(0, 0, 0, 1, 1, 1),
            meshParentIndex,
            meshType,
            0,
            1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            0,
            0)
        {
            RawBoneIndexPointer0 =
                rawBoneIndexPointer0,
            RawBoneIndexPointer1 =
                rawBoneIndexPointer1,
        };
        entities.Add(meshEntity);
        var hierarchy = new CompactMeshDocument(
            entities.Count,
            entities.Count,
            0,
            entities,
            []);
        Dl1MeshVertex[] vertices =
        [
            Vertex(new Vector3(0, 0, 0)),
            Vertex(new Vector3(1, 0, 0)),
            Vertex(new Vector3(0, 1, 0)),
        ];
        Dl1MeshVertex Vertex(Vector3 position) =>
            new(
                position,
                Vector3.UnitZ,
                new Vector4(1, 0, 0, 1),
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One,
                weights,
                boneIndexes);
        var layout = new Dl1VertexLayout(
            includeBone && includeBlendStreams ? 16 : 8,
            includeBone && includeBlendStreams
                ?
                [
                new Dl1VertexElement(
                    Dl1VertexSemantic.Position,
                    0,
                    Dl1VertexElementFormat.Half4,
                    0,
                    0),
                new Dl1VertexElement(
                    Dl1VertexSemantic.BlendWeights,
                    0,
                    Dl1VertexElementFormat.Byte4Normalized,
                    0,
                    8),
                new Dl1VertexElement(
                    Dl1VertexSemantic.BlendIndices,
                    0,
                    Dl1VertexElementFormat.Byte4,
                    0,
                    12),
                ]
                :
                [
                    new Dl1VertexElement(
                        Dl1VertexSemantic.Position,
                        0,
                        Dl1VertexElementFormat.Half4,
                        0,
                        0),
                ]);
        var mappedSubmesh = new Dl1MeshSubmesh(
            0,
            0,
            3,
            0,
            palette);
        Dl1SkinBindingMode bindingMode =
            !includeBone
                ? Dl1SkinBindingMode.None
                : includeBlendStreams
                    ? Dl1SkinBindingMode
                        .ExplicitVertexWeights
                    : Dl1SkinBindingPolicy.Classify(
                        layout,
                        vertices,
                        [0, 1, 2],
                        mappedSubmesh,
                        meshEntity,
                        hierarchy
                            .ReconstructGlobalMatrices()[
                                meshEntityIndex]);
        Dl1MeshSurface surface = new(
            "surface",
            meshEntityIndex,
            0,
            0,
            layout,
            new Dl1MeshBufferSlice(
                3,
                0,
                includeBone && includeBlendStreams ? 48 : 24,
                includeBone && includeBlendStreams ? 16 : 8),
            new Dl1MeshBufferSlice(4, 0, 6, 2),
            3,
            3,
            vertices,
            [0, 1, 2],
            [
                mappedSubmesh with
                    {
                        SkinBindingMode = bindingMode,
                    },
            ]);
        return new Dl1MeshData(
            name,
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            rig,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "material",
                    0,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);
    }

    private static Dl1MeshData CreateNonTrsRawBindMesh(
        int nonTrsEntityIndex)
    {
        Dl1MeshData source = CreateTriangleMesh(
            "non_trs",
            CompactMeshEntityType.SkinnedMesh,
            Vector4.UnitX,
            new Dl1BoneIndex4(0, 0, 0, 0),
            [0],
            includeBone: true);
        CompactMeshEntity sourceSurface =
            source.Hierarchy.Entities[1];
        CompactMatrix3x4 shear = new(
            1, 0.5f, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0);
        CompactMeshEntity[] entities =
        [
            source.Hierarchy.Entities[0] with
            {
                LocalMatrix =
                    nonTrsEntityIndex == 0
                        ? shear
                        : CompactMatrix3x4.Identity,
            },
            new CompactMeshEntity(
                1,
                "collision_helper",
                0,
                new CompactBounds(0, 0, 0, 1, 1, 1),
                0,
                CompactMeshEntityType.Helper,
                0,
                1,
                nonTrsEntityIndex == 1
                    ? shear
                    : CompactMatrix3x4.Identity,
                CompactMatrix3x4.Identity,
                0,
                0),
            sourceSurface with
            {
                Index = 2,
            },
        ];
        return source with
        {
            Hierarchy = new CompactMeshDocument(
                entities.Length,
                2,
                0,
                entities,
                []),
            Rig = null,
            Surfaces =
            [
                source.Surfaces[0] with
                {
                    EntityIndex = 2,
                },
            ],
            Diagnostics =
            [
                new Dl1MeshDiagnostic(
                    "DL1MESH014",
                    Dl1MeshDiagnosticSeverity.Error,
                    "The retail hierarchy could not be promoted to an authoring rig: a singular or sheared local transform."),
            ],
        };
    }
}

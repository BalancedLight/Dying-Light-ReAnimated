using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Rp6l;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class AppDl1MeshPreviewAdapterTests
{
    [Fact]
    public void PlayerFppDefaultSkinControlRequiresExactIdentityAndContent()
    {
        Assert.True(
            Dl1MeshPreviewAdapter.IsValidatedPlayer1FppSelection(
                "player_1_fpp",
                Dl1MeshPreviewAdapter
                    .ValidatedPlayer1FppResourceSha256));
        Assert.False(
            Dl1MeshPreviewAdapter.IsValidatedPlayer1FppSelection(
                "player_2_fpp",
                Dl1MeshPreviewAdapter
                    .ValidatedPlayer1FppResourceSha256));
        Assert.False(
            Dl1MeshPreviewAdapter.IsValidatedPlayer1FppSelection(
                "player_1_fpp",
                new string('0', 64)));
        Assert.False(
            Dl1MeshPreviewAdapter.IsValidatedPlayer1FppSelection(
                "player_1_fpp",
                null));
    }

    [Fact]
    public void PreviewSelectsOneLodAndOmitsNonDisplayMaterials()
    {
        CompactMeshEntity[] entities =
        [
            CreateStaticEntity(0, "visible"),
            CreateStaticEntity(1, "shadow"),
            CreateStaticEntity(2, "null_decal"),
        ];
        var hierarchy = new CompactMeshDocument(
            entities.Length,
            entities.Length,
            0,
            entities,
            []);
        Dl1MeshSurface visibleLod0 =
            CreateStaticSurface(
                "visible",
                entityIndex: 0,
                lodIndex: 0,
                materialSlotIndex: 0);
        Dl1MeshSurface visibleLod1 =
            CreateStaticSurface(
                "visible",
                entityIndex: 0,
                lodIndex: 1,
                materialSlotIndex: 0);
        Dl1MeshSurface shadow =
            CreateStaticSurface(
                "shadow",
                entityIndex: 1,
                lodIndex: 0,
                materialSlotIndex: 1);
        Dl1MeshSurface nullDecal =
            CreateStaticSurface(
                "null_decal",
                entityIndex: 2,
                lodIndex: 0,
                materialSlotIndex: 2);
        var mesh = new Dl1MeshData(
            "synthetic",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [visibleLod0, visibleLod1, shadow, nullDecal],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
                new Dl1MaterialSlot(
                    1,
                    "SHADOW_CASTER.MAT",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
                new Dl1MaterialSlot(
                    2,
                    "NULL.MAT",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);

        MeshRenderData renderMesh = Assert.Single(payload.Meshes);
        Assert.Equal(
            "synthetic/visible/lod0/partall",
            renderMesh.Id);
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "LOD 1 was omitted",
                    StringComparison.Ordinal));
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "shadow_caster",
                    StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "null.mat",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ZeroTechniquePolicyRequiresResolvedEvidenceOrExactRetailIdentity()
    {
        Assert.True(
            Dl1PreviewMaterialPolicy
                .IsKnownZeroTechniqueMaterial(
                    "NULL.MAT"));
        Assert.True(
            Dl1PreviewMaterialPolicy
                .IsKnownZeroTechniqueMaterial(
                    "default.mat"));
        Assert.False(
            Dl1PreviewMaterialPolicy
                .IsKnownZeroTechniqueMaterial(
                    "null_decal.mat"));

        var zeroTechnique = new Dl1MaterialSlot(
            0,
            "custom_zero.mat",
            null,
            null,
            Dl1MaterialBindingStatus.Resolved)
        {
            ResolvedMaterial = new(
                "custom_zero.mat",
                0,
                0,
                []),
        };
        var visible = zeroTechnique with
        {
            DatabaseName = "custom_visible.mat",
            ResolvedMaterial = new(
                "custom_visible.mat",
                0,
                1,
                []),
        };
        var replacedDeclaredNull = visible with
        {
            DeclaredDatabaseName = "null.mat",
            SkinReplacementDatabaseEntryIndex = 7,
        };
        var resolvedVisibleNull = visible with
        {
            DatabaseName = "null.mat",
        };

        Assert.True(
            Dl1PreviewMaterialPolicy
                .IsNonDisplayZeroTechnique(
                    zeroTechnique));
        Assert.False(
            Dl1PreviewMaterialPolicy
                .IsNonDisplayZeroTechnique(
                    visible));
        Assert.False(
            Dl1PreviewMaterialPolicy
                .IsNonDisplayZeroTechnique(
                    replacedDeclaredNull));
        Assert.False(
            Dl1PreviewMaterialPolicy
                .IsNonDisplayZeroTechnique(
                    resolvedVisibleNull));
    }

    [Fact]
    public void DefaultSkinMaterialDoesNotExposeDeclaredShadowCasterDraw()
    {
        CompactMeshEntity[] entities =
        [
            CreateStaticEntity(0, "shadow"),
        ];
        var mesh = new Dl1MeshData(
            "synthetic",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            new CompactMeshDocument(
                entities.Length,
                entities.Length,
                0,
                entities,
                []),
            null,
            [],
            [
                CreateStaticSurface(
                    "shadow",
                    entityIndex: 0,
                    lodIndex: 0,
                    materialSlotIndex: 0),
            ],
            [
                new Dl1MaterialSlot(
                    0,
                    "body.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded)
                {
                    DeclaredDatabaseName =
                        "shadow_caster.mat",
                    SkinReplacementDatabaseEntryIndex = 1,
                    AppliedSkinName = "Default",
                },
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);

        Assert.Empty(payload.Meshes);
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "shadow_caster",
                    StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BindPoseOnlyFallbackRejectsWeightedSingularEntity()
    {
        CompactMeshEntity singularBone =
            CreateStaticEntity(0, "singular") with
            {
                EntityType = CompactMeshEntityType.Bone,
                LocalMatrix = new CompactMatrix3x4(
                    0, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0),
            };
        var hierarchy = new CompactMeshDocument(
            2,
            2,
            0,
            [
                singularBone,
                CreateStaticEntity(1, "weighted") with
                {
                    EntityType =
                        CompactMeshEntityType.SkinnedMesh,
                },
            ],
            []);
        Dl1MeshSurface surface =
            CreateStaticSurface(
                "weighted",
                entityIndex: 1,
                lodIndex: 0,
                materialSlotIndex: 0) with
            {
                Vertices =
                [
                    CreateStaticVertex(0, 0, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                    CreateStaticVertex(1, 0, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                    CreateStaticVertex(0, 1, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                ],
                Submeshes =
                [
                    new Dl1MeshSubmesh(
                        0,
                        0,
                        3,
                        0,
                        [0])
                    {
                        SkinBindingMode =
                            Dl1SkinBindingMode
                                .ExplicitVertexWeights,
                    },
                ],
            };
        var mesh = new Dl1MeshData(
            "singular_weighted",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            [
                new Dl1MeshDiagnostic(
                    "DL1MESH014",
                    Dl1MeshDiagnosticSeverity.Error,
                    "The retail hierarchy could not be promoted to an authoring rig: The retail compact hierarchy contains a singular or sheared local transform."),
            ]);

        Assert.False(
            Dl1MeshPreviewAdapter
                .CanPublishBindPoseOnlyPreview(mesh));
        Assert.Empty(
            Dl1MeshPreviewAdapter.Convert(mesh).Meshes);
    }

    [Fact]
    public void BindPoseOnlyFallbackPublishesUnweightedNonTrsHelper()
    {
        CompactMeshEntity[] entities =
        [
            CreateStaticEntity(0, "weighted_bone") with
            {
                EntityType = CompactMeshEntityType.Bone,
            },
            CreateStaticEntity(1, "unweighted_helper") with
            {
                EntityType = CompactMeshEntityType.Helper,
                LocalMatrix = new CompactMatrix3x4(
                    0, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0),
            },
            CreateStaticEntity(2, "surface") with
            {
                EntityType = CompactMeshEntityType.SkinnedMesh,
            },
        ];
        var hierarchy = new CompactMeshDocument(
            entities.Length,
            entities.Length,
            0,
            entities,
            []);
        Dl1MeshSurface surface =
            CreateStaticSurface(
                "surface",
                entityIndex: 2,
                lodIndex: 0,
                materialSlotIndex: 0) with
            {
                Vertices =
                [
                    CreateStaticVertex(0, 0, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                    CreateStaticVertex(1, 0, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                    CreateStaticVertex(0, 1, 0) with
                    {
                        BlendWeights = Vector4.UnitX,
                    },
                ],
                Submeshes =
                [
                    new Dl1MeshSubmesh(
                        0,
                        0,
                        3,
                        0,
                        [0])
                    {
                        SkinBindingMode =
                            Dl1SkinBindingMode
                                .ExplicitVertexWeights,
                    },
                ],
            };
        var mesh = new Dl1MeshData(
            "unweighted_non_trs_helper",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            [
                new Dl1MeshDiagnostic(
                    "DL1MESH014",
                    Dl1MeshDiagnosticSeverity.Error,
                    "The retail hierarchy could not be promoted to an authoring rig: The retail compact hierarchy contains a singular or sheared local transform."),
            ]);

        Assert.True(
            Dl1MeshPreviewAdapter
                .CanPublishBindPoseOnlyPreview(mesh));
        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);
        Assert.Single(payload.Meshes);
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "outside every effective skin palette",
                    StringComparison.Ordinal));
        Assert.True(
            Dl1MeshPreviewAdapter.HasIdentityBindPalettes(
                payload.Meshes,
                payload.Skeleton));
    }

    [Fact]
    public void BindPoseValidationRejectsNonFiniteEffectiveIndex()
    {
        var mesh = new MeshRenderData(
            "non-finite",
            new MeshVertex[]
            {
                new MeshVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.UnitX,
                    new Vector4(float.NaN, 0, 0, 0)),
            },
            new uint[] { 0, 0, 0 },
            Matrix4x4.Identity,
            new Matrix4x4[] { Matrix4x4.Identity },
            true);
        var skeleton = new SkeletonRenderData(
            [
                new BoneRenderData(
                    "root",
                    -1,
                    Matrix4x4.Identity,
                    Matrix4x4.Identity,
                    false),
            ],
            Matrix4x4.Identity);

        Assert.False(
            Dl1MeshPreviewAdapter.HasIdentityBindPalettes(
                [mesh],
                skeleton));
        MeshRenderData nonFiniteWeight = mesh with
        {
            Vertices =
            new MeshVertex[]
            {
                new MeshVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    new Vector4(float.NaN, 0, 0, 0),
                    Vector4.Zero),
            },
        };
        Assert.False(
            Dl1MeshPreviewAdapter.HasIdentityBindPalettes(
                [nonFiniteWeight],
                skeleton));
        MeshRenderData noEffectiveWeights = mesh with
        {
            Vertices =
            new MeshVertex[]
            {
                new MeshVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    Vector2.Zero,
                    Vector4.Zero,
                    Vector4.Zero),
            },
        };
        Assert.False(
            Dl1MeshPreviewAdapter.HasIdentityBindPalettes(
                [noEffectiveWeights],
                skeleton));
    }

    [Fact]
    public void ValidatedPlayerFppSelectionOmitsStockEditorExcludedSurfaces()
    {
        string[] names =
        [
            "player_4_head",
            "player_1_hand_l_tpp",
            "flashlight",
            "kevin_shirt_fpp",
            "player_1_hand_l_fpp",
        ];
        CompactMeshEntity[] entities = names
            .Select((name, index) =>
                CreateStaticEntity(index, name))
            .ToArray();
        Dl1MeshSurface[] surfaces = names
            .Select((name, index) =>
                CreateStaticSurface(
                    name,
                    index,
                    0,
                    0))
            .ToArray();
        var hierarchy = new CompactMeshDocument(
            entities.Length,
            entities.Length,
            0,
            entities,
            []);
        var mesh = new Dl1MeshData(
            "player_1_fpp",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            surfaces,
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload selected =
            Dl1MeshPreviewAdapter.Convert(
                mesh,
                Dl1MeshPreviewAdapter
                    .ValidatedPlayer1FppResourceSha256);
        Assert.Equal(
            [
                "player_1_fpp/kevin_shirt_fpp/lod0/partall",
                "player_1_fpp/player_1_hand_l_fpp/lod0/partall",
            ],
            selected.Meshes
                .Select(static renderMesh => renderMesh.Id)
                .OrderBy(static id => id));

        Dl1MeshPreviewPayload mismatched =
            Dl1MeshPreviewAdapter.Convert(
                mesh,
                new string('0', 64));
        Assert.Equal(5, mismatched.Meshes.Count);
        Assert.Contains(
            mismatched.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "Default skin/variant visibility was not guessed",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void CompactColumnVectorMatrixConvertsToRendererRowVectorMatrix()
    {
        var compact = new CompactMatrix3x4(
            1.0f, 2.0f, 3.0f, 10.0f,
            4.0f, 5.0f, 6.0f, 20.0f,
            7.0f, 8.0f, 9.0f, 30.0f);
        Vector3 point = new(1.0f, 2.0f, 3.0f);

        Matrix4x4 renderer = Dl1MeshPreviewAdapter.ConvertMatrix(compact);
        Vector3 actual = Vector3.Transform(point, renderer);
        (float X, float Y, float Z) expected =
            compact.TransformPoint(point.X, point.Y, point.Z);

        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Z, actual.Z);
        Assert.Equal(new Vector3(10.0f, 20.0f, 30.0f), renderer.Translation);
    }

    [Fact]
    public async Task DecodedRetailMorphDeltasReachRendererVertices()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture();
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "morph_preview",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(42, fixture.Metadata),
                    new RpackTestItem(42, fixture.Variants),
                    new RpackTestItem(42, [1]),
                    new RpackTestItem(42, fixture.Vertices),
                    new RpackTestItem(42, fixture.Indices),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            Rp6lResourceDescriptor resource =
                Assert.Single(archive.Resources);
            await using Rp6lChunkCache cache = new(new Rp6lChunkCacheOptions
            {
                CacheDirectory = Path.Combine(directory, "cache"),
                MaximumMemoryBytes = 0,
                MaximumMemoryEntryBytes = 0,
                MaximumDiskBytes = 8 * 1024 * 1024,
            });
            Dl1MeshData decoded =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);

            Dl1MeshPreviewPayload payload =
                Dl1MeshPreviewAdapter.Convert(decoded);

            ReAnimated.Renderer.D3D11.MeshRenderData renderMesh =
                Assert.Single(payload.Meshes);
            ReAnimated.Renderer.D3D11.MorphTargetRenderData morph =
                Assert.Single(renderMesh.MorphTargets);
            Assert.Equal("smile", morph.Name);
            Assert.Equal(
                new Vector3(1, 0, 0),
                morph.PositionDeltas.Span[0]);
            Assert.Equal(
                new Vector3(0, -0.5f, 0.25f),
                morph.PositionDeltas.Span[1]);
            Assert.Equal(
                Vector3.Zero,
                morph.PositionDeltas.Span[2]);
            Assert.True(morph.NormalDeltas.IsEmpty);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task SinglePaletteWithoutBlendIndicesUsesStaticEntityTransform()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            CompiledMeshTestFixture fixture =
                RpackTestData.BuildCompiledMeshFixture(
                    includeBlendStreams: false);
            string path = await RpackTestData.WriteArchiveAsync(
                directory,
                "rigid_skin_preview",
                Rp6lResourceTypes.Mesh,
                [
                    new RpackTestItem(42, fixture.Metadata),
                    new RpackTestItem(42, fixture.Variants),
                    new RpackTestItem(42, [1]),
                    new RpackTestItem(42, fixture.Vertices),
                    new RpackTestItem(42, fixture.Indices),
                ],
                RpackTestCompression.None);
            Rp6lArchive archive = await Rp6lArchive.OpenAsync(path);
            Rp6lResourceDescriptor resource =
                Assert.Single(archive.Resources);
            await using Rp6lChunkCache cache = new(
                new Rp6lChunkCacheOptions
                {
                    CacheDirectory =
                        Path.Combine(directory, "cache"),
                    MaximumMemoryBytes = 0,
                    MaximumMemoryEntryBytes = 0,
                    MaximumDiskBytes = 8 * 1024 * 1024,
                });

            Dl1MeshData decoded =
                await Dl1MeshResourceDecoder.DecodeAsync(
                    archive,
                    resource,
                    cache);

            Assert.True(
                decoded.Surfaces.Count == 1,
                string.Join(
                    Environment.NewLine,
                    decoded.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
            Dl1MeshSurface decodedSurface = decoded.Surfaces[0];
            Dl1MeshSubmesh decodedSubmesh =
                Assert.Single(decodedSurface.Submeshes);
            Assert.Equal(
                Dl1SkinBindingMode
                    .StaticEntityTransformIgnoredPalette,
                decodedSubmesh.SkinBindingMode);
            Assert.All(
                decodedSurface.Vertices,
                static vertex =>
                    Assert.Equal(
                        Vector4.Zero,
                        vertex.BlendWeights));

            Dl1MeshPreviewPayload payload =
                Dl1MeshPreviewAdapter.Convert(decoded);

            ReAnimated.Renderer.D3D11.MeshRenderData renderMesh =
                Assert.Single(payload.Meshes);
            Assert.False(renderMesh.IsSkinned);
            Assert.True(renderMesh.InverseBindMatrices.IsEmpty);
            Assert.Equal(
                Matrix4x4.Identity,
                renderMesh.LocalToWorld);
            Assert.All(
                renderMesh.Vertices.ToArray(),
                static vertex =>
                {
                    Assert.Equal(
                        Vector4.Zero,
                        vertex.BoneWeights);
                    Assert.Equal(
                        Vector4.Zero,
                        vertex.BoneIndices);
                });
            Assert.DoesNotContain(
                payload.Diagnostics,
                static diagnostic => diagnostic.Contains(
                    "unresolved skin binding",
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                decoded.Diagnostics,
                static diagnostic =>
                    diagnostic.Code == "DL1MESH016");

            SkeletonRenderData movedSkeleton =
                payload.Skeleton! with
                {
                    RootTransform =
                        Matrix4x4.CreateTranslation(3, 4, 5),
                };
            Assert.True(
                RenderMeshValidation.TryValidate(
                    renderMesh,
                    movedSkeleton,
                    out string? validationError),
                validationError);
            CpuDeformedVertex[] cpuVertices =
                CpuMeshDeformationEvaluator.Evaluate(
                    renderMesh,
                    movedSkeleton,
                    []);
            Assert.Equal(
                Vector3.Zero,
                cpuVertices[0].Position);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void RuntimeIgnoredPalettePublishesStaticEntityTransform()
    {
        CompactMeshEntity bone =
            CreateStaticEntity(0, "root") with
            {
                EntityType = CompactMeshEntityType.Bone,
            };
        CompactMeshEntity entity =
            CreateStaticEntity(1, "composite_part") with
            {
                EntityType =
                    CompactMeshEntityType.SkinnedMesh,
                LocalMatrix = new CompactMatrix3x4(
                    1, 0, 0, 3,
                    0, 1, 0, 4,
                    0, 0, 1, 5),
            };
        var hierarchy = new CompactMeshDocument(
            2,
            2,
            0,
            [bone, entity],
            []);
        Dl1MeshSurface sourceSurface =
            CreateStaticSurface(
                "composite_part",
                entityIndex: 1,
                lodIndex: 0,
                materialSlotIndex: 0);
        Dl1MeshSurface surface = sourceSurface with
        {
            Submeshes =
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    0,
                    [0, 0])
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode
                            .StaticEntityTransformIgnoredPalette,
                },
            ],
        };
        var mesh = new Dl1MeshData(
            "composite",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);

        MeshRenderData renderMesh =
            Assert.Single(payload.Meshes);
        Assert.False(renderMesh.IsSkinned);
        Assert.True(renderMesh.InverseBindMatrices.IsEmpty);
        Assert.Equal(
            Matrix4x4.CreateTranslation(3, 4, 5),
            renderMesh.LocalToWorld);
        Assert.All(
            renderMesh.Vertices.ToArray(),
            static vertex =>
            {
                Assert.Equal(
                    Vector4.Zero,
                    vertex.BoneWeights);
                Assert.Equal(
                    Vector4.Zero,
                    vertex.BoneIndices);
            });
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "serialized palette is retained but ignored",
                    StringComparison.Ordinal) &&
                diagnostic.Contains(
                    "Bone editing is unavailable",
                    StringComparison.Ordinal));

        SkeletonRenderData movedSkeleton =
            payload.Skeleton! with
            {
                RootTransform =
                    Matrix4x4.CreateTranslation(100, 200, 300),
            };
        CpuDeformedVertex[] cpu =
            CpuMeshDeformationEvaluator.Evaluate(
                renderMesh,
                movedSkeleton,
                []);
        Assert.Equal(
            new Vector3(3, 4, 5),
            cpu[0].Position);
        Assert.Equal(
            new Vector3(4, 4, 5),
            cpu[1].Position);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ForgedIgnoredPaletteModeFailsClosedAtPreviewBoundary(
        bool nonFiniteWorld)
    {
        CompactMeshEntity bone =
            CreateStaticEntity(0, "root") with
            {
                EntityType = CompactMeshEntityType.Bone,
            };
        CompactMeshEntity unsafeEntity =
            CreateStaticEntity(1, "unsafe") with
            {
                EntityType =
                    nonFiniteWorld
                        ? CompactMeshEntityType.SkinnedMesh
                        : CompactMeshEntityType.Mesh,
                LocalMatrix =
                    nonFiniteWorld
                        ? new CompactMatrix3x4(
                            1, 0, 0, float.NaN,
                            0, 1, 0, 0,
                            0, 0, 1, 0)
                        : CompactMatrix3x4.Identity,
            };
        var hierarchy = new CompactMeshDocument(
            2,
            2,
            0,
            [bone, unsafeEntity],
            []);
        Dl1MeshSurface sourceSurface =
            CreateStaticSurface(
                "unsafe",
                entityIndex: 1,
                lodIndex: 0,
                materialSlotIndex: 0);
        Dl1MeshSurface surface = sourceSurface with
        {
            Submeshes =
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    0,
                    [0, 0])
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode
                            .StaticEntityTransformIgnoredPalette,
                },
            ],
        };
        var mesh = new Dl1MeshData(
            "unsafe",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);

        Assert.Empty(payload.Meshes);
        Assert.Contains(
            payload.Diagnostics,
            static diagnostic =>
                diagnostic.Contains(
                    "not a skinned-mesh entity with a finite",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void LargeRetailRigUsesCompactPerDrawPalette()
    {
        CompactMeshEntity[] entities =
        [
            .. Enumerable.Range(0, 300)
                .Select(index =>
                    CreateStaticEntity(
                        index,
                        $"bone_{index}") with
                    {
                        EntityType =
                            CompactMeshEntityType.Bone,
                    }),
            CreateStaticEntity(300, "surface") with
            {
                EntityType =
                    CompactMeshEntityType.SkinnedMesh,
            },
        ];
        var hierarchy = new CompactMeshDocument(
            entities.Length,
            entities.Length,
            0,
            entities,
            []);
        Dl1MeshSurface sourceSurface =
            CreateStaticSurface(
                "surface",
                entityIndex: 300,
                lodIndex: 0,
                materialSlotIndex: 0);
        Dl1MeshSurface surface = sourceSurface with
        {
            Vertices = sourceSurface.Vertices
                .Select(vertex => vertex with
                {
                    BlendWeights = Vector4.UnitX,
                })
                .ToArray(),
            Submeshes =
            [
                new Dl1MeshSubmesh(
                    0,
                    0,
                    3,
                    0,
                    [299])
                {
                    SkinBindingMode =
                        Dl1SkinBindingMode
                            .ExplicitVertexWeights,
                },
            ],
        };
        var mesh = new Dl1MeshData(
            "large_rig",
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            null,
            [],
            [surface],
            [
                new Dl1MaterialSlot(
                    0,
                    "visible.mat",
                    null,
                    null,
                    Dl1MaterialBindingStatus.DatabaseNameDecoded),
            ],
            [],
            [],
            []);

        Dl1MeshPreviewPayload payload =
            Dl1MeshPreviewAdapter.Convert(mesh);
        SkeletonRenderData skeleton =
            Assert.IsType<SkeletonRenderData>(
                payload.Skeleton);
        MeshRenderData renderMesh =
            Assert.Single(payload.Meshes);

        Assert.Equal(300, skeleton.Bones.Count);
        Assert.True(renderMesh.IsSkinned);
        Assert.Single(renderMesh.InverseBindMatrices.ToArray());
        Assert.Equal(
            [299],
            renderMesh.SkinBoneIndices.ToArray());
        Assert.All(
            renderMesh.Vertices.ToArray(),
            static vertex =>
                Assert.Equal(
                    Vector4.Zero,
                    vertex.BoneIndices));
        Assert.True(
            RenderMeshValidation.TryValidate(
                renderMesh,
                skeleton,
                out string? validationError),
            validationError);

        BoneRenderData[] movedBones =
            skeleton.Bones.ToArray();
        movedBones[299] = movedBones[299] with
        {
            WorldTransform =
                Matrix4x4.CreateTranslation(4, 5, 6),
        };
        CpuDeformedVertex moved =
            CpuMeshDeformationEvaluator.Evaluate(
                renderMesh,
                skeleton with { Bones = movedBones },
                [])[0];
        Assert.Equal(
            new Vector3(4, 5, 6),
            moved.Position);
    }

    private static CompactMeshEntity CreateStaticEntity(
        int index,
        string name) =>
        new(
            index,
            name,
            0,
            new CompactBounds(0, 0, 0, 1, 1, 1),
            -1,
            CompactMeshEntityType.Mesh,
            0,
            1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            0,
            0);

    private static Dl1MeshSurface CreateStaticSurface(
        string name,
        int entityIndex,
        int lodIndex,
        int materialSlotIndex) =>
        new(
            name,
            entityIndex,
            lodIndex,
            materialSlotIndex,
            new Dl1VertexLayout(
                32,
                [
                    new Dl1VertexElement(
                        Dl1VertexSemantic.Position,
                        0,
                        Dl1VertexElementFormat.Float3,
                        0,
                        0),
                ]),
            new Dl1MeshBufferSlice(3, 0, 96, 32),
            new Dl1MeshBufferSlice(4, 0, 6, 2),
            3,
            3,
            [
                CreateStaticVertex(0, 0, 0),
                CreateStaticVertex(1, 0, 0),
                CreateStaticVertex(0, 1, 0),
            ],
            [0, 1, 2],
            []);

    private static Dl1MeshVertex CreateStaticVertex(
        float x,
        float y,
        float z) =>
        new(
            new Vector3(x, y, z),
            Vector3.UnitZ,
            Vector4.UnitX,
            Vector2.Zero,
            Vector2.Zero,
            Vector4.One,
            Vector4.Zero,
            new Dl1BoneIndex4(0, 0, 0, 0));
}

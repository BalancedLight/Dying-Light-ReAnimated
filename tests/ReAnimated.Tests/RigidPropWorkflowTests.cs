using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Renderer.D3D11;

namespace ReAnimated.Tests;

public sealed class RigidPropWorkflowTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public async Task AuthoredRigidDoorMovesInBothSharedPlaybackViewports(bool externalView, bool loop)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var model = FbxModelAuthoringImporter.Import(CreateDoorFixture(), "hinged-door.fbx");
            model = FbxAuthoredAnimationAuthoring.Create(model, "open", 1, new FrameRate(30, 1));
            var clip = model.Package.Document.AnimationClips.Single(static item => item.AuthoredAnimation is not null);
            var pivot = model.Package.Document.Bones.Single(bone =>
                bone.FbxObjectId == model.Surfaces[0].RigidGeometryOwnerFbxObjectId);
            model = FbxAuthoredAnimationAuthoring.SetKey(model, clip.Id, pivot.Index, 30,
                pivot.LocalBindTransform with { Rotation = QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2) });
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "catalog.sqlite3"),
                Path.Combine(directory, "cache"));
            await using var owner = new MainWindowViewModel(new JsonWorkspaceStateStore(
                Path.Combine(directory, "recovery.json")), new WindowsProjectFileDialogService(), assets);
            owner.Models.CommitProjectRestore(new(model, Path.Combine(directory, "door.dlrmodel"),
                new ReAnimated.Core.Project.ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
            owner.Models.SelectedAnimation = owner.Models.Animations.Single(item => item.Id == clip.Id);
            await owner.Models.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);
            Assert.False(owner.IsTargetPlaybackBlocked, owner.StatusText);
            var sessionField = typeof(MainWindowViewModel).GetField("_customTargetPreviewSession",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var targetSession = Assert.IsType<CustomModelPreviewSession>(sessionField.GetValue(owner));
            Assert.True(targetSession.HasRigidGeometry, "Rigid owner metadata must survive the playback handoff.");
            owner.Timeline.IsPlaying = false;
            owner.Timeline.IsLooping = loop;
            if (externalView) owner.ActiveWorkspaceMode = "FPP";
            owner.Timeline.CurrentFrame = 0;
            var closedSource = owner.SourceViewport.SceneSource.CaptureFrame();
            var closedTarget = owner.TargetViewport.SceneSource.CaptureFrame();
            owner.Timeline.CurrentFrame = 30;
            var openedSource = owner.SourceViewport.SceneSource.CaptureFrame();
            var openedTarget = owner.TargetViewport.SceneSource.CaptureFrame();
            AssertRigidMotion(closedTarget.Meshes, openedTarget.Meshes);
            AssertRigidMotion(closedSource.Meshes, openedSource.Meshes);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    private static void AssertRigidMotion(IReadOnlyList<MeshRenderData> before, IReadOnlyList<MeshRenderData> after)
    {
        MeshRenderData closed = Assert.Single(before);
        MeshRenderData opened = Assert.Single(after);
        Assert.False(opened.IsSkinned);
        Assert.Equal(closed.Id, opened.Id);
        float displacement = 0;
        for (int index = 0; index < closed.Vertices.Length; index++)
        {
            Vector3 first = Vector3.Transform(closed.Vertices.Span[index].Position, closed.LocalToWorld);
            Vector3 second = Vector3.Transform(opened.Vertices.Span[index].Position, opened.LocalToWorld);
            displacement = Math.Max(displacement, Vector3.Distance(first, second));
        }
        Assert.True(displacement > 0.5f, $"Rigid leaf moved only {displacement} units.");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "CustomModelPreview")]
    public async Task PreviewActorScaleScalesRigidGeometryWithItsSkeleton(bool externalView, bool hasRigidOwner)
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            var model = FbxAuthoredAnimationAuthoring.Create(
                FbxModelAuthoringImporter.Import(CreateDoorFixture(hasRigidOwner), "rigid-prop.fbx"),
                "move", 1, new FrameRate(30, 1));
            var clip = model.Package.Document.AnimationClips.Single(static item => item.AuthoredAnimation is not null);
            await using var assets = new Dl1AssetWorkspace(Path.Combine(directory, "catalog.sqlite3"),
                Path.Combine(directory, "cache"));
            await using var owner = new MainWindowViewModel(new JsonWorkspaceStateStore(
                Path.Combine(directory, "recovery.json")), new WindowsProjectFileDialogService(), assets);
            owner.Models.CommitProjectRestore(new(model, Path.Combine(directory, "prop.dlrmodel"),
                new ReAnimated.Core.Project.ProjectModelsWorkspaceState { PackageAssetId = Guid.NewGuid() }));
            owner.Models.SelectedAnimation = owner.Models.Animations.Single(item => item.Id == clip.Id);
            await owner.Models.OpenSelectedAnimationInAnimateCommand.ExecuteAsync(null);
            Assert.False(owner.IsTargetPlaybackBlocked, owner.StatusText);
            owner.Timeline.IsPlaying = false;
            if (externalView) owner.ActiveWorkspaceMode = "FPP";
            owner.Timeline.CurrentFrame = 0;
            var viewport = externalView ? owner.SourceViewport : owner.TargetViewport;
            RenderFrameSnapshot before = viewport.SceneSource.CaptureFrame();

            owner.SecondaryMotion.PreviewActorScale = 2;
            RenderFrameSnapshot after = viewport.SceneSource.CaptureFrame();

            var beforeMesh = Assert.Single(before.Meshes);
            var afterMesh = Assert.Single(after.Meshes);
            Assert.False(afterMesh.IsSkinned);
            Vector3 origin = before.Skeleton!.RootTransform.Translation;
            for (int index = 0; index < beforeMesh.Vertices.Length; index++)
            {
                Vector3 original = Vector3.Transform(beforeMesh.Vertices.Span[index].Position, beforeMesh.LocalToWorld);
                Vector3 scaled = Vector3.Transform(afterMesh.Vertices.Span[index].Position, afterMesh.LocalToWorld);
                Assert.InRange(Vector3.Distance(origin + (original - origin) * 2, scaled), 0, 1e-5f);
            }
            float originalScale = Vector3.TransformNormal(Vector3.UnitX, before.Skeleton.RootTransform).Length();
            float scaledSkeleton = Vector3.TransformNormal(Vector3.UnitX, after.Skeleton!.RootTransform).Length();
            Assert.InRange(Math.Abs(scaledSkeleton - originalScale * 2), 0, 1e-5f);
        }
        finally { RpackTestData.DeleteTemporaryDirectory(directory); }
    }

    [Fact]
    public void RigidPlaybackRetainsPresentationAndAppliesActorTransformOnce()
    {
        var model = FbxModelAuthoringImporter.Import(CreateDoorFixture(), "hinged-door.fbx");
        var session = CustomModelPreviewAdapter.CreateSession(model, CustomModelPreviewMode.SourceFbx);
        var pivot = model.Package.Document.Bones.Single(bone =>
            bone.FbxObjectId == model.Surfaces[0].RigidGeometryOwnerFbxObjectId);
        SkeletonPose bind = model.Rig!.CreateBindPose();
        SkeletonPose pose = bind.WithLocalTransform(pivot.Index, pivot.LocalBindTransform with
            { Translation = pivot.LocalBindTransform.Translation + new Vector3D(0.25, 0, 0) });
        MeshRenderData current = session.Meshes.Single() with
            { IsSelected = true, Tint = new Vector4(0.2f, 0.4f, 0.6f, 1), ProjectionRole = MeshProjectionRole.FppHands };
        TransformMatrix actor = TransformMatrix.CreateTranslation(new(2, 3, 4)) *
            new TransformTRS(Vector3D.Zero, QuaternionD.Identity, new Vector3D(2, 2, 2)).ToMatrix();
        MeshRenderData rendered = session.CreateMeshes(pose, [current], actorWorldTransform: actor).Single();
        Vector3 vertex = current.Vertices.Span[1].Position;
        Vector3D expected = (actor * pose.GlobalMatrices[pivot.Index] *
            bind.GlobalMatrices[pivot.Index].InvertedAffine()).TransformPoint(new(vertex.X, vertex.Y, vertex.Z));
        Vector3 actual = Vector3.Transform(vertex, rendered.LocalToWorld);
        Assert.InRange((new Vector3D(actual.X, actual.Y, actual.Z) - expected).Length, 0, 1e-5);
        Assert.Equal(current.Tint, rendered.Tint);
        Assert.Equal(current.IsSelected, rendered.IsSelected);
        Assert.Equal(current.ProjectionRole, rendered.ProjectionRole);
    }

    [Fact]
    public void AutoImportRetainsUnskinnedAnimatedNullPivotAndItsRigidMeshOwner()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            CreateHingedPropFixture(animateMeshModel: false),
            "hinged-prop.fbx");

        Assert.NotNull(imported.Rig);
        Assert.Contains(imported.Rig!.Bones, bone => bone.Name == "Root");
        Assert.Contains(imported.Rig.Bones, bone => bone.Name == "Child");
        Assert.Single(imported.AnimationClips);
        Assert.Single(imported.Package.Document.AnimationClips,
            clip => clip.Included && clip.HasSkeletalTracks);
        Assert.All(imported.Surfaces, surface => Assert.False(surface.IsSkinned));

        CustomModelMeshPart meshPart = Assert.Single(imported.Package.Document.Meshes,
            mesh => mesh.Name == "RetailMesh");
        Assert.Equal(2L, meshPart.RigidGeometryOwnerFbxObjectId);
    }

    [Fact]
    public void RigidUnskinnedGeometryFollowsItsAnimatedNullOwnerInPreview()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            CreateHingedPropFixture(animateMeshModel: false),
            "hinged-prop-preview.fbx");
        AnimationClip clip = Assert.Single(imported.AnimationClips.Values);
        CustomModelPreviewSession session = CustomModelPreviewAdapter.CreateSession(
            imported,
            CustomModelPreviewMode.SourceFbx);

        CustomModelPreviewPayload bind = session.CreatePayload(clip, 0);
        CustomModelPreviewPayload posed = session.CreatePayload(clip, 2);
        MeshRenderData bindMesh = Assert.Single(bind.Meshes);
        MeshRenderData posedMesh = Assert.Single(posed.Meshes);
        Assert.False(bindMesh.IsSkinned);
        Assert.False(posedMesh.IsSkinned);

        Vector3 bindWorld = Vector3.Transform(
            bindMesh.Vertices.Span[0].Position,
            bindMesh.LocalToWorld);
        Vector3 posedWorld = Vector3.Transform(
            posedMesh.Vertices.Span[0].Position,
            posedMesh.LocalToWorld);
        Assert.InRange(posedWorld.X - bindWorld.X, 0.199f, 0.201f);
    }

    [Fact]
    public async Task SourceWriterParentsRigidMeshAndRemapsSkinnedPaletteAfterInterleaving()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            CreateMixedHingedPropFixture(),
            "mixed-hinged-prop.fbx");
        Assert.Contains(imported.Surfaces, static surface => !surface.IsSkinned);
        Assert.Contains(imported.Surfaces, static surface => surface.IsSkinned);

        string outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ReAnimated-rigid-prop-{Guid.NewGuid():N}");
        try
        {
            Dl1SourceModelBuildResult build = await Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = imported,
                OutputDirectory = outputDirectory,
                ResourceName = "mixed_prop",
            });
            byte[] msh = await File.ReadAllBytesAsync(build.SourceMshPath);
            ImmutableArray<SourceNodeHeader> nodes = ReadSourceNodeHeaders(msh);

            SourceNodeHeader owner = nodes.Single(node => node.Name == "Root");
            SourceNodeHeader rigidMesh = nodes.Single(node =>
                node.Type == 1 && node.Name.Contains("RetailMesh", StringComparison.Ordinal));
            Assert.Equal(checked((short)owner.Index), rigidMesh.ParentIndex);
            Assert.All(imported.Surfaces.Where(static surface => !surface.IsSkinned),
                surface => Assert.Empty(surface.Vertices[0].BoneWeights));

            Dl1AuthoredRigNode ownerBind = Assert.Single(
                build.AuthoredRigContract!.Nodes,
                node => node.Name == "Root");
            Vector3D emittedRigidVertex = ownerBind.GlobalBindMatrix.TransformPoint(
                ReadFirstLodPosition(msh, rigidMesh));
            Vector3D importedRigidVertex = imported.Surfaces
                .Single(static surface => !surface.IsSkinned)
                .Vertices[0].Position;
            Assert.InRange((emittedRigidVertex - importedRigidVertex).Length, 0, 1e-5);

            SourceNodeHeader weightedBone = nodes.Single(node => node.Name == "WeightedBone");
            SourceNodeHeader skinnedMesh = nodes.Single(node =>
                node.Type == 2 && node.Name.Contains("WeightedMesh", StringComparison.Ordinal));
            Assert.Equal(-1, skinnedMesh.ParentIndex);
            Assert.True(new[] { weightedBone.Index }.SequenceEqual(
                ReadFirstSubsetPalette(msh, skinnedMesh)));
            Assert.NotEmpty(ReadSkinChunkPayload(msh, skinnedMesh));

            Assert.Equal(nodes.Select(static node => node.Index),
                build.PreparedPhysicalNodeExpectations.Select(static node => node.PhysicalIndex));
            Assert.Equal(weightedBone.Index,
                Assert.Single(build.PreparedSkinningExpectations
                    .Where(surface => surface.NodeName.Contains("WeightedMesh", StringComparison.Ordinal))
                    .SelectMany(static surface => surface.Vertices)
                    .SelectMany(static vertex => vertex.Influences)
                    .Select(static influence => influence.EntityIndex)
                    .Distinct()));
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
                Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public void DirectlyAnimatedMeshModelIsRetainedAsAClipTransform()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            CreateHingedPropFixture(animateMeshModel: true),
            "animated-mesh-prop.fbx");

        Assert.NotNull(imported.Rig);
        int meshTransform = imported.Rig!.GetBoneIndex("RetailMesh");
        Assert.True(meshTransform >= 0);
        AnimationClip clip = Assert.Single(imported.AnimationClips.Values);
        Assert.Contains(clip.TransformTracks,
            track => track.BoneIndex == meshTransform);
    }

    [Fact]
    public void UnanimatedRootPivotRemainsAvailableForAHandAuthoredPropClip()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            CreateStaticRootPivotFixture(),
            "static-root-pivot.fbx");

        CustomModelBone pivot = Assert.Single(
            imported.Package.Document.CreateEffectiveBones(),
            static bone => bone.FbxObjectId == 1);
        Assert.Contains(imported.Surfaces, surface =>
            surface.RigidGeometryOwnerFbxObjectId == pivot.FbxObjectId);

        FbxModelAuthoringImportResult authored = FbxAuthoredAnimationAuthoring.Create(
            imported,
            "gate-open",
            1.0,
            new FrameRate(30, 1));
        CustomModelAnimationClip selection = Assert.Single(
            authored.Package.Document.AnimationClips,
            static clip => clip.AuthoredAnimation is not null);
        Assert.Contains(authored.AnimationClips[selection.Id].TransformTracks,
            track => track.BoneIndex == pivot.Index);
    }

    internal static byte[] CreateDoorFixture(bool attachMeshToPivot = true)
    {
        FbxBinaryDocument source = FbxBinaryReader.Read(CreateHingedPropFixture(false));
        FbxNode objects = source.Nodes.Single(static node => node.Name == "Objects");
        FbxNode geometry = FindObject(objects, 10);
        var children = geometry.Children.Where(static child =>
            !child.Name.StartsWith("LayerElement", StringComparison.Ordinal) && child.Name != "Layer")
            .Select(child => child.Name switch
            {
                "Vertices" => Node("Vertices", [ImmutableArray.Create(0.0, 0, 0, 80, 0, 0, 80, 200, 0, 0, 200, 0)]),
                "PolygonVertexIndex" => Node("PolygonVertexIndex", [ImmutableArray.Create(0L, 1L, -3L, 0L, 2L, -4L)]),
                _ => child,
            }).ToImmutableArray();
        var nodes = source.Nodes.Replace(objects,
            objects with { Children = objects.Children.Replace(geometry, geometry with { Children = children }) });
        if (!attachMeshToPivot)
        {
            FbxNode connections = source.Nodes.Single(static node => node.Name == "Connections");
            nodes = nodes.Replace(connections, connections with
            {
                Children = connections.Children.Where(static connection =>
                    !(connection.Name == "C" && connection.Properties.Length >= 3 &&
                      connection.Properties[0].Value is string kind && kind == "OO" &&
                      connection.Properties[1].Value is long child && child == 4 &&
                      connection.Properties[2].Value is long parent && parent == 2)).ToImmutableArray(),
            });
        }
        return BlenderFbxStrictValidationTests.Serialize(source with { Nodes = nodes });
    }

    internal static byte[] CreateHingedPropFixture(bool animateMeshModel)
    {
        FbxBinaryDocument source = FbxBinaryReader.Read(
            BlenderFbxStrictValidationTests.CreateValidModelFixture());
        FbxNode objects = source.Nodes.Single(node => node.Name == "Objects");
        FbxNode connections = source.Nodes.Single(node => node.Name == "Connections");

        // Reuse the generated mesh/animation fixture while converting its two
        // transforms into an unskinned Null pivot chain.
        ImmutableArray<FbxNode> objectRows = objects.Children
            .Select(node => NodeObjectId(node) switch
            {
                long id when id == 2L || id == 3L => WithModelSubtype(node, "Null"),
                _ => node,
            })
            .Select(node => NodeObjectId(node) == 70L
                ? WithCurveValues(node, [0.0, 10.0, 20.0])
                : node)
            .Select(node => NodeObjectId(node) == 2L
                ? WithLocalTranslation(node, new Vector3D(0.5, 0, 0))
                : node)
            .Where(node => NodeObjectId(node) switch
            {
                long id => id is not (20L or 30L or 31L or 32L),
                null => true,
            })
            .ToImmutableArray();

        var connectionRows = connections.Children
            .Where(node => !ReferencesAny(node, 30, 31, 32))
            .Select(node => animateMeshModel &&
                node.Name == "C" &&
                node.Properties.Length >= 3 &&
                node.Properties[0].Value is string kind && kind == "OP" &&
                node.Properties[1].Value is long child && child == 60 &&
                node.Properties[2].Value is long parent && parent == 2
                    ? node with { Properties = node.Properties.SetItem(2, new FbxProperty('L', 4L)) }
                    : node)
            .Append(Node("C", ["OO", 4L, 2L]))
            .ToImmutableArray();

        return BlenderFbxStrictValidationTests.Serialize(source with
        {
            Nodes = source.Nodes
                .Replace(objects, objects with { Children = objectRows })
                .Replace(connections, connections with { Children = connectionRows }),
        });
    }

    private static FbxNode WithModelSubtype(FbxNode node, string subtype) => node with
    {
        Properties = node.Properties.SetItem(2, new FbxProperty('S', subtype)),
    };

    private static FbxNode WithLocalTranslation(FbxNode model, Vector3D translation)
    {
        FbxNode properties70 = model.FindChild("Properties70") ??
            throw new InvalidDataException("Generated model has no Properties70 block.");
        FbxNode position = properties70.FindChildren("P").Single(node =>
            node.Properties[0].Value is string name && name == "Lcl Translation");
        FbxNode changed = position with
        {
            Properties = position.Properties
                .SetItem(4, new FbxProperty('D', translation.X))
                .SetItem(5, new FbxProperty('D', translation.Y))
                .SetItem(6, new FbxProperty('D', translation.Z)),
        };
        return model with
        {
            Children = model.Children.Replace(
                properties70,
                properties70 with { Children = properties70.Children.Replace(position, changed) }),
        };
    }

    private static FbxNode WithCurveValues(FbxNode curve, double[] values)
    {
        FbxNode keyValues = curve.FindChild("KeyValueFloat") ??
            throw new InvalidDataException("Generated curve has no KeyValueFloat array.");
        return curve with
        {
            Children = curve.Children.Replace(keyValues, keyValues with
            {
                Properties = keyValues.Properties.SetItem(
                    0,
                    new FbxProperty('d', values.ToImmutableArray())),
            }),
        };
    }

    internal static byte[] CreateMixedHingedPropFixture()
    {
        FbxBinaryDocument source = FbxBinaryReader.Read(
            CreateHingedPropFixture(animateMeshModel: false));
        FbxNode objects = source.Nodes.Single(node => node.Name == "Objects");
        FbxNode connections = source.Nodes.Single(node => node.Name == "Connections");
        FbxNode root = FindObject(objects, 1);
        FbxNode mesh = FindObject(objects, 4);
        FbxNode geometry = FindObject(objects, 10);
        FbxNode weightedBone = CloneModel(root, 6, "WeightedBone", "LimbNode");
        FbxNode weightedMesh = CloneModel(mesh, 8, "WeightedMesh", "Mesh");
        FbxNode weightedGeometry = geometry with
        {
            Properties = geometry.Properties
                .SetItem(0, new FbxProperty('L', 12L))
                .SetItem(1, new FbxProperty('S', "Geometry::WeightedMesh_Mesh")),
        };
        FbxNode skin = Node("Deformer", [33L, "Deformer::WeightedSkin", "Skin"]);
        FbxNode cluster = Node("Deformer", [34L, "SubDeformer::WeightedCluster", "Cluster"],
            Node("Indexes", [ImmutableArray.Create(0L, 1L, 2L)]),
            Node("Weights", [ImmutableArray.Create(1.0, 1.0, 1.0)]),
            Node("Transform", [IdentityMatrix()]),
            Node("TransformLink", [IdentityMatrix()]));
        ImmutableArray<FbxNode> objectRows = objects.Children
            .Add(weightedBone)
            .Add(weightedMesh)
            .Add(weightedGeometry)
            .Add(skin)
            .Add(cluster);
        ImmutableArray<FbxNode> connectionRows = connections.Children
            .Add(Node("C", ["OO", 6L, 1L]))
            .Add(Node("C", ["OO", 8L, 6L]))
            .Add(Node("C", ["OO", 12L, 8L]))
            .Add(Node("C", ["OO", 33L, 12L]))
            .Add(Node("C", ["OO", 34L, 33L]))
            .Add(Node("C", ["OO", 6L, 34L]));
        return BlenderFbxStrictValidationTests.Serialize(source with
        {
            Nodes = source.Nodes
                .Replace(objects, objects with { Children = objectRows })
                .Replace(connections, connections with { Children = connectionRows }),
        });
    }

    private static byte[] CreateStaticRootPivotFixture()
    {
        FbxBinaryDocument source = FbxBinaryReader.Read(
            CreateHingedPropFixture(animateMeshModel: false));
        FbxNode objects = source.Nodes.Single(node => node.Name == "Objects");
        FbxNode connections = source.Nodes.Single(node => node.Name == "Connections");
        long[] removedIds = [2, 3, 50, 51, 60, 61, 70, 71];
        ImmutableArray<FbxNode> objectRows = objects.Children
            .Where(node => NodeObjectId(node) is null ||
                !removedIds.Contains(NodeObjectId(node)!.Value))
            .ToImmutableArray();
        ImmutableArray<FbxNode> connectionRows = connections.Children
            .Where(node => !ReferencesAny(node, removedIds))
            .Append(Node("C", ["OO", 4L, 1L]))
            .ToImmutableArray();
        return BlenderFbxStrictValidationTests.Serialize(source with
        {
            Nodes = source.Nodes
                .Replace(objects, objects with { Children = objectRows })
                .Replace(connections, connections with { Children = connectionRows }),
        });
    }

    private static FbxNode CloneModel(FbxNode source, long id, string name, string subtype) => source with
    {
        Properties = source.Properties
            .SetItem(0, new FbxProperty('L', id))
            .SetItem(1, new FbxProperty('S', $"Model::{name}"))
            .SetItem(2, new FbxProperty('S', subtype)),
    };

    private static FbxNode FindObject(FbxNode objects, long objectId) => objects.Children.Single(node =>
        node.Properties.Length > 0 && node.Properties[0].Value is long id && id == objectId);

    private static ImmutableArray<SourceNodeHeader> ReadSourceNodeHeaders(byte[] msh)
    {
        Assert.Equal(0x0048_534Du, BinaryPrimitives.ReadUInt32LittleEndian(msh));
        int rootEnd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(8, 4)));
        int offset = 16 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(12, 4)));
        var result = ImmutableArray.CreateBuilder<SourceNodeHeader>();
        while (offset < rootEnd)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset, 4));
            int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 8, 4)));
            int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 12, 4)));
            if (id == 0x0003)
            {
                ReadOnlySpan<byte> payload = msh.AsSpan(offset + 16, payloadSize);
                int terminator = payload.Slice(4, 64).IndexOf((byte)0);
                int nameLength = terminator < 0 ? 64 : terminator;
                result.Add(new(
                    result.Count,
                    System.Text.Encoding.UTF8.GetString(payload.Slice(4, nameLength)),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload),
                    BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(68, 2)),
                    offset,
                    offset + 16 + payloadSize,
                    offset + total));
            }

            offset = checked(offset + total);
        }

        Assert.Equal(rootEnd, offset);
        return result.ToImmutable();
    }

    private static ImmutableArray<int> ReadFirstSubsetPalette(
        byte[] msh,
        SourceNodeHeader node)
    {
        byte[] payload = ReadLodChildPayload(msh, node, 0x151);
        int paletteCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(10, 2));
        return Enumerable.Range(0, paletteCount)
            .Select(index => (int)BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(12 + index * 2, 2)))
            .ToImmutableArray();
    }

    private static byte[] ReadSkinChunkPayload(byte[] msh, SourceNodeHeader node)
    {
        int lodStart = node.FirstChildOffset;
        int lodEnd = node.EndOffset;
        int lodTotal = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(lodStart + 8, 4)));
        int lodPayload = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(lodStart + 12, 4)));
        int offset = lodStart + 16 + lodPayload;
        int end = lodStart + lodTotal;
        while (offset < end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset, 4));
            int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 8, 4)));
            int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 12, 4)));
            if (id is 0x130 or 0x131)
                return msh.AsSpan(offset + 16, payloadSize).ToArray();
            offset = checked(offset + total);
        }

        throw new InvalidDataException("The generated skinned mesh has no source MSH skin chunk.");
    }

    private static Vector3D ReadFirstLodPosition(byte[] msh, SourceNodeHeader node)
    {
        byte[] payload = ReadLodChildPayload(msh, node, 0x101);
        return new Vector3D(
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4))),
            BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(8, 4))));
    }

    private static byte[] ReadLodChildPayload(byte[] msh, SourceNodeHeader node, uint childId)
    {
        int lodStart = node.FirstChildOffset;
        int lodTotal = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(lodStart + 8, 4)));
        int lodPayload = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(lodStart + 12, 4)));
        int offset = lodStart + 16 + lodPayload;
        int end = lodStart + lodTotal;
        while (offset < end)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset, 4));
            int total = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 8, 4)));
            int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 12, 4)));
            if (id == childId)
                return msh.AsSpan(offset + 16, payloadSize).ToArray();
            offset = checked(offset + total);
        }

        throw new InvalidDataException($"The generated LOD has no source MSH child 0x{childId:X}.");
    }

    private static bool ReferencesAny(FbxNode connection, params long[] ids) =>
        connection.Name == "C" && connection.Properties.Length >= 3 &&
        (connection.Properties[1].Value is long child && ids.Contains(child) ||
         connection.Properties[2].Value is long parent && ids.Contains(parent));

    private static long? NodeObjectId(FbxNode node) =>
        node.Properties.Length > 0 && node.Properties[0].Value is long id
            ? id
            : null;

    private static FbxNode Node(string name, object[] values, params FbxNode[] children) => new(
        name,
        values.Select(value => new FbxProperty(value switch
        {
            long => 'L',
            string => 'S',
            ImmutableArray<long> => 'l',
            ImmutableArray<double> => 'd',
            _ => throw new InvalidDataException($"Unsupported generated FBX value {value.GetType().Name}."),
        }, value)).ToImmutableArray(),
        children.ToImmutableArray(),
        0,
        0);

    private static ImmutableArray<double> IdentityMatrix() =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private readonly record struct SourceNodeHeader(
        int Index,
        string Name,
        uint Type,
        short ParentIndex,
        int ChunkOffset,
        int FirstChildOffset,
        int EndOffset);
}

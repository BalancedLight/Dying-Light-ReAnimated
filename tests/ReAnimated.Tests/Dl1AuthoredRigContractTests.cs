using System.Buffers.Binary;
using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1AuthoredRigContractTests
{
    private static readonly int[] DepthFirstSourceOrder = [0, 1, 3, 2];

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void PreparePreservesPivotsAndAuthorsRightHandedPositiveXFrames()
    {
        FbxModelAuthoringImportResult imported = CreateSyntheticModel();

        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(imported);
        ImmutableArray<TransformMatrix> sourceGlobals = ComputeSourceGlobals(
            imported.Package.Document.Bones);

        foreach (Dl1AuthoredRigNode node in prepared.Contract.Nodes)
        {
            AssertVectorNear(
                sourceGlobals[node.SourceBoneIndex].Translation,
                node.GlobalBindMatrix.Translation);
            AssertOrthonormal(node.GlobalBindMatrix);
        }

        Dl1AuthoredRigNode root = prepared.Contract.Nodes[0];
        Vector3D rootPositiveX = new(
            root.GlobalBindMatrix.M11,
            root.GlobalBindMatrix.M21,
            root.GlobalBindMatrix.M31);
        Vector3D expectedContinuation = (
            sourceGlobals[1].Translation - sourceGlobals[0].Translation).Normalized();
        Assert.True(
            Vector3D.Dot(rootPositiveX, expectedContinuation) > 0.99999,
            "The root's emitted local +X axis must follow the selected authored continuation.");
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void PrepareCreatesDepthFirstStableIdentityAndExactInverseReferences()
    {
        FbxModelAuthoringImportResult imported = CreateSyntheticModel();

        Dl1PreparedAuthoredRig first = Dl1CustomModelRigPreparer.Prepare(imported);
        Dl1PreparedAuthoredRig second = Dl1CustomModelRigPreparer.Prepare(imported);

        Assert.Equal(DepthFirstSourceOrder, first.Contract.Nodes.Select(static node => node.SourceBoneIndex).ToArray());
        Assert.Equal(DepthFirstSourceOrder, first.Contract.SourceToPhysicalIndices.ToArray());
        Assert.Equal(first.Contract.ContractId, second.Contract.ContractId);
        Assert.Equal(first.Contract.BindFingerprint, second.Contract.BindFingerprint);
        Assert.Equal(first.Contract.SkeletonFingerprint, second.Contract.SkeletonFingerprint);
        foreach (Dl1AuthoredRigNode node in first.Contract.Nodes)
        {
            Assert.True(node.ParentPhysicalIndex < node.PhysicalIndex);
            AssertMatrixNear(
                TransformMatrix.Identity,
                node.GlobalBindMatrix * node.InverseGlobalReferenceMatrix);
            TransformMatrix reconstructed = node.ParentPhysicalIndex < 0
                ? node.LocalBindMatrix
                : first.Contract.Nodes[node.ParentPhysicalIndex].GlobalBindMatrix * node.LocalBindMatrix;
            AssertMatrixNear(node.GlobalBindMatrix, reconstructed);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public void PrepareAuthorsNonzeroSegmentBoundsAndBindPoseSkinIdentity()
    {
        FbxModelAuthoringImportResult imported = CreateSyntheticModel();

        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(imported);

        foreach (Dl1AuthoredRigNode node in prepared.Contract.Nodes)
        {
            Assert.True(node.Bounds.IsFiniteAndNonZero);
            Assert.True(node.Bounds.HalfExtents.X >= 0.005);
            Assert.True(node.Bounds.HalfExtents.Y >= 0.005);
            Assert.True(node.Bounds.HalfExtents.Z >= 0.005);
        }

        Dl1PreparedSkinSurface surface = Assert.Single(prepared.Surfaces);
        Assert.Equal(DepthFirstSourceOrder, surface.PhysicalPalette.ToArray());
        for (int paletteIndex = 0; paletteIndex < surface.PhysicalPalette.Length; paletteIndex++)
        {
            int physicalBone = surface.PhysicalPalette[paletteIndex];
            AssertMatrixNear(
                TransformMatrix.Identity,
                prepared.Contract.Nodes[physicalBone].GlobalBindMatrix *
                surface.InverseBindMatrices[paletteIndex]);
        }

        SkeletonPose rebasedBind = prepared.RebasePose(imported.Rig!.CreateBindPose());
        for (int physicalIndex = 0; physicalIndex < prepared.Contract.Nodes.Length; physicalIndex++)
        {
            AssertMatrixNear(
                prepared.Contract.Nodes[physicalIndex].GlobalBindMatrix,
                rebasedBind.GlobalMatrices[physicalIndex]);
        }
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ViewModelWpf")]
    public void PreviewModesKeepRawFbxAndDl1OutputContractsDistinct()
    {
        FbxModelAuthoringImportResult imported = CreateSyntheticModel();

        CustomModelPreviewPayload raw = CustomModelPreviewAdapter.Create(
            imported,
            clip: null,
            frame: 0,
            mode: CustomModelPreviewMode.SourceFbx);
        CustomModelPreviewPayload dl1 = CustomModelPreviewAdapter.Create(
            imported,
            clip: null,
            frame: 0,
            mode: CustomModelPreviewMode.Dl1Output);

        Assert.NotNull(raw.Skeleton);
        Assert.NotNull(dl1.Skeleton);
        Assert.Equal([0, 1, 2, 3], Assert.Single(raw.Meshes).SkinBoneIndices.ToArray());
        Assert.Equal([0, 1, 3, 2], Assert.Single(dl1.Meshes).SkinBoneIndices.ToArray());
        Assert.Contains(raw.Diagnostics, static value => value.StartsWith("Source FBX preview", StringComparison.Ordinal));
        Assert.Contains(dl1.Diagnostics, static value => value.StartsWith("DL1 Output preview", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CodecEvaluation")]
    public async Task SourceWriterSerializesAuthoredFramesInverseReferencesAndBounds()
    {
        FbxModelAuthoringImportResult imported = CreateSyntheticModel();
        Dl1PreparedAuthoredRig prepared = Dl1CustomModelRigPreparer.Prepare(imported);
        string output = Path.Combine(Path.GetTempPath(), $"dlr-authored-rig-{Guid.NewGuid():N}");
        try
        {
            Dl1SourceModelBuildResult build = await Dl1SourceModelWriter.WriteAsync(
                new Dl1SourceModelBuildRequest
                {
                    Model = imported,
                    OutputDirectory = output,
                    ResourceName = "synthetic_character",
                });
            byte[] msh = await File.ReadAllBytesAsync(build.SourceMshPath);
            ImmutableArray<SerializedNode> nodes = ReadTopLevelNodes(msh);

            Assert.True(nodes.Length >= prepared.Contract.Nodes.Length);
            for (int physicalIndex = 0; physicalIndex < prepared.Contract.Nodes.Length; physicalIndex++)
            {
                Dl1AuthoredRigNode expected = prepared.Contract.Nodes[physicalIndex];
                SerializedNode actual = nodes[physicalIndex];
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.ParentPhysicalIndex, actual.ParentIndex);
                AssertMatrixNear(expected.LocalBindMatrix, actual.LocalMatrix, 2e-5);
                AssertMatrixNear(expected.InverseGlobalReferenceMatrix, actual.ReferenceMatrix, 2e-5);
                AssertVectorNear(expected.Bounds.Center, actual.Bounds.Center, 2e-5);
                AssertVectorNear(expected.Bounds.HalfExtents, actual.Bounds.HalfExtents, 2e-5);
            }

            Dl1ChrV4Document character = Dl1ChrV4Codec.Parse(
                await File.ReadAllBytesAsync(build.CharacterDefinitionPath));
            Dl1ChrV4Variant variant = Assert.Single(character.Variants);
            Assert.Equal("default", variant.Name);
            Assert.Equal(nodes.Select(static node => node.Name), character.ObjectNames);
            Assert.Equal(nodes.Length, variant.ObjectTransforms.Length);
            for (int objectIndex = 0; objectIndex < nodes.Length; objectIndex++)
            {
                // CMesh::SetCharacter installs CHR rows as hierarchy-local
                // matrices.  Comparing against the source-MSH object table,
                // rather than a codec self-round-trip, prevents a regression
                // back to accumulated globals that fans out child bones.
                AssertMatrixNear(
                    nodes[objectIndex].LocalMatrix,
                    variant.ObjectTransforms[objectIndex],
                    2e-5);
            }

            int childIndex = Enumerable.Range(0, prepared.Contract.Nodes.Length)
                .Single(index => prepared.Contract.Nodes[index].Name == "chain_a");
            Dl1AuthoredRigNode child = prepared.Contract.Nodes[childIndex];
            TransformMatrix accumulatedGlobal =
                prepared.Contract.Nodes[child.ParentPhysicalIndex].GlobalBindMatrix *
                child.LocalBindMatrix;
            Assert.False(
                child.LocalBindMatrix.NearlyEquals(accumulatedGlobal, 2e-5),
                "The control child must distinguish a hierarchy-local CHR row from an accumulated global row.");
            Assert.False(
                variant.ObjectTransforms[childIndex].NearlyEquals(accumulatedGlobal, 2e-5),
                "CHR output must not accumulate the parent transform into a child row.");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static FbxModelAuthoringImportResult CreateSyntheticModel()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Guid materialId = new("73cb889e-a691-4273-9f05-ff683fc51fc8");
        ImmutableArray<CustomModelBone> bones =
        [
            Bone(0, "root", -1, new Vector3D(0.4, 0.2, -0.1), BoneKind.Root),
            Bone(
                1,
                "chain_a",
                0,
                new Vector3D(0.0, 1.0, 0.0),
                exactLocal: new TransformMatrix(
                    1.0, 0.16, 0.0, 0.0,
                    0.0, 1.0, 0.08, 1.0,
                    0.0, 0.0, 1.0, 0.0,
                    0.0, 0.0, 0.0, 1.0)),
            Bone(2, "branch_b", 0, new Vector3D(0.7, 0.0, 0.0)),
            Bone(3, "chain_a_tip", 1, new Vector3D(0.0, 0.8, 0.0)),
        ];
        var document = new CustomModelDocument
        {
            Name = "Synthetic character",
            RigMode = CustomModelRigMode.ExactFbxRig,
            Source = new CustomModelSourceIdentity
            {
                OriginalFileName = "synthetic_character.fbx",
                ContentSha256 = hash,
                FbxVersion = 7400,
            },
            RigSignature = hash,
            Bones = bones,
            Meshes =
            [
                new CustomModelMeshPart
                {
                    Name = "body",
                    ControlPointCount = 6,
                    PolygonCount = 2,
                    TriangleCount = 2,
                    ExpandedVertexCount = 6,
                    MaterialSlotCount = 1,
                },
            ],
            Materials = [new CustomModelMaterial { Id = materialId, Name = "body" }],
        };
        RigDefinition rig = document.CreateRigDefinition();
        ImmutableArray<TransformMatrix> globals = rig.CreateBindPose().GlobalMatrices;
        var vertices = ImmutableArray.CreateBuilder<FbxModelVertex>();
        for (int index = 0; index < 6; index++)
        {
            int paletteIndex = index % 4;
            vertices.Add(new FbxModelVertex(
                globals[paletteIndex].Translation + new Vector3D(
                    0.02 * (index + 1),
                    0.01 * (index % 2),
                    0.015 * (index % 3)),
                Vector3D.UnitZ,
                index % 2,
                index / 5.0,
                [paletteIndex],
                [1.0]));
        }

        var surface = new FbxModelSurface(
            "body/0",
            "body",
            materialId,
            vertices.ToImmutable(),
            [0, 1, 2, 3, 4, 5],
            [0, 1, 2, 3],
            globals.Select(static value => value.InvertedAffine()).ToImmutableArray(),
            IsSkinned: true);
        var package = new CustomModelPackage(
            document,
            [0x46, 0x42, 0x58],
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
        return new FbxModelAuthoringImportResult(
            package,
            rig,
            [surface],
            ImmutableDictionary<Guid, AnimationClip>.Empty,
            null!);
    }

    private static CustomModelBone Bone(
        int index,
        string name,
        int parent,
        Vector3D translation,
        BoneKind kind = BoneKind.Deform,
        TransformMatrix? exactLocal = null) =>
        new()
        {
            Index = index,
            FbxObjectId = 100 + index,
            Name = name,
            ParentIndex = parent,
            LocalBindTransform = new TransformTRS(translation, QuaternionD.Identity, Vector3D.One),
            ExactLocalBindMatrix = exactLocal ?? TransformMatrix.CreateTranslation(translation),
            Kind = kind,
            IsWeighted = true,
        };

    private static ImmutableArray<TransformMatrix> ComputeSourceGlobals(
        ImmutableArray<CustomModelBone> bones)
    {
        var globals = ImmutableArray.CreateBuilder<TransformMatrix>(bones.Length);
        foreach (CustomModelBone bone in bones)
        {
            globals.Add(bone.ParentIndex < 0
                ? bone.ExactLocalBindMatrix
                : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix);
        }

        return globals.MoveToImmutable();
    }

    private static ImmutableArray<SerializedNode> ReadTopLevelNodes(byte[] msh)
    {
        Assert.Equal(0x0048_534Du, BinaryPrimitives.ReadUInt32LittleEndian(msh));
        int rootPayloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(12, 4)));
        int rootEnd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(8, 4)));
        int offset = 16 + rootPayloadSize;
        var nodes = ImmutableArray.CreateBuilder<SerializedNode>();
        while (offset < rootEnd)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset, 4));
            int totalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 8, 4)));
            int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 12, 4)));
            Assert.True(totalSize >= 16 + payloadSize);
            if (id == 0x0003)
            {
                ReadOnlySpan<byte> payload = msh.AsSpan(offset + 16, payloadSize);
                nodes.Add(new SerializedNode(
                    ReadFixedName(payload.Slice(4, 64)),
                    BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(68, 2)),
                    ReadMatrix3X4(payload.Slice(76, 48)),
                    ReadMatrix3X4(payload.Slice(124, 48)),
                    new Dl1AuthoredBoneBounds(
                        ReadVector3(payload.Slice(172, 12)),
                        ReadVector3(payload.Slice(184, 12)))));
            }

            offset += totalSize;
        }

        return nodes.ToImmutable();
    }

    private static string ReadFixedName(ReadOnlySpan<byte> payload)
    {
        int end = payload.IndexOf((byte)0);
        if (end < 0) end = payload.Length;
        return System.Text.Encoding.UTF8.GetString(payload[..end]);
    }

    private static TransformMatrix ReadMatrix3X4(ReadOnlySpan<byte> payload) =>
        new(
            ReadFloat(payload, 0), ReadFloat(payload, 4), ReadFloat(payload, 8), ReadFloat(payload, 12),
            ReadFloat(payload, 16), ReadFloat(payload, 20), ReadFloat(payload, 24), ReadFloat(payload, 28),
            ReadFloat(payload, 32), ReadFloat(payload, 36), ReadFloat(payload, 40), ReadFloat(payload, 44),
            0.0, 0.0, 0.0, 1.0);

    private static Vector3D ReadVector3(ReadOnlySpan<byte> payload) =>
        new(ReadFloat(payload, 0), ReadFloat(payload, 4), ReadFloat(payload, 8));

    private static double ReadFloat(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));

    private static void AssertOrthonormal(TransformMatrix value)
    {
        Vector3D x = new(value.M11, value.M21, value.M31);
        Vector3D y = new(value.M12, value.M22, value.M32);
        Vector3D z = new(value.M13, value.M23, value.M33);
        Assert.InRange(Math.Abs(x.Length - 1.0), 0.0, 1e-8);
        Assert.InRange(Math.Abs(y.Length - 1.0), 0.0, 1e-8);
        Assert.InRange(Math.Abs(z.Length - 1.0), 0.0, 1e-8);
        Assert.InRange(Math.Abs(Vector3D.Dot(x, y)), 0.0, 1e-8);
        Assert.InRange(Math.Abs(Vector3D.Dot(x, z)), 0.0, 1e-8);
        Assert.InRange(Math.Abs(Vector3D.Dot(y, z)), 0.0, 1e-8);
        Assert.InRange(Math.Abs(value.LinearDeterminant - 1.0), 0.0, 1e-8);
    }

    private static void AssertVectorNear(Vector3D expected, Vector3D actual, double tolerance = 1e-8)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0.0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0.0, tolerance);
    }

    private static void AssertMatrixNear(
        TransformMatrix expected,
        TransformMatrix actual,
        double tolerance = 1e-8) =>
        Assert.True(actual.NearlyEquals(expected, tolerance), $"Expected {expected}; actual {actual}.");

    private sealed record SerializedNode(
        string Name,
        int ParentIndex,
        TransformMatrix LocalMatrix,
        TransformMatrix ReferenceMatrix,
        Dl1AuthoredBoneBounds Bounds);
}

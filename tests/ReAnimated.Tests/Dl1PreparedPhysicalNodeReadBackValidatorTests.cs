using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1PreparedPhysicalNodeReadBackValidatorTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void AcceptsAllPreparedNodeTypesWithNonidentityGeometryFramesAndBounds()
    {
        ImmutableArray<Dl1PreparedPhysicalNodeExpectation> expected =
        [
            Node(0, "root", -1, 8,
                Matrix(1, 0, 0, 0, 0, 1, 0, 2, 0, 0, 1, 3),
                Matrix(1, 0, 0, 0, 0, 1, 0, -2, 0, 0, 1, -3),
                new CompactBounds(0, 0, 0, 0.25f, 0.5f, 0.75f)),
            Node(1, "helper", 0, 4,
                Matrix(1, 0, 0, 0.5f, 0, 1, 0, 2, 0, 0, 1, 3),
                Matrix(1, 0, 0, -0.5f, 0, 1, 0, -4, 0, 0, 1, -6),
                new CompactBounds(0.5f, 2, 3, 0.1f, 0.2f, 0.3f)),
            Node(2, "surface", -1, 1,
                Matrix(1, 0, 0, 4, 0, 1, 0, 5, 0, 0, 1, 6),
                CompactMatrix3x4.Identity,
                new CompactBounds(4, 5, 6, 1, 2, 3)),
            Node(3, "skinned_surface", -1, 2,
                CompactMatrix3x4.Identity,
                CompactMatrix3x4.Identity,
                new CompactBounds(1, 2, 3, 4, 5, 6)),
            Node(4, "bounds", -1, 1,
                CompactMatrix3x4.Identity,
                CompactMatrix3x4.Identity,
                new CompactBounds(2, 3, 4, 4, 5, 6)),
        ];

        Dl1PreparedPhysicalNodeReadBackEvidence evidence =
            Dl1PreparedPhysicalNodeReadBackValidator.Validate(expected, Compiled(expected));

        Assert.Equal(expected.Length, evidence.VerifiedNodeCount);
        Assert.Equal(expected.Length, evidence.Nodes.Length);
        Assert.Equal(2, evidence.Nodes[2].CompiledEntityIndex);
        Assert.InRange(evidence.Nodes[2].LocalMatrixError, 0, 1e-5);
        Assert.Matches("^[0-9a-f]{64}$", evidence.ContractFingerprint);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("reordered")]
    [InlineData("declared_count")]
    [InlineData("root_count")]
    [InlineData("type")]
    [InlineData("parent")]
    [InlineData("local")]
    [InlineData("reference")]
    [InlineData("bounds")]
    [InlineData("nonfinite")]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public void RejectsAnyPreparedPhysicalNodeDisagreement(string change)
    {
        ImmutableArray<Dl1PreparedPhysicalNodeExpectation> expected = Fixture();
        CompactMeshDocument compiled = Compiled(expected);
        CompactMeshEntity[] entities = compiled.Entities.ToArray();
        CompactMeshEntity node = entities[change == "reordered" ? 0 : 2];

        switch (change)
        {
            case "missing":
                compiled = compiled with { Entities = entities[..^1] };
                break;
            case "extra":
                CompactMeshEntity last = entities[^1];
                CompactMeshEntity[] withExtra =
                [
                    .. entities,
                    last with { Index = entities.Length, Name = "extra_node" },
                ];
                compiled = compiled with { Entities = withExtra };
                break;
            case "duplicate":
                entities[2] = node with { Name = entities[0].Name };
                compiled = compiled with { Entities = entities };
                break;
            case "reordered":
                (entities[2], entities[3]) =
                    (entities[3] with { Index = 2 }, entities[2] with { Index = 3 });
                compiled = compiled with { Entities = entities };
                break;
            case "declared_count":
                compiled = compiled with { DeclaredEntityCount = compiled.DeclaredEntityCount + 1 };
                break;
            case "root_count":
                compiled = compiled with { DeclaredRootCount = compiled.DeclaredRootCount + 1 };
                break;
            case "type":
                entities[2] = node with { EntityType = CompactMeshEntityType.Helper };
                compiled = compiled with { Entities = entities };
                break;
            case "parent":
                entities[1] = entities[1] with { ParentIndex = -1 };
                compiled = compiled with { Entities = entities };
                break;
            case "local":
                entities[2] = node with { LocalMatrix = node.LocalMatrix with { M14 = node.LocalMatrix.M14 + 0.01f } };
                compiled = compiled with { Entities = entities };
                break;
            case "reference":
                entities[2] = node with { ReferenceMatrix = node.ReferenceMatrix with { M24 = node.ReferenceMatrix.M24 + 0.01f } };
                compiled = compiled with { Entities = entities };
                break;
            case "bounds":
                entities[2] = node with { Bounds = node.Bounds with { HalfX = node.Bounds.HalfX + 0.01f } };
                compiled = compiled with { Entities = entities };
                break;
            case "nonfinite":
                entities[2] = node with { LocalMatrix = node.LocalMatrix with { M11 = float.NaN } };
                compiled = compiled with { Entities = entities };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }

        Assert.Throws<InvalidDataException>(() =>
            Dl1PreparedPhysicalNodeReadBackValidator.Validate(expected, compiled));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "CustomModelCompilerContract")]
    public async Task StaticSourceWriterProvidesEverySurfaceAndBoundsNodeExpectation()
    {
        FbxModelAuthoringImportResult model = FbxModelAuthoringImporter.Import(
            FbxCustomModelMorphImportTests.CreateMorphFbx(["generic_smile"]),
            "generic_static.fbx",
            new FbxModelAuthoringImportOptions { RigMode = CustomModelRigMode.StaticProp });
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            Dl1SourceModelBuildResult sourceBuild = await Dl1SourceModelWriter.WriteAsync(new()
            {
                Model = model,
                OutputDirectory = directory,
                ResourceName = "generic_static",
            });
            ImmutableArray<Dl1PreparedPhysicalNodeExpectation> expected = sourceBuild.PreparedPhysicalNodeExpectations;
            ImmutableArray<Dl1PreparedPhysicalNodeExpectation> writtenMsh =
                ReadPhysicalNodes(await File.ReadAllBytesAsync(sourceBuild.SourceMshPath));

            Assert.Null(sourceBuild.AuthoredRigContract);
            Assert.Equal(model.Surfaces.Length + 1, expected.Length);
            Assert.Equal(expected.ToArray(), writtenMsh.ToArray());
            Assert.All(expected, static node =>
            {
                Assert.Equal(1u, node.SourceMshType);
                Assert.Equal(CompactMeshEntityType.Mesh, node.ExpectedEntityType);
            });
            Assert.Contains(expected, static node => node.Bounds.HalfX > 0 || node.Bounds.HalfY > 0 || node.Bounds.HalfZ > 0);

            Dl1PreparedPhysicalNodeReadBackEvidence evidence =
                Dl1PreparedPhysicalNodeReadBackValidator.Validate(expected, Compiled(expected));
            Assert.Equal(expected.Length, evidence.VerifiedNodeCount);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private static ImmutableArray<Dl1PreparedPhysicalNodeExpectation> Fixture() =>
    [
        Node(0, "root", -1, 8,
            Matrix(1, 0, 0, 0, 0, 1, 0, 2, 0, 0, 1, 3),
            Matrix(1, 0, 0, 0, 0, 1, 0, -2, 0, 0, 1, -3),
            new CompactBounds(0, 0, 0, 0.25f, 0.5f, 0.75f)),
        Node(1, "helper", 0, 4,
            Matrix(1, 0, 0, 0.5f, 0, 1, 0, 2, 0, 0, 1, 3),
            Matrix(1, 0, 0, -0.5f, 0, 1, 0, -4, 0, 0, 1, -6),
            new CompactBounds(0.5f, 2, 3, 0.1f, 0.2f, 0.3f)),
        Node(2, "surface", -1, 1,
            Matrix(1, 0, 0, 4, 0, 1, 0, 5, 0, 0, 1, 6),
            CompactMatrix3x4.Identity,
            new CompactBounds(4, 5, 6, 1, 2, 3)),
        Node(3, "skinned_surface", -1, 2,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            new CompactBounds(1, 2, 3, 4, 5, 6)),
        Node(4, "bounds", -1, 1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            new CompactBounds(2, 3, 4, 4, 5, 6)),
    ];

    private static Dl1PreparedPhysicalNodeExpectation Node(
        int index,
        string name,
        int parentIndex,
        uint sourceType,
        CompactMatrix3x4 local,
        CompactMatrix3x4 reference,
        CompactBounds bounds) =>
        new(
            index,
            name,
            parentIndex,
            sourceType,
            Dl1PreparedPhysicalNodeReadBackValidator.MapSourceMshType(sourceType),
            local,
            reference,
            bounds);

    private static CompactMeshDocument Compiled(
        ImmutableArray<Dl1PreparedPhysicalNodeExpectation> expected)
    {
        CompactMeshEntity[] entities = expected
            .Select(node => new CompactMeshEntity(
                node.PhysicalIndex,
                node.Name.ToUpperInvariant(),
                0,
                node.Bounds,
                checked((short)node.ParentIndex),
                node.ExpectedEntityType,
                checked((byte)expected.Count(candidate => candidate.ParentIndex == node.PhysicalIndex)),
                node.ExpectedEntityType is CompactMeshEntityType.Mesh or CompactMeshEntityType.SkinnedMesh
                    ? (byte)1
                    : (byte)0,
                node.LocalMatrix,
                node.ReferenceMatrix,
                0,
                0))
            .ToArray();
        return new CompactMeshDocument(
            entities.Length,
            entities.Count(static entity => entity.ParentIndex < 0),
            0,
            entities,
            []);
    }

    private static CompactMatrix3x4 Matrix(
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34) =>
        new(m11, m12, m13, m14, m21, m22, m23, m24, m31, m32, m33, m34);

    private static ImmutableArray<Dl1PreparedPhysicalNodeExpectation> ReadPhysicalNodes(
        byte[] msh)
    {
        Assert.Equal(0x0048_534Du, BinaryPrimitives.ReadUInt32LittleEndian(msh));
        int rootPayloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(12, 4)));
        int rootEnd = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(8, 4)));
        int offset = 16 + rootPayloadSize;
        var nodes = ImmutableArray.CreateBuilder<Dl1PreparedPhysicalNodeExpectation>();
        while (offset < rootEnd)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset, 4));
            int totalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 8, 4)));
            int payloadSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(msh.AsSpan(offset + 12, 4)));
            if (totalSize < 16 + payloadSize || offset + totalSize > rootEnd)
                throw new InvalidDataException("Test MSH chunk bounds are invalid.");
            if (id == 0x0003)
            {
                ReadOnlySpan<byte> payload = msh.AsSpan(offset + 16, payloadSize);
                uint sourceType = BinaryPrimitives.ReadUInt32LittleEndian(payload);
                nodes.Add(new(
                    nodes.Count,
                    ReadFixedName(payload.Slice(4, 64)),
                    BinaryPrimitives.ReadInt16LittleEndian(payload.Slice(68, 2)),
                    sourceType,
                    Dl1PreparedPhysicalNodeReadBackValidator.MapSourceMshType(sourceType),
                    ReadMatrix(payload.Slice(76, 48)),
                    ReadMatrix(payload.Slice(124, 48)),
                    new CompactBounds(
                        ReadSingle(payload, 172), ReadSingle(payload, 176), ReadSingle(payload, 180),
                        ReadSingle(payload, 184), ReadSingle(payload, 188), ReadSingle(payload, 192))));
            }

            offset += totalSize;
        }

        Assert.Equal(rootEnd, offset);
        return nodes.ToImmutable();
    }

    private static string ReadFixedName(ReadOnlySpan<byte> payload)
    {
        int end = payload.IndexOf((byte)0);
        if (end < 0)
            end = payload.Length;
        return Encoding.UTF8.GetString(payload[..end]);
    }

    private static CompactMatrix3x4 ReadMatrix(ReadOnlySpan<byte> payload) => new(
        ReadSingle(payload, 0), ReadSingle(payload, 4), ReadSingle(payload, 8), ReadSingle(payload, 12),
        ReadSingle(payload, 16), ReadSingle(payload, 20), ReadSingle(payload, 24), ReadSingle(payload, 28),
        ReadSingle(payload, 32), ReadSingle(payload, 36), ReadSingle(payload, 40), ReadSingle(payload, 44));

    private static float ReadSingle(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4)));
}

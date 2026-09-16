using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledRigValidatorTests
{
    private static (Dl1AuthoredRigContract Expected, CompactMeshDocument Compiled) Fixture()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        var contract = Dl1CustomModelRigPreparer.Prepare(model).Contract;
        var nodes = contract.Nodes.Select(n => new CompactMeshEntity(n.PhysicalIndex, n.Name.ToLowerInvariant(), 0,
            new((float)n.Bounds.Center.X, (float)n.Bounds.Center.Y, (float)n.Bounds.Center.Z,
                (float)n.Bounds.HalfExtents.X, (float)n.Bounds.HalfExtents.Y, (float)n.Bounds.HalfExtents.Z),
            (short)n.ParentPhysicalIndex, n.IsDeform ? CompactMeshEntityType.Bone : CompactMeshEntityType.Helper, 0, 0,
            Matrix(n.LocalBindMatrix), Matrix(n.InverseGlobalReferenceMatrix), 0, 0)).ToArray();
        return (contract, new(nodes.Length, nodes.Count(n => n.ParentIndex < 0), 0, nodes, []));
    }

    [Fact]
    public void MatchesPreparedFramesBoundsAndCanonicalNames()
    {
        var (expected, compiled) = Fixture();
        var rows = Dl1CompiledRigValidator.Validate(expected, compiled);
        Assert.Equal(expected.Nodes.Length, rows.Length);
        Assert.All(rows, r => Assert.InRange(r.LocalMatrixError, 0, 1e-5));
        Assert.Equal(expected.Nodes[0].Name.ToLowerInvariant(), rows[0].CompiledName);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("reference")]
    [InlineData("bounds")]
    [InlineData("parent")]
    [InlineData("type")]
    [InlineData("nan")]
    public void RejectsAlteredSemanticData(string change)
    {
        var (expected, compiled) = Fixture();
        var nodes = compiled.Entities.ToArray();
        int index = change == "parent" ? 1 : 0;
        CompactMeshEntity node = nodes[index];
        nodes[index] = change switch
        {
            "local" => node with { LocalMatrix = node.LocalMatrix with { M14 = node.LocalMatrix.M14 + .01f } },
            "reference" => node with { ReferenceMatrix = node.ReferenceMatrix with { M24 = node.ReferenceMatrix.M24 + .01f } },
            "bounds" => node with { Bounds = node.Bounds with { HalfX = node.Bounds.HalfX + .01f } },
            "parent" => node with { ParentIndex = -1 },
            "type" => node with { EntityType = CompactMeshEntityType.Mesh },
            _ => node with { LocalMatrix = node.LocalMatrix with { M11 = float.NaN } },
        };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledRigValidator.Validate(expected, compiled with { Entities = nodes }));
    }

    [Fact]
    public void RejectsMissingAndAmbiguousNativeIdentities()
    {
        var (expected, compiled) = Fixture();
        var nodes = compiled.Entities.ToArray();
        nodes[0] = nodes[0] with { Name = "other" };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledRigValidator.Validate(expected, compiled with { Entities = nodes }));
        nodes[0] = compiled.Entities[0];
        nodes[1] = nodes[1] with { Name = nodes[0].Name.ToUpperInvariant() };
        Assert.Throws<InvalidDataException>(() => Dl1CompiledRigValidator.Validate(expected, compiled with { Entities = nodes }));
    }

    [Fact]
    public void ReorderedSiblingsAreMappedByIdentityRatherThanSourceIndex()
    {
        var (expected, compiled) = Fixture();
        var additional = expected.Nodes[1] with { PhysicalIndex = 2, SourceBoneIndex = 2, Name = "another_child",
            DescriptorHash = ReAnimated.Codecs.Anm2.Dl1NameHash.Compute("another_child") };
        var expanded = new Dl1AuthoredRigContract(expected.SourceModelName, expected.SourceFbxSha256, expected.Nodes.Add(additional), expected.MorphChannels);
        var nodes = new[] { compiled.Entities[0], compiled.Entities[1] with { Index = 1, Name = "another_child" }, compiled.Entities[1] with { Index = 2 } };
        var rows = Dl1CompiledRigValidator.Validate(expanded, compiled with { DeclaredEntityCount = 3, Entities = nodes });
        Assert.Equal(2, rows[1].CompiledEntityIndex);
        Assert.Equal(1, rows[2].CompiledEntityIndex);
        Assert.Equal(0, rows[1].CompiledParentIndex);
    }

    private static CompactMatrix3x4 Matrix(TransformMatrix m) => new((float)m.M11, (float)m.M12, (float)m.M13, (float)m.M14,
        (float)m.M21, (float)m.M22, (float)m.M23, (float)m.M24, (float)m.M31, (float)m.M32, (float)m.M33, (float)m.M34);
}

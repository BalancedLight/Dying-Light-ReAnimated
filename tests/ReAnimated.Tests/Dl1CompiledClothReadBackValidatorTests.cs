using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1CompiledClothReadBackValidatorTests
{
    [Fact]
    public void MovableWeightedBoneHasNoMovementWarning()
    {
        var findings = Dl1CompiledClothReadBackValidator.Validate(
            Hierarchy(), Geometry(weightedEntity: 2), [("panel.phx", Phx("cloth_root", "cloth_tip"))]);
        Assert.Empty(findings);
    }

    [Fact]
    public void CaseOnlyCompilerRenameIsReportedWithoutClaimingNativeFailure()
    {
        var findings = Dl1CompiledClothReadBackValidator.Validate(
            Hierarchy(), Geometry(weightedEntity: 2), [("panel.phx", Phx("Cloth_Root", "Cloth_Tip"))]);
        Assert.Equal(2, findings.Length);
        Assert.All(findings, finding => Assert.Contains("differs in case", finding, StringComparison.Ordinal));
    }

    [Fact]
    public void UnweightedMovableGridCannotBeClaimedAsVisibleCloth()
    {
        var findings = Dl1CompiledClothReadBackValidator.Validate(
            Hierarchy(), Geometry(weightedEntity: 1), [("panel.phx", Phx("cloth_root", "cloth_tip"))]);
        Assert.Single(findings);
        Assert.Contains("none influence a rendered vertex", findings[0], StringComparison.Ordinal);
    }

    [Fact]
    public void CompiledBoneMissingBeyondCaseDifferenceFails()
    {
        Assert.Throws<InvalidDataException>(() => Dl1CompiledClothReadBackValidator.Validate(
            Hierarchy(), Geometry(weightedEntity: 2), [("panel.phx", Phx("cloth_root", "unpublished_tip"))]));
    }

    private static NativePhxDocument Phx(string root, string tip) => Dl1ClothCodec.ReadPhx(
        $"!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{{\nBonesGridSize(1, 2)\n" +
        $"Bone(0, 0, \"{root}\", 1, -1, -1)\nBone(0, 1, \"{tip}\", 0, -1, -1)\n}}\n");

    private static CompactMeshDocument Hierarchy()
    {
        string[] names = ["root", "cloth_root", "cloth_tip"];
        CompactMeshEntity[] entities = names.Select((name, index) => new CompactMeshEntity(
            index, name, 0, default, (short)(index - 1), CompactMeshEntityType.Bone,
            0, 0, CompactMatrix3x4.Identity, CompactMatrix3x4.Identity, 0, 0)).ToArray();
        return new CompactMeshDocument(entities.Length, 1, 0, entities, []);
    }

    private static CompiledMeshGeometryDocument Geometry(int weightedEntity)
    {
        var vertex = new CompiledVertex(Vector3.Zero, Vector3.UnitZ, Vector4.Zero,
            Vector2.Zero, Vector2.Zero, Vector4.One, new Vector4(1, 0, 0, 0),
            new CompiledBoneIndex4(0, 0, 0, 0));
        var surface = new CompiledMeshSurface(0, "panel", 0, 0, 0, 0,
            new CompiledVertexLayout(0, 0, []), [vertex], [0],
            [new CompiledMeshSubmesh(0, 0, 1, null, [(short)weightedEntity])]);
        return new CompiledMeshGeometryDocument([], [surface], [], CompiledMaterialDatabase.Empty,
            [], [], []);
    }
}

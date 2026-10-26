using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class RigNativeHierarchyIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeightedNativeEntityWithoutAnatomicalRoleKeepsItsBinding(bool geometryAssisted)
    {
        var (model, template) = Fixture();
        var correspondence = RigCorrespondenceSolver.Solve(template, model.Rig!,
            Options(model.Rig!, geometryAssisted));
        var mapped = Assert.Single(correspondence.Rows, row => row.TemplateIndex == 2);
        Assert.Equal(RigBoneDisposition.Mapped, mapped.Disposition);
        Assert.Equal(2, mapped.SourceBoneIndex);
        Assert.Null(mapped.Role);
        Assert.Equal(0, correspondence.ExtraCount);

        var fit = RigConformanceSolver.Solve(template, model.Rig!, correspondence,
            new() { UniformScale = 1, PelvisAnchor = Vector3D.Zero, Samples = [], RegionFits = [],
                ProportionResidual = 0, WorstSampleDeviation = 0, Evidence = "unit-scale fixture" });
        var actual = Dl1RigConformanceApplier.Apply(model, fit);
        var prepared = Dl1CustomModelRigPreparer.Prepare(actual);
        Assert.Equal(3, actual.Rig!.BoneCount);
        Assert.Equal("weighted_aux", prepared.Contract.Nodes[2].Name);
        Assert.Equal(model.Surfaces[0].PaletteBoneIndices.ToArray(), actual.Surfaces[0].PaletteBoneIndices.ToArray());
        for (int index = 0; index < model.Surfaces[0].Vertices.Length; index++)
        {
            var before = model.Surfaces[0].Vertices[index];
            var after = actual.Surfaces[0].Vertices[index];
            Assert.Equal(before.BoneIndices.ToArray(), after.BoneIndices.ToArray());
            Assert.Equal(before.BoneWeights.ToArray(), after.BoneWeights.ToArray());
            Assert.True((before.Position - after.Position).Length < 1e-9);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ANameDoesNotOverrideAnIncompatibleParent(bool geometryAssisted)
    {
        var (model, template) = Fixture();
        var original = model.Rig!;
        var source = new RigDefinition("different-parent", "Different parent",
        [original.Bones[0], original.Bones[1], new(2, "other_parent", 1, TransformTRS.Identity),
            new(3, "weighted_aux", 2, original.Bones[2].LocalBindPose)]);
        var result = RigCorrespondenceSolver.Solve(template, source, Options(source, geometryAssisted));
        Assert.Equal(RigBoneDisposition.Synthesized,
            Assert.Single(result.Rows, row => row.TemplateIndex == 2).Disposition);
        Assert.Contains(result.Rows, row => row.SourceBoneIndex == 3 && row.Disposition == RigBoneDisposition.Extra);
    }

    private static RigCorrespondenceOptions Options(RigDefinition rig, bool geometryAssisted) => new()
    {
        GeometryEvidence = !geometryAssisted ? null : new(new string('a', 64), "generic-current-binding",
            rig.Bones.Select(b => b.Name).ToImmutableArray(), rig.Bones.Select(b => b.ParentIndex).ToImmutableArray(),
            rig.Bones.Select(b => b.LocalBindPose).ToImmutableArray(), rig.CreateBindPose().GlobalMatrices,
            [new(rig.BoneCount - 1, 3, 1, rig.CreateBindPose().GlobalMatrices[^1].Translation,
                rig.CreateBindPose().GlobalMatrices[^1].Translation, rig.CreateBindPose().GlobalMatrices[^1].Translation)]),
    };

    private static (FbxModelAuthoringImportResult Model, Dl1RigTemplate Template) Fixture()
    {
        string hash = new('a', 64);
        Guid material = Guid.NewGuid();
        ImmutableArray<CustomModelBone> bones = [
            new() { Index = 0, Name = "bip01", ParentIndex = -1, Kind = BoneKind.Root,
                LocalBindTransform = TransformTRS.Identity, ExactLocalBindMatrix = TransformMatrix.Identity },
            new() { Index = 1, Name = "head", ParentIndex = 0, Kind = BoneKind.Deform, IsWeighted = true,
                LocalBindTransform = new(Vector3D.UnitY, QuaternionD.Identity, Vector3D.One),
                ExactLocalBindMatrix = TransformMatrix.CreateTranslation(Vector3D.UnitY) },
            new() { Index = 2, Name = "weighted_aux", ParentIndex = 1, Kind = BoneKind.Deform, IsWeighted = true,
                LocalBindTransform = new(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One),
                ExactLocalBindMatrix = TransformMatrix.CreateTranslation(Vector3D.UnitX) },
        ];
        var document = new CustomModelDocument
        {
            Name = "Generic native rig", Source = new() { OriginalFileName = "generic.fbx", ContentSha256 = hash, FbxVersion = 7400 },
            RigMode = CustomModelRigMode.ExactFbxRig, Bones = bones, RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            Materials = [new() { Id = material, Name = "material" }],
            Meshes = [new() { Name = "mesh", ControlPointCount = 3, PolygonCount = 1, TriangleCount = 1,
                ExpandedVertexCount = 3, MaterialSlotCount = 1 }],
        };
        var rig = document.CreateRigDefinition();
        var globals = rig.CreateBindPose().GlobalMatrices;
        var surface = new FbxModelSurface("part", "mesh", material,
            Enumerable.Range(0, 3).Select(i => new FbxModelVertex(new(1 + i * .1, 1 + i * .1, 0), Vector3D.UnitZ, 0, 0, [0], [1])).ToImmutableArray(),
            [0, 1, 2], [2], [globals[2].InvertedAffine()], true);
        var model = new FbxModelAuthoringImportResult(new(document, [1], ImmutableDictionary<string, ImmutableArray<byte>>.Empty),
            rig, [surface], ImmutableDictionary<Guid, AnimationClip>.Empty, null!);
        var template = new Dl1RigTemplate("generic", "generic", hash, bones.Select(b => new Dl1RigTemplateEntity
        {
            Index = b.Index, Name = b.Name, ParentIndex = b.ParentIndex, Kind = b.Kind, IsDeform = b.IsWeighted,
            LocalRestMatrix = b.ExactLocalBindMatrix, GlobalRestMatrix = globals[b.Index],
            SemanticRole = b.Index == 0 ? "body.root" : b.Index == 1 ? "body.head" : null,
        }));
        return (model, template);
    }
}

using System.Collections.Immutable;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Discovery;
using Xunit;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class InstalledDl1ModelCompilerFactAttribute : FactAttribute
{
    public InstalledDl1ModelCompilerFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DLR_RUN_INSTALLED_MODEL_COMPILER"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set DLR_RUN_INSTALLED_MODEL_COMPILER=1 to run the installed DL1 compiler acceptance gate.";
        }
    }
}

public sealed class Dl1OfficialCompilerSkinningReadBackAcceptanceTests
{
    private const string ArtifactRootEnvironmentVariable = "DLR_SKINNING_READBACK_ARTIFACT_ROOT";
    private readonly ITestOutputHelper _output;

    public Dl1OfficialCompilerSkinningReadBackAcceptanceTests(ITestOutputHelper output) =>
        _output = output;

    [InstalledDl1ModelCompilerFact]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public Task GenericTwoBoneTriangleCompilesAndReportsValidatedSkinningReadBack() => RunAcceptanceAsync(false);

    [InstalledDl1ModelCompilerFact]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public Task GenericTwoLevelLodCompilesAndReportsEveryLevelReadBack() => RunAcceptanceAsync(true);

    private async Task RunAcceptanceAsync(bool includeLods)
    {
        string? compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable();
        Assert.True(File.Exists(compiler), "The Dying Light Developer Tools compiler was not discovered.");
        Dl1InstallLocation? install = SteamInstallDiscovery.Discover()
            .FirstOrDefault(static candidate => candidate.IsValid);
        Assert.NotNull(install);
        string data0Pak = Path.Combine(install!.InstallPath, "DW", "Data0.pak");
        Assert.True(File.Exists(data0Pak), "The installed retail compiler bootstrap Data0.pak was not found.");

        string? artifactRoot = Environment.GetEnvironmentVariable(ArtifactRootEnvironmentVariable);
        string outputDirectory = string.IsNullOrWhiteSpace(artifactRoot)
            ? RpackTestData.CreateTemporaryDirectory()
            : Path.Combine(
                Path.GetFullPath(artifactRoot),
                "generic-skinning-readback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);

        FbxModelAuthoringImportResult model = CreateTwoBoneTriangle();
        if (includeLods) model = CreateTwoLevelModel(model);
        Dl1OfficialModelCompilerResult result = await Dl1OfficialModelCompiler.CompileAsync(
            new Dl1OfficialModelCompilerRequest
            {
                Model = model,
                CompilerExecutablePath = compiler!,
                RetailData0PakPath = data0Pak,
                OutputRpackPath = Path.Combine(outputDirectory, "generic_skinning_readback_pc.rpack"),
                ResourceName = "generic_skinning_readback",
                WorkingDirectoryRoot = Path.Combine(Path.GetDirectoryName(outputDirectory)!, "w"),
            });

        Assert.Equal(CustomModelBuildState.CompilerValidated, result.BuildReceipt.State);
        Assert.True(File.Exists(result.OutputRpackPath));
        Assert.True(File.Exists(result.CompiledMeshObjectPath));
        Assert.True(File.Exists(result.ReceiptPath));
        Assert.NotNull(result.CompilerEvidence.SkinningReadBackContractFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", result.CompilerEvidence.SkinningReadBackContractFingerprint!);
        int levels = includeLods ? 2 : 1;
        Assert.Equal(levels, result.CompilerEvidence.VerifiedSkinningSurfaceCount);
        int draws = includeLods ? 3 : 1;
        Assert.Equal(draws, result.CompilerEvidence.VerifiedSkinningSubsetCount);
        Assert.Equal(3 * draws, result.CompilerEvidence.VerifiedSkinningVertexCount);
        Assert.Equal(6 * draws, result.CompilerEvidence.VerifiedSkinningInfluenceCount);
        Dl1PreparedPhysicalNodeReadBackEvidence physicalEvidence =
            Assert.IsType<Dl1PreparedPhysicalNodeReadBackEvidence>(result.CompilerEvidence.PreparedPhysicalNodeReadBack);
        Assert.Equal(model.Rig!.BoneCount + FbxModelLodLayout.Create(model).Length + 1, physicalEvidence.VerifiedNodeCount);
        Assert.Equal(physicalEvidence.Nodes.Length, physicalEvidence.VerifiedNodeCount);
        Assert.NotEmpty(physicalEvidence.Nodes);
        Assert.All(
            physicalEvidence.Nodes,
            node =>
            {
                Assert.Equal(node.Expected.PhysicalIndex, node.CompiledEntityIndex);
                Assert.Equal(node.Expected.ParentIndex, node.CompiledParentIndex);
                Assert.True(node.CompiledLocalMatrix.IsFinite);
                Assert.True(node.CompiledReferenceMatrix.IsFinite);
                Assert.True(node.CompiledBounds.IsFinite);
                Assert.True(node.CompiledBounds.HalfX > 0.0 || node.CompiledBounds.HalfY > 0.0 || node.CompiledBounds.HalfZ > 0.0);
            });
        Dl1PreparedPhysicalNodeReadBack helper = Assert.Single(
            physicalEvidence.Nodes.Where(node => node.CompiledName == "synthetic_unweighted_helper"));
        Assert.Equal(CompactMeshEntityType.Helper, helper.CompiledEntityType);
        Assert.Equal(0, helper.CompiledParentIndex);
        Assert.NotEqual(CompactMatrix3x4.Identity, helper.CompiledLocalMatrix);
        Assert.NotEqual(CompactMatrix3x4.Identity, helper.CompiledReferenceMatrix);
        Assert.True(helper.CompiledBounds.HalfX > 0.0);
        Assert.True(helper.CompiledBounds.HalfY > 0.0);
        Assert.True(helper.CompiledBounds.HalfZ > 0.0);
        _output.WriteLine("Skinning read-back receipt: {0}", result.ReceiptPath);
        _output.WriteLine("Skinning contract SHA-256: {0}", result.CompilerEvidence.SkinningReadBackContractFingerprint);
        _output.WriteLine(
            "Verified skin surfaces/subsets/vertices/influences: {0}/{1}/{2}/{3}",
            result.CompilerEvidence.VerifiedSkinningSurfaceCount,
            result.CompilerEvidence.VerifiedSkinningSubsetCount,
            result.CompilerEvidence.VerifiedSkinningVertexCount,
            result.CompilerEvidence.VerifiedSkinningInfluenceCount);
        _output.WriteLine(
            "Verified physical nodes: {0}; helper type/parent/local/bounds: {1}/{2}/{3}/{4}",
            physicalEvidence.VerifiedNodeCount,
            helper.CompiledEntityType,
            helper.CompiledParentIndex,
            helper.CompiledLocalMatrix,
            helper.CompiledBounds);
    }

    private static FbxModelAuthoringImportResult CreateTwoLevelModel(FbxModelAuthoringImportResult original)
    {
        FbxModelSurface first = original.Surfaces[0] with
        {
            SourceGeometry = new GeometrySourceComponent("fbx:902:700", original.Surfaces[0].Vertices.Select(vertex => vertex.Position).ToImmutableArray()),
        };
        FbxModelSurface second = first with
        {
            Id = "generic_lower_detail",
            MeshName = "generic_lower_detail",
            SourceGeometry = new GeometrySourceComponent("fbx:903:701", first.Vertices.Select(vertex => vertex.Position * 0.8).ToImmutableArray()),
            Vertices = first.Vertices.Select(vertex => vertex with
            {
                Position = vertex.Position * 0.8,
                BoneIndices = vertex.BoneIndices.Select(index => 1 - index).ToImmutableArray(),
            }).ToImmutableArray(),
            PaletteBoneIndices = first.PaletteBoneIndices.Reverse().ToImmutableArray(),
            InverseBindMatrices = first.InverseBindMatrices.Reverse().ToImmutableArray(),
            MorphTargets = first.MorphTargets.Select(target => target with
            {
                PositionDeltas = target.PositionDeltas.Select(delta => delta * 0.8).ToImmutableArray(),
            }).ToImmutableArray(),
        };
        CustomModelMaterial extraMaterial = original.Package.Document.Materials[0] with
        {
            Id = new Guid("2f7e77e6-4127-46c1-8aaf-414a391a88c7"),
            Name = "detail",
        };
        FbxModelSurface extraDraw = second with
        {
            Id = "generic_material_draw",
            MeshName = "generic_material_draw",
            MaterialId = extraMaterial.Id,
            SourceGeometry = new GeometrySourceComponent("fbx:902:702", second.Vertices.Select(vertex => vertex.Position + new Vector3D(4, 3, 2)).ToImmutableArray()),
            Vertices = second.Vertices.Select(vertex => vertex with { Position = vertex.Position + new Vector3D(4, 3, 2) }).ToImmutableArray(),
        };
        return original with
        {
            Package = original.Package with { Document = original.Package.Document with { Materials = original.Package.Document.Materials.Add(extraMaterial) } },
            Surfaces = [first, extraDraw, second],
            SourceLodGroups = [new(901, 900, "generic_detail_group", [],
                [new(0, 902, "generic_high", [new(700, "generic_high_geometry", 902), new(702, "generic_material_geometry", 902)]),
                 new(1, 903, "generic_low", [new(701, "generic_low_geometry", 903)])])],
        };
    }

    internal static FbxModelAuthoringImportResult CreateTwoBoneTriangle()
    {
        FbxModelAuthoringImportResult original = CustomModelSchema2MorphTests.CreateMorphModel();
        CustomModelDocument document = original.Package.Document;
        RigDefinition originalRig = Assert.IsType<RigDefinition>(original.Rig);
        TransformTRS childBind = new(
            new Vector3D(0.0, 0.10, 0.0),
            QuaternionD.Identity,
            Vector3D.One);
        TransformTRS helperBind = new(
            new Vector3D(0.15, 0.20, 0.05),
            QuaternionD.FromAxisAngle(new Vector3D(0.0, 0.0, 1.0), Math.PI / 6.0),
            Vector3D.One);
        BoneDefinition helperDefinition = new(
            1,
            "synthetic_unweighted_helper",
            0,
            helperBind,
            BoneKind.Helper);
        BoneDefinition childDefinition = new(
            2,
            "synthetic_weighted_child",
            1,
            childBind,
            BoneKind.Deform);
        RigDefinition rig = new(
            originalRig.Id,
            originalRig.DisplayName,
            originalRig.Bones.Add(helperDefinition).Add(childDefinition));

        CustomModelBone helperBone = new()
        {
            Index = 1,
            FbxObjectId = 2,
            Name = helperDefinition.Name,
            ParentIndex = 0,
            LocalBindTransform = helperBind,
            ExactLocalBindMatrix = helperBind.ToMatrix(),
            Kind = BoneKind.Helper,
            IsWeighted = false,
        };
        CustomModelBone childBone = new()
        {
            Index = 2,
            FbxObjectId = 3,
            Name = childDefinition.Name,
            ParentIndex = 1,
            LocalBindTransform = childBind,
            ExactLocalBindMatrix = childBind.ToMatrix(),
            Kind = BoneKind.Deform,
            IsWeighted = true,
        };
        ImmutableArray<CustomModelBone> bones = document.Bones.Add(helperBone).Add(childBone);
        document = document with
        {
            Bones = bones,
            RigSignature = CustomModelContractSignatures.ComputeRig(bones),
            Meshes = document.Meshes.SetItem(0, document.Meshes[0] with
            {
                MaximumSourceInfluences = 2,
                MaximumRetainedInfluences = 2,
                RequiredPaletteSize = 2,
            }),
        };
        document.Validate();

        FbxModelSurface originalSurface = Assert.Single(original.Surfaces);
        ImmutableArray<FbxModelVertex> vertices =
        [
            originalSurface.Vertices[0] with { BoneIndices = [0, 1], BoneWeights = [0.75, 0.25] },
            originalSurface.Vertices[1] with { BoneIndices = [0, 1], BoneWeights = [0.50, 0.50] },
            originalSurface.Vertices[2] with { BoneIndices = [0, 1], BoneWeights = [0.25, 0.75] },
        ];
        TransformMatrix childInverseBind = (helperBind.ToMatrix() * childBind.ToMatrix()).InvertedAffine();
        FbxModelSurface surface = originalSurface with
        {
            Vertices = vertices,
            Indices = [0u, 1u, 2u],
            PaletteBoneIndices = [0, 2],
            InverseBindMatrices = [TransformMatrix.Identity, childInverseBind],
        };

        return original with
        {
            Package = original.Package with { Document = document },
            Rig = rig,
            Surfaces = [surface],
        };
    }
}

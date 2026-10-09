using ReAnimated.Codecs.Fbx;
using System.Collections.Immutable;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.DL1.Assets.Discovery;
using Xunit.Abstractions;

namespace ReAnimated.Tests;

public sealed class RigidPropCompilerAcceptanceTests(ITestOutputHelper output)
{
    [InstalledDl1ModelCompilerFact]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public async Task AuthoredDoorCompilesWithItsAnimationLibraryAndOptionalIsolatedDeployment()
    {
        string compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable() ??
            throw new InvalidOperationException("The Developer Tools compiler was not discovered.");
        var install = SteamInstallDiscovery.Discover().First(static location => location.IsValid);
        string? artifactRoot = Environment.GetEnvironmentVariable("DLR_SKINNING_READBACK_ARTIFACT_ROOT");
        string directory = string.IsNullOrWhiteSpace(artifactRoot) ? RpackTestData.CreateTemporaryDirectory()
            : Path.Combine(Path.GetFullPath(artifactRoot), "authored-door-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = FbxModelAuthoringImporter.Import(RigidPropWorkflowTests.CreateDoorFixture(), "generated-door.fbx");
            model = model with { Package = model.Package with { Document = model.Package.Document with
            {
                BuildSettings = model.Package.Document.BuildSettings with
                { ResourceName = "generated_door", CharacterId = "generated_door", AnimationScriptAlias = "generated_door_anim" },
                AnimationClips = model.Package.Document.AnimationClips.Select(static clip => clip with { Included = false }).ToImmutableArray(),
            } } };
            model = FbxAuthoredAnimationAuthoring.Create(model, "open", 1.5, new FrameRate(30, 1));
            var clip = model.Package.Document.AnimationClips.Single(static clip => clip.AuthoredAnimation is not null);
            var pivot = model.Package.Document.Bones.Single(bone =>
                bone.FbxObjectId == model.Surfaces[0].RigidGeometryOwnerFbxObjectId);
            TransformTRS opened = pivot.LocalBindTransform with { Rotation = QuaternionD.FromAxisAngle(Vector3D.UnitY, Math.PI / 2) };
            model = FbxAuthoredAnimationAuthoring.SetKey(model, clip.Id, pivot.Index, 45, opened);
            var library = await CustomModelAnimationLibraryExporter.PrepareAsync(new()
            { Model = model, OutputPath = Path.Combine(directory, "generated_door_anim_pc.rpack") });
            var result = await Dl1OfficialModelCompiler.CompileAsync(new()
            {
                Model = model, CompilerExecutablePath = compiler,
                RetailData0PakPath = Path.Combine(install.InstallPath, "DW", "Data0.pak"),
                OutputRpackPath = Path.Combine(directory, "generated_door_pc.rpack"), ResourceName = "generated_door",
                AnimationScriptAlias = library.AnimationScriptName, AnimationLibrary = library,
                WorkingDirectoryRoot = Environment.GetEnvironmentVariable("DLR_MODEL_COMPILER_STAGE_ROOT"),
            });
            Assert.Equal(CustomModelBuildState.CompilerValidated, result.BuildReceipt.State);
            CustomModelPackageSerializer.SaveAtomic(model.Package, Path.Combine(directory, "generated-door.dlrmodel"));
            output.WriteLine("Authored door compiler receipt: {0}", result.ReceiptPath);
            string? projectRoot = Environment.GetEnvironmentVariable("DLR_MODEL_ACCEPTANCE_PROJECT");
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                Assert.True(File.Exists(Path.Combine(projectRoot, "desc.scr")), "Provide an existing isolated Developer Tools project.");
                var request = new Dl1DeveloperToolsDeploymentRequest
                {
                    Model = model, ProjectRoot = projectRoot, CompilerExecutablePath = compiler,
                    RetailData0PakPath = Path.Combine(install.InstallPath, "DW", "Data0.pak"),
                    CharacterId = "generated_door", ModelResourceName = "generated_door", AnimationLibraryName = library.AnimationScriptName,
                    PreparedAnimationLibrary = library, InstallProjectDataAnimationRpack = true,
                    CompilerWorkingDirectoryRoot = Environment.GetEnvironmentVariable("DLR_MODEL_COMPILER_STAGE_ROOT"),
                };
                var plan = await Dl1DeveloperToolsProjectDeployer.PreflightAsync(request);
                Assert.True(plan.CanDeploy, string.Join("; ", plan.Conflicts));
                var deployed = await Dl1DeveloperToolsProjectDeployer.DeployAsync(request);
                output.WriteLine("Isolated door deployment receipt: {0}", deployed.ReceiptPath);
            }
        }
        finally { if (string.IsNullOrWhiteSpace(artifactRoot)) RpackTestData.DeleteTemporaryDirectory(directory); }
    }
    [InstalledDl1ModelCompilerFact]
    [Trait("ValidationTier", "Release")]
    [Trait("Gate", "InstalledDl1ModelCompiler")]
    public async Task AnimatedNullMeshAndMixedPropsCompileWithMatchingPhysicalHierarchy()
    {
        string compiler = Dl1OfficialModelCompiler.FindDefaultCompilerExecutable() ??
            throw new InvalidOperationException("The Developer Tools compiler was not discovered.");
        var install = SteamInstallDiscovery.Discover().First(static location => location.IsValid);
        string? artifactRoot = Environment.GetEnvironmentVariable("DLR_SKINNING_READBACK_ARTIFACT_ROOT");
        string directory = string.IsNullOrWhiteSpace(artifactRoot)
            ? RpackTestData.CreateTemporaryDirectory()
            : Path.Combine(Path.GetFullPath(artifactRoot), "rigid-props-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[][] fixtures = [RigidPropWorkflowTests.CreateHingedPropFixture(false),
                RigidPropWorkflowTests.CreateHingedPropFixture(true), RigidPropWorkflowTests.CreateMixedHingedPropFixture()];
            for (int index = 0; index < fixtures.Length; index++)
            {
                var model = FbxModelAuthoringImporter.Import(fixtures[index], "generated-prop.fbx");
                string resource = "generated_prop_" + index;
                var result = await Dl1OfficialModelCompiler.CompileAsync(new()
                {
                    Model = model, CompilerExecutablePath = compiler,
                    RetailData0PakPath = Path.Combine(install.InstallPath, "DW", "Data0.pak"),
                    OutputRpackPath = Path.Combine(directory, resource + "_pc.rpack"), ResourceName = resource,
                    WorkingDirectoryRoot = Environment.GetEnvironmentVariable("DLR_MODEL_COMPILER_STAGE_ROOT"),
                });
                Assert.Equal(CustomModelBuildState.CompilerValidated, result.BuildReceipt.State);
                Assert.NotNull(result.CompilerEvidence.PreparedPhysicalNodeReadBack);
                Assert.NotEmpty(result.CompilerEvidence.PreparedPhysicalNodeReadBack!.Nodes);
                output.WriteLine("Compiler receipt: {0}", result.ReceiptPath);
            }
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(artifactRoot)) RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }
}

using System.Collections.Immutable;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectModelOnlyPersistenceTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "ProjectPersistence")]
    public void Schema3ModelLibraryWithoutAnimationsRetainsAllRiggedModels()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dlra-model-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            ImmutableArray<ProjectAssetReference> assets = Enumerable.Range(0, 4)
                .Select(index => new ProjectAssetReference
                {
                    Id = Guid.NewGuid(),
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = $"assets/model-{index}.dlrmodel",
                    ContentSha256 = new string('a', 64),
                }).ToImmutableArray();
            ImmutableArray<ProjectModelEntry> models = assets.Select((asset, index) => new ProjectModelEntry
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Name = $"Rigged model {index}",
                RigSignature = new string('b', 64),
                AuthoringRigContractSignature = new string('c', 64),
                AnimationSkeletonSignature = new string('d', 64),
                IsStatic = false,
                ExportableEyeCameraHelperCount = 1,
            }).ToImmutableArray();
            DlraProject project = DlraProject.Create("Model library") with
            {
                Assets = assets,
                Models = models,
                ModelsWorkspace = new ProjectModelsWorkspaceState { PackageAssetId = assets[1].Id },
                Workflow = new ProjectWorkflowState { SelectedModelId = models[1].Id },
            };
            project.Validate();
            string path = Path.Combine(directory, "library.dlraproj");
            ProjectSerializer.SaveAtomic(project, path);
            DlraProject reopened = ProjectSerializer.Load(path);

            Assert.Equal(models.ToArray(), reopened.Models.ToArray());
            Assert.Equal(assets.ToArray(), reopened.Assets.ToArray());
            Assert.Equal(project.ModelsWorkspace, reopened.ModelsWorkspace);
            Assert.Equal(models[1].Id, reopened.Workflow.SelectedModelId);
            Assert.Empty(reopened.AnimationSources);
            Assert.Empty(reopened.AnimationVariants);

            ProjectSerializer.SaveAtomic(reopened, path);
            Assert.Equal(models.ToArray(), ProjectSerializer.Load(path).Models.ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

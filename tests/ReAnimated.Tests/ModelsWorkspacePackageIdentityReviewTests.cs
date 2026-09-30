using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ModelsWorkspacePackageIdentityReviewTests
{
    [Fact]
    [Trait("Gate", "ProjectPersistence")]
    public async Task SameIdentityPackageCannotReplaceCurrentSessionWithoutReview()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult original =
                FbxModelAuthoringImporter.Import(
                    BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                    "generic-character.fbx");
            FbxModelAuthoringImportResult replacement = original with
            {
                Package = original.Package with
                {
                    Document = original.Package.Document with
                    {
                        Name = "Generic replacement",
                    },
                },
            };
            string replacementPath = Path.Combine(
                directory,
                "generic-replacement.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(
                replacement.Package,
                replacementPath);

            bool permit = false;
            int reviews = 0;
            using var workspace = new ModelsWorkspaceViewModel(
                new NoDialogs(),
                static _ => { },
                static _ => Task.CompletedTask,
                static () => null,
                confirmPackageIdentityReplacement: (_, path, activeSameIdentity) =>
                {
                    reviews++;
                    Assert.Equal(replacementPath, path);
                    Assert.True(activeSameIdentity);
                    return permit
                        ? CustomModelPackageOpenDecision.Open
                        : CustomModelPackageOpenDecision.Cancel;
                });
            workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
                original,
                "generic-original.dlrmodel",
                new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                }));
            FbxModelAuthoringImportResult prior =
                workspace.CaptureProjectSession().Model!;

            await workspace.OpenPackagePathAsync(replacementPath);

            Assert.Equal(1, reviews);
            Assert.Same(prior, workspace.CaptureProjectSession().Model);
            Assert.Contains("canceled", workspace.BuildStatus,
                StringComparison.OrdinalIgnoreCase);

            permit = true;
            await workspace.OpenPackagePathAsync(replacementPath);

            Assert.Equal(2, reviews);
            Assert.Equal("Generic replacement",
                workspace.CaptureProjectSession().Model!.Package.Document.Name);
            Assert.Equal(original.Package.Document.ModelId,
                workspace.CaptureProjectSession().Model!.Package.Document.ModelId);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    [Trait("Gate", "ProjectPersistence")]
    public async Task MatchingDiskPackageStillReviewsUnsavedActiveSessionEdits()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            FbxModelAuthoringImportResult original =
                FbxModelAuthoringImporter.Import(
                    BlenderFbxStrictValidationTests.CreateValidModelFixture(),
                    "generic-character.fbx");
            string packagePath = Path.Combine(directory, "generic-character.dlrmodel");
            CustomModelPackageSerializer.SaveAtomic(original.Package, packagePath);
            FbxModelAuthoringImportResult restored =
                FbxModelAuthoringImporter.ImportPackage(
                    CustomModelPackageSerializer.Load(packagePath));

            CustomModelPackageOpenDecision decision =
                CustomModelPackageOpenDecision.KeepCurrent;
            var activeMatches = new List<bool>();
            int synchronizations = 0;
            using var workspace = new ModelsWorkspaceViewModel(
                new NoDialogs(),
                static _ => { },
                static _ => Task.CompletedTask,
                static () => null,
                synchronizeProject: () =>
                {
                    synchronizations++;
                    return Task.CompletedTask;
                },
                confirmPackageIdentityReplacement: (_, path, activeSameIdentity) =>
                {
                    Assert.Equal(packagePath, path);
                    activeMatches.Add(activeSameIdentity);
                    return decision;
                });
            workspace.CommitProjectRestore(new PreparedModelsWorkspaceRestore(
                restored,
                packagePath,
                new ProjectModelsWorkspaceState
                {
                    PackageAssetId = Guid.NewGuid(),
                }));

            FbxModelAuthoringImportResult clean =
                workspace.CaptureProjectSession().Model!;
            await workspace.OpenPackagePathAsync(packagePath);
            Assert.Single(activeMatches);
            Assert.True(activeMatches[0]);
            Assert.Same(clean, workspace.CaptureProjectSession().Model);
            Assert.Equal(0, synchronizations);
            Assert.False(MainWindowViewModel.NeedsPackageReplacementReview(
                "abc", "ABC", activeSameIdentityWithPendingEdits: false));

            workspace.ModelName = "Unsaved live edit";
            FbxModelAuthoringImportResult edited =
                workspace.CaptureProjectSession().Model!;
            decision = CustomModelPackageOpenDecision.Cancel;
            await workspace.OpenPackagePathAsync(packagePath);

            Assert.Equal(2, activeMatches.Count);
            Assert.True(activeMatches[1]);
            Assert.True(MainWindowViewModel.NeedsPackageReplacementReview(
                "abc", "ABC", activeSameIdentityWithPendingEdits: true));
            Assert.Same(edited, workspace.CaptureProjectSession().Model);
            Assert.Equal(0, synchronizations);
            Assert.Equal("Unsaved live edit", workspace.ModelName);
            Assert.Contains("canceled", workspace.BuildStatus,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    private sealed class NoDialogs : IProjectFileDialogService
    {
        public string? ShowOpenProjectDialog(string? initialPath) => null;
        public string? ShowSaveProjectDialog(string suggestedName, string? currentPath) => null;
    }
}

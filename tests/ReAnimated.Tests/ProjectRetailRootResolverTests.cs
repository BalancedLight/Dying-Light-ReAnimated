using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectRetailRootResolverTests
{
    [Fact]
    public void ResolvesConfiguredRootsAgainstProjectDirectoryInOrder()
    {
        string directory = RpackTestData.CreateTemporaryDirectory();
        try
        {
            string projectPath = Path.Combine(directory, "authoring.dlraproj");
            DlraProject project = DlraProject.Create("Authoring") with
            {
                Dl1Settings = new Dl1ProjectSettings
                {
                    AdditionalRpackRoots =
                    [
                        "packs/overrides",
                        "packs/secondary.rpack",
                    ],
                },
            };

            IReadOnlyList<string> roots =
                ProjectRetailRootResolver.ResolveAdditionalRpackRoots(
                    project,
                    projectPath);

            Assert.Equal(
                [
                    Path.Combine(directory, "packs", "overrides"),
                    Path.Combine(directory, "packs", "secondary.rpack"),
                ],
                roots);
        }
        finally
        {
            RpackTestData.DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void UnsavedProjectWithConfiguredRootFailsClearly()
    {
        DlraProject project = DlraProject.Create("Unsaved") with
        {
            Dl1Settings = new Dl1ProjectSettings
            {
                AdditionalRpackRoots =
                    ImmutableArray.Create("packs"),
            },
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(
            () => ProjectRetailRootResolver.ResolveAdditionalRpackRoots(
                project,
                null));
        Assert.Contains(
            "Save the project",
            exception.Message,
            StringComparison.Ordinal);
    }
}

using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AnimationExportDefaultsTests
{
    [Fact]
    public void NewRetailPlayerVariantAutomaticallyUsesDlc99()
    {
        var project = Project(ProjectAssetKind.RetailGameResource, "player_generic");
        var normalized = ProjectAnimationOutputNormalizer.Normalize(project);
        var library = Assert.Single(normalized.AnimationLibraries);
        Assert.Equal("anims_player_dlc99", library.ResourceName);
        Assert.Equal(library.Id, normalized.AnimationVariants[0].OwningAnimationLibraryId);
        Assert.Equal(library.Id, normalized.Models[0].RootAnimationLibraryId);
        Assert.Empty(library.Imports);
        Assert.Equal(library.Id, ProjectAnimationOutputNormalizer.Normalize(normalized).AnimationLibraries[0].Id);
    }

    [Fact]
    public void CustomPlayerCompatibleModelKeepsItsOwnLibrary()
    {
        var normalized = ProjectAnimationOutputNormalizer.Normalize(Project(ProjectAssetKind.CustomModelSource, "player_compatible_character"));
        Assert.Equal("player_compatible_character_animations", Assert.Single(normalized.AnimationLibraries).ResourceName);
    }

    [Fact]
    public void ExistingScriptAssignmentIsNotRenamed()
    {
        var project = Project(ProjectAssetKind.RetailGameResource, "player_generic");
        var library = new ProjectAnimationLibrary { ResourceName = "anims_player_dlc72", DisplayName = "Saved override" };
        project = project with { AnimationLibraries = [library], Models = [project.Models[0] with { RootAnimationLibraryId = library.Id }], AnimationVariants = [project.AnimationVariants[0] with { OwningAnimationLibraryId = library.Id }] };
        Assert.Equal("anims_player_dlc72", Assert.Single(ProjectAnimationOutputNormalizer.Normalize(project).AnimationLibraries).ResourceName);
    }

    [Fact]
    public void CustomLibraryCanBorrowStockAnimationsWithoutBecomingAnOverride()
    {
        var project = Project(ProjectAssetKind.CustomModelSource, "generic_character");
        var library = new ProjectAnimationLibrary
        {
            ResourceName = "generic_character_animations",
            Imports = [new ProjectAnimationLibraryImport
            {
                Kind = ProjectAnimationLibraryImportKind.RetailScript,
                RetailScriptIdentity = new ProjectRetailAssetIdentity
                {
                    ResourceName = "anims_player",
                    ContentSha256 = new string('c', 64),
                },
            }],
        };
        project = project with
        {
            AnimationLibraries = [library],
            Models = [project.Models[0] with { RootAnimationLibraryId = library.Id }],
        };

        var normalized = ProjectAnimationOutputNormalizer.Normalize(project);
        Assert.Equal(library, Assert.Single(normalized.AnimationLibraries));
        Assert.Equal(library.Id, normalized.AnimationVariants[0].OwningAnimationLibraryId);
    }

    [Fact]
    public void PlayerTargetsShareTheDefaultOverrideWithoutDuplicateBanks()
    {
        var project = Project(ProjectAssetKind.RetailGameResource, "player_generic");
        var second = project.Models[0] with { Id = Guid.NewGuid(), Name = "Another player target" };
        project = project with { Models = project.Models.Add(second), AnimationVariants = project.AnimationVariants.Add(project.AnimationVariants[0] with { Id = Guid.NewGuid(), Name = "second_motion", TargetModelId = second.Id }) };
        var normalized = ProjectAnimationOutputNormalizer.Normalize(project);
        var library = Assert.Single(normalized.AnimationLibraries);
        Assert.All(normalized.AnimationVariants, variant => Assert.Equal(library.Id, variant.OwningAnimationLibraryId));
        Assert.Equal(2, normalized.AnimationVariants.Select(v => v.OutputAnm2Name).Distinct().Count());
    }

    private static DlraProject Project(ProjectAssetKind targetKind, string targetName)
    {
        var asset = new ProjectAssetReference { Kind = targetKind, RelativePath = "Models/generic_model.dat", ContentSha256 = new string('a', 64) };
        var sourceAsset = new ProjectAssetReference { Kind = ProjectAssetKind.SourceAnimation, RelativePath = "Sources/generic_motion.fbx", ContentSha256 = new string('b', 64) };
        var model = new ProjectModelEntry { AssetId = asset.Id, Name = targetName };
        var source = new ProjectAnimationSource { Name = "generic_motion", SourceAssetId = sourceAsset.Id, FrameRate = new FrameRate(30, 1), FrameCount = 10 };
        return DlraProject.Create("Export defaults") with { Assets = [asset, sourceAsset], Models = [model], AnimationSources = [source], AnimationVariants = [new ProjectAnimationVariant { SourceId = source.Id, TargetModelId = model.Id, Name = "generic_motion" }] };
    }
}

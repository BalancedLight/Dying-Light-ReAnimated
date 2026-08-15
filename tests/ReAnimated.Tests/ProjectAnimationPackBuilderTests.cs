using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectAnimationPackBuilderTests
{
    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "AnimationProjectExport")]
    public void TransitiveProjectScrImportsAreIncludedInPackAndLooseSources()
    {
        Guid rootId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        DlraProject project = CreateProject(
            modelId,
            sourceId,
            variantId,
            rootId,
            childId,
            rootImportsChild: true);

        MainWindowViewModel.BuiltProjectAnimationPack result =
            MainWindowViewModel.BuildProjectAnimationPack(
                project,
                [CreateTarget(modelId, variantId, rootId, "generic_motion")]);

        Assert.Equal(
            ["child_scr", "root_scr"],
            result.Scripts.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["child_scr", "root_scr"],
            result.LooseScripts.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(
            "!include(\"child_scr.scr\")",
            result.LooseScripts["root_scr"],
            StringComparison.Ordinal);
        Assert.Contains(
            "SeqTrack( \"generic_motion\"",
            result.LooseScripts["child_scr"],
            StringComparison.Ordinal);
        Assert.True(result.Animations.ContainsKey("generic_motion"));
        Assert.NotEmpty(result.Rpack);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "AnimationProjectExport")]
    public void CustomModelRootMustReachAssignedVariantLibrary()
    {
        Guid rootId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        DlraProject project = CreateProject(
            modelId,
            sourceId,
            variantId,
            rootId,
            childId,
            rootImportsChild: false);

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() =>
            MainWindowViewModel.BuildProjectAnimationPack(
                project,
                [CreateTarget(
                    modelId,
                    variantId,
                    rootId,
                    "generic_motion")]));

        Assert.Contains(
            "does not import assigned SCR",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    [Trait("Gate", "AnimationProjectExport")]
    public void DuplicateType320IdentityIsRejectedWithoutSilentSuffixing()
    {
        Guid rootId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        DlraProject project = CreateProject(
            modelId,
            sourceId,
            variantId,
            rootId,
            childId,
            rootImportsChild: true);
        MainWindowViewModel.ProjectAnimationPackTarget target =
            CreateTarget(
                modelId,
                variantId,
                rootId,
                "duplicate_clip");
        target = target with
        {
            Animations =
            [
                target.Animations[0],
                target.Animations[0] with
                {
                    Payload = "ANM2-B"u8.ToArray(),
                },
            ],
        };

        InvalidOperationException exception = Assert.Throws<
            InvalidOperationException>(() =>
            MainWindowViewModel.BuildProjectAnimationPack(
                project,
                [target]));

        Assert.Contains(
            "is duplicated",
            exception.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            variantId.ToString("N")[..8],
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DlraProject CreateProject(
        Guid modelId,
        Guid sourceId,
        Guid variantId,
        Guid rootId,
        Guid childId,
        bool rootImportsChild)
    {
        var root = new ProjectAnimationLibrary
        {
            Id = rootId,
            ResourceName = "root_scr",
            DisplayName = "Root SCR",
            Imports = rootImportsChild
                ?
                [
                    new ProjectAnimationLibraryImport
                    {
                        Kind =
                            ProjectAnimationLibraryImportKind.ProjectLibrary,
                        ProjectLibraryId = childId,
                    },
                ]
                : [],
        };
        var child = new ProjectAnimationLibrary
        {
            Id = childId,
            ResourceName = "child_scr",
            DisplayName = "Child SCR",
        };
        return new DlraProject
        {
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = Guid.NewGuid(),
                    Name = "Generic model",
                    RigSignature = Sha('a'),
                    RootAnimationLibraryId = rootId,
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "generic_motion",
                    SourceAssetId = Guid.NewGuid(),
                    FrameCount = 2,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = variantId,
                    SourceId = sourceId,
                    Name = "Generic target variant",
                    TargetModelId = modelId,
                    TargetRigSignature = Sha('b'),
                    OwningAnimationLibraryId = childId,
                    OutputAnm2Name = "generic_motion.anm2",
                },
            ],
            AnimationLibraries = [root, child],
        };
    }

    private static MainWindowViewModel.ProjectAnimationPackTarget
        CreateTarget(
            Guid modelId,
            Guid variantId,
            Guid rootLibraryId,
            string resourceName) =>
        new(
            new ProjectModelEntry
            {
                Id = modelId,
                AssetId = Guid.NewGuid(),
                Name = "Generic model",
                RigSignature = Sha('a'),
                RootAnimationLibraryId = rootLibraryId,
            },
            IsCustomModel: true,
            Animations:
            [
                new Dl1PortableAnimationResource
                {
                    VariantId = variantId,
                    Name = resourceName,
                    Role = Dl1PortableAnimationRole.Body,
                    Payload = "ANM2-A"u8.ToArray(),
                    FrameCount = 2,
                    FramesPerSecond = 24.0f,
                    SourceFingerprint = Sha('c'),
                },
            ]);

    private static string Sha(char value) => new(value, 64);
}

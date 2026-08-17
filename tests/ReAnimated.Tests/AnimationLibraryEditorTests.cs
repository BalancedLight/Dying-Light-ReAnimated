using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class AnimationLibraryEditorTests
{
    [Fact]
    public void EmptyProjectCanCreateAndAssignItsFirstLibrary()
    {
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_first_target.anm2"),
            });

        Assert.Null(viewModel.SelectedLibrary);
        viewModel.AddLibrary();

        Assert.True(viewModel.TryCreateResult(out
            AnimationLibraryAssignmentResult? result));
        ProjectAnimationLibrary created = Assert.Single(result!.Libraries);
        Assert.Equal(created.Id, result.OwningLibraryId);
        Assert.Equal(
            "generic_first_target.anm2",
            result.OutputAnm2Name);
        Assert.Equal(
            "animation_library",
            created.ResourceName);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void EditorPreservesOrderedImportsAndBuildsReadableSourcePreview()
    {
        Guid dependencyId = Guid.NewGuid();
        ProjectAnimationLibrary dependency = Library(
            dependencyId,
            "generic_shared_motion");
        ProjectRetailAssetIdentity retail = RetailScript(
            "generic_stock_motion",
            'a');
        ProjectAnimationLibrary owner = Library(
            Guid.NewGuid(),
            "generic_actor_motion") with
        {
            Imports =
            [
                new ProjectAnimationLibraryImport
                {
                    Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
                    ProjectLibraryId = dependencyId,
                },
                new ProjectAnimationLibraryImport
                {
                    Kind = ProjectAnimationLibraryImportKind.RetailScript,
                    RetailScriptIdentity = retail,
                },
            ],
        };
        var request = new AnimationLibraryEditorRequest
        {
            Assignment = Assignment("generic_actor_target.anm2"),
            Libraries = [owner, dependency],
            RetailScripts =
            [
                new AnimationLibraryRetailScriptOption(
                    retail,
                    "Generic stock motion"),
            ],
            SelectedLibraryId = owner.Id,
        };
        var viewModel = new AnimationLibraryEditorViewModel(request);

        Assert.Equal(owner.Id, viewModel.SelectedLibrary?.Id);
        Assert.Equal(
            ["generic_shared_motion", "generic_stock_motion"],
            viewModel.SelectedLibrary!.Imports
                .Select(static row => row.ResourceName)
                .ToArray());
        Assert.Contains(
            "!include(\"generic_shared_motion.scr\")",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
        Assert.Contains(
            "!include(\"generic_stock_motion.scr\")",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
        Assert.True(
            viewModel.GeneratedSourcePreview.IndexOf(
                "generic_shared_motion.scr",
                StringComparison.Ordinal) <
            viewModel.GeneratedSourcePreview.IndexOf(
                "generic_stock_motion.scr",
                StringComparison.Ordinal));
        Assert.Contains(
            "SeqTrack( \"generic_motion\", \"generic_actor_target.anm2\"",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);

        viewModel.SelectedImport = viewModel.SelectedLibrary.Imports[1];
        viewModel.MoveSelectedImport(-1);

        Assert.Equal(
            ["generic_stock_motion", "generic_shared_motion"],
            viewModel.SelectedLibrary.Imports
                .Select(static row => row.ResourceName)
                .ToArray());
    }

    [Fact]
    public void ValidationRejectsMissingImportsAndIndirectCycles()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        ProjectAnimationLibrary missing = Library(
            firstId,
            "generic_missing_owner") with
        {
            Imports =
            [
                ProjectImport(secondId),
            ],
        };

        InvalidOperationException missingError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [missing]));
        Assert.Contains(
            "missing project library",
            missingError.Message,
            StringComparison.OrdinalIgnoreCase);

        ProjectAnimationLibrary first = missing;
        ProjectAnimationLibrary second = Library(
            secondId,
            "generic_cycle_dependency") with
        {
            Imports =
            [
                ProjectImport(firstId),
            ],
        };
        InvalidOperationException cycleError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [first, second]));
        Assert.Contains(
            "cycle",
            cycleError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationRejectsDuplicateResourceAndRetailIdentities()
    {
        ProjectAnimationLibrary first = Library(
            Guid.NewGuid(),
            "generic_duplicate");
        ProjectAnimationLibrary second = Library(
            Guid.NewGuid(),
            "GENERIC_DUPLICATE");

        InvalidOperationException resourceError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [first, second]));
        Assert.Contains(
            "type-322 resource name",
            resourceError.Message,
            StringComparison.OrdinalIgnoreCase);

        ProjectRetailAssetIdentity retail = RetailScript(
            "generic_retail_motion",
            'b');
        ProjectAnimationLibrary duplicateImport = Library(
            Guid.NewGuid(),
            "generic_import_owner") with
        {
            Imports =
            [
                RetailImport(retail),
                RetailImport(retail),
            ],
        };
        InvalidOperationException importError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [duplicateImport]));
        Assert.Contains(
            "same fingerprinted retail script",
            importError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingExtensionRequiresExactMatchingRetailIdentity()
    {
        ProjectRetailAssetIdentity baseScript = RetailScript(
            "generic_existing_motion",
            'c');
        ProjectAnimationLibrary extension = Library(
            Guid.NewGuid(),
            "different_resource_name") with
        {
            Mode = ProjectAnimationLibraryMode.ExistingScriptExtension,
            ExistingScriptIdentity = baseScript,
        };

        InvalidOperationException error = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [extension]));

        Assert.Contains(
            "keep the resource name",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FingerprintedType322ProjectAssetBecomesExtensionCandidate()
    {
        ProjectRetailAssetIdentity identity = RetailScript(
            "anims_man_all",
            'd');
        DlraProject project = CreateProject();
        project = project with
        {
            Assets = project.Assets.Add(new ProjectAssetReference
            {
                Kind = ProjectAssetKind.RetailGameResource,
                RelativePath = "retail/322/00000007",
                ResourceId = "rpack:322:anims_man_all",
                ContentSha256 = identity.ContentSha256,
                RetailIdentity = identity,
            }),
        };

        AnimationLibraryEditorRequest request =
            AnimationLibraryEditorRequest.FromProject(
                project,
                Assert.Single(project.AnimationVariants).Id);

        AnimationLibraryRetailScriptOption option =
            Assert.Single(request.RetailScripts);
        Assert.Equal("anims_man_all", option.Identity.ResourceName);
        Assert.Equal(322, option.Identity.ResourceType);
    }

    [Fact]
    public void DlcConventionAcceptsNumericSuffixAndWarnsBelowSixty()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "anims_man_all_dlc12");
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_output.anm2"),
                Libraries = [library],
                SelectedLibraryId = library.Id,
            });

        Assert.True(viewModel.TryCreateResult(out _));
        Assert.Contains(
            "below the conventional 60+ range",
            viewModel.SelectedLibrary!.DlcConventionMessage,
            StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedLibrary.ResourceName = "anims_man_all_dlc60";
        Assert.True(viewModel.TryCreateResult(out _));
        Assert.Contains(
            "dlc60",
            viewModel.SelectedLibrary.DlcConventionMessage,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DlcConventionRejectsMalformedSuffix()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "anims_man_all_dlc_preview");

        InvalidOperationException error = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [library]));

        Assert.Contains(
            "must end with '_dlc' followed by decimal digits",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignmentRejectsNamesTheOutputBuilderWouldSilentlyRewrite()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_exact_library");

        InvalidOperationException outputError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateAssignment(
                    [library],
                    library.Id,
                    "generic output.anm2"));
        Assert.Contains(
            "never silently renamed",
            outputError.Message,
            StringComparison.OrdinalIgnoreCase);

        InvalidOperationException scriptError = Assert.Throws<
            InvalidOperationException>(() =>
                AnimationLibraryAssignmentValidator.ValidateLibraries(
                    [library with
                    {
                        ResourceName = "generic script",
                    }]));
        Assert.Contains(
            "exact DL1 resource identity",
            scriptError.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void ResultAssignsOneOwningLibraryAndStableOutputWithoutChangingIds()
    {
        DlraProject project = CreateProject();
        ProjectAnimationVariant original = Assert.Single(
            project.AnimationVariants);
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_target_library");
        var result = new AnimationLibraryAssignmentResult(
            [library],
            library.Id,
            "generic_motion_target.anm2");

        DlraProject updated = result.ApplyTo(project, original.Id);

        ProjectAnimationVariant assigned = Assert.Single(
            updated.AnimationVariants);
        Assert.Equal(original.Id, assigned.Id);
        Assert.Equal(library.Id, assigned.OwningAnimationLibraryId);
        Assert.Equal(
            "generic_motion_target.anm2",
            assigned.OutputAnm2Name);
        Assert.Equal(
            project.AnimationSources,
            updated.AnimationSources);
        Assert.Equal(project.Models, updated.Models);
    }

    [Fact]
    public void EditorMakesReplaceBehaviorExplicitAndDoesNotInferIt()
    {
        ProjectAnimationLibrary library = Library(
            Guid.NewGuid(),
            "generic_replace_owner");
        var viewModel = new AnimationLibraryEditorViewModel(
            new AnimationLibraryEditorRequest
            {
                Assignment = Assignment("generic_output.anm2"),
                Libraries = [library],
                SelectedLibraryId = library.Id,
            });

        Assert.Equal(
            ProjectAnimationSequenceCollisionPolicy.Reject,
            viewModel.SelectedLibrary!.SelectedCollision.Value);
        Assert.Contains(
            "reject collisions",
            viewModel.GeneratedSourcePreview,
            StringComparison.OrdinalIgnoreCase);

        viewModel.SelectedLibrary.SelectedCollision = viewModel
            .CollisionOptions.Single(option => option.Value ==
                ProjectAnimationSequenceCollisionPolicy.ReplaceExisting);

        Assert.Contains(
            "Replace existing sequence explicitly",
            viewModel.GeneratedSourcePreview,
            StringComparison.Ordinal);
    }

    private static AnimationLibrarySequenceAssignment Assignment(
        string outputName) =>
        new(
            Guid.NewGuid(),
            "generic_motion",
            outputName,
            31,
            30);

    private static ProjectAnimationLibrary Library(
        Guid id,
        string resourceName) =>
        new()
        {
            Id = id,
            ResourceName = resourceName,
            DisplayName = resourceName.Replace('_', ' '),
        };

    private static ProjectAnimationLibraryImport ProjectImport(Guid id) =>
        new()
        {
            Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
            ProjectLibraryId = id,
        };

    private static ProjectAnimationLibraryImport RetailImport(
        ProjectRetailAssetIdentity identity) =>
        new()
        {
            Kind = ProjectAnimationLibraryImportKind.RetailScript,
            RetailScriptIdentity = identity,
        };

    private static ProjectRetailAssetIdentity RetailScript(
        string resourceName,
        char fingerprintCharacter) =>
        new()
        {
            InstallFingerprint = new string('1', 64),
            ProviderId = "generic-provider",
            ProviderPack = "data/generic-provider.rpack",
            ResourceType = 322,
            ResourceIndex = 7,
            ResourceName = resourceName,
            Precedence = 3,
            ContentSha256 = new string(fingerprintCharacter, 64),
        };

    private static DlraProject CreateProject()
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid modelAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        return DlraProject.Create("Generic SCR assignment") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-motion.fbx",
                    ContentSha256 = Sha('1'),
                },
                new ProjectAssetReference
                {
                    Id = modelAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/generic-target.dlrmodel",
                    ContentSha256 = Sha('2'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = modelAssetId,
                    Name = "Generic target",
                    RigSignature = Sha('3'),
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Generic motion",
                    SourceAssetId = sourceAssetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalFbx,
                        AssetId = sourceAssetId,
                        Roles = AnimationSourceRoles.Body,
                        SourceRigSignature = Sha('4'),
                        TimingProvenance =
                            AnimationTimingProvenance.EmbeddedFbx,
                    },
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 31,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = variantId,
                    SourceId = sourceId,
                    Name = "Generic target motion",
                    TargetModelId = modelId,
                    TargetRigId = "generic-target",
                    TargetRigSignature = Sha('3'),
                    BindingMode = ProjectAnimationBindingMode.Retarget,
                    BoneMappings =
                    [
                        new ProjectBoneMapping
                        {
                            SourceBoneName = "source_root",
                            TargetBoneName = "target_root",
                            Method = "manual",
                            ComponentPolicy =
                                RetargetComponentPolicy.FullTransform,
                            TransformComponents =
                                RetargetTransformComponents.All,
                        },
                    ],
                },
            ],
        };
    }

    private static string Sha(char value) => new(value, 64);
}

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectSchema3MigrationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Schema3-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void Schema2LoadAddsPresentationSelectionAndExactComponentFlags()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-schema2.dlraproj");
        DlraProject original = CreateProject(
            RetargetComponentPolicy.RotationTranslation,
            includeInPackage: true);
        ProjectSerializer.SaveAtomic(original, path);

        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["schemaVersion"] = 2;
        Assert.True(document.Remove("animationLibraries"));
        Assert.True(document.Remove("exportSelection"));
        JsonObject source = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animationSources"])[0]);
        Assert.True(source.Remove("presentation"));
        JsonObject variant = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animationVariants"])[0]);
        JsonObject mapping = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(variant["boneMappings"])[0]);
        Assert.True(mapping.Remove("transformComponents"));
        File.WriteAllText(path, document.ToJsonString());

        DlraProject migrated = ProjectSerializer.Load(path);

        Assert.Equal(DlraProject.CurrentSchemaVersion, migrated.SchemaVersion);
        ProjectAnimationSource migratedSource = Assert.Single(
            migrated.AnimationSources);
        ProjectAnimationSourcePresentation presentation = Assert.IsType<
            ProjectAnimationSourcePresentation>(migratedSource.Presentation);
        Assert.Equal(
            ProjectAnimationSourceOriginKind.ImportedFbxRig,
            presentation.OriginKind);
        Assert.Equal("generic-motion", presentation.OriginName);
        Assert.Equal(
            migratedSource.SourceAssetId,
            presentation.ProjectAssetId);

        ProjectAnimationVariant migratedVariant = Assert.Single(
            migrated.AnimationVariants);
        Assert.Equal(
            RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Translation,
            Assert.Single(migratedVariant.BoneMappings)
                .TransformComponents);
        Assert.Equal(
            migratedVariant.Id,
            Assert.Single(
                migrated.ExportSelection.AnimationVariantIds));
        Assert.Equal(
            migratedVariant.TargetModelId,
            Assert.Single(migrated.ExportSelection.ModelIds));

        ProjectSerializer.SaveAtomic(migrated, path);
        JsonObject saved = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        Assert.Equal(
            DlraProject.CurrentSchemaVersion,
            saved["schemaVersion"]!.GetValue<int>());
        Assert.NotNull(saved["animationLibraries"]);
        Assert.NotNull(saved["exportSelection"]);
    }

    [Theory]
    [InlineData(
        RetargetComponentPolicy.FullTransform,
        RetargetTransformComponents.All)]
    [InlineData(
        RetargetComponentPolicy.Rotation,
        RetargetTransformComponents.Rotation)]
    [InlineData(
        RetargetComponentPolicy.Translation,
        RetargetTransformComponents.Translation)]
    [InlineData(
        RetargetComponentPolicy.RotationTranslation,
        RetargetTransformComponents.Rotation |
            RetargetTransformComponents.Translation)]
    [InlineData(
        RetargetComponentPolicy.Scale,
        RetargetTransformComponents.Scale)]
    public void LegacyComponentPoliciesHaveExactSchema3Flags(
        RetargetComponentPolicy legacy,
        RetargetTransformComponents expected)
    {
        RetargetTransformComponents migrated =
            RetargetTransformComponentsCompatibility.FromLegacy(legacy);

        Assert.Equal(expected, migrated);
        Assert.True(
            RetargetTransformComponentsCompatibility.TryToLegacy(
                migrated,
                out RetargetComponentPolicy roundTrip));
        Assert.Equal(legacy, roundTrip);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void Schema3RoundTripPreservesAComponentCombinationWithoutLegacyEquivalent()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-component-flags.dlraproj");
        DlraProject project = CreateProject(
            RetargetComponentPolicy.FullTransform,
            includeInPackage: true);
        ProjectAnimationVariant variant = Assert.Single(
            project.AnimationVariants);
        ProjectBoneMapping mapping = Assert.Single(
            variant.BoneMappings);
        project = project with
        {
            AnimationVariants =
            [
                variant with
                {
                    BoneMappings =
                    [
                        mapping with
                        {
                            TransformComponents =
                                RetargetTransformComponents.Rotation |
                                RetargetTransformComponents.Scale,
                        },
                    ],
                },
            ],
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject loaded = ProjectSerializer.Load(path);
        ProjectBoneMapping loadedMapping = Assert.Single(
            Assert.Single(loaded.AnimationVariants).BoneMappings);

        Assert.Equal(
            RetargetTransformComponents.Rotation |
                RetargetTransformComponents.Scale,
            loadedMapping.TransformComponents);
        Assert.False(
            RetargetTransformComponentsCompatibility.TryToLegacy(
                loadedMapping.EffectiveTransformComponents,
                out _));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void Schema3RoundTripKeepsLibrariesOutputsAndIndependentSelection()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-libraries.dlraproj");
        DlraProject baseProject = CreateProject(
            RetargetComponentPolicy.Rotation,
            includeInPackage: true);
        ProjectModelEntry model = Assert.Single(baseProject.Models);
        ProjectAnimationVariant variant = Assert.Single(
            baseProject.AnimationVariants);
        Guid baseLibraryId = Guid.NewGuid();
        Guid extensionLibraryId = Guid.NewGuid();
        ProjectRetailAssetIdentity retailScript = RetailScript(
            "generic_existing_library",
            '8');
        ProjectAnimationLibrary[] libraries =
        [
            new ProjectAnimationLibrary
            {
                Id = baseLibraryId,
                ResourceName = "generic_shared_library",
                DisplayName = "Generic shared library",
            },
            new ProjectAnimationLibrary
            {
                Id = extensionLibraryId,
                ResourceName = "generic_existing_library",
                DisplayName = "Generic existing extension",
                Mode = ProjectAnimationLibraryMode.ExistingScriptExtension,
                ExistingScriptIdentity = retailScript,
                Imports =
                [
                    new ProjectAnimationLibraryImport
                    {
                        Kind =
                            ProjectAnimationLibraryImportKind.ProjectLibrary,
                        ProjectLibraryId = baseLibraryId,
                    },
                    new ProjectAnimationLibraryImport
                    {
                        Kind = ProjectAnimationLibraryImportKind.RetailScript,
                        RetailScriptIdentity = RetailScript(
                            "generic_dependency",
                            '9'),
                    },
                ],
                CollisionPolicy =
                    ProjectAnimationSequenceCollisionPolicy.ReplaceExisting,
            },
        ];
        DlraProject project = baseProject with
        {
            Models =
            [
                model with
                {
                    RootAnimationLibraryId = extensionLibraryId,
                    Dl1OutputRigSignature = Sha('6'),
                    Dl1DescriptorInventoryFingerprint = Sha('7'),
                },
            ],
            AnimationLibraries = libraries.ToImmutableArray(),
            AnimationVariants =
            [
                variant with
                {
                    OwningAnimationLibraryId = extensionLibraryId,
                    OutputAnm2Name = "generic_motion_target.anm2",
                },
            ],
            // IncludeInPackage is authored legacy state; this explicit empty
            // schema-3 selection must remain empty after normalization.
            ExportSelection = new ProjectExportSelection(),
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject reopened = ProjectSerializer.Load(path);

        Assert.Equal(2, reopened.AnimationLibraries.Length);
        ProjectAnimationLibrary extension = reopened.AnimationLibraries
            .Single(library => library.Id == extensionLibraryId);
        Assert.Equal(
            baseLibraryId,
            Assert.Single(extension.Imports
                .Where(import => import.ProjectLibraryId.HasValue))
                .ProjectLibraryId!.Value);
        Assert.Equal(
            ProjectAnimationSequenceCollisionPolicy.ReplaceExisting,
            extension.CollisionPolicy);
        Assert.Equal(
            extensionLibraryId,
            Assert.Single(reopened.Models).RootAnimationLibraryId);
        Assert.Equal(
            "generic_motion_target.anm2",
            Assert.Single(reopened.AnimationVariants).OutputAnm2Name);
        Assert.Empty(reopened.ExportSelection.ModelIds);
        Assert.Empty(reopened.ExportSelection.AnimationVariantIds);
    }

    [Fact]
    public void Schema2ExcludedVariantStaysOutsideMigratedExportSelection()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-excluded-schema2.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateProject(
                RetargetComponentPolicy.FullTransform,
                includeInPackage: false),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["schemaVersion"] = 2;
        Assert.True(document.Remove("animationLibraries"));
        Assert.True(document.Remove("exportSelection"));
        File.WriteAllText(path, document.ToJsonString());

        ProjectExportSelection selection = ProjectSerializer.Load(path)
            .ExportSelection;

        Assert.Empty(selection.ModelIds);
        Assert.Empty(selection.AnimationVariantIds);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void UnassignedVariantGetsStableDefaultLibraryAndOutputIdentity()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string firstPath = Path.Combine(
            _temporaryDirectory,
            "generic-default-output-first.dlraproj");
        string secondPath = Path.Combine(
            _temporaryDirectory,
            "generic-default-output-second.dlraproj");
        DlraProject project = CreateProject(
            RetargetComponentPolicy.FullTransform,
            includeInPackage: true);

        ProjectSerializer.SaveAtomic(project, firstPath);
        DlraProject first = ProjectSerializer.Load(firstPath);
        ProjectAnimationLibrary library = Assert.Single(
            first.AnimationLibraries);
        ProjectModelEntry model = Assert.Single(first.Models);
        ProjectAnimationVariant variant = Assert.Single(
            first.AnimationVariants);

        Assert.Equal(library.Id, model.RootAnimationLibraryId);
        Assert.Equal(library.Id, variant.OwningAnimationLibraryId);
        Assert.EndsWith(
            ".anm2",
            variant.OutputAnm2Name,
            StringComparison.OrdinalIgnoreCase);

        ProjectSerializer.SaveAtomic(first, secondPath);
        DlraProject second = ProjectSerializer.Load(secondPath);
        Assert.Equal(
            library.Id,
            Assert.Single(second.AnimationLibraries).Id);
        Assert.Equal(
            variant.OutputAnm2Name,
            Assert.Single(second.AnimationVariants).OutputAnm2Name);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void GeneratedLibraryAndOutputNamesUseExactDl1Identities()
    {
        DlraProject project = CreateProject(
            RetargetComponentPolicy.FullTransform,
            includeInPackage: true);
        ProjectModelEntry model = Assert.Single(project.Models);
        ProjectAnimationVariant variant = Assert.Single(
            project.AnimationVariants);
        project = project with
        {
            Models = [model with { Name = "Généric-target" }],
            AnimationVariants =
            [
                variant with
                {
                    Name = "Mötion-take",
                },
            ],
        };

        DlraProject normalized =
            ProjectAnimationOutputNormalizer.Normalize(project);

        ProjectAnimationLibrary library = Assert.Single(
            normalized.AnimationLibraries);
        string outputName = Assert.IsType<string>(
            Assert.Single(normalized.AnimationVariants).OutputAnm2Name);
        string outputStem = Assert.IsType<string>(
            Path.GetFileNameWithoutExtension(outputName));
        Assert.Matches("^[A-Za-z0-9_]+$", library.ResourceName);
        Assert.Matches("^[A-Za-z0-9_]+$", outputStem);
        Assert.DoesNotContain('-', library.ResourceName);
        Assert.DoesNotContain('-', outputStem);
        normalized.Validate();
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void SharedLibraryAllocatesUniqueOutputsAcrossTargetModels()
    {
        DlraProject project = CreateProject(
            RetargetComponentPolicy.FullTransform,
            includeInPackage: true);
        ProjectModelEntry firstModel = Assert.Single(project.Models);
        ProjectAnimationVariant firstVariant = Assert.Single(
            project.AnimationVariants);
        Guid libraryId = Guid.NewGuid();
        Guid secondModelAssetId = Guid.NewGuid();
        var secondModel = firstModel with
        {
            Id = Guid.NewGuid(),
            AssetId = secondModelAssetId,
            Name = "Second generic target",
        };
        var secondVariant = firstVariant with
        {
            Id = Guid.NewGuid(),
            Name = firstVariant.Name,
            TargetModelId = secondModel.Id,
        };
        project = project with
        {
            Assets = project.Assets.Add(
                new ProjectAssetReference
                {
                    Id = secondModelAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/second-generic-target.dlrmodel",
                    ContentSha256 = Sha('5'),
                }),
            Models =
            [
                firstModel with { RootAnimationLibraryId = libraryId },
                secondModel with { RootAnimationLibraryId = libraryId },
            ],
            AnimationLibraries =
            [
                new ProjectAnimationLibrary
                {
                    Id = libraryId,
                    ResourceName = "generic_shared_library",
                    DisplayName = "Generic shared library",
                },
            ],
            AnimationVariants =
            [
                firstVariant with
                {
                    OwningAnimationLibraryId = libraryId,
                    OutputAnm2Name = null,
                },
                secondVariant with
                {
                    OwningAnimationLibraryId = libraryId,
                    OutputAnm2Name = null,
                },
            ],
        };

        DlraProject normalized =
            ProjectAnimationOutputNormalizer.Normalize(project);

        string[] outputs = normalized.AnimationVariants
            .Select(static variant => variant.OutputAnm2Name!)
            .ToArray();
        Assert.Equal(2, outputs.Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        normalized.Validate();
    }

    [Fact]
    public void DuplicateOutputsInOneLibraryAreRejected()
    {
        DlraProject baseProject = ProjectAnimationOutputNormalizer.Normalize(
            CreateProject(
                RetargetComponentPolicy.FullTransform,
                includeInPackage: true));
        ProjectAnimationVariant first = Assert.Single(
            baseProject.AnimationVariants);
        ProjectAnimationVariant duplicate = first with
        {
            Id = Guid.NewGuid(),
            Name = "Second generic motion",
        };
        DlraProject invalid = baseProject with
        {
            AnimationVariants = [first, duplicate],
            ExportSelection = new ProjectExportSelection(),
        };

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            invalid.Validate);

        Assert.Contains(
            "duplicate target-specific ANM2",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Schema2EmbeddedSourceGetsOwningModelPresentationWithoutGuessingAlias()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-embedded-schema2.dlraproj");
        Guid packageAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Embedded source") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = packageAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/generic-package.dlrmodel",
                    ContentSha256 = Sha('c'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = packageAssetId,
                    Name = "Generic package model",
                    RigSignature = Sha('d'),
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Generic embedded take",
                    SourceAssetId = packageAssetId,
                    EmbeddedCustomModelStack =
                        new ProjectEmbeddedAnimationStackIdentity
                        {
                            ClipId = Guid.NewGuid(),
                            FbxObjectId = 42,
                            StackFingerprint = Sha('e'),
                            SourceRigSignature = Sha('d'),
                        },
                    FrameCount = 2,
                },
            ],
        };
        ProjectSerializer.SaveAtomic(project, path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["schemaVersion"] = 2;
        Assert.True(document.Remove("animationLibraries"));
        Assert.True(document.Remove("exportSelection"));
        Assert.True(Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animationSources"])[0])
            .Remove("presentation"));
        File.WriteAllText(path, document.ToJsonString());

        DlraProject migrated = ProjectSerializer.Load(path);

        ProjectAnimationSourcePresentation presentation = Assert.IsType<
            ProjectAnimationSourcePresentation>(
                Assert.Single(migrated.AnimationSources).Presentation);
        Assert.Equal(
            ProjectAnimationSourceOriginKind.OwningCustomModel,
            presentation.OriginKind);
        Assert.Equal(modelId, presentation.OwningModelId);
        Assert.Empty(migrated.AnimationLibraries);
        Assert.Null(Assert.Single(migrated.Models).RootAnimationLibraryId);
    }

    [Fact]
    public void Schema3RejectsProjectLibraryImportCycles()
    {
        DlraProject project = CreateProject(
            RetargetComponentPolicy.FullTransform,
            includeInPackage: false);
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        project = project with
        {
            AnimationLibraries =
            [
                LibraryImporting("generic_first", firstId, secondId),
                LibraryImporting("generic_second", secondId, firstId),
            ],
        };

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            project.Validate);

        Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void SkeletonFreeFacialFbxSourceKeepsOriginPresentation()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "generic-facial-source.dlraproj");
        Guid assetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Facial source") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = assetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-facial.fbx",
                    ContentSha256 = Sha('a'),
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Generic facial take",
                    SourceAssetId = assetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalFbx,
                        AssetId = assetId,
                        Roles = AnimationSourceRoles.Facial,
                        SourceRigSignature = string.Empty,
                        TimingProvenance =
                            AnimationTimingProvenance.EmbeddedFbx,
                    },
                    FacialSourceValueUnit =
                        ProjectMorphSourceValueUnit.Percent,
                    FrameCount = 2,
                },
            ],
        };

        ProjectSerializer.SaveAtomic(project, path);
        ProjectAnimationSource reopened = Assert.Single(
            ProjectSerializer.Load(path).AnimationSources);

        Assert.Equal(string.Empty, reopened.SourceRigSignature);
        ProjectAnimationSourcePresentation presentation = Assert.IsType<
            ProjectAnimationSourcePresentation>(reopened.Presentation);
        Assert.Equal(
            ProjectAnimationSourceOriginKind.ImportedFbxRig,
            presentation.OriginKind);
        Assert.Equal("generic-facial", presentation.OriginName);
        Assert.Null(presentation.SourceRigIdentity);
    }

    [Fact]
    public void BodyFbxSourceStillRequiresExactRigFingerprint()
    {
        Guid assetId = Guid.NewGuid();
        DlraProject project = DlraProject.Create("Invalid body source") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = assetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-body.fbx",
                    ContentSha256 = Sha('b'),
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Name = "Generic body take",
                    SourceAssetId = assetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalFbx,
                        AssetId = assetId,
                        Roles = AnimationSourceRoles.Body,
                        SourceRigSignature = string.Empty,
                    },
                    FrameCount = 2,
                },
            ],
        };

        ArgumentException error = Assert.Throws<ArgumentException>(
            project.Validate);

        Assert.Contains(
            "body animation source",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static DlraProject CreateProject(
        RetargetComponentPolicy componentPolicy,
        bool includeInPackage)
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid modelAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        return DlraProject.Create("Generic project") with
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
                    FrameCount = 2,
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
                            ComponentPolicy = componentPolicy,
                        },
                    ],
                    IncludeInPackage = includeInPackage,
                },
            ],
        };
    }

    private static ProjectAnimationLibrary LibraryImporting(
        string resourceName,
        Guid id,
        Guid dependencyId) =>
        new()
        {
            Id = id,
            ResourceName = resourceName,
            DisplayName = resourceName,
            Imports =
            [
                new ProjectAnimationLibraryImport
                {
                    Kind = ProjectAnimationLibraryImportKind.ProjectLibrary,
                    ProjectLibraryId = dependencyId,
                },
            ],
        };

    private static ProjectRetailAssetIdentity RetailScript(
        string resourceName,
        char fingerprint) =>
        new()
        {
            InstallFingerprint = $"generic-install-{fingerprint}",
            ProviderId = "generic-provider",
            ProviderPack = "packs/generic.rpack",
            ResourceType = 322,
            ResourceIndex = 1,
            ResourceName = resourceName,
            ContentSha256 = Sha(fingerprint),
        };

    private static string Sha(char value) => new(value, 64);

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

using System.Text.Json.Nodes;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Project;

namespace ReAnimated.Tests;

public sealed class ProjectSchema2MigrationTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"ReAnimated-Schema2-{Guid.NewGuid():N}");

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void OrdinaryImportedFbxSourceCreatesPlayableRuntimeAnimation()
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string sourceRig = Sha('1');
        string targetRig = Sha('2');
        var binding = new ProjectAnimationSourceBinding
        {
            Kind = AnimationSourceKind.LocalFbx,
            AssetId = sourceAssetId,
            Roles = AnimationSourceRoles.Body,
            SourceRigSignature = sourceRig,
            TimingProvenance = AnimationTimingProvenance.EmbeddedFbx,
            SourceRangeStartFrame = 0,
            SourceRangeEndFrame = 16,
            TimingDetail = "external-fbx-stack-v1|42|generic",
        };
        var source = new ProjectAnimationSource
        {
            Id = sourceId,
            Name = "Imported motion",
            SourceAssetId = sourceAssetId,
            SourceBinding = binding,
            FrameRate = new FrameRate(30, 1),
            FrameCount = 17,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = Guid.NewGuid(),
            SourceId = sourceId,
            Name = "Imported motion - target",
            TargetModelId = modelId,
            TargetRigId = "generic-target",
            TargetRigSignature = targetRig,
            BindingMode = ProjectAnimationBindingMode.Retarget,
        };
        DlraProject project = DlraProject.Create("Imported FBX") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/imported-motion.fbx",
                    ContentSha256 = Sha('3'),
                },
                new ProjectAssetReference
                {
                    Id = targetAssetId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "retail/target",
                    ContentSha256 = Sha('4'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = targetAssetId,
                    Name = "Generic target",
                    RigSignature = targetRig,
                },
            ],
        };

        ProjectAnimation runtime =
            MainWindowViewModel.CreateRuntimeAnimation(
                project,
                source,
                variant);

        Assert.True(
            MainWindowViewModel.HasRuntimeSourceIdentity(source));
        Assert.Equal(binding, runtime.SourceBinding);
        Assert.Equal(sourceAssetId, runtime.SourceAssetId);
        Assert.Equal(targetAssetId, runtime.TargetAssetId);
        Assert.Equal(variant.Id, runtime.Id);
        Assert.Equal(17, runtime.FrameCount);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void ResolvedRetargetMapFingerprintAndRowsStayInSchema2Variant()
    {
        Guid variantId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        ProjectBoneMapping previousRow = new()
        {
            SourceBoneName = "source_spine",
            TargetBoneName = "target_spine",
            Method = "Semantic",
            Confidence = 0.9,
            Evidence = "Explicit author review.",
            ReviewOrigin = ProjectMappingReviewOrigin.Explicit,
            ScorerVersion = "test-v1",
            EvidenceFingerprint = Sha('4'),
            IsReviewed = true,
            MappingKind = RetargetMappingKind.Bone,
            TransferPolicy = RetargetTransferPolicy.RotationDelta,
            ComponentPolicy = RetargetComponentPolicy.Rotation,
            TransformComponents = RetargetTransformComponents.Rotation,
        };
        ProjectBoneMapping resolvedRow = previousRow with
        {
            TransferPolicy = RetargetTransferPolicy.AnatomicalDirection,
        };
        var variant = new ProjectAnimationVariant
        {
            Id = variantId,
            SourceId = sourceId,
            Name = "Retargeted motion",
            TargetModelId = modelId,
            TargetRigId = "target",
            TargetRigSignature = Sha('1'),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            MappingFingerprint = Sha('2'),
            BoneMappings = [previousRow],
        };
        var resolved = new ProjectAnimation
        {
            Id = variantId,
            TargetRigId = "target",
            TargetRigSignature = Sha('1'),
            BindingMode = ProjectAnimationBindingMode.Retarget,
            MappingFingerprint = Sha('3'),
            BoneMappings = [resolvedRow],
            TargetBindReviews =
            [
                new ProjectTargetBindReview
                {
                    TargetBoneIndex = 4,
                    TargetBoneName = "target_helper",
                },
            ],
        };

        DlraProject updated =
            MainWindowViewModel.PersistResolvedAnimationBinding(
                DlraProject.Create("Retarget migration") with
                {
                    AnimationVariants = [variant],
                },
                resolved);

        ProjectAnimationVariant stored = Assert.Single(
            updated.AnimationVariants);
        Assert.Equal(resolved.MappingFingerprint, stored.MappingFingerprint);
        Assert.Equal(resolved.BoneMappings, stored.BoneMappings);
        Assert.Equal(resolved.TargetBindReviews, stored.TargetBindReviews);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void LocalAnm2MayBindToExactProjectOwnedCustomModelSkeleton()
    {
        Guid animationAssetId = Guid.NewGuid();
        Guid modelAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string rigSignature = Sha('a');
        var binding = new ProjectAnimationSourceBinding
        {
            Kind = AnimationSourceKind.LocalAnm2,
            AssetId = animationAssetId,
            Roles = AnimationSourceRoles.Body,
            SourceRigSignature = rigSignature,
            RetailSourceModelAssetId = modelAssetId,
            TimingProvenance =
                AnimationTimingProvenance.UserSpecified,
            Partition = new Anm2TrackPartition
            {
                BodyDescriptors = [0x12345678u],
                Fingerprint = Sha('b'),
            },
        };
        var project = DlraProject.Create("Custom ANM2 source") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = animationAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "assets/generic.anm2",
                    ContentSha256 = Sha('c'),
                },
                new ProjectAssetReference
                {
                    Id = modelAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "models/generic.dlrmodel",
                    ContentSha256 = Sha('d'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = modelAssetId,
                    Name = "Generic custom model",
                    RigSignature = rigSignature,
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Generic animation",
                    SourceAssetId = animationAssetId,
                    SourceBinding = binding,
                    FrameCount = 2,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    SourceId = sourceId,
                    Name = "Generic direct variant",
                    TargetModelId = modelId,
                    TargetRigId = "generic-rig",
                    TargetRigSignature = rigSignature,
                },
            ],
        };

        project.Validate();
    }

    [Fact]
    public void Schema2RoundTripKeepsEmbeddedStackOnCustomModelAsset()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "embedded-stack.dlraproj");
        Guid modelAssetId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid variantId = Guid.NewGuid();
        Guid clipId = Guid.NewGuid();
        const long stackObjectId = 4_294_967_401;
        DlraProject project = DlraProject.Create("Embedded custom stack") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = modelAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/portable-character.dlrmodel",
                    ContentSha256 = Sha('a'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = modelAssetId,
                    Name = "Portable character",
                    RigSignature = Sha('b'),
                    MorphSignature = Sha('c'),
                    PreviewCameraNodeName = "camera_preview",
                },
            ],
            AnimationSources =
            [
                new ProjectAnimationSource
                {
                    Id = sourceId,
                    Name = "Combined take",
                    SourceAssetId = modelAssetId,
                    EmbeddedCustomModelStack =
                        new ProjectEmbeddedAnimationStackIdentity
                        {
                            ClipId = clipId,
                            FbxObjectId = stackObjectId,
                            StackFingerprint = Sha('d'),
                            SourceRigSignature = Sha('b'),
                            Roles = AnimationSourceRoles.Body |
                                AnimationSourceRoles.Facial,
                            FacialSourceValueUnit =
                                ProjectMorphSourceValueUnit.Percent,
                        },
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 20,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    Id = variantId,
                    SourceId = sourceId,
                    Name = "Combined take - Portable character",
                    TargetModelId = modelId,
                    TargetRigId = "custom:portable-character",
                    TargetRigSignature = Sha('b'),
                },
            ],
            Workflow = new ProjectWorkflowState
            {
                ActiveTab = ProjectWorkflowTab.Playback,
                SelectedModelId = modelId,
                SelectedAnimationSourceId = sourceId,
                SelectedAnimationVariantId = variantId,
            },
            ActiveAnimationId = variantId,
        };

        ProjectSerializer.SaveAtomic(project, path);
        string json = File.ReadAllText(path);
        DlraProject reopened = ProjectSerializer.Load(path);

        Assert.Contains(
            $"\"schemaVersion\": {DlraProject.CurrentSchemaVersion}",
            json,
            StringComparison.Ordinal);
        Assert.Single(reopened.Assets);
        Assert.DoesNotContain(
            reopened.Assets,
            static asset => asset.Kind == ProjectAssetKind.SourceAnimation);
        ProjectAnimationSource source = Assert.Single(
            reopened.AnimationSources);
        ProjectEmbeddedAnimationStackIdentity stack = Assert.IsType<
            ProjectEmbeddedAnimationStackIdentity>(
                source.EmbeddedCustomModelStack);
        Assert.Equal(clipId, stack.ClipId);
        Assert.Equal(stackObjectId, stack.FbxObjectId);
        Assert.Empty(reopened.Animations);
        Assert.Equal(ProjectWorkflowTab.Playback, reopened.Workflow.ActiveTab);
        Assert.Equal(variantId, reopened.Workflow.SelectedAnimationVariantId);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "ProjectSchema")]
    public void Schema2RoundTripKeepsCompatibleDirectBindingEvidence()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "compatible-direct.dlraproj");
        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        string sourceSkeleton = Sha('d');
        string targetSkeleton = Sha('e');
        DirectBoneBinding[] rows =
        [
            new DirectBoneBinding
            {
                SourceBoneIndex = 0,
                TargetBoneIndex = 1,
                SourceBoneName = "root",
                TargetBoneName = "root",
                IdentityEvidence =
                    DirectBoneIdentityEvidence.UniqueDescriptor,
                ParentTopologyMatches = true,
                LocalBindMatches = true,
                GlobalBindMatches = true,
            },
        ];
        string evidence = DirectRigBindingFingerprint.Compute(
            sourceSkeleton,
            targetSkeleton,
            DirectRigBinding.PolicyVersion,
            rows);
        var direct = new DirectRigBinding(
            sourceSkeleton,
            targetSkeleton,
            evidence,
            DirectRigBinding.PolicyVersion,
            rows);
        DlraProject project = DlraProject.Create("Compatible direct") with
        {
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/generic-motion.fbx",
                    ContentSha256 = Sha('a'),
                },
                new ProjectAssetReference
                {
                    Id = targetAssetId,
                    Kind = ProjectAssetKind.CustomModelSource,
                    RelativePath = "Models/generic-target.dlrmodel",
                    ContentSha256 = Sha('b'),
                },
            ],
            Models =
            [
                new ProjectModelEntry
                {
                    Id = modelId,
                    AssetId = targetAssetId,
                    Name = "Generic target",
                    RigSignature = Sha('c'),
                    AnimationSkeletonSignature = targetSkeleton,
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
                        SourceRigSignature = Sha('f'),
                        Roles = AnimationSourceRoles.Body,
                        TimingProvenance =
                            AnimationTimingProvenance.EmbeddedFbx,
                    },
                    SourceAnimationSkeletonSignature = sourceSkeleton,
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                },
            ],
            AnimationVariants =
            [
                new ProjectAnimationVariant
                {
                    SourceId = sourceId,
                    Name = "Generic target variant",
                    TargetModelId = modelId,
                    TargetRigId = "generic-target",
                    TargetRigSignature = Sha('c'),
                    TargetAnimationSkeletonSignature = targetSkeleton,
                    BindingMode =
                        ProjectAnimationBindingMode.CompatibleDirect,
                    DirectBinding = direct,
                    BindingEvidenceFingerprint = evidence,
                    BindingPolicyVersion = direct.Policy,
                },
            ],
        };

        ProjectSerializer.SaveAtomic(project, path);
        DlraProject reopened = ProjectSerializer.Load(path);

        ProjectAnimationVariant variant = Assert.Single(
            reopened.AnimationVariants);
        Assert.Equal(
            ProjectAnimationBindingMode.CompatibleDirect,
            variant.BindingMode);
        DirectRigBinding loaded = Assert.IsType<DirectRigBinding>(
            variant.DirectBinding);
        Assert.Equal(evidence, loaded.EvidenceFingerprint);
        Assert.Equal(0, Assert.Single(loaded.Rows).SourceBoneIndex);
        Assert.Equal(1, loaded.Rows[0].TargetBoneIndex);
        Assert.Null(variant.MappingFingerprint);
        reopened.Validate();
    }

    [Fact]
    public void Schema1LoadMigratesLibrariesAndFailsClosedMappingProvenance()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "legacy-schema1.dlraproj");
        Guid groupId = Guid.NewGuid();
        DlraProject legacy = CreateLegacyProject(groupId);
        ProjectSerializer.SaveAtomic(legacy, path);

        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["schemaVersion"] = 1;
        Assert.True(document.Remove("models"));
        Assert.True(document.Remove("animationSources"));
        Assert.True(document.Remove("animationVariants"));
        Assert.True(document.Remove("workflow"));
        JsonObject animation = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animations"])[0]);
        JsonObject reviewedBone = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(animation["boneMappings"])[0]);
        RemoveProvenance(reviewedBone, removeConfidence: true);
        JsonObject unreviewedMorph = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(animation["morphBindings"])[0]);
        RemoveProvenance(unreviewedMorph, removeConfidence: false);
        File.WriteAllText(path, document.ToJsonString());

        DlraProject migrated = ProjectSerializer.Load(path);

        Assert.Equal(DlraProject.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.Single(migrated.Models);
        ProjectAnimationSource source = Assert.Single(
            migrated.AnimationSources);
        Assert.Equal(groupId, source.Id);
        Assert.Equal(groupId, source.LegacyVariantGroupId);
        ProjectAnimationVariant variant = Assert.Single(
            migrated.AnimationVariants);
        Assert.Equal(source.Id, variant.SourceId);
        ProjectBoneMapping bone = Assert.Single(variant.BoneMappings);
        Assert.Equal(ProjectMappingReviewOrigin.Explicit, bone.ReviewOrigin);
        Assert.True(bone.IsReviewed);
        Assert.True(bone.IsLocked);
        Assert.Equal(0.0, bone.Confidence);
        Assert.Contains("explicit", bone.Evidence, StringComparison.OrdinalIgnoreCase);
        ProjectMorphBinding morph = Assert.Single(variant.MorphBindings);
        Assert.Equal(ProjectMappingReviewOrigin.None, morph.ReviewOrigin);
        Assert.False(morph.IsReviewed);
        Assert.False(morph.IsLocked);
        Assert.True(morph.Confidence < 0.90);
        Assert.Contains("review", morph.Evidence, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{64}$", bone.EvidenceFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", morph.EvidenceFingerprint);

        // Loading migration is in-memory only. The schema-1 source remains
        // untouched until the user explicitly saves it.
        Assert.Equal(1, Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)))["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void Schema1ConflictingVariantGroupSplitsSourcesWithoutGuessing()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "conflicting-group.dlraproj");
        Guid groupId = Guid.NewGuid();
        DlraProject first = CreateLegacyProject(groupId);
        ProjectAssetReference secondSourceAsset = new()
        {
            Kind = ProjectAssetKind.SourceAnimation,
            RelativePath = "Sources/alternate.fbx",
            ContentSha256 = Sha('e'),
        };
        ProjectAnimation secondAnimation = first.Animations[0] with
        {
            Id = Guid.NewGuid(),
            Name = "Alternate source",
            SourceAssetId = secondSourceAsset.Id,
            SourceBinding = first.Animations[0].SourceBinding! with
            {
                AssetId = secondSourceAsset.Id,
                SourceRigSignature = Sha('f'),
            },
            SourceRigSignature = Sha('f'),
        };
        DlraProject conflicting = first with
        {
            Assets = first.Assets.Add(secondSourceAsset),
            Animations = first.Animations.Add(secondAnimation),
        };

        ProjectSerializer.SaveAtomic(conflicting, path);
        DlraProject migrated = ProjectSerializer.Load(path);

        Assert.Equal(2, migrated.AnimationSources.Length);
        Assert.Equal(2, migrated.AnimationVariants.Length);
        Assert.All(
            migrated.AnimationSources,
            source =>
            {
                Assert.Equal(groupId, source.LegacyVariantGroupId);
                Assert.NotNull(source.MigrationNote);
                Assert.Contains(
                    "conflicting",
                    source.MigrationNote!,
                    StringComparison.OrdinalIgnoreCase);
            });
        Assert.Equal(
            2,
            migrated.AnimationVariants
                .Select(static variant => variant.SourceId)
                .Distinct()
            .Count());
    }

    [Fact]
    public void Schema1TargetlessAnimationMigratesToSourceOnlyState()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "targetless-schema1.dlraproj");
        DlraProject legacy = CreateLegacyProject(Guid.NewGuid());
        ProjectSerializer.SaveAtomic(legacy, path);
        JsonObject document = DowngradeSavedProjectToSchema1(path);
        JsonObject animation = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animations"])[0]);
        Assert.True(animation.Remove("targetAssetId"));
        File.WriteAllText(path, document.ToJsonString());

        DlraProject migrated = ProjectSerializer.Load(path);

        ProjectAnimationSource source = Assert.Single(
            migrated.AnimationSources);
        Assert.Empty(migrated.AnimationVariants);
        Assert.Empty(migrated.Animations);
        Assert.Null(migrated.ActiveAnimationId);
        Assert.Equal(
            ProjectWorkflowTab.Animations,
            migrated.Workflow.ActiveTab);
        Assert.Equal(
            source.Id,
            migrated.Workflow.SelectedAnimationSourceId);
        Assert.Null(migrated.Workflow.SelectedAnimationVariantId);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("target")]
    public void Schema1UnknownAnimationAssetReferenceFailsAsProjectFormatError(
        string reference)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            $"unknown-{reference}-schema1.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateLegacyProject(Guid.NewGuid()),
            path);
        JsonObject document = DowngradeSavedProjectToSchema1(path);
        JsonObject animation = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animations"])[0]);
        string missingId = Guid.NewGuid().ToString();
        if (reference == "source")
        {
            animation["sourceAssetId"] = missingId;
            Assert.IsType<JsonObject>(animation["sourceBinding"])["assetId"] =
                missingId;
        }
        else
        {
            animation["targetAssetId"] = missingId;
        }

        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains(
            "unknown project asset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<KeyNotFoundException>(error.InnerException);
    }

    [Theory]
    [InlineData("sourceId")]
    [InlineData("targetModelId")]
    public void Schema2UnknownVariantReferenceFailsAsProjectFormatError(
        string propertyName)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            $"unknown-{propertyName}-schema2.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateLegacyProject(Guid.NewGuid()),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        JsonObject variant = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animationVariants"])[0]);
        variant[propertyName] = Guid.NewGuid().ToString();
        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains(
            propertyName == "sourceId" ? "unknown source" : "unknown target model",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<KeyNotFoundException>(error.InnerException);
    }

    [Fact]
    public void Schema2UnknownModelAssetFailsAsProjectFormatError()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "unknown-model-asset-schema2.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateLegacyProject(Guid.NewGuid()),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        JsonObject model = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["models"])[0]);
        model["assetId"] = Guid.NewGuid().ToString();
        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains(
            "unknown project asset",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsNotType<KeyNotFoundException>(error.InnerException);
    }

    [Fact]
    public void Schema2RiggedModelWithoutSignatureIsRejected()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "missing-model-rig-signature.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateLegacyProject(Guid.NewGuid()),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        JsonObject model = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["models"])[0]);
        Assert.False(model["isStatic"]!.GetValue<bool>());
        Assert.True(model.Remove("rigSignature"));
        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains(
            "invalid values",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.IsType<ArgumentException>(error.InnerException);
    }

    [Theory]
    [InlineData("evidence", true)]
    [InlineData("scorerVersion", true)]
    [InlineData("evidenceFingerprint", true)]
    [InlineData("evidence", false)]
    [InlineData("scorerVersion", false)]
    [InlineData("evidenceFingerprint", false)]
    public void InvalidSchema2NullMappingProvenanceIsReportedAsProjectFormatError(
        string propertyName,
        bool boneMapping)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        string path = Path.Combine(
            _temporaryDirectory,
            "null-provenance.dlraproj");
        ProjectSerializer.SaveAtomic(
            CreateLegacyProject(Guid.NewGuid()),
            path);
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        JsonObject variant = Assert.IsType<JsonObject>(
            Assert.IsType<JsonArray>(document["animationVariants"])[0]);
        JsonObject mapping = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(
            variant[boneMapping ? "boneMappings" : "morphBindings"])[0]);
        mapping[propertyName] = null;
        File.WriteAllText(path, document.ToJsonString());

        ProjectFormatException error = Assert.Throws<ProjectFormatException>(
            () => ProjectSerializer.Load(path));

        Assert.Contains("invalid", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DlraProject CreateLegacyProject(Guid groupId)
    {
        Guid sourceAssetId = Guid.NewGuid();
        Guid targetAssetId = Guid.NewGuid();
        return DlraProject.Create("Legacy schema one") with
        {
            SchemaVersion = 1,
            Assets =
            [
                new ProjectAssetReference
                {
                    Id = sourceAssetId,
                    Kind = ProjectAssetKind.SourceAnimation,
                    RelativePath = "Sources/source.fbx",
                    ContentSha256 = Sha('1'),
                },
                new ProjectAssetReference
                {
                    Id = targetAssetId,
                    Kind = ProjectAssetKind.RetailGameResource,
                    RelativePath = "Data0.pak::models/target.msh",
                    ContentSha256 = Sha('2'),
                },
            ],
            Animations =
            [
                new ProjectAnimation
                {
                    VariantGroupId = groupId,
                    Name = "Source clip",
                    SourceAssetId = sourceAssetId,
                    SourceBinding = new ProjectAnimationSourceBinding
                    {
                        Kind = AnimationSourceKind.LocalFbx,
                        AssetId = sourceAssetId,
                        Roles = AnimationSourceRoles.Body |
                            AnimationSourceRoles.Facial,
                        SourceRigSignature = Sha('3'),
                        TimingProvenance =
                            AnimationTimingProvenance.EmbeddedFbx,
                    },
                    SourceRigSignature = Sha('3'),
                    TargetAssetId = targetAssetId,
                    TargetRigId = "retail:test-target",
                    TargetRigSignature = Sha('4'),
                    FrameRate = new FrameRate(30, 1),
                    FrameCount = 2,
                    BoneMappings =
                    [
                        new ProjectBoneMapping
                        {
                            SourceBoneName = "Root",
                            TargetBoneName = "bip01",
                            Method = "Manual",
                            IsReviewed = true,
                            IsLocked = true,
                        },
                    ],
                    MorphBindings =
                    [
                        new ProjectMorphBinding
                        {
                            SourceChannel = "Smile",
                            TargetMorph = "smile",
                            Method = "semantic_alias",
                            Confidence = 1.0,
                        },
                    ],
                },
            ],
        };
    }

    private static void RemoveProvenance(
        JsonObject row,
        bool removeConfidence)
    {
        if (removeConfidence)
        {
            row.Remove("confidence");
        }

        row.Remove("evidence");
        row.Remove("reviewOrigin");
        row.Remove("scorerVersion");
        row.Remove("evidenceFingerprint");
    }

    private static JsonObject DowngradeSavedProjectToSchema1(string path)
    {
        JsonObject document = Assert.IsType<JsonObject>(
            JsonNode.Parse(File.ReadAllText(path)));
        document["schemaVersion"] = 1;
        Assert.True(document.Remove("models"));
        Assert.True(document.Remove("animationSources"));
        Assert.True(document.Remove("animationVariants"));
        Assert.True(document.Remove("workflow"));
        return document;
    }

    private static string Sha(char value) => new(value, 64);

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}

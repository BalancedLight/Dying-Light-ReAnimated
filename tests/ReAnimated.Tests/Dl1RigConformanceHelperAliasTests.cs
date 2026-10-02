using System.Collections.Immutable;
using ReAnimated.App.Infrastructure;
using ReAnimated.App.ViewModels;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Meshes;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class Dl1RigConformanceHelperAliasTests
{
    private const string HelperName = "synthetic_unweighted_native_helper";
    private const string NestedHelperName = "synthetic_unweighted_nested_helper";

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public async Task PreviouslyGeneratedUnweightedNativeHelperRefitsWithoutLosingItsIdentity()
    {
        FbxModelAuthoringImportResult original = CreateGeneratedHelperModel(
            includeSkinInfluence: false);
        int helperIndex = original.Rig!.BoneCount - 1;
        RiggingSession originalSession =
            original.Package.Document.RiggingSession!;
        Guid helperId = originalSession.Recipe.Entities[helperIndex].EntityId;
        Guid parentId = originalSession.Recipe.Entities[
            original.Package.Document.Bones[helperIndex].ParentIndex].EntityId;
        CustomModelDocument sourceDocument = original.Package.Document;
        RigDefinition sourceRig = original.Rig!;
        ImmutableArray<RigEntityBinding> sourceEntities =
            originalSession.Recipe.Entities;
        ImmutableArray<RigRoleAssignment> sourceAssignments =
            originalSession.Recipe.Assignments;
        ImmutableArray<RigParentDecision> sourceParentDecisions =
            originalSession.ParentDecisions;
        ImmutableArray<FbxModelSurface> sourceSurfaces = original.Surfaces;
        Dl1RigTemplateResolution resolution =
            CreateResolutionWithHelper(HelperName);

        (RigConformanceResult fit, CustomModelRigConformance? settings) =
            await PrepareAliasedFitAsync(original, helperIndex, resolution);
        Assert.IsType<CustomModelRigConformance>(settings);
        RigConformedBone canonical = Assert.Single(
            fit.Bones,
            row => row.Name == HelperName &&
                row.TemplateIndex >= 0 &&
                row.SourceBoneIndex < 0);
        RigConformedBone retainedHelper = Assert.Single(
            fit.Bones,
            row => row.Name == HelperName &&
                row.TemplateIndex < 0 &&
                row.SourceBoneIndex == helperIndex);
        Assert.True(retainedHelper.IsDeform);
        Assert.Equal(BoneKind.Deform, retainedHelper.Kind);
        Assert.Equal(canonical.ParentIndex, retainedHelper.ParentIndex);
        Assert.True(original.Package.Document.Bones[helperIndex].IsWeighted);
        Assert.False(HasSourceSkinInfluence(original.Surfaces, helperIndex));

        Dl1RigConformanceApplyResult first =
            Dl1RigConformanceApplier.ApplyDetailed(original, fit, settings);
        Assert.Same(sourceDocument, original.Package.Document);
        Assert.Same(sourceRig, original.Rig);
        Assert.Equal(sourceEntities, originalSession.Recipe.Entities);
        Assert.Equal(sourceAssignments, originalSession.Recipe.Assignments);
        Assert.Equal(sourceParentDecisions, originalSession.ParentDecisions);
        Assert.Equal(sourceSurfaces, original.Surfaces);
        Assert.Single(
            first.Model.Package.Document.CreateEffectiveBones(),
            bone => bone.Name == HelperName);
        int firstHelperIndex = Assert.Single(
            first.Model.Package.Document.Bones,
            bone => bone.Name == HelperName).Index;
        Assert.False(HasSourceSkinInfluence(first.Model.Surfaces, firstHelperIndex));
        RiggingSession firstSession =
            first.Model.Package.Document.RiggingSession!;
        firstSession.Validate();
        RigEntityBinding firstHelperEntity = Assert.Single(
            firstSession.Recipe.Entities,
            entity => entity.EntityId == helperId);
        Assert.Equal("source-name:" + HelperName, firstHelperEntity.SourceEntityId);
        Assert.Contains(
            firstSession.Recipe.Assignments,
            assignment => assignment.EntityId == helperId);
        Assert.Equal(
            parentId,
            Assert.Single(firstSession.ParentDecisions,
                decision => decision.EntityId == helperId).ParentEntityId);

        (RigConformanceResult refit, CustomModelRigConformance? refitSettings) =
            await PrepareAliasedFitAsync(
                first.Model,
                Assert.Single(
                    first.Model.Package.Document.Bones,
                    bone => bone.Name == HelperName).Index,
                resolution);
        Dl1RigConformanceApplyResult second =
            Dl1RigConformanceApplier.ApplyDetailed(
                first.Model,
                refit,
                refitSettings);

        Assert.Same(sourceDocument, original.Package.Document);
        Assert.Same(sourceRig, original.Rig);
        Assert.Equal(sourceEntities, originalSession.Recipe.Entities);
        Assert.Equal(sourceAssignments, originalSession.Recipe.Assignments);
        Assert.Equal(sourceParentDecisions, originalSession.ParentDecisions);
        Assert.Equal(sourceSurfaces, original.Surfaces);

        Assert.Single(
            second.Model.Package.Document.CreateEffectiveBones(),
            bone => bone.Name == HelperName);
        RiggingSession secondSession =
            second.Model.Package.Document.RiggingSession!;
        secondSession.Validate();
        Assert.Single(
            secondSession.Recipe.Entities,
            entity => entity.EntityId == helperId);
        Assert.Contains(
            secondSession.Recipe.Assignments,
            assignment => assignment.EntityId == helperId);
        Assert.Equal(
            first.Model.Surfaces[0].PaletteBoneIndices.ToArray(),
            second.Model.Surfaces[0].PaletteBoneIndices.ToArray());
        Assert.Equal(
            first.Model.Surfaces[0].Vertices[0].BoneIndices.ToArray(),
            second.Model.Surfaces[0].Vertices[0].BoneIndices.ToArray());
        Assert.Equal(
            first.Model.Surfaces[0].Vertices[0].BoneWeights.ToArray(),
            second.Model.Surfaces[0].Vertices[0].BoneWeights.ToArray());
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public async Task WeightedDeformWithSameNameIsNotFoldedIntoUnweightedHelperRole()
    {
        FbxModelAuthoringImportResult source = CreateGeneratedHelperModel(
            includeSkinInfluence: true);
        int helperIndex = source.Rig!.BoneCount - 1;
        Dl1RigTemplateResolution resolution =
            CreateResolutionWithHelper(HelperName);
        (RigConformanceResult fit, CustomModelRigConformance? settings) =
            await PrepareAliasedFitAsync(source, helperIndex, resolution);
        Assert.IsType<CustomModelRigConformance>(settings);

        Assert.True(HasSourceSkinInfluence(source.Surfaces, helperIndex));
        Assert.Contains(
            fit.Bones,
            row => row.Name == HelperName && row.TemplateIndex >= 0 && row.SourceBoneIndex < 0);
        RigConformedBone weightedExtra = Assert.Single(
            fit.Bones,
            row => row.Name == HelperName && row.TemplateIndex < 0 && row.SourceBoneIndex == helperIndex);
        Assert.True(weightedExtra.IsDeform);
        Assert.Equal(BoneKind.Deform, weightedExtra.Kind);

        CustomModelDocument originalDocument = source.Package.Document;
        ImmutableArray<int> originalPalette = source.Surfaces[0].PaletteBoneIndices;
        ImmutableArray<double> originalWeights = source.Surfaces[0].Vertices[0].BoneWeights;
        Exception? failure = Record.Exception(() =>
            Dl1RigConformanceApplier.ApplyDetailed(source, fit, settings));

        Assert.IsType<InvalidDataException>(failure);
        Assert.Same(originalDocument, source.Package.Document);
        Assert.Equal(originalPalette, source.Surfaces[0].PaletteBoneIndices);
        Assert.Equal(originalWeights, source.Surfaces[0].Vertices[0].BoneWeights);
        Assert.True(HasSourceSkinInfluence(source.Surfaces, helperIndex));
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public async Task GeneratedUnweightedHelperChainMergesThroughRetainedParentAlias()
    {
        FbxModelAuthoringImportResult source = CreateGeneratedHelperModel(
            includeSkinInfluence: false,
            includeNestedHelper: true);
        int helperIndex = Assert.Single(
            source.Package.Document.Bones,
            bone => bone.Name == HelperName).Index;
        int nestedHelperIndex = Assert.Single(
            source.Package.Document.Bones,
            bone => bone.Name == NestedHelperName).Index;
        RiggingSession sourceSession =
            source.Package.Document.RiggingSession!;
        Guid helperId = sourceSession.Recipe.Entities[helperIndex].EntityId;
        Guid nestedHelperId = sourceSession.Recipe.Entities[nestedHelperIndex].EntityId;
        Dl1RigTemplateResolution resolution = CreateResolutionWithHelpers(
            HelperName,
            NestedHelperName);

        (RigConformanceResult fit, CustomModelRigConformance? settings) =
            await PrepareAliasedFitAsync(
                source,
                helperIndex,
                resolution);
        Assert.IsType<CustomModelRigConformance>(settings);
        RigConformedBone canonicalParent = Assert.Single(
            fit.Bones,
            row => row.Name == HelperName &&
                row.TemplateIndex >= 0 &&
                row.SourceBoneIndex < 0);
        RigConformedBone sourceParent = Assert.Single(
            fit.Bones,
            row => row.Name == HelperName &&
                row.TemplateIndex < 0 &&
                row.SourceBoneIndex == helperIndex);
        RigConformedBone canonicalChild = Assert.Single(
            fit.Bones,
            row => row.Name == NestedHelperName &&
                row.TemplateIndex >= 0 &&
                row.SourceBoneIndex < 0);
        RigConformedBone sourceChild = Assert.Single(
            fit.Bones,
            row => row.Name == NestedHelperName &&
                row.TemplateIndex < 0 &&
                row.SourceBoneIndex == nestedHelperIndex);
        Assert.True(sourceParent.IsDeform);
        Assert.Equal(BoneKind.Deform, sourceParent.Kind);
        Assert.True(sourceChild.IsDeform);
        Assert.Equal(BoneKind.Deform, sourceChild.Kind);
        Assert.NotEqual(canonicalChild.ParentIndex, sourceChild.ParentIndex);
        Assert.Equal(canonicalParent.Index, canonicalChild.ParentIndex);
        Assert.Equal(sourceParent.Index, sourceChild.ParentIndex);
        Assert.False(HasSourceSkinInfluence(source.Surfaces, helperIndex));
        Assert.False(HasSourceSkinInfluence(source.Surfaces, nestedHelperIndex));

        Dl1RigConformanceApplyResult applied =
            Dl1RigConformanceApplier.ApplyDetailed(source, fit, settings);

        Assert.Single(
            applied.Model.Package.Document.CreateEffectiveBones(),
            bone => bone.Name == HelperName);
        Assert.Single(
            applied.Model.Package.Document.CreateEffectiveBones(),
            bone => bone.Name == NestedHelperName);
        RiggingSession appliedSession =
            applied.Model.Package.Document.RiggingSession!;
        appliedSession.Validate();
        Assert.Single(
            appliedSession.Recipe.Entities,
            entity => entity.EntityId == helperId);
        Assert.Single(
            appliedSession.Recipe.Entities,
            entity => entity.EntityId == nestedHelperId);
        Assert.Equal(
            helperId,
            Assert.Single(
                appliedSession.ParentDecisions,
                decision => decision.EntityId == nestedHelperId).ParentEntityId);
    }

    private static FbxModelAuthoringImportResult CreateGeneratedHelperModel(
        bool includeSkinInfluence,
        bool includeNestedHelper = false)
    {
        FbxModelAuthoringImportResult model =
            RigConformanceWizardTests.CreateModel();
        RigDefinition sourceRig = Assert.IsType<RigDefinition>(model.Rig);
        CustomModelDocument document = model.Package.Document;
        int parentIndex = sourceRig.Bones.Single(
            static bone => bone.Name == "CC_Base_Head").Index;
        int helperIndex = sourceRig.BoneCount;
        var helperBind = new TransformTRS(
            new Vector3D(0.0, 0.05, 0.10),
            QuaternionD.Identity,
            Vector3D.One);
        var helperSpecs = new List<(string Name, int ParentIndex, TransformTRS Bind)>
        {
            (HelperName, parentIndex, helperBind),
        };
        if (includeNestedHelper)
        {
            helperSpecs.Add((
                NestedHelperName,
                helperIndex,
                new TransformTRS(
                    new Vector3D(0.0, 0.01, 0.02),
                    QuaternionD.Identity,
                    Vector3D.One)));
        }

        ImmutableArray<BoneDefinition> addedDefinitions = helperSpecs
            .Select((spec, offset) => new BoneDefinition(
                helperIndex + offset,
                spec.Name,
                spec.ParentIndex,
                spec.Bind,
                BoneKind.Deform))
            .ToImmutableArray();
        sourceRig = new RigDefinition(
            sourceRig.Id,
            sourceRig.DisplayName,
            sourceRig.Bones.AddRange(addedDefinitions));

        ImmutableArray<CustomModelBone> addedDocumentBones = helperSpecs
            .Select((spec, offset) => new CustomModelBone
            {
                Index = helperIndex + offset,
                FbxObjectId = 0,
                Name = spec.Name,
                ParentIndex = spec.ParentIndex,
                LocalBindTransform = spec.Bind,
                ExactLocalBindMatrix = spec.Bind.ToMatrix(),
                Kind = BoneKind.Deform,
                // In this fixture the stored deform flag remains set even when
                // no mesh palette actually contains a positive helper weight.
                IsWeighted = true,
            })
            .ToImmutableArray();
        ImmutableArray<CustomModelBone> sourceDocumentBones =
            document.Bones.AddRange(addedDocumentBones);
        document = document with
        {
            Bones = sourceDocumentBones,
            RigSignature = CustomModelContractSignatures.ComputeRig(
                sourceDocumentBones),
        };

        RiggingSession session = RiggingSessions.Create(
            document,
            RigStudioEntryPath.RepairExistingRig);
        var entities = session.Recipe.Entities.ToBuilder();
        var assignments = ImmutableArray.CreateBuilder<RigRoleAssignment>();
        var parentDecisions = ImmutableArray.CreateBuilder<RigParentDecision>();
        for (int offset = 0; offset < helperSpecs.Count; offset++)
        {
            int entityIndex = helperIndex + offset;
            RigEntityBinding helperEntity = entities[entityIndex] with
            {
                Kind = RigNativeEntityKind.Bone,
                Imported = false,
            };
            entities[entityIndex] = helperEntity;
            string role = offset == 0
                ? "synthetic.generated-helper"
                : $"synthetic.generated-helper-{offset}";
            assignments.Add(new RigRoleAssignment(role, helperEntity.EntityId));
            parentDecisions.Add(new RigParentDecision(
                helperEntity.EntityId,
                entities[helperSpecs[offset].ParentIndex].EntityId,
                session.SourceSha256,
                userApproved: false));
        }
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = entities.ToImmutable(),
                Assignments = assignments.ToImmutable(),
            },
            ParentDecisions = parentDecisions.ToImmutable(),
        };
        session.Validate();
        document = document with { RiggingSession = session };

        ImmutableArray<FbxModelSurface> surfaces = model.Surfaces;
        if (includeSkinInfluence)
        {
            FbxModelSurface surface = surfaces[0];
            int newPaletteSlot = surface.PaletteBoneIndices.Length;
            const double helperWeight = 0.05;
            ImmutableArray<FbxModelVertex> vertices = surface.Vertices
                .Select(vertex =>
                {
                    double existingWeight = vertex.BoneWeights.Sum();
                    ImmutableArray<double> updatedWeights = existingWeight > 0.0
                        ? vertex.BoneWeights
                            .Select(weight => weight * (1.0 - helperWeight) / existingWeight)
                            .Append(helperWeight)
                            .ToImmutableArray()
                        : vertex.BoneWeights.Add(1.0);
                    return vertex with
                    {
                        BoneIndices = vertex.BoneIndices.Add(newPaletteSlot),
                        BoneWeights = updatedWeights,
                    };
                })
                .ToImmutableArray();
            surfaces = surfaces.SetItem(
                0,
                surface with
                {
                    PaletteBoneIndices = surface.PaletteBoneIndices.Add(helperIndex),
                    InverseBindMatrices = surface.InverseBindMatrices.Add(
                        TransformMatrix.Identity),
                    Vertices = vertices,
                });
        }

        return model with
        {
            Package = model.Package with { Document = document },
            Rig = sourceRig,
            Surfaces = surfaces,
        };
    }

    private static Dl1RigTemplateResolution CreateResolutionWithHelper(
        string helperName) => CreateResolutionWithHelpers(helperName);

    private static Dl1RigTemplateResolution CreateResolutionWithHelpers(
        params string[] helperNames)
    {
        Dl1RigTemplateResolution baseResolution =
            RigConformanceWizardTests.CreateResolution(
                Dl1RigTemplateFactory.PlayerProfileName);
        Dl1RigTemplate template = Assert.IsType<Dl1RigTemplate>(
            baseResolution.Template);
        Dl1RigTemplateEntity parent = Assert.Single(
            template.Entities,
            entity => entity.Name == "head");
        var entities = template.Entities.ToBuilder();
        int parentIndex = parent.Index;
        TransformMatrix parentGlobal = parent.GlobalRestMatrix;
        for (int offset = 0; offset < helperNames.Length; offset++)
        {
            TransformMatrix local = TransformMatrix.CreateTranslation(
                offset == 0
                    ? new Vector3D(0.0, 0.05, 0.10)
                    : new Vector3D(0.0, 0.01, 0.02));
            var alias = new Dl1RigTemplateEntity
            {
                Index = entities.Count,
                Name = helperNames[offset],
                ParentIndex = parentIndex,
                Kind = BoneKind.Helper,
                IsDeform = false,
                LocalRestMatrix = local,
                GlobalRestMatrix = parentGlobal * local,
                SemanticRole = null,
            };
            entities.Add(alias);
            parentIndex = alias.Index;
            parentGlobal = alias.GlobalRestMatrix;
        }
        Dl1RigTemplate helperTemplate = new(
            template.ProfileName,
            template.SourceResourceName,
            template.SourceFingerprint,
            entities.ToImmutable());
        return baseResolution with { Template = helperTemplate };
    }

    private static async Task<(RigConformanceResult Fit, CustomModelRigConformance? Settings)>
        PrepareAliasedFitAsync(
            FbxModelAuthoringImportResult model,
            int helperIndex,
            Dl1RigTemplateResolution resolution,
            string helperName = HelperName)
    {
        var wizard = new RigConformanceWizardViewModel(
            (_, _) => Task.FromResult(resolution),
            static _ => { });
        wizard.SetModel(model);
        GuidedFitPreparationResult prepared =
            await wizard.PrepareGuidedFitAsync();
        Assert.True(prepared.CanApply, prepared.Reason);
        RigConformanceResult fit = Assert.IsType<RigConformanceResult>(
            wizard.Fit);
        RigConformedBone canonical = Assert.Single(
            fit.Bones,
            row => row.Name == helperName &&
                row.TemplateIndex >= 0 &&
                row.SourceBoneIndex < 0);
        RigConformedBone extra = Assert.Single(
            fit.Bones,
            row => row.Name == helperName &&
                row.SourceBoneIndex == helperIndex &&
                row.TemplateIndex < 0);
        Assert.Equal(canonical.ParentIndex, extra.ParentIndex);
        return (
            fit,
            wizard.CreateSettings());
    }

    private static bool HasSourceSkinInfluence(
        ImmutableArray<FbxModelSurface> surfaces,
        int sourceBoneIndex) =>
        surfaces.Where(static surface => surface.IsSkinned)
            .SelectMany(static surface => surface.Vertices.Select(vertex =>
                (Surface: surface, Vertex: vertex)))
            .Any(entry => entry.Vertex.BoneIndices
                .Select((slot, index) => (slot, weight: entry.Vertex.BoneWeights[index]))
                .Any(influence => influence.weight > 0.0 &&
                    entry.Surface.PaletteBoneIndices[influence.slot] == sourceBoneIndex));
}

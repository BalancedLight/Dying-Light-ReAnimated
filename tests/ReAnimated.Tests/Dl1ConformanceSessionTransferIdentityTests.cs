using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.Retargeting.Conformance;

namespace ReAnimated.Tests;

public sealed class Dl1ConformanceSessionTransferIdentityTests
{
    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void FallbackOutputIdentityCollisionPreservesExistingEntityAndRoleAssignment()
    {
        FbxModelAuthoringImportResult imported = RigConformanceWizardTests.CreateModel();
        CustomModelDocument sourceDocument = imported.Package.Document;
        const string templateId = "synthetic-template";
        const string addedBoneName = "synthetic-conformed-role";
        Assert.DoesNotContain(
            sourceDocument.Bones,
            bone => string.Equals(
                bone.Name,
                addedBoneName,
                StringComparison.Ordinal));

        Guid legacyFallbackId = LegacyFallbackIdentity(
            sourceDocument.ModelId,
            templateId,
            addedBoneName);
        RiggingSession session = RiggingSessions.Create(
            sourceDocument,
            RigStudioEntryPath.RepairExistingRig);
        RigEntityBinding sourceEntity = session.Recipe.Entities[0] with
        {
            EntityId = legacyFallbackId,
        };
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.SetItem(0, sourceEntity),
                Assignments =
                [new RigRoleAssignment("synthetic.reviewed-role", legacyFallbackId)],
            },
        };
        int childIndex = Enumerable
            .Range(0, sourceDocument.Bones.Length)
            .First(index => sourceDocument.Bones[index].ParentIndex >= 0);
        Guid childId = session.Recipe.Entities[childIndex].EntityId;
        Guid parentId = session.Recipe.Entities[
            sourceDocument.Bones[childIndex].ParentIndex].EntityId;
        session = session with
        {
            ParentDecisions =
            [new RigParentDecision(
                childId,
                parentId,
                session.SourceSha256,
                userApproved: false)],
        };
        session.Validate();
        sourceDocument = sourceDocument with { RiggingSession = session };
        sourceEntity = session.Recipe.Entities[0];

        int addedBoneIndex = sourceDocument.Bones.Length;
        CustomModelBone addedBone = sourceDocument.Bones[0] with
        {
            Index = addedBoneIndex,
            FbxObjectId = 0,
            Name = addedBoneName,
            ParentIndex = 0,
            Kind = BoneKind.Helper,
            IsWeighted = false,
        };
        CustomModelDocument resultDocument = sourceDocument with
        {
            Bones = sourceDocument.Bones.Add(addedBone),
        };
        resultDocument = resultDocument with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(
                resultDocument.CreateEffectiveBones()),
        };

        ImmutableArray<CustomModelBone> outputBones =
            resultDocument.CreateEffectiveBones();
        ImmutableArray<int> sourceToOutput = Enumerable
            .Range(0, sourceDocument.Bones.Length)
            .Concat(Enumerable.Range(
                0,
                sourceDocument.AuthoredHelpers.Length)
                .Select(index => resultDocument.Bones.Length + index))
            .ToImmutableArray();
        Assert.Equal(
            sourceDocument.CreateEffectiveBones().Length,
            sourceToOutput.Length);
        var hierarchy = new Dl1ConformanceHierarchy(
            resultDocument.Bones,
            resultDocument.AuthoredHelpers,
            Enumerable.Range(0, outputBones.Length).ToImmutableArray(),
            sourceToOutput,
            Enumerable.Repeat(TransformMatrix.Identity, outputBones.Length)
                .ToImmutableArray(),
            []);

        CustomModelDocument transferred =
            Dl1ConformanceSessionTransfer.Apply(
                sourceDocument,
                resultDocument,
                hierarchy,
                templateId);

        RiggingSession transferredSession =
            Assert.IsType<RiggingSession>(transferred.RiggingSession);
        transferredSession.Validate();
        RigEntityBinding preservedSource = Assert.Single(
            transferredSession.Recipe.Entities,
            entity => entity.EntityId == legacyFallbackId);
        Assert.Equal(sourceEntity.NativeName, preservedSource.NativeName);
        Assert.Equal(sourceEntity.SourceEntityId, preservedSource.SourceEntityId);
        Assert.Equal(
            session.Recipe.Assignments,
            transferredSession.Recipe.Assignments);
        Assert.Equal(
            session.Recipe.AssetRoles,
            transferredSession.Recipe.AssetRoles);
        Assert.Equal(
            resultDocument.Bones.Select(static bone => bone.ParentIndex),
            transferred.Bones.Select(static bone => bone.ParentIndex));
        Assert.Equal(resultDocument.Meshes, transferred.Meshes);
        Assert.Equal(
            session.ParentDecisions.Single().ParentEntityId,
            transferredSession.ParentDecisions.Single().ParentEntityId);

        RigEntityBinding addedEntity = Assert.Single(
            transferredSession.Recipe.Entities,
            entity => entity.NativeName == addedBoneName);
        Assert.NotEqual(legacyFallbackId, addedEntity.EntityId);
        Assert.Equal("source-name:" + addedBoneName, addedEntity.SourceEntityId);
        Assert.Equal(
            session.Recipe.Entities.Length + 1,
            transferredSession.Recipe.Entities.Length);

        Guid[] firstPassEntityIds = transferredSession.Recipe.Entities
            .Select(static entity => entity.EntityId)
            .Order()
            .ToArray();
        CustomModelDocument refit = Dl1ConformanceSessionTransfer.Apply(
            transferred,
            transferred,
            CreateIdentityHierarchy(transferred),
            templateId);
        RiggingSession refitSession =
            Assert.IsType<RiggingSession>(refit.RiggingSession);
        refitSession.Validate();
        Assert.Equal(
            firstPassEntityIds,
            refitSession.Recipe.Entities
                .Select(static entity => entity.EntityId)
                .Order());
        Assert.Equal(
            session.Recipe.Assignments,
            refitSession.Recipe.Assignments);
    }

    [Fact]
    [Trait("ValidationTier", "Focused")]
    [Trait("Gate", "EditorUsability")]
    public void RefitReusesUnmappedSourceIdentityDeterministicallyAndRejectsTwoRetainedRows()
    {
        FbxModelAuthoringImportResult imported = RigConformanceWizardTests.CreateModel();
        CustomModelDocument sourceDocument = imported.Package.Document;
        const string templateId = "synthetic-template";
        const string sourceRoleName = "synthetic_conformed_role";
        Assert.DoesNotContain(
            sourceDocument.Bones,
            bone => string.Equals(
                bone.Name,
                sourceRoleName,
                StringComparison.Ordinal));

        CustomModelBone sourceRole = sourceDocument.Bones[0] with
        {
            Index = sourceDocument.Bones.Length,
            FbxObjectId = 0,
            Name = sourceRoleName,
            ParentIndex = 0,
            Kind = BoneKind.Helper,
            IsWeighted = false,
        };
        sourceDocument = sourceDocument with
        {
            Bones = sourceDocument.Bones.Add(sourceRole),
        };
        sourceDocument = sourceDocument with
        {
            RigSignature = CustomModelContractSignatures.ComputeRig(
                sourceDocument.CreateEffectiveBones()),
        };

        RiggingSession session = RiggingSessions.Create(
            sourceDocument,
            RigStudioEntryPath.RepairExistingRig);
        int replacedSourceIndex = sourceDocument.Bones.Length - 1;
        Guid legacyFallbackId = LegacyFallbackIdentity(
            sourceDocument.ModelId,
            templateId,
            sourceRoleName);
        RigEntityBinding retainedEntity = session.Recipe.Entities[
            replacedSourceIndex] with
        {
            EntityId = legacyFallbackId,
        };
        Guid parentEntityId = session.Recipe.Entities[
            sourceDocument.Bones[replacedSourceIndex].ParentIndex].EntityId;
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.SetItem(
                    replacedSourceIndex,
                    retainedEntity),
                Assignments =
                [new RigRoleAssignment("synthetic.reviewed-role", legacyFallbackId)],
            },
            ParentDecisions =
            [new RigParentDecision(
                legacyFallbackId,
                parentEntityId,
                session.SourceSha256,
                userApproved: false)],
        };
        session.Validate();
        sourceDocument = sourceDocument with { RiggingSession = session };

        int sourceEffectiveCount = sourceDocument.CreateEffectiveBones().Length;
        ImmutableArray<int> refitSourceMap = Enumerable
            .Range(0, sourceEffectiveCount)
            .Select(index => index == replacedSourceIndex ? -1 : index)
            .ToImmutableArray();
        Dl1ConformanceHierarchy refitHierarchy = CreateHierarchy(
            sourceDocument,
            refitSourceMap);
        CustomModelDocument transferred = Dl1ConformanceSessionTransfer.Apply(
            sourceDocument,
            sourceDocument,
            refitHierarchy,
            templateId);

        RiggingSession transferredSession =
            Assert.IsType<RiggingSession>(transferred.RiggingSession);
        transferredSession.Validate();
        RigEntityBinding reusedEntity = Assert.Single(
            transferredSession.Recipe.Entities,
            entity => entity.EntityId == legacyFallbackId);
        Assert.Equal(sourceRoleName, reusedEntity.NativeName);
        Assert.Equal("source-name:" + sourceRoleName, reusedEntity.SourceEntityId);
        Assert.Equal(session.Recipe.Assignments, transferredSession.Recipe.Assignments);
        Assert.Equal(parentEntityId, transferredSession.ParentDecisions.Single().ParentEntityId);

        Guid[] firstTransferIds = transferredSession.Recipe.Entities
            .Select(static entity => entity.EntityId)
            .Order()
            .ToArray();
        CustomModelDocument repeatedTransfer = Dl1ConformanceSessionTransfer.Apply(
            transferred,
            transferred,
            CreateIdentityHierarchy(transferred),
            templateId);
        RiggingSession repeatedSession =
            Assert.IsType<RiggingSession>(repeatedTransfer.RiggingSession);
        repeatedSession.Validate();
        Assert.Equal(
            firstTransferIds,
            repeatedSession.Recipe.Entities
                .Select(static entity => entity.EntityId)
                .Order());
        Assert.Equal(session.Recipe.Assignments, repeatedSession.Recipe.Assignments);

        CustomModelDocument ambiguousResult = transferred with
        {
            Bones = transferred.Bones.Add(sourceRole with
            {
                Index = transferred.Bones.Length,
            }),
        };
        int repeatedSourceCount = transferred.CreateEffectiveBones().Length;
        InvalidDataException ambiguity = Assert.Throws<InvalidDataException>(() =>
            Dl1ConformanceSessionTransfer.Apply(
                transferred,
                ambiguousResult,
                CreateHierarchy(
                    ambiguousResult,
                    Enumerable.Range(0, repeatedSourceCount)
                        .ToImmutableArray()),
                templateId));
        Assert.Contains("already retained", ambiguity.Message, StringComparison.Ordinal);
    }

    private static Dl1ConformanceHierarchy CreateHierarchy(
        CustomModelDocument result,
        ImmutableArray<int> sourceToOutput)
    {
        ImmutableArray<CustomModelBone> effective = result.CreateEffectiveBones();
        return new Dl1ConformanceHierarchy(
            result.Bones,
            result.AuthoredHelpers,
            Enumerable.Range(0, effective.Length).ToImmutableArray(),
            sourceToOutput,
            Enumerable.Repeat(TransformMatrix.Identity, effective.Length)
                .ToImmutableArray(),
            []);
    }

    private static Dl1ConformanceHierarchy CreateIdentityHierarchy(
        CustomModelDocument document)
    {
        ImmutableArray<CustomModelBone> effective = document.CreateEffectiveBones();
        ImmutableArray<int> identity = Enumerable
            .Range(0, effective.Length)
            .ToImmutableArray();
        return new Dl1ConformanceHierarchy(
            document.Bones,
            document.AuthoredHelpers,
            identity,
            identity,
            Enumerable.Repeat(TransformMatrix.Identity, effective.Length)
                .ToImmutableArray(),
            []);
    }

    private static Guid LegacyFallbackIdentity(
        Guid modelId,
        string templateId,
        string boneName) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(
            modelId.ToString("N") + ":conformance:" + templateId + ":" + boneName))
            .AsSpan(0, 16));
}

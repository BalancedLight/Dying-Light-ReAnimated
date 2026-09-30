using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using ReAnimated.Codecs.CompactMesh;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;
using ReAnimated.DL1.Assets.Catalog;
using ReAnimated.DL1.Assets.Meshes;
using Xunit;

namespace ReAnimated.Tests;

public sealed class Dl1RetailFamilyCandidateTests
{
    [Fact]
    public void AnalyzeRetainsExactIdentityAndReachesFullOnlyThroughSelectedProfileRoles()
    {
        (RetailAssetRecord asset, Dl1MeshData mesh, Dl1RetailMeshProfile profile, Dl1RigTemplate template) =
            Fixture("synthetic_actor_tpp", Dl1MeshPerspective.ThirdPerson);
        RigCapabilityProfile capabilityProfile = CapabilityProfile(
            ["body.root", "body.pelvis", "body.head"]);

        Dl1RetailFamilyCandidate candidate = Dl1RetailFamilyCandidateService.Analyze(
            asset,
            mesh,
            profile,
            template,
            capabilityProfile,
            ["body"]);

        Assert.Equal(Dl1RetailFamilyReadiness.Full, candidate.Readiness);
        Assert.Equal(asset.Id, candidate.AssetId);
        Assert.Equal(asset.Source.ProviderId, candidate.ProviderId);
        Assert.Equal(asset.Id.ContentFingerprint, candidate.ContentFingerprint);
        Assert.Equal(mesh.ResourceName, candidate.MeshResourceName);
        Assert.Same(template, candidate.Template);
        Assert.Contains("body.root", candidate.ActualRoleIds);
        Assert.Equal(RigValidationStatus.Passed, candidate.ProfileResolution.Status);
    }

    [Fact]
    public void MissingDecodedRoleIsPartialAndIsReportedWithoutInventingPlayerAnatomy()
    {
        (RetailAssetRecord asset, Dl1MeshData mesh, Dl1RetailMeshProfile profile, Dl1RigTemplate template) =
            Fixture("synthetic_actor_tpp", Dl1MeshPerspective.ThirdPerson);
        RigCapabilityProfile capabilityProfile = CapabilityProfile(
            ["body.root", "camera.eye"]);

        Dl1RetailFamilyCandidate candidate = Dl1RetailFamilyCandidateService.Analyze(
            asset,
            mesh,
            profile,
            template,
            capabilityProfile,
            ["body"]);

        Assert.Equal(Dl1RetailFamilyReadiness.Partial, candidate.Readiness);
        Assert.Contains(
            candidate.Diagnostics,
            diagnostic => diagnostic.Code == "role.missing-from-decoded-inventory");
        Assert.DoesNotContain("camera.eye", candidate.ActualRoleIds);
    }

    [Fact]
    public void MissingCapabilityProfileLeavesReadinessUnknown()
    {
        (RetailAssetRecord asset, Dl1MeshData mesh, Dl1RetailMeshProfile profile, Dl1RigTemplate template) =
            Fixture("synthetic_actor_tpp", Dl1MeshPerspective.ThirdPerson);

        Dl1RetailFamilyCandidate candidate = Dl1RetailFamilyCandidate.Assess(
            asset,
            mesh,
            profile,
            template);

        Assert.Equal(Dl1RetailFamilyReadiness.Unknown, candidate.Readiness);
        Assert.Contains(
            candidate.Diagnostics,
            diagnostic => diagnostic.Code == "capabilities.profile-unavailable");
    }

    [Fact]
    public void MetadataOnlyAssessRetainsTemplateAndLeavesMeshReadinessUnknown()
    {
        (RetailAssetRecord asset, _, Dl1RetailMeshProfile profile, Dl1RigTemplate template) =
            Fixture("synthetic_actor_tpp", Dl1MeshPerspective.ThirdPerson);
        RigCapabilityProfile capabilityProfile = CapabilityProfile(["body.root"]);

        Dl1RetailFamilyCandidate candidate = Dl1RetailFamilyCandidate.Assess(
            asset,
            profile,
            template,
            capabilityProfile,
            ["body"]);

        Assert.Equal(Dl1RetailFamilyReadiness.Unknown, candidate.Readiness);
        Assert.Same(template, candidate.Template);
        Assert.Contains(
            candidate.Diagnostics,
            diagnostic => diagnostic.Code == "decode.mesh-unavailable");
    }

    [Fact]
    public void PairRequiresMatchingDecodedRoleSetsAndRigSignature()
    {
        (RetailAssetRecord fppAsset, Dl1MeshData fppMesh, Dl1RetailMeshProfile fppProfile, Dl1RigTemplate fppTemplate) =
            Fixture("synthetic_actor_fpp", Dl1MeshPerspective.FirstPerson);
        (RetailAssetRecord tppAsset, Dl1MeshData tppMesh, Dl1RetailMeshProfile tppProfile, Dl1RigTemplate tppTemplate) =
            Fixture("synthetic_actor_tpp", Dl1MeshPerspective.ThirdPerson);
        RigCapabilityProfile capabilityProfile = CapabilityProfile(
            ["body.root", "body.pelvis", "body.head"]);
        
        Dl1RetailFamilyCandidate fpp = Dl1RetailFamilyCandidateService.Analyze(
            fppAsset, fppMesh, fppProfile, fppTemplate, capabilityProfile, ["body"]);
        Dl1RetailFamilyCandidate tpp = Dl1RetailFamilyCandidateService.Analyze(
            tppAsset, tppMesh, tppProfile, tppTemplate, capabilityProfile, ["body"]);

        Dl1RetailFamilyPair pair = Dl1RetailFamilyCandidateService.Pair(fpp, tpp);

        Assert.True(pair.IsPaired, string.Join(" | ", pair.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Same(fpp, pair.FirstPerson);
        Assert.Same(tpp, pair.ThirdPerson);

        Dl1RetailFamilyCandidate changed = tpp with
        {
            RoleInventory = tpp.RoleInventory with
            {
                RoleIds = tpp.RoleInventory.RoleIds
                    .Where(role => role != "body.head")
                    .ToImmutableArray(),
            },
        };
        Dl1RetailFamilyPair conflict = Dl1RetailFamilyCandidateService.Pair(fpp, changed);
        Assert.Equal(Dl1RetailFamilyPairStatus.Conflict, conflict.Status);
        Assert.Contains(
            conflict.Diagnostics,
            diagnostic => diagnostic.Code == "pair.role-inventory-conflict");
    }

    private static (
        RetailAssetRecord Asset,
        Dl1MeshData Mesh,
        Dl1RetailMeshProfile Profile,
        Dl1RigTemplate Template) Fixture(
        string resourceName,
        Dl1MeshPerspective perspective)
    {
        RetailAssetRecord asset = CreateAsset(resourceName);
        Dl1MeshData mesh = CreateMesh(resourceName);
        Dl1RetailMeshProfile profile = new Dl1RetailMeshClassificationService()
            .Classify(asset, mesh) with
        {
            RigFamily = Dl1RigFamily.Player,
            RigFamilyConfidence = Dl1ClassificationConfidence.High,
            Perspective = perspective,
            PerspectiveConfidence = Dl1ClassificationConfidence.High,
        };
        Dl1RigTemplate template =
            Assert.IsType<Dl1RigTemplate>(Dl1RigTemplateFactory.TryCreate(
                "synthetic-player",
                resourceName,
                "template-source",
                mesh.Hierarchy));
        return (asset, mesh, profile, template);
    }

    private static RigCapabilityProfile CapabilityProfile(
        IReadOnlyList<string> roleIds)
    {
        string build = new('b', 64);
        RigEvidenceReference Evidence(string id) => new()
        {
            Id = id,
            Kind = RigEvidenceKind.ProfileRule,
            ArtifactSha256 = new('a', 64),
            BuildFingerprint = build,
        };

        RigRuntimeRole Role(string roleId, string nativeName) => new()
        {
            Id = roleId,
            NativeName = nativeName,
            OwnerAssetRoleId = "character",
            Category = RigRoleCategory.Body,
            EntityKind = RigNativeEntityKind.Bone,
            Requirement = RigRoleRequirementKind.Required,
            FrameRuleId = "frame",
            ComponentRuleId = "components",
            RetentionRuleId = "retention",
            RulesComplete = true,
            Evidence = [Evidence("role-" + roleId)],
        };

        string NativeName(string roleId) => roleId switch
        {
            "body.root" => "bip01",
            "body.pelvis" => "pelvis",
            "body.head" => "head",
            "camera.eye" => "eyecamera",
            _ => roleId,
        };
        var roles = roleIds
            .Select(roleId => Role(roleId, NativeName(roleId)))
            .ToImmutableArray();
        return new RigCapabilityProfile
        {
            Identity = new()
            {
                Id = "synthetic-profile",
                Version = "1",
                ContentSha256 = new('c', 64),
                BuildFingerprint = build,
            },
            FamilyId = "synthetic-player",
            Roles = roles,
            Capabilities =
            [
                new()
                {
                    Id = "body",
                    RoleIds = roleIds.ToImmutableArray(),
                },
            ],
            Consumers =
            [
                new()
                {
                    ConsumerId = "synthetic-reader",
                    CapabilityIds = ["body"],
                    DiscoveredRoleIds = roleIds.ToImmutableArray(),
                    Inspected = true,
                    Evidence = [Evidence("consumer")],
                },
            ],
            ConsumerCoverageComplete = true,
        };
    }

    private static RetailAssetRecord CreateAsset(string resourceName)
    {
        RetailAssetLogicalId logical = RetailAssetLogicalId.Rpack(272, resourceName);
        return new(
            RetailAssetId.Create(
                logical,
                "synthetic-install",
                "synthetic-provider",
                1,
                10,
                "synthetic-snapshot",
                new('d', 64)),
            resourceName,
            new(
                "synthetic-provider",
                RetailAssetSourceKind.Rpack,
                10,
                "synthetic.rpack",
                resourceName,
                1,
                128,
                256,
                DateTime.UnixEpoch));
    }

    private static Dl1MeshData CreateMesh(string resourceName)
    {
        string[] names = ["bip01", "pelvis", "head", "l_upperarm", "r_upperarm", "l_thigh", "r_thigh"];
        var entities = names.Select((name, index) => new CompactMeshEntity(
            index,
            name,
            0,
            new CompactBounds(0, 0, 0, 1, 1, 1),
            (short)(index == 0 ? -1 : index == 1 ? 0 : 1),
            CompactMeshEntityType.Bone,
            0,
            1,
            CompactMatrix3x4.Identity,
            CompactMatrix3x4.Identity,
            0,
            0)).ToArray();
        var hierarchy = new CompactMeshDocument(
            entities.Length,
            1,
            0,
            entities,
            []);
        RigDefinition rig = Assert.IsType<RigDefinition>(
            Dl1RigDefinitionFactory.TryCreate(resourceName, hierarchy));
        var surface = new Dl1MeshSurface(
            "body",
            0,
            0,
            0,
            new Dl1VertexLayout(12, []),
            new Dl1MeshBufferSlice(0, 0, 12, 12),
            new Dl1MeshBufferSlice(1, 0, 2, 2),
            1,
            1,
            [new(
                Vector3.Zero,
                Vector3.UnitY,
                Vector4.UnitX,
                Vector2.Zero,
                Vector2.Zero,
                Vector4.One,
                Vector4.UnitX,
                new Dl1BoneIndex4(0, 0, 0, 0))],
            [0],
            [new Dl1MeshSubmesh(0, 0, 1, 0, [0])]);
        return new(
            resourceName,
            Dl1MeshContainerLayout.FiveItemSplitGpu,
            hierarchy,
            rig,
            [],
            [surface],
            [],
            [],
            [],
            []);
    }
}

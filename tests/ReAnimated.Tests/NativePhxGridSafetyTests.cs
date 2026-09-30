using System.Text;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class NativePhxGridSafetyTests
{
    [Fact]
    public void RejectsFixedGridNodeBoundToAnExplicitBodyBoneRole()
    {
        const string phx = "!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n" +
            "  BonesGridSize(2, 1)\n" +
            "  Bone(0, 0, \"joint_alpha\", 1, 0, 0)\n" +
            "  Bone(1, 0, \"sim_root\", 1, 0, 0)\n" +
            "}\n";
        CustomModelDocument model = Model(phx, "joint_alpha", "sim_root");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            Dl1NativeCompanionWriter.Build(model, "sample", ["joint_alpha", "sim_root"]));

        Assert.Contains("PHX grid node (type 1)", error.Message, StringComparison.Ordinal);
        Assert.Contains("joint_alpha", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsFixedExtraRootAndCoreBoneColliderReferencePreservingPhxText()
    {
        const string phx = "!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n" +
            "  BonesGridSize(1, 1)\n" +
            "  Bone(0, 0, \"sim_root\", 1, 0, 0)\n" +
            "  CollisionSphere(\"joint_alpha\", 0.2)\n" +
            "}\n";
        CustomModelDocument model = Model(phx, "joint_alpha", "sim_root");

        Dl1NativeCompanionBuild result = Dl1NativeCompanionWriter.Build(model, "sample", ["joint_alpha", "sim_root"]);

        Assert.Equal(phx, Encoding.UTF8.GetString(result.Files["sample_000.phx"]));
    }

    [Fact]
    public void ReportsUnverifiedCoreSafetyWhenGridNodeCannotBeClassified()
    {
        const string phx = "!include(\"MeshPartCloth.def\")\nMeshPartCloth()\n{\n" +
            "  BonesGridSize(1, 1)\n" +
            "  Bone(0, 0, \"sim_root\", 1, 0, 0)\n" +
            "}\n";
        CustomModelDocument model = Model(phx, "joint_alpha", "sim_root", includeRoleMap: false);

        Dl1NativeCompanionBuild result =
            Dl1NativeCompanionWriter.Build(model, "sample", ["joint_alpha", "sim_root"]);

        Assert.Contains(result.Notes, note => note.Contains("Core-body grid safety is unverified", StringComparison.Ordinal));
        Assert.Contains("sample_000.phx", result.Files.Keys);
    }

    private static CustomModelDocument Model(string phx, string bodyBoneName, string extraBoneName, bool includeRoleMap = true)
    {
        Guid modelId = Guid.NewGuid();
        Guid bodyBoneId = Guid.NewGuid();
        Guid extraBoneId = Guid.NewGuid();
        return new CustomModelDocument
        {
            ModelId = modelId,
            SecondaryMotion = new SecondaryMotionDefinition
            {
                NativeSources =
                [
                    new() { Kind = NativeClothSourceKind.Phx, ResourceName = "cloth.phx", Text = phx },
                    new() { Kind = NativeClothSourceKind.MpCloth, ResourceName = "cloth.mpcloth",
                        Text = "MeshPartCloth(\"cloth.phx\", 1, 1)\n" },
                ],
            },
            RiggingSession = includeRoleMap ? new RiggingSession
            {
                OwnerModelId = modelId,
                Recipe = new RuntimeRigRecipe
                {
                    ProfileSnapshot = new RigCapabilityProfile
                    {
                        Roles = [new RigRuntimeRole
                        {
                            Id = "body.axis",
                            Category = RigRoleCategory.Body,
                            EntityKind = RigNativeEntityKind.Bone,
                        }, new RigRuntimeRole
                        {
                            Id = "cloth.root",
                            Category = RigRoleCategory.Cloth,
                            EntityKind = RigNativeEntityKind.Bone,
                        }],
                    },
                    Entities =
                    [
                        new() { EntityId = bodyBoneId, OwnerAssetId = modelId, NativeName = bodyBoneName,
                            Kind = RigNativeEntityKind.Bone, Imported = false },
                        new() { EntityId = extraBoneId, OwnerAssetId = modelId, NativeName = extraBoneName,
                            Kind = RigNativeEntityKind.Bone, Imported = false },
                    ],
                    Assignments = [new("body.axis", bodyBoneId), new("cloth.root", extraBoneId)],
                },
            } : null,
        };
    }
}

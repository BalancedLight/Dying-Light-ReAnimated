using ReAnimated.Codecs.Models;

namespace ReAnimated.Tests;

public sealed class Dl1RagdollBoneReviewTests
{
    private const string Source = """
        !include("generic_base.phx")
        PhysicsParams() { QuickStepNumIterations(12) }
        Bones() {
            UseBone("arm", "capsule", 2)
            UseBoneScale("arm_extra", "sphere", 3, 0.75)
            CollisionHelper("helper", "arm", 1.25)
            NoCollidedPair("arm_extra", "helper")
            SelfCollisionGeom("arm", 0.5)
            BoneSynchro("arm", "arm_extra")
            NPBoneSynchroExclusionPropagate("arm_extra")
            UnknownSetting("arm")
        }
        Joints() {
            DefineJoint("arm", "arm_extra", "hinge", 0, 1, 0, 0, 0, 0)
            Set1DOFStops("arm arm_extra", -0.1, 0.3, 0)
        }
        """;

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void ExactTypedReferencesGroupJointCollisionAndSyncCallsWithoutGuessingGlobalSettings()
    {
        Dl1RagdollDocument parsed = Dl1RagdollCodec.Read(Source,
            ["arm", "arm_extra", "helper"], ["generic_base.phx"]);
        Assert.True(parsed.IsValid);
        Dl1RagdollBoneReview[] reviews = Dl1RagdollBoneReviewProjection.Build(parsed).ToArray();
        Assert.Equal(2, reviews.Length);
        Dl1RagdollBoneReview arm = Assert.Single(reviews, review => review.BoneName == "arm");
        Dl1RagdollBoneReview extra = Assert.Single(reviews, review => review.BoneName == "arm_extra");
        Assert.Equal("capsule", arm.ShapeToken);
        Assert.Equal(2, arm.RelativeMass);
        Assert.Null(arm.RadiusMultiplier);
        Assert.Equal("sphere", extra.ShapeToken);
        Assert.Equal(3, extra.RelativeMass);
        Assert.Equal(0.75, extra.RadiusMultiplier);
        Assert.Equal(extra.RadiusMultiplier,extra.ScaleMultiplier);
        Assert.Equal(1, arm.JointCount);
        Assert.Equal(2, arm.CollisionCount);
        Assert.Equal(1, arm.SynchronizationCount);
        Assert.Equal(1, extra.JointCount);
        Assert.Equal(1, extra.CollisionCount);
        Assert.Equal(2, extra.SynchronizationCount);

        Assert.Equal(["UseBone", "CollisionHelper", "SelfCollisionGeom", "BoneSynchro", "DefineJoint", "Set1DOFStops"],
            Names(parsed, arm));
        Assert.Equal(["UseBoneScale", "NoCollidedPair", "BoneSynchro", "NPBoneSynchroExclusionPropagate", "DefineJoint", "Set1DOFStops"],
            Names(parsed, extra));
        Assert.DoesNotContain("UnknownSetting", Names(parsed, arm));
        Assert.DoesNotContain("!include", Names(parsed, arm));
        Assert.DoesNotContain("QuickStepNumIterations", Names(parsed, arm));
        Assert.DoesNotContain(reviews, review => review.BoneName == "helper");
        Assert.Equal(Source, parsed.Syntax.Write());
    }

    [Fact]
    [Trait("ValidationTier", "Hermetic")]
    public void DuplicatePhysicalBoneDeclarationFailsClosed()
    {
        const string duplicate = "Bones() { UseBone(\"arm\", \"capsule\", 2) UseBone(\"arm\", \"sphere\", 3) }";
        Dl1RagdollDocument parsed = Dl1RagdollCodec.Read(duplicate);
        Assert.False(parsed.IsValid);
        Assert.Contains(parsed.Diagnostics, diagnostic => diagnostic.Code == "ragdoll_bone_duplicate");
        Assert.Throws<InvalidDataException>(() => Dl1RagdollBoneReviewProjection.Build(parsed));
        Assert.Equal(duplicate, parsed.Syntax.Write());
    }

    private static string[] Names(Dl1RagdollDocument parsed, Dl1RagdollBoneReview review) =>
        review.RelatedCallIndexes.Select(index => parsed.Syntax.Calls[index].Name).ToArray();
}

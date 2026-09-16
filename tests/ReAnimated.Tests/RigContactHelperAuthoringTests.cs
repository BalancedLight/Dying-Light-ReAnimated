using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigContactHelperAuthoringTests
{
    [Fact]
    public void CreatesAndMaterializesContactWithoutChangingSourceData()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        CustomModelDocument result = Apply(
            document,
            parentId,
            helperEntityId: null,
            name: "contact_helper",
            roleId: "contact.foot",
            frame: Frame(.1, .2, .3),
            center: new Vector3D(.01, .02, .03),
            halfExtents: new Vector3D(.04, .05, .06));

        CustomModelAuthoredHelper helper = Assert.Single(result.AuthoredHelpers);
        HelperRecipe recipe = Assert.Single(result.RiggingSession!.Recipe.Helpers);
        Assert.Equal(helper.Id, recipe.EntityId);
        Assert.Equal("contact.foot", recipe.RoleId);
        Assert.Equal(parentId, recipe.ParentEntityId);
        Assert.Equal(Frame(.1, .2, .3), recipe.LocalFrame);
        Assert.Equal(new Vector3D(.01, .02, .03), recipe.BoundsCenter);
        Assert.Equal(new Vector3D(.04, .05, .06), recipe.BoundsHalfExtents);
        Assert.Equal(RigFramePolicy.Contact, recipe.FramePolicy);
        Assert.Equal(RigEvidenceKind.GeometryInference, recipe.PlacementProvenance);
        Assert.True(recipe.UserApproved);
        Assert.Contains(recipe.Evidence, evidence =>
            evidence.Kind == RigEvidenceKind.ImportedSource &&
            evidence.ArtifactSha256 == document.Source.ContentSha256);
        Assert.Contains(recipe.Evidence, evidence =>
            evidence.Kind == RigEvidenceKind.GeometryInference &&
            evidence.ArtifactSha256 == document.Source.ContentSha256 &&
            evidence.Description!.Contains("geometry", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(result.Bones, document.Bones);
        Assert.Equal(result.Meshes, document.Meshes);
        Assert.Equal(result.MorphChannels, document.MorphChannels);
        Assert.Equal(result.Materials, document.Materials);
        Assert.Equal(result.Source, document.Source);
        Assert.Equal(0, helper.ParentNodeIndex);
        Assert.Null(result.LastBuildReceipt);
        Assert.True(result.RiggingSession.Revision > document.RiggingSession!.Revision);
    }

    [Fact]
    public void ReapplyingTheSameContactIsAnIdentityPreservingNoOp()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        CustomModelDocument first = Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));
        Guid helperId = Assert.Single(first.AuthoredHelpers).Id;

        CustomModelDocument second = Apply(
            first,
            parentId,
            helperId,
            "contact_helper",
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));

        Assert.Same(first, second);
        Assert.Single(second.AuthoredHelpers);
        Assert.Single(second.RiggingSession!.Recipe.Helpers);
    }

    [Fact]
    public void ExistingContactUpdatesInPlaceAndPreservesItsStableId()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        CustomModelDocument first = Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));
        Guid helperId = Assert.Single(first.AuthoredHelpers).Id;

        CustomModelDocument updated = Apply(
            first,
            parentId,
            helperId,
            "contact_helper_updated",
            "contact.foot.updated",
            Frame(.2, .3, .4),
            new Vector3D(.01, .02, .03),
            new Vector3D(.06, .07, .08));

        Assert.Equal(helperId, Assert.Single(updated.AuthoredHelpers).Id);
        Assert.Equal("contact_helper_updated", updated.AuthoredHelpers[0].Name);
        HelperRecipe recipe = Assert.Single(updated.RiggingSession!.Recipe.Helpers);
        Assert.Equal("contact.foot.updated", recipe.RoleId);
        Assert.Equal(Frame(.2, .3, .4), recipe.LocalFrame);
        Assert.Equal(new Vector3D(.06, .07, .08), recipe.BoundsHalfExtents);
        Assert.Equal(first.Bones, updated.Bones);
        Assert.Equal(first.Meshes, updated.Meshes);
    }

    [Fact]
    public void ImportedHelperGetsAFrameOnlyContactRecipeWithoutChangingSourceIdentity()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        Assert.True(document.Bones.Length > 1);
        RiggingSession session = document.RiggingSession!;
        Guid importedHelperId = session.Recipe.Entities[1].EntityId;
        string sourceName = document.Bones[1].Name;
        CustomModelDocument importedHelper = document with
        {
            Bones = document.Bones.SetItem(1, document.Bones[1] with { Kind = BoneKind.Helper }),
            RiggingSession = session with
            {
                Recipe = session.Recipe with
                {
                    Entities = session.Recipe.Entities.SetItem(
                        1,
                        session.Recipe.Entities[1] with { Kind = RigNativeEntityKind.Unknown }),
                },
            },
        };
        importedHelper.Validate();

        CustomModelDocument result = Apply(
            importedHelper,
            parentId,
            importedHelperId,
            sourceName,
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));

        Assert.Empty(result.AuthoredHelpers);
        HelperRecipe recipe = Assert.Single(result.RiggingSession!.Recipe.Helpers);
        Assert.Equal(importedHelperId, recipe.EntityId);
        RigEntityBinding entity = result.RiggingSession.Recipe.Entities.Single(
            candidate => candidate.EntityId == importedHelperId);
        Assert.True(entity.Imported);
        Assert.Equal(session.Recipe.Entities[1].SourceEntityId, entity.SourceEntityId);
        Assert.Equal(sourceName, entity.NativeName);
        Assert.Equal(RigNativeEntityKind.Helper, entity.Kind);
        Assert.Contains(result.RiggingSession.Recipe.Helpers[0].Evidence, evidence =>
            evidence.Description!.Contains("observed source kind", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(BoneKind.Helper, result.Bones[1].Kind);
        Assert.Equal(importedHelper.Bones, result.Bones);

        Assert.Throws<InvalidOperationException>(() => Apply(
            importedHelper,
            parentId,
            importedHelperId,
            "renamed_source_helper",
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06)));
    }

    [Fact]
    public void AuthoredHelperWithoutPriorRecipeReceivesContactRecipeAndKeepsItsId()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        CustomModelDocument withHelper = CustomModelHelperAuthoring.DuplicateAsHelper(
            document,
            0,
            CustomModelAuthoredHelperKind.Helper,
            "generic_authored_helper");
        RiggingSession session = withHelper.RiggingSession!;
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.Select(entity =>
                    entity.EntityId == parentId
                        ? entity with { Kind = RigNativeEntityKind.Bone }
                        : entity).ToImmutableArray(),
            },
        };
        withHelper = withHelper with { RiggingSession = session };
        withHelper.Validate();
        CustomModelAuthoredHelper helper = Assert.Single(withHelper.AuthoredHelpers);
        Assert.Empty(session.Recipe.Helpers);

        CustomModelDocument result = Apply(
            withHelper,
            parentId,
            helper.Id,
            helper.Name,
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));

        Assert.Equal(helper.Id, Assert.Single(result.AuthoredHelpers).Id);
        Assert.Equal(helper.Name, result.AuthoredHelpers[0].Name);
        Assert.Equal(helper.Id, Assert.Single(result.RiggingSession!.Recipe.Helpers).EntityId);
        Assert.DoesNotContain(result.RiggingSession.Recipe.FramePolicies, policy => policy.EntityId == helper.Id);
    }

    [Fact]
    public void RejectsForeignParentAndStaleSessionToken()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        RiggingJobToken token = document.RiggingSession!.CreateJobToken();

        Assert.Throws<ArgumentException>(() => RigContactHelperAuthoring.Apply(
            document,
            token,
            Guid.NewGuid(),
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06),
            RigEvidenceKind.GeometryInference,
            "Geometry contact proposal."));

        Assert.Throws<InvalidOperationException>(() => RigContactHelperAuthoring.Apply(
            document,
            token,
            parentId,
            parentId,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06),
            RigEvidenceKind.GeometryInference,
            "Geometry contact proposal."));

        RiggingSession changed = document.RiggingSession! with
        {
            WeightMirrorTolerance = document.RiggingSession.WeightMirrorTolerance + .001,
        };
        CustomModelDocument stale = document with { RiggingSession = changed };
        stale.Validate();
        Assert.Throws<InvalidOperationException>(() => RigContactHelperAuthoring.Apply(
            stale,
            token,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06),
            RigEvidenceKind.GeometryInference,
            "Geometry contact proposal."));
    }

    [Fact]
    public void UnknownImportedDeformEntityIsAcceptedAsAnAuthorSelectedParent()
    {
        CustomModelDocument document = CreateDocumentWithUnknownDeformParent(out Guid parentId);

        CustomModelDocument result = Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));

        Assert.Single(result.RiggingSession!.Recipe.Helpers);
        Assert.Equal(
            RigNativeEntityKind.Bone,
            result.RiggingSession.Recipe.Entities.Single(e => e.EntityId == parentId).Kind);
    }

    [Fact]
    public void UnknownImportedHelperEntityIsNotAcceptedAsAContactParent()
    {
        CustomModelDocument document = CreateDocument(out _);
        Assert.True(document.Bones.Length > 1);
        Guid parentId = document.RiggingSession!.Recipe.Entities[1].EntityId;
        document = document with
        {
            Bones = document.Bones.SetItem(1, document.Bones[1] with { Kind = BoneKind.Helper }),
        };
        document.Validate();

        Assert.Throws<ArgumentException>(() => Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06)));
    }

    [Fact]
    public void RejectsNonPositiveExtentsAndNonInvertibleFrame()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        RiggingJobToken token = document.RiggingSession!.CreateJobToken();

        Assert.Throws<ArgumentOutOfRangeException>(() => RigContactHelperAuthoring.Apply(
            document,
            token,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, 0, .06),
            RigEvidenceKind.GeometryInference,
            "Geometry contact proposal."));

        Assert.Throws<ArgumentException>(() => RigContactHelperAuthoring.Apply(
            document,
            token,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            new TransformMatrix(0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06),
            RigEvidenceKind.GeometryInference,
            "Geometry contact proposal."));
    }

    [Fact]
    public void LockedContactFieldsRejectChangedNameParentFrameAndExtents()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        CustomModelDocument first = Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(.1, .2, .3),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));
        Guid helperId = Assert.Single(first.AuthoredHelpers).Id;
        RiggingSession lockedSession = first.RiggingSession! with
        {
            Recipe = first.RiggingSession.Recipe with
            {
                Helpers = [first.RiggingSession.Recipe.Helpers[0] with
                {
                    LockedFields = RigHelperEditFields.All,
                }],
            },
        };
        CustomModelDocument locked = first with { RiggingSession = lockedSession };
        locked.Validate();

        Assert.Throws<InvalidOperationException>(() => Apply(
            locked,
            parentId,
            helperId,
            "contact_helper_changed",
            "contact.foot.changed",
            Frame(.2, .3, .4),
            new Vector3D(.01, .02, .03),
            new Vector3D(.06, .07, .08)));
        Assert.Equal("contact_helper", locked.AuthoredHelpers[0].Name);
    }

    [Fact]
    public void UnrelatedFramePoliciesSurviveAndHelperPolicyDoesNotCompete()
    {
        CustomModelDocument document = CreateDocument(out Guid parentId);
        Guid unrelated = document.RiggingSession!.Recipe.Entities[0].EntityId;
        RigEntityFramePolicy policy = new()
        {
            EntityId = unrelated,
            FramePolicy = RigFramePolicy.Manual,
            BoundsPolicy = RigBoundsPolicy.Solved,
            BoundsCenter = Vector3D.Zero,
            BoundsHalfExtents = Vector3D.One,
            Evidence = [new RigEvidenceReference
            {
                Id = "unrelated-policy",
                Kind = RigEvidenceKind.UserOverride,
                Description = "Unrelated synthetic frame policy.",
            }],
        };
        document = document with
        {
            RiggingSession = document.RiggingSession with
            {
                Recipe = document.RiggingSession.Recipe with { FramePolicies = [policy] },
            },
        };
        document.Validate();

        CustomModelDocument result = Apply(
            document,
            parentId,
            null,
            "contact_helper",
            "contact.foot",
            Frame(0, 0, 0),
            Vector3D.Zero,
            new Vector3D(.04, .05, .06));

        Assert.Single(result.RiggingSession!.Recipe.FramePolicies);
        Assert.Equal(unrelated, result.RiggingSession.Recipe.FramePolicies[0].EntityId);
        Guid helperId = Assert.Single(result.RiggingSession.Recipe.Helpers).EntityId;
        Assert.DoesNotContain(result.RiggingSession.Recipe.FramePolicies, p => p.EntityId == helperId);
    }

    [Fact]
    public void MaterializerStillRefusesImportedHelperRename()
    {
        CustomModelDocument document = CreateDocument(out _);
        RiggingSession session = document.RiggingSession!;
        Assert.True(document.Bones.Length > 1);
        Guid rootId = session.Recipe.Entities[0].EntityId;
        RigEntityBinding imported = session.Recipe.Entities[1] with
        {
            Kind = RigNativeEntityKind.Bone,
            NativeName = "renamed_imported_bone",
        };
        Guid importedId = imported.EntityId;
        RiggingSession candidate = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities
                    .SetItem(0, session.Recipe.Entities[0] with { Kind = RigNativeEntityKind.Bone })
                    .SetItem(1, imported),
                Helpers = [new HelperRecipe
                {
                    EntityId = importedId,
                    OwnerAssetId = document.ModelId,
                    RoleId = "contact.foot",
                    ParentEntityId = rootId,
                    LocalFrame = TransformMatrix.Identity,
                    BoundsCenter = Vector3D.Zero,
                    BoundsHalfExtents = new Vector3D(.04, .05, .06),
                    FramePolicy = RigFramePolicy.Contact,
                }],
            },
        };
        document = document with { RiggingSession = candidate };
        document.Validate();

        Assert.Throws<InvalidOperationException>(() => RiggingHelperMaterializer.Apply(document));
    }

    private static CustomModelDocument CreateDocument(out Guid parentId)
    {
        var imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(),
            "generic-contact.fbx");
        RiggingSession session = RiggingSessions.Create(
            imported.Package.Document,
            RigStudioEntryPath.RepairExistingRig);
        parentId = session.Recipe.Entities[0].EntityId;
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities
                    .SetItem(0, session.Recipe.Entities[0] with
                    {
                        Kind = RigNativeEntityKind.Bone,
                    }),
            },
        };
        CustomModelDocument document = imported.Package.Document with
        {
            RiggingSession = session,
        };
        document.Validate();
        return document;
    }

    private static CustomModelDocument CreateDocumentWithUnknownDeformParent(out Guid parentId)
    {
        CustomModelDocument document = CreateDocument(out _);
        int deformIndex = -1;
        for (int index = 0; index < document.Bones.Length; index++)
        {
            if (document.Bones[index].Kind == BoneKind.Deform)
            {
                deformIndex = index;
                break;
            }
        }

        Assert.True(deformIndex >= 0);
        parentId = document.RiggingSession!.Recipe.Entities[deformIndex].EntityId;
        document.Validate();
        return document;
    }

    private static CustomModelDocument Apply(
        CustomModelDocument document,
        Guid parentId,
        Guid? helperEntityId,
        string name,
        string roleId,
        TransformMatrix frame,
        Vector3D center,
        Vector3D halfExtents) =>
        RigContactHelperAuthoring.Apply(
            document,
            document.RiggingSession!.CreateJobToken(),
            parentId,
            helperEntityId,
            name,
            roleId,
            frame,
            center,
            halfExtents,
            RigEvidenceKind.GeometryInference,
            "Geometry-derived contact placement for review.");

    private static TransformMatrix Frame(double x, double y, double z) =>
        new(1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1);

}

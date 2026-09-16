using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Codecs.Models;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RiggingHelperEditTests
{
    private static FbxModelAuthoringImportResult Model()
    {
        var model = FbxModelAuthoringImporter.Import(BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic.fbx");
        return model with { Package = model.Package with { Document = model.Package.Document with
            { RiggingSession = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.RepairExistingRig) } } };
    }

    [Fact]
    public void HelperCreationSynchronizesStableIdentityAndCanBePreparedImmediately()
    {
        var model = Model();
        var document = CustomModelHelperAuthoring.DuplicateAsHelper(model.Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "generic_helper");
        var helper = Assert.Single(document.AuthoredHelpers);
        Assert.Contains(document.RiggingSession!.Recipe.Entities, e => e.EntityId == helper.Id && e.OwnerAssetId == document.ModelId);
        var prepared = Dl1CustomModelRigPreparer.Prepare(model with { Package = model.Package with { Document = document }, Rig = document.CreateRigDefinition() });
        Assert.Contains(prepared.Contract.Nodes, n => n.SemanticEntityId == helper.Id);
        Assert.Equal(model.Package.Document.Bones, document.Bones);
        Assert.True(document.RiggingSession.Revision > model.Package.Document.RiggingSession!.Revision);
    }

    [Fact]
    public void TransformEditingRetainsScaleAndAffineResidual()
    {
        var original = CustomModelHelperAuthoring.DuplicateAsHelper(Model().Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "generic_helper");
        var helper = original.AuthoredHelpers[0];
        var trs = new TransformTRS(Vector3D.Zero, QuaternionD.Identity, new(.5, 1, 2));
        var shear = new TransformMatrix(1, .2, 0, 0, 0, 1, .1, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        original = CustomModelHelperAuthoring.SetLocalTransform(original, helper.Id, trs, trs.ToMatrix() * shear);
        var movedTrs = trs with { Translation = new(.25, .5, .75) };
        var updated = CustomModelHelperAuthoring.SetLocalTransform(original, helper.Id, movedTrs);
        Assert.True(updated.AuthoredHelpers[0].ExactLocalMatrix.NearlyEquals(movedTrs.ToMatrix() * shear, 1e-10));
        Assert.NotEqual(original.RiggingSession!.ComputeInputFingerprint(), updated.RiggingSession!.ComputeInputFingerprint());
        Assert.Equal(trs.Scale, updated.AuthoredHelpers[0].LocalTransform.Scale);
    }

    [Fact]
    public void LockedRecipeRejectsAnEditBeforePublishingAnyDocument()
    {
        var document = CustomModelHelperAuthoring.DuplicateAsHelper(Model().Package.Document, 0, CustomModelAuthoredHelperKind.Helper, "generic_helper");
        var helper = document.AuthoredHelpers[0];
        var session = document.RiggingSession!;
        var parent = session.Recipe.Entities[0].EntityId;
        session = session with { Recipe = session.Recipe with
        {
            Entities = session.Recipe.Entities.Select(e => e.EntityId == parent ? e with { Kind = RigNativeEntityKind.Bone } : e).ToImmutableArray(),
            FramePolicies = [],
            Helpers = [new() { EntityId = helper.Id, ParentEntityId = parent, OwnerAssetId = document.ModelId, RoleId = "generic-role",
                FramePolicy = RigFramePolicy.Manual, LocalFrame = helper.ExactLocalMatrix, LockedFields = RigHelperEditFields.Position }],
        } };
        document = document with { RiggingSession = session };
        Assert.Throws<InvalidOperationException>(() => CustomModelHelperAuthoring.SetLocalTransform(document, helper.Id,
            new TransformTRS(Vector3D.UnitX, QuaternionD.Identity, Vector3D.One)));
        Assert.Equal(Vector3D.Zero, document.AuthoredHelpers[0].LocalTransform.Translation);
        Assert.Equal(session.Generation, document.RiggingSession.Generation);
    }

    [Fact]
    public void PreparedHelpersMaterializeInParentOrderAndReapplyAsANoOp()
    {
        var model = Model();
        var document = DocumentWithPendingHelpers(model.Package.Document);
        var materialized = RiggingHelperMaterializer.Apply(document);
        Assert.Equal(2, materialized.AuthoredHelpers.Length);
        Assert.Equal("generic_parent", materialized.AuthoredHelpers[0].Name);
        Assert.Equal("generic_contact", materialized.AuthoredHelpers[1].Name);
        Assert.Equal(document.Bones.Length, materialized.AuthoredHelpers[1].ParentNodeIndex);
        Assert.Equal(document.Bones, materialized.Bones);
        Assert.Same(materialized, RiggingHelperMaterializer.Apply(materialized));
        var prepared = Dl1CustomModelRigPreparer.Prepare(model with { Package = model.Package with { Document = materialized }, Rig = materialized.CreateRigDefinition() });
        var contact = Assert.Single(prepared.Contract.Nodes, n => n.Name == "generic_contact");
        Assert.Equal(RigFramePolicy.Contact, contact.FramePolicy);
        Assert.True(Math.Abs(contact.Bounds.HalfExtents.Y - .01) < 1e-8);
    }

    [Fact]
    public void MaterializationFailureLeavesSourceAndRecipesUntouched()
    {
        var document = DocumentWithPendingHelpers(Model().Package.Document);
        var session = document.RiggingSession!;
        Guid childId = session.Recipe.Helpers[0].EntityId;
        var collision = session.Recipe.Entities.Select(e => e.EntityId == childId ? e with { NativeName = document.Bones[0].Name } : e).ToImmutableArray();
        document = document with { RiggingSession = session with { Recipe = session.Recipe with { Entities = collision } } };
        Assert.Throws<ArgumentException>(() => RiggingHelperMaterializer.Apply(document));
        Assert.Empty(document.AuthoredHelpers);
        Assert.Equal(2, document.RiggingSession.Recipe.Helpers.Length);
    }

    internal static CustomModelDocument DocumentWithPendingHelpers(CustomModelDocument document)
    {
        var session = document.RiggingSession ?? RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        Guid parentId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        Guid root = session.Recipe.Entities[0].EntityId;
        return document with { RiggingSession = session with { Recipe = session.Recipe with
        {
            Entities = session.Recipe.Entities.Select(e => e.EntityId == root ? e with { Kind = RigNativeEntityKind.Bone } : e).Concat(new RigEntityBinding[]
            {
                new() { EntityId = parentId, OwnerAssetId = document.ModelId, NativeName = "generic_parent", Kind = RigNativeEntityKind.Helper, Imported = false },
                new() { EntityId = childId, OwnerAssetId = document.ModelId, NativeName = "generic_contact", Kind = RigNativeEntityKind.Helper, Imported = false },
            }).ToImmutableArray(),
            Helpers =
            [
                new() { EntityId = childId, OwnerAssetId = document.ModelId, ParentEntityId = parentId, RoleId = "generic-contact", FramePolicy = RigFramePolicy.Contact,
                    LocalFrame = new TransformTRS(new(.1, .2, .3), QuaternionD.Identity, Vector3D.One).ToMatrix(), BoundsCenter = Vector3D.Zero, BoundsHalfExtents = new(.05, .01, .08) },
                new() { EntityId = parentId, OwnerAssetId = document.ModelId, ParentEntityId = root, RoleId = "generic-parent", FramePolicy = RigFramePolicy.Manual },
            ],
        } } };
    }
}

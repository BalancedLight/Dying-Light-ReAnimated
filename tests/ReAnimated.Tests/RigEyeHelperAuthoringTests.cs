using System.Collections.Immutable;
using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigEyeHelperAuthoringTests
{
    [Fact]
    public void AppliesExactGlobalFrameWithoutChangingImportedSource()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        TransformMatrix parentGlobal = Globals(original.CreateEffectiveBones())[0];
        TransformMatrix desired = parentGlobal * new TransformMatrix(
            1.0, .2, 0, .3,
            0, 1.0, .1, -.2,
            0, 0, 1.0, .4,
            0, 0, 0, 1.0);
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, RigEyeSetupMode.GeometryPivot, parentId, desired);

        CustomModelDocument result = RigEyeHelperAuthoring.Apply(
            original, session.CreateJobToken(), setup, "left_eye_pivot");
        CustomModelAuthoredHelper helper = Assert.Single(result.AuthoredHelpers);
        TransformMatrix actualGlobal = Globals(result.CreateEffectiveBones())[result.Bones.Length];

        Assert.True(actualGlobal.NearlyEquals(desired, 1e-10));
        Assert.True(helper.ExactLocalMatrix.NearlyEquals(parentGlobal.InvertedAffine() * desired, 1e-10));
        Assert.Equal<CustomModelBone>(original.Bones, result.Bones);
        Assert.Equal<CustomModelMeshPart>(original.Meshes, result.Meshes);
        Assert.Equal(original.Source, result.Source);
        Assert.Equal(CustomModelAuthoredHelperKind.Helper, helper.Kind);
        CustomModelBone effectiveHelper = result.CreateEffectiveBones()[result.Bones.Length];
        Assert.Equal(BoneKind.Helper, effectiveHelper.Kind);
        Assert.False(effectiveHelper.IsWeighted);

        RigEyeSetup saved = Assert.Single(result.RiggingSession!.Eyes);
        Assert.Equal(helper.Id, saved.HelperEntityId);
        HelperRecipe recipe = Assert.Single(result.RiggingSession.Recipe.Helpers);
        Assert.Equal(RigFramePolicy.Manual, recipe.FramePolicy);
        Assert.Equal(Vector3D.Zero, recipe.BoundsCenter);
        Assert.Equal(Vector3D.Zero, recipe.BoundsHalfExtents);
        Assert.Contains(recipe.Evidence, evidence => evidence.Kind == RigEvidenceKind.ImportedSource && evidence.ArtifactSha256 == session.SourceSha256);
    }

    [Fact]
    public void GlobeUsesZeroCenterAndFittedRadiusAndRepeatIsNoOp()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, RigEyeSetupMode.GeometryPivot, parentId,
            TransformMatrix.CreateTranslation(new(.1, .2, .3))) with
        {
            GeometryKind = RigEyeGeometryKind.GlobeCandidate,
            ComponentId = session.Components[0].Id,
            IslandIndex = 0,
            SourceControlPointIds = [0, 1, 2],
            GlobeRadius = .125,
        };

        CustomModelDocument result = RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(), setup, "left_globe");
        RigEyeSetup saved = Assert.Single(result.RiggingSession!.Eyes);
        CustomModelDocument repeated = RigEyeHelperAuthoring.Apply(result, result.RiggingSession.CreateJobToken(), saved, "left_globe");
        HelperRecipe recipe = Assert.Single(result.RiggingSession.Recipe.Helpers);

        Assert.Equal(new Vector3D(.125, .125, .125), recipe.BoundsHalfExtents);
        Assert.Equal(Vector3D.Zero, recipe.BoundsCenter);
        Assert.Same(result, repeated);
    }

    [Fact]
    public void SupportsSeparateGeometryAndGazeModesAndPreservesStandalonePolicyScope()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup left = Setup(session, RigEyeSide.Left, RigEyeSetupMode.GeometryPivot, parentId,
            TransformMatrix.CreateTranslation(new(-.1, .2, .3)));
        CustomModelDocument first = RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(), left, "left_pivot");
        RiggingSession firstSession = first.RiggingSession!;
        RigEyeSetup right = Setup(firstSession, RigEyeSide.Right, RigEyeSetupMode.GazeReference, parentId,
            TransformMatrix.CreateTranslation(new(.1, .2, .3)));
        CustomModelDocument second = RigEyeHelperAuthoring.Apply(first, firstSession.CreateJobToken(), right, "right_gaze");

        Assert.Equal(2, second.AuthoredHelpers.Length);
        Assert.Equal(2, second.RiggingSession!.Eyes.Length);
        Assert.Equal(2, second.RiggingSession.Recipe.Helpers.Length);
        Assert.Empty(second.RiggingSession.Recipe.FramePolicies);
    }

    [Fact]
    public void RejectsStaleJobsReservedCameraNamesAndImportedHelperReferences()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, RigEyeSetupMode.GeometryPivot, parentId,
            TransformMatrix.CreateTranslation(new(.1, .2, .3)));

        RiggingSession changedSession = RiggingSessions.Change(session,
            session with { SymmetryOrigin = new(.1, 0, 0) }, RiggingEditKind.Coordinates);
        CustomModelDocument changed = original with { RiggingSession = changedSession };
        Assert.Throws<InvalidOperationException>(() => RigEyeHelperAuthoring.Apply(changed, session.CreateJobToken(), setup, "left_pivot"));
        Assert.Throws<InvalidOperationException>(() => RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(), setup, "EyeCamera"));
        Assert.Throws<InvalidOperationException>(() => RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(),
            setup with { HelperEntityId = parentId }, "left_pivot"));
        Assert.Throws<ArgumentException>(() => RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(),
            setup with { Mode = RigEyeSetupMode.SourceEye }, "left_pivot"));
    }

    [Fact]
    public void ExistingHelperKeepsNameParentAndRejectsLockedExtentChanges()
    {
        CustomModelDocument original = WithSession();
        RiggingSession session = original.RiggingSession!;
        Guid parentId = session.Recipe.Entities[0].EntityId;
        RigEyeSetup setup = Setup(session, RigEyeSide.Left, RigEyeSetupMode.GeometryPivot, parentId,
            TransformMatrix.CreateTranslation(new(.1, .2, .3))) with
        {
            GeometryKind = RigEyeGeometryKind.GlobeCandidate,
            ComponentId = session.Components[0].Id,
            IslandIndex = 0,
            SourceControlPointIds = [0, 1, 2],
            GlobeRadius = .1,
        };
        CustomModelDocument applied = RigEyeHelperAuthoring.Apply(original, session.CreateJobToken(), setup, "left_globe");
        Guid helperId = Assert.Single(applied.AuthoredHelpers).Id;
        RiggingSession lockedSession = applied.RiggingSession! with
        {
            Recipe = applied.RiggingSession.Recipe with
            {
                Helpers = [Assert.Single(applied.RiggingSession.Recipe.Helpers) with { LockedFields = RigHelperEditFields.Extents }],
            },
        };
        lockedSession.Validate();
        CustomModelDocument locked = applied with { RiggingSession = lockedSession };
        RigEyeSetup changed = lockedSession.Eyes[0] with { HelperEntityId = helperId, GlobeRadius = .2 };

        Assert.Throws<InvalidOperationException>(() => RigEyeHelperAuthoring.Apply(locked, lockedSession.CreateJobToken(), changed, "left_globe"));
        Assert.Throws<InvalidOperationException>(() => RigEyeHelperAuthoring.Apply(applied, applied.RiggingSession.CreateJobToken(),
            setup with { HelperEntityId = helperId }, "renamed_globe"));
    }

    private static RigEyeSetup Setup(RiggingSession session, RigEyeSide side, RigEyeSetupMode mode, Guid parentId, TransformMatrix frame) => new()
    {
        Side = side,
        Mode = mode,
        GeometryKind = RigEyeGeometryKind.ManualPivot,
        ParentEntityId = parentId,
        GlobalFrame = frame,
        UserApproved = true,
        Evidence = [new RigEvidenceReference
        {
            Id = "synthetic-eye-review",
            Kind = RigEvidenceKind.UserOverride,
            ArtifactSha256 = session.SourceSha256,
            Description = "Reviewed generic eye helper placement.",
        }],
    };

    private static CustomModelDocument WithSession()
    {
        FbxModelAuthoringImportResult imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-eye-helper.fbx");
        CustomModelDocument document = imported.Package.Document;
        // Keep the test independent of TRS projection: the observed parent
        // carries nonuniform scale plus shear, while the helper stores the
        // exact affine residual.
        TransformMatrix shearedParent = new(
            2.0, .35, 0, 0,
            0, 1.5, .2, 0,
            0, 0, .75, 0,
            0, 0, 0, 1.0);
        document = document with
        {
            Bones = document.Bones.SetItem(0, document.Bones[0] with
            {
                LocalBindTransform = TransformTRS.Identity,
                ExactLocalBindMatrix = shearedParent,
            }),
        };
        RiggingSession session = RiggingSessions.Create(document, RigStudioEntryPath.RepairExistingRig);
        return document with { RiggingSession = session };
    }

    private static TransformMatrix[] Globals(IReadOnlyList<CustomModelBone> bones)
    {
        var globals = new TransformMatrix[bones.Count];
        foreach (CustomModelBone bone in bones)
            globals[bone.Index] = bone.ParentIndex < 0 ? bone.ExactLocalBindMatrix : globals[bone.ParentIndex] * bone.ExactLocalBindMatrix;
        return globals;
    }
}

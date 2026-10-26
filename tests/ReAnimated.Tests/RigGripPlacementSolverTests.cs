using ReAnimated.Codecs.Fbx;
using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigGripPlacementSolverTests
{
    [Fact]
    public void TransfersCompleteGripFrameAndScalesOnlyItsWristOffset()
    {
        RigPalmLandmarks reference = Palm();
        TransformMatrix modelRotation = TransformMatrix.CreateRotation(
            QuaternionD.FromAxisAngle(Vector3D.UnitZ, Math.PI / 2));
        Vector3D origin = new(3, -2, 1);
        Vector3D Map(Vector3D point) => origin + modelRotation.TransformDirection(point) * .4;
        RigPalmLandmarks source = new(Map(reference.Wrist), Map(reference.IndexBase), Map(reference.MiddleBase),
            Map(reference.RingBase), Map(reference.LittleBase));
        TransformMatrix referenceGrip = TransformMatrix.CreateRotation(
            QuaternionD.FromAxisAngle(Vector3D.UnitX, .3)) with { M14 = .8, M24 = -.1, M34 = .2 };
        TransformMatrix hand = modelRotation * TransformMatrix.CreateRotation(
            QuaternionD.FromAxisAngle(Vector3D.UnitY, -.6));
        hand = hand with { M14 = origin.X, M24 = origin.Y, M34 = origin.Z };

        RigGripPlacementSolution solved = RigGripPlacementSolver.Solve(source, reference, referenceGrip, hand);

        Assert.Equal(.4, solved.HandScale, 12);
        TransformMatrix expected = modelRotation * referenceGrip;
        Vector3D expectedPosition = Map(referenceGrip.Translation);
        expected = expected with { M14 = expectedPosition.X, M24 = expectedPosition.Y, M34 = expectedPosition.Z };
        Assert.True(solved.GlobalFrame.NearlyEquals(expected, 1e-12));
        Assert.True((hand * solved.LocalFrame).NearlyEquals(expected, 1e-12));
        Assert.Equal(1, solved.GlobalFrame.LinearDeterminant, 12);
    }

    [Fact]
    public void RejectsDegeneratePalmDirectionsWithUsefulErrors()
    {
        RigPalmLandmarks collapsed = new(Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero, Vector3D.Zero);
        ArgumentException forward = Assert.Throws<ArgumentException>(() =>
            RigGripPlacementSolver.Solve(collapsed, Palm(), TransformMatrix.Identity, TransformMatrix.Identity));
        Assert.Contains("source wrist", forward.Message);
        RigPalmLandmarks collinear = new(Vector3D.Zero, Vector3D.UnitX, Vector3D.UnitX * 2,
            Vector3D.UnitX * 3, Vector3D.UnitX * 4);
        ArgumentException width = Assert.Throws<ArgumentException>(() =>
            RigGripPlacementSolver.Solve(Palm(), collinear, TransformMatrix.Identity, TransformMatrix.Identity));
        Assert.Contains("reference finger bases are collinear", width.Message);
    }

    [Fact]
    public void PreservesManualGripUntilExplicitRecalibrationWithoutChangingSourceBones()
    {
        var imported = FbxModelAuthoringImporter.Import(
            BlenderFbxStrictValidationTests.CreateValidModelFixture(), "generic-grip.fbx");
        RiggingSession session = RiggingSessions.Create(imported.Package.Document, RigStudioEntryPath.RepairExistingRig);
        Guid parentId = session.Recipe.Entities[0].EntityId;
        session = session with
        {
            Recipe = session.Recipe with
            {
                Entities = session.Recipe.Entities.SetItem(0,
                    session.Recipe.Entities[0] with { Kind = RigNativeEntityKind.Bone }),
            },
        };
        CustomModelDocument source = imported.Package.Document with { RiggingSession = session };
        RigGripPlacementSolution solved = RigGripPlacementSolver.Solve(Palm(), Palm(),
            TransformMatrix.CreateTranslation(new(.6, .1, .2)), TransformMatrix.Identity);
        RigGripHelperPlacement placement = new(parentId, null, "hand_grip", "grip.left", solved,
            Vector3D.Zero, new(.01, .01, .01));
        CustomModelDocument automatic = RigGripHelperAuthoring.Apply(source, session.CreateJobToken(), [placement]);
        Assert.Same(automatic, RigGripHelperAuthoring.Apply(automatic,
            automatic.RiggingSession!.CreateJobToken(), [placement]));
        Guid helper = Assert.Single(automatic.AuthoredHelpers).Id;
        TransformMatrix adjusted = TransformMatrix.CreateTranslation(new(.4, -.1, .1));
        CustomModelDocument manual = RigGripHelperAuthoring.SetLocalFrame(automatic,
            automatic.RiggingSession!.CreateJobToken(), helper, adjusted);

        CustomModelDocument kept = RigGripHelperAuthoring.Apply(manual, manual.RiggingSession!.CreateJobToken(), [placement]);
        Assert.Same(manual, kept);
        Assert.Equal(adjusted, Assert.Single(kept.RiggingSession!.Recipe.Helpers).LocalFrame);
        CustomModelDocument recalibrated = RigGripHelperAuthoring.Apply(manual,
            manual.RiggingSession!.CreateJobToken(), [placement], recalibrateManualPlacements: true);
        HelperRecipe recipe = Assert.Single(recalibrated.RiggingSession!.Recipe.Helpers);
        Assert.Equal(helper, recipe.EntityId);
        Assert.Equal(RigFramePolicy.Socket, recipe.FramePolicy);
        Assert.Equal(RigEvidenceKind.GeometryInference, recipe.PlacementProvenance);
        Assert.Equal(solved.LocalFrame, recipe.LocalFrame);
        Assert.Equal(source.Bones, recalibrated.Bones);
        Assert.Equal(source.Meshes, recalibrated.Meshes);
    }

    private static RigPalmLandmarks Palm() => new(Vector3D.Zero,
        new(1, .3, 0), new(1, 0, 0), new(1, -.3, 0), new(1, -.6, 0));
}

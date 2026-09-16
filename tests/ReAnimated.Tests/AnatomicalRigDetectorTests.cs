using System.Collections.Immutable;
using ReAnimated.Core.Geometry;
using ReAnimated.Core.Mathematics;
using ReAnimated.Retargeting.Geometry;

namespace ReAnimated.Tests;

public sealed class AnatomicalRigDetectorTests
{
    [Theory]
    [InlineData(AnatomicalFixturePose.TPose, false, false)]
    [InlineData(AnatomicalFixturePose.APose, true, true)]
    public void UnriggedBipedGetsGeometryDerivedBodyAndLimbProposals(AnatomicalFixturePose pose, bool bentElbows, bool bentKnees)
    {
        var fixture = AnatomicalVolumeFixtures.Create(new() { Pose = pose, BentElbows = bentElbows, BentKnees = bentKnees });
        var volume = SourceGeometryVolume.Build(fixture.Geometry);
        Assert.False(volume.HasUnreliableTopology);
        var grid = SourceVolumeGrid.Build(volume, new() { LongestAxisCells = 64 });
        Assert.True(grid.HasCompleteField);
        var result = AnatomicalRigDetector.Detect(grid, new() { Frame = new() { Left = -Vector3D.UnitX } });
        Assert.True(result.Status == AnatomicalDetectionStatus.Proposed, string.Join("\n", result.Diagnostics.Select(d => $"{d.Code}: {d.Role}: {d.Message}")));
        Assert.Equal(grid.CellCount, result.Regions.Length);
        Assert.True(result.RequiresReview);
        Assert.Equal(grid.InputFingerprint, result.GridFingerprint);
        Assert.All(result.Joints, joint => Assert.True(joint.Position.IsFinite));
        Check("body.pelvis", "pelvis", .35);
        Check("body.head", "head", .3);
        Check("arm.left.upper", "left_shoulder", .35);
        Check("arm.right.upper", "right_shoulder", .35);
        Check("arm.left.lower", "left_elbow", .4);
        Check("arm.right.lower", "right_elbow", .4);
        Check("hand.left", "left_wrist", .35);
        Check("hand.right", "right_wrist", .35);
        Check("leg.left.lower", "left_knee", .4);
        Check("leg.right.lower", "right_knee", .4);
        Check("foot.left", "left_ankle", .3);
        Check("foot.right", "right_ankle", .3);
        Assert.Contains(result.Regions, r => r == AnatomicalRegion.LeftArm);
        Assert.Contains(result.Regions, r => r == AnatomicalRegion.RightLeg);
        Assert.Contains(result.Connections, c => c.ParentRole == "arm.left.upper" && c.ChildRole == "arm.left.lower");
        Assert.All(result.Joints.Where(static j => j.SampleCell is not null), j => {
            var c = j.SampleCell!.Value;
            Assert.Equal(SourceVolumeLocation.Interior, grid.GetCell(c.X, c.Y, c.Z).Location);
        });
        void Check(string role, string expectedRole, double tolerance)
        {
            var joint = Assert.Single(result.Joints, j => j.Role == role);
            double error = (joint.Position - fixture.ExpectedJointCoordinates[expectedRole]).Length;
            Assert.True(error < tolerance, $"{role}: error {error:0.000}; proposed {joint.Position}; authored {fixture.ExpectedJointCoordinates[expectedRole]}\n" +
                string.Join("\n", result.Joints.Select(j => $"{j.Role}: {j.Position.X:0.00}, {j.Position.Y:0.00}, {j.Position.Z:0.00} ({j.Method})")));
        }
    }

    [Fact]
    public void UnsupportedShapeDoesNotBecomeABipedAndLockedGuidesRemainExact()
    {
        var grid = SourceVolumeGrid.Build(SourceGeometryVolume.Build(SourceGeometryVolumeTests.Analysis(SourceGeometryVolumeTests.Box("box"))), new() { LongestAxisCells = 8 });
        var point = new Vector3D(.123, .234, .345);
        var result = AnatomicalRigDetector.Detect(grid, new() { Guides = [new("body.pelvis", point)] });
        Assert.Equal(AnatomicalDetectionStatus.NeedsAssistance, result.Status);
        var retained = Assert.Single(result.Joints);
        Assert.True(retained.Locked);
        Assert.Equal(point, retained.Position);
        Assert.Contains(result.Diagnostics, d => d.Code == "bilateral_legs_unresolved");
        Assert.ThrowsAny<OperationCanceledException>(() => AnatomicalRigDetector.Detect(grid, cancellationToken: new(true)));
        Assert.Throws<ArgumentException>(() => AnatomicalRigDetector.Detect(grid, new() { Frame = new() { Up = Vector3D.UnitX, Left = Vector3D.UnitX } }));
    }

    [Fact]
    public void ScalingAndTranslationFollowObservedGeometryAndLocksConstrainTheDraft()
    {
        var fixture = AnatomicalVolumeFixtures.Create(new() { UnequalSegmentProportions = true });
        var grid = SourceVolumeGrid.Build(SourceGeometryVolume.Build(fixture.Geometry), new() { LongestAxisCells = 48 });
        var options = new AnatomicalDetectionOptions { Frame = new() { Left = -Vector3D.UnitX } };
        var baseline = AnatomicalRigDetector.Detect(grid, options);
        Assert.Equal(AnatomicalDetectionStatus.Proposed, baseline.Status);
        var offset = new Vector3D(8, -3, 5);
        var scaledFixture = AnatomicalVolumeFixtures.Create(new() { UnequalSegmentProportions = true, UniformScale = 2, RigidTransform = TransformMatrix.CreateTranslation(offset) });
        var scaledGrid = SourceVolumeGrid.Build(SourceGeometryVolume.Build(scaledFixture.Geometry), new() { LongestAxisCells = 48 });
        var scaled = AnatomicalRigDetector.Detect(scaledGrid, options);
        Assert.Equal(AnatomicalDetectionStatus.Proposed, scaled.Status);
        foreach (var joint in baseline.Joints)
        {
            var other = Assert.Single(scaled.Joints, j => j.Role == joint.Role);
            Assert.True((other.Position - (joint.Position * 2 + offset)).Length <= scaledGrid.CellSize * 2.1,
                $"{joint.Role}: {joint.Position} {joint.Method}; transformed {other.Position} {other.Method}; cell {scaledGrid.CellSize}\n" +
                $"grids {grid.SizeX},{grid.SizeY},{grid.SizeZ} / {scaledGrid.SizeX},{scaledGrid.SizeY},{scaledGrid.SizeZ}; inside {grid.InteriorCellCount}/{scaledGrid.InteriorCellCount}\n" +
                string.Join("\n", baseline.Joints.Select(j => $"base {j.Role}: {j.Position.X:0.00}, {j.Position.Y:0.00}, {j.Position.Z:0.00}")) + "\n" +
                string.Join("\n", baseline.Diagnostics.Select(d => $"base {d.Code}: {d.Message}")) + "\n" + string.Join("\n", scaled.Diagnostics.Select(d => $"scaled {d.Code}: {d.Message}")));
        }
        var knee = fixture.ExpectedJointCoordinates["left_knee"];
        var guided = AnatomicalRigDetector.Detect(grid, options with { Guides = [new("leg.left.lower", knee)] });
        var locked = Assert.Single(guided.Joints, j => j.Role == "leg.left.lower");
        Assert.Equal(knee, locked.Position);
        Assert.True(locked.Locked);
        Assert.Equal(AnatomicalPlacementMethod.UserGuide, locked.Method);
        Assert.NotEqual(baseline.InputFingerprint, guided.InputFingerprint);
        var outside = AnatomicalRigDetector.Detect(grid, options with { Guides = [new("hand.left", new(100, 100, 100))] });
        Assert.Equal(AnatomicalDetectionStatus.NeedsAssistance, outside.Status);
        Assert.Equal(new Vector3D(100, 100, 100), outside.Joints.Single(j => j.Role == "hand.left").Position);
    }
}

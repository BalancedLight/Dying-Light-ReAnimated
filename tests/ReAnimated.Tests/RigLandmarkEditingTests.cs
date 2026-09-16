using ReAnimated.Core.Mathematics;
using ReAnimated.Core.ModelAuthoring;

namespace ReAnimated.Tests;

public sealed class RigLandmarkEditingTests
{
    [Fact]
    public void PreviewIsTransientAndCommitInvalidatesFitWithStableIds()
    {
        var session = Session();
        var selected = session.Landmarks[0];
        Assert.True(RigLandmarkEditing.TryBeginMove(session, selected.Id, false, out var move));
        var delta = new Vector3D(.1, .2, .3);
        var preview = move!.Preview(delta);
        Assert.Equal(selected.Position + delta, preview[selected.Id]);
        Assert.Equal(selected, session.Landmarks[0]);
        Assert.True(RigLandmarkEditing.TryCommitMove(session, move, delta, out var changed));
        Assert.Equal(selected.Id, changed.Landmarks[0].Id);
        Assert.Equal(selected.Position + delta, changed.Landmarks[0].Position);
        Assert.False(changed.Landmarks[0].UserApproved);
        Assert.Equal(session.Revision + 1, changed.Revision);
        Assert.False(RigLandmarkEditing.TryCommitMove(changed, move, delta, out _));
    }

    [Fact]
    public void MirroringUsesTheDeclaredPlaneAndRefusesPinnedPartners()
    {
        var session = Session();
        session = session with { SymmetryOrigin = new(1, 2, 3), SymmetryNormal = new(1, 1, 0) };
        Assert.True(RigLandmarkEditing.TryBeginMove(session, session.Landmarks[0].Id, true, out var move));
        var point = session.Landmarks[0].Position + Vector3D.UnitZ;
        var expected = point - session.SymmetryNormal.Normalized() * (2 * Vector3D.Dot(point - session.SymmetryOrigin, session.SymmetryNormal.Normalized()));
        Assert.Equal(expected, move!.Preview(Vector3D.UnitZ)[session.Landmarks[1].Id]);
        var pinned = RigLandmarkEditing.SetLocked(session, session.Landmarks[1].Id, true);
        Assert.False(RigLandmarkEditing.TryBeginMove(pinned, pinned.Landmarks[0].Id, true, out _));
        Assert.False(RigLandmarkEditing.TryBeginMove(pinned, pinned.Landmarks[1].Id, false, out _));
        Assert.False(RigLandmarkEditing.TryCommitMove(pinned, move, Vector3D.UnitZ, out _));
    }

    [Fact]
    public void CancelledOrStaleMovesCannotChangeSourceAndUnpinIsExplicit()
    {
        var session = Session();
        Assert.True(RigLandmarkEditing.TryBeginMove(session, session.Landmarks[0].Id, false, out var move));
        _ = move!.Preview(Vector3D.UnitX); // Discarding the preview is cancellation; no session mutation occurred.
        Assert.Equal(new Vector3D(2, 3, 4), session.Landmarks[0].Position);
        var restored = RiggingSessions.RestoreForUndo(session, session);
        Assert.False(RigLandmarkEditing.TryCommitMove(restored, move, Vector3D.UnitX, out _));
        var pinned = RigLandmarkEditing.SetLocked(session, session.Landmarks[0].Id, true);
        var unpinned = RigLandmarkEditing.SetLocked(pinned, pinned.Landmarks[0].Id, false);
        Assert.Equal(session.Landmarks[0].Position, unpinned.Landmarks[0].Position);
        Assert.True(RigLandmarkEditing.TryBeginMove(unpinned, unpinned.Landmarks[0].Id, false, out var next));
        Assert.Throws<ArgumentException>(() => next!.Preview(new(double.NaN, 0, 0)));
    }

    private static RiggingSession Session()
    {
        var model = AnatomicalDetectionWorkflowTests.CreateUnriggedModel();
        var session = RiggingSessions.Create(model.Package.Document, RigStudioEntryPath.AutoRigBiped);
        Guid left = Guid.NewGuid(), right = Guid.NewGuid();
        return session with { Landmarks = [new() { Id = left, RoleId = "hand.left", Position = new(2, 3, 4), MirrorPartnerId = right, UserApproved = true },
            new() { Id = right, RoleId = "hand.right", Position = new(0, 3, 4), MirrorPartnerId = left }] };
    }

    [Fact]
    public void SmallPlaneNormalAndNoMovementPreserveAsymmetricSourcePositions()
    {
        var session = Session() with { SymmetryNormal = new(1e-14, 0, 0) };
        session.Validate();
        Assert.True(RigLandmarkEditing.TryBeginMove(session, session.Landmarks[0].Id, true, out var move));
        Assert.Equal(Vector3D.UnitX, move!.PlaneNormal);
        Assert.True(RigLandmarkEditing.TryCommitMove(session, move, Vector3D.Zero, out var unchanged));
        Assert.Same(session, unchanged);
        Assert.Equal(session.Landmarks[1].Position, move.Preview(Vector3D.Zero)[session.Landmarks[1].Id]);
        var oneWay = session with { Landmarks = session.Landmarks.SetItem(1, session.Landmarks[1] with { MirrorPartnerId = null }) };
        oneWay.Validate();
        Assert.False(RigLandmarkEditing.TryBeginMove(oneWay, oneWay.Landmarks[0].Id, true, out _));
        Assert.True(RigLandmarkEditing.TryBeginMove(oneWay, oneWay.Landmarks[0].Id, false, out _));
    }
}

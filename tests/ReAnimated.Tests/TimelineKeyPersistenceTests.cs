using System.Collections.Immutable;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

public sealed class TimelineKeyPersistenceTests
{
    [Fact]
    public void EditingOneTransformCurvePreservesOtherComponents()
    {
        TransformTRS original = new(
            new Vector3D(1, 2, 3),
            new QuaternionD(0, 0, 0, 1),
            new Vector3D(1.2, 1.3, 1.4));

        TransformTRS edited = MainWindowViewModel.ApplyTransformComponent(
            original, "Translation Y", 7.5);

        Assert.Equal(new Vector3D(1, 7.5, 3), edited.Translation);
        Assert.Equal(original.Rotation, edited.Rotation);
        Assert.Equal(original.Scale, edited.Scale);
    }

    [Fact]
    public void RotationKeysNormalizeAndInvalidScaleIsRejected()
    {
        TransformTRS original = new(
            new Vector3D(0.2, 0.3, 0.4),
            new QuaternionD(0.1, 0.2, 0.3, 0.9),
            new Vector3D(1, 1, 1));
        TransformTRS edited = MainWindowViewModel.ApplyTransformComponent(
            original, "Rotation W", 0.5);

        TransformTRS keyed = new TransformKeyframe(4, edited).Value;
        Assert.Equal(1, keyed.Rotation.LengthSquared, 10);
        Assert.Equal(original.Translation, keyed.Translation);
        Assert.Equal(original.Scale, keyed.Scale);

        TransformTRS invalid = edited with { Scale = new Vector3D(0, 1, 1) };
        Assert.Throws<InvalidOperationException>(
            () => MainWindowViewModel.ValidateEditedTransform(invalid));
    }

    [Fact]
    public void MovingAKeyRejectsCollisionsButAllowsAFreeFrame()
    {
        double[] frames = [2, 5, 9];

        Assert.Throws<InvalidOperationException>(
            () => MainWindowViewModel.RejectFrameCollision(frames, movingIndex: 1, destination: 9));
        MainWindowViewModel.RejectFrameCollision(frames, movingIndex: 1, destination: 7);
    }

    [Fact]
    public void DeletingTheLastBoneKeyRemovesOnlyThatTrack()
    {
        BoneEditTrack remove = new(2, [new TransformKeyframe(0, TransformTRS.Identity)]);
        BoneEditTrack preserve = new(4,
        [
            new TransformKeyframe(0, TransformTRS.Identity),
            new TransformKeyframe(8, TransformTRS.Identity with
            {
                Translation = new Vector3D(2, 0, 0),
            }),
        ]);

        ImmutableArray<BoneEditTrack> result = MainWindowViewModel.RemoveBoneKey(
            [remove, preserve], trackIndex: 0, keyIndex: 0);

        BoneEditTrack only = Assert.Single(result);
        Assert.Equal(preserve.BoneIndex, only.BoneIndex);
        Assert.Equal(preserve.Keyframes, only.Keyframes);
    }

    [Fact]
    public void DeletingOneMorphKeyLeavesTheRemainingKeysSorted()
    {
        MorphEditTrack track = new("mouth_open",
        [
            new ScalarKeyframe(0, 0),
            new ScalarKeyframe(5, 0.5),
            new ScalarKeyframe(9, 1),
        ]);

        ImmutableArray<MorphEditTrack> result = MainWindowViewModel.RemoveMorphKey(
            [track], trackIndex: 0, keyIndex: 1);

        Assert.Equal(new double[] { 0, 9 }, Assert.Single(result).Keyframes.Select(static key => key.Frame));
    }
}


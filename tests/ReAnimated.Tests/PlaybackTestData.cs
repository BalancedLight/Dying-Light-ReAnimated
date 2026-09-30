using System.Reflection;
using ReAnimated.App.ViewModels;
using ReAnimated.Core.Domain;
using ReAnimated.Core.Mathematics;

namespace ReAnimated.Tests;

internal static class PlaybackTestData
{
    public static void SetEvaluableSource(MainWindowViewModel owner, int frameCount = 2)
    {
        var rig = new RigDefinition("synthetic", "Synthetic",
            [new BoneDefinition(0, "root", -1, TransformTRS.Identity, BoneKind.Root)]);
        var clip = new AnimationClip("Generic motion", new FrameRate(30, 1), frameCount,
            [new TransformTrack(0,
                [new TransformKeyframe(0, TransformTRS.Identity),
                 new TransformKeyframe(frameCount - 1,
                     new TransformTRS(new Vector3D(0.05, 0, 0), QuaternionD.Identity, Vector3D.One))])]);
        Type sessionType = typeof(MainWindowViewModel).GetNestedType(
            "ImportedAnimationSession", BindingFlags.NonPublic)!;
        object session = Activator.CreateInstance(sessionType, rig, clip,
            "synthetic://playback", "Synthetic")!;
        typeof(MainWindowViewModel).GetField("_sourceAnimation",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, session);
    }
}

using ReAnimated.Core.Domain;

namespace ReAnimated.Evaluation;

/// <summary>Separates physical model motion from view-only first-person presentation corrections.</summary>
public static class SecondaryMotionDomain
{
    public static SkeletonPose SelectPhysicsPose(EvaluationFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.PreviewProfile.Context == Dl1PreviewContext.Dl1Fpp
            ? frame.AuthoredPose
            : frame.DisplayPose;
    }
}

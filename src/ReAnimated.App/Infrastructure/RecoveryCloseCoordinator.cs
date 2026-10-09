namespace ReAnimated.App.Infrastructure;

public enum RecoveryCloseDecision
{
    Retry,
    SaveElsewhere,
    Cancel,
}

public static class RecoveryCloseCoordinator
{
    public static async Task<bool> TryCloseAsync(Func<Task<bool>> saveRecovery,
        Func<RecoveryCloseDecision> choose, Func<Task<bool>> saveElsewhere)
    {
        ArgumentNullException.ThrowIfNull(saveRecovery);
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(saveElsewhere);
        while (!await saveRecovery())
        {
            switch (choose())
            {
                case RecoveryCloseDecision.Retry:
                    break;
                case RecoveryCloseDecision.SaveElsewhere:
                    if (await saveElsewhere()) return true;
                    break;
                case RecoveryCloseDecision.Cancel:
                    return false;
                default:
                    throw new InvalidOperationException("The recovery closing choice is invalid.");
            }
        }
        return true;
    }
}

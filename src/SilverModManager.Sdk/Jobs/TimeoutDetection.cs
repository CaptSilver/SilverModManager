namespace SilverModManager.Sdk.Jobs;

/// <summary>
/// Tells a timeout apart from a cancellation. HttpClient reports its own timeout as an
/// <see cref="OperationCanceledException"/>, so the type alone can't say who gave up.
/// </summary>
public static class TimeoutDetection
{
    /// <summary>
    /// Whether a <see cref="TimeoutException"/> is the exception or anywhere in its inner chain.
    /// </summary>
    public static bool HasTimeoutInChain(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
        {
            if (exception is TimeoutException) return true;
        }

        return false;
    }

    /// <summary>
    /// Whether this exception is a cancellation someone asked for, as opposed to a timeout that merely looks like one.
    /// </summary>
    public static bool IsRealCancellation(Exception exception) => exception is OperationCanceledException && !HasTimeoutInChain(exception);
}

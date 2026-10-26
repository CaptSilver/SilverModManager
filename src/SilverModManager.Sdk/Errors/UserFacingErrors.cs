using System.Reflection;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace SilverModManager.Sdk.Errors;

/// <summary>
/// Tells refusals (<see cref="UserFacingException"/>) apart from crashes.
/// </summary>
[PublicAPI]
public static class UserFacingErrors
{
    /// <summary>
    /// Returns the refusal to show for <paramref name="exception"/>, or null when it is a crash.
    /// </summary>
    /// <remarks>
    /// Every leaf of an <see cref="AggregateException"/> must be a refusal, at any depth: one bug among the
    /// refusals makes the whole thing a crash. <see cref="TargetInvocationException"/> is looked through, since
    /// reflection-invoked verbs wrap what they throw. <see cref="TypeInitializationException"/> is not: a failing
    /// static constructor is a bug even when it threw a refusal. With several distinct leaves the result joins their
    /// messages, each once, and takes the first leaf's title and action.
    /// </remarks>
    public static UserFacingException? Find(Exception? exception)
    {
        if (exception is null) return null;

        var collected = new List<UserFacingException>();
        if (!CollectLeaves(exception, collected) || collected.Count == 0) return null;

        // Task.WhenAll over a shared task rethrows the same instance once per awaiter.
        var leaves = collected.Distinct(ReferenceEqualityComparer.Instance).Cast<UserFacingException>().ToList();
        if (leaves.Count == 1) return leaves[0];

        return new JoinedRefusal(
            string.Join(Environment.NewLine, leaves.Select(l => l.Message).Distinct(StringComparer.Ordinal)),
            leaves[0].Title,
            leaves[0].Action
        );
    }

    /// <summary>
    /// Logs a caught failure at Warning when it is a refusal and at Error when it is a crash. A refusal is
    /// something the player can act on; only a crash should raise an error signal.
    /// </summary>
    public static void LogFailure(this ILogger logger, Exception exception, string message, params object?[] args)
    {
        logger.Log(Find(exception) is null ? LogLevel.Error : LogLevel.Warning, exception, message, args);
    }

    private static bool CollectLeaves(Exception exception, List<UserFacingException> leaves)
    {
        switch (exception)
        {
            case UserFacingException refusal:
                leaves.Add(refusal);
                return true;
            case AggregateException aggregate:
                return aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(e => CollectLeaves(e, leaves));
            case TargetInvocationException { InnerException: { } inner }:
                return CollectLeaves(inner, leaves);
            default:
                return false;
        }
    }

    private sealed class JoinedRefusal : UserFacingException
    {
        public JoinedRefusal(string message, string title, UserFacingAction? action) : base(message)
        {
            Title = title;
            Action = action;
        }

        public override string Title { get; }
    }
}

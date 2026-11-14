using SilverModManager.Abstractions.Secrets;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Accounts;

/// <summary>An account row's button could not do what it says, for a reason the user can act on.</summary>
public sealed class AccountActionRefusedException(string message) : UserFacingException(message)
{
    public override string Title => "The account was not changed";

    /// <summary>Turns a write the secret store did not accept into the refusal, and passes a written one.</summary>
    public static void ThrowUnlessWritten(SecretWriteResult result, string doing)
    {
        switch (result)
        {
            case SecretWriteResult.Written:
                return;
            case SecretWriteResult.Unavailable unavailable:
                throw new AccountActionRefusedException($"Could not {doing}: {unavailable.Reason}");
            case SecretWriteResult.ReadOnly readOnly:
                throw new AccountActionRefusedException($"Could not {doing}: it is supplied by {readOnly.Source} and cannot be changed here.");
            default:
                throw new InvalidOperationException("Unknown secret write result");
        }
    }
}

using NexusMods.Abstractions.Cli;
using NexusMods.Abstractions.Secrets;
using NexusMods.Sdk.ProxyConsole;

namespace NexusMods.Games.FromSoft.EldenRing;

/// <summary>
/// Sets or clears the Seamless Co-op password in the secret store. The password comes from stdin, never from an
/// option: the argument list is logged.
/// </summary>
internal static class CoopPasswordVerb
{
    [Verb("fromsoft-coop-password", "Stores the Seamless Co-op password read from stdin in the secret store, or removes it")]
    internal static async Task<int> CoopPassword(
        [Injected] IRenderer renderer,
        [Injected] ISecretStore store,
        [Option("c", "clear", "Remove the stored password instead of setting one", true)] bool clear,
        [Injected] CancellationToken token)
        => await Run(renderer, store, Console.In, clear, token);

    internal static async Task<int> Run(IRenderer renderer, ISecretStore store, TextReader input, bool clear, CancellationToken token)
    {
        SecretWriteResult result;
        if (clear)
        {
            result = await store.DeleteAsync(FromSoftSecretKeys.CoopPassword, token);
        }
        else
        {
            var password = (await input.ReadLineAsync(token))?.Trim();
            if (string.IsNullOrEmpty(password))
            {
                // With the app open the verb runs in the app's process, which never sees this terminal's stdin.
                await renderer.TextLine("No password on stdin. Pipe it in, e.g. `silvermod fromsoft-coop-password < password.txt`, with the app closed.");
                return 1;
            }

            result = await store.SetAsync(FromSoftSecretKeys.CoopPassword, password, token);
        }

        switch (result)
        {
            case SecretWriteResult.Written:
                await renderer.TextLine(clear ? "The stored Seamless Co-op password was removed." : "The Seamless Co-op password was stored.");
                return 0;
            case SecretWriteResult.ReadOnly readOnly:
                await renderer.TextLine($"The Seamless Co-op password is provided by {readOnly.Source} and cannot be changed here.");
                return 1;
            case SecretWriteResult.Unavailable unavailable:
                await renderer.TextLine($"The secret store is not available: {unavailable.Reason}");
                return 1;
            default:
                throw new InvalidOperationException("Unknown secret write result");
        }
    }
}

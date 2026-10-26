using System.Net.Sockets;
using SilverModManager.SingleProcess;
using SilverModManager.SingleProcess.Exceptions;

namespace SilverModManager.App.Mcp;

/// <summary>How one verb call is run and its outcome read.</summary>
/// <param name="Timeout">How long to wait for the main process before giving up on the answer.</param>
/// <param name="ReportsExitOne">
/// True for a tool whose verb exits 1 to say "the loadout has a problem" and still prints a valid report: that is a
/// result, not a failure of the tool.
/// </param>
/// <param name="MaxChars">The most text to return.</param>
/// <param name="EmptyText">What to return when the verb succeeds but prints nothing.</param>
public sealed record VerbCall(TimeSpan Timeout, bool ReportsExitOne, int MaxChars, string? EmptyText = null);

/// <summary>Turns what a verb printed and how it ended into a tool result.</summary>
public static class VerbResults
{
    public const string NotRunning = "SilverModManager is not running. Start the SilverModManager window and call again.";

    public static ToolResult Map(int? exitCode, string output, VerbCall call)
    {
        var text = Cap(output, call.MaxChars);
        switch (exitCode)
        {
            case null:
                // Nothing says the verb finished, so whatever it printed may be only the start of it.
                return new ToolResult(AfterOutput(text, "SilverModManager closed the connection before the command finished."), true);
            case 0:
                return new ToolResult(string.IsNullOrWhiteSpace(text) ? call.EmptyText ?? "" : text, false);
            case 1 when call.ReportsExitOne:
                return new ToolResult(AfterOutput(text, "") + "exit code 1", false);
            default:
                return new ToolResult(string.IsNullOrWhiteSpace(text) ? $"The command ended with exit code {exitCode} and printed nothing." : text, true);
        }
    }

    /// <summary>Cuts <paramref name="text"/> to <paramref name="maxChars"/> and says so.</summary>
    public static string Cap(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        var keep = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return $"{text[..keep]}\n[output cut at {maxChars} characters]\n";
    }

    private static string AfterOutput(string text, string line) =>
        text.Length == 0 ? line : text + (text.EndsWith('\n') ? "\n" : "\n\n") + line;
}

/// <summary>
/// Runs verbs in the running window through a <see cref="CliClient"/>, one connection per call, and words every way
/// that can go wrong as a sentence.
/// </summary>
public sealed class VerbRunner(CliClient client)
{
    public async Task<ToolResult> RunAsync(string[] args, VerbCall call, CancellationToken token)
    {
        var renderer = new CapturingRenderer();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(call.Timeout);
        try
        {
            var result = await client.ExecuteCommand(args, renderer, timeout.Token);
            return VerbResults.Map(result.ExitCode, renderer.Output, call);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var seconds = (int)Math.Ceiling(call.Timeout.TotalSeconds);
            return new ToolResult($"SilverModManager did not answer within {seconds} s; the command may still be running in the app.", true);
        }
        catch (NoMainProcessStarted)
        {
            return new ToolResult(VerbResults.NotRunning, true);
        }
        catch (HeadlessMainRunning e)
        {
            return new ToolResult(e.Message, true);
        }
        catch (Exception e) when (e is VersionMismatch or MainProcessSilent)
        {
            return new ToolResult(e.Message, true);
        }
        catch (SocketException)
        {
            return new ToolResult("SilverModManager is running but did not answer. Close it and start it again if this keeps happening.", true);
        }
    }
}

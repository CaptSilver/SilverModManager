using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Games.RedEngine.Tests;

/// <summary>
/// The script the deploy tool runs through Wine. It has no console to press a key in, so a <c>pause</c> on an error
/// path would leave the deploy waiting until something killed it.
/// </summary>
public class RedModDeployScriptTests
{
    private static string Script()
    {
        using var stream = typeof(RedModDeployTool).Assembly.GetManifestResourceStream("deploy_redmod.bat")
                           ?? throw new InvalidOperationException("deploy_redmod.bat is not embedded in the RedEngine assembly.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheScriptNeverWaitsForAKeyPress()
    {
        Regex.IsMatch(Script(), @"^\s*pause\b", RegexOptions.Multiline | RegexOptions.IgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void EveryErrorPathStillEndsInAFailingExitCode()
    {
        var script = Script();

        script.Should().Contain("echo Error: STEAM_APP_PATH environment variable is not set.");
        Regex.Matches(script, @"^\s*exit /b 1\s*$", RegexOptions.Multiline).Count.Should().Be(2, "each of the two checks reports failure to the app through its exit code");
    }
}

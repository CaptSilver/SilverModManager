using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// MSBuild and the test hosts leave folders in TMPDIR, and /tmp is RAM on the dev machine. Scripts that run
/// dotnet must point TMPDIR at a per-run folder they delete, or those folders accumulate until the machine runs
/// out of memory. Every script also turns off the shared compiler server: a per-run TMPDIR hides the server's
/// socket from later builds, so a server left running could never be reused and would only hold memory.
/// </summary>
public partial class ScriptTempDirectoryGuardTests
{
    private static string Repo(string relative) => Path.Combine(RepoScanGuard.FindRepoRoot(), relative);

    [GeneratedRegex(@"(^|[\s(!;&|])dotnet\s")]
    private static partial Regex DotnetCall();

    private static string[] CodeLines(string script) =>
        File.ReadAllLines(Repo(script)).Where(line => !line.TrimStart().StartsWith('#')).ToArray();

    /// <summary>Runs a bash snippet with TMPDIR set, and returns what it printed.</summary>
    private static string Bash(string snippet, string tmpdir)
    {
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(snippet);
        start.Environment["TMPDIR"] = tmpdir;
        start.Environment.Remove("UseSharedCompilation");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, error);
        return output.Trim();
    }

    private static string OwnedParent()
    {
        var parent = TestScratch.Combine($"script-tmp-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(parent);
        return parent;
    }

    // Runs the real function: the folder must exist while the script runs, sit under the parent TMPDIR the caller
    // gave it, replace TMPDIR for the rest of the script, and be gone once the shell exits.
    [Fact]
    public void Test_ScratchTmpdirCreatesAFolderAndDeletesItWhenTheShellExits()
    {
        var parent = OwnedParent();
        try
        {
            var box = Repo("scripts/lib/box.sh");
            var scratch = Bash($"source '{box}'; scratch_tmpdir; [ -d \"$TMPDIR\" ] || exit 3; echo \"$TMPDIR\"", parent);

            scratch.Should().StartWith(parent + "/").And.NotBe(parent, "the script must get its own folder, not the caller's TMPDIR");
            Directory.Exists(scratch).Should().BeFalse("the EXIT trap in scratch_tmpdir must delete the folder");
            Directory.GetFileSystemEntries(parent).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Test_ScratchTmpdirFolderIsDeletedEvenWhenTheScriptFails()
    {
        var parent = OwnedParent();
        try
        {
            var box = Repo("scripts/lib/box.sh");
            Bash($"source '{box}'; ( scratch_tmpdir; touch \"$TMPDIR/leftover\"; exit 1 ) || true", parent);

            Directory.GetFileSystemEntries(parent).Should().BeEmpty("a failed script must not leave its folder behind");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void Test_ExportDotnetEnvTurnsOffTheSharedCompiler()
    {
        var parent = OwnedParent();
        try
        {
            var box = Repo("scripts/lib/box.sh");
            Bash($"source '{box}'; export_dotnet_env; echo \"$UseSharedCompilation\"", parent).Should().Be("false");
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    // scratch_tmpdir's trap is the only thing that deletes the folder, so a script that sets its own EXIT trap
    // after calling it has to delete $scratch_tmp as well.
    [Theory]
    [InlineData("scripts/dev-build.sh")]
    [InlineData("scripts/dev-setup.sh")]
    [InlineData("scripts/dev-package.sh")]
    public void Test_EveryExitTrapInADevScriptDeletesTheScratchFolder(string script)
    {
        var traps = CodeLines(script).Where(line => Regex.IsMatch(line, @"\btrap\b.*\bEXIT\b")).ToArray();

        // A script with no trap of its own keeps scratch_tmpdir's, so an empty list is fine.
        traps.Where(line => !line.Contains("$scratch_tmp")).Should().BeEmpty($"{script} would replace scratch_tmpdir's trap and leak the folder");
    }

    [Theory]
    [InlineData("scripts/dev-build.sh")]
    [InlineData("scripts/dev-setup.sh")]
    [InlineData("scripts/dev-package.sh")]
    public void Test_NoDotnetCallComesBeforeTheScratchTmpdirAndDotnetEnv(string script)
    {
        var lines = CodeLines(script);
        var scratch = Array.FindIndex(lines, line => line.Trim() == "scratch_tmpdir");
        var env = Array.FindIndex(lines, line => line.Trim() == "export_dotnet_env");
        var firstDotnet = Array.FindIndex(lines, line => DotnetCall().IsMatch(line));

        scratch.Should().BeGreaterThanOrEqualTo(0, $"{script} must call scratch_tmpdir");
        env.Should().BeGreaterThanOrEqualTo(0, $"{script} must call export_dotnet_env");
        firstDotnet.Should().BeGreaterThan(scratch, "TMPDIR must be redirected before any dotnet call");
        firstDotnet.Should().BeGreaterThan(env, "the build-server switches must be set before any dotnet call");
    }

    [Fact]
    public void Test_TestScriptRedirectsTmpdirBeforeBuilding()
    {
        var text = File.ReadAllText(Repo("scripts/test.sh"));

        var redirect = text.IndexOf("TMPDIR=\"$host_tmp_run\"", StringComparison.Ordinal);
        var build = text.IndexOf("dotnet build", StringComparison.Ordinal);
        redirect.Should().BePositive("test.sh must always give the run its own TMPDIR, even when /tmp is tmpfs");
        redirect.Should().BeLessThan(build, "MSBuildTemp folders are created by the build, not only by the tests");
        text.Should().Contain("rm -rf \"$xdg_root\" \"$scratch_root\" \"$host_tmp_run\"", "the per-run folder must be deleted at exit");
    }

    [Theory]
    [InlineData("scripts/test.sh")]
    [InlineData("scripts/dev-build.sh")]
    public void Test_BuildCommandTurnsOffTheSharedCompiler(string script)
    {
        var build = CodeLines(script).Single(line => line.Contains("dotnet build"));

        build.Should().Contain("-p:UseSharedCompilation=false", "a per-run TMPDIR means no later build can reuse a compiler server");
    }
}

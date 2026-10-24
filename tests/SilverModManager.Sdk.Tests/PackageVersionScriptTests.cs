using System.Diagnostics;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// <c>scripts/dev-package.sh</c> takes the packaged version from the nearest release tag.
/// A hard-coded default made every release report the same version, so the update checker
/// could not tell builds apart. The packaging itself is never run here; these tests source
/// <c>scripts/lib/app-version.sh</c> against throwaway git repositories.
/// </summary>
public class PackageVersionScriptTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "smm-version-" + Guid.NewGuid().ToString("N"));

    public PackageVersionScriptTests() => Directory.CreateDirectory(_repo);

    public void Dispose() => Directory.Delete(_repo, recursive: true);

    [Fact]
    public void Test_PackagingScriptDoesNotHardCodeADefaultVersion()
    {
        var script = File.ReadAllText(Path.Combine(RepoScanGuard.FindRepoRoot(), "scripts/dev-package.sh"));

        script.Should().NotContain("0.0.1", "a fixed default stamps every build with the same version");
        script.Should().Contain("app_version", "the version comes from the release tag helper");
    }

    [Fact]
    public void Test_ACommitOnTheTagIsStampedWithTheTagVersion()
    {
        Git("init", "-q");
        Commit("one");
        Git("tag", "v1.2.3");

        RunAppVersion().Should().Be((0, "1.2.3"));
    }

    [Fact]
    public void Test_CommitsPastTheTagGetADevSuffixCountingThem()
    {
        Git("init", "-q");
        Commit("one");
        Git("tag", "v1.2.3");
        Commit("two");
        Commit("three");

        RunAppVersion().Should().Be((0, "1.2.3-dev.2"));
    }

    [Fact]
    public void Test_OnlyReleaseTagsCount()
    {
        Git("init", "-q");
        Commit("one");
        Git("tag", "v1.2.3");
        Commit("two");
        Git("tag", "v1.2.4-rc1");
        Git("tag", "nightly");

        RunAppVersion().Should().Be((0, "1.2.3-dev.1"));
    }

    [Fact]
    public void Test_TheEnvironmentVariableOverridesTheTag()
    {
        Git("init", "-q");
        Commit("one");
        Git("tag", "v1.2.3");

        RunAppVersion(("SILVER_APP_VERSION", "9.9.9")).Should().Be((0, "9.9.9"));
    }

    [Fact]
    public void Test_NoTagFailsInsteadOfFallingBack()
    {
        Git("init", "-q");
        Commit("one");

        var (exitCode, output) = RunAppVersion();

        exitCode.Should().NotBe(0);
        output.Should().BeEmpty("a failed lookup must not print a version a caller could use");
    }

    private (int ExitCode, string Output) RunAppVersion(params (string Name, string Value)[] environment)
    {
        var libFile = Path.Combine(RepoScanGuard.FindRepoRoot(), "scripts/lib/app-version.sh");
        var info = new ProcessStartInfo("bash")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("set -euo pipefail; source \"$1\"; app_version \"$2\"");
        info.ArgumentList.Add("bash");
        info.ArgumentList.Add(libFile);
        info.ArgumentList.Add(_repo);
        info.Environment.Remove("SILVER_APP_VERSION");
        foreach (var (name, value) in environment) info.Environment[name] = value;

        using var process = Process.Start(info) ?? throw new InvalidOperationException("bash did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        stderr.Wait();
        return (process.ExitCode, stdout.Result.Trim());
    }

    private void Commit(string message) => Git("commit", "-q", "--allow-empty", "-m", message);

    private void Git(params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false" }.Concat(arguments))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("git did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, $"git {string.Join(' ', arguments)} failed: {stderr.Result}{stdout.Result}");
    }
}

using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Sdk;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests.Steam;

public class LauncherShimTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("silvermod-shim-").FullName;
    private readonly IFileSystem _fs = FileSystem.Shared;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AbsolutePath Path(string relative) => _fs.FromUnsanitizedFullPath(System.IO.Path.Combine(_root, relative));

    private LauncherShim Shim => new(Path("data"));

    [Fact]
    public void Test_ShimPathIsUnderDataHome()
    {
        Shim.ShimPath.ToString().Should().Be(System.IO.Path.Combine(_root, "data", "silvermod", "bin", "silvermod"));
    }

    [Fact]
    public void Test_WriteCreatesTwoLineScriptExecutingTarget()
    {
        var shim = Shim;
        shim.Write(Path("apps/SilverModManager.App")).Should().BeTrue();

        File.ReadAllText(shim.ShimPath.ToString()).Should().Be($"#!/bin/sh\nexec '{Path("apps/SilverModManager.App")}' \"$@\"\n");
        File.GetUnixFileMode(shim.ShimPath.ToString()).Should().Be((UnixFileMode)0b111_101_101);
    }

    [Fact]
    public void Test_WriteQuotesTargetWithSingleQuote()
    {
        var shim = Shim;
        shim.Write(Path("it's/app"));

        File.ReadAllText(shim.ShimPath.ToString()).Should().Contain(@"exec '" + _root + @"/it'\''s/app' ""$@""");
    }

    [Fact]
    public void Test_TargetsOnlyTheExecutableItWasWrittenFor()
    {
        var shim = Shim;
        shim.Targets(Path("app")).Should().BeFalse("the script does not exist yet");

        shim.Write(Path("app"));

        shim.Targets(Path("app")).Should().BeTrue();
        shim.Targets(Path("other")).Should().BeFalse();
    }

    [Fact]
    public void Test_WriteLeavesFileUntouchedWhenTargetIsSame()
    {
        var shim = Shim;
        shim.Write(Path("app"));
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(shim.ShimPath.ToString(), stamp);

        shim.Write(Path("app")).Should().BeFalse();

        File.GetLastWriteTimeUtc(shim.ShimPath.ToString()).Should().Be(stamp);
    }

    [Fact]
    public void Test_WriteRewritesWhenTargetChanges()
    {
        var shim = Shim;
        shim.Write(Path("old-app"));

        shim.Write(Path("new-app")).Should().BeTrue();

        File.ReadAllText(shim.ShimPath.ToString()).Should().Contain("new-app").And.NotContain("old-app");
    }

    [Fact]
    public void Test_WriteRestoresExecutableBitWhenContentIsCurrent()
    {
        var shim = Shim;
        shim.Write(Path("app"));
        File.SetUnixFileMode(shim.ShimPath.ToString(), (UnixFileMode)0b110_100_100);

        shim.Write(Path("app")).Should().BeTrue();

        File.GetUnixFileMode(shim.ShimPath.ToString()).Should().Be((UnixFileMode)0b111_101_101);
    }

    [Fact]
    public void Test_ShimPassesArgumentsAndExitCodeThrough()
    {
        var target = System.IO.Path.Combine(_root, "target.sh");
        File.WriteAllText(target, "#!/bin/sh\nfor a in \"$@\"; do echo \"[$a]\"; done\nexit 7\n");
        File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
        var shim = Shim;
        shim.Write(_fs.FromUnsanitizedFullPath(target));

        var info = new ProcessStartInfo("sh") { RedirectStandardOutput = true };
        info.ArgumentList.Add(shim.ShimPath.ToString());
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("two words");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        output.Should().Be("[run]\n[two words]\n");
        process.ExitCode.Should().Be(7);
    }

    [Fact]
    public void Test_ResolveDataHomeUsesAbsoluteXdgDataHome()
    {
        var env = new FakeEnvironment(new() { ["XDG_DATA_HOME"] = "/xdg/data", ["HOME"] = "/home/u" });
        LauncherShim.ResolveDataHome(env, new InMemoryFileSystem(OSInformation.FakeUnix)).ToString().Should().Be("/xdg/data");
    }

    [Theory]
    [InlineData("relative/data")]
    [InlineData("")]
    public void Test_ResolveDataHomeIgnoresRelativeOrEmptyXdgDataHome(string xdg)
    {
        var env = new FakeEnvironment(new() { ["XDG_DATA_HOME"] = xdg, ["HOME"] = "/home/u" });
        LauncherShim.ResolveDataHome(env, new InMemoryFileSystem(OSInformation.FakeUnix)).ToString().Should().Be("/home/u/.local/share");
    }

    [Fact]
    public void Test_ResolveDataHomeFallsBackWhenXdgUnset()
    {
        var env = new FakeEnvironment(new() { ["HOME"] = "/home/u" });
        LauncherShim.ResolveDataHome(env, new InMemoryFileSystem(OSInformation.FakeUnix)).ToString().Should().Be("/home/u/.local/share");
    }

    [Fact]
    public async Task Test_RefresherBakesInRunningExecutable()
    {
        var interop = Substitute.For<IOSInterop>();
        var exe = Path("apps/SilverModManager");
        interop.GetRunningExecutablePath(out Arg.Any<string>()).Returns(exe);
        var env = new FakeEnvironment(new() { ["XDG_DATA_HOME"] = System.IO.Path.Combine(_root, "xdg") });
        var refresher = new LauncherShimRefresher(interop, env, _fs, NullLogger<LauncherShimRefresher>.Instance);

        await refresher.StartAsync(CancellationToken.None);

        File.ReadAllText(System.IO.Path.Combine(_root, "xdg", "silvermod", "bin", "silvermod")).Should().Contain(exe.ToString());
    }

    [Fact]
    public void Test_AddProtonDoesNotRegisterRefresher_AddLauncherShimDoes()
    {
        var plain = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        plain.AddProton();
        plain.Any(d => d.ImplementationType == typeof(LauncherShimRefresher)).Should().BeFalse();

        var withShim = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        withShim.AddLauncherShim();
        withShim.Any(d => d.ImplementationType == typeof(LauncherShimRefresher)).Should().BeTrue();
    }

    private sealed class FakeEnvironment(Dictionary<string, string> values) : IEnvironmentReader
    {
        public string? Get(string name) => values.GetValueOrDefault(name);
    }
}

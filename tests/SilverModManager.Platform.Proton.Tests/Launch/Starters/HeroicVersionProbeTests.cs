using System.Text;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.Platform.Proton.Launch.Starters;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests.Launch.Starters;

public class HeroicVersionProbeTests
{
    private static IProcessRunner RunnerPrinting(string output, int exitCode = 0)
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>()).Returns(async call =>
        {
            await call.Arg<Command>().StandardOutputPipe.CopyFromAsync(new MemoryStream(Encoding.UTF8.GetBytes(output)));
            return FakeClientRunner.Result(exitCode);
        });
        return runner;
    }

    private static HeroicVersionProbe Probe(IProcessRunner runner) => new(runner, NullLogger<HeroicVersionProbe>.Instance);

    [Theory]
    [InlineData("2.16.1", "2.16.1")]
    [InlineData("Heroic 2.19.0\n", "2.19.0")]
    [InlineData("v2.18\n", "2.18")]
    public void Read_ParsesTheNativeBinarysVersionOutput(string output, string expected)
        => Probe(RunnerPrinting(output)).Read(flatpak: false).Should().Be(Version.Parse(expected));

    [Fact]
    public void Read_TakesTheVersionLineOfFlatpakInfoNotTheFirstNumber()
    {
        const string info = "Heroic Games Launcher - Launch your games\n\n          ID: com.heroicgameslauncher.hgl\n         Ref: app/com.heroicgameslauncher.hgl/x86_64/stable\n        Arch: x86_64\n      Branch: stable\n     Version: 2.19.1\n     Runtime: org.freedesktop.Platform/x86_64/24.08\n";

        Probe(RunnerPrinting(info)).Read(flatpak: true).Should().Be(new Version(2, 19, 1));
    }

    [Fact]
    public void Read_UsesFlatpakInfoForTheFlatpakAndTheBinaryOtherwise()
    {
        var runner = new FakeClientRunner();
        var probe = Probe(runner);

        probe.Read(flatpak: false);
        probe.Read(flatpak: true);

        runner.Commands.Select(c => (c.TargetFilePath, c.Arguments)).Should().Equal(
            ("heroic", "--version"),
            ("flatpak", "info com.heroicgameslauncher.hgl"));
    }

    [Fact]
    public void Read_IsNullWhenTheCommandFails()
        => Probe(RunnerPrinting("2.16.1", exitCode: 1)).Read(flatpak: false).Should().BeNull();

    [Fact]
    public void Read_IsNullWhenNothingParses()
        => Probe(RunnerPrinting("no version here")).Read(flatpak: false).Should().BeNull();

    [Fact]
    public void Read_IsNullWhenTheProgramCannotBeStarted()
    {
        var runner = Substitute.For<IProcessRunner>();
        runner.RunAsync(Arg.Any<Command>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<Action<int>?>())
            .Returns<Task<CommandResult>>(_ => throw new System.ComponentModel.Win32Exception("not found"));

        Probe(runner).Read(flatpak: false).Should().BeNull();
    }

    [Fact]
    public void Read_AsksOncePerKindEvenWhenTheAnswerWasNull()
    {
        var runner = new FakeClientRunner();
        var probe = Probe(runner);

        probe.Read(flatpak: false);
        probe.Read(flatpak: false);

        runner.Commands.Should().ContainSingle();
    }
}

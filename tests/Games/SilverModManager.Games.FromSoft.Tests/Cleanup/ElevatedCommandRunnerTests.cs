using System.ComponentModel;
using CliWrap;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Sdk;

namespace SilverModManager.Games.FromSoft.Tests.Cleanup;

/// <summary>Records the commands it is given and answers with a chosen exit code; nothing starts a process.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<Command> Commands { get; } = [];
    public int ExitCode { get; set; }
    public Exception? Throws { get; set; }
    public Action<Command>? OnRun { get; set; }

    public void Run(Command command, bool logOutput = false) => throw new NotSupportedException("The cleanup waits for its commands.");

    public Task<CommandResult> RunAsync(Command command, bool logOutput = false, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        if (Throws is not null) throw Throws;

        OnRun?.Invoke(command);
        return Task.FromResult(new CommandResult(ExitCode, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }

    public Task RunAsync(System.Diagnostics.Process process, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public class ElevatedCommandRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "elevated-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProcessRunner _process = new();

    public ElevatedCommandRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Game"));
        File.WriteAllText(LauncherPath, "x");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string GameFolder => Path.Combine(_root, "Game");
    private string LauncherPath => Path.Combine(_root, "Game", "start_protected_game.exe");

    private ElevatedCommandRunner Runner => new(_process, NullLogger<ElevatedCommandRunner>.Instance);

    [Fact]
    public async Task PkexecRunsChattrByItsAbsolutePathWithoutAShell()
    {
        var result = await Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(LauncherPath));

        result.Outcome.Should().Be(ElevatedOutcome.Succeeded);
        var command = _process.Commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be("pkexec");
        command.Arguments.Should().Be($"/usr/bin/chattr -i {LauncherPath}");
    }

    [Fact]
    public async Task APathWithSpacesAndQuotesStaysOneArgument()
    {
        var odd = Path.Combine(GameFolder, "it's a \"file\"; rm -rf.exe");
        File.WriteAllText(odd, "x");

        await Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(odd));

        var command = _process.Commands.Single();
        command.TargetFilePath.Should().Be("pkexec");
        // CliWrap quotes the one argument itself; the text stays a single path, never reaching a shell.
        command.Arguments.Should().StartWith("/usr/bin/chattr -i \"").And.Contain("it's a \\\"file\\\"; rm -rf.exe");
    }

    [Theory]
    [InlineData(0, ElevatedOutcome.Succeeded)]
    [InlineData(126, ElevatedOutcome.Cancelled)]
    [InlineData(127, ElevatedOutcome.Unavailable)]
    [InlineData(1, ElevatedOutcome.Unavailable)]
    public async Task ExitCodesMapToSuccessCancelAndFallback(int exitCode, ElevatedOutcome expected)
    {
        _process.ExitCode = exitCode;

        var result = await Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(LauncherPath));

        result.Outcome.Should().Be(expected);
        (result.Detail is null).Should().Be(expected is ElevatedOutcome.Succeeded or ElevatedOutcome.Cancelled);
    }

    [Fact]
    public async Task AMissingPkexecFallsBackInsteadOfThrowing()
    {
        _process.Throws = new Win32Exception(2, "No such file or directory");

        var result = await Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(LauncherPath));

        result.Outcome.Should().Be(ElevatedOutcome.Unavailable);
        result.Detail.Should().Contain("not installed");
    }

    public static TheoryData<string[]> Refused() => new()
    {
        new[] { "rm", "-i", "/f" },
        new[] { "chattr", "-R", "/f" },
        new[] { "chattr", "+i", "/f" },
        new[] { "chattr", "-i" },
        new[] { "chattr", "-i", "/f", "/g" },
        new[] { "sh", "-c", "chattr -i /f" },
        new[] { "/usr/bin/chattr", "-i", "/f" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task AnythingButChattrDashIOnOneFileIsRefusedBeforeAnythingStarts(string[] argv)
    {
        var inside = argv.Select(a => a == "/f" ? LauncherPath : a == "/g" ? Path.Combine(GameFolder, "g") : a).ToArray();

        var act = () => Runner.RunAsync(GameFolder, new PrivilegedCommand([..inside]));

        await act.Should().ThrowAsync<ArgumentException>();
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task APathOutsideTheGameFolderIsRefused()
    {
        var outside = Path.Combine(_root, "elsewhere.exe");
        File.WriteAllText(outside, "x");

        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(outside));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*inside the game folder*");
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ASiblingFolderWithTheSamePrefixIsOutside()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Game2"));

        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(Path.Combine(_root, "Game2", "a.exe")));

        await act.Should().ThrowAsync<ArgumentException>();
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ADotDotPathThatWalksOutIsRefused()
    {
        var sneaky = Path.Combine(GameFolder, "..", "elsewhere.exe");

        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(sneaky));

        await act.Should().ThrowAsync<ArgumentException>();
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ARelativePathIsRefused()
    {
        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable("start_protected_game.exe"));

        await act.Should().ThrowAsync<ArgumentException>();
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task ALinkInsideTheFolderIsRefusedBecauseChattrFollowsIt()
    {
        var outside = Path.Combine(_root, "elsewhere.exe");
        File.WriteAllText(outside, "x");
        var link = Path.Combine(GameFolder, "link.exe");
        File.CreateSymbolicLink(link, outside);

        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(link));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*link*");
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task AFileReachedThroughALinkedFolderIsRefused()
    {
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "a.exe"), "x");
        Directory.CreateSymbolicLink(Path.Combine(GameFolder, "sub"), outside);

        var act = () => Runner.RunAsync(GameFolder, PrivilegedCommand.ClearImmutable(Path.Combine(GameFolder, "sub", "a.exe")));

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*link*");
        _process.Commands.Should().BeEmpty();
    }

    [Fact]
    public void CommandsWithTheSameArgumentsAreEqual()
    {
        PrivilegedCommand.ClearImmutable("/a/b").Should().Be(PrivilegedCommand.ClearImmutable("/a/b"));
        PrivilegedCommand.ClearImmutable("/a/b").Should().NotBe(PrivilegedCommand.ClearImmutable("/a/c"));
    }
}

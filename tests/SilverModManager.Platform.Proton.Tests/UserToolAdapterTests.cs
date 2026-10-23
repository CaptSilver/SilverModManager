using System.Collections.Immutable;
using System.Runtime.InteropServices;
using CliWrap;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using NSubstitute;

namespace SilverModManager.Platform.Proton.Tests;

public class UserToolAdapterTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly IToolRunner _toolRunner = Substitute.For<IToolRunner>();
    private readonly IProcessRunner _processRunner = Substitute.For<IProcessRunner>();
    private readonly List<Command> _commands = [];

    public UserToolAdapterTests()
    {
        _toolRunner
            .RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs(new ToolRunResult(0));
        _processRunner
            .RunAsync(Arg.Do<Command>(c => _commands.Add(c)), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(_ => new CommandResult(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
    }

    private AbsolutePath Exe => _fs.FromUnsanitizedFullPath("/tools/xEdit/SSEEdit.exe");

    private GameInstallation Installation()
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game");
        var gameFolder = _fs.FromUnsanitizedFullPath("/games/Test Game");
        var result = new GameLocatorResult
        {
            Game = game,
            Path = gameFolder,
            Store = GameStore.Steam,
            StoreIdentifier = "42",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, gameFolder)));
    }

    private UserToolAdapter Adapter(ToolRunVia via, string arguments = "")
        => new(GameId.From(1), "xEdit", Exe, arguments, via, _toolRunner, _processRunner);

    [Theory]
    [InlineData(ToolRunVia.Auto)]
    [InlineData(ToolRunVia.Umu)]
    public async Task WindowsTool_RunsThroughTheToolRunnerWithItsSplitArgumentsAndForcedStrategy(ToolRunVia via)
    {
        var installation = Installation();

        await Adapter(via, "-SSE \"-o:C:\\out dir\"").Execute(installation, CancellationToken.None);

        await _toolRunner.Received(1).RunWindowsTool(
            installation,
            Exe,
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-SSE", "-o:C:\\out dir" })),
            null,
            true,
            Arg.Any<CancellationToken>(),
            via);
        _commands.Should().BeEmpty();
    }

    [Fact]
    public async Task NativeTool_StartsTheProgramDirectlyThroughTheProcessRunner()
    {
        await Adapter(ToolRunVia.Native, "--flag \"two words\"").Execute(Installation(), CancellationToken.None);

        var command = _commands.Should().ContainSingle().Subject;
        command.TargetFilePath.Should().Be(Exe.ToString());
        command.Arguments.Should().Be("--flag \"two words\"");
        command.WorkingDirPath.Should().Be(Exe.Parent.ToString());
        await _toolRunner.DidNotReceiveWithAnyArgs().RunWindowsTool(default!, default, default!, default, default, default, default);
    }

    [Fact]
    public async Task WindowsTool_ThatCannotRun_ThrowsTheRunnersDiagnostic()
    {
        _toolRunner
            .RunWindowsTool(default!, default, default!, default, default, default, default)
            .ReturnsForAnyArgs<Task<ToolRunResult>>(_ => throw new InvalidOperationException("No Wine prefix was found."));

        var act = () => Adapter(ToolRunVia.Auto).Execute(Installation(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("No Wine prefix was found.");
    }

    [Fact]
    public void Adapter_IsListedForTheGameItWasCreatedFor()
    {
        Adapter(ToolRunVia.Auto).GameIds.Should().Equal(GameId.From(1));
        Adapter(ToolRunVia.Auto).Name.Should().Be("xEdit");
    }

    [Theory]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    [InlineData("-a -b", new[] { "-a", "-b" })]
    [InlineData("  -a   -b  ", new[] { "-a", "-b" })]
    [InlineData("\"two words\" x", new[] { "two words", "x" })]
    [InlineData("-o:\"C:\\My Dir\"", new[] { "-o:C:\\My Dir" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("\"unterminated word", new[] { "unterminated word" })]
    public void ArgumentSplitter_SplitsOnSpacesAndGroupsDoubleQuotedWords(string input, string[] expected)
    {
        ArgumentSplitter.Split(input).Should().Equal(expected);
    }
}

using FluentAssertions;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.CLI;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.NexusModsApi;
using NSubstitute;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// The nxm handler's refusals, through <see cref="NxmHandlerHarness"/>: the skyrim domain has no loadout, and the
/// library holds the file in the tests that say so.
/// </summary>
public class NxmModUrlTests
{
    private static readonly NexusModsGameId SkyrimId = NexusModsGameId.From(110);

    private readonly NxmHandlerHarness _harness;

    public NxmModUrlTests(IServiceProvider host, IConnection connection)
    {
        _harness = new NxmHandlerHarness(host, connection, GameDomain.From("skyrim"), SkyrimId);
    }

    private Task<NxmHandlerHarness.Handler> CreateHandler(IExpectedNexusDownloads? expectations, bool libraryHoldsFile)
        => _harness.CreateHandler(expectations, libraryHoldsFile);

    [Fact]
    public async Task AnExpectedFileIsDownloadedEvenThoughTheLibraryHoldsItAndNoLoadoutExists()
    {
        var expectations = new ExpectingDownloads(expected: true);
        var handler = await CreateHandler(expectations, libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        var asked = expectations.Asked.Should().ContainSingle().Subject;
        asked.Item1.Should().Be(GameDomain.From("skyrim"));
        asked.Item2.Value.Should().Be(asked.Item3.Value);
        handler.Events.Received(1).Send(Arg.Any<CliMessages.ModDownloadStarted>());
        handler.Events.Received(1).Send(Arg.Any<CliMessages.ModDownloadSucceeded>());
        handler.Events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadFailed>());
    }

    [Fact]
    public async Task AFileTheLibraryHoldsIsRefusedWhenNothingExpectsIt()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.AlreadyExists));
        handler.Events.DidNotReceive().Send(Arg.Any<CliMessages.ModDownloadStarted>());
    }

    [Fact]
    public async Task AFileForAGameWithoutALoadoutIsRefusedWhenNothingExpectsIt()
    {
        var handler = await CreateHandler(new ExpectingDownloads(expected: false), libraryHoldsFile: false);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.GameNotManaged));
    }

    [Fact]
    public async Task WithNoExpectationsRegisteredTheLinkIsTreatedAsUnexpected()
    {
        var handler = await CreateHandler(expectations: null, libraryHoldsFile: true);

        await handler.Instance.Handle(handler.Link, CancellationToken.None);

        handler.Events.Received(1).Send(Arg.Is<CliMessages.ModDownloadFailed>(m => m.Reason is FailureReason.AlreadyExists));
    }
}

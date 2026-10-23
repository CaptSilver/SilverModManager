using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Backend;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Sdk.Games;
using SilverModManager.Library.Lock;
using SilverModManager.Library.Sources;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using NSubstitute;
using Xunit;

namespace SilverModManager.Library.Tests.Lock;

public class LockSourceDispatchTests(IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);

    private FakeLibrarySource Fake(string name, Dictionary<string, string>? served = null) =>
        new(name, _fixtures, served ?? [], entry => entry.Asset);

    private LockImport Import(params ILibrarySource[] sources) => new(connection, sources, Substitute.For<ILibraryService>());

    [Fact]
    public async Task AnEntryOfARegisteredSourcePlansAndDownloadsThroughThatSource()
    {
        var content = "fake bytes " + Guid.NewGuid();
        var source = Fake("fake", new() { ["pkg.zip"] = content });
        var import = Import(source);
        var entry = new LockEntry("pkg", "3", null, "pkg.zip", LockFixtures.DigestOf(content).ToString(), "fake");

        var plan = await import.Plan(new LockFile([entry]), game: null, TestContext.Current.CancellationToken);
        var summary = await import.Apply(plan, TestContext.Current.CancellationToken);

        plan.Items.Should().ContainSingle().Which.Action.Should().Be(LockAction.Download);
        summary.Verified.Should().ContainSingle().Which.Entry.Id.Should().Be("pkg");
        source.Downloads.Should().Equal("pkg.zip");
    }

    [Fact]
    public async Task AnEntryOfAnUnregisteredSourceFailsThePlanNamingTheSourcesThereAre()
    {
        var source = Fake("fake");
        var import = Import(source);
        var entry = new LockEntry("pkg", "3", null, "pkg.zip", LockFixtures.DigestOf("x").ToString(), "elsewhere");

        var plan = await import.Plan(new LockFile([entry]), game: null, TestContext.Current.CancellationToken);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(LockAction.Fail);
        item.Message.Should().Contain("unknown source 'elsewhere'").And.Contain("fake");
        source.Downloads.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEntryAlreadyInTheLibraryIsSkippedEvenWhenItsSourceIsNotRegistered()
    {
        var content = "present " + Guid.NewGuid();
        var file = await _fixtures.AddLibraryFile("pkg.zip", content, LockFixtures.DigestOf(content));
        var import = Import(Fake("fake"));
        var entry = new LockEntry("pkg", "3", null, "pkg.zip", LockFixtures.DigestOf(content).ToString(), "elsewhere");

        var plan = await import.Plan(new LockFile([entry]), game: null, TestContext.Current.CancellationToken);

        var item = plan.Items.Should().ContainSingle().Subject;
        item.Action.Should().Be(LockAction.Skip, "the bytes are already verified, so no source is needed");
        item.Existing!.Value.Id.Should().Be(file.Id);
    }

    [Fact]
    public async Task ANexusDownloadWithoutANexusGameFailsBeforeTouchingAnyService()
    {
        var source = new NexusLibrarySource(new ServiceCollection().BuildServiceProvider(), new LibraryFileDigests(Substitute.For<SilverModManager.Sdk.FileStore.IFileStore>(), connection));
        var entry = new LibrarySourceEntry("pkg", "1.0", "6", "pkg.zip", LockFixtures.DigestOf("x").ToString(), LockSources.Nexus);

        var act = async () => await source.Download(entry, LockFixtures.DigestOf("x"), new LibrarySourceContext(connection, null, null), TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("without a Nexus Mods game");
    }

    [Fact]
    public async Task AReasonNotToDownloadFailsThePlanWithThatMessage()
    {
        var source = new RefusingSource();
        var import = Import(source);
        var entry = new LockEntry("pkg", "3", null, "pkg.zip", LockFixtures.DigestOf("x").ToString(), "refusing");

        var plan = await import.Plan(new LockFile([entry]), game: null, TestContext.Current.CancellationToken);

        plan.Items.Should().ContainSingle().Which.Should().Match<LockPlanItem>(item => item.Action == LockAction.Fail && item.Message == "pkg: not today");
    }

    [Fact]
    public async Task ExportAsksTheSourcesInOrderAndTheFirstOwnerWins()
    {
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddLibraryFile("shared.zip", "bytes", LockFixtures.DigestOf("bytes"));
        await _fixtures.Link(loadout, file, "Shared");
        var none = Fake("none");
        var first = Fake("first");
        var second = Fake("second");
        first.Owns = _ => new LibrarySourceEntry("shared", "1", null, "shared.zip", LockFixtures.DigestOf("bytes").ToString(), "first");
        second.Owns = _ => new LibrarySourceEntry("shared", "2", null, "shared.zip", LockFixtures.DigestOf("bytes").ToString(), "second");

        var lockFile = await new LockExport([none, first, second]).FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().ContainSingle().Which.Source.Should().Be("first");
        second.Described.Should().BeEmpty("a source after the owner is never asked");
    }

    [Fact]
    public async Task ExportOfAnItemNoSourceOwnsListsTheRegisteredSources()
    {
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddLibraryFile("local.zip", "bytes", LockFixtures.DigestOf("bytes"));
        await _fixtures.Link(loadout, file, "Local");

        var act = async () => await new LockExport([Fake("alpha"), Fake("beta")]).FromLoadout(loadout, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LockException>()).Which.Message.Should().Contain("local.zip").And.Contain("alpha, beta");
    }

    private sealed class RefusingSource : ILibrarySource
    {
        public string Name => "refusing";
        public ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken) => ValueTask.FromResult<LibrarySourceEntry?>(null);
        public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context) => $"{entry.Id}: not today";
        public ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, SilverModManager.Sdk.Hashes.Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services
                .AddLibraryModels()
                .AddGameInstallMetadataModel()
                .AddLoadoutAbstractions()
                .AddOSInterop()
                .AddFileExtractors();
        }
    }
}

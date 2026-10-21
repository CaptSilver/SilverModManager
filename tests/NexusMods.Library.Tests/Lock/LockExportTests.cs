using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Backend;
using NexusMods.Library.Tests.DownloadsService;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Library.Lock;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Library;
using NSubstitute;
using Xunit;

namespace NexusMods.Library.Tests.Lock;

public class LockExportTests(IConnection connection)
{
    private readonly LockFixtures _fixtures = new(connection);

    private LockExport Export() => new(new LibraryFileDigests(Substitute.For<IFileStore>(), connection));

    [Fact]
    public async Task ANexusItemAndAGitHubItemExportInErmsShape()
    {
        var loadout = await _fixtures.CreateLoadout();
        var nexusDigest = LockFixtures.DigestOf("fov archive");
        var githubDigest = LockFixtures.DigestOf("host archive");
        var nexus = await _fixtures.AddNexusFile("AdjustTheFov-325-2-0.zip", "fov archive", nexusDigest, "Adjust The FOV!", "2.0", modId: 325, fileId: 5087);
        var github = await _fixtures.AddGitHubFile("me3-linux-amd64.tar.gz", "host archive", githubDigest, "v0.13.0");
        await _fixtures.Link(loadout, nexus, "Adjust The FOV!");
        await _fixtures.Link(loadout, github, "me3");

        var lockFile = await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Should().Equal(
            new LockEntry("adjust-the-fov", "2.0", "5087", "AdjustTheFov-325-2-0.zip", nexusDigest.ToString(), "nexus"),
            new LockEntry("me3", "v0.13.0", null, "me3-linux-amd64.tar.gz", githubDigest.ToString(), "github")
        );
    }

    [Fact]
    public async Task AReleaseFileIsRecognisedWhetherItsRepositoryIsRecordedByIdOrByName()
    {
        var loadout = await _fixtures.CreateLoadout();
        var byId = await _fixtures.AddGitHubFile("by-id.zip", "by id", LockFixtures.DigestOf("by id"), "v1", repositoryId: 42);
        var byName = await _fixtures.AddGitHubFile("by-name.zip", "by name", LockFixtures.DigestOf("by name"), "v2");
        await _fixtures.Link(loadout, byId, "by id");
        await _fixtures.Link(loadout, byName, "by name");

        var lockFile = await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Select(entry => (entry.Id, entry.Version, entry.Source)).Should().Equal(("by-id", "v1", "github"), ("by-name", "v2", "github"));
    }

    [Fact]
    public async Task DisabledGroupsAndOtherLoadoutsAreLeftOut()
    {
        var loadout = await _fixtures.CreateLoadout();
        var other = await _fixtures.CreateLoadout();
        var kept = await _fixtures.AddGitHubFile("kept.zip", "kept", LockFixtures.DigestOf("kept"), "v1");
        var disabled = await _fixtures.AddGitHubFile("disabled.zip", "disabled", LockFixtures.DigestOf("disabled"), "v1");
        var elsewhere = await _fixtures.AddGitHubFile("elsewhere.zip", "elsewhere", LockFixtures.DigestOf("elsewhere"), "v1");
        await _fixtures.Link(loadout, kept, "kept");
        await _fixtures.Link(loadout, disabled, "disabled", disabled: true);
        await _fixtures.Link(other, elsewhere, "elsewhere");

        var lockFile = await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        lockFile.Entries.Select(entry => entry.Id).Should().Equal("kept");
    }

    [Fact]
    public async Task AFileWithoutADigestThatTheStoreCannotSupplyFailsTheExportNamingTheFile()
    {
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddNexusFile("NoDigest-1-0.zip", "bytes", sha256: null, "No Digest", "1.0", modId: 1, fileId: 2);
        await _fixtures.Link(loadout, file, "No Digest");

        var act = async () => await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LockException>()).Which.Message.Should().Contain("NoDigest-1-0.zip");
    }

    [Fact]
    public async Task ALocalFileHasNoSourceToPinAndFailsTheExportNamingTheFile()
    {
        var loadout = await _fixtures.CreateLoadout();
        var file = await _fixtures.AddLibraryFile("local.zip", "bytes", LockFixtures.DigestOf("bytes"));
        await _fixtures.Link(loadout, file, "Local Mod");

        var act = async () => await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LockException>()).Which.Message.Should().Contain("local.zip");
    }

    [Fact]
    public async Task TwoModsThatSanitizeToTheSameIdFailTheExport()
    {
        var loadout = await _fixtures.CreateLoadout();
        var first = await _fixtures.AddNexusFile("a.zip", "a", LockFixtures.DigestOf("a"), "Better Bows", "1", modId: 1, fileId: 1);
        var second = await _fixtures.AddNexusFile("b.zip", "b", LockFixtures.DigestOf("b"), "better-bows!", "1", modId: 2, fileId: 2);
        await _fixtures.Link(loadout, first, "Better Bows");
        await _fixtures.Link(loadout, second, "better-bows!");

        var act = async () => await Export().FromLoadout(loadout, TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<LockException>()).Which.Message.Should().Contain("better-bows");
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

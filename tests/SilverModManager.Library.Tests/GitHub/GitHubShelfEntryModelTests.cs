using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GitHub;

public class GitHubShelfEntryModelTests(IConnection connection)
{
    private static readonly GameId Game = GameId.From("Tests.GitHubShelfModel");

    [Fact]
    public async Task AnEntryAddedByOwnerAndNameIsRecognisedWithoutARepositoryId()
    {
        using var tx = connection.BeginTransaction();
        var entry = new GitHubShelfEntry.New(tx)
        {
            Organization = "ianpatt",
            Repository = "skse64-model-test",
            AssetSuffix = ".7z",
            GameId = Game,
            IncludePrereleases = false,
        };
        var saved = (await tx.Commit()).Remap(entry);

        saved.IsValid().Should().BeTrue();
        GitHubShelfEntry.All(connection.Db).Should().Contain(row => row.Id == saved.Id && row.Organization == "ianpatt");
        GitHubShelfEntry.RepositoryId.TryGetValue(saved, out _).Should().BeFalse("the id is recorded on the first fetch");
        GitHubShelfEntry.NameHint.TryGetValue(saved, out _).Should().BeFalse();
    }

    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            DIHelpers.ConfigureServices(services);
            services.AddLibraryModels();
        }
    }
}

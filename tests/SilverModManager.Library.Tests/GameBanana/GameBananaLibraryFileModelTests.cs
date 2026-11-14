using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.GameBanana;

public class GameBananaLibraryFileModelTests(IConnection connection)
{
    [Fact]
    public async Task AFileIsRecognisedByItsModId()
    {
        var saved = await new LockFixtures(connection).AddGameBananaFile("mod.zip", "gb " + Guid.NewGuid(), null, modId: 44334, fileId: 583960, dateUpdated: 1622678310);

        var model = GameBananaLibraryFile.Load(connection.Db, saved.Id);
        model.IsValid().Should().BeTrue();
        model.ModId.Should().Be(44334);
        model.FileId.Should().Be(583960);
        model.DateUpdated.Should().Be(1622678310);
        GameBananaLibraryFile.All(connection.Db).Should().Contain(row => row.Id == saved.Id);
        GitHubReleaseLibraryFile.Load(connection.Db, saved.Id).IsValid().Should().BeFalse();
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

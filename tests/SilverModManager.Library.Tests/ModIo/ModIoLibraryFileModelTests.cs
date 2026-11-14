using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Library.Tests.DownloadsService;
using SilverModManager.Library.Tests.Lock;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Library;
using Xunit;

namespace SilverModManager.Library.Tests.ModIo;

public class ModIoLibraryFileModelTests(IConnection connection)
{
    [Fact]
    public async Task AModIoFileIsRecognisedByItsGameId()
    {
        var saved = await new LockFixtures(connection).AddModIoFile("mod.zip", "io " + Guid.NewGuid(), null, gameId: 6715, modId: 4101, fileId: 88001, version: "1.2.0", dateUpdated: 1710000500);

        var model = ModIoLibraryFile.Load(connection.Db, saved.Id);
        model.IsValid().Should().BeTrue();
        model.GameId.Should().Be(6715u);
        model.ModId.Should().Be(4101u);
        model.FileId.Should().Be(88001u);
        model.Version.Should().Be("1.2.0");
        model.DateUpdated.Should().Be(1710000500);
        ModIoLibraryFile.All(connection.Db).Should().Contain(row => row.Id == saved.Id);
        GameBananaLibraryFile.Load(connection.Db, saved.Id).IsValid().Should().BeFalse();
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

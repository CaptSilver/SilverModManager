using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// A database written by the MnemonicDB NuGet package (0.28.2), before the library was vendored as source,
/// must open with the build under test. The fixture holds a Loadout and a settings row, plus one row for every
/// model whose attribute namespace keeps its upstream name, so the kept ids are read back from disk too.
/// </summary>
public class VendoredDatabaseCompatibilityTests
{
    private const string Fixture = "Resources/PackageBuildDatabase.zip";

    [Fact]
    public async Task Test_PackageWrittenDatabaseOpensAndReadsBack()
    {
        using var folder = new OnDiskDataFolder();
        ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, Fixture), folder.DatabaseFolder.ToString());

        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var db = connection.Db;

        var loadout = Loadout.All(db).Single();
        loadout.Name.Should().Be("Compat Fixture Loadout");
        loadout.Revision.Should().Be(7UL);
        SettingRows.Has(connection, "SilverModManager.App.UI.Settings.WelcomeSettings").Should().BeTrue();

        VersionDefinition.All(db).Single().Name.Should().Be("Compat Fixture Version");
        SteamManifest.All(db).Single().ManifestId.Value.Should().Be(424242UL);
        GogManifest.All(db).Single().ManifestId.Should().Be("fixture-gog-manifest");
        GogDepot.All(db).Single().Languages.Should().Equal("en-US");
        GogBuild.All(db).Single().VersionName.Should().Be("fixture-gog-version");
        EpicGameStoreBuild.All(db).Single().AppName.Should().Be("FixtureApp");
        PathHashRelation.All(db).Single().Path.ToString().Should().Be("Data/fixture.bin");
        HashRelation.All(db).Single().Size.Value.Should().Be(5UL);

        var namespaces = db.AttributeCache.AllAttributeIds.Select(id => id.Namespace).ToHashSet();
        foreach (var (ns, _) in SchemaFailsafes.AllowedLegacyNamespaces)
        {
            namespaces.Should().Contain(ns);
        }
    }
}

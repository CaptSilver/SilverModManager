using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.Games.Generic.Tests.Simple;

/// <summary>The installers built from marker rules whose marker file is a path, and the refusals a rule can ask for.</summary>
public class SimpleInstallersTests(ITestOutputHelper outputHelper)
    : ALibraryArchiveInstallerTests<SimpleInstallersTests>(outputHelper, SimpleGames.Witcher1.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddSimpleGames()
        .AddUniversalGameLocator(SimpleGames.Witcher1.GameId, locationsFileSystem: IsolatedKnownFolders.Create);

    /// <summary>Names the folder after the packageId element of the marker, and says what it refuses with.</summary>
    private sealed class PackageIdNamer(bool requireName) : IModFolderNameFromMarker
    {
        public bool RequireName => requireName;

        public string RefusalReason => "The archive's About.xml has no packageId, so the game could not list the mod";

        public string? FolderName(byte[] markerFile)
        {
            var text = Encoding.UTF8.GetString(markerFile);
            var start = text.IndexOf("<packageId>", StringComparison.Ordinal);
            var end = text.IndexOf("</packageId>", StringComparison.Ordinal);
            return start < 0 || end < 0 ? null : text[(start + "<packageId>".Length)..end].ToLowerInvariant();
        }
    }

    private static SimpleInstallRules AboutRules(bool refuseWhenSeveral = false, bool requireName = false)
        => SimpleInstallPresets.InOwnFolderByMarker("About/About.xml", new PackageIdNamer(requireName), refuseWhenSeveral);

    private static string About(string id) => $"<ModMetaData><packageId>{id}</packageId></ModMetaData>";

    private async Task<(string[]? Targets, string? Reason)> Install(SimpleInstallRules rules, string archiveFileName, params (string Path, string Content)[] files)
    {
        var loadout = await CreateLoadout();
        var archive = await AddArchiveWithContents(archiveFileName, files);
        var libraryItem = archive.AsLibraryFile().AsLibraryItem();
        string? reason = null;

        foreach (var installer in SimpleInstallers.Create(ServiceProvider, new ModLocation(ModLocationRoot.GameFolder, "Mods"), rules))
        {
            using var tx = Connection.BeginTransaction();
            var group = new LoadoutItemGroup.New(tx, out var groupId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = libraryItem.Name, LoadoutId = loadout },
            };

            var result = await installer.ExecuteAsync(libraryItem, group, tx, loadout, CancellationToken.None);
            if (result.IsNotSupported(out var why))
            {
                reason ??= why;
                continue;
            }

            var committed = (await tx.Commit()).Remap(group);
            return (committed.Children.OfTypeLoadoutItemWithTargetPath()
                .Select(file => ((GamePath)file.TargetPath).ToString())
                .Order(StringComparer.Ordinal)
                .ToArray(), null);
        }

        return (null, reason);
    }

    [Fact]
    public async Task AMarkerPathMatchesAFolderWhoseAboutFolderHoldsAboutXml()
    {
        var (targets, _) = await Install(AboutRules(), "download.zip", ("Harmony/About/About.xml", About("Brrainz.Harmony")), ("Harmony/Defs/x.xml", ""));

        targets.Should().Equal("{Game}/Mods/brrainz.harmony/About/About.xml", "{Game}/Mods/brrainz.harmony/Defs/x.xml");
    }

    [Fact]
    public async Task AMarkerPathAtTheTopOfTheArchiveIsNamedAfterTheArchiveWhenTheNameIsNotRequired()
    {
        var (targets, _) = await Install(AboutRules(), "Some Mod.zip", ("About/About.xml", "<ModMetaData/>"), ("Defs/x.xml", ""));

        targets.Should().Equal("{Game}/Mods/Some Mod/About/About.xml", "{Game}/Mods/Some Mod/Defs/x.xml");
    }

    [Fact]
    public async Task ALooseAboutXmlDoesNotMatchTheMarkerPath()
    {
        var (targets, reason) = await Install(AboutRules(), "download.zip", ("Harmony/About.xml", About("a")));

        targets.Should().BeNull();
        reason.Should().Be("Found no matching root");
    }

    [Fact]
    public async Task TwoMarkerFoldersAreRefusedWithTheCount()
    {
        var (targets, reason) = await Install(AboutRules(refuseWhenSeveral: true), "pack.zip",
            ("One/About/About.xml", About("a")), ("Two/About/About.xml", About("b")));

        targets.Should().BeNull();
        reason.Should().Be("The archive holds 2 mods (About/About.xml appears 2 times); install each on its own");
    }

    [Fact]
    public async Task TwoMarkerFoldersWithoutTheFlagKeepTheOldReason()
    {
        var (targets, reason) = await Install(AboutRules(), "pack.zip", ("One/About/About.xml", About("a")), ("Two/About/About.xml", About("b")));

        targets.Should().BeNull();
        reason.Should().Be("Did not handle all files");
    }

    [Fact]
    public async Task AManifestWithoutAnIdIsRefusedWhenTheNameIsRequired()
    {
        var (targets, reason) = await Install(AboutRules(requireName: true), "download.zip", ("Harmony/About/About.xml", "<ModMetaData/>"));

        targets.Should().BeNull();
        reason.Should().Contain("has no packageId");
    }

    [Fact]
    public async Task AManifestWithoutAnIdFallsBackToTheArchivesFolderWhenTheNameIsNotRequired()
    {
        var (targets, _) = await Install(AboutRules(), "download.zip", ("Harmony/About/About.xml", "<ModMetaData/>"));

        targets.Should().Equal("{Game}/Mods/Harmony/About/About.xml");
    }

    [Fact]
    public async Task AMarkerWithoutASeparatorStillMatchesALooseFileInTheFolder()
    {
        var rules = SimpleInstallPresets.InOwnFolderByMarker("mod.manifest");

        var (targets, _) = await Install(rules, "download.zip", ("Cool/mod.manifest", "x"), ("Cool/a.pak", "y"));

        targets.Should().Equal("{Game}/Mods/Cool/a.pak", "{Game}/Mods/Cool/mod.manifest");
    }
}

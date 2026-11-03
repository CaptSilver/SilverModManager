using FluentAssertions;
using FomodInstaller.Interface;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.FOMOD;
using SilverModManager.Games.FOMOD.CoreDelegates;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3FomodTests(ITestOutputHelper outputHelper) : AWitcher3ArchiveTest<Witcher3FomodTests>(outputHelper)
{
    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const string ModuleConfig = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
            <moduleName>W3 Mod</moduleName>
            <installSteps order="Explicit">
                <installStep name="Step 1">
                    <optionalFileGroups>
                        <group name="Group 1" type="SelectExactlyOne">
                            <plugins order="Explicit">
                                <plugin name="Option A">
                                    <description>A</description>
                                    <files><file source="a.ws" destination="mods/modFoo/content/scripts/a.ws"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                                <plugin name="Option B">
                                    <description>B</description>
                                    <files><file source="b.ws" destination="mods/modFoo/content/scripts/b.ws"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                            </plugins>
                        </group>
                    </optionalFileGroups>
                </installStep>
            </installSteps>
        </config>
        """;

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddSingleton<ICoreDelegates, InstallerDelegates>();

    [Fact]
    public async Task Witcher3_ListsAFomodInstallerFirst_ThatInstallsWithSavedChoices()
    {
        Game.LibraryItemInstallers[0].Should().BeOfType<FomodXmlInstaller>();

        var loadout = await CreateLoadout();
        var archive = await AddArchiveWithContents("W3 Mod.zip",
            ("fomod/ModuleConfig.xml", ModuleConfig),
            ("a.ws", "a"),
            ("b.ws", "b"));
        FomodOption[] choices =
        [
            new FomodOption
            {
                name = "Step 1",
                groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = "Option B" }] }],
            },
        ];

        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var groupId)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, groupId) { Name = "W3 Mod", LoadoutId = loadout },
        };

        var installer = Game.LibraryItemInstallers.OfType<FomodXmlInstaller>().Single();
        var result = await installer.ExecuteWithSavedChoicesAsync(archive, group, tx, loadout, choices, "W3 Mod", allowPrompt: false, CancellationToken.None)
            .AsTask().WaitAsync(Timeout);

        result.IsSuccess.Should().BeTrue();
        var committed = await tx.Commit();
        Targets(committed.Remap(group)).Should().Equal("{Game}/mods/modFoo/content/scripts/b.ws");
    }
}

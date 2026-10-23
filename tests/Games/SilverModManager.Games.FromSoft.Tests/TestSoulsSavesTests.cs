using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Games.FromSoft.Launch;
using NexusMods.Games.FromSoft.Saves;
using NexusMods.Games.FromSoft.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Launch;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Tests;

public class TestSoulsSavesTests(ITestOutputHelper helper) : AModdedTestSoulsTest<TestSoulsSavesTests>(helper)
{
    private const ulong Account = 76561198000000001;

    private readonly string _prefix = Directory.CreateTempSubdirectory("silvermod-testsouls-prefix-").FullName;

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);
        // The factory runs after construction, when the prefix folder exists; this method runs from the base constructor.
        services.Replace(ServiceDescriptor.Singleton<IPrefixResolver>(provider => new FixedPrefixResolver(PrefixResolution.Resolved(
            new ResolvedPrefix(provider.GetRequiredService<IFileSystem>().FromUnsanitizedFullPath(_prefix), CompatibilityLayerKind.SteamProton)))));
        return services;
    }

    private sealed class FixedPrefixResolver(PrefixResolution resolution) : IPrefixResolver
    {
        public PrefixResolution Resolve(GameInstallation installation) => resolution;
    }

    private VanillaSaveGate SaveGate => ServiceProvider.GetServices<ILaunchGate>().OfType<VanillaSaveGate>().Single();

    private SaveLedger Ledger => SaveLedger.For(Settings, FileSystem, GameInstallation);

    private string Save(string name, string content)
    {
        var folder = FromSoftPaths.SaveFolder(TestSoulsDefinition.Layout, FileSystem.FromUnsanitizedFullPath(_prefix), Account).GetFullPath();
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task TheGateCopiesTheGamesVanillaSaveAndLeavesAnEldenRingSaveInTheSameFolderAlone()
    {
        var loadout = await CreateModdedLoadout();
        var testSouls = Save("TS0000.sl2", "test souls");
        var eldenRing = Save("ER0000.sl2", "elden ring");

        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        File.WriteAllText(eldenRing, "changed by something else");
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(testSouls).Should().Be("test souls");
        File.ReadAllText(eldenRing).Should().Be("changed by something else", "the gate guards only the game's own save");
        Ledger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task TheGateRestoresAChangedVanillaSaveOfTheGame()
    {
        var loadout = await CreateModdedLoadout();
        var save = Save("TS0000.sl2", "test souls");
        await SaveGate.Prepare(ContextOf(loadout), CancellationToken.None);
        Directory.GetFiles(Ledger.Session.GetFullPath(), "*", SearchOption.AllDirectories).Select(Path.GetFileName)
            .Should().Equal("TS0000.sl2");

        File.WriteAllText(save, "written by a mod");
        await SaveGate.Finish(ContextOf(loadout));

        File.ReadAllText(save).Should().Be("test souls");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(_prefix, recursive: true);
    }
}

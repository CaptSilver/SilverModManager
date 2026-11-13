using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Unity.Tests;

/// <summary>The shared checks, run against a game whose files are the given install files.</summary>
public abstract class AUnityRuntimeBranchEmitterTests<TTest>(ITestOutputHelper outputHelper, UnityGameDefinition definition, params string[] installFiles)
    : ALoadoutDiagnosticEmitterTest<TTest, IGame, UnityRuntimeBranchEmitter>(outputHelper, definition.GameId)
{
    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var files = new[] { definition.Executable.ToString() }.Concat(installFiles)
            .ToDictionary(file => RelativePath.FromUnsanitizedInput(file), _ => Array.Empty<byte>());
        return base.AddServices(services)
            .AddUnityGames()
            .AddUniversalGameLocator(definition.GameId, files, locationsFileSystem: IsolatedKnownFolders.Create);
    }

    protected async Task<EntityId> AddFiles(Sdk.Loadouts.LoadoutId loadout, string group, params string[] paths)
    {
        using var tx = Connection.BeginTransaction();
        var groupId = AddEmptyGroup(tx, loadout, group);
        foreach (var path in paths) AddFile(tx, loadout, groupId, new GamePath(LocationId.Game, path));
        return (await tx.Commit())[groupId.Value];
    }
}

public class Il2CppInstallBranchEmitterTests(ITestOutputHelper outputHelper)
    : AUnityRuntimeBranchEmitterTests<Il2CppInstallBranchEmitterTests>(outputHelper, UnityGames.ScheduleI, "GameAssembly.dll")
{
    [Fact]
    public async Task AMonoDllOnAnIl2CppInstallIsReported()
    {
        var loadout = await CreateLoadout();
        var mod = await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Mono.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Id.Should().Be(new DiagnosticId("SilverModManager.Games.Unity", number: 2));
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        var data = diagnostic.Should().BeOfType<Diagnostic<Diagnostics.RuntimeBranchMismatchMessageData>>().Subject.MessageData;
        data.Group.DataId.Value.Should().Be(mod);
        data.Built.Should().Be("Mono");
        data.Install.Should().Be("IL2CPP");
    }

    [Fact]
    public async Task AMatchingDllIsNotReported()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Il2cpp.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AnUntaggedDllIsNotReported()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "Mods/Foo.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task ADisabledGroupIsNotReported()
    {
        var loadout = await CreateLoadout();
        var mod = await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Mono.dll");
        await DisableItem(mod);

        await ShouldHaveNoDiagnostics(loadout);
    }

    [Fact]
    public async Task AGroupWithTwoWrongDllsIsReportedOnce()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Mono.dll", "Mods/Bar.Mono.dll");

        await GetSingleDiagnostic(loadout);
    }
}

public class MonoInstallBranchEmitterTests(ITestOutputHelper outputHelper)
    : AUnityRuntimeBranchEmitterTests<MonoInstallBranchEmitterTests>(outputHelper, UnityGames.ScheduleI, "Schedule I_Data/Managed/Assembly-CSharp.dll")
{
    [Fact]
    public async Task AnIl2CppDllOnAMonoInstallIsReported()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Il2cpp.dll");

        var diagnostic = await GetSingleDiagnostic(loadout);

        diagnostic.Should().BeOfType<Diagnostic<Diagnostics.RuntimeBranchMismatchMessageData>>().Which.MessageData.Install.Should().Be("Mono");
    }
}

public class UnknownInstallBranchEmitterTests(ITestOutputHelper outputHelper)
    : AUnityRuntimeBranchEmitterTests<UnknownInstallBranchEmitterTests>(outputHelper, UnityGames.ScheduleI)
{
    [Fact]
    public async Task NothingIsReportedWhenTheInstallsBranchIsUnknown()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "Mods/Foo.Mono.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

public class BepInExGameBranchEmitterTests(ITestOutputHelper outputHelper)
    : AUnityRuntimeBranchEmitterTests<BepInExGameBranchEmitterTests>(outputHelper, UnityGames.Repo, "GameAssembly.dll")
{
    [Fact]
    public async Task NothingIsReportedForABepInExGame()
    {
        var loadout = await CreateLoadout();
        await AddFiles(loadout, "Someone-Foo", "BepInEx/plugins/Someone-Foo/Foo.Mono.dll");

        await ShouldHaveNoDiagnostics(loadout);
    }
}

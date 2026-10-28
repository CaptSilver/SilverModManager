using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Sdk.Games;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public abstract class AWitcher3PrimaryFileTest<TTest>(ITestOutputHelper helper) : AIsolatedGameTest<TTest, Witcher3Game>(helper)
{
    protected abstract string[] ExePaths { get; }

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        var files = ExePaths.ToDictionary(p => RelativePath.FromUnsanitizedInput(p), _ => "x"u8.ToArray());
        return base.AddServices(services)
            .AddWitcherGames()
            .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), files, locationsFileSystem: IsolatedKnownFolders.Create);
    }
}

public class Witcher3Dx12PrimaryFileTests(ITestOutputHelper helper) : AWitcher3PrimaryFileTest<Witcher3Dx12PrimaryFileTests>(helper)
{
    protected override string[] ExePaths => ["bin/x64_dx12/witcher3.exe", "bin/x64/witcher3.exe"];

    [Fact]
    public void PrimaryFile_IsTheDx12ExeWhenPresent()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "bin/x64_dx12/witcher3.exe"));
    }
}

public class Witcher3X64PrimaryFileTests(ITestOutputHelper helper) : AWitcher3PrimaryFileTest<Witcher3X64PrimaryFileTests>(helper)
{
    protected override string[] ExePaths => ["bin/x64/witcher3.exe"];

    [Fact]
    public void PrimaryFile_FallsBackToTheX64ExeWhenDx12IsAbsent()
    {
        Game.GetPrimaryFile(GameInstallation).Should().Be(new GamePath(LocationId.Game, "bin/x64/witcher3.exe"));
    }
}

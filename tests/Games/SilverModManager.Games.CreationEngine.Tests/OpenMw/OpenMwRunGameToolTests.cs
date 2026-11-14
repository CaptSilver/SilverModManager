using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.CreationEngine.OpenMw;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Games.CreationEngine.Tests.OpenMw;
using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using Xunit;

namespace SilverModManager.Games.CreationEngine.Tests.OpenMw;

public class OpenMwRunGameToolTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwRunGameToolTests>(helper)
{
    private OpenMwRunGameTool Tool => ServiceProvider.GetServices<ITool>().OfType<OpenMwRunGameTool>().Single();

    [Fact]
    public async Task TheNativeInstallResolvesToTheBinaryWithNoArguments()
    {
        var loadout = await CreateLoadout();

        var spec = await Tool.GetLaunchSpec(Refresh(loadout), CancellationToken.None);

        spec.Exe.Should().Be(Roots.NativeProgram!.Value);
        spec.ExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUninstalledProgramIsRefused()
    {
        var loadout = await CreateLoadout();
        Roots.NativeProgram = null;

        var act = () => Tool.GetLaunchSpec(Refresh(loadout), CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<OpenMwProgramMissingException>()).Which.Message.Should().Contain("not installed");
    }

    [Fact]
    public async Task RunningTheFakeBinaryWaitsForItToExit()
    {
        // The default fake resolves a Proton prefix, which would send the program through the tool runner.
        ServiceProvider.GetRequiredService<FakePrefixResolver>().Resolution = _ => PrefixResolution.Native("native build");

        // The test scratch folder is mounted noexec, so the script lives in the temp folder.
        var folder = Directory.CreateTempSubdirectory("fake-openmw");
        try
        {
            var script = Path.Combine(folder.FullName, "openmw");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 1\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Roots.NativeProgram = FileSystem.FromUnsanitizedFullPath(script);
            var loadout = await CreateLoadout();

            var stopwatch = Stopwatch.StartNew();
            await Tool.Execute(Refresh(loadout), CancellationToken.None, []);

            stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}

public class OpenMwFlatpakRunGameToolTests(ITestOutputHelper helper) : AOpenMwGameTest<OpenMwFlatpakRunGameToolTests>(helper)
{
    protected override int[] RootsWithConfig => [1];

    [Fact]
    public async Task TheFlatpakInstallIsRefusedBecauseItsWaitIsNotKnown()
    {
        GameInstallation.LocatorResult.Store.Should().Be(SilverModManager.Sdk.Games.GameStore.OpenMWFlatpak);
        var loadout = await CreateLoadout();
        var tool = ServiceProvider.GetServices<ITool>().OfType<OpenMwRunGameTool>().Single();

        var act = () => tool.GetLaunchSpec(Refresh(loadout), CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<UserFacingException>();
    }
}

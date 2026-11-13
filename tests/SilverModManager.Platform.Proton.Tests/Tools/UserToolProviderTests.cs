using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Platform.Proton.Tools;
using SilverModManager.Platform.Proton.UserTools;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Platform.Proton.Tests.Tools;

/// <summary>A hand-added program lists as the plain program it is, unless the row asks for its writes to be kept.</summary>
public class UserToolProviderTests(ITestOutputHelper helper) : AToolFlowTest<UserToolProviderTests>(helper)
{
    private UserToolProvider Provider() => new(Connection, ToolFactory());

    private async Task AddRow(Loadout.ReadOnly loadout, string name, bool keepsOutput)
    {
        using var tx = Connection.BeginTransaction();
        var row = new UserTool.New(tx)
        {
            InstallationId = loadout.InstallationId,
            Name = name,
            Exe = "/tools/" + name + ".exe",
            Arguments = "",
            RunVia = ToolRunVia.Auto,
        };
        if (keepsOutput) tx.Add(row.Id, UserTool.CapturesGameFolderOutput, Null.Instance);
        await tx.Commit();
    }

    [Fact]
    public async Task TheCaptureMarkerSelectsTheOutput()
    {
        var loadout = await CreateLoadout();
        await AddRow(loadout, "Kept", keepsOutput: true);
        await AddRow(loadout, "Plain", keepsOutput: false);

        var tools = Provider().GetTools(loadout).Cast<IToolWithOutput>().ToDictionary(t => ((ITool)t).Name);

        tools["Kept"].Output.Should().Be(new ToolOutput.GameFolder("Kept output"));
        tools["Kept"].IgnoredOutput.Should().Contain("**/*.log");
        tools["Plain"].Output.Should().Be(ToolOutput.None);
    }
}

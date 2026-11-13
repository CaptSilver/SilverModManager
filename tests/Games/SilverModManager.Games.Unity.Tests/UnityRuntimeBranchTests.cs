using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Games.Unity.Tests.Fixtures;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.Tests;

public class UnityRuntimeBranchTests(ITestOutputHelper outputHelper) : AUnityGameTests<UnityRuntimeBranchTests>(outputHelper, UnityGames.Repo)
{
    // A data folder with a space in it, as Schedule I has, so a path built from a hard-coded name cannot pass.
    private static readonly UnityGameDefinition SpacedDataFolder = UnityGames.Repo with { DataFolder = "Schedule I_Data" };

    private AbsolutePath GameFolder => GameInstallation.Locations[LocationId.Game].Path;

    private void WriteInGame(string relative)
    {
        var file = GameFolder.Combine(relative);
        file.Parent.CreateDirectory();
        File.WriteAllText(file.ToString(), relative);
    }

    [Fact]
    public void AGameAssemblyBesideTheExecutableMeansIl2Cpp()
    {
        WriteInGame("GameAssembly.dll");

        UnityRuntimeBranch.Of(GameInstallation, SpacedDataFolder).Should().Be(UnityRuntime.Il2Cpp);
    }

    [Fact]
    public void AManagedAssemblyCSharpMeansMono()
    {
        WriteInGame("Schedule I_Data/Managed/Assembly-CSharp.dll");

        UnityRuntimeBranch.Of(GameInstallation, SpacedDataFolder).Should().Be(UnityRuntime.Mono);
    }

    [Fact]
    public void NeitherMeansUnknown()
    {
        UnityRuntimeBranch.Of(GameInstallation, SpacedDataFolder).Should().Be(UnityRuntime.Unknown);
    }

    [Fact]
    public void TheDataFolderComesFromTheDefinitionNotAFixedName()
    {
        WriteInGame("Schedule I_Data/Managed/Assembly-CSharp.dll");

        UnityRuntimeBranch.Of(GameInstallation, UnityGames.Repo).Should().Be(UnityRuntime.Unknown);
    }

    [Theory]
    [InlineData("Foo.Il2cpp.dll", UnityRuntime.Il2Cpp)]
    [InlineData("Foo.IL2CPP.dll", UnityRuntime.Il2Cpp)]
    [InlineData("Foo_Mono.dll", UnityRuntime.Mono)]
    [InlineData("Foo-mono.dll", UnityRuntime.Mono)]
    [InlineData("Monolith.dll", UnityRuntime.Unknown)]
    [InlineData("Foo.dll", UnityRuntime.Unknown)]
    public void AFileNameTokenNamesTheRuntimeABuildIsFor(string dllName, UnityRuntime expected)
    {
        UnityRuntimeBranch.OfFileName(dllName).Should().Be(expected);
    }
}

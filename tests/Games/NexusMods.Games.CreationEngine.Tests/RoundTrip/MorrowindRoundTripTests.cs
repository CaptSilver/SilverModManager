using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Games.CreationEngine.Morrowind.OpenMw;
using NexusMods.Games.CreationEngine.Tests.Morrowind.OpenMw;
using NexusMods.Sdk.Games;
using Xunit;

namespace NexusMods.Games.CreationEngine.Tests.RoundTrip;

public class MorrowindRoundTripTests(ITestOutputHelper helper)
    : ABethesdaRoundTripTest<MorrowindRoundTripTests, CreationEngine.Morrowind.Morrowind>(helper)
{
    protected override Version GameVersion => new("1.6.0.1820");
    protected override string PrimaryExe => "Morrowind.exe";
    protected override GamePath DataPath => new(LocationId.Game, "Data Files");

    protected override IServiceCollection AddServices(IServiceCollection services) =>
        base.AddServices(services).AddTempOpenMwConfigRoots();

    // Applies Morrowind, so the production roots would let it rewrite a real openmw.cfg.
    [Fact]
    public void OpenMwConfigRootsAreTempFolders() =>
        ServiceProvider.GetRequiredService<IOpenMwConfigRoots>().Should().BeOfType<TempOpenMwConfigRoots>();
}

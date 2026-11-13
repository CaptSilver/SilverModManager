using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.Abstractions.Games.Tests;

public class DiagnosticDismissalsTests(ITestOutputHelper helper) : AIsolatedGameTest<DiagnosticDismissalsTests, StubbedGame>(helper)
{
    private static readonly DiagnosticId First = new("SilverModManager.Games.Generic", number: 11);
    private static readonly DiagnosticId Second = new("SilverModManager.Games.Generic", number: 13);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<StubbedGame>()
            .AddSingleton<IGameLocator>(s => new UniversalStubbedGameLocator<StubbedGame>(
                s,
                s.GetRequiredService<IFileSystem>(),
                s.GetRequiredService<TemporaryFileManager>(),
                stores: [GameStore.Steam])
            {
                LocatorIds = [LocatorId.From("3976631895")],
            });
    }

    private async Task Dismiss(LoadoutId loadout, DiagnosticId id, string subject, string? identity = null)
    {
        using var tx = Connection.BeginTransaction();
        _ = new DiagnosticDismissal.New(tx)
        {
            LoadoutId = loadout,
            Source = id.Source,
            Number = id.Number,
            Subject = subject,
            Identity = identity,
            Title = "a title",
        };
        await tx.Commit();
    }

    private static FileIdentity IdentityOf(string text) => FileIdentity.XxHash3(System.Text.Encoding.UTF8.GetBytes(text).xxHash3());

    [Fact]
    public async Task ForReturnsTheSubjectsOfOneIdOnly()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        await Dismiss(loadout.LoadoutId, First, "Game|a.dll", "XxHash3:AA");
        await Dismiss(loadout.LoadoutId, Second, "Game|b.dll");
        await Dismiss(other.LoadoutId, First, "Game|c.dll");

        var found = DiagnosticDismissals.For(Connection.Db, loadout.LoadoutId, First);

        found.Should().Equal(new DiagnosticDismissals.Dismissed("Game|a.dll", "XxHash3:AA"));
    }

    [Fact]
    public async Task ForReadsAnAbsentIdentityAsNull()
    {
        var loadout = await CreateLoadout();
        await Dismiss(loadout.LoadoutId, First, "Game|a.dll");

        DiagnosticDismissals.For(Connection.Db, loadout.LoadoutId, First).Should().ContainSingle().Which.Identity.Should().BeNull();
    }

    [Fact]
    public void SubjectOfNamesTheLocationAndThePath()
    {
        DiagnosticDismissals.SubjectOf(new GamePath(LocationId.Game, "Data/SKSE/Plugins/x.dll")).Should().Be("Game|Data/SKSE/Plugins/x.dll");
    }

    [Fact]
    public void IdentityTextNamesTheKindAndTheDigest()
    {
        var identity = IdentityOf("a");

        DiagnosticDismissals.IdentityText(identity).Should().Be($"{identity.Kind}:{Convert.ToHexString(identity.Digest)}");
    }

    [Fact]
    public void CoversRequiresTheSameIdentityWhenOneWasRecorded()
    {
        var old = IdentityOf("old");
        var current = IdentityOf("new");
        IReadOnlyList<DiagnosticDismissals.Dismissed> dismissed =
        [
            new("Game|a.dll", DiagnosticDismissals.IdentityText(old)),
            new("Game|b.dll", null),
        ];

        DiagnosticDismissals.Covers(dismissed, "Game|a.dll", old).Should().BeTrue("same bytes at the same path");
        DiagnosticDismissals.Covers(dismissed, "Game|a.dll", current).Should().BeFalse("new bytes at an ignored path warn again");
        DiagnosticDismissals.Covers(dismissed, "Game|b.dll", current).Should().BeTrue("a dismissal with no identity covers the path");
        DiagnosticDismissals.Covers(dismissed, "Game|c.dll", old).Should().BeFalse("another path");
    }
}

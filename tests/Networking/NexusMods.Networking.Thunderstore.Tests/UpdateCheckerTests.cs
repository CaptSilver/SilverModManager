using System.Security.Cryptography;
using System.Text.Json;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore.DTOs;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;
using Xunit;

namespace NexusMods.Networking.Thunderstore.Tests;

public class UpdateCheckerTests(IConnection connection)
{
    private readonly ThunderstoreUpdateChecker _checker = new(connection);

    private static ThunderstoreVersionNumber V(string text)
    {
        ThunderstoreVersionNumber.TryParse(text, out var version).Should().BeTrue();
        return version;
    }

    private static string UniqueTeam() => "Team" + Guid.NewGuid().ToString("N");

    private static ThunderstoreCatalog Catalog(string community, string team, string name, params (string Version, bool Active)[] versions)
    {
        var listing = JsonSerializer.Deserialize<PackageListing>(ThunderstoreJson.Listing(team, name), ThunderstoreApi.JsonOptions)!;
        // Newest first, the order the listing chunks use.
        var records = versions
            .OrderByDescending(v => V(v.Version))
            .Select(v => JsonSerializer.Deserialize<PackageVersion>(ThunderstoreJson.ChunkVersion(team, name, v.Version, []), ThunderstoreApi.JsonOptions)! with { IsActive = v.Active })
            .ToArray();
        return new ThunderstoreCatalog(community, [listing with { Versions = records }]);
    }

    private async Task<ThunderstoreLibraryFile.ReadOnly> AddFile(string team, string name, string version, string community = "repo", bool frozen = false)
    {
        var fileName = $"{team}-{name}-{version}.zip";
        var bytes = System.Text.Encoding.UTF8.GetBytes(fileName);
        using var tx = connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        _ = new ThunderstoreLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Namespace = team,
            Name = name,
            VersionNumber = version,
            Community = community,
            PinnedSha256 = Sha256Value.From(SHA256.HashData(bytes)),
        };
        if (frozen) _ = new UpdatePin.New(tx, id) { Frozen = true };
        var result = await tx.Commit();
        return ThunderstoreLibraryFile.Load(result.Db, result[id]);
    }

    [Fact]
    public async Task NewerVersionInTheCatalog_IsReported()
    {
        var team = UniqueTeam();
        var file = await AddFile(team, "Mod", "4.1.0");

        var updates = _checker.Check("repo", Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true)), connection.Db);

        var update = updates.Should().ContainSingle().Which;
        update.LibraryFileId.Should().Be(file.Id);
        update.Current.Should().Be(new ThunderstoreVersionRef(new ThunderstorePackageRef(team, "Mod"), V("4.1.0")));
        update.Newest.Should().Be(new ThunderstoreVersionRef(new ThunderstorePackageRef(team, "Mod"), V("4.2.0")));
    }

    [Fact]
    public async Task EqualVersion_ReportsNothing()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.2.0");

        _checker.Check("repo", Catalog("repo", team, "Mod", ("4.2.0", true)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task OlderVersionInTheCatalog_ReportsNothing()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.3.0");

        _checker.Check("repo", Catalog("repo", team, "Mod", ("4.2.0", true)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task VersionsCompareByNumber_NotByText()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.9.0");

        var updates = _checker.Check("repo", Catalog("repo", team, "Mod", ("4.9.0", true), ("4.10.0", true)), connection.Db);

        updates.Should().ContainSingle().Which.Newest.Version.Should().Be(V("4.10.0"));
    }

    [Fact]
    public async Task FrozenItem_ReportsNothing()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.1.0", frozen: true);

        _checker.Check("repo", Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task InactiveNewerVersion_IsSkippedForTheNewestActiveOne()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.1.0");

        var updates = _checker.Check("repo", Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true), ("4.3.0", false)), connection.Db);

        updates.Should().ContainSingle().Which.Newest.Version.Should().Be(V("4.2.0"));
    }

    [Fact]
    public async Task OnlyAnInactiveNewerVersion_ReportsNothing()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.1.0");

        _checker.Check("repo", Catalog("repo", team, "Mod", ("4.1.0", true), ("4.3.0", false)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task ItemOfAnotherCommunity_IsNotConsidered()
    {
        var team = UniqueTeam();
        await AddFile(team, "Mod", "4.1.0", community: "peak");

        _checker.Check("repo", Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task PackageMissingFromTheCatalog_ReportsNothing()
    {
        var team = UniqueTeam();
        await AddFile(team, "Gone", "1.0.0");

        _checker.Check("repo", Catalog("repo", team, "Other", ("2.0.0", true)), connection.Db).Should().BeEmpty();
    }

    [Fact]
    public async Task Observe_EmitsAfterARefresh()
    {
        var team = UniqueTeam();
        var file = await AddFile(team, "Mod", "4.1.0");
        var seen = new List<Optional<ThunderstoreUpdate>>();
        using var subscription = _checker.Observe(file).Subscribe(seen.Add);

        seen.Should().BeEmpty("nothing is known until a catalog arrives");

        _checker.Refresh(Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true)));

        seen.Should().ContainSingle().Which.Value.Newest.Version.Should().Be(V("4.2.0"));

        _checker.Refresh(Catalog("repo", team, "Mod", ("4.1.0", true)));

        seen.Should().HaveCount(2);
        seen[1].HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task Observe_AfterARefresh_EmitsTheKnownStateToANewSubscriber()
    {
        var team = UniqueTeam();
        var file = await AddFile(team, "Mod", "4.1.0");
        _checker.Refresh(Catalog("repo", team, "Mod", ("4.1.0", true), ("4.2.0", true)));
        var seen = new List<Optional<ThunderstoreUpdate>>();

        using var subscription = _checker.Observe(file).Subscribe(seen.Add);

        seen.Should().ContainSingle().Which.HasValue.Should().BeTrue();
    }

    [Fact]
    public async Task Observe_IgnoresARefreshOfAnotherCommunity()
    {
        var team = UniqueTeam();
        var file = await AddFile(team, "Mod", "4.1.0");
        var seen = new List<Optional<ThunderstoreUpdate>>();
        using var subscription = _checker.Observe(file).Subscribe(seen.Add);

        _checker.Refresh(Catalog("peak", team, "Mod", ("4.1.0", true), ("4.2.0", true)));

        seen.Should().BeEmpty();
    }
}

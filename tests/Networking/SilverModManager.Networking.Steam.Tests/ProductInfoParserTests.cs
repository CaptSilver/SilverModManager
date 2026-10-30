using FluentAssertions;
using SilverModManager.Abstractions.Steam.Values;
using SteamKit2;
using Xunit;

namespace SilverModManager.Networking.Steam.Tests;

public class ProductInfoParserTests
{
    private static KeyValue Depot(string depotId, params KeyValue[] branches)
    {
        var manifests = new KeyValue("manifests");
        manifests.Children.AddRange(branches);
        var depot = new KeyValue(depotId);
        depot.Children.Add(manifests);
        return depot;
    }

    private static KeyValue Branch(string name, string? gid, string? size, string? download)
    {
        var branch = new KeyValue(name);
        if (gid is not null) branch.Children.Add(new KeyValue("gid", gid));
        if (size is not null) branch.Children.Add(new KeyValue("size", size));
        if (download is not null) branch.Children.Add(new KeyValue("download", download));
        return branch;
    }

    [Fact]
    public void ABranchWithAllValuesIsParsed()
    {
        var depot = Depot("489831", Branch("public", "7", "100", "50"));

        ProductInfoParser.TryParseDownloadableDepot(AppId.From(1), depot, out var result).Should().BeTrue();

        result!.Manifests["public"].ManifestId.Should().Be(ManifestId.From(7));
    }

    [Theory]
    [InlineData(null, "100", "50", "gid")]
    [InlineData("7", null, "50", "size")]
    [InlineData("7", "100", null, "download")]
    public void ABranchMissingAValueThrowsNamingTheBranchAndTheValue(string? gid, string? size, string? download, string missing)
    {
        var depot = Depot("489831", Branch("beta", gid, size, download));

        var act = () => ProductInfoParser.TryParseDownloadableDepot(AppId.From(1), depot, out _);

        act.Should().Throw<InvalidDataException>().WithMessage($"*branch 'beta'*'{missing}'*");
    }
}

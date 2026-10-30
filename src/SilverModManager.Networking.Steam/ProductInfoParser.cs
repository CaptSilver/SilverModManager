using System.Diagnostics.CodeAnalysis;
using SilverModManager.Abstractions.Steam.DTOs;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;
using SteamKit2;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Parses a PICSProductInfoCallback into a <see cref="ProductInfo"/>.
/// </summary>
public static class ProductInfoParser
{
    public static ProductInfo Parse(SteamApps.PICSProductInfoCallback callback)
    {
        var appInfo = callback.Apps.First();
        var appId = AppId.From(appInfo.Key);
        var depotsSection = appInfo.Value.KeyValues.Children.First(kv => kv.Name == "depots").Children;

        List<Depot> depots = [];
        foreach (var maybeDepot in depotsSection)
        {
            if (!TryParseDownloadableDepot(appId, maybeDepot, out var depot))
                continue;
            depots.Add(depot);
        }
        
        var productInfo = new ProductInfo
        {
            ChangeNumber = appInfo.Value.ChangeNumber,
            AppId = appId,
            Depots = depots.ToArray(),
        };
        return productInfo;
    }

    private static string RequiredValue(DepotId depotId, string branchName, KeyValue branch, string key)
    {
        return branch[key].Value ?? throw new InvalidDataException($"Depot {depotId.Value} branch '{branchName}' has no '{key}' value");
    }

    internal static bool TryParseDownloadableDepot(AppId appId, KeyValue depot, [NotNullWhen(true)] out Depot? result)
    {
        if (!uint.TryParse(depot.Name, out var parsedDepotId))
        {
            result = null;
            return false;
        }

        var configSection = depot.Children.FirstOrDefault(c => c.Name == "config");
        var osList = configSection?.Children.FirstOrDefault(c => c.Name == "oslist")?.Value ?? "";

        var depotId = DepotId.From(parsedDepotId);

        var manifestsKey = depot.Children.FirstOrDefault(c => c.Name == "manifests");
        if (manifestsKey == null)
        {
            result = null;
            return false;
        }
        
        Dictionary<string, ManifestInfo> manifestInfos = new();
        foreach (var branch in manifestsKey.Children)
        {
            var branchName = branch.Name ?? throw new InvalidDataException($"A branch of depot {depotId.Value} has no name");
            var manifestId = ManifestId.From(ulong.Parse(RequiredValue(depotId, branchName, branch, "gid")));
            var sizeOnDisk = Size.From(ulong.Parse(RequiredValue(depotId, branchName, branch, "size")));
            var downloadSize = Size.From(ulong.Parse(RequiredValue(depotId, branchName, branch, "download")));

            var manifestInfo = new ManifestInfo
            {
                ManifestId = manifestId,
                Size = sizeOnDisk,
                DownloadSize = downloadSize,
            };

            manifestInfos[branchName] = manifestInfo;
        }

        result = new Depot
        {
            DepotId = depotId,
            OsList = osList.Split(',', ' '),
            Manifests = manifestInfos,
        };

        return true;
    }
    
}

using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;
using SilverModManager.Sdk.Settings;
using SilverModManager.Sdk.Thunderstore;
using SilverModManager.SingleProcess;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// What the Thunderstore verb tests share: a package index and package downloads answered from a table, and a way to
/// run a verb and read back what it printed.
/// </summary>
internal static class ThunderstoreVerbFixtures
{
    public const string IndexUrl = "https://thunderstore.io/c/repo/api/v1/package-listing-index/";

    public static readonly byte[] PluginZip = BuildZip("plugins/Plugin.dll");
    public static readonly byte[] LoaderZip = BuildZip("BepInExPack/BepInEx/core/BepInEx.dll");

    private static byte[] BuildZip(string entry)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("synthetic file");
        }

        return stream.ToArray();
    }

    private const string LoaderVersionJson = """
        {
          "namespace": "BepInEx", "name": "BepInExPack", "version_number": "5.4.2305", "full_name": "BepInEx-BepInExPack-5.4.2305",
          "description": "Synthetic pack", "icon": "https://gcdn.thunderstore.io/live/repository/icons/BepInEx-BepInExPack-5.4.2305.png",
          "dependencies": [],
          "download_url": "https://thunderstore.io/package/download/BepInEx/BepInExPack/5.4.2305/",
          "downloads": 1000, "date_created": "2025-09-01T10:00:00.000000Z", "website_url": "", "is_active": true
        }
        """;

    // The resolver reads the experimental endpoints, so those answer for the two packages an install touches.
    public static RoutingHandler BuildRoutes()
    {
        var chunk = Gzip.Compress($"[{ThunderstoreJson.RepoLib},{ThunderstoreJson.BepInExPack}]");
        var chunkUrl = $"https://cdn.example/sha256/{Gzip.HexDigest(chunk)}/chunk";
        var index = Gzip.Compress($"[\"{chunkUrl}\"]");

        return new RoutingHandler()
            .Add(IndexUrl, index, "application/octet-stream", new Uri($"https://cdn.example/sha256/{Gzip.HexDigest(index)}/index"))
            .Add(chunkUrl, chunk, "application/octet-stream")
            .Add("https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/", ThunderstoreJson.ExperimentalPackage)
            .Add("https://thunderstore.io/api/experimental/package/BepInEx/BepInExPack/5.4.2305/", LoaderVersionJson)
            .Add("https://thunderstore.io/api/experimental/package/Zehs/REPOLib/4.2.0/", ThunderstoreJson.ExperimentalVersion)
            .Add("https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/", PluginZip, "application/zip")
            .Add("https://thunderstore.io/package/download/BepInEx/BepInExPack/5.4.2305/", LoaderZip, "application/zip");
    }

    public static IServiceCollection AddVerbHost(this IServiceCollection services, RoutingHandler routes)
    {
        var cacheRoot = new ConfigurablePath(KnownPath.EntryDirectory, $"ThunderstoreVerbs.{Guid.NewGuid():N}");
        services
            .AddThunderstore()
            .OverrideSettingsForTests<ThunderstoreSettings>(settings => settings with { CacheRoot = cacheRoot })
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(routes)));
        return services;
    }

    public static async Task<(int ExitCode, string Output)> Run(IServiceProvider provider, params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await provider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Join("\n", renderer.Logs.SelectMany(Flatten)));
    }

    // A table's cells are renderables of their own, so a row's text is read out of them.
    private static IEnumerable<string> Flatten(IRenderable renderable) => renderable switch
    {
        Text text => [text.Template],
        Table table => table.Columns.Concat(table.Rows.SelectMany(row => row)).OfType<Text>().Select(t => t.Template),
        _ => [],
    };

    /// <summary>
    /// Adds <c>Zehs-REPOLib</c> at <paramref name="version"/> to the library as if it had been downloaded earlier.
    /// </summary>
    public static async Task<LibraryFile.ReadOnly> AddThunderstoreLibraryPackage(this IConnection connection, string version)
    {
        var fileName = $"Zehs-REPOLib-{version}.zip";
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
            Namespace = "Zehs",
            Name = "REPOLib",
            VersionNumber = version,
            Community = "repo",
            PinnedSha256 = Sha256Value.From(System.Security.Cryptography.SHA256.HashData(bytes)),
        };
        var result = await tx.Commit();
        return result.Remap(file);
    }

    /// <summary>
    /// Links a library file into the loadout as a group of its own.
    /// </summary>
    public static async Task LinkIntoLoadout(this IConnection connection, Loadout.ReadOnly loadout, LibraryFile.ReadOnly file, bool enabled = true)
    {
        using var tx = connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = file.FileName.ToString(), LoadoutId = loadout.LoadoutId, IsDisabled = !enabled },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = file.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }
}

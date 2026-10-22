using System.IO.Compression;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Networking.Thunderstore.Tests.Fixtures;
using NexusMods.Paths;
using NexusMods.Sdk;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.ProxyConsole;
using NexusMods.SingleProcess;
using NexusMods.StandardGameLocators.TestHelpers;
using NexusMods.StandardGameLocators.TestHelpers.StubbedGames;

namespace NexusMods.CLI.Tests;

/// <summary>
/// <c>library export-lock</c> followed by <c>library import-lock</c> for a Thunderstore package. The host has its own
/// Thunderstore API and an HTTP client that answers from a table, so nothing reaches the network.
/// </summary>
[Collection("ConsoleOutput")]
public class LibraryLockThunderstoreVerbTests(ITestOutputHelper helper) : AIsolatedGameTest<LibraryLockThunderstoreVerbTests, ThunderstoreStubbedGame>(helper)
{
    private static readonly byte[] Zip = BuildZip();
    private static readonly Sha256Value ZipDigest = Sha256Value.From(SHA256.HashData(Zip));

    // A real archive, since the import installs the downloaded package into the loadout.
    private static byte[] BuildZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("plugins/REPOLib.dll").Open());
            writer.Write("synthetic plugin");
        }

        return stream.ToArray();
    }

    private const string VersionJson = """
        {
          "namespace": "Zehs", "name": "REPOLib", "version_number": "4.2.0", "full_name": "Zehs-REPOLib-4.2.0",
          "description": "Synthetic", "icon": "https://gcdn.thunderstore.io/live/repository/icons/Zehs-REPOLib-4.2.0.png",
          "dependencies": [],
          "download_url": "https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/",
          "downloads": 1, "date_created": "2025-09-01T10:00:00.000000Z", "website_url": "", "is_active": true
        }
        """;

    private static readonly RoutingHandler Routes = new RoutingHandler()
        .Add("https://thunderstore.io/api/experimental/package/Zehs/REPOLib/4.2.0/", VersionJson)
        .Add("https://thunderstore.io/package/download/Zehs/REPOLib/4.2.0/", Zip, "application/zip")
        .Add("https://thunderstore.io/api/experimental/package/Zehs/REPOLib/", $$"""
            {
              "namespace": "Zehs", "name": "REPOLib", "full_name": "Zehs-REPOLib", "owner": "Zehs",
              "package_url": "https://thunderstore.io/c/repo/p/Zehs/REPOLib/",
              "date_created": "2025-01-01T00:00:00.000000Z", "date_updated": "2025-09-02T10:00:00.000000Z",
              "rating_score": 1, "is_pinned": false, "is_deprecated": false, "total_downloads": 1,
              "latest": {{VersionJson}},
              "community_listings": [{ "community": "repo", "categories": [], "review_status": "approved", "has_nsfw_content": false, "url": "https://thunderstore.io/c/repo/p/Zehs/REPOLib/" }]
            }
            """);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services)
            .AddGame<ThunderstoreStubbedGame>()
            .AddUniversalGameLocator<ThunderstoreStubbedGame>(Version.Parse("0.0.0"))
            .AddThunderstore()
            .AddSingleton<CommandLineConfigurator>()
            .AddDefaultParsers()
            .AddCLI()
            .ReplaceDesktopNotifier();
        services.Replace(ServiceDescriptor.Singleton(new HttpClient(Routes)));
        return services;
    }

    private async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var renderer = new LoggingRenderer();
        var exitCode = await ServiceProvider.GetRequiredService<CommandLineConfigurator>().RunAsync(args, renderer, CancellationToken.None);
        return (exitCode, string.Join("\n", renderer.Logs.OfType<Text>().Select(t => t.Template)));
    }

    // The row stands in for a download that happened earlier: its bytes are not the zip, only its pin is.
    private async Task<LibraryFile.ReadOnly> AddPackageRow()
    {
        const string fileName = "Zehs-REPOLib-4.2.0.zip";
        var bytes = "earlier download"u8.ToArray();
        using var tx = Connection.BeginTransaction();
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = fileName,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = fileName },
        };
        tx.Add(id, LibraryFile.Sha256, ZipDigest);
        _ = new ThunderstoreLibraryFile.New(tx, id)
        {
            LibraryFile = file,
            Namespace = "Zehs",
            Name = "REPOLib",
            VersionNumber = "4.2.0",
            Community = "repo",
            PinnedSha256 = ZipDigest,
        };
        var result = await tx.Commit();
        return result.Remap(file);
    }

    private async Task Link(Loadout.ReadOnly loadout, LibraryFile.ReadOnly file)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { Name = "REPOLib", LoadoutId = loadout.LoadoutId },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = file.Id, LoadoutItemGroup = group };
        await tx.Commit();
    }

    [Fact]
    public async Task ExportedThunderstoreLockImportsBackIntoAnotherLoadoutThroughTheApi()
    {
        var root = Directory.CreateTempSubdirectory("silvermod-lock-thunderstore-").FullName;
        try
        {
            var source = await CreateLoadout();
            var target = await CreateLoadout();
            var row = await AddPackageRow();
            await Link(source, row);

            var lockPath = Path.Combine(root, "mods.lock.toml");
            var (exportCode, exportOutput) = await Run("library", "export-lock", "--loadout", source.Name, "--out", lockPath);
            exportCode.Should().Be(0, exportOutput);
            (await File.ReadAllTextAsync(lockPath, TestContext.Current.CancellationToken)).Should().Be(
                "# Generated by SilverModManager. Pinned mod versions + hashes.\n\n" +
                $"[zehs-repolib]\nversion = \"4.2.0\"\nasset = \"Zehs-REPOLib-4.2.0.zip\"\nsha256 = \"{ZipDigest}\"\nsource = \"thunderstore\"\n");

            // Forget the earlier download so the import has to fetch it again.
            await LoadoutManager.DeleteLoadout(source.LoadoutId);
            await LibraryService.RemoveLibraryItems([row.AsLibraryItem()]);

            var (importCode, importOutput) = await Run("library", "import-lock", "--loadout", target.Name, "--file", lockPath);

            importCode.Should().Be(0, importOutput);
            importOutput.Should().Contain("[verified] zehs-repolib").And.Contain("[installed] zehs-repolib");
            var imported = ThunderstoreLibraryFile.All(Connection.Db).Single(file => file.Name == "REPOLib" && file.VersionNumber == "4.2.0");
            imported.PinnedSha256.Should().Be(ZipDigest);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

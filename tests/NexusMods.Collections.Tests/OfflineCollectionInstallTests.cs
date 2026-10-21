using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GuidedInstallers;
using NexusMods.Abstractions.Collections;
using NexusMods.Abstractions.Collections.Json;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.Abstractions.NexusModsLibrary.Models;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Games.FOMOD;
using NexusMods.Games.TestFramework;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;
using Xunit;

namespace NexusMods.Collections.Tests;

/// <summary>
/// Installs a collection built from local archives. Nothing here touches the network: the collection
/// entities are written to the database directly and the mods are zips created on disk.
/// </summary>
public class OfflineCollectionInstallTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<OfflineCollectionInstallTests>(helper)
{
    // Stands in for the guided installer window. Assigned before the base constructor builds the services.
    private readonly ScriptedGuidedInstallerState _window = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        ConfigOptions.RegisterNullGuidedInstaller = false;
        return base.AddServices(services)
            .AddSingleton(_window)
            .AddTransient<IGuidedInstaller>(_ => _window.Create());
    }

    // A stalled install must fail the test instead of hanging the runner.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private const string ModuleConfig = """
        <config xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://qconsulting.ca/fo3/ModConfig5.0.xsd">
            <moduleName>Mod {0}</moduleName>
            <installSteps order="Explicit">
                <installStep name="Step 1">
                    <optionalFileGroups>
                        <group name="Group 1" type="SelectExactlyOne">
                            <plugins order="Explicit">
                                <plugin name="Option A">
                                    <description>A</description>
                                    <files><file source="mod{0}_a.esp" destination="mod{0}_a.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                                <plugin name="Option B">
                                    <description>B</description>
                                    <files><file source="mod{0}_b.esp" destination="mod{0}_b.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                            </plugins>
                        </group>
                    </optionalFileGroups>
                </installStep>
                <installStep name="Step 2">
                    <optionalFileGroups>
                        <group name="Group 1" type="SelectAny">
                            <plugins order="Explicit">
                                <plugin name="Option C">
                                    <description>C</description>
                                    <files><file source="mod{0}_c.esp" destination="mod{0}_c.out.esp"/></files>
                                    <typeDescriptor><type name="Optional"/></typeDescriptor>
                                </plugin>
                            </plugins>
                        </group>
                    </optionalFileGroups>
                </installStep>
            </installSteps>
        </config>
        """;

    private static FomodOption[] Choices(string firstStepOption = "Option A", string secondStepOption = "Option C") =>
    [
        new FomodOption
        {
            name = "Step 1",
            groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = firstStepOption }] }],
        },
        new FomodOption
        {
            name = "Step 2",
            groups = [new FomodGroup { name = "Group 1", choices = [new FomodChoice { name = secondStepOption }] }],
        },
    ];

    private async Task<(AbsolutePath Path, Md5Value Md5)> CreateFomodMod(AbsolutePath folder, int index, bool plain)
    {
        var path = folder.Combine($"mod{index}.zip");
        await using (var stream = path.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }

            if (plain)
            {
                Add($"archive/pc/mod/plain{index}.archive", $"plain mod {index}");
            }
            else
            {
                Add("fomod/ModuleConfig.xml", string.Format(ModuleConfig, index));
                foreach (var option in "abc") Add($"mod{index}_{option}.esp", $"mod {index} option {option}");
            }
        }

        await using var read = path.Read();
        return (path, await Md5Hasher.HashAsync(read));
    }

    private async Task<(NexusModsCollectionLibraryFile.ReadOnly File, CollectionRevisionMetadata.ReadOnly Revision, CollectionDownload.ReadOnly[] Items)> CreateCollection(
        (FomodOption[]? Choices, string Name)[] fomodMods)
    {
        var folder = TemporaryFileManager.CreateFolder();
        var slug = CollectionSlug.From("offline");
        var revisionNumber = RevisionNumber.From(1);

        var mods = new List<Mod>();
        var archives = new List<(AbsolutePath Path, Md5Value Md5)>();
        for (var i = 0; i < fomodMods.Length; i++)
        {
            var archive = await CreateFomodMod(folder.Path, i, plain: fomodMods[i].Choices is null);
            archives.Add(archive);
            mods.Add(new Mod
            {
                Name = fomodMods[i].Name,
                DomainName = GameDomain.From("cyberpunk2077"),
                Source = new ModSource
                {
                    Type = ModSourceType.Direct,
                    Md5 = archive.Md5,
                    Url = new Uri($"https://example.invalid/mod{i}.zip"),
                    LogicalFilename = $"mod{i}.zip",
                    FileSize = Size.From((ulong)archive.Path.FileInfo.Size),
                },
                Choices = fomodMods[i].Choices is { } choices ? new Choices { Type = ChoicesType.fomod, Options = choices } : null,
            });
        }

        var root = new CollectionRoot
        {
            Info = new CollectionInfo { Name = "Offline", DomainName = GameDomain.From("cyberpunk2077") },
            Mods = mods.ToArray(),
        };

        var collectionJson = folder.Path.Combine("collection.json");
        var collectionZip = folder.Path.Combine("collection.zip");
        await collectionJson.WriteAllTextAsync(JsonSerializer.Serialize(root, ServiceProvider.GetRequiredService<JsonSerializerOptions>()));
        await using (var stream = collectionZip.Create())
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(collectionJson.ToString(), "collection.json");
        }

        var collectionLibraryFile = await LibraryService.AddLocalFile(collectionZip);

        using var tx = Connection.BeginTransaction();

        tx.Add(collectionLibraryFile.Id, NexusModsCollectionLibraryFile.CollectionSlug, slug);
        tx.Add(collectionLibraryFile.Id, NexusModsCollectionLibraryFile.CollectionRevisionNumber, revisionNumber);

        var author = new User.New(tx) { Name = "Author", NexusId = 1 };
        var collection = new CollectionMetadata.New(tx)
        {
            CollectionId = CollectionId.From(1),
            Slug = slug,
            Name = "Offline",
            GameId = NexusModsGameId.From(3333),
            AuthorId = author,
        };

        var revision = new CollectionRevisionMetadata.New(tx)
        {
            RevisionId = RevisionId.From(1),
            RevisionNumber = revisionNumber,
            CollectionId = collection,
        };

        for (var i = 0; i < mods.Count; i++)
        {
            var download = new CollectionDownload.New(tx)
            {
                ArrayIndex = i,
                CollectionRevisionId = revision,
                IsOptional = false,
                Name = mods[i].Name,
            };

            _ = new CollectionDownloadExternal.New(tx, download.Id)
            {
                CollectionDownload = download,
                Md5 = mods[i].Source.Md5,
                Size = mods[i].Source.FileSize,
                Uri = mods[i].Source.Url!,
            };
        }

        var result = await tx.Commit();

        // The mod archives go into the library as the direct downloads the collection refers to.
        foreach (var (path, md5) in archives)
        {
            var file = await LibraryService.AddLocalFile(path);
            using var fileTx = Connection.BeginTransaction();
            fileTx.Add(file.Id, LibraryFile.Md5, md5);
            fileTx.Add(file.Id, DirectDownloadLibraryFile.LogicalFileName, path.FileName);
            await fileTx.Commit();
        }

        var db = Connection.Db;
        var libraryFile = NexusModsCollectionLibraryFile.Load(db, collectionLibraryFile.Id);
        var revisionMetadata = CollectionRevisionMetadata.Load(db, result[revision.Id]);
        var items = CollectionDownloader.GetItems(revisionMetadata, CollectionDownloader.ItemType.Required);
        return (libraryFile, revisionMetadata, items);
    }

    private static async Task<NexusCollectionLoadoutGroup.ReadOnly> AwaitJob(IJobTask<InstallCollectionJob, NexusCollectionLoadoutGroup.ReadOnly> job)
    {
        async Task<NexusCollectionLoadoutGroup.ReadOnly> Run() => await job;
        return await Run().WaitAsync(Timeout);
    }

    [Fact]
    public async Task FomodModsWithSavedChoices_AllInstall()
    {
        var mods = Enumerable.Range(0, 6).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        var files = LoadoutItem.FindByLoadout(group.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
            .Order()
            .ToArray();

        files.Should().Equal(Enumerable.Range(0, 6).SelectMany(i => new[] { $"mod{i}_a.out.esp", $"mod{i}_c.out.esp" }).Order().ToArray());
    }

    [Fact]
    public async Task ModWithSavedChoicesThatDoNotApply_FailsWithAReasonAndTheRestInstall()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            (Choices("No such option"), "Broken mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        _window.SupportsUserInteraction = false; // headless: no window to fall back to

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        var failures = job.JobDefinition.FailedItems;
        failures.Should().ContainSingle();
        failures[0].Name.Should().Be("Broken mod");
        failures[0].Exception.Should().BeOfType<SavedChoicesDoNotApplyException>();
        failures[0].Reason.Should().Contain("No such option");

        var installedNames = LoadoutItem.FindByLoadout(group.Db, loadout)
            .OfTypeLoadoutItemWithTargetPath()
            .OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
            .ToArray();
        installedNames.Should().Contain(["mod0_a.out.esp", "mod2_a.out.esp"]).And.NotContain("mod1_a.out.esp");
    }

    [Fact]
    public async Task Progress_CountsRiseAndEveryModIsReportedAsInstallingBeforeItCounts()
    {
        var mods = Enumerable.Range(0, 6).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var snapshots = new List<CollectionInstallProgress.Snapshot>();
        using var subscription = job.JobDefinition.Progress.Changes.Subscribe(snapshots.Add);
        await AwaitJob(job);

        // The job starts on the thread pool, so subscribing can still replay the empty snapshot from before it began.
        var seen = snapshots.SkipWhile(s => ReferenceEquals(s, CollectionInstallProgress.Snapshot.Empty)).ToArray();
        seen.Select(s => s.Installed).Should().BeInAscendingOrder();
        seen.Should().OnlyContain(s => s.Total == 6 && s.Installed + s.Failed.Count + s.Installing.Count <= s.Total);
        seen.SelectMany(s => s.Installing).Distinct().Should().BeEquivalentTo(mods.Select(m => m.Item2));

        var last = seen[^1];
        last.IsFinished.Should().BeTrue();
        last.Installed.Should().Be(6);
        last.Installing.Should().BeEmpty();
        last.Failed.Should().BeEmpty();
        last.LeftDisabled.Should().BeFalse();
    }

    [Fact]
    public async Task Progress_ListsAFailedModWithItsReasonAndSaysTheCollectionWasLeftDisabled()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            (Choices("No such option"), "Broken mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        _window.SupportsUserInteraction = false; // headless: no window to fall back to

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        var last = job.JobDefinition.Progress.Current;
        last.IsFinished.Should().BeTrue();
        last.Installed.Should().Be(2);
        last.Failed.Should().ContainSingle();
        last.Failed[0].Name.Should().Be("Broken mod");
        last.Failed[0].Reason.Should().Contain("No such option");
        last.LeftDisabled.Should().BeTrue();
    }

    [Fact]
    public async Task Progress_ReportsAlreadyInstalledModsSeparatelyFromThisRunsTotal()
    {
        var mods = Enumerable.Range(0, 3).Select(i => (Choices(), $"Fomod mod {i}")).ToArray();
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        await AwaitJob(InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items));
        var second = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(second);

        var last = second.JobDefinition.Progress.Current;
        last.AlreadyInstalled.Should().Be(3);
        last.Total.Should().Be(0);
        last.Installed.Should().Be(0);
        last.IsFinished.Should().BeTrue();
    }

    private static string[] InstalledFileNames(NexusCollectionLoadoutGroup.ReadOnly group, LoadoutId loadout) => LoadoutItem.FindByLoadout(group.Db, loadout)
        .OfTypeLoadoutItemWithTargetPath()
        .OfTypeLoadoutFile()
        .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).FileName.ToString())
        .Order()
        .ToArray();

    [Fact]
    public async Task ValidSavedChoices_InstallWithoutAnyPrompt()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod 0"), (Choices(), "Good mod 1")]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        _window.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task SavedChoicesThatDoNotApply_PromptOnceWithTheMatchingOptionsSelected_AndTheAnswerInstalls()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod 0"), (Choices(secondStepOption: "No such option"), "Mod with stale choices")]);
        var loadout = await CreateLoadout();

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        job.JobDefinition.FailedItems.Should().BeEmpty();
        var prompt = _window.Prompts.Should().ContainSingle().Which;
        prompt.Notice.Should().Contain("Mod with stale choices").And.Contain("No such option");
        prompt.Steps[0].Groups.Single().Options.Single(o => o.Name == "Option A").Type.Should().Be(OptionType.PreSelected);
        prompt.Steps[0].Groups.Single().Options.Single(o => o.Name == "Option B").Type.Should().Be(OptionType.Available);

        InstalledFileNames(group, loadout).Should().Equal("mod0_a.out.esp", "mod0_c.out.esp", "mod1_a.out.esp");
    }

    [Fact]
    public async Task TwoModsNeedingPrompts_ArePromptedOneAfterTheOther_WhileAnotherModFinishes()
    {
        var mods = new (FomodOption[]?, string)[]
        {
            (Choices(secondStepOption: "No such option"), "Stale mod 0"),
            (Choices(secondStepOption: "No such option"), "Stale mod 1"),
            (null, "Plain mod"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();

        // Keep the first window open until the plain mod has finished.
        var release = new TaskCompletionSource();
        _window.BeforeAnswer = _ => release.Task;

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var running = AwaitJob(job);

        var deadline = DateTime.UtcNow + Timeout;
        while (job.JobDefinition.Progress.Current.Installed < 1 || _window.Prompts.Length < 1)
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the plain mod should finish while a window is open");
            await Task.Delay(20);
        }

        // The other stale mod waits for the window instead of opening a second one.
        await Task.Delay(300);
        _window.Prompts.Should().ContainSingle();
        job.JobDefinition.Progress.Current.Installed.Should().Be(1);

        release.SetResult();
        await running;

        _window.Prompts.Should().HaveCount(2);
        _window.MaxConcurrentPrompts.Should().Be(1);
        job.JobDefinition.FailedItems.Should().BeEmpty();
        job.JobDefinition.Progress.Current.Installed.Should().Be(3);
    }

    [Fact]
    public async Task CancellingThePrompt_SkipsThatModWithTheReason_AndTheCollectionCompletes()
    {
        var mods = new[]
        {
            (Choices(), "Good mod 0"),
            (Choices(secondStepOption: "No such option"), "Stale mod"),
            (Choices(), "Good mod 2"),
        };
        var (file, revision, items) = await CreateCollection(mods);
        var loadout = await CreateLoadout();
        _window.Cancel = true;

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        var group = await AwaitJob(job);

        var failure = job.JobDefinition.FailedItems.Should().ContainSingle().Which;
        failure.Name.Should().Be("Stale mod");
        failure.Exception.Should().BeOfType<FomodInstallCancelledException>();
        failure.Reason.Should().Contain("No such option");
        InstalledFileNames(group, loadout).Should().Contain(["mod0_a.out.esp", "mod2_a.out.esp"]).And.NotContain("mod1_a.out.esp");
    }

    [Fact]
    public async Task AGuidedInstallerWithoutUserInteraction_NeverPrompts_AndFailsTheModWithTheReason()
    {
        var (file, revision, items) = await CreateCollection([(Choices(), "Good mod"), (Choices(secondStepOption: "No such option"), "Stale mod")]);
        var loadout = await CreateLoadout();
        _window.SupportsUserInteraction = false;

        var job = InstallCollectionJob.Create(ServiceProvider, loadout, file, revision, items);
        await AwaitJob(job);

        _window.Prompts.Should().BeEmpty();
        var failure = job.JobDefinition.FailedItems.Should().ContainSingle().Which;
        failure.Exception.Should().BeOfType<SavedChoicesDoNotApplyException>();
        failure.Reason.Should().Contain("No such option");
    }
}

using System.Reactive.Linq;
using SilverModManager.Sdk;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Dialog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ReactiveUI;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.GC;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Helpers;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class LoadoutCardDeleteSavesTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FileRedirect(Func<Loadout.ReadOnly, string> fileName) : IPerLoadoutSaves
    {
        public SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout) => new SaveRedirectPlan.LoaderKey("savefile", fileName(loadout));

        public SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot) => null;
    }

    private sealed class Rig
    {
        public Rig(IServiceProvider provider)
        {
            Presenter = new PresenterRig(provider);
            FileSystem = new InMemoryFileSystem();
            Root = FileSystem.FromUnsanitizedFullPath("/saves");
            FileSystem.CreateDirectory(Root);
            Manager = Substitute.For<ILoadoutManager>();
            Services = new OverridingServiceProvider(Presenter.Services, (typeof(IFileSystem), FileSystem), (typeof(ILoadoutManager), Manager));
        }

        public PresenterRig Presenter { get; }
        public InMemoryFileSystem FileSystem { get; }
        public AbsolutePath Root { get; }
        public ILoadoutManager Manager { get; }
        public IServiceProvider Services { get; }
    }

    private async Task<Loadout.ReadOnly> SeedLoadout(string name, bool keepSaves)
    {
        var installation = Provider.GetRequiredService<IGameRegistry>().LocateGameInstallations().First();
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = installation.LocatorResult.Path.ToString(),
            Name = installation.Game.DisplayName,
            Store = installation.LocatorResult.Store,
            Game = installation.Game.GameId,
            GameId = installation.Game.NexusModsGameId.Value,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = name,
            ShortName = "ZZZ",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        if (keepSaves) tx.Add(loadout.Id, Loadout.KeepSavesWithLoadout, true);
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private static string SaveName(Loadout.ReadOnly loadout) => $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2";

    private LoadoutCardViewModel CardFor(Rig rig, Loadout.ReadOnly loadout)
    {
        var settings = new LoadoutGameSettings(loadout, rig.Services, new FileRedirect(SaveName), null, () => [rig.Root]);
        return new LoadoutCardViewModel(loadout, Connection, rig.Services)
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
            GameSettings = settings,
        };
    }

    private static void Answer(Rig rig, ButtonDefinitionId button) =>
        rig.Presenter.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = button });

    [Fact]
    public async Task Deleting_AsksAboutTheSaves_WithNoAsTheDefault_AndKeepsThemOnNo()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves No", keepSaves: true);
        var save = rig.Root / SaveName(loadout);
        rig.FileSystem.WriteAllText(save, "progress");
        Answer(rig, ButtonDefinitionId.No);

        await CardFor(rig, loadout).DeleteLoadoutCommand.Execute();

        await rig.Manager.Received(1).DeleteLoadout(loadout.LoadoutId, Arg.Any<GarbageCollectorRunMode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        var prompt = rig.Presenter.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Content(prompt).Text.Should().Contain("Also delete the saves for this loadout at").And.Contain(save.ToString());
        prompt.ButtonDefinitions.Single(b => b.ButtonStyling == ButtonStyling.Primary).Id.Should().Be(ButtonDefinitionId.No);
        rig.FileSystem.FileExists(save).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_RemovesTheSlotFilesOnYes_AndNothingElse()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves Yes", keepSaves: true);
        var save = rig.Root / SaveName(loadout);
        rig.FileSystem.WriteAllText(save, "progress");
        rig.FileSystem.WriteAllText(rig.Root / (SaveName(loadout) + ".bak"), "backup");
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "somebody else's");
        Answer(rig, ButtonDefinitionId.Accept);

        await CardFor(rig, loadout).DeleteLoadoutCommand.Execute();

        rig.FileSystem.FileExists(save).Should().BeFalse();
        rig.FileSystem.FileExists(rig.Root / (SaveName(loadout) + ".bak")).Should().BeFalse();
        rig.FileSystem.FileExists(rig.Root / "current.sl2").Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_AFolderSlot_RemovesTheWholeFolderOnYes()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves Folder", keepSaves: true);
        var id = LoadoutSaveSlot.IdOf(loadout);
        var folder = rig.Root / "Saves" / id;
        rig.FileSystem.CreateDirectory(folder);
        rig.FileSystem.WriteAllText(folder / "one.ess", "a");
        var saves = Substitute.For<IPerLoadoutSaves>();
        saves.PlanSaveRedirect(Arg.Any<Loadout.ReadOnly>()).Returns(new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $@"Saves\{id}\"));
        var card = new LoadoutCardViewModel(loadout, Connection, rig.Services)
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { }),
            GameSettings = new LoadoutGameSettings(loadout, rig.Services, saves, null, () => [rig.Root]),
        };
        Answer(rig, ButtonDefinitionId.Accept);

        await card.DeleteLoadoutCommand.Execute();

        rig.FileSystem.DirectoryExists(folder).Should().BeFalse();
        rig.FileSystem.DirectoryExists(rig.Root / "Saves").Should().BeTrue("only the loadout's own folder goes");
    }

    [Fact]
    public async Task Deleting_AsksNothing_WhenTheLoadoutDidNotKeepItsSaves()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves Off", keepSaves: false);
        rig.FileSystem.WriteAllText(rig.Root / SaveName(loadout), "progress");

        await CardFor(rig, loadout).DeleteLoadoutCommand.Execute();

        rig.Presenter.Dialogs.Should().BeEmpty();
        rig.FileSystem.FileExists(rig.Root / SaveName(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task Deleting_AsksNothing_WhenNoSaveWasEverWritten()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves None", keepSaves: true);

        await CardFor(rig, loadout).DeleteLoadoutCommand.Execute();

        rig.Presenter.Dialogs.Should().BeEmpty();
    }

    [Fact]
    public async Task Deleting_AsksNothing_WhenTheDeleteWasRefused()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Delete Saves Refused", keepSaves: true);
        rig.FileSystem.WriteAllText(rig.Root / SaveName(loadout), "progress");
        rig.Manager.DeleteLoadout(Arg.Any<LoadoutId>(), Arg.Any<GarbageCollectorRunMode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Throws(new ExecutableInUseException("running"));

        await CardFor(rig, loadout).DeleteLoadoutCommand.Execute();

        rig.Presenter.Dialogs.Select(d => d.WindowTitle).Should().Equal("The game is still running");
        rig.FileSystem.FileExists(rig.Root / SaveName(loadout)).Should().BeTrue();
    }
}

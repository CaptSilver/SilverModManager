using System.Reactive.Linq;
using SilverModManager.Sdk;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Dialog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using ReactiveUI;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Exceptions;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.App.UI.Dialog;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Tests.Helpers;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class LoadoutGameSettingsTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class FakeSaves : IPerLoadoutSaves
    {
        public SaveRedirectPlan? Redirect { get; set; } = new SaveRedirectPlan.LoaderKey("savefile", "unused");

        public Func<Loadout.ReadOnly, AbsolutePath, SaveAdoptionPlan?>? Adopt { get; set; }

        public SaveRedirectPlan? PlanSaveRedirect(Loadout.ReadOnly loadout) => Redirect;

        public SaveAdoptionPlan? PlanAdoptCurrentSave(Loadout.ReadOnly loadout, AbsolutePath saveRoot) => Adopt?.Invoke(loadout, saveRoot);
    }

    private sealed class Rig
    {
        public Rig(IServiceProvider provider)
        {
            Presenter = new PresenterRig(provider);
            FileSystem = new InMemoryFileSystem();
            Root = FileSystem.FromUnsanitizedFullPath("/saves");
            FileSystem.CreateDirectory(Root);
            Services = new OverridingServiceProvider(Presenter.Services, (typeof(IFileSystem), FileSystem));
        }

        public PresenterRig Presenter { get; }
        public InMemoryFileSystem FileSystem { get; }
        public AbsolutePath Root { get; }
        public IServiceProvider Services { get; }
        public FakeSaves Saves { get; } = new();

        public void Answer(ButtonDefinitionId button) =>
            Presenter.Answer = _ => Task.FromResult(new StandardDialogResult { ButtonId = button });

        /// <summary>Adopts <c>current.sl2</c> into <c>slot_&lt;id&gt;.sl2</c> when the first exists and the second does not.</summary>
        public void AdoptsCurrentSave() => Saves.Adopt = (loadout, root) =>
        {
            var source = root / "current.sl2";
            var destination = root / $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2";
            return FileSystem.FileExists(source) && !FileSystem.FileExists(destination)
                ? new SaveAdoptionPlan("Copy current.sl2 into the loadout's slot.", [new SaveAdoptionPlan.Copy(source, destination)])
                : null;
        };

        public LoadoutGameSettings Settings(Loadout.ReadOnly loadout, IPerLoadoutSaves? saves = null, IniSet? iniFiles = null)
            => new(loadout, Services, saves ?? Saves, iniFiles, () => [Root]);
    }

    private async Task<Loadout.ReadOnly> SeedLoadout(string name)
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
        var result = await tx.Commit();
        return result.Remap(loadout);
    }

    private Loadout.ReadOnly Reload(Loadout.ReadOnly loadout) => Loadout.Load(Connection.Db, loadout.LoadoutId);

    [Fact]
    public async Task KeepSaves_IsDisabledWithAReason_WhenTheGameKeepsNoSlots()
    {
        var rig = new Rig(Provider);
        var settings = new LoadoutGameSettings(await SeedLoadout("No Slots"), rig.Services, null, null, () => []);

        settings.CanKeepSaves.Should().BeFalse();
        settings.KeepSavesToolTip.Should().NotBeNullOrWhiteSpace();
        (await settings.ToggleKeepSavesCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_IsDisabledWithThePlansReason_WhenTheGameReturnsUnsupported()
    {
        var rig = new Rig(Provider);
        rig.Saves.Redirect = new SaveRedirectPlan.Unsupported("Seamless Co-op shares one save.");
        var settings = rig.Settings(await SeedLoadout("Unsupported"));

        settings.CanKeepSaves.Should().BeFalse();
        settings.KeepSavesToolTip.Should().Contain("Seamless Co-op shares one save.");
        (await settings.ToggleKeepSavesCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_MentionsSteamCloud_OnlyForTitlesThatRedirectThroughAnIniKey()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Cloud");

        rig.Saves.Redirect = new SaveRedirectPlan.IniKey("General", "sLocalSavePath", @"Saves\x\");
        var bethesda = rig.Settings(loadout);
        rig.Saves.Redirect = new SaveRedirectPlan.LoaderKey("savefile", "x.sl2");
        var fromSoft = rig.Settings(loadout);

        bethesda.CanKeepSaves.Should().BeTrue();
        bethesda.KeepSavesToolTip.Should().Contain("Steam Cloud");
        fromSoft.CanKeepSaves.Should().BeTrue();
        fromSoft.KeepSavesToolTip.Should().NotContain("Steam Cloud");
    }

    [Fact]
    public async Task OwnGameInis_IsHiddenWithoutAnIniSet_AndReadsOnWhenTheAttributeIsAbsent()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Inis");

        rig.Settings(loadout).ShowOwnsGameInis.Should().BeFalse();

        var withInis = rig.Settings(loadout, iniFiles: new IniSet("Skyrim.ini"));
        withInis.ShowOwnsGameInis.Should().BeTrue();
        withInis.OwnsGameInis.Should().BeTrue();
    }

    [Fact]
    public async Task OwnGameInis_WritesTheFlagBothWays()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Inis Toggle");
        var settings = rig.Settings(loadout, iniFiles: new IniSet("Skyrim.ini"));

        await settings.ToggleOwnsGameInisCommand.Execute();
        settings.OwnsGameInis.Should().BeFalse();
        Loadout.OwnsGameInis.Get(Reload(loadout)).Should().BeFalse();

        await settings.ToggleOwnsGameInisCommand.Execute();
        settings.OwnsGameInis.Should().BeTrue();
        Loadout.OwnsGameInis.Get(Reload(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task KeepSaves_TurnsOnWithoutADialog_WhenThereIsNothingToCopy()
    {
        var rig = new Rig(Provider);
        rig.AdoptsCurrentSave();
        var loadout = await SeedLoadout("Nothing To Copy");
        var settings = rig.Settings(loadout);

        await settings.ToggleKeepSavesCommand.Execute();

        rig.Presenter.Dialogs.Should().BeEmpty();
        settings.KeepSavesWithLoadout.Should().BeTrue();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task KeepSaves_OffersTheCopyWithYesAsTheDefault_AndCopiesOnYes()
    {
        var rig = new Rig(Provider);
        rig.AdoptsCurrentSave();
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        var loadout = await SeedLoadout("Copy Yes");
        var settings = rig.Settings(loadout);
        var destination = rig.Root / $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2";
        rig.Answer(ButtonDefinitionId.Accept);

        await settings.ToggleKeepSavesCommand.Execute();

        var offer = rig.Presenter.Dialogs.Should().ContainSingle().Subject;
        offer.WindowTitle.Should().Be("Copy your current save into this loadout's slot?");
        PresenterRig.Content(offer).Text.Should().Contain((rig.Root / "current.sl2").ToString()).And.Contain(destination.ToString());
        offer.ButtonDefinitions.Single(b => b.ButtonStyling == ButtonStyling.Primary).Id.Should().Be(ButtonDefinitionId.Accept);
        (await rig.FileSystem.ReadAllTextAsync(destination)).Should().Be("progress");
        (await rig.FileSystem.ReadAllTextAsync(rig.Root / "current.sl2")).Should().Be("progress", "the save is copied, never moved");
        settings.KeepSavesWithLoadout.Should().BeTrue();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task KeepSaves_OnNo_TurnsTheFlagOnWithoutCopying()
    {
        var rig = new Rig(Provider);
        rig.AdoptsCurrentSave();
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        var loadout = await SeedLoadout("Copy No");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.No);

        await settings.ToggleKeepSavesCommand.Execute();

        rig.FileSystem.FileExists(rig.Root / $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2").Should().BeFalse();
        settings.KeepSavesWithLoadout.Should().BeTrue();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task KeepSaves_WhenTheOfferIsDismissed_ChangesNothing()
    {
        var rig = new Rig(Provider);
        rig.AdoptsCurrentSave();
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        var loadout = await SeedLoadout("Copy Dismissed");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.Cancel);

        await settings.ToggleKeepSavesCommand.Execute();

        rig.FileSystem.FileExists(rig.Root / $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2").Should().BeFalse();
        settings.KeepSavesWithLoadout.Should().BeFalse();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_RefusesToOverwriteADestinationThatAppearedAfterThePlan_AndLeavesTheFlagOff()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Copy Race");
        var destination = rig.Root / $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2";
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        rig.Saves.Adopt = (_, _) => new SaveAdoptionPlan("Copy it.", [new SaveAdoptionPlan.Copy(rig.Root / "current.sl2", destination)]);
        rig.FileSystem.WriteAllText(destination, "the save already there");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.Accept);

        await settings.ToggleKeepSavesCommand.Execute();

        (await rig.FileSystem.ReadAllTextAsync(destination)).Should().Be("the save already there");
        rig.Presenter.Dialogs.Should().HaveCount(2, "the offer, then the refusal");
        settings.KeepSavesWithLoadout.Should().BeFalse();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_WhenALaterCopyFails_RemovesTheEarlierCopy_AndNamesTheFileThatFailed()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Copy Fails");
        var slot = LoadoutSaveSlot.IdOf(loadout);
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        var missing = rig.Root / "current.sl2.bak";
        rig.Saves.Adopt = (_, _) => new SaveAdoptionPlan("Copy it.",
        [
            new SaveAdoptionPlan.Copy(rig.Root / "current.sl2", rig.Root / $"slot_{slot}.sl2"),
            new SaveAdoptionPlan.Copy(missing, rig.Root / $"slot_{slot}.sl2.bak"),
        ]);
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.Accept);

        await settings.ToggleKeepSavesCommand.Execute();

        rig.FileSystem.FileExists(rig.Root / $"slot_{slot}.sl2").Should().BeFalse("a copy that fails part-way removes what it created");
        rig.FileSystem.FileExists(rig.Root / $"slot_{slot}.sl2.bak").Should().BeFalse();
        var refusal = rig.Presenter.Dialogs.Should().HaveCount(2, "the offer, then the refusal").And.Subject.Last();
        refusal.WindowTitle.Should().Be("The save was not copied");
        PresenterRig.Body(refusal).Should().Contain($"Could not copy {missing}");
        settings.KeepSavesWithLoadout.Should().BeFalse();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_TurnsOffWithoutAskingAnything()
    {
        var rig = new Rig(Provider);
        rig.AdoptsCurrentSave();
        rig.FileSystem.WriteAllText(rig.Root / "current.sl2", "progress");
        var loadout = await SeedLoadout("Turn Off");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.No);
        await settings.ToggleKeepSavesCommand.Execute();
        var dialogsBefore = rig.Presenter.Dialogs.Length;

        await settings.ToggleKeepSavesCommand.Execute();

        rig.Presenter.Dialogs.Length.Should().Be(dialogsBefore);
        settings.KeepSavesWithLoadout.Should().BeFalse();
        Loadout.KeepSavesWithLoadout.Get(Reload(loadout)).Should().BeFalse();
    }

    [Fact]
    public async Task KeepSaves_CanBeTurnedOff_EvenWhenTheGameNowRefusesIt()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Refused Later");
        var settings = rig.Settings(loadout);
        await settings.ToggleKeepSavesCommand.Execute();
        rig.Saves.Redirect = new SaveRedirectPlan.Unsupported("no longer");
        var refusing = rig.Settings(Reload(loadout));

        (await refusing.ToggleKeepSavesCommand.CanExecute.FirstAsync()).Should().BeTrue("the user must be able to switch it off");
    }

    [Fact]
    public async Task FindSaveLocations_ListsTheExistingSlotFilesOfALoaderRedirect_OnlyWhenTheLoadoutKeepsSaves()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Locations File");
        var name = $"slot_{LoadoutSaveSlot.IdOf(loadout)}.sl2";
        rig.Saves.Redirect = new SaveRedirectPlan.LoaderKey("savefile", name);
        rig.FileSystem.WriteAllText(rig.Root / name, "a");
        rig.FileSystem.WriteAllText(rig.Root / (name + ".bak"), "b");
        var settings = rig.Settings(loadout);

        settings.FindSaveLocations().Should().BeEmpty("the loadout does not keep its saves");

        rig.Answer(ButtonDefinitionId.No);
        await settings.ToggleKeepSavesCommand.Execute();

        settings.FindSaveLocations().Should().BeEquivalentTo([rig.Root / name, rig.Root / (name + ".bak")]);
    }

    [Fact]
    public async Task FindSaveLocations_FollowsAnIniKeyToTheSlotFolder()
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Locations Folder");
        var id = LoadoutSaveSlot.IdOf(loadout);
        rig.Saves.Redirect = new SaveRedirectPlan.IniKey("General", "sLocalSavePath", $@"Saves\{id}\");
        rig.FileSystem.WriteAllText(rig.Root / "Saves" / id / "one.ess", "a");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.No);
        await settings.ToggleKeepSavesCommand.Execute();

        settings.FindSaveLocations().Should().Equal(rig.Root / "Saves" / id);
    }

    [Theory]
    [InlineData(@"Saves\..\")]
    [InlineData(@"..\Saves\")]
    [InlineData(@"Saves\")]
    [InlineData(@"\Saves\{id}\")]
    public async Task FindSaveLocations_IgnoresAPathThatIsNotInsideTheRootOrNotNamedForTheLoadout(string value)
    {
        var rig = new Rig(Provider);
        var loadout = await SeedLoadout("Locations Unsafe " + value.Length);
        var id = LoadoutSaveSlot.IdOf(loadout);
        rig.Saves.Redirect = new SaveRedirectPlan.IniKey("General", "sLocalSavePath", value.Replace("{id}", id));
        rig.FileSystem.CreateDirectory(rig.Root / "Saves");
        rig.FileSystem.WriteAllText(rig.Root / "Saves" / "keep.ess", "a");
        var settings = rig.Settings(loadout);
        rig.Answer(ButtonDefinitionId.No);
        await settings.ToggleKeepSavesCommand.Execute();

        settings.FindSaveLocations().Should().BeEmpty();
    }
}

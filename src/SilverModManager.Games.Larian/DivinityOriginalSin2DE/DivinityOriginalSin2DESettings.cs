using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSin2DE;

public class DivinityOriginalSin2DESettings : ISettings
{
    /// <summary>
    /// If true, the contents of the game folder will be backed up. If the game updates
    /// the loadout may become invalid. If mods are installed into this folder via the app they
    /// will still be backed up as needed
    /// </summary>
    public bool DoFullGameBackup { get; set; } = false;

    /// <summary>
    /// The player profile whose <c>modsettings.lsx</c> the app writes: the name of a folder under
    /// <c>PlayerProfiles</c>. Only needed when there is more than one.
    /// </summary>
    public string ProfileName { get; set; } = "";

    public static string SettingsId => "SilverModManager.Games.Larian.DivinityOriginalSin2DE.DivinityOriginalSin2DESettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureProperty(
                x => x.ProfileName,
                new PropertyOptions<DivinityOriginalSin2DESettings, string>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Divinity: Original Sin 2: profile",
                    DescriptionFactory = _ => "The name of the player profile folder (under PlayerProfiles) whose mod list the app writes. Only needed when the game has more than one profile.",
                },
                new TextContainerOptions()
            )
            .ConfigureProperty(
                x => x.DoFullGameBackup,
                new PropertyOptions<DivinityOriginalSin2DESettings, bool>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Full game backup: Divinity: Original Sin 2",
                    DescriptionFactory = _ => "Backup all game folders, this will greatly increase disk space usage. Should only be changed before managing the game.",
                },
                new BooleanContainerOptions()
            );
    }
}

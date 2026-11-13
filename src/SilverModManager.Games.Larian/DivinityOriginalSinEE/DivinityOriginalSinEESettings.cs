using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.Larian.DivinityOriginalSinEE;

public class DivinityOriginalSinEESettings : ISettings
{
    /// <summary>
    /// If true, the contents of the game folder will be backed up. If the game updates
    /// the loadout may become invalid. If mods are installed into this folder via the app they
    /// will still be backed up as needed
    /// </summary>
    public bool DoFullGameBackup { get; set; } = false;

    public static string SettingsId => "SilverModManager.Games.Larian.DivinityOriginalSinEE.DivinityOriginalSinEESettings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder
            .ConfigureProperty(
                x => x.DoFullGameBackup,
                new PropertyOptions<DivinityOriginalSinEESettings, bool>
                {
                    Section = Sections.Experimental,
                    DisplayName = "Full game backup: Divinity: Original Sin Enhanced Edition",
                    DescriptionFactory = _ => "Backup all game folders, this will greatly increase disk space usage. Should only be changed before managing the game.",
                },
                new BooleanContainerOptions()
            );
    }
}

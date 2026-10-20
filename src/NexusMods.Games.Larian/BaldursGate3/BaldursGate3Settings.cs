using NexusMods.Sdk.Settings;

namespace NexusMods.Games.Larian.BaldursGate3;

public class BaldursGate3Settings : ISettings
{
    /// <summary>
    /// If true, the contents of the game folder will be backed up. If the game updates
    /// the loadout may become invalid. If mods are installed into this folder via the app they
    /// will still be backed up as needed
    /// </summary>
    public bool DoFullGameBackup { get; set; } = false;
    
    public static string SettingsId => "SilverModManager.Games.Larian.BaldursGate3.BaldursGate3Settings";

    public static ISettingsBuilder Configure(ISettingsBuilder settingsBuilder)
    {
        return settingsBuilder.ConfigureProperty(
            x => x.DoFullGameBackup,
            new PropertyOptions<BaldursGate3Settings, bool>
            {
                Section = Sections.Experimental,
                DisplayName = "Full game backup: Baldur's Gate 3",
                DescriptionFactory = _ => "Backup all game folders, this will greatly increase disk space usage. Should only be changed before managing the game.",
            },
            new BooleanContainerOptions()
        );
    }
}

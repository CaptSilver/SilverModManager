namespace NexusMods.Games.Bethesda.LoadOrder;

/// <summary>
/// The header layout a game's plugins use. It decides the record header size
/// (16, 20 or 24 bytes) and the subrecord length width (u32 for Tes3, u16 otherwise).
/// </summary>
public enum PluginFormat
{
    Tes3,
    Tes4Oblivion,
    Tes4,
}

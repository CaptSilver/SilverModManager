using Mutagen.Bethesda;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Tests;

namespace NexusMods.Games.CreationEngine.Tests;

/// <summary>Small real plugins for any release Mutagen reads: an empty mod whose header lists its masters.</summary>
internal static class BethesdaPluginBytes
{
    public static byte[] Bytes(GameRelease release, string fileName, params string[] masters)
    {
        if (release == GameRelease.Starfield)
            return PluginBytes.Write(PluginFormat.Tes4, new PluginSpec { Masters = masters });

        var key = ModKey.FromFileName(fileName);
        IModGetter mod;
        switch (release)
        {
            case GameRelease.Fallout4 or GameRelease.Fallout4VR:
                var fallout = new Fallout4Mod(key, release.ToFallout4Release());
                foreach (var master in masters) fallout.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName(master) });
                mod = fallout;
                break;
            default:
                var skyrim = new SkyrimMod(key, release.ToSkyrimRelease());
                foreach (var master in masters) skyrim.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName(master) });
                mod = skyrim;
                break;
        }

        var temp = Path.Combine(Path.GetTempPath(), $"bethesda-plugin-{Guid.NewGuid():N}-{fileName}");
        try
        {
            mod.WriteToBinary(temp, new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
            });
            return File.ReadAllBytes(temp);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}

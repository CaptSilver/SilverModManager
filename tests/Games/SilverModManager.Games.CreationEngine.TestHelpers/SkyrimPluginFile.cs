using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace SilverModManager.Games.CreationEngine.Tests.SkyrimSE;

/// <summary>Small real Skyrim SE plugins: an empty mod with a header that lists its masters.</summary>
internal static class SkyrimPluginFile
{
    public static byte[] Bytes(string fileName, params string[] masters)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(fileName), SkyrimRelease.SkyrimSE);
        foreach (var master in masters)
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromFileName(master) });

        var temp = Path.Combine(Path.GetTempPath(), $"skyrim-plugin-{Guid.NewGuid():N}-{fileName}");
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

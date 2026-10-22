using System.Diagnostics;
using NexusMods.Games.Bethesda.Loot.Masterlists;
using NexusMods.Games.Bethesda.Loot.Metadata;

namespace NexusMods.Games.Bethesda.Loot.Tests.Masterlists;

/// <summary>
/// Times parsing the largest bundled masterlist. If the median passes ~400 ms, parsing once per
/// masterlist revision and keeping the document in the store is the next step; the bound here only
/// catches a parse that has gone badly wrong.
/// </summary>
public class MasterlistLoadTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void SkyrimSEMasterlistLoadsWithThePreludeWellUnderTwoSeconds()
    {
        var store = new BundledMasterlistStore();
        var masterlist = store.MasterlistText("skyrimse");
        var prelude = store.PreludeText();

        var timings = new List<double>();
        for (var run = 0; run < 3; run++)
        {
            var sw = Stopwatch.StartNew();
            MetadataDocument.LoadWithPrelude(masterlist, prelude);
            sw.Stop();
            timings.Add(sw.Elapsed.TotalMilliseconds);
        }

        var median = timings.Order().ElementAt(1);
        output.WriteLine($"skyrimse masterlist with prelude: median {median:F0} ms over runs {string.Join(", ", timings.Select(t => $"{t:F0} ms"))}");
        median.Should().BeLessThan(2000);
    }
}

using FluentAssertions;
using NexusMods.Backend;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

internal static class SettingRows
{
    public static bool Has(IConnection connection, string name)
        => Setting.All(connection.Db).Any(x => x.Name == name);

    public static async Task WaitFor(IConnection connection, string name)
    {
        // The MnemonicDB backend saves on the scheduler, so wait for the row.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !Has(connection, name)) await Task.Delay(50);
        Has(connection, name).Should().BeTrue();
    }
}

using FluentAssertions;
using NexusMods.Networking.ModUpdates.Tests.Helpers;
using NexusMods.Networking.NexusWebApi;

namespace NexusMods.Networking.ModUpdates.Tests;

/// <summary>
/// The update check on a page whose files are parallel variants of one mod.
/// </summary>
public class VariantUpdateSelectionTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ANewerSiblingVariantIsNotAnUpdate()
    {
        var page = MockModPage.Create(p =>
        {
            p.AddFile("NOFALLDEAD 10402 1 2026-07-15T03-58Z 2fNK8JduR.zip", "1", BaseTime);
            p.AddFile("NoFallDead Longtail Cat Version 10402 1 2026-07-15T04-00Z HqR4yjEgC.zip", "1", BaseTime.AddDays(1));
        });

        RunUpdateCheck.GetNewerFilesForExistingFile(page.Files[0]).Should().BeEmpty();
    }

    [Fact]
    public void ANewerUploadOfTheSameVariantIsAnUpdateInTheCurrentFileNameShape()
    {
        var page = MockModPage.Create(p =>
        {
            p.AddFile("Vanilla or Randomizer - v2.0.5 10062 v2.0.5 2026-07-14T10-47Z ZTHtIFwXR.zip", "v2.0.5", BaseTime);
            p.AddFile("Graceborne - v2.1.2 10062 v2.1.2 2026-08-06T08-51Z ZTHtIFfdq.zip", "v2.1.2", BaseTime.AddDays(30));
            p.AddFile("Vanilla or Randomizer - v2.1.2 10062 v2.1.2 2026-08-06T08-53Z ZTHtIFwXQ.zip", "v2.1.2", BaseTime.AddDays(30));
        });

        var result = RunUpdateCheck.GetNewerFilesForExistingFile(page.Files[0]).ToArray();

        result.Should().ContainSingle().Which.Version.Should().Be("v2.1.2");
        result[0].Name.Should().StartWith("Vanilla or Randomizer");
    }

    [Fact]
    public void ALegacyPinSeesTheCurrentShapeUploadOfItsOwnVariant()
    {
        var page = MockModPage.Create(p =>
        {
            p.AddFile("moveset_modpack 25.0-1928-25-0-1738532654.zip", "25.0", BaseTime);
            p.AddFile("moveset_modpack 26.1 1928 26.1 2026-08-27T23-54Z xzSjGqMUM.zip", "26.1", BaseTime.AddDays(30));
        });

        RunUpdateCheck.GetNewerFilesForExistingFile(page.Files[0]).Should().ContainSingle();
    }

    [Fact]
    public void AFrozenFileHasNoUpdate()
    {
        var page = MockModPage.Create(p =>
        {
            p.AddFile("moveset_modpack 25.0-1928-25-0-1738532654.zip", "25.0", BaseTime);
            p.AddFile("moveset_modpack 26.1 1928 26.1 2026-08-27T23-54Z xzSjGqMUM.zip", "26.1", BaseTime.AddDays(30));
        });

        RunUpdateCheck.GetNewerFilesForExistingFile(page.Files[0], frozen: true).Should().BeEmpty();
        RunUpdateCheck.GetNewerFilesForExistingFile(page.Files[0], frozen: false).Should().ContainSingle();
    }
}

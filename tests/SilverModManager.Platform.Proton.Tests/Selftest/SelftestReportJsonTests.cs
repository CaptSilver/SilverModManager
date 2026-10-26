using System.Text.Json;
using FluentAssertions;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Platform.Proton.Selftest;

namespace SilverModManager.Platform.Proton.Tests.Selftest;

/// <summary>
/// What the <c>--json</c> forms of the selftest and doctor reports do with punctuation in a sentence.
/// </summary>
public class SelftestReportJsonTests
{
    [Fact]
    public void SelftestDetailKeepsAnApostropheLiterally()
    {
        var report = new SelftestReport([new SelftestCheck("steam", SelftestStatus.Failed, true, "Steam's Play button is missing")]);

        var json = report.ToJson();

        json.Should().Contain("Steam's Play button is missing").And.NotContain("\\u0027");
        JsonDocument.Parse(json).RootElement.GetProperty("checks")[0].GetProperty("status").GetString().Should().Be("failed");
    }

    [Fact]
    public void DoctorProblemKeepsAnApostropheLiterally()
    {
        var report = new DoctorReport([]) { Problems = ["Baldur's Gate 3 is installed twice"] };

        report.ToJson().Should().Contain("Baldur's Gate 3 is installed twice").And.NotContain("\\u0027");
    }
}

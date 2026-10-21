using FluentAssertions;
using NexusMods.Games.FromSoft.Me3;

namespace NexusMods.Games.FromSoft.Tests;

public class SeamlessCoopSettingsTests
{
    [Fact]
    public void ThePlaceholderIsReplacedByThePassword()
    {
        SeamlessCoopSettings.Inject("[PASSWORD]\ncooppassword = @COOP_PASSWORD@\n", "hunter2").Should().Be("[PASSWORD]\ncooppassword = hunter2\n");
    }

    [Fact]
    public void TheCooppasswordLineIsRewrittenWhenThereIsNoPlaceholder()
    {
        var injected = SeamlessCoopSettings.Inject("[PASSWORD]\ncooppassword = \n[SAVE]\nsave_file_extension = co2\n", "hunter2");

        injected.Should().Be("[PASSWORD]\ncooppassword = hunter2\n[SAVE]\nsave_file_extension = co2\n");
    }

    [Fact]
    public void AnExistingPasswordIsOverwritten()
    {
        SeamlessCoopSettings.Inject("cooppassword = old\n", "new").Should().Be("cooppassword = new\n");
    }

    [Fact]
    public void ALayoutWithNowhereToPutThePasswordIsRefusedLoudly()
    {
        var inject = () => SeamlessCoopSettings.Inject("[PASSWORD]\nsecret = \n", "hunter2");

        inject.Should().Throw<InvalidOperationException>().WithMessage("*cooppassword*");
    }

    [Fact]
    public void WithNoPasswordAnUnrecognisedLayoutIsReturnedAsItIs()
    {
        const string template = "[PASSWORD]\nsecret = \n";

        SeamlessCoopSettings.Inject(template, null).Should().Be(template);
        SeamlessCoopSettings.Inject(template, "").Should().Be(template);
    }

    [Fact]
    public void WithNoPasswordTheTemplateKeepsAnEmptyPasswordLine()
    {
        const string template = "[PASSWORD]\ncooppassword = \n";

        SeamlessCoopSettings.Inject(template, null).Should().Be(template);
    }

    [Fact]
    public void WithNoPasswordAPlaceholderIsBlankedAndNeverLeftAsTheLiteralPassword()
    {
        SeamlessCoopSettings.Inject("cooppassword = @COOP_PASSWORD@\n", null).Should().Be("cooppassword = \n");
    }

    [Fact]
    public void APasswordWithRegexReplacementCharactersIsWrittenLiterally()
    {
        SeamlessCoopSettings.Inject("cooppassword = \n", "p$1\\w$").Should().Be("cooppassword = p$1\\w$\n");
    }

    [Fact]
    public void WindowsLineEndingsSurviveTheRewrite()
    {
        SeamlessCoopSettings.Inject("[PASSWORD]\r\ncooppassword = \r\n[SAVE]\r\n", "x").Should().Be("[PASSWORD]\r\ncooppassword = x\r\n[SAVE]\r\n");
    }
}

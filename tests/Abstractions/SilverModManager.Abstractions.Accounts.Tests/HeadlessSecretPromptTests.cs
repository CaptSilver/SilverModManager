using FluentAssertions;

namespace SilverModManager.Abstractions.Accounts.Tests;

public class HeadlessSecretPromptTests
{
    [Fact]
    public async Task TheHeadlessPromptRefusesNamingTheSettingsRow()
    {
        var prompt = new HeadlessSecretPrompt();

        var thrown = await prompt
            .Invoking(p => p.PromptAsync("GitHub", "Paste a token.", page: null, (_, _) => ValueTask.FromResult<string?>(null), CancellationToken.None).AsTask())
            .Should().ThrowAsync<SecretPromptUnavailableException>();

        thrown.Which.Message.Should().Contain("Settings → Accounts → GitHub");
        thrown.Which.Title.Should().Be("Not signed in");
    }

    [Fact]
    public void TheRowNameIsEscapedSoItCannotBecomeALink()
    {
        new SecretPromptUnavailableException("[x](http://evil.example)").Message.Should().NotContain("[x](");
    }
}

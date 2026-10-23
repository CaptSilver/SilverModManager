using SilverModManager.Games.Bethesda.Loot.Metadata;

namespace SilverModManager.Games.Bethesda.Loot.Tests.Metadata;

// Test names follow libloot's metadata/message.rs; its YAML and emitter tests are not ported here.
public class MessageTests
{
    private static MessageContent Content(string text, string language) => new(text) { Language = language };

    public class SelectMessageContentTests
    {
        [Fact]
        public void ShouldReturnNullIfTheSliceIsEmpty()
        {
            MessageContent.Select([], MessageContent.DefaultLanguage).Should().BeNull();
        }

        [Fact]
        public void ShouldReturnTheOnlyElementOfASingleElementSlice()
        {
            var slice = new[] { Content("test", "de") };

            MessageContent.Select(slice, "fr").Should().BeSameAs(slice[0]);
        }

        [Fact]
        public void ShouldReturnElementWithExactlyMatchingLocaleCode()
        {
            var slice = new[]
            {
                Content("test1", "en"), Content("test2", "de"), Content("test3", "pt"),
                Content("test4", "pt_PT"), Content("test5", "pt_BR"),
            };

            MessageContent.Select(slice, "pt_BR").Should().BeSameAs(slice[4]);
        }

        [Fact]
        public void ShouldReturnElementWithMatchingLanguageCodeIfExactlyMatchingLocalCodeIsNotPresent()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de"), Content("test3", "pt"), Content("test4", "pt_PT") };

            MessageContent.Select(slice, "pt_BR").Should().BeSameAs(slice[2]);
        }

        [Fact]
        public void ShouldReturnElementWithEnLanguageCodeIfNoMatchingLanguageCodeIsPresent()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de"), Content("test3", "pt_PT") };

            MessageContent.Select(slice, "pt_BR").Should().BeSameAs(slice[0]);
        }

        [Fact]
        public void ShouldReturnElementWithExactlyMatchingLanguageCodeIfLanguageCodeIsGiven()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de"), Content("test3", "pt_BR"), Content("test4", "pt") };

            MessageContent.Select(slice, "pt").Should().BeSameAs(slice[3]);
        }

        [Fact]
        public void ShouldReturnFirstElementWithMatchingLanguageCodeIfLanguageCodeIsGivenAndNoExactMatchIsPresent()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de"), Content("test3", "pt_PT"), Content("test4", "pt_BR") };

            MessageContent.Select(slice, "pt").Should().BeSameAs(slice[2]);
        }

        [Fact]
        public void ShouldReturnNullIfThereIsNoMatchAndNoEnglishText()
        {
            var slice = new[] { Content("test2", "de"), Content("test3", "pt_PT"), Content("test4", "pt_BR") };

            MessageContent.Select(slice, "fr").Should().BeNull();
        }

        [Fact]
        public void ShouldNotMatchADifferentRegionalVariantOfTheRequestedLanguage()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de_AT") };

            MessageContent.Select(slice, "de_CH").Should().BeSameAs(slice[0]);
        }

        [Fact]
        public void ShouldPreferALanguageCodeMatchToAnEarlierEnglishContent()
        {
            var slice = new[] { Content("test1", "en"), Content("test2", "de") };

            MessageContent.Select(slice, "de_CH").Should().BeSameAs(slice[1]);
        }

        [Fact]
        public void MessageShouldSelectFromItsContent()
        {
            var message = new Message(MessageType.Say, [Content("en text", "en"), Content("de text", "de")]);

            message.SelectMessageContent("de")!.Text.Should().Be("de text");
            message.SelectMessageContent("fr")!.Text.Should().Be("en text");
        }
    }

    public class MessageConstructionTests
    {
        [Fact]
        public void ShouldUseTheDefaultLanguageForASingleContentString()
        {
            var message = new Message(MessageType.Warn, "text");

            message.Type.Should().Be(MessageType.Warn);
            message.Content.Should().Equal(new MessageContent("text"));
            message.Condition.Should().BeNull();
        }

        [Fact]
        public void ShouldNotThrowIfOneContentObjectIsGivenAndItIsNotEnglish()
        {
            var message = new Message(MessageType.Say, [Content("content1", "fr")]);

            message.Content.Should().Equal(Content("content1", "fr"));
        }

        [Fact]
        public void ShouldThrowIfMultipleContentsAreGivenAndNoneAreEnglish()
        {
            var act = () => new Message(MessageType.Say, [Content("content1", "de"), Content("content2", "fr")]);

            act.Should().Throw<MultilingualMessageContentException>();
        }

        [Fact]
        public void ShouldCompareByValue()
        {
            new Message(MessageType.Say, "a") { Condition = "c" }.Should().Be(new Message(MessageType.Say, "a") { Condition = "c" });
            new Message(MessageType.Say, "a").Should().NotBe(new Message(MessageType.Warn, "a"));
        }
    }

    public class FormatTests
    {
        [Fact]
        public void ShouldTreatInvalidPlaceholdersAsLiterals()
        {
            foreach (var input in new[]
                     {
                         "a{", "a{a", "a{}", "a{a}", "a}", "a{ }", "a{0a}", "a{{", "a{b{", "a{b{c", "a}}b", "a}b}", "a}b}c",
                     })
            {
                Message.Format(input, []).Should().Be(input, "{0} has no valid placeholder", input);
            }

            Message.Format("a{{0}", ["b"]).Should().Be("a{b");
            Message.Format("a{0}}", ["b"]).Should().Be("ab}");
        }

        [Fact]
        public void AllowsLeadingZeroesInPlaceholders()
        {
            Message.Format("a{0}-{01}", ["b", "c"]).Should().Be("ab-c");
        }

        [Fact]
        public void ShouldSubstituteIntoTheMiddleOfText()
        {
            Message.Format("con{0}tent1", ["sub1"]).Should().Be("consub1tent1");
        }

        [Fact]
        public void ShouldThrowIfAPlaceholderHasNoSubstitution()
        {
            var act = () => Message.Format("{0} {1}", ["sub1"]);

            act.Should().Throw<MessageFormatException>().WithMessage("*\"{1}\"*");
        }

        [Fact]
        public void ShouldThrowIfASubstitutionHasNoPlaceholder()
        {
            var act = () => Message.Format("content1", ["sub1"]);

            act.Should().Throw<MessageFormatException>().WithMessage("*\"sub1\"*{0}*");
        }

        [Fact]
        public void ShouldThrowIfAWellFormedPlaceholderHasNoSubstitutionsAtAll()
        {
            // libloot's YAML reader skips formatting when a message has no subs, so "{0}" survives there.
            var act = () => Message.Format("content {0}", []);

            act.Should().Throw<MessageFormatException>();
        }

        [Fact]
        public void ShouldTreatAnOverflowingIndexAsALiteral()
        {
            Message.Format("a{99999999999999999999}", []).Should().Be("a{99999999999999999999}");
        }
    }
}

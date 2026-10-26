using System.Text;
using FluentAssertions;
using SilverModManager.Sdk.Ini;

namespace SilverModManager.Sdk.Tests.Ini;

public class IniDocumentTests
{
    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Ini", "Fixtures", name));

    private static string Text(IniDocument document) => Encoding.UTF8.GetString(document.ToBytes()).TrimStart('﻿');

    [Theory]
    [InlineData("utf8bom-crlf.ini")]
    [InlineData("utf16le.ini")]
    [InlineData("latin1.ini")]
    [InlineData("lf-only.ini")]
    [InlineData("inline-comment.ini")]
    [InlineData("duplicate-keys.ini")]
    [InlineData("global-keys.ini")]
    [InlineData("no-final-newline.ini")]
    [InlineData("mixed-endings.ini")]
    public void ARoundTripWithoutChangesIsByteIdentical(string fixture)
    {
        var bytes = Fixture(fixture);

        IniDocument.Parse(bytes).ToBytes().Should().Equal(bytes);
    }

    [Fact]
    public void ARoundTripOfAnEmptyFileIsEmpty() =>
        IniDocument.Parse([]).ToBytes().Should().BeEmpty();

    [Theory]
    [InlineData("utf8bom-crlf.ini", IniEncoding.Utf8Bom, "\r\n")]
    [InlineData("utf16le.ini", IniEncoding.Utf16LittleEndian, "\r\n")]
    [InlineData("latin1.ini", IniEncoding.Latin1, "\r\n")]
    [InlineData("lf-only.ini", IniEncoding.Utf8, "\n")]
    public void EncodingAndNewlineStyleAreDetected(string fixture, IniEncoding encoding, string newline)
    {
        var document = IniDocument.Parse(Fixture(fixture));

        document.Encoding.Should().Be(encoding);
        document.NewLine.Should().Be(newline);
    }

    [Fact]
    public void ALatin1ByteIsReadAsItsCharacter() =>
        IniDocument.Parse(Fixture("latin1.ini")).Get("General", "sPlayerName").Should().Be("Renée");

    [Fact]
    public void Utf16ValuesAreReadAsText() =>
        IniDocument.Parse(Fixture("utf16le.ini")).Get("Display", "iSize W").Should().Be("1920");

    [Fact]
    public void AnEditKeepsTheEncodingOfTheFile()
    {
        var document = IniDocument.Parse(Fixture("utf16le.ini"));
        document.Set("Display", "iSize W", "2560");

        var expected = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("[General]\r\nsLanguage=ENGLISH\r\n[Display]\r\niSize W=2560\r\n"));
        document.ToBytes().Should().Equal(expected);
    }

    [Fact]
    public void AnEditedLatin1FileStaysLatin1()
    {
        var document = IniDocument.Parse(Fixture("latin1.ini"));
        document.Set("General", "sLanguage", "FRENCH");

        document.ToBytes().Should().Contain(0xE9).And.NotContain(0xC3);
    }

    [Fact]
    public void SettingACharacterLatin1CannotHoldRefusesInsteadOfWritingAQuestionMark()
    {
        var document = IniDocument.Parse(Fixture("latin1.ini"));
        document.Set("General", "sPlayerName", "ツ");

        var act = () => document.ToBytes();

        act.Should().Throw<IniEncodingException>().WithMessage("*sPlayerName*");
    }

    [Fact]
    public void SetKeepsTheLinePositionAndTheInlineComment()
    {
        var document = IniDocument.Parse(Fixture("inline-comment.ini"));
        document.Set("General", "sLanguage", "FRENCH");

        Text(document).Should().Be("[General]\r\n; a full line comment\r\nsLanguage=FRENCH ; the game language\r\nbFlag=1;kept\r\n");
    }

    [Fact]
    public void ASemicolonInsideAValueIsPartOfTheValue()
    {
        var document = IniDocument.Parse(Fixture("inline-comment.ini"));

        document.Get("General", "bFlag").Should().Be("1;kept");
        document.Get("General", "sLanguage").Should().Be("ENGLISH");
    }

    [Fact]
    public void DuplicateKeysGetAndSetUseTheFirst()
    {
        var document = IniDocument.Parse(Fixture("duplicate-keys.ini"));

        document.Get("General", "sKey").Should().Be("first");
        document.Set("General", "sKey", "changed");

        Text(document).Should().Be("[General]\r\nsKey=changed\r\nsOther=x\r\nsKey=second\r\n");
    }

    [Fact]
    public void SectionAndKeyNamesIgnoreCase()
    {
        var document = IniDocument.Parse(Fixture("lf-only.ini"));

        document.Get("DISPLAY", "isize w").Should().Be("1920");
        document.Set("display", "ISIZE W", "800");
        Text(document).Should().Be("[General]\nsLanguage=ENGLISH\n\n[Display]\niSize W=800\n");
    }

    [Fact]
    public void KeysBeforeAnySectionLiveInTheUnnamedSection()
    {
        var document = IniDocument.Parse(Fixture("global-keys.ini"));

        document.Get("", "keyBefore").Should().Be("1");
        document.Get("General", "keyBefore").Should().BeNull();
        document.Get("General", "sKey").Should().Be("in section");
    }

    [Fact]
    public void SettingAKeyBeforeAnySectionAppendsItAboveTheFirstHeader()
    {
        var document = IniDocument.Parse(Fixture("global-keys.ini"));
        document.Set("", "third", "3");

        Text(document).Should().Be("keyBefore=1\r\nother=2\r\nthird=3\r\n\r\n[General]\r\nsKey=in section\r\n");
    }

    [Fact]
    public void SetOfANewKeyAppendsToTheSectionBeforeItsTrailingBlankLines()
    {
        var document = IniDocument.Parse(Fixture("lf-only.ini"));
        document.Set("General", "sNew", "1");

        Text(document).Should().Be("[General]\nsLanguage=ENGLISH\nsNew=1\n\n[Display]\niSize W=1920\n");
    }

    [Fact]
    public void SetIntoAMissingSectionCreatesItAtTheEnd()
    {
        var document = IniDocument.Parse(Fixture("lf-only.ini"));
        document.Set("Audio", "fVolume", "0.5");

        Text(document).Should().Be("[General]\nsLanguage=ENGLISH\n\n[Display]\niSize W=1920\n[Audio]\nfVolume=0.5\n");
    }

    [Fact]
    public void AppendingAfterALineWithoutATerminatorGivesItOne()
    {
        var document = IniDocument.Parse(Fixture("no-final-newline.ini"));
        document.Set("General", "sNew", "2");

        Text(document).Should().Be("[General]\r\nsKey=1\r\nsNew=2\r\n");
    }

    [Fact]
    public void RemoveDropsTheLineAndReportsWhetherItWasThere()
    {
        var document = IniDocument.Parse(Fixture("duplicate-keys.ini"));

        document.Remove("General", "sKey").Should().BeTrue();
        document.Remove("General", "missing").Should().BeFalse();

        Text(document).Should().Be("[General]\r\nsOther=x\r\nsKey=second\r\n");
    }

    [Fact]
    public void ANewDocumentWritesCrLf()
    {
        var document = IniDocument.CreateEmpty();
        document.Set("General", "sKey", "1");

        document.ToBytes().Should().Equal(Encoding.UTF8.GetBytes("[General]\r\nsKey=1\r\n"));
    }

    [Fact]
    public void MergeAppliesEveryKeyUnderASectionAndSkipsKeysOutsideOne()
    {
        var document = IniDocument.Parse(Fixture("lf-only.ini"));
        var tweak = IniDocument.Parse(Encoding.UTF8.GetBytes("stray=1\n[Display]\niSize W=3840\nbBorderless=1\n[Audio]\nfVolume=0.2\n"));

        document.Merge(tweak);

        Text(document).Should().Be("[General]\nsLanguage=ENGLISH\n\n[Display]\niSize W=3840\nbBorderless=1\n[Audio]\nfVolume=0.2\n");
        document.Get("", "stray").Should().BeNull();
    }

    [Fact]
    public void ReplaceKeysPutsTheNewLinesWhereTheFirstOldOneWas()
    {
        var document = IniDocument.Parse(Encoding.UTF8.GetBytes("[S]\nGameFile0=a\nkeep=1\nGameFile1=b\n[T]\nx=y\n"));

        document.ReplaceKeys("S", key => key.StartsWith("GameFile", StringComparison.OrdinalIgnoreCase), [new("GameFile0", "z")]);

        Text(document).Should().Be("[S]\nGameFile0=z\nkeep=1\n[T]\nx=y\n");
    }

    [Fact]
    public void KeysListsEveryKeyOfASectionInOrder()
    {
        var document = IniDocument.Parse(Fixture("duplicate-keys.ini"));

        document.Entries("General").Select(e => $"{e.Key}={e.Value}").Should().Equal("sKey=first", "sOther=x", "sKey=second");
        document.Sections.Should().Equal("General");
    }

    [Fact]
    public void AFileWithAUtf8MarkButInvalidBytesIsReadByteForByteAndRoundTrips()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("[General]\r\nsPlayerName=Ren\u00e9e\r\n")];

        var document = IniDocument.Parse(bytes);

        document.Get("General", "sPlayerName").Should().Be("Ren\u00e9e");
        document.ToBytes().Should().Equal(bytes);
        document.Set("General", "sPlayerName", "Zo\u00eb");
        document.ToBytes().Should().Equal([0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes("[General]\r\nsPlayerName=Zo\u00eb\r\n")]);
    }
}

using FluentAssertions;
using SilverModManager.Secrets.Overlay;
using Xunit;

namespace SilverModManager.Secrets.Tests.Overlay;

public class SecretsFileReaderTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("secrets-file-reader-").FullName;

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    private static SecretsFileContents Parse(params string[] lines) => SecretsFileReader.Parse(lines);

    [Fact]
    public void Plain_assignments_are_read()
    {
        var result = Parse("NEXUS_API_KEY=abc", "GITHUB_TOKEN=def");

        result.Error.Should().BeNull();
        result.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["NEXUS_API_KEY"] = "abc", ["GITHUB_TOKEN"] = "def" });
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var result = Parse("# a comment", "", "   ", "  # indented comment", "A=1");

        result.Error.Should().BeNull();
        result.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["A"] = "1" });
    }

    [Fact]
    public void An_export_prefix_is_accepted()
    {
        Parse("export A=1", "  export   B=2").Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
    }

    [Theory]
    [InlineData("A=\"quoted value\"", "quoted value")]
    [InlineData("A='quoted value'", "quoted value")]
    [InlineData("A=\"it's\"", "it's")]
    [InlineData("A='say \"hi\"'", "say \"hi\"")]
    [InlineData("A=\"back\\slash\"", "back\\slash")]
    [InlineData("A=\"\"", "")]
    public void A_single_pair_of_quotes_is_removed_and_nothing_inside_is_unescaped(string line, string expected)
    {
        Parse(line).Values["A"].Should().Be(expected);
    }

    [Fact]
    public void An_unquoted_value_is_verbatim_to_the_end_of_the_line_with_trailing_whitespace_trimmed()
    {
        Parse("A=value with spaces # not a comment   \t", "B=x=y").Values.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["A"] = "value with spaces # not a comment", ["B"] = "x=y" });
    }

    [Fact]
    public void Carriage_returns_from_windows_line_endings_are_dropped()
    {
        Parse("A=1\r", "B=\"2\"\r").Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" });
    }

    [Fact]
    public void A_later_assignment_replaces_an_earlier_one()
    {
        Parse("A=1", "A=2").Values["A"].Should().Be("2");
    }

    [Theory]
    [InlineData("no equals sign here")]
    [InlineData("=value")]
    [InlineData("1BAD=value")]
    [InlineData("A B=value")]
    [InlineData("A=\"unterminated")]
    [InlineData("A='mismatched\"")]
    [InlineData("A=\"closed\" trailing")]
    public void A_malformed_line_reports_its_number(string bad)
    {
        var result = Parse("# header", "OK=1", bad, "ALSO=2");

        result.Error.Should().NotBeNull();
        result.Error!.Line.Should().Be(3);
        result.Error.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Read_returns_the_file_contents()
    {
        var path = Path.Combine(_scratch, "secrets.env");
        System.IO.File.WriteAllText(path, "A=1\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var result = new SecretsFileReader(path, new CapturingLogger()).Read();

        result.Error.Should().BeNull();
        result.Values["A"].Should().Be("1");
    }

    [Fact]
    public void A_group_or_other_readable_file_is_read_with_one_warning_naming_path_and_mode()
    {
        var path = Path.Combine(_scratch, "loose.env");
        System.IO.File.WriteAllText(path, "A=1\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var logger = new CapturingLogger();

        var result = new SecretsFileReader(path, logger).Read();

        result.Values["A"].Should().Be("1");
        logger.Warnings.Should().ContainSingle().Which.Should().Contain(path).And.Contain("644");
    }

    [Fact]
    public void An_owner_only_file_logs_no_warning()
    {
        var path = Path.Combine(_scratch, "tight.env");
        System.IO.File.WriteAllText(path, "A=1\n");
        System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var logger = new CapturingLogger();

        new SecretsFileReader(path, logger).Read();

        logger.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_file_is_an_error_not_an_empty_file()
    {
        var path = Path.Combine(_scratch, "missing.env");

        var result = new SecretsFileReader(path, new CapturingLogger()).Read();

        result.Error.Should().NotBeNull();
        result.Error!.Message.Should().Contain(path);
    }
}

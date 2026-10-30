using System.Text.RegularExpressions;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

/// <summary>
/// Keeps nullable warnings fatal in every project under <c>src</c>. The promotion lives in one place,
/// <c>src/Directory.Build.props</c> (<c>WarningsAsErrors=Nullable</c>); this guard fails if that line goes
/// or if anything else can quietly undo it: a project, or the root <c>Directory.Build.props</c>,
/// <c>Directory.Build.targets</c> or <c>Directory.Packages.props</c> every src project imports, that sets its own <c>WarningsAsErrors</c>,
/// <c>TreatWarningsAsErrors</c> or <c>WarningsNotAsErrors</c>, turns <c>Nullable</c> off, lists a CS86xx or
/// CS87xx code in <c>NoWarn</c>, or an <c>.editorconfig</c> / <c>.globalconfig</c> that lowers such a code
/// below warning (<c>none</c>, <c>silent</c>, <c>suggestion</c> and <c>default</c> all bypass the promotion
/// without touching a project file).
/// <para>
/// The vendored MnemonicDB projects are scanned too. The <c>!</c> guard leaves their code to upstream, but
/// they build under <c>src/Directory.Build.props</c> and ship with the app, so a loosening in one of their
/// project files would still reach us.
/// </para>
/// </summary>
public class NullableBuildSettingsGuardTests
{
    private const string PromotionFile = "src/Directory.Build.props";

    private static readonly string[] ProjectPatterns = ["*.csproj", "*.props", "*.targets"];

    // Imported by every src project (src/Directory.Build.props pulls the root props in), so a loosening here
    // reaches all of them without touching a csproj.
    private static readonly string[] RootBuildFiles = ["Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props"];

    private static readonly Regex Property = new(@"<(?<name>WarningsAsErrors|WarningsNotAsErrors|TreatWarningsAsErrors|Nullable|NoWarn)\b[^>]*>(?<value>[^<]*)</\k<name>>", RegexOptions.IgnoreCase);
    private static readonly Regex NullableCode = new(@"^(CS)?8[67]\d\d$|^nullable$", RegexOptions.IgnoreCase);
    private static readonly Regex SeverityLine = new(@"^\s*dotnet_diagnostic\.CS8[67]\d\d\.severity\s*=\s*(?<value>\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static IEnumerable<string> Tokens(string value) => value.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// The properties in a project, props or targets file that can undo the promotion of nullable warnings.
    /// <paramref name="allowWarningsAsErrors"/> is true only for the one file that sets it on purpose.
    /// </summary>
    internal static string[] ProjectFileViolations(string xml, bool allowWarningsAsErrors = false)
    {
        var violations = new List<string>();
        foreach (Match match in Property.Matches(xml))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["value"].Value.Trim();
            switch (name.ToLowerInvariant())
            {
                case "warningsaserrors" when !allowWarningsAsErrors:
                case "warningsnotaserrors":
                case "treatwarningsaserrors":
                    violations.Add(match.Value.Trim());
                    break;
                case "nullable" when !value.Equals("enable", StringComparison.OrdinalIgnoreCase):
                    violations.Add(match.Value.Trim());
                    break;
                case "nowarn" when Tokens(value).Any(token => NullableCode.IsMatch(token)):
                    violations.Add(match.Value.Trim());
                    break;
            }
        }

        return [.. violations];
    }

    /// <summary>Whether the file promotes nullable warnings: a <c>WarningsAsErrors</c> value that includes <c>Nullable</c>.</summary>
    internal static bool PromotesNullable(string xml)
        => Property.Matches(xml).Any(match =>
            match.Groups["name"].Value.Equals("WarningsAsErrors", StringComparison.OrdinalIgnoreCase)
            && Tokens(match.Groups["value"].Value).Any(token => token.Equals("Nullable", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The CS86xx and CS87xx severity lines in an editorconfig or globalconfig that set something other than error or warning.</summary>
    internal static string[] ConfigViolations(string text)
        => SeverityLine.Matches(text)
            .Where(match => !match.Groups["value"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
                         && !match.Groups["value"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Value.Trim())
            .ToArray();

    [Fact]
    public void Project_files_flag_every_way_to_loosen_nullable()
    {
        ProjectFileViolations("<WarningsAsErrors>CS8618</WarningsAsErrors>").Should().HaveCount(1);
        ProjectFileViolations("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>").Should().HaveCount(1);
        ProjectFileViolations("<WarningsNotAsErrors>nullable</WarningsNotAsErrors>").Should().HaveCount(1);
        ProjectFileViolations("<Nullable>disable</Nullable>").Should().HaveCount(1);
        ProjectFileViolations("<Nullable>annotations</Nullable>").Should().HaveCount(1);
        ProjectFileViolations("<NoWarn>CS8618</NoWarn>").Should().HaveCount(1);
        ProjectFileViolations("<NoWarn>$(NoWarn);8602</NoWarn>").Should().HaveCount(1);
        ProjectFileViolations("<NoWarn>CA1822;nullable</NoWarn>").Should().HaveCount(1);
        ProjectFileViolations("<NoWarn Condition=\"'$(Configuration)' == 'Debug'\">CS8714</NoWarn>").Should().HaveCount(1);
    }

    [Fact]
    public void Project_files_allow_settings_that_leave_nullable_alone()
    {
        ProjectFileViolations("<Nullable>enable</Nullable>").Should().BeEmpty();
        ProjectFileViolations("<NoWarn>CS1591;CA1822;CS8509x</NoWarn>").Should().BeEmpty();
        ProjectFileViolations("<NoWarn>CS8509;CS8524</NoWarn>").Should().BeEmpty();
        ProjectFileViolations("<WarningsAsErrors>Nullable</WarningsAsErrors>", allowWarningsAsErrors: true).Should().BeEmpty();
    }

    [Fact]
    public void Promotion_is_recognised_in_a_list_of_values()
    {
        PromotesNullable("<WarningsAsErrors>Nullable</WarningsAsErrors>").Should().BeTrue();
        PromotesNullable("<WarningsAsErrors>$(WarningsAsErrors);nullable</WarningsAsErrors>").Should().BeTrue();
        PromotesNullable("<WarningsAsErrors>CS8618</WarningsAsErrors>").Should().BeFalse();
        PromotesNullable("<PropertyGroup/>").Should().BeFalse();
    }

    [Fact]
    public void Config_files_flag_severities_below_warning()
    {
        ConfigViolations("dotnet_diagnostic.CS8618.severity = none").Should().HaveCount(1);
        ConfigViolations("dotnet_diagnostic.CS8602.severity = suggestion").Should().HaveCount(1);
        ConfigViolations("[*.cs]\ndotnet_diagnostic.CS8714.severity=silent").Should().HaveCount(1);
        ConfigViolations("dotnet_diagnostic.CS8618.severity = default").Should().HaveCount(1);
        ConfigViolations("dotnet_diagnostic.CS8618.severity = error").Should().BeEmpty();
        ConfigViolations("dotnet_diagnostic.CS8602.severity = warning").Should().BeEmpty();
        ConfigViolations("dotnet_diagnostic.CS8524.severity = none").Should().BeEmpty();
    }

    [Fact]
    public void Nullable_warnings_stay_errors_in_every_src_project()
    {
        var root = RepoScanGuard.FindRepoRoot();

        File.Exists(Path.Combine(root, PromotionFile)).Should().BeTrue();
        PromotesNullable(File.ReadAllText(Path.Combine(root, PromotionFile)))
            .Should().BeTrue($"{PromotionFile} must keep <WarningsAsErrors>Nullable</WarningsAsErrors>");

        var rootFiles = RootBuildFiles.Where(name => File.Exists(Path.Combine(root, name)));

        var violations = rootFiles.Concat(RepoScanGuard.EnumerateFiles(root, "src", includeVendored: true, ProjectPatterns))
            .SelectMany(file => ProjectFileViolations(File.ReadAllText(Path.Combine(root, file)), allowWarningsAsErrors: file == PromotionFile)
                .Select(violation => $"{file}: {violation}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string.Join(Environment.NewLine, violations).Should().BeEmpty("nullable warnings are errors in src and in the root build files every src project imports, set once in " + PromotionFile);
    }

    [Fact]
    public void No_config_file_lowers_a_nullable_diagnostic()
    {
        var root = RepoScanGuard.FindRepoRoot();

        var rootConfigs = new[] { ".editorconfig", ".globalconfig" }.Where(name => File.Exists(Path.Combine(root, name)));
        var srcConfigs = RepoScanGuard.EnumerateFiles(root, "src", includeVendored: true, ".editorconfig", ".globalconfig", "*.globalconfig");

        var violations = rootConfigs.Concat(srcConfigs)
            .Distinct()
            .SelectMany(file => ConfigViolations(File.ReadAllText(Path.Combine(root, file))).Select(violation => $"{file}: {violation}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        string.Join(Environment.NewLine, violations).Should().BeEmpty("a severity below warning bypasses WarningsAsErrors");
    }
}

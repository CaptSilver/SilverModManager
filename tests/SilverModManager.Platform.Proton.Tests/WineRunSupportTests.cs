using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.ToolRunner;
using SilverModManager.Sdk;

namespace SilverModManager.Platform.Proton.Tests;

public class WineRunSupportTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _protonDir;
    private AbsolutePath Script => _protonDir.Combine("proton");

    public WineRunSupportTests()
    {
        _protonDir = _fs.FromUnsanitizedFullPath("/steam/steamapps/common/Proton 10.0");
        _fs.CreateDirectory(_protonDir);
        _fs.WriteAllText(Script, "#!/usr/bin/env python3");
    }

    private sealed class FixedEnvironment(string? protonUseArm64) : IEnvironmentReader
    {
        public string? Get(string name) => name == "PROTON_USE_ARM64" ? protonUseArm64 : null;
    }

    private const string Advice = "pick a Proton build with ARM64 support for Test Game in Steam";

    [Fact]
    public void HasArm64Build_IsTrueWhenTheDirectoryExists()
    {
        _fs.CreateDirectory(_protonDir.Combine("files/bin-arm64"));
        WineRunSupport.HasArm64Build(Script).Should().BeTrue();
    }

    [Fact]
    public void HasArm64Build_IsFalseWithoutTheDirectory()
        => WineRunSupport.HasArm64Build(Script).Should().BeFalse();

    [Fact]
    public void HasArm64Build_IsFalseForAFileNamedBinArm64()
    {
        _fs.CreateDirectory(_protonDir.Combine("files"));
        _fs.WriteAllText(_protonDir.Combine("files/bin-arm64"), "");
        WineRunSupport.HasArm64Build(Script).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    public void ProtonArm64Requirement_IsNullWithTheDirectoryAndTheVariableUnsetOrOne(string? value)
    {
        _fs.CreateDirectory(_protonDir.Combine("files/bin-arm64"));
        WineRunSupport.ProtonArm64Requirement(Script, "proton_10", new FixedEnvironment(value), Advice).Should().BeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("")]
    public void ProtonArm64Requirement_NamesTheVariableWhenItIsNotOne(string value)
    {
        _fs.CreateDirectory(_protonDir.Combine("files/bin-arm64"));
        var requirement = WineRunSupport.ProtonArm64Requirement(Script, "proton_10", new FixedEnvironment(value), Advice);
        requirement.Should().Be($"PROTON_USE_ARM64 is set to '{value}', so Proton skips its ARM64 Wine; unset it");
    }

    [Fact]
    public void ProtonArm64Requirement_NamesTheBuildAndTheAdviceWithoutTheDirectory()
    {
        var requirement = WineRunSupport.ProtonArm64Requirement(Script, "proton_10", new FixedEnvironment(null), Advice);
        requirement.Should().Be($"the Proton build proton_10 at '{_protonDir}' has no ARM64 build (files/bin-arm64); {Advice}");
    }
}

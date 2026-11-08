using System.Runtime.InteropServices;
using FluentAssertions;

namespace SilverModManager.Sdk.Tests;

public class ReleaseAssetArchitectureTests
{
    [Fact]
    public void X64IsX86_64AndArm64IsAarch64()
    {
        ReleaseAssetArchitecture.Token(Architecture.X64).Should().Be("x86_64");
        ReleaseAssetArchitecture.Token(Architecture.Arm64).Should().Be("aarch64");
        ReleaseAssetArchitecture.RuntimeIdentifierSuffix(Architecture.X64).Should().Be("-x64");
        ReleaseAssetArchitecture.RuntimeIdentifierSuffix(Architecture.Arm64).Should().Be("-arm64");
        ReleaseAssetArchitecture.ElfMachine(Architecture.X64).Should().Be(0x3e);
        ReleaseAssetArchitecture.ElfMachine(Architecture.Arm64).Should().Be(0xb7);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Wasm)]
    [InlineData(Architecture.S390x)]
    public void OtherCpusHaveNoToken(Architecture architecture)
    {
        ReleaseAssetArchitecture.Token(architecture).Should().BeNull();
        ReleaseAssetArchitecture.RuntimeIdentifierSuffix(architecture).Should().BeNull();
        ReleaseAssetArchitecture.ElfMachine(architecture).Should().BeNull();
    }
}

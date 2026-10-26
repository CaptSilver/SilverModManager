using System.Text;
using FluentAssertions;
using NexusMods.Paths;
using SilverModManager.Platform.Proton.PrefixSetup;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Platform.Proton.Tests.PrefixSetup;

public class PrefixInspectorTests
{
    private readonly InMemoryFileSystem _fs = new(OSInformation.FakeUnix);
    private readonly AbsolutePath _prefixPath;
    private readonly ResolvedPrefix _prefix;

    public PrefixInspectorTests()
    {
        _prefixPath = _fs.FromUnsanitizedFullPath("/prefix/pfx");
        _prefixPath.CreateDirectory();
        _prefix = new ResolvedPrefix(_prefixPath, CompatibilityLayerKind.SteamProton, ProtonAppId: 1);
    }

    private static readonly byte[] PeHeader = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0, 0xFF, 0xFF, 0, 0, (byte)'P', (byte)'E', 0, 0];

    private AbsolutePath System32 => _prefixPath.Combine("drive_c/windows/system32");
    private AbsolutePath SysWow64 => _prefixPath.Combine("drive_c/windows/syswow64");

    private static byte[] Pe(params byte[][] sections) => [.. PeHeader, .. sections.SelectMany(s => s)];

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] VersionResource => Encoding.Unicode.GetBytes("VS_VERSION_INFO");

    private void Write(AbsolutePath path, byte[] bytes)
    {
        path.Parent.CreateDirectory();
        path.WriteAllBytesAsync(bytes).GetAwaiter().GetResult();
    }

    private void WriteText(string relative, string text) => Write(_prefixPath.Combine(relative), Encoding.UTF8.GetBytes(text));

    private void WritePlaceholder(AbsolutePath dir, string dll) => Write(dir.Combine(dll), Pe(Ascii("Wine placeholder DLL")));

    private void WriteNative(AbsolutePath dir, string dll) => Write(dir.Combine(dll), Pe(Ascii(".text native code"), VersionResource));

    private const string VcRedistKey = "[Software\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Installer\\\\Dependencies\\\\VC,redist.x64,amd64,14.50,bundle] 1700000000\n\"DisplayName\"=\"Microsoft Visual C++ 2015-2022 Redistributable\"\n";

    private PrefixComponentStatus InspectOne(PrefixComponentId id)
    {
        var results = new PrefixInspector().Inspect(_prefix, [id]);
        results.Should().ContainSingle().Which.Id.Should().Be(id);
        return results[0];
    }

    private static PrefixComponentId Vc => PrefixComponents.VisualCppRuntime.Id;
    private static PrefixComponentId D3d => PrefixComponents.D3DCompiler47.Id;

    [Fact]
    public void Inspect_EmptyDriveC_IsMissing()
    {
        (_prefixPath.Combine("drive_c")).CreateDirectory();

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Missing);
        InspectOne(D3d).State.Should().Be(PrefixComponentState.Missing);
    }

    [Fact]
    public void Inspect_PlaceholderDllsAreNotEvidence()
    {
        WritePlaceholder(System32, "vcruntime140.dll");
        WritePlaceholder(System32, "msvcp140.dll");
        WritePlaceholder(System32, "d3dcompiler_47.dll");

        var vc = InspectOne(Vc);
        vc.State.Should().Be(PrefixComponentState.Missing);
        vc.Evidence.Should().Contain("placeholder");
        InspectOne(D3d).State.Should().Be(PrefixComponentState.Missing);
    }

    [Fact]
    public void Inspect_BuiltinMarkerIsAlsoAPlaceholder()
    {
        Write(System32.Combine("vcruntime140.dll"), Pe(Ascii("Wine builtin DLL"), VersionResource));
        Write(System32.Combine("msvcp140.dll"), Pe(Ascii("Wine builtin DLL"), VersionResource));

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Missing);
    }

    [Fact]
    public void Inspect_NativeDllsAndRuntimeRegistryKey_AreInstalled()
    {
        WriteNative(System32, "vcruntime140.dll");
        WriteNative(System32, "msvcp140.dll");
        WriteText("system.reg", "WINE REGISTRY Version 2\n\n" + VcRedistKey);

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Installed);
    }

    [Fact]
    public void Inspect_VisualStudioRuntimesKey_CountsAsRegistryEvidence()
    {
        WriteNative(System32, "vcruntime140.dll");
        WriteNative(System32, "msvcp140.dll");
        WriteText("system.reg", "[Software\\\\Microsoft\\\\VisualStudio\\\\14.0\\\\VC\\\\Runtimes\\\\x64] 1700000000\n\"Installed\"=dword:00000001\n");

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Installed);
    }

    [Fact]
    public void Inspect_RedistKeyOnly_IsUnknownBecauseNoNativeFileBacksIt()
    {
        WritePlaceholder(System32, "vcruntime140.dll");
        WriteText("system.reg", VcRedistKey);

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Unknown);
    }

    [Fact]
    public void Inspect_NativeDllWithoutRegistryKey_IsUnknownAndSaysSo()
    {
        WriteNative(System32, "vcruntime140.dll");
        WriteNative(System32, "msvcp140.dll");
        WriteText("system.reg", "WINE REGISTRY Version 2\n");

        var result = InspectOne(Vc);
        result.State.Should().Be(PrefixComponentState.Unknown);
        result.Evidence.Should().Contain("registry");
    }

    [Fact]
    public void Inspect_WinetricksLogOnly_IsUnknown()
    {
        WriteText("winetricks.log", "vcrun2022\r\n");

        var result = InspectOne(Vc);
        result.State.Should().Be(PrefixComponentState.Unknown);
        result.Evidence.Should().Be("winetricks.log only");
    }

    [Fact]
    public void Inspect_WinetricksLogNamingAnAlias_IsUnknown()
    {
        WriteText("winetricks.log", "vcrun2019\n");

        InspectOne(Vc).Evidence.Should().Be("winetricks.log only");
    }

    [Fact]
    public void Inspect_UnrelatedWinetricksVerb_IsStillMissing()
    {
        WriteText("winetricks.log", "corefonts\n");

        InspectOne(Vc).State.Should().Be(PrefixComponentState.Missing);
    }

    [Fact]
    public void Inspect_NativeD3DCompilerWithVersionResource_IsInstalled()
    {
        WriteNative(System32, "d3dcompiler_47.dll");
        WriteNative(SysWow64, "d3dcompiler_47.dll");

        InspectOne(D3d).State.Should().Be(PrefixComponentState.Installed);
    }

    [Fact]
    public void Inspect_D3DCompilerWithoutAVersionResource_IsUnknown()
    {
        Write(System32.Combine("d3dcompiler_47.dll"), Pe(Ascii(".text some stub")));

        InspectOne(D3d).State.Should().Be(PrefixComponentState.Unknown);
    }

    [Fact]
    public void Inspect_D3DCompilerNativeInOneDirectoryAndPlaceholderInTheOther_IsUnknown()
    {
        WriteNative(System32, "d3dcompiler_47.dll");
        WritePlaceholder(SysWow64, "d3dcompiler_47.dll");

        InspectOne(D3d).State.Should().Be(PrefixComponentState.Unknown);
    }

    [Fact]
    public void Inspect_ReturnsOneStatusPerComponentInOrder()
    {
        var results = new PrefixInspector().Inspect(_prefix, [D3d, Vc]);

        results.Select(r => r.Id).Should().Equal(D3d, Vc);
    }
}

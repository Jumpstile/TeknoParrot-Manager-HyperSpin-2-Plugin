using System.Net;
using System.Text;
using System.Xml.Linq;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class ReShadeTests
{
    // Builds a minimal MZ/PE-shaped byte buffer: MZ signature, e_lfanew
    // pointing at a PE header offset, and the machine word the real PE
    // header carries 4 bytes after its own "PE\0\0" signature. Matches
    // exactly what GetExeArchitecture actually reads -- it never validates
    // the "PE\0\0" magic itself, same as the original PowerShell function.
    private static byte[] BuildFakeExe(ushort machine, string asciiPayload)
    {
        const int peOffset = 0x80;
        var buffer = new byte[peOffset + 6 + Encoding.ASCII.GetByteCount(asciiPayload)];
        buffer[0] = 0x4D; // 'M'
        buffer[1] = 0x5A; // 'Z'
        BitConverter.GetBytes(peOffset).CopyTo(buffer, 0x3C);
        Encoding.ASCII.GetBytes("PE\0\0").CopyTo(buffer, peOffset);
        BitConverter.GetBytes(machine).CopyTo(buffer, peOffset + 4);
        Encoding.ASCII.GetBytes(asciiPayload).CopyTo(buffer, peOffset + 6);
        return buffer;
    }

    private static string WriteFakeExe(string path, ushort machine, string asciiPayload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, BuildFakeExe(machine, asciiPayload));
        return path;
    }

    [Theory]
    [InlineData("imports d3d12.dll and dxgi.dll", "d3d12.dll")]
    [InlineData("imports d3d11.dll only", "dxgi.dll")]
    [InlineData("imports dxgi.dll only", "dxgi.dll")]
    [InlineData("imports d3d9.dll only", "d3d9.dll")]
    [InlineData("imports opengl32.dll only", "opengl32.dll")]
    [InlineData("imports nothing recognizable", null)]
    public void GetGameApiDll_detects_expected_dll_from_exe_text(string payload, string? expectedDll)
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, 0x8664, payload);

            Assert.Equal(expectedDll, TeknoParrotProfileScanner.GetGameApiDll(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetExeArchitecture_detects_x64()
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, 0x8664, "");
            Assert.Equal("x64", TeknoParrotProfileScanner.GetExeArchitecture(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetExeArchitecture_detects_x86()
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, 0x014C, "");
            Assert.Equal("x86", TeknoParrotProfileScanner.GetExeArchitecture(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetExeArchitecture_returns_null_for_non_pe_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            File.WriteAllText(path, "not a real executable");
            Assert.Null(TeknoParrotProfileScanner.GetExeArchitecture(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetReShadeTargetInfo_budgieloader_always_uses_opengl32()
    {
        var doc = XDocument.Parse("<GameProfile><EmulatorType>BudgieLoader</EmulatorType></GameProfile>");
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, 0x8664, "imports d3d9.dll");

            var info = TeknoParrotProfileScanner.GetReShadeTargetInfo(doc, path, Path.GetDirectoryName(path)!);

            Assert.Equal("opengl32.dll", info.DllName);
            Assert.True(info.ApiDetected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetReShadeTargetInfo_redirects_to_openparrot_subfolder_when_present()
    {
        var doc = XDocument.Parse("<GameProfile><EmulatorType>OpenParrot</EmulatorType></GameProfile>");
        var exeDir = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(exeDir, "openparrot"));
        var path = Path.Combine(exeDir, "game.exe");
        try
        {
            WriteFakeExe(path, 0x8664, "imports dxgi.dll");

            var info = TeknoParrotProfileScanner.GetReShadeTargetInfo(doc, path, exeDir);

            Assert.Equal(Path.Combine(exeDir, "openparrot"), info.TargetDir);
        }
        finally
        {
            Directory.Delete(exeDir, recursive: true);
        }
    }

    [Fact]
    public void GetReShadeTargetInfo_falls_back_to_dxgi_when_api_not_detected()
    {
        var doc = XDocument.Parse("<GameProfile></GameProfile>");
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, 0x8664, "imports nothing recognizable");

            var info = TeknoParrotProfileScanner.GetReShadeTargetInfo(doc, path, Path.GetDirectoryName(path)!);

            Assert.Equal("dxgi.dll", info.DllName);
            Assert.False(info.ApiDetected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("NoSignature")]
    [InlineData("BadDigest")]
    [InlineData("UntrustedRoot")]
    [InlineData("CertificateExpired")]
    [InlineData("RevokedCertificate")]
    [InlineData("NotSupportedOnThisPlatform")]
    [InlineData("Error")]
    [InlineData("SomeFutureUnknownValue")]
    public void GetSignatureStatusText_returns_non_empty_explanation_for_every_known_status(string status)
    {
        var text = TeknoParrotProfileScanner.GetSignatureStatusText(status);

        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void CheckReShadeDllSignature_does_not_throw_for_a_non_pe_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-reshade-test-" + Guid.NewGuid() + ".dll");
        try
        {
            File.WriteAllText(path, "not a real dll");

            var result = TeknoParrotProfileScanner.CheckReShadeDllSignature(path);

            Assert.False(string.IsNullOrWhiteSpace(result.Status));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task GetReShadeLatestVersionAsync_parses_version_from_html()
    {
        const string html = """<html><body><a href="/ReShade_Setup_6.7.3.exe">Download</a></body></html>""";
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(html, Encoding.UTF8, "text/html")
        }));

        var version = await TeknoParrotProfileScanner.GetReShadeLatestVersionAsync(http);

        Assert.Equal("6.7.3", version);
    }

    [Fact]
    public async Task GetReShadeLatestVersionAsync_returns_null_when_unreachable()
    {
        using var http = new HttpClient(new FakeHttpMessageHandler(_ => throw new HttpRequestException("unreachable")));

        var version = await TeknoParrotProfileScanner.GetReShadeLatestVersionAsync(http);

        Assert.Null(version);
    }

    [Fact]
    public void ApplyReShadeSetup_deploys_detected_dll_to_game_folder()
    {
        using var fixture = new TeknoParrotFixture();
        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), 0x8664, "imports d3d9.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"""
            <GameProfile>
              <GamePath>{exePath}</GamePath>
            </GameProfile>
            """);

        var dllPath = Path.Combine(fixture.RootPath, "ReShade64.dll");
        File.WriteAllText(dllPath, "fake dll contents");
        fixture.Settings.ReShadeSourceDllPath = dllPath;

        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Contains("SomeGame", result.DeployedProfiles);
        Assert.True(File.Exists(Path.Combine(exeDir, "d3d9.dll")));
    }

    [Fact]
    public void ApplyReShadeSetup_dry_run_does_not_write_files()
    {
        using var fixture = new TeknoParrotFixture();
        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), 0x8664, "imports d3d9.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"""
            <GameProfile>
              <GamePath>{exePath}</GamePath>
            </GameProfile>
            """);

        var dllPath = Path.Combine(fixture.RootPath, "ReShade64.dll");
        File.WriteAllText(dllPath, "fake dll contents");
        fixture.Settings.ReShadeSourceDllPath = dllPath;

        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(fixture.Settings, gameCodes: null, dryRun: true);

        Assert.Contains("SomeGame", result.DeployedProfiles);
        Assert.False(File.Exists(Path.Combine(exeDir, "d3d9.dll")));
    }

    [Fact]
    public void ApplyReShadeSetup_skips_x86_game_when_no_32_bit_dll_configured()
    {
        using var fixture = new TeknoParrotFixture();
        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), 0x014C, "imports d3d9.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"""
            <GameProfile>
              <GamePath>{exePath}</GamePath>
            </GameProfile>
            """);

        var dllPath = Path.Combine(fixture.RootPath, "ReShade64.dll");
        File.WriteAllText(dllPath, "fake dll contents");
        fixture.Settings.ReShadeSourceDllPath = dllPath;

        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Contains("SomeGame", result.SkippedProfiles);
        Assert.Empty(result.DeployedProfiles);
    }

    [Fact]
    public void ApplyReShadeSetup_gameCodes_filter_restricts_deployment()
    {
        using var fixture = new TeknoParrotFixture();
        var exeDirA = Path.Combine(fixture.RootPath, "Games", "GameA");
        var exeDirB = Path.Combine(fixture.RootPath, "Games", "GameB");
        var exePathA = WriteFakeExe(Path.Combine(exeDirA, "game.exe"), 0x8664, "imports d3d9.dll");
        var exePathB = WriteFakeExe(Path.Combine(exeDirB, "game.exe"), 0x8664, "imports d3d9.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "GameA.xml"), $"<GameProfile><GamePath>{exePathA}</GamePath></GameProfile>");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "GameB.xml"), $"<GameProfile><GamePath>{exePathB}</GamePath></GameProfile>");

        var dllPath = Path.Combine(fixture.RootPath, "ReShade64.dll");
        File.WriteAllText(dllPath, "fake dll contents");
        fixture.Settings.ReShadeSourceDllPath = dllPath;

        var result = TeknoParrotProfileScanner.ApplyReShadeSetup(fixture.Settings, gameCodes: new[] { "GameA" }, dryRun: false);

        Assert.Contains("GameA", result.DeployedProfiles);
        Assert.DoesNotContain("GameB", result.DeployedProfiles);
        Assert.DoesNotContain("GameB", result.SkippedProfiles);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> responder;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            this.responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}

using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class DgVoodoo2Tests
{
    private static string WriteFakeExe(string path, string asciiPayload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, asciiPayload);
        return path;
    }

    [Theory]
    [InlineData("imports d3d8.dll", new[] { "D3D8" })]
    [InlineData("imports ddraw.dll", new[] { "DDraw" })]
    [InlineData("imports glide2x.dll", new[] { "Glide2x" })]
    [InlineData("imports glide3x.dll", new[] { "Glide3x" })]
    [InlineData("imports glide.dll", new[] { "Glide3x" })]
    [InlineData("imports nothing recognizable", new string[0])]
    public void GetGameLegacyApi_detects_expected_apis(string payload, string[] expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-dgvoodoo2-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, payload);
            Assert.Equal(expected, TeknoParrotProfileScanner.GetGameLegacyApi(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetGameLegacyApi_detects_multiple_apis_at_once()
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-dgvoodoo2-test-" + Guid.NewGuid() + ".exe");
        try
        {
            WriteFakeExe(path, "imports d3d8.dll and ddraw.dll");
            var result = TeknoParrotProfileScanner.GetGameLegacyApi(path);
            Assert.Equal(new[] { "D3D8", "DDraw" }, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EvaluateDgVoodoo2_not_eligible_when_no_apis_detected()
    {
        var result = TeknoParrotProfileScanner.EvaluateDgVoodoo2(Array.Empty<string>(), Path.GetTempPath());

        Assert.False(result.Eligible);
        Assert.True(result.UpToDate);
    }

    [Fact]
    public void EvaluateDgVoodoo2_not_up_to_date_when_required_dll_missing()
    {
        var exeDir = Path.Combine(Path.GetTempPath(), "tpm-dgvoodoo2-test-" + Guid.NewGuid());
        Directory.CreateDirectory(exeDir);
        try
        {
            var result = TeknoParrotProfileScanner.EvaluateDgVoodoo2(new[] { "D3D8" }, exeDir);

            Assert.True(result.Eligible);
            Assert.False(result.UpToDate);
        }
        finally
        {
            Directory.Delete(exeDir, recursive: true);
        }
    }

    [Fact]
    public void EvaluateDgVoodoo2_up_to_date_when_all_required_dlls_present()
    {
        var exeDir = Path.Combine(Path.GetTempPath(), "tpm-dgvoodoo2-test-" + Guid.NewGuid());
        Directory.CreateDirectory(exeDir);
        try
        {
            File.WriteAllText(Path.Combine(exeDir, "D3D8.dll"), "");
            File.WriteAllText(Path.Combine(exeDir, "DDraw.dll"), "");

            var result = TeknoParrotProfileScanner.EvaluateDgVoodoo2(new[] { "D3D8", "DDraw" }, exeDir);

            Assert.True(result.Eligible);
            Assert.True(result.UpToDate);
        }
        finally
        {
            Directory.Delete(exeDir, recursive: true);
        }
    }

    private static string CreateDgVoodoo2SourceDir(TeknoParrotFixture fixture)
    {
        var sourceDir = Path.Combine(fixture.RootPath, "dgVoodoo2");
        Directory.CreateDirectory(sourceDir);
        return sourceDir;
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_reports_no_available_dlls_when_source_folder_empty()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Empty(result.AvailableDlls);
        Assert.Empty(result.DeployedProfiles);
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_deploys_matching_dlls_for_detected_api_by_default()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "real d3d8 dll");
        File.WriteAllText(Path.Combine(sourceDir, "D3DImm.dll"), "real d3dimm dll");
        File.WriteAllText(Path.Combine(sourceDir, "Glide2x.dll"), "real glide2x dll");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports d3d8.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Contains("SomeGame", result.DeployedProfiles);
        Assert.True(File.Exists(Path.Combine(exeDir, "D3D8.dll")));
        Assert.True(File.Exists(Path.Combine(exeDir, "D3DImm.dll")));
        Assert.False(File.Exists(Path.Combine(exeDir, "Glide2x.dll")));
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_default_selection_skips_games_with_no_detected_api()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "real d3d8 dll");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "ModernGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports nothing legacy");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "ModernGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.DoesNotContain("ModernGame", result.DeployedProfiles);
        Assert.DoesNotContain("ModernGame", result.SkippedProfiles);
        Assert.False(File.Exists(Path.Combine(exeDir, "D3D8.dll")));
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_explicit_gameCodes_deploys_all_available_when_no_api_detected()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "real d3d8 dll");
        File.WriteAllText(Path.Combine(sourceDir, "Glide2x.dll"), "real glide2x dll");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "ModernGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports nothing legacy");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "ModernGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: new[] { "ModernGame" }, dryRun: false);

        Assert.Contains("ModernGame", result.DeployedProfiles);
        Assert.True(File.Exists(Path.Combine(exeDir, "D3D8.dll")));
        Assert.True(File.Exists(Path.Combine(exeDir, "Glide2x.dll")));
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_never_overwrites_an_already_deployed_dll()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "new d3d8 dll");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports d3d8.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");
        File.WriteAllText(Path.Combine(exeDir, "D3D8.dll"), "old already-deployed dll");

        TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Equal("old already-deployed dll", File.ReadAllText(Path.Combine(exeDir, "D3D8.dll")));
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_dry_run_does_not_write_files()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "real d3d8 dll");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports d3d8.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: true);

        Assert.Contains("SomeGame", result.DeployedProfiles);
        Assert.False(File.Exists(Path.Combine(exeDir, "D3D8.dll")));
    }

    [Fact]
    public void ApplyDgVoodoo2Setup_global_conf_not_overwritten_but_per_game_conf_always_wins()
    {
        using var fixture = new TeknoParrotFixture();
        var sourceDir = CreateDgVoodoo2SourceDir(fixture);
        File.WriteAllText(Path.Combine(sourceDir, "D3D8.dll"), "real d3d8 dll");
        File.WriteAllText(Path.Combine(sourceDir, "dgVoodoo.conf"), "global conf");
        fixture.Settings.DgVoodoo2SourcePath = sourceDir;

        var presetsDir = Path.Combine(fixture.RootPath, "dgVoodoo2Presets");
        Directory.CreateDirectory(presetsDir);
        File.WriteAllText(Path.Combine(presetsDir, "SomeGame.conf"), "per-game conf");
        fixture.Settings.DgVoodoo2PresetsPath = presetsDir;

        var exeDir = Path.Combine(fixture.RootPath, "Games", "SomeGame");
        var exePath = WriteFakeExe(Path.Combine(exeDir, "game.exe"), "imports d3d8.dll");
        File.WriteAllText(Path.Combine(fixture.UserProfilesPath, "SomeGame.xml"), $"<GameProfile><GamePath>{exePath}</GamePath></GameProfile>");

        var result = TeknoParrotProfileScanner.ApplyDgVoodoo2Setup(fixture.Settings, gameCodes: null, dryRun: false);

        Assert.Equal(1, result.PresetOverrides);
        Assert.Equal("per-game conf", File.ReadAllText(Path.Combine(exeDir, "dgVoodoo.conf")));
    }
}

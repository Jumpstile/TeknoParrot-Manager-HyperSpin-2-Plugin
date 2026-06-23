using System.Text.Json;
using TeknoParrotToolsPlugin;
using Xunit;

namespace TeknoParrotToolsPlugin.Tests;

public class TeknoParrotProfileScannerTests
{
    [Fact]
    public void Scan_parses_user_profiles_and_resolves_paths_from_root()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("InitialD8.exe");
        fixture.WriteProfile("ID8", "Initial D8 Infinity", executable);

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        Assert.Empty(result.Errors);
        Assert.Equal(fixture.RootPath, result.RootPath);
        Assert.Equal(fixture.ExecutablePath, result.ExecutablePath);
        Assert.Equal(fixture.UserProfilesPath, result.UserProfilesPath);
        var game = Assert.Single(result.Games);
        Assert.Equal("ID8", game.ProfileName);
        Assert.Equal("Initial D8 Infinity", game.Title);
        Assert.Equal(executable, game.GamePath);
        Assert.Empty(game.Warnings);
    }

    [Fact]
    public async Task Scan_prefers_description_title_and_reports_health_counts()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Batman.exe");
        fixture.WriteDescriptionProfile("Batman", "Batman Arcade", executable, "Batman.exe");
        fixture.WriteProfile("BrokenProfile", "", Path.Combine(fixture.RootPath, "Missing", "missing.exe"));

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);
        var batman = result.Games.Single(game => game.ProfileName == "Batman");

        Assert.Equal("Batman Arcade", batman.Title);
        var healthResponse = await TeknoParrotToolsPluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "execute",
            data = new
            {
                action = "health_check",
                teknoparrotRootPath = fixture.RootPath,
                gamesRootPath = Path.Combine(fixture.RootPath, "Games")
            }
        }));
        var healthJson = JsonSerializer.Serialize(healthResponse);
        Assert.Contains("\"valid_game_paths\":1", healthJson);
        Assert.Contains("\"broken_game_paths\":1", healthJson);
    }

    [Fact]
    public void Scan_falls_back_to_filename_and_reports_missing_game_path()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("BrokenProfile", "", "");

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        var game = Assert.Single(result.Games);
        Assert.Equal("Broken Profile", game.Title);
        Assert.Contains(game.Warnings, warning => warning.Contains("Description and GameName were missing"));
        Assert.Contains(game.Warnings, warning => warning.Contains("GamePath is empty"));
    }

    [Fact]
    public void RegisterGames_creates_missing_user_profile_from_unique_template_without_overwrite()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Initial D Arcade Stage 8", "InitialD8.exe");
        fixture.WriteProfileTemplate("ID8", "Initial D8 Infinity", "InitialD8.exe");

        var preview = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: true);
        Assert.True(preview.Success);
        Assert.True(preview.DryRun);
        Assert.Single(preview.Registered);
        Assert.False(File.Exists(Path.Combine(fixture.UserProfilesPath, "ID8.xml")));

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);
        Assert.True(result.Success);
        Assert.Single(result.Registered);
        Assert.Equal(executable, result.Registered[0].GamePath);
        Assert.Contains("\"success\":true", JsonSerializer.Serialize(result));
        Assert.Contains("\"registered\":", JsonSerializer.Serialize(result));
        var profilePath = Path.Combine(fixture.UserProfilesPath, "ID8.xml");
        Assert.True(File.Exists(profilePath));
        Assert.Contains(executable, File.ReadAllText(profilePath));

        var secondRun = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);
        Assert.Contains("ID8", secondRun.AlreadyRegistered);
        Assert.Empty(secondRun.Registered);
    }

    [Fact]
    public void RegisterGames_uses_fuzzy_folder_match_for_shared_executables()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Initial D8", "game.exe");
        fixture.WriteProfileTemplate("InitialD8", "Initial D8 Infinity", "game.exe");
        fixture.WriteProfileTemplate("OtherSharedGame", "Other Shared Game", "game.exe");

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        var registered = Assert.Single(result.Registered);
        Assert.Equal("InitialD8", registered.Code);
        Assert.Equal("fuzzy", registered.MatchType);
        Assert.Equal(executable, registered.GamePath);
    }

    [Fact]
    public void RegisterGames_uses_dat_index_when_folder_name_does_not_fuzzy_match_any_template()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Mystery Game Folder", "game.exe");
        fixture.WriteProfileTemplate("InitialD8", "Initial D8 Infinity", "game.exe");
        fixture.WriteProfileTemplate("OtherSharedGame", "Other Shared Game", "game.exe");
        var datPath = fixture.WriteEggmanDat(("Mystery Game Folder", "OtherSharedGame", "game.exe"));
        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false, datIndex);

        Assert.True(result.Success);
        var registered = Assert.Single(result.Registered);
        Assert.Equal("OtherSharedGame", registered.Code);
        Assert.Equal("dat", registered.MatchType);
        Assert.Equal(executable, registered.GamePath);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void RegisterGames_resolves_dat_profile_code_against_profile_set_when_local_filename_differs()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Mystery Game Folder", "game.exe");
        // Local template is filed under "OtherSharedGameNesica" but the dat's own
        // ProfileCode text is the slightly different "OtherSharedGame" -- this only
        // resolves to the real template via fuzzy matching against the known
        // profile-code set, the same way Resolve-ProfileCode does in the original tool.
        fixture.WriteProfileTemplate("OtherSharedGameNesica", "Other Shared Game", "game.exe");
        var datPath = fixture.WriteEggmanDat(("Mystery Game Folder", "OtherSharedGame", "game.exe"));
        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);
        var profileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OtherSharedGameNesica" };

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false, datIndex, profileSet);

        var registered = Assert.Single(result.Registered);
        Assert.Equal("OtherSharedGameNesica", registered.Code);
        Assert.Equal(executable, registered.GamePath);
    }

    [Fact]
    public void BuildDatIndex_parses_every_game_despite_many_rom_entries()
    {
        using var fixture = new TeknoParrotFixture();
        var manyRoms = Enumerable.Range(0, 50).Select(i => (GameName: $"Game {i}", ProfileCode: $"Code{i}", Executable: "game.exe")).ToArray();
        var datPath = fixture.WriteEggmanDat(manyRoms);

        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);

        Assert.Equal(50, datIndex.Count);
        Assert.Equal("Code0", datIndex[TeknoParrotProfileScanner.NormalizeGameKey("Game 0")].ProfileCode);
        Assert.Equal("Code49", datIndex[TeknoParrotProfileScanner.NormalizeGameKey("Game 49")].ProfileCode);
    }

    [Fact]
    public void ResolveProfileCode_falls_back_to_fuzzy_match_against_profile_set()
    {
        using var fixture = new TeknoParrotFixture();
        var profileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AkaiKatanaShinNesica" };

        var resolved = TeknoParrotProfileScanner.ResolveProfileCode("AkaiKatanaShin", fixture.GameProfilesPath, profileSet);

        Assert.Equal("AkaiKatanaShinNesica", resolved);
    }

    [Fact]
    public void RepairGamePaths_updates_broken_profile_when_executable_match_is_unique()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Batman", "Batman.exe");
        fixture.WriteProfileTemplate("Batman", "Batman Arcade", "Batman.exe");
        fixture.WriteDescriptionProfile("Batman", "Batman Arcade", Path.Combine(fixture.RootPath, "Old", "Batman.exe"), "Batman.exe");

        var result = TeknoParrotProfileScanner.RepairGamePaths(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        var repair = Assert.Single(result.Repairs);
        Assert.Equal("fixed", repair.Status);
        Assert.Equal(executable, repair.NewPath);
        Assert.Contains(executable, File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "Batman.xml")));
    }

    [Fact]
    public void Scan_reports_missing_user_profiles_as_error()
    {
        using var fixture = new TeknoParrotFixture(createUserProfiles: false);

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, error => error.Contains("UserProfiles folder was not found"));
    }

    [Fact]
    public void BackupProfiles_creates_timestamped_profile_copy()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("ID8", "Initial D8 Infinity", fixture.WriteGameExecutable("InitialD8.exe"));

        var response = TeknoParrotToolsPluginMain.BackupProfiles(fixture.Settings);
        var backupPath = response.GetType().GetProperty("backup_path")?.GetValue(response)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(backupPath));
        Assert.True(File.Exists(Path.Combine(backupPath!, "ID8.xml")));
    }

    [Fact]
    public async Task RestoreBackup_preserves_current_profiles_before_overwrite()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("ID8", "Initial D8 Infinity", fixture.WriteGameExecutable("InitialD8.exe"));
        var backupResponse = TeknoParrotToolsPluginMain.BackupProfiles(fixture.Settings);
        var backupPath = backupResponse.GetType().GetProperty("backup_path")?.GetValue(backupResponse)?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(backupPath));

        fixture.WriteProfile("ID8", "Changed Title", fixture.WriteGameExecutable("Changed.exe"));
        var message = JsonSerializer.Serialize(new
        {
            method = "execute",
            data = new
            {
                action = "restore_backup",
                teknoparrotRootPath = fixture.RootPath,
                backupPath
            }
        });

        var response = await TeknoParrotToolsPluginMain.ProcessMessage(message);
        var preRestoreBackupPath = response.GetType().GetProperty("pre_restore_backup_path")?.GetValue(response)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(preRestoreBackupPath));
        Assert.True(File.Exists(Path.Combine(preRestoreBackupPath!, "ID8.xml")));
        Assert.Contains("Initial D8 Infinity", File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "ID8.xml")));
    }
}

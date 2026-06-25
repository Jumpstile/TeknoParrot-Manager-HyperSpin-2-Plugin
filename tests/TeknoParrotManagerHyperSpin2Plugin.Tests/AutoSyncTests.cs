using System.IO.Compression;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class AutoSyncTests
{
    private static string CreateZip(string dir, string baseName, string content = "rom-data", DateTime? lastWriteUtc = null)
    {
        Directory.CreateDirectory(dir);
        var zipPath = Path.Combine(dir, baseName + ".zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry($"{baseName}/game.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        if (lastWriteUtc.HasValue)
        {
            File.SetLastWriteTimeUtc(zipPath, lastWriteUtc.Value);
        }

        return zipPath;
    }

    // -- NormalizeStagingFolderName ---------------------------------------

    [Theory]
    [InlineData("Daytona 3.teknoparrot", "Daytona 3")]
    [InlineData("Daytona 3.parrot", "Daytona 3")]
    [InlineData("Daytona 3.game", "Daytona 3")]
    [InlineData("Game (ver) [Platform] [TP]", "Game(ver)[Platform][TP]")]
    [InlineData("Game(ver)[Platform][TP]", "Game(ver)[Platform][TP]")]
    [InlineData("Plain Folder Name", "Plain Folder Name")]
    public void NormalizeStagingFolderName_normalizes_known_variations(string input, string expected)
    {
        Assert.Equal(expected, TeknoParrotProfileScanner.NormalizeStagingFolderName(input));
    }

    // -- ResolveRegisteredGameFolder ---------------------------------------

    [Fact]
    public void ResolveRegisteredGameFolder_resolves_via_dat_index_and_gamepath()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("RenamedFolder", "game.exe");
        fixture.WriteProfile("Daytona3", "Daytona 3", gamePath);
        var datIndex = new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [TeknoParrotProfileScanner.NormalizeGameKey("Daytona 3")] = new TeknoParrotDatEntry("Daytona3", "game.exe")
        };

        var resolved = TeknoParrotProfileScanner.ResolveRegisteredGameFolder("Daytona 3", datIndex, fixture.UserProfilesPath);

        Assert.Equal(Path.GetDirectoryName(gamePath), resolved);
    }

    [Fact]
    public void ResolveRegisteredGameFolder_returns_null_for_an_unsafe_profile_code()
    {
        var datIndex = new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [TeknoParrotProfileScanner.NormalizeGameKey("Daytona 3")] = new TeknoParrotDatEntry("../etc/passwd", "game.exe")
        };

        var resolved = TeknoParrotProfileScanner.ResolveRegisteredGameFolder("Daytona 3", datIndex, Path.GetTempPath());

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveRegisteredGameFolder_returns_null_when_profile_does_not_exist()
    {
        var datIndex = new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [TeknoParrotProfileScanner.NormalizeGameKey("Daytona 3")] = new TeknoParrotDatEntry("Daytona3", "game.exe")
        };

        var resolved = TeknoParrotProfileScanner.ResolveRegisteredGameFolder("Daytona 3", datIndex, Path.GetTempPath());

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveRegisteredGameFolder_returns_null_when_dat_index_is_empty()
    {
        var resolved = TeknoParrotProfileScanner.ResolveRegisteredGameFolder(
            "Daytona 3", new Dictionary<string, TeknoParrotDatEntry>(), Path.GetTempPath());

        Assert.Null(resolved);
    }

    // -- RunAutoSync integration tests -------------------------------------

    [Fact]
    public void RunAutoSync_extracts_a_brand_new_game()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "New Game");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Equal(new[] { "New Game" }, result.SyncedGames);
        Assert.True(File.Exists(Path.Combine(installFolder, "New Game", "New Game", "game.exe")));
        Assert.True(File.Exists(syncStatePath));
    }

    [Fact]
    public void RunAutoSync_reports_an_unchanged_game_as_up_to_date_without_re_extracting()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "Game A");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var first = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);
        Assert.Single(first.SyncedGames);
        var marker = Path.Combine(installFolder, "Game A", "marker.txt");
        File.WriteAllText(marker, "untouched");

        var second = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Empty(second.SyncedGames);
        Assert.Equal(1, second.UpToDateCount);
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void RunAutoSync_re_extracts_when_the_nas_zip_changes()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        var zipPath = CreateZip(zipSource, "Game A", "v1");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        File.Delete(zipPath);
        CreateZip(zipSource, "Game A", "v2-longer-content-changes-size");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Equal(new[] { "Game A" }, result.SyncedGames);
    }

    [Fact]
    public void RunAutoSync_never_touches_a_skip_listed_game()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "Skip Me");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, new[] { "Skip Me" }, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Empty(result.SyncedGames);
        Assert.Equal(1, result.SkippedCount);
        Assert.False(Directory.Exists(Path.Combine(installFolder, "Skip Me")));
    }

    [Fact]
    public void RunAutoSync_with_an_allow_list_only_touches_named_games()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "Wanted");
        CreateZip(zipSource, "Not Wanted");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, new[] { "Wanted" }, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Equal(new[] { "Wanted" }, result.SyncedGames);
        Assert.False(Directory.Exists(Path.Combine(installFolder, "Not Wanted")));
    }

    [Fact]
    public void RunAutoSync_dry_run_reports_would_sync_and_writes_nothing()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "New Game");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: true, null, fixture.UserProfilesPath);

        Assert.Equal(1, result.WouldSyncCount);
        Assert.Empty(result.SyncedGames);
        Assert.False(Directory.Exists(Path.Combine(installFolder, "New Game")));
        Assert.False(File.Exists(syncStatePath));
    }

    [Fact]
    public void RunAutoSync_forces_re_extraction_when_a_stale_sentinel_is_present()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "Game A");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var first = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);
        Assert.Single(first.SyncedGames);

        File.WriteAllText(Path.Combine(installFolder, "Game A.extracting"), string.Empty);

        var second = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Equal(new[] { "Game A" }, second.SyncedGames);
        Assert.False(File.Exists(Path.Combine(installFolder, "Game A.extracting")));
    }

    [Fact]
    public void RunAutoSync_skips_teknoparrot_collection_metadata_zips()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "!TeknoParrot Collection 2026-01-01");
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);
        var syncStatePath = Path.Combine(installFolder, "state.json");

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, syncStatePath, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Empty(result.SyncedGames);
        Assert.Equal(1, result.SkippedCount);
    }

    [Fact]
    public void RunAutoSync_reports_a_clear_note_when_the_source_folder_has_no_zips()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "EmptyZipSource");
        Directory.CreateDirectory(zipSource);
        var installFolder = fixture.Settings.GamesRootPath;
        Directory.CreateDirectory(installFolder);

        var result = TeknoParrotProfileScanner.RunAutoSync(zipSource, installFolder, Path.Combine(installFolder, "state.json"), null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.NotNull(result.Note);
        Assert.Empty(result.SyncedGames);
    }

    // -- RunAutoSyncBothSources ---------------------------------------------

    [Fact]
    public void RunAutoSyncBothSources_supplementary_is_null_when_not_configured()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSource = Path.Combine(fixture.RootPath, "ZipSource");
        CreateZip(zipSource, "Game A");
        fixture.Settings.RomZipSourcePath = zipSource;
        Directory.CreateDirectory(fixture.Settings.GamesRootPath);

        var result = TeknoParrotProfileScanner.RunAutoSyncBothSources(fixture.Settings, null, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Single(result.Main.SyncedGames);
        Assert.Null(result.Supplementary);
    }

    [Fact]
    public void RunAutoSyncBothSources_runs_both_sources_against_a_shared_sync_state()
    {
        using var fixture = new TeknoParrotFixture();
        var zipSourceMain = Path.Combine(fixture.RootPath, "ZipSourceMain");
        var zipSourceSupp = Path.Combine(fixture.RootPath, "ZipSourceSupp");
        CreateZip(zipSourceMain, "Main Game");
        CreateZip(zipSourceSupp, "Supp Game");
        fixture.Settings.RomZipSourcePath = zipSourceMain;
        fixture.Settings.RomZipSourceSupplementaryPath = zipSourceSupp;
        Directory.CreateDirectory(fixture.Settings.GamesRootPath);

        var result = TeknoParrotProfileScanner.RunAutoSyncBothSources(fixture.Settings, null, null, null, dryRun: false, null, fixture.UserProfilesPath);

        Assert.Equal(new[] { "Main Game" }, result.Main.SyncedGames);
        Assert.NotNull(result.Supplementary);
        Assert.Equal(new[] { "Supp Game" }, result.Supplementary!.SyncedGames);
        Assert.True(File.Exists(Path.Combine(fixture.Settings.GamesRootPath, "TeknoParrot-Manager.syncstate.json")));
    }
}

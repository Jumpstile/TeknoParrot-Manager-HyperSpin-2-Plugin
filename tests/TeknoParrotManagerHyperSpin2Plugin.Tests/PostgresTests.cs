using System.Xml.Linq;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class PostgresTests
{
    private static XDocument PostgresProfile(string dbName = "GameDBTest", string path = "", string address = "", string port = "", string user = "", string pass = "", string autoCreate = "") => XDocument.Parse($"""
        <GameProfile>
          <GamePath>placeholder</GamePath>
          <ConfigValues>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>DbName</FieldName>
              <FieldValue>{dbName}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>Path</FieldName>
              <FieldValue>{path}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>Address</FieldName>
              <FieldValue>{address}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>Port</FieldName>
              <FieldValue>{port}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>User</FieldName>
              <FieldValue>{user}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>Pass</FieldName>
              <FieldValue>{pass}</FieldValue>
            </FieldInformation>
            <FieldInformation>
              <CategoryName>Postgres</CategoryName>
              <FieldName>Automatically create Database</FieldName>
              <FieldValue>{autoCreate}</FieldValue>
            </FieldInformation>
          </ConfigValues>
        </GameProfile>
        """);

    // -- IsSafePostgresDbName ---------------------------------------------

    [Theory]
    [InlineData("GameDB19", true)]
    [InlineData("GAMEDBPP12", true)]
    [InlineData("Game_DB_06", true)]
    [InlineData("", false)]
    [InlineData("Game DB", false)]
    [InlineData("GameDB; DROP TABLE games;", false)]
    [InlineData("../etc/passwd", false)]
    public void IsSafePostgresDbName_validates_strictly(string dbName, bool expected)
    {
        Assert.Equal(expected, TeknoParrotProfileScanner.IsSafePostgresDbName(dbName));
    }

    // -- TestGameNeedsPostgres / Get/SetPostgresFieldValue ----------------

    [Fact]
    public void TestGameNeedsPostgres_false_when_no_postgres_category()
    {
        var doc = XDocument.Parse("<GameProfile><ConfigValues></ConfigValues></GameProfile>");
        Assert.False(TeknoParrotProfileScanner.TestGameNeedsPostgres(doc));
    }

    [Fact]
    public void TestGameNeedsPostgres_true_when_postgres_category_present()
    {
        var doc = PostgresProfile();
        Assert.True(TeknoParrotProfileScanner.TestGameNeedsPostgres(doc));
    }

    [Fact]
    public void GetPostgresFieldValue_reads_existing_field()
    {
        var doc = PostgresProfile(dbName: "GameDB19");
        Assert.Equal("GameDB19", TeknoParrotProfileScanner.GetPostgresFieldValue(doc, "DbName"));
    }

    [Fact]
    public void GetPostgresFieldValue_returns_null_for_a_field_that_does_not_exist()
    {
        var doc = PostgresProfile();
        Assert.Null(TeknoParrotProfileScanner.GetPostgresFieldValue(doc, "NotARealField"));
    }

    [Fact]
    public void SetPostgresFieldValue_writes_an_existing_field()
    {
        var doc = PostgresProfile(address: "");
        Assert.True(TeknoParrotProfileScanner.SetPostgresFieldValue(doc, "Address", "127.0.0.1"));
        Assert.Equal("127.0.0.1", TeknoParrotProfileScanner.GetPostgresFieldValue(doc, "Address"));
    }

    [Fact]
    public void SetPostgresFieldValue_is_a_noop_when_the_field_does_not_exist()
    {
        var doc = PostgresProfile();
        Assert.False(TeknoParrotProfileScanner.SetPostgresFieldValue(doc, "NotARealField", "value"));
    }

    // -- Password encryption round-trip (Windows-only) --------------------

    [Fact]
    public void EncryptDecryptPostgresPassword_round_trips_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is Windows-only; nothing to verify on other OSes.
        }

        var encrypted = TeknoParrotProfileScanner.EncryptPostgresPassword("correct horse battery staple");
        var decrypted = TeknoParrotProfileScanner.DecryptPostgresPassword(encrypted);
        Assert.Equal("correct horse battery staple", decrypted);
    }

    // -- ShouldUninstallCandidate (pure decision logic, the highest-stakes
    //    function in this whole phase) -------------------------------------

    [Fact]
    public void ShouldUninstallCandidate_false_for_an_unrelated_install_at_a_different_path()
    {
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: false, Mismatch: false);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(@"C:\Some\Other\Path", @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.False(result);
    }

    [Fact]
    public void ShouldUninstallCandidate_true_for_a_matching_path_with_no_installations_record()
    {
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: false, Mismatch: false);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(@"C:\Program Files (x86)\PostgreSQL\8.3", @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.True(result);
    }

    [Fact]
    public void ShouldUninstallCandidate_true_for_a_matching_path_with_an_agreeing_installations_record()
    {
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: true, Mismatch: false);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(@"C:\Program Files (x86)\PostgreSQL\8.3", @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.True(result);
    }

    [Fact]
    public void ShouldUninstallCandidate_false_for_a_matching_path_with_a_disagreeing_installations_record()
    {
        // Per issue #4: a PRESENT-and-DISAGREEING Installations registry
        // record blocks the uninstall, even though InstallLocation itself
        // matches -- this is the safety net the upstream hardening added.
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: true, Mismatch: true);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(@"C:\Program Files (x86)\PostgreSQL\8.3", @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.False(result);
    }

    [Fact]
    public void ShouldUninstallCandidate_false_for_a_null_install_location()
    {
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: false, Mismatch: false);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(null, @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.False(result);
    }

    [Fact]
    public void ShouldUninstallCandidate_matches_case_insensitively_and_ignores_a_trailing_backslash()
    {
        var registryCheck = new PostgresInstallationsRegistryCheck(HasRecord: false, Mismatch: false);
        var result = TeknoParrotProfileScanner.ShouldUninstallCandidate(@"c:\program files (x86)\postgresql\8.3\", @"C:\Program Files (x86)\PostgreSQL\8.3", registryCheck);
        Assert.True(result);
    }

    // -- GetPostgresBackupFile --------------------------------------------

    [Fact]
    public void GetPostgresBackupFile_returns_null_when_pg_backup_folder_is_missing()
    {
        var gameFolder = Path.Combine(Path.GetTempPath(), "tpm-pg-test-" + Guid.NewGuid());
        Directory.CreateDirectory(gameFolder);
        try
        {
            Assert.Null(TeknoParrotProfileScanner.GetPostgresBackupFile(gameFolder));
        }
        finally
        {
            Directory.Delete(gameFolder, recursive: true);
        }
    }

    [Fact]
    public void GetPostgresBackupFile_returns_null_when_pg_backup_folder_is_empty()
    {
        var gameFolder = Path.Combine(Path.GetTempPath(), "tpm-pg-test-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(gameFolder, "pg_backup"));
        try
        {
            Assert.Null(TeknoParrotProfileScanner.GetPostgresBackupFile(gameFolder));
        }
        finally
        {
            Directory.Delete(gameFolder, recursive: true);
        }
    }

    [Fact]
    public void GetPostgresBackupFile_picks_the_highest_leading_number_directly_inside_pg_backup()
    {
        var gameFolder = Path.Combine(Path.GetTempPath(), "tpm-pg-test-" + Guid.NewGuid());
        var pgBackupDir = Path.Combine(gameFolder, "pg_backup");
        Directory.CreateDirectory(pgBackupDir);
        try
        {
            File.WriteAllText(Path.Combine(pgBackupDir, "0001_backup.backup"), "old");
            File.WriteAllText(Path.Combine(pgBackupDir, "0005_backup.backup"), "newest");
            File.WriteAllText(Path.Combine(pgBackupDir, "0003_backup.backup"), "middle");

            var result = TeknoParrotProfileScanner.GetPostgresBackupFile(gameFolder);

            Assert.NotNull(result);
            Assert.Equal("0005_backup.backup", Path.GetFileName(result));
        }
        finally
        {
            Directory.Delete(gameFolder, recursive: true);
        }
    }

    [Fact]
    public void GetPostgresBackupFile_prefers_the_most_recent_date_subfolder()
    {
        var gameFolder = Path.Combine(Path.GetTempPath(), "tpm-pg-test-" + Guid.NewGuid());
        var pgBackupDir = Path.Combine(gameFolder, "pg_backup");
        var olderDir = Path.Combine(pgBackupDir, "2025-01-01");
        var newerDir = Path.Combine(pgBackupDir, "2026-06-01");
        Directory.CreateDirectory(olderDir);
        Directory.CreateDirectory(newerDir);
        try
        {
            File.WriteAllText(Path.Combine(olderDir, "0009_backup.backup"), "old, but higher number");
            File.WriteAllText(Path.Combine(newerDir, "0001_backup.backup"), "newer subfolder wins");

            var result = TeknoParrotProfileScanner.GetPostgresBackupFile(gameFolder);

            Assert.NotNull(result);
            Assert.Equal(newerDir, Path.GetDirectoryName(result));
        }
        finally
        {
            Directory.Delete(gameFolder, recursive: true);
        }
    }

    // -- ApplyPostgresGameSetup integration tests (no real Postgres server
    //    needed for these branches -- they all short-circuit before any
    //    process call) -----------------------------------------------------

    [Fact]
    public async Task ApplyPostgresGameSetup_skips_a_game_with_no_postgres_category()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        fixture.WriteProfile("Game1", "Game One", gamePath);

        var result = await TeknoParrotProfileScanner.ApplyPostgresGameSetup(fixture.Settings, "anypassword", dryRun: true);

        Assert.Empty(result.ConfiguredProfiles);
        Assert.Empty(result.DbCreatedProfiles);
        Assert.Empty(result.AlreadyConfiguredProfiles);
        Assert.Empty(result.ErrorProfiles);
    }

    [Fact]
    public async Task ApplyPostgresGameSetup_rejects_an_unsafe_dbname_as_an_error()
    {
        using var fixture = new TeknoParrotFixture();
        var gamePath = fixture.WriteGameExecutable("Game1", "game.exe");
        var profilePath = Path.Combine(fixture.UserProfilesPath, "Game1.xml");
        File.WriteAllText(profilePath, PostgresProfile(dbName: "bad; name").ToString().Replace("placeholder", gamePath));

        var result = await TeknoParrotProfileScanner.ApplyPostgresGameSetup(fixture.Settings, "anypassword", dryRun: true);

        var error = Assert.Single(result.ErrorProfiles);
        Assert.Equal("Game1", error);
    }
}

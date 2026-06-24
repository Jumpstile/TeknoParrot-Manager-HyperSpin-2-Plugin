using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 9 (per-game setup, backup, restore) of ROADMAP.md. Unlike
// PostgresInstall.cs/PostgresCleanup.cs, nothing here needs elevation or
// network access -- it only ever talks to an already-installed local
// Postgres server (via psql/createdb/pg_dump/pg_restore) and edits
// UserProfiles XML, the same trust tier as every other feature in this
// plugin.
public static partial class TeknoParrotProfileScanner
{
    // Locates the right backup file inside a game's own pg_backup
    // folder. Per the original guide: backups may sit directly inside
    // pg_backup\, or inside a YYYY-MM-DD-named subfolder; when subfolders
    // exist, the most recent one (sorting correctly as ISO-formatted
    // strings) is used. The right file within is the one with the
    // highest leading 4-digit number. Mirrors Get-PostgresBackupFile.
    internal static string? GetPostgresBackupFile(string gameFolder)
    {
        var pgBackupDir = Path.Combine(gameFolder, "pg_backup");
        if (!Directory.Exists(pgBackupDir))
        {
            return null;
        }

        var dateSubfolders = Directory.GetDirectories(pgBackupDir)
            .Where(dir => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(dir), @"^\d{4}-\d{2}-\d{2}$"))
            .OrderByDescending(dir => Path.GetFileName(dir), StringComparer.Ordinal)
            .ToList();
        var searchDir = dateSubfolders.Count > 0 ? dateSubfolders[0] : pgBackupDir;

        var candidates = Directory.GetFiles(searchDir);
        if (candidates.Length == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(file =>
            {
                var match = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(file), @"^(\d{4})");
                return match.Success ? int.Parse(match.Groups[1].Value) : -1;
            })
            .First();
    }

    // Creates a database and restores a game's bundled backup into it.
    // Only ever called for a database confirmed NOT to already exist --
    // never recreates or overwrites an existing database. encoding should
    // be "UTF8" only for the Golden Tee Live 2006 database (GameDB06);
    // "SQL_ASCII" for every other game -- a static, empirically-confirmed
    // exception per the original, not derived at runtime. pg_restore
    // warnings on stderr are expected and ignored -- only a database that
    // doesn't exist afterward is treated as a real failure. Mirrors
    // New-PostgresDatabaseFromBackup.
    private static async Task<bool> NewPostgresDatabaseFromBackupAsync(
        string dbName, string encoding, string backupFile, string superPasswordPlain, CancellationToken cancellationToken)
    {
        if (!IsSafePostgresDbName(dbName))
        {
            return false;
        }

        try
        {
            var (createExit, _) = await RunPostgresToolAsync(
                "createdb.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-E", encoding, "-T", "template0", dbName },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);
            if (createExit != 0)
            {
                return false;
            }

            await RunPostgresToolAsync(
                "psql.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-d", dbName, "-c", $"ALTER DATABASE \"{dbName}\" SET standard_conforming_strings = on;" },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);

            await RunPostgresToolAsync(
                "pg_restore.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-d", dbName, backupFile },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);

            return await TestPostgresDatabaseExists(dbName, superPasswordPlain, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    // Main per-game Postgres setup pass: for every registered profile
    // that needs Postgres, fills in connection fields (Path/Address/Port/
    // User/Pass only when currently empty -- TeknoParrot ships these
    // already correctly pre-filled in practice, so this is normally a
    // no-op; NEVER overwrites an existing value the user may have already
    // configured) and, for profiles whose GameProfile predates the
    // "Automatically create Database" feature, creates and restores that
    // game's database -- but only when confirmed not to already exist.
    // Mirrors Invoke-PostgresGameSetup. Backs up UserProfiles first via
    // TryBackupProfilesForMutation when dryRun is false, same as every
    // other mutating action in this plugin.
    public static async Task<PostgresGameSetupResult> ApplyPostgresGameSetup(
        TeknoParrotSettings settings, string superPasswordPlain, bool dryRun, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var relBinPath = PostgresBinDir.TrimEnd('\\') + "\\";

        var configured = new List<string>();
        var dbCreated = new List<string>();
        var alreadyConfigured = new List<string>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new PostgresGameSetupResult(configured, dbCreated, alreadyConfigured, errors);
        }

        foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                if (!TestGameNeedsPostgres(doc))
                {
                    continue;
                }

                var gamePath = ChildByLocalName(doc.Root, "GamePath")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
                {
                    continue;
                }

                var gameFolder = Path.GetDirectoryName(gamePath);
                if (string.IsNullOrWhiteSpace(gameFolder))
                {
                    continue;
                }

                var dbName = GetPostgresFieldValue(doc, "DbName");
                if (string.IsNullOrWhiteSpace(dbName) || !IsSafePostgresDbName(dbName))
                {
                    log?.Invoke($"Postgres: {code} has no usable DbName -- skipped.");
                    errors.Add(code);
                    continue;
                }

                var changed = false;
                if (string.IsNullOrWhiteSpace(GetPostgresFieldValue(doc, "Path")) && SetPostgresFieldValue(doc, "Path", relBinPath)) { changed = true; }
                if (string.IsNullOrWhiteSpace(GetPostgresFieldValue(doc, "Address")) && SetPostgresFieldValue(doc, "Address", "127.0.0.1")) { changed = true; }
                if (string.IsNullOrWhiteSpace(GetPostgresFieldValue(doc, "Port")) && SetPostgresFieldValue(doc, "Port", "5432")) { changed = true; }
                if (string.IsNullOrWhiteSpace(GetPostgresFieldValue(doc, "User")) && SetPostgresFieldValue(doc, "User", "postgres")) { changed = true; }
                if (string.IsNullOrWhiteSpace(GetPostgresFieldValue(doc, "Pass")) && SetPostgresFieldValue(doc, "Pass", superPasswordPlain)) { changed = true; }

                if (await TestPostgresDatabaseExists(dbName, superPasswordPlain, cancellationToken).ConfigureAwait(false))
                {
                    if (changed)
                    {
                        if (!dryRun) { SaveProfileDocument(doc, file); }
                        configured.Add(code);
                    }
                    else
                    {
                        alreadyConfigured.Add(code);
                    }
                    log?.Invoke($"Postgres: {code} -- database '{dbName}' already exists, left untouched.");
                    continue;
                }

                var autoCreate = GetPostgresFieldValue(doc, "Automatically create Database");
                if (autoCreate == "1")
                {
                    // TeknoParrotUI's own first-launch flow creates the
                    // database itself -- nothing more to do here.
                    if (changed)
                    {
                        if (!dryRun) { SaveProfileDocument(doc, file); }
                        configured.Add(code);
                    }
                    log?.Invoke($"Postgres: {code} -- deferring database creation to TeknoParrotUI's own setup.");
                    continue;
                }

                var backupFile = GetPostgresBackupFile(gameFolder);
                if (backupFile is null)
                {
                    log?.Invoke($"Postgres: {code} -- no pg_backup file found, database not created.");
                    if (changed && !dryRun) { SaveProfileDocument(doc, file); }
                    if (changed) { configured.Add(code); }
                    errors.Add(code);
                    continue;
                }

                if (dryRun)
                {
                    // Preview never actually creates a database -- report
                    // it as a would-be creation without running createdb/pg_restore.
                    dbCreated.Add(code);
                    if (changed) { configured.Add(code); }
                    continue;
                }

                var encoding = dbName == "GameDB06" ? "UTF8" : "SQL_ASCII";
                if (await NewPostgresDatabaseFromBackupAsync(dbName, encoding, backupFile, superPasswordPlain, cancellationToken).ConfigureAwait(false))
                {
                    dbCreated.Add(code);
                    log?.Invoke($"Postgres: {code} -- created and restored database '{dbName}'.");
                }
                else
                {
                    errors.Add(code);
                    log?.Invoke($"Postgres: {code} -- FAILED to create/restore database '{dbName}'.");
                }

                if (changed)
                {
                    SaveProfileDocument(doc, file);
                    configured.Add(code);
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                log?.Invoke($"Postgres: error processing {code} -- {ex.Message}");
                errors.Add(code);
            }
        }

        return new PostgresGameSetupResult(configured, dbCreated, alreadyConfigured, errors);
    }

    // Finds every Postgres-needing game with an existing database and
    // dumps each via pg_dump into a timestamped folder under this
    // plugin's own storage. Mirrors Backup-PostgresDatabases. No preview
    // pairing -- inherently non-destructive (only ever adds a new
    // backup), matching backup_profiles's existing precedent.
    public static async Task<PostgresBackupResult> BackupPostgresDatabases(
        TeknoParrotSettings settings, string superPasswordPlain, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var dbNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(userProfilesPath) && Directory.Exists(userProfilesPath))
        {
            foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var doc = XDocument.Load(file);
                    if (!TestGameNeedsPostgres(doc))
                    {
                        continue;
                    }

                    var dbName = GetPostgresFieldValue(doc, "DbName");
                    if (!string.IsNullOrWhiteSpace(dbName) && IsSafePostgresDbName(dbName) &&
                        await TestPostgresDatabaseExists(dbName, superPasswordPlain, cancellationToken).ConfigureAwait(false))
                    {
                        dbNames.Add(dbName);
                    }
                }
                catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
                {
                    log?.Invoke($"Postgres backup: could not check {Path.GetFileName(file)} -- {ex.Message}");
                }
            }
        }

        if (dbNames.Count == 0)
        {
            log?.Invoke("Postgres backup: no existing databases to back up.");
            return new PostgresBackupResult(null, Array.Empty<string>());
        }

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var backupPath = Path.Combine(AppContext.BaseDirectory, "PostgresBackups", timestamp);
        Directory.CreateDirectory(backupPath);

        var dumped = new List<string>();
        foreach (var dbName in dbNames)
        {
            var destFile = Path.Combine(backupPath, $"{dbName}.backup");
            await RunPostgresToolAsync(
                "pg_dump.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-F", "c", "-f", destFile, dbName },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);
            if (File.Exists(destFile))
            {
                dumped.Add(dbName);
                log?.Invoke($"Postgres backup: dumped {dbName} -> {destFile}");
            }
            else
            {
                log?.Invoke($"Postgres backup: FAILED to dump {dbName}");
            }
        }

        return new PostgresBackupResult(backupPath, dumped);
    }

    // Restores every .backup file found directly inside backupPath.
    // Destructive -- drops each database entirely first (dropdb
    // --if-exists), then recreates and restores it via the same
    // createdb/psql/pg_restore sequence fresh creation uses
    // (NewPostgresDatabaseFromBackupAsync) -- mirrors
    // Invoke-RestorePostgresBackup exactly (it does NOT pg_restore
    // --clean against the live database; it drops and rebuilds).
    // Adapted from an interactive numbered-menu picker to a direct
    // backupPath parameter, same adaptation as the existing
    // restore_backup (UserProfiles restore) action.
    public static async Task<PostgresRestoreResult> RestorePostgresBackup(
        string backupPath, string superPasswordPlain, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var restored = new List<string>();
        var errors = new List<string>();

        if (!Directory.Exists(backupPath))
        {
            return new PostgresRestoreResult(restored, errors, "Backup folder not found.");
        }

        var backupFiles = Directory.GetFiles(backupPath, "*.backup", SearchOption.TopDirectoryOnly);
        if (backupFiles.Length == 0)
        {
            return new PostgresRestoreResult(restored, errors, "No .backup files found in that folder.");
        }

        foreach (var backupFile in backupFiles)
        {
            var dbName = Path.GetFileNameWithoutExtension(backupFile);
            if (!IsSafePostgresDbName(dbName))
            {
                errors.Add(dbName);
                continue;
            }

            try
            {
                await RunPostgresToolAsync(
                    "dropdb.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "--if-exists", dbName },
                    superPasswordPlain, cancellationToken).ConfigureAwait(false);

                var encoding = dbName == "GameDB06" ? "UTF8" : "SQL_ASCII";
                if (await NewPostgresDatabaseFromBackupAsync(dbName, encoding, backupFile, superPasswordPlain, cancellationToken).ConfigureAwait(false))
                {
                    restored.Add(dbName);
                    log?.Invoke($"Postgres restore: restored {dbName} from {backupFile}");
                }
                else
                {
                    errors.Add(dbName);
                    log?.Invoke($"Postgres restore: FAILED to restore {dbName}");
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
            {
                errors.Add(dbName);
                log?.Invoke($"Postgres restore: error restoring {dbName} -- {ex.Message}");
            }
        }

        return new PostgresRestoreResult(restored, errors, null);
    }
}

public sealed record PostgresGameSetupResult(
    [property: JsonPropertyName("configured_profiles")] IReadOnlyList<string> ConfiguredProfiles,
    [property: JsonPropertyName("db_created_profiles")] IReadOnlyList<string> DbCreatedProfiles,
    [property: JsonPropertyName("already_configured_profiles")] IReadOnlyList<string> AlreadyConfiguredProfiles,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles);

public sealed record PostgresBackupResult(
    [property: JsonPropertyName("backup_path")] string? BackupPath,
    [property: JsonPropertyName("database_names")] IReadOnlyList<string> DatabaseNames);

public sealed record PostgresRestoreResult(
    [property: JsonPropertyName("restored_databases")] IReadOnlyList<string> RestoredDatabases,
    [property: JsonPropertyName("error_databases")] IReadOnlyList<string> ErrorDatabases,
    [property: JsonPropertyName("error")] string? Error);

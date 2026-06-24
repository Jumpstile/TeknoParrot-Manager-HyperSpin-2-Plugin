using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 9 of ROADMAP.md: ports the PostgreSQL 8.3 setup feature several
// Incredible Technologies titles need (Golden Tee Live, Power Putt Live,
// Silver Strike Bowling Live, Target Toss Pro, Orange County Choppers
// Pinball). This is the highest-risk phase in this plugin: unlike
// BepInEx (v0.12.0) and FFB Plugin (v0.13.0), which only ever copy or
// extract files, installing PostgreSQL runs an MSI installer, creates a
// Windows service and a local Windows user account, and requires
// Administrator privileges for that one step -- self-elevated via a UAC
// prompt on just the msiexec child process (PostgresInstall.cs), never
// the plugin process or HyperHQ itself. See README.md Safety Notes for
// the full set of safeguards. Windows-only throughout -- this project
// also targets linux-x64, so every Windows-specific API here is guarded
// with OperatingSystem.IsWindows(), same pattern as GpuFix.cs's WMI use.
public static partial class TeknoParrotProfileScanner
{
    internal const string PostgresInstallDir = @"C:\Program Files (x86)\PostgreSQL\8.3";
    internal static readonly string PostgresBinDir = Path.Combine(PostgresInstallDir, "bin");
    internal const string PostgresServiceName = "pgsql-8.3";

    private static readonly Regex SafePostgresDbNamePattern = new(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    // -- Detection (Group A, pure local checks) --------------------------

    // True if this profile has any Postgres-category field at all.
    // Mirrors Test-GameNeedsPostgres.
    public static bool TestGameNeedsPostgres(XDocument doc) =>
        FindPostgresField(doc, fieldName: null) is not null;

    // Reads one named field's current value from the Postgres category,
    // or null if the field is missing entirely (distinct from an empty
    // string, which means the field exists but is blank). Mirrors
    // Get-PostgresFieldValue.
    public static string? GetPostgresFieldValue(XDocument doc, string fieldName)
    {
        var field = FindPostgresField(doc, fieldName);
        return field is null ? null : ChildByLocalName(field, "FieldValue")?.Value;
    }

    // Sets one named field's value in the Postgres category. No-op
    // (returns false) if the field doesn't exist on this profile --
    // never creates a new field, since the schema is owned by
    // TeknoParrot's own GameProfile definitions, not this plugin.
    // Mirrors Set-PostgresFieldValue.
    public static bool SetPostgresFieldValue(XDocument doc, string fieldName, string value)
    {
        var field = FindPostgresField(doc, fieldName);
        var valueNode = field is null ? null : ChildByLocalName(field, "FieldValue");
        if (valueNode is null)
        {
            return false;
        }

        valueNode.Value = value;
        return true;
    }

    // fieldName == null matches any field in the Postgres category (used
    // by TestGameNeedsPostgres to check existence); otherwise matches the
    // specific named field within that category.
    private static XElement? FindPostgresField(XDocument doc, string? fieldName)
    {
        var configValues = ChildByLocalName(doc.Root, "ConfigValues");
        foreach (var field in ChildrenByLocalName(configValues, "FieldInformation"))
        {
            var categoryName = ChildByLocalName(field, "CategoryName")?.Value;
            if (categoryName != "Postgres")
            {
                continue;
            }

            if (fieldName is null)
            {
                return field;
            }

            if (ChildByLocalName(field, "FieldName")?.Value == fieldName)
            {
                return field;
            }
        }

        return null;
    }

    // Validates a database name is safe to use as a psql/createdb/pg_dump
    // command-line argument before any Postgres operation touches it.
    // DbName ultimately comes from a GameProfile XML field -- shipped by
    // TeknoParrot, but treated as semi-trusted rather than blindly safe,
    // matching this project's "external-ish input into a command must be
    // validated" convention used elsewhere (e.g. IsPathInside for
    // filesystem paths). Mirrors Test-SafePostgresDbName.
    public static bool IsSafePostgresDbName(string dbName) => SafePostgresDbNamePattern.IsMatch(dbName);

    // True if the current process has Administrator privileges. Mirrors
    // Test-RunningAsAdministrator. Windows-only -- returns false on any
    // other OS, since elevation is a Windows concept.
    public static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // Read-only: true if PostgreSQL 8.3 is already installed (service
    // exists and psql.exe is present). Never reinstalls or modifies an
    // existing install. Mirrors Test-PostgresInstalled. Windows-only.
    public static bool IsPostgresInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var psqlExists = File.Exists(Path.Combine(PostgresBinDir, "psql.exe"));
        if (!psqlExists)
        {
            return false;
        }

        try
        {
            using var controller = new ServiceController(PostgresServiceName);
            _ = controller.Status; // throws InvalidOperationException if the service doesn't exist
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // -- Password encryption (first secret this plugin has ever needed
    //    to persist) ------------------------------------------------------

    private static string PostgresSecretsPath => Path.Combine(AppContext.BaseDirectory, "PostgresSecrets", "superuser.dat");

    // Windows DPAPI (via ProtectedData), scoped to the current Windows
    // user + machine -- the direct .NET equivalent of the original's
    // ConvertTo-SecureString/ConvertFrom-SecureString with no -Key.
    // Windows-only; throws PlatformNotSupportedException on any other OS
    // -- [SupportedOSPlatform] makes that an enforced contract (CA1416)
    // rather than just a comment, so every caller must itself either be
    // marked Windows-only or check OperatingSystem.IsWindows() first.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static byte[] EncryptPostgresPassword(string plainText)
    {
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plainText);
        return ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string DecryptPostgresPassword(byte[] encrypted)
    {
        var plainBytes = ProtectedData.Unprotect(encrypted, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return System.Text.Encoding.UTF8.GetString(plainBytes);
    }

    // Persists the encrypted superuser password to this plugin's own
    // storage (write-to-temp-then-atomic-replace, per CLAUDE.md's
    // convention), as a base64 string in a small JSON file. Never logs
    // the password, encrypted or otherwise. Windows-only (see
    // EncryptPostgresPassword) -- callers must guard with
    // OperatingSystem.IsWindows() first.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void SaveSuperuserPassword(string plainText)
    {
        var encrypted = EncryptPostgresPassword(plainText);
        var json = JsonSerializer.Serialize(new PostgresSecretFile(Convert.ToBase64String(encrypted)));
        var path = PostgresSecretsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tempPath, path);
    }

    // Reads + decrypts the saved password if present. Returns null if
    // absent, corrupt, or undecryptable (e.g. a secrets file copied from
    // a different machine/user) -- never throws on a missing or stale
    // file, matching the original's try/catch around
    // ConvertFrom-SecureStringPlain degrading to "no saved password."
    internal static string? TryGetSavedSuperuserPassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var path = PostgresSecretsPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var secretFile = JsonSerializer.Deserialize<PostgresSecretFile>(json);
            if (secretFile is null || string.IsNullOrWhiteSpace(secretFile.EncryptedBase64))
            {
                return null;
            }

            return DecryptPostgresPassword(Convert.FromBase64String(secretFile.EncryptedBase64));
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or CryptographicException)
        {
            return null;
        }
    }

    // -- Low-level Postgres process helper -------------------------------

    // Every Postgres tool call (psql/createdb/pg_dump/pg_restore) follows
    // the same shape: write a temporary .pgpass file, point PGPASSFILE at
    // it for *this child process only* (never this plugin's own
    // environment), run the tool, delete the temp file afterward.
    // Consolidated into one shared helper rather than duplicating the
    // temp-pgpass-file dance per call site, unlike the original's four
    // near-identical copies (a case where PowerShell's lower cost for
    // small standalone functions doesn't carry over -- a shared private
    // C# helper is the right call here). Returns (ExitCode, StdOut).
    private static async Task<(int ExitCode, string StdOut)> RunPostgresToolAsync(
        string exeName, IReadOnlyList<string> args, string superPasswordPlain, CancellationToken cancellationToken)
    {
        var exePath = Path.Combine(PostgresBinDir, exeName);
        var pgpassFile = CreatePgPassFile(superPasswordPlain);
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
            startInfo.Environment["PGPASSFILE"] = pgpassFile;

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            process.Start();
            var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdOut = await stdOutTask.ConfigureAwait(false);
            await stdErrTask.ConfigureAwait(false);
            return (process.ExitCode, stdOut);
        }
        finally
        {
            try
            {
                File.Delete(pgpassFile);
            }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // Same single-line 127.0.0.1:5432:*:postgres:<password> format and
    // icacls lockdown attempt (best-effort, not load-bearing) as the
    // original's New-PostgresPgPassFile. Per the .pgpass format
    // (postgresql.org/docs/current/libpq-pgpass.html), only "\" and ":"
    // need escaping in a field.
    private static string CreatePgPassFile(string superPasswordPlain)
    {
        var path = Path.Combine(Path.GetTempPath(), "tpm-pgpass-" + Guid.NewGuid().ToString("N") + ".conf");
        var escaped = superPasswordPlain.Replace("\\", "\\\\").Replace(":", "\\:");
        var line = $"127.0.0.1:5432:*:postgres:{escaped}";
        File.WriteAllText(path, line + Environment.NewLine, new System.Text.UTF8Encoding(false));

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var owner = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                var icaclsInfo = new System.Diagnostics.ProcessStartInfo("icacls", $"\"{path}\" /inheritance:r /grant:r \"{owner}:(R)\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var icacls = System.Diagnostics.Process.Start(icaclsInfo);
                icacls?.WaitForExit(5000);
            }
            catch (Exception) { /* best-effort hardening, not load-bearing */ }
        }

        return path;
    }

    // Read-only: true if a database with this exact name already exists
    // on the local Postgres server. Mirrors Test-PostgresDatabaseExists.
    public static async Task<bool> TestPostgresDatabaseExists(string dbName, string superPasswordPlain, CancellationToken cancellationToken = default)
    {
        if (!IsSafePostgresDbName(dbName) || !File.Exists(Path.Combine(PostgresBinDir, "psql.exe")))
        {
            return false;
        }

        try
        {
            var (exitCode, stdOut) = await RunPostgresToolAsync(
                "psql.exe",
                new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-tAc", $"SELECT 1 FROM pg_database WHERE datname='{dbName}'" },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);
            return exitCode == 0 && stdOut.Contains('1');
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }

    // Verifies a password actually authenticates against the running
    // Postgres server. Mirrors Test-PostgresPassword.
    public static async Task<bool> TestPostgresPassword(string superPasswordPlain, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path.Combine(PostgresBinDir, "psql.exe")))
        {
            return false;
        }

        try
        {
            var (exitCode, _) = await RunPostgresToolAsync(
                "psql.exe", new[] { "-U", "postgres", "-h", "127.0.0.1", "-p", "5432", "-tAc", "SELECT 1" },
                superPasswordPlain, cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }
    }
}

internal sealed record PostgresSecretFile(string EncryptedBase64);

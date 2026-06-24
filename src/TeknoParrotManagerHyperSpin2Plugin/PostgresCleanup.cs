using System.Diagnostics;
using System.DirectoryServices.AccountManagement;
using System.ServiceProcess;
using Microsoft.Win32;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 9 (partial-install cleanup) of ROADMAP.md. This is the
// highest-blast-radius code in this entire plugin: it can stop/delete a
// Windows service, delete a local Windows user account and its profile,
// and uninstall an MSI package -- all via identity checks that must
// never misfire against unrelated system state (an unrelated PostgreSQL
// install at a different path, or a hand-named service/account that
// merely happens to share a name). The decision logic
// (ShouldUninstallCandidate) is deliberately a pure function taking its
// inputs as parameters, not reading the registry/SCM itself, so it is
// unit-testable without touching the real system -- only
// RemovePostgresPartialInstall (the orchestration around it) actually
// performs I/O. Only ever called when IsPostgresInstalled() is false
// (never against a working install), and only from the elevated install
// worker (PostgresInstall.cs) -- every operation here needs an
// Administrator token. Windows-only throughout.
public static partial class TeknoParrotProfileScanner
{
    // Cross-checks PostgreSQL's own installation registry record --
    // written by the EnterpriseDB-based installer under
    // \Installations\<install-id>, independent of the generic Windows
    // Installer Uninstall key -- against the expected install directory.
    // Mirrors Test-PostgresInstallationsRegistry. Absence of any record
    // is "no additional information," never a green light to skip the
    // safety check in ShouldUninstallCandidate; only an explicit
    // mismatch (a record exists and disagrees) is a red flag.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static PostgresInstallationsRegistryCheck GetPostgresInstallationsRegistryCheck(string expectedInstallDir)
    {
        var expected = expectedInstallDir.TrimEnd('\\');
        var subKeyPaths = new[]
        {
            @"SOFTWARE\PostgreSQL\Installations",
            @"SOFTWARE\WOW6432Node\PostgreSQL\Installations",
        };

        var baseDirectories = new List<string>();
        foreach (var subKeyPath in subKeyPaths)
        {
            using var installationsKey = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (installationsKey is null)
            {
                continue;
            }

            foreach (var installId in installationsKey.GetSubKeyNames())
            {
                using var installKey = installationsKey.OpenSubKey(installId);
                if (installKey?.GetValue("Base Directory") is string baseDirectory && !string.IsNullOrWhiteSpace(baseDirectory))
                {
                    baseDirectories.Add(baseDirectory);
                }
            }
        }

        if (baseDirectories.Count == 0)
        {
            return new PostgresInstallationsRegistryCheck(HasRecord: false, Mismatch: false);
        }

        var anyMatch = baseDirectories.Any(dir => string.Equals(dir.TrimEnd('\\'), expected, StringComparison.OrdinalIgnoreCase));
        return new PostgresInstallationsRegistryCheck(HasRecord: true, Mismatch: !anyMatch);
    }

    // Pure decision function (no I/O) -- whether a single Windows
    // Installer Uninstall-key candidate should actually be uninstalled.
    // Only true when BOTH: (1) its InstallLocation exactly matches the
    // expected PostgreSQL 8.3 install directory (ordinal, case-insensitive
    // -- not a wildcard/-like-style match, the same v0.99.24/v0.99.25
    // upstream hardening already ported elsewhere in this plugin), AND
    // (2) PostgreSQL's own Installations registry record does not
    // actively disagree (absent is fine; present-and-disagreeing blocks
    // it). Mirrors the per-candidate logic inside Remove-PostgresPartialInstall.
    internal static bool ShouldUninstallCandidate(string? installLocation, string expectedInstallDir, PostgresInstallationsRegistryCheck registryCheck)
    {
        var expected = expectedInstallDir.TrimEnd('\\');
        var actual = installLocation?.TrimEnd('\\') ?? string.Empty;
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (registryCheck.HasRecord && registryCheck.Mismatch)
        {
            return false;
        }

        return true;
    }

    // Cleans up a half-installed/stale PostgreSQL 8.3 before a fresh
    // install attempt. Safe to call even when nothing is present -- every
    // step checks existence first and skips cleanly. Mirrors
    // Remove-PostgresPartialInstall exactly, including the same
    // confirmed-empirically rationale (a failed install can leave a real
    // Windows account, an orphaned profile, and a ProfileList SID entry
    // behind even when the installer itself reports failure).
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void RemovePostgresPartialInstall(Action<string>? log = null)
    {
        var expectedInstallDir = PostgresInstallDir.TrimEnd('\\');
        var registryCheck = GetPostgresInstallationsRegistryCheck(expectedInstallDir);

        foreach (var (entry, productCode) in EnumeratePostgresUninstallCandidates())
        {
            if (!ShouldUninstallCandidate(entry.InstallLocation, expectedInstallDir, registryCheck))
            {
                log?.Invoke($"Postgres: skipping uninstall of '{entry.DisplayName}' -- does not match our expected install, not touching it.");
                continue;
            }

            var uninstallLog = Path.Combine(Path.GetTempPath(), "pg83-uninstall-" + Guid.NewGuid().ToString("N") + ".log");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                startInfo.ArgumentList.Add("/x");
                startInfo.ArgumentList.Add(productCode);
                startInfo.ArgumentList.Add("/qn");
                startInfo.ArgumentList.Add("/l*v");
                startInfo.ArgumentList.Add(uninstallLog);
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                log?.Invoke($"Postgres: uninstalled stale entry {productCode}");
            }
            finally
            {
                try
                {
                    if (File.Exists(uninstallLog))
                    {
                        File.Delete(uninstallLog);
                    }
                }
                catch (IOException) { /* best-effort cleanup */ }
            }
        }

        RemovePostgresService(log);
        RemovePostgresInstallDirectory(log);
        RemovePostgresLocalUser(log);
        RemoveOrphanedPostgresProfile(log);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<(PostgresUninstallEntry Entry, string ProductCode)> EnumeratePostgresUninstallCandidates()
    {
        var subKeyPaths = new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        foreach (var subKeyPath in subKeyPaths)
        {
            using var uninstallKey = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (uninstallKey is null)
            {
                continue;
            }

            foreach (var productCode in uninstallKey.GetSubKeyNames())
            {
                if (!MsiProductCodePattern.IsMatch(productCode))
                {
                    continue;
                }

                using var entryKey = uninstallKey.OpenSubKey(productCode);
                var displayName = entryKey?.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    !displayName.Contains("PostgreSQL", StringComparison.OrdinalIgnoreCase) ||
                    !displayName.Contains("8.3", StringComparison.Ordinal))
                {
                    continue;
                }

                var installLocation = entryKey?.GetValue("InstallLocation") as string;
                yield return (new PostgresUninstallEntry(displayName, installLocation), productCode);
            }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemovePostgresService(Action<string>? log)
    {
        try
        {
            using var controller = new ServiceController(PostgresServiceName);
            _ = controller.Status; // throws if the service doesn't exist
            if (controller.Status == ServiceControllerStatus.Running)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (InvalidOperationException)
        {
            return; // service doesn't exist -- nothing to remove
        }

        var startInfo = new ProcessStartInfo("sc.exe", $"delete {PostgresServiceName}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo);
        process?.WaitForExit();
        log?.Invoke($"Postgres: removed leftover service {PostgresServiceName}");
    }

    private static void RemovePostgresInstallDirectory(Action<string>? log)
    {
        if (!Directory.Exists(PostgresInstallDir))
        {
            return;
        }

        try
        {
            Directory.Delete(PostgresInstallDir, recursive: true);
            log?.Invoke($"Postgres: removed leftover {PostgresInstallDir}");
        }
        catch (IOException ex)
        {
            log?.Invoke($"Postgres: could not remove leftover {PostgresInstallDir} -- {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemovePostgresLocalUser(Action<string>? log)
    {
        try
        {
            using var context = new PrincipalContext(ContextType.Machine);
            using var user = UserPrincipal.FindByIdentity(context, "postgres");
            if (user is not null)
            {
                user.Delete();
                log?.Invoke("Postgres: removed leftover local user 'postgres'");
            }
        }
        catch (Exception ex) when (ex is PrincipalException or InvalidOperationException)
        {
            log?.Invoke($"Postgres: could not remove leftover local user 'postgres' -- {ex.Message}");
        }
    }

    // Remove-LocalUser does not clean up the profile folder or its
    // ProfileList registry SID mapping -- a stale entry here produces
    // "No mapping between account names and security IDs was done" on
    // the next install attempt (confirmed empirically by the original
    // project). Mirrors that same cleanup.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RemoveOrphanedPostgresProfile(Action<string>? log)
    {
        const string profileListPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
        using var profileListKey = Registry.LocalMachine.OpenSubKey(profileListPath, writable: true);
        if (profileListKey is null)
        {
            return;
        }

        foreach (var sid in profileListKey.GetSubKeyNames())
        {
            string? imagePath;
            using (var sidKey = profileListKey.OpenSubKey(sid))
            {
                imagePath = sidKey?.GetValue("ProfileImagePath") as string;
            }

            if (string.IsNullOrWhiteSpace(imagePath) || !imagePath.EndsWith(@"\postgres", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                profileListKey.DeleteSubKeyTree(sid, throwOnMissingSubKey: false);
                if (Directory.Exists(imagePath))
                {
                    Directory.Delete(imagePath, recursive: true);
                }
                log?.Invoke($"Postgres: removed orphaned profile registration for {imagePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"Postgres: could not remove orphaned profile {imagePath} -- {ex.Message}");
            }
        }

        const string fallbackProfilePath = @"C:\Users\postgres";
        if (Directory.Exists(fallbackProfilePath))
        {
            try
            {
                Directory.Delete(fallbackProfilePath, recursive: true);
            }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}

internal sealed record PostgresInstallationsRegistryCheck(bool HasRecord, bool Mismatch);

internal sealed record PostgresUninstallEntry(string DisplayName, string? InstallLocation);

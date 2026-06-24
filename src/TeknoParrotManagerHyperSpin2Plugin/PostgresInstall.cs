using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 9 (install half) of ROADMAP.md. Installing PostgreSQL 8.3 runs an
// MSI installer and creates a Windows service plus a local Windows user
// account -- operations that need Administrator privileges. Unlike the
// msiexec child-process call alone, the partial-install cleanup this
// file also performs touches the registry and a local user account
// in-process (Microsoft.Win32.Registry, System.DirectoryServices.AccountManagement),
// which cannot be elevated via Process.Start's Verb="runas" the way a
// child *process* launch can -- only the calling process's own token
// matters for those APIs. So the whole install+cleanup sequence
// self-elevates by re-launching this same executable with a special,
// internal-only "--postgres-install-elevated" argument (one UAC prompt),
// rather than elevating the plugin's own long-running process or
// HyperHQ itself. See README.md Safety Notes for the full picture.
public static partial class TeknoParrotProfileScanner
{
    private const string PostgresGuideRepo = "Eggmansworld/tp-it-guides";
    private static readonly Regex MsiProductCodePattern = new(
        @"^\{[0-9A-Fa-f]{8}-([0-9A-Fa-f]{4}-){3}[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

    // Fetches the community-packaged PostgreSQL 8.3 installer + per-game
    // guide release info. Unlike BepInEx (BepInEx's own repo) and FFB
    // Plugin (the plugin's own repo), this ZIP is a community
    // repackaging of PostgreSQL 8.3 -- long discontinued by the
    // PostgreSQL project itself -- hosted by Eggman, the same source
    // already credited for this plugin's Collection Dat. Mirrors
    // Get-PostgresGuideRelease.
    public static async Task<PostgresGuideRelease?> GetPostgresGuideReleaseAsync(
        HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{PostgresGuideRepo}/releases/tags/universal-guide");
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellationToken).ConfigureAwait(false);

                if (!body.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                    if (string.IsNullOrWhiteSpace(downloadUrl) || !SafeGitHubDownloadHost.IsMatch(downloadUrl))
                    {
                        log?.Invoke("PostgresGuide: unexpected download URL format -- skipping.");
                        return null;
                    }

                    var sizeBytes = asset.TryGetProperty("size", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0L;
                    return new PostgresGuideRelease(downloadUrl, name, sizeBytes);
                }

                return null;
            }
            catch (HttpRequestException ex)
            {
                var status = (int?)ex.StatusCode ?? 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"PostgresGuide: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or JsonException)
            {
                if (attempt >= 3)
                {
                    log?.Invoke($"PostgresGuide: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Same temp-file-then-move retry shape as DownloadBepInExZipAsync.
    private static async Task<string?> DownloadPostgresGuideAsync(
        HttpClient http, PostgresGuideRelease release, string destinationDir, Action<string>? log, CancellationToken cancellationToken)
    {
        var savePath = ResolveEggmanDatSavePath(destinationDir, release.FileName);
        if (savePath is null)
        {
            log?.Invoke($"PostgresGuide: SECURITY -- unsafe release filename '{release.FileName}', aborted.");
            return null;
        }

        Directory.CreateDirectory(destinationDir);
        var tempPath = savePath + ".tmp";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl))
                {
                    request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                    await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }
                File.Move(tempPath, savePath);
                LogDownloadAudit(release.DownloadUrl, release.FileName, savePath, version: null, log);
                return savePath;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch (IOException) { /* best-effort cleanup */ }

                var status = ex is HttpRequestException httpEx ? (int?)httpEx.StatusCode ?? 0 : 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"PostgresGuide: download failed -- {ex.Message}");
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    // Top-level orchestration for the install action, run UNELEVATED.
    // Downloads/extracts the guide ZIP (no elevation needed for network
    // access), then self-elevates the actual cleanup+install sequence by
    // re-launching this same executable with a special internal CLI
    // argument (one UAC prompt covers the whole privileged sequence).
    // Mirrors Install-Postgres83, restructured for self-elevation instead
    // of requiring the whole calling process to already be elevated.
    public static async Task<PostgresInstallResult> InstallPostgres83Async(
        HttpClient http, string serviceAccountPasswordPlain, string superuserPasswordPlain, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PostgresInstallResult(false, "PostgreSQL setup is only supported on Windows.");
        }

        if (IsPostgresInstalled())
        {
            return new PostgresInstallResult(true, "PostgreSQL is already installed.");
        }

        var release = await GetPostgresGuideReleaseAsync(http, log, cancellationToken).ConfigureAwait(false);
        if (release is null)
        {
            return new PostgresInstallResult(false, "Could not find the PostgreSQL installer guide on GitHub. See the log for details.");
        }

        var cacheDir = Path.Combine(AppContext.BaseDirectory, "PostgresInstallCache");
        var workDir = Path.Combine(Path.GetTempPath(), "pg83-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        try
        {
            var zipPath = await DownloadPostgresGuideAsync(http, release, cacheDir, log, cancellationToken).ConfigureAwait(false);
            if (zipPath is null)
            {
                return new PostgresInstallResult(false, "Download failed. See the log for details.");
            }

            ExtractZipSafe(zipPath, workDir);
            var msiFile = Directory.EnumerateFiles(workDir, "postgresql-8.3-int.msi", SearchOption.AllDirectories).FirstOrDefault();
            if (msiFile is null)
            {
                return new PostgresInstallResult(false, "Installer file not found inside the downloaded ZIP.");
            }

            return await RunElevatedInstallAsync(msiFile, serviceAccountPasswordPlain, superuserPasswordPlain, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    // Self-elevates by re-launching this same executable with
    // "--postgres-install-elevated <argsFile> <resultFile>" via
    // Verb="runas" (one UAC prompt), waits for it to finish, then reads
    // back the result. The args file (containing both plaintext
    // passwords) is written here and deleted by the elevated process as
    // soon as it reads it; the result file (success/error message only --
    // never a password) is deleted here after reading.
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task<PostgresInstallResult> RunElevatedInstallAsync(
        string msiPath, string serviceAccountPasswordPlain, string superuserPasswordPlain, Action<string>? log, CancellationToken cancellationToken)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new PostgresInstallResult(false, "Could not determine this plugin's own executable path for self-elevation.");
        }

        var argsPath = Path.Combine(Path.GetTempPath(), "tpm-pg83-args-" + Guid.NewGuid().ToString("N") + ".json");
        var resultPath = Path.Combine(Path.GetTempPath(), "tpm-pg83-result-" + Guid.NewGuid().ToString("N") + ".json");

        try
        {
            var argsJson = JsonSerializer.Serialize(new PostgresInstallElevatedArgs(msiPath, serviceAccountPasswordPlain, superuserPasswordPlain));
            File.WriteAllText(argsPath, argsJson, new System.Text.UTF8Encoding(false));
            LockDownFileToCurrentUser(argsPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
            };
            startInfo.ArgumentList.Add("--postgres-install-elevated");
            startInfo.ArgumentList.Add(argsPath);
            startInfo.ArgumentList.Add(resultPath);

            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED -- user declined the UAC prompt
            {
                return new PostgresInstallResult(false, "The Administrator permission prompt was cancelled -- PostgreSQL was not installed.");
            }

            if (process is null)
            {
                return new PostgresInstallResult(false, "Could not start the elevated installer process.");
            }

            using (process)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(resultPath))
            {
                return new PostgresInstallResult(false, "The elevated installer process did not report a result.");
            }

            var resultJson = File.ReadAllText(resultPath);
            var result = JsonSerializer.Deserialize<PostgresInstallElevatedResult>(resultJson);
            if (result is null)
            {
                return new PostgresInstallResult(false, "Could not read the elevated installer process's result.");
            }

            if (result.Success)
            {
                SaveSuperuserPassword(superuserPasswordPlain);
                log?.Invoke("Postgres install: succeeded.");
                return new PostgresInstallResult(true, "PostgreSQL 8.3 installed and running.");
            }

            log?.Invoke($"Postgres install: FAILED -- {result.ErrorMessage}");
            return new PostgresInstallResult(false, result.ErrorMessage ?? "PostgreSQL install did not complete successfully.");
        }
        finally
        {
            try
            {
                if (File.Exists(argsPath))
                {
                    File.Delete(argsPath);
                }
                if (File.Exists(resultPath))
                {
                    File.Delete(resultPath);
                }
            }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void LockDownFileToCurrentUser(string path)
    {
        try
        {
            var owner = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            var icaclsInfo = new ProcessStartInfo("icacls", $"\"{path}\" /inheritance:r /grant:r \"{owner}:(R)\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var icacls = Process.Start(icaclsInfo);
            icacls?.WaitForExit(5000);
        }
        catch (Exception) { /* best-effort hardening, not load-bearing */ }
    }

    // Entry point for the self-elevated child process (see Program.cs's
    // Main, "--postgres-install-elevated" branch). Runs WITH an
    // Administrator token -- this is the only place in this plugin that
    // performs privileged Windows operations (partial-install cleanup,
    // the MSI install itself). Never throws out of this method -- every
    // failure path is captured into the result file so the unelevated
    // caller always gets a clear answer back.
    public static async Task RunElevatedPostgresInstallWorkerAsync(string argsPath, string resultPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.WriteAllText(resultPath, JsonSerializer.Serialize(new PostgresInstallElevatedResult(false, "PostgreSQL setup is only supported on Windows.")), new System.Text.UTF8Encoding(false));
            }
            catch (IOException) { /* nothing more we can do */ }
            return;
        }

        PostgresInstallElevatedResult result;
        try
        {
            var argsJson = File.ReadAllText(argsPath);
            var installArgs = JsonSerializer.Deserialize<PostgresInstallElevatedArgs>(argsJson)
                ?? throw new InvalidOperationException("Could not parse install arguments.");

            try
            {
                File.Delete(argsPath);
            }
            catch (IOException) { /* best-effort -- contains plaintext passwords, delete ASAP regardless */ }

            if (!IsRunningAsAdministrator())
            {
                result = new PostgresInstallElevatedResult(false, "Elevation did not succeed -- still not running as Administrator.");
            }
            else
            {
                RemovePostgresPartialInstall();
                result = RunMsiInstall(installArgs.MsiPath, installArgs.ServiceAccountPasswordPlain, installArgs.SuperuserPasswordPlain);
            }
        }
        catch (Exception ex)
        {
            result = new PostgresInstallElevatedResult(false, $"Elevated install worker failed -- {ex.Message}");
        }

        try
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result), new System.Text.UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Nothing more we can do -- the unelevated caller will time
            // out waiting for a result file that never appeared, which
            // it already handles as "no result reported."
        }

        await Task.CompletedTask;
    }

    // Builds and runs the same msiexec property set as the original's
    // Install-Postgres83, already confirmed working by the original's
    // own testing. Checks proc.ExitCode == 0 AND IsPostgresInstalled()
    // afterward (not exit code alone) -- the original's own comment
    // notes a misleading "success" with no real service was observed
    // empirically. Known, accepted trade-off (same as the original):
    // SERVICEPASSWORD/SUPERPASSWORD passed as msiexec command-line
    // properties are briefly visible to anything that can inspect this
    // process's command line for the duration of this one call -- there
    // is no msiexec mechanism that avoids this for a silent
    // property-driven install.
    private static PostgresInstallElevatedResult RunMsiInstall(string msiPath, string serviceAccountPasswordPlain, string superuserPasswordPlain)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "pg83-install-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[]
            {
                "/i", msiPath,
                "/qn",
                "/l*v", logPath,
                "INTERNALLAUNCH=1",
                "ROOTDRIVE=C:\\",
                "SERVICEACCOUNT=postgres",
                $"SERVICEDOMAIN={Environment.MachineName}",
                $"SERVICEPASSWORD={serviceAccountPasswordPlain}",
                $"SERVICEPASSWORDV={serviceAccountPasswordPlain}",
                "CREATESERVICEUSER=1",
                "SUPERUSER=postgres",
                $"SUPERPASSWORD={superuserPasswordPlain}",
                "LISTENPORT=5432",
                "LOCALE=C",
                "ENCODING=UTF8",
                "CLENCODE=UTF8",
                "PERMITREMOTE=0",
                "RUNSTACKBUILDER=0",
                "DOSERVICE=1",
                "DOINITDB=1",
            })
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            process?.WaitForExit();

            if (process is null || process.ExitCode != 0 || !IsPostgresInstalled())
            {
                return new PostgresInstallElevatedResult(false, $"PostgreSQL install did not complete successfully (msiexec exit code {process?.ExitCode}).");
            }

            return new PostgresInstallElevatedResult(true, null);
        }
        finally
        {
            try
            {
                if (File.Exists(logPath))
                {
                    File.Delete(logPath);
                }
            }
            catch (IOException) { /* best-effort -- the verbose log can contain passwords in deferred custom-action data */ }
        }
    }
}

public sealed record PostgresGuideRelease(string DownloadUrl, string FileName, long SizeBytes);

public sealed record PostgresInstallResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message);

internal sealed record PostgresInstallElevatedArgs(string MsiPath, string ServiceAccountPasswordPlain, string SuperuserPasswordPlain);

internal sealed record PostgresInstallElevatedResult(bool Success, string? ErrorMessage);

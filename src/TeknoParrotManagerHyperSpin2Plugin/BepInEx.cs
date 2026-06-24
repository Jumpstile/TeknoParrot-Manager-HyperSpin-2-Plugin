using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 8 of ROADMAP.md: ports Get-BepInExLatestRelease,
// Get-BepInExInstalledVersion, Get-BepInExInstalledArch (via the existing
// GetExeArchitecture helper in ReShade.cs), and Invoke-BepInExUpdateCheck.
// This is the first Group B feature in this plugin: it downloads and
// extracts a third-party binary release, rather than only reading/parsing
// data the user already supplied. It is deliberately update-only, exactly
// like the original -- it never fresh-installs BepInEx, only updates a
// game that already has an existing 64-bit install. See README.md Safety
// Notes for the full description of the safeguards involved.
public static partial class TeknoParrotProfileScanner
{
    private const string BepInExRepo = "BepInEx/BepInEx";

    private static readonly Regex BepInExAssetPattern = new(
        @"^BepInEx_win_x64_.*\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Picks the newest non-prerelease entry from the /releases LIST
    // endpoint (not /releases/latest -- BepInEx's literal "latest" tag is
    // sometimes a v6 pre-release) and selects its x64 zip asset. Returns
    // null if the first non-prerelease release has no matching asset, or
    // if the matching asset's download URL fails SafeGitHubDownloadHost.
    internal static BepInExRelease? SelectBepInExAsset(JsonElement releasesJson)
    {
        if (releasesJson.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var release in releasesJson.EnumerateArray())
        {
            var isPrerelease = release.TryGetProperty("prerelease", out var prereleaseProp) &&
                                prereleaseProp.ValueKind == JsonValueKind.True;
            if (isPrerelease)
            {
                continue;
            }

            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tagName = release.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || !BepInExAssetPattern.IsMatch(name))
                {
                    continue;
                }

                var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProp) ? urlProp.GetString() : null;
                if (string.IsNullOrWhiteSpace(downloadUrl) || !SafeGitHubDownloadHost.IsMatch(downloadUrl))
                {
                    return null;
                }

                var version = (tagName ?? string.Empty).TrimStart('v');
                return new BepInExRelease(version, downloadUrl, name);
            }

            // First non-prerelease release had no matching asset -- mirrors
            // the original script taking "the first (newest) match" rather
            // than scanning further releases for one.
            return null;
        }

        return null;
    }

    // Queries GitHub's /releases LIST endpoint for BepInEx/BepInEx and
    // returns the newest non-prerelease's x64 zip asset info, or null.
    // Same retry/timeout/User-Agent shape as GetEggmanDatReleaseAsync.
    public static async Task<BepInExRelease?> GetBepInExLatestReleaseAsync(
        HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{BepInExRepo}/releases");
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, attemptCts.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: attemptCts.Token).ConfigureAwait(false);
                var release = SelectBepInExAsset(body);
                if (release is null)
                {
                    log?.Invoke("BepInEx: no matching x64 release asset found.");
                }
                return release;
            }
            catch (HttpRequestException ex)
            {
                var status = (int?)ex.StatusCode ?? 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"BepInEx: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex) when (ex is TaskCanceledException or JsonException)
            {
                if (attempt >= 3)
                {
                    log?.Invoke($"BepInEx: GitHub release query failed -- {ex.Message}");
                    return null;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    // Reads BepInEx\core\BepInEx.dll's FileVersionInfo for a game's exe
    // folder. Returns null if not installed there or unreadable. Does NOT
    // read .doorstop_version -- that's a different, unrelated Doorstop
    // bootstrap version, not BepInEx's own. Mirrors
    // Get-BepInExInstalledVersion.
    public static string? GetBepInExInstalledVersion(string exeDir)
    {
        var dllPath = Path.Combine(exeDir, "BepInEx", "core", "BepInEx.dll");
        if (!File.Exists(dllPath))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(dllPath);
            return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Walks every registered UserProfile with a GamePath that exists on
    // disk, checking each for an existing x64 BepInEx install and
    // comparing its version against latest. Read-only -- never downloads
    // or writes anything. This is the preview half; see
    // ApplyBepInExUpdates for the mutating half. Architecture is
    // determined by reading the native Doorstop winhttp.dll shim's PE
    // machine type via the existing GetExeArchitecture helper (ReShade.cs)
    // -- BepInEx's own managed DLLs are AnyCPU in both x86/x64 zips and
    // can't reveal install arch. Mirrors the read-only portion of
    // Invoke-BepInExUpdateCheck.
    public static BepInExCheckResult CheckBepInExUpdates(TeknoParrotSettings settings, BepInExRelease latest)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");

        var outdated = new List<BepInExGameStatus>();
        var upToDateCount = 0;
        var skippedX86Count = 0;
        var skippedUnknownArchCount = 0;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new BepInExCheckResult(latest.Version, outdated, upToDateCount, skippedX86Count, skippedUnknownArchCount, errors);
        }

        foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            try
            {
                var doc = XDocument.Load(file);
                var gamePath = ChildByLocalName(doc.Root, "GamePath")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
                {
                    continue;
                }

                var exeDir = Path.GetDirectoryName(gamePath);
                if (string.IsNullOrWhiteSpace(exeDir))
                {
                    continue;
                }

                var installedVersion = GetBepInExInstalledVersion(exeDir);
                if (installedVersion is null)
                {
                    // BepInEx not installed here -- irrelevant, not an error.
                    continue;
                }

                var arch = GetExeArchitecture(Path.Combine(exeDir, "winhttp.dll"));
                if (arch == "x86")
                {
                    skippedX86Count++;
                    continue;
                }
                if (arch is null)
                {
                    skippedUnknownArchCount++;
                    continue;
                }

                if (Version.TryParse(installedVersion, out var installed) &&
                    Version.TryParse(latest.Version, out var latestVersion) &&
                    installed < latestVersion)
                {
                    outdated.Add(new BepInExGameStatus(code, exeDir, installedVersion, latest.Version));
                }
                else
                {
                    upToDateCount++;
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                errors.Add(code);
            }
        }

        return new BepInExCheckResult(latest.Version, outdated, upToDateCount, skippedX86Count, skippedUnknownArchCount, errors);
    }

    // Backs up an existing BepInEx install's well-known files/folders into
    // a timestamped sibling folder under the game's own exe directory
    // (NOT the unrelated UserProfiles-wide backup TryBackupProfilesForMutation
    // already does -- this backs up the game's own exe-folder contents),
    // then extracts the already-downloaded zip into exeDir via
    // ExtractZipSafe. Never deletes anything first, only overwrites.
    // Returns the backup folder path, or an empty string if nothing
    // existed to back up. Mirrors the per-game backup-then-extract half of
    // Invoke-BepInExUpdateCheck.
    private static string BackupAndExtractBepInEx(string exeDir, string zipPath, Action<string>? log)
    {
        var backupRoot = Path.Combine(exeDir, "BepInExBackups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var itemsToBackUp = new[] { "BepInEx", "doorstop_config.ini", "winhttp.dll", ".doorstop_version", "changelog.txt" };
        var backedUpAny = false;

        foreach (var item in itemsToBackUp)
        {
            var sourcePath = Path.Combine(exeDir, item);
            if (Directory.Exists(sourcePath))
            {
                Directory.CreateDirectory(backupRoot);
                CopyDirectoryRecursive(sourcePath, Path.Combine(backupRoot, item));
                backedUpAny = true;
            }
            else if (File.Exists(sourcePath))
            {
                Directory.CreateDirectory(backupRoot);
                File.Copy(sourcePath, Path.Combine(backupRoot, item), overwrite: false);
                backedUpAny = true;
            }
        }

        ExtractZipSafe(zipPath, exeDir);
        log?.Invoke($"BepInEx: backed up {(backedUpAny ? backupRoot : "(nothing existed)")} then updated {exeDir}.");
        return backedUpAny ? backupRoot : string.Empty;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectoryRecursive(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }

    // Downloads the release ZIP once into this plugin's own BepInExCache
    // folder, audit-logs it, then backs up + extracts it into every
    // outdated game's exe folder. Never fresh-installs -- callers must
    // have already filtered to games with an existing x64 install (see
    // CheckBepInExUpdates). Mirrors the mutating half of
    // Invoke-BepInExUpdateCheck.
    public static async Task<BepInExApplyResult> ApplyBepInExUpdates(
        HttpClient http, BepInExRelease latest, IReadOnlyCollection<BepInExGameStatus> outdatedGames,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(latest);

        var updated = new List<BepInExUpdatedGame>();
        var errors = new List<string>();

        if (outdatedGames.Count == 0)
        {
            return new BepInExApplyResult(updated, errors, null);
        }

        var cacheDir = Path.Combine(AppContext.BaseDirectory, "BepInExCache");
        var downloadedPath = await DownloadBepInExZipAsync(http, latest, cacheDir, log, cancellationToken).ConfigureAwait(false);
        if (downloadedPath is null)
        {
            return new BepInExApplyResult(updated, errors, null);
        }

        LogDownloadAudit(latest.DownloadUrl, latest.FileName, downloadedPath, latest.Version, log);

        foreach (var game in outdatedGames)
        {
            try
            {
                var backupPath = BackupAndExtractBepInEx(game.ExeDir, downloadedPath, log);
                updated.Add(new BepInExUpdatedGame(game.ProfileCode, game.InstalledVersion, latest.Version, backupPath));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Invoke($"BepInEx: FAILED {game.ProfileCode} -- {ex.Message}");
                errors.Add(game.ProfileCode);
            }
        }

        return new BepInExApplyResult(updated, errors, cacheDir);
    }

    // Downloads the BepInEx release ZIP into destinationDir. Same
    // sanitize-filename-then-containment-check (ResolveEggmanDatSavePath)
    // and temp-file-then-move retry shape as DownloadEggmanDatAsync.
    private static async Task<string?> DownloadBepInExZipAsync(
        HttpClient http, BepInExRelease release, string destinationDir, Action<string>? log, CancellationToken cancellationToken)
    {
        var savePath = ResolveEggmanDatSavePath(destinationDir, release.FileName);
        if (savePath is null)
        {
            log?.Invoke($"BepInEx: SECURITY -- unsafe release filename '{release.FileName}', aborted.");
            return null;
        }

        Directory.CreateDirectory(destinationDir);
        var tempPath = savePath + ".tmp";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, release.DownloadUrl);
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(savePath))
                {
                    File.Delete(savePath);
                }
                File.Move(tempPath, savePath);
                log?.Invoke($"BepInEx: downloaded '{release.FileName}' to '{savePath}'.");
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
                catch (IOException cleanupEx)
                {
                    log?.Invoke($"BepInEx: could not remove partial download '{tempPath}' -- {cleanupEx.Message}");
                }

                var status = ex is HttpRequestException httpEx ? (int?)httpEx.StatusCode ?? 0 : 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"BepInEx: download failed -- {ex.Message}");
                    return null;
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }
}

public sealed record BepInExRelease(string Version, string DownloadUrl, string FileName);

public sealed record BepInExGameStatus(
    [property: JsonPropertyName("profile_code")] string ProfileCode,
    [property: JsonPropertyName("exe_dir")] string ExeDir,
    [property: JsonPropertyName("installed_version")] string InstalledVersion,
    [property: JsonPropertyName("latest_version")] string LatestVersion);

public sealed record BepInExCheckResult(
    [property: JsonPropertyName("latest_version")] string LatestVersion,
    [property: JsonPropertyName("outdated_games")] IReadOnlyList<BepInExGameStatus> OutdatedGames,
    [property: JsonPropertyName("up_to_date_count")] int UpToDateCount,
    [property: JsonPropertyName("skipped_x86_count")] int SkippedX86Count,
    [property: JsonPropertyName("skipped_unknown_arch_count")] int SkippedUnknownArchCount,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles);

public sealed record BepInExUpdatedGame(
    [property: JsonPropertyName("profile_code")] string ProfileCode,
    [property: JsonPropertyName("old_version")] string OldVersion,
    [property: JsonPropertyName("new_version")] string NewVersion,
    [property: JsonPropertyName("backup_path")] string BackupPath);

public sealed record BepInExApplyResult(
    [property: JsonPropertyName("updated_games")] IReadOnlyList<BepInExUpdatedGame> UpdatedGames,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles,
    [property: JsonPropertyName("cache_path")] string? CachePath);

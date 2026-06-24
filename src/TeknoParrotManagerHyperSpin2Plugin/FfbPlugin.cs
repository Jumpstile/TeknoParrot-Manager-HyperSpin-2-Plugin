using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 7 (free third-party plugin path) of ROADMAP.md: ports
// Get-FFBPluginGameMap, Invoke-FFBPluginDownload, and Invoke-FFBPluginSetup.
// Covers a different game set than FFB Blaster (FfbBlaster.cs) via the
// free, open-source mightymikem/FFBArcadePlugin project's dinput8.dll-
// replacement hook. This is the second Group B feature in this plugin
// (after BepInEx update check, v0.12.0) -- it downloads MAME32.dll/
// MAME64.dll directly from raw.githubusercontent.com, and a live
// game-name -> destination-filename table parsed from that repo's
// AutoSetup.cmd script. See README.md Safety Notes for the full set of
// safeguards involved.
public static partial class TeknoParrotProfileScanner
{
    private const string FfbPluginRepoRawBase = "https://raw.githubusercontent.com/mightymikem/FFBArcadePlugin/master";

    // Mirrors Get-FFBPluginGameMap's exact regex: a "cd <folder>" /
    // "rename dinput8.dll <dest>" / "cd.." block per game in the repo's
    // own install script.
    private static readonly Regex FfbPluginAutoSetupPattern = new(
        "^cd\\s+\"?([^\"\\r\\n]+?)\"?\\s*\\r?\\nrename\\s+dinput8\\.dll\\s+(\\S+)\\s*\\r?\\ncd\\.\\.",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // Fetches and parses the live AutoSetup.cmd table (folder name ->
    // destination DLL filename) from the FFBArcadePlugin repo. Same
    // 3-attempt/5s-backoff retry shape as every other fetch this session.
    // Returns an empty map (never a hardcoded fallback) on failure --
    // callers treat an empty map as "could not determine," not "no games
    // need this."
    public static async Task<Dictionary<string, string>> GetFfbPluginGameMapAsync(
        HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{FfbPluginRepoRawBase}/AutoSetup.cmd");
                request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                foreach (Match m in FfbPluginAutoSetupPattern.Matches(content))
                {
                    var folderName = m.Groups[1].Value.Trim();
                    var destDll = m.Groups[2].Value.Trim();
                    if (folderName.Length > 0 && destDll.Length > 0)
                    {
                        map[folderName] = destDll;
                    }
                }

                if (map.Count == 0)
                {
                    log?.Invoke("FfbPlugin: 0 entries parsed from AutoSetup.cmd -- format may have changed.");
                }
                return map;
            }
            catch (HttpRequestException ex)
            {
                var status = (int?)ex.StatusCode ?? 0;
                if (attempt >= 3 || status is >= 400 and < 500)
                {
                    log?.Invoke($"FfbPlugin: AutoSetup.cmd fetch failed -- {ex.Message}");
                    return map;
                }
            }
            catch (TaskCanceledException ex)
            {
                if (attempt >= 3)
                {
                    log?.Invoke($"FfbPlugin: AutoSetup.cmd fetch failed -- {ex.Message}");
                    return map;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        return map;
    }

    // Downloads MAME32.dll and MAME64.dll as plain files directly from the
    // repo root -- no release ZIP, no extraction step. Returns true if at
    // least one architecture's DLL was downloaded. Mirrors
    // Invoke-FFBPluginDownload.
    public static async Task<bool> DownloadFfbPluginDllsAsync(
        HttpClient http, string destDir, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        Directory.CreateDirectory(destDir);
        var got = false;

        foreach (var dllName in new[] { "MAME32.dll", "MAME64.dll" })
        {
            var uri = $"{FfbPluginRepoRawBase}/{dllName}";
            var destPath = Path.Combine(destDir, dllName);

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                    {
                        request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
                        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        response.EnsureSuccessStatusCode();

                        await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                        await using (var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await contentStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    got = true;
                    log?.Invoke($"FfbPlugin: downloaded {dllName}");
                    LogDownloadAudit(uri, dllName, destPath, version: null, log);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    try
                    {
                        if (File.Exists(destPath))
                        {
                            File.Delete(destPath);
                        }
                    }
                    catch (IOException) { /* best-effort cleanup */ }

                    var status = ex is HttpRequestException httpEx ? (int?)httpEx.StatusCode ?? 0 : 0;
                    if (attempt >= 3 || status is >= 400 and < 500)
                    {
                        log?.Invoke($"FfbPlugin: {dllName} download failed -- {ex.Message}");
                        break;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        return got;
    }

    // Read-only: fuzzy-matches every registered UserProfile's exe folder
    // name against the live FFB plugin table, then determines whether
    // each match is already covered by FFB Blaster by reading the
    // profile's own current on-disk state (not a session-scoped "enabled
    // this run" list like the original's $NativeEnabledCodes -- this
    // reads what's actually true right now, including a game enabled via
    // TeknoParrotUI itself or in an earlier session). Default scope
    // (gameCodes null) skips a match already covered by native FFB
    // Blaster; an explicit gameCodes list overrides that skip for named
    // games, mirroring dgVoodoo2/ReShade's existing override convention.
    // Mirrors the read-only matching portion of Invoke-FFBPluginSetup.
    public static FfbPluginCheckResult CheckFfbPluginSetup(
        TeknoParrotSettings settings, Dictionary<string, string> gameMap, IReadOnlyCollection<string>? gameCodes, Action<string>? log = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolveGameProfilesPathForSettings(settings);
        var ffbBlasterFields = DiscoverFfbBlasterFieldNames(gameProfilesPath, log);
        var explicitCodes = gameCodes is null ? null : new HashSet<string>(gameCodes, StringComparer.OrdinalIgnoreCase);

        var matched = new List<FfbPluginMatch>();
        var skippedNative = 0;
        var skippedCollision = 0;
        var skippedNoMatch = 0;
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath) || gameMap.Count == 0)
        {
            return new FfbPluginCheckResult(matched, skippedNative, skippedCollision, skippedNoMatch, errors);
        }

        var normalizedTable = gameMap.Keys
            .Select(name => (Name: name, Norm: NormalizeGameKey(name), Dest: gameMap[name]))
            .ToList();

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

                var folderName = Path.GetFileName(exeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var normFolder = NormalizeGameKey(folderName);

                string? bestDest = null;
                string? bestName = null;
                var bestScore = 0.0;
                foreach (var entry in normalizedTable)
                {
                    var score = GetDiceSimilarity(normFolder, entry.Norm);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDest = entry.Dest;
                        bestName = entry.Name;
                    }
                }

                if (bestDest is null || bestScore < FuzzyAutoThreshold)
                {
                    skippedNoMatch++;
                    continue;
                }

                var isNativeEnabled = ffbBlasterFields.Count > 0 && EvaluateFfbBlaster(doc, ffbBlasterFields) is { Eligible: true, UpToDate: true };
                if (isNativeEnabled && (explicitCodes is null || !explicitCodes.Contains(code)))
                {
                    skippedNative++;
                    continue;
                }

                var destPath = Path.Combine(exeDir, bestDest);
                if (!IsPathInside(destPath, exeDir))
                {
                    log?.Invoke($"FfbPlugin: SECURITY -- skipped {code}, destDll '{bestDest}' resolves outside {exeDir}");
                    errors.Add(code);
                    continue;
                }

                if (File.Exists(destPath))
                {
                    skippedCollision++;
                    continue;
                }

                matched.Add(new FfbPluginMatch(code, exeDir, gamePath, bestDest, bestName ?? string.Empty, Math.Round(bestScore, 2)));
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                errors.Add(code);
            }
        }

        return new FfbPluginCheckResult(matched, skippedNative, skippedCollision, skippedNoMatch, errors);
    }

    // Downloads both plugin DLLs once into this plugin's own FFBPluginCache
    // folder, then deploys the matching architecture's DLL to every game
    // CheckFfbPluginSetup matched. Never overwrites an existing file at
    // the destination -- the check pass already filters those out as
    // collisions, but this re-checks immediately before each write too,
    // same belt-and-suspenders pattern as ExtractZipSafe's inline re-check.
    // Mirrors the deploy portion of Invoke-FFBPluginSetup.
    public static async Task<FfbPluginApplyResult> ApplyFfbPluginSetup(
        HttpClient http, TeknoParrotSettings settings, Dictionary<string, string> gameMap, IReadOnlyCollection<string>? gameCodes,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var check = CheckFfbPluginSetup(settings, gameMap, gameCodes, log);
        var deployed = new List<FfbPluginDeployedGame>();
        var errors = new List<string>(check.ErrorProfiles);
        var skippedDllMissing = 0;

        if (check.MatchedGames.Count == 0)
        {
            return new FfbPluginApplyResult(deployed, check.SkippedNativeCount, check.SkippedCollisionCount, check.SkippedNoMatchCount, skippedDllMissing, errors, null);
        }

        var cacheDir = Path.Combine(AppContext.BaseDirectory, "FFBPluginCache");
        if (!await DownloadFfbPluginDllsAsync(http, cacheDir, log, cancellationToken).ConfigureAwait(false))
        {
            log?.Invoke("FfbPlugin: aborted -- DLL download failed.");
            return new FfbPluginApplyResult(deployed, check.SkippedNativeCount, check.SkippedCollisionCount, check.SkippedNoMatchCount, skippedDllMissing, errors, null);
        }

        var srcDll32 = Path.Combine(cacheDir, "MAME32.dll");
        var srcDll64 = Path.Combine(cacheDir, "MAME64.dll");

        foreach (var match in check.MatchedGames)
        {
            try
            {
                var destPath = Path.Combine(match.ExeDir, match.DestDll);
                if (!IsPathInside(destPath, match.ExeDir))
                {
                    log?.Invoke($"FfbPlugin: SECURITY -- skipped {match.ProfileCode}, destDll '{match.DestDll}' resolves outside {match.ExeDir}");
                    errors.Add(match.ProfileCode);
                    continue;
                }

                if (File.Exists(destPath))
                {
                    continue;
                }

                var arch = GetExeArchitecture(match.GamePath);
                var srcDll = arch == "x86" ? srcDll32 : srcDll64;
                if (!File.Exists(srcDll))
                {
                    skippedDllMissing++;
                    continue;
                }

                File.Copy(srcDll, destPath);
                log?.Invoke($"FfbPlugin: deployed {match.DestDll} to {match.ExeDir} (matched '{match.MatchedName}', score {match.Score})");
                deployed.Add(new FfbPluginDeployedGame(match.ProfileCode, match.DestDll, match.MatchedName, match.Score));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"FfbPlugin: FAILED {match.ProfileCode} -- {ex.Message}");
                errors.Add(match.ProfileCode);
            }
        }

        return new FfbPluginApplyResult(deployed, check.SkippedNativeCount, check.SkippedCollisionCount, check.SkippedNoMatchCount, skippedDllMissing, errors, cacheDir);
    }
}

public sealed record FfbPluginMatch(
    [property: JsonPropertyName("profile_code")] string ProfileCode,
    [property: JsonPropertyName("exe_dir")] string ExeDir,
    [property: JsonPropertyName("game_path")] string GamePath,
    [property: JsonPropertyName("dest_dll")] string DestDll,
    [property: JsonPropertyName("matched_name")] string MatchedName,
    [property: JsonPropertyName("score")] double Score);

public sealed record FfbPluginCheckResult(
    [property: JsonPropertyName("matched_games")] IReadOnlyList<FfbPluginMatch> MatchedGames,
    [property: JsonPropertyName("skipped_native_count")] int SkippedNativeCount,
    [property: JsonPropertyName("skipped_collision_count")] int SkippedCollisionCount,
    [property: JsonPropertyName("skipped_no_match_count")] int SkippedNoMatchCount,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles);

public sealed record FfbPluginDeployedGame(
    [property: JsonPropertyName("profile_code")] string ProfileCode,
    [property: JsonPropertyName("dest_dll")] string DestDll,
    [property: JsonPropertyName("matched_name")] string MatchedName,
    [property: JsonPropertyName("score")] double Score);

public sealed record FfbPluginApplyResult(
    [property: JsonPropertyName("deployed_games")] IReadOnlyList<FfbPluginDeployedGame> DeployedGames,
    [property: JsonPropertyName("skipped_native_count")] int SkippedNativeCount,
    [property: JsonPropertyName("skipped_collision_count")] int SkippedCollisionCount,
    [property: JsonPropertyName("skipped_no_match_count")] int SkippedNoMatchCount,
    [property: JsonPropertyName("skipped_dll_missing_count")] int SkippedDllMissingCount,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles,
    [property: JsonPropertyName("cache_path")] string? CachePath);

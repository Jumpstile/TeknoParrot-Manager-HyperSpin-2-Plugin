using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// New ROADMAP phase, added after a direct user request (not part of the
// original phase list researched for v0.1.0-v0.14.0). Ports the original
// PowerShell tool's AutoSync feature (Invoke-AutoSync): extracts game ZIPs
// from a configured source folder (a NAS share, a local staging drive,
// etc.) into the games install folder, skipping games already extracted
// and up to date via a persisted sync-state file (NAS ZIP size + mtime per
// game). Optionally supports a second "supplementary" source folder,
// synced the same way against the same install folder and sync-state file.
//
// Unrelated to this plugin's own pre-existing AutoSyncOnDbConnect setting
// (which auto-triggers the HyperHQ library sync action on a DB-connect
// event) -- different feature, same word.
//
// Group A: the ZIP source is a folder the user points this plugin at
// themselves, same trust tier as GamesRootPath/ReShadeSourceDllPath -- not
// something this plugin fetches from the internet. No new permission.
public static partial class TeknoParrotProfileScanner
{
    private const string SyncStateFileName = "TeknoParrot-Manager.syncstate.json";
    private static readonly Regex StagingSuffixPattern = new(@"\.(teknoparrot|parrot|game)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StagingSpacingPattern = new(@" (?=[\[\(])", RegexOptions.Compiled);

    // Normalizes a staging-folder name so "Game (ver) [Platform] [TP]"
    // (old convention) and "Game(ver)[Platform][TP]" (new convention) map
    // to the same key, and a ".teknoparrot"/".parrot"/".game" suffix
    // doesn't prevent matching. Mirrors Get-StagingFolderMap's per-name
    // normalization.
    internal static string NormalizeStagingFolderName(string folderName)
    {
        var withoutSuffix = StagingSuffixPattern.Replace(folderName, string.Empty);
        return StagingSpacingPattern.Replace(withoutSuffix, string.Empty);
    }

    private static Dictionary<string, string> BuildStagingFolderMap(string installFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(installFolder))
        {
            return map;
        }

        foreach (var dir in Directory.GetDirectories(installFolder))
        {
            var normalized = NormalizeStagingFolderName(Path.GetFileName(dir));
            if (!map.ContainsKey(normalized))
            {
                map[normalized] = dir;
            }
        }

        return map;
    }

    // Resolves a ZIP's real extraction folder via the registered game's
    // own GamePath, when the folder-name map above can't find it (e.g. a
    // hand-renamed folder). Mirrors Resolve-RegisteredGameFolder: dat
    // lookup -> ProfileCode (validated, since the dat is externally
    // sourced/untrusted input, same discipline as RegisterGames already
    // applies to dat-sourced ProfileCodes) -> UserProfiles\<code>.xml ->
    // GamePath -> containing folder. Returns null at any missing step.
    internal static string? ResolveRegisteredGameFolder(
        string rawZipBaseName, IReadOnlyDictionary<string, TeknoParrotDatEntry> datIndex, string userProfilesDir)
    {
        if (datIndex.Count == 0 || string.IsNullOrWhiteSpace(userProfilesDir))
        {
            return null;
        }

        if (!datIndex.TryGetValue(NormalizeGameKey(rawZipBaseName), out var datEntry) || string.IsNullOrWhiteSpace(datEntry.ProfileCode))
        {
            return null;
        }

        if (!IsSafeProfileCode(datEntry.ProfileCode))
        {
            return null;
        }

        var profilePath = Path.Combine(userProfilesDir, $"{datEntry.ProfileCode}.xml");
        if (!File.Exists(profilePath))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Load(profilePath);
            var gamePath = ChildByLocalName(doc.Root, "GamePath")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
            {
                return null;
            }

            return Path.GetDirectoryName(gamePath);
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, AutoSyncStateEntry> LoadSyncState(string syncStatePath)
    {
        if (!File.Exists(syncStatePath))
        {
            return new Dictionary<string, AutoSyncStateEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(syncStatePath);
            return JsonSerializer.Deserialize<Dictionary<string, AutoSyncStateEntry>>(json)
                ?? new Dictionary<string, AutoSyncStateEntry>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new Dictionary<string, AutoSyncStateEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveSyncState(string syncStatePath, Dictionary<string, AutoSyncStateEntry> state)
    {
        var json = JsonSerializer.Serialize(state);
        var tempPath = syncStatePath + ".tmp";
        File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
        if (File.Exists(syncStatePath))
        {
            File.Delete(syncStatePath);
        }
        File.Move(tempPath, syncStatePath);
    }

    // Extracts game ZIPs from zipSourceDir into installFolder, skipping
    // games already extracted and up to date. Mirrors Invoke-AutoSync.
    // Never deletes a local game folder unless it is about to immediately
    // re-extract it from the matching ZIP -- nothing is ever removed
    // without also being replaced in the same pass.
    public static AutoSyncResult RunAutoSync(
        string zipSourceDir, string installFolder, string syncStatePath,
        IReadOnlyCollection<string>? skipCodes, IReadOnlyCollection<string>? onlyCodes, bool dryRun,
        IReadOnlyDictionary<string, TeknoParrotDatEntry>? datIndex, string userProfilesDir, Action<string>? log = null)
    {
        var synced = new List<string>();
        var failed = new List<string>();
        var upToDate = 0;
        var skipped = 0;
        var wouldSync = 0;

        if (!Directory.Exists(zipSourceDir))
        {
            return new AutoSyncResult(synced, failed, upToDate, skipped, wouldSync, "The configured source folder does not exist.");
        }

        var zipFiles = Directory.GetFiles(zipSourceDir, "*.zip", SearchOption.TopDirectoryOnly);
        if (zipFiles.Length == 0)
        {
            var subdirHint = Directory.GetDirectories(zipSourceDir)
                .Select(dir => (Dir: dir, Count: Directory.GetFiles(dir, "*.zip", SearchOption.TopDirectoryOnly).Length))
                .Where(x => x.Count > 0)
                .ToList();
            var tip = subdirHint.Count > 0
                ? $"No ZIPs found directly in the source folder, but found ZIPs one level down in: {string.Join(", ", subdirHint.Select(x => x.Dir))}. Point the source path at one of these directly."
                : "No ZIP files found in the source folder.";
            return new AutoSyncResult(synced, failed, upToDate, skipped, wouldSync, tip);
        }

        var syncState = LoadSyncState(syncStatePath);
        var stagingMap = BuildStagingFolderMap(installFolder);
        var effectiveDatIndex = datIndex ?? new Dictionary<string, TeknoParrotDatEntry>(StringComparer.OrdinalIgnoreCase);
        var skipSet = skipCodes is null ? null : new HashSet<string>(skipCodes, StringComparer.OrdinalIgnoreCase);
        var onlySet = onlyCodes is null ? null : new HashSet<string>(onlyCodes, StringComparer.OrdinalIgnoreCase);

        foreach (var zipPath in zipFiles)
        {
            var rawName = Path.GetFileNameWithoutExtension(zipPath);

            if (skipSet is not null && skipSet.Contains(rawName))
            {
                skipped++;
                continue;
            }

            if (rawName.StartsWith("!TeknoParrot Collection", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            if (onlySet is not null && onlySet.Count > 0 && !onlySet.Contains(rawName))
            {
                skipped++;
                continue;
            }

            var extractDir = Path.Combine(installFolder, rawName);
            if (!IsPathInside(extractDir, installFolder))
            {
                log?.Invoke($"AutoSync: SECURITY -- skipped '{rawName}', resolved extract path is outside the install folder.");
                skipped++;
                continue;
            }

            var sentinel = extractDir + ".extracting";
            var zipInfo = new FileInfo(zipPath);
            var nasModified = zipInfo.LastWriteTimeUtc.ToString("o");
            var stored = syncState.TryGetValue(rawName, out var existingEntry) ? existingEntry : null;

            var normalizedZipName = NormalizeStagingFolderName(rawName);
            var matchedFolder = stagingMap.TryGetValue(normalizedZipName, out var foundFolder) ? foundFolder : null;
            matchedFolder ??= ResolveRegisteredGameFolder(rawName, effectiveDatIndex, userProfilesDir);

            var needsSync = false;
            if (stored is null)
            {
                var hasContent = matchedFolder is not null && !File.Exists(sentinel) &&
                                  Directory.Exists(matchedFolder) && Directory.EnumerateFileSystemEntries(matchedFolder).Any();
                if (hasContent)
                {
                    syncState[rawName] = new AutoSyncStateEntry(zipInfo.Length, nasModified, matchedFolder!, DateTime.UtcNow.ToString("o"));
                    upToDate++;
                    continue;
                }

                needsSync = true;
            }
            else if (stored.NasSize != zipInfo.Length || stored.NasLastModified != nasModified)
            {
                needsSync = true;
            }
            else if (!((!string.IsNullOrWhiteSpace(stored.LocalPath) && Directory.Exists(stored.LocalPath)) || Directory.Exists(extractDir)))
            {
                if (matchedFolder is not null && Directory.Exists(matchedFolder))
                {
                    syncState[rawName] = stored with { LocalPath = matchedFolder };
                }
                else
                {
                    needsSync = true;
                }
            }
            else if (File.Exists(sentinel))
            {
                needsSync = true;
            }

            if (!needsSync)
            {
                upToDate++;
                continue;
            }

            if (dryRun)
            {
                wouldSync++;
                continue;
            }

            try
            {
                File.WriteAllText(sentinel, string.Empty, new System.Text.UTF8Encoding(false));
                try
                {
                    if (Directory.Exists(extractDir))
                    {
                        Directory.Delete(extractDir, recursive: true);
                    }

                    ExtractZipSafe(zipPath, extractDir);
                    syncState[rawName] = new AutoSyncStateEntry(zipInfo.Length, nasModified, extractDir, DateTime.UtcNow.ToString("o"));
                    synced.Add(rawName);
                    log?.Invoke($"AutoSync: extracted {rawName} -> {extractDir}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    syncState.Remove(rawName);
                    try
                    {
                        if (Directory.Exists(extractDir))
                        {
                            Directory.Delete(extractDir, recursive: true);
                        }
                    }
                    catch (IOException) { /* best-effort cleanup of a partial extraction */ }

                    failed.Add(rawName);
                    log?.Invoke($"AutoSync: FAILED {rawName} -- {ex.Message}");
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(sentinel))
                    {
                        File.Delete(sentinel);
                    }
                }
                catch (IOException) { /* best-effort cleanup */ }
            }
        }

        if (!dryRun)
        {
            try
            {
                SaveSyncState(syncStatePath, syncState);
            }
            catch (IOException ex)
            {
                log?.Invoke($"AutoSync: WARNING -- could not save sync state: {ex.Message}");
            }
        }

        return new AutoSyncResult(synced, failed, upToDate, skipped, wouldSync, null);
    }

    // Runs the main source always, then the supplementary source (if
    // configured) against the same install folder and the same sync-state
    // file -- a game synced from either source is tracked identically, so
    // running both never double-extracts the same game.
    public static AutoSyncCombinedResult RunAutoSyncBothSources(
        TeknoParrotSettings settings, IReadOnlyCollection<string>? skipCodes,
        IReadOnlyCollection<string>? onlyCodes, IReadOnlyCollection<string>? onlyCodesSupplementary, bool dryRun,
        IReadOnlyDictionary<string, TeknoParrotDatEntry>? datIndex, string userProfilesDir, Action<string>? log = null)
    {
        var installFolder = ResolveGamesRootPath(settings);
        var syncStatePath = Path.Combine(installFolder, SyncStateFileName);

        var main = RunAutoSync(settings.RomZipSourcePath, installFolder, syncStatePath, skipCodes, onlyCodes, dryRun, datIndex, userProfilesDir, log);

        AutoSyncResult? supplementary = null;
        if (!string.IsNullOrWhiteSpace(settings.RomZipSourceSupplementaryPath) && Directory.Exists(settings.RomZipSourceSupplementaryPath))
        {
            supplementary = RunAutoSync(settings.RomZipSourceSupplementaryPath, installFolder, syncStatePath, skipCodes, onlyCodesSupplementary, dryRun, datIndex, userProfilesDir, log);
        }

        return new AutoSyncCombinedResult(main, supplementary);
    }
}

internal sealed record AutoSyncStateEntry(long NasSize, string NasLastModified, string LocalPath, string SyncedAt);

public sealed record AutoSyncResult(
    [property: JsonPropertyName("synced_games")] IReadOnlyList<string> SyncedGames,
    [property: JsonPropertyName("failed_games")] IReadOnlyList<string> FailedGames,
    [property: JsonPropertyName("up_to_date_count")] int UpToDateCount,
    [property: JsonPropertyName("skipped_count")] int SkippedCount,
    [property: JsonPropertyName("would_sync_count")] int WouldSyncCount,
    [property: JsonPropertyName("note")] string? Note);

public sealed record AutoSyncCombinedResult(
    [property: JsonPropertyName("main")] AutoSyncResult Main,
    [property: JsonPropertyName("supplementary")] AutoSyncResult? Supplementary);

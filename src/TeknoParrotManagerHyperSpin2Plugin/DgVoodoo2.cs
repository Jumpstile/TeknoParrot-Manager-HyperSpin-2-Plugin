using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 5 of ROADMAP.md: ports Invoke-DgVoodoo2Setup, Get-GameLegacyApi,
// and Test-DgVoodoo2UpToDate from the original PowerShell tool. The DLLs
// are user-supplied via a settings path (dgVoodoo2SourcePath, same "user
// already has it" pattern as crosshairsPath/reShadeSourceDllPath) -- this
// plugin does not download dgVoodoo2. Zero network calls anywhere in this
// phase, so no new plugin.json permission is needed.
public static partial class TeknoParrotProfileScanner
{
    internal static readonly string[] AllDgVoodoo2Dlls = { "D3D8.dll", "DDraw.dll", "D3DImm.dll", "Glide2x.dll", "Glide3x.dll" };

    // Scans the first 2 MB of a game exe for legacy graphics API imports.
    // Returns a subset of "D3D8", "DDraw", "Glide2x", "Glide3x" -- empty if
    // nothing recognized was found or the file couldn't be read. Mirrors
    // Get-GameLegacyApi.
    public static List<string> GetGameLegacyApi(string exePath)
    {
        var found = new List<string>();
        var text = ReadAsciiPrefix(exePath);
        if (text is null)
        {
            return found;
        }

        if (text.Contains("d3d8.dll", StringComparison.OrdinalIgnoreCase)) found.Add("D3D8");
        if (text.Contains("ddraw.dll", StringComparison.OrdinalIgnoreCase)) found.Add("DDraw");
        if (text.Contains("glide2x.dll", StringComparison.OrdinalIgnoreCase)) found.Add("Glide2x");
        if (text.Contains("glide3x.dll", StringComparison.OrdinalIgnoreCase) || text.Contains("glide.dll", StringComparison.OrdinalIgnoreCase)) found.Add("Glide3x");
        return found;
    }

    // Pure, read-only check: given the legacy APIs an exe imports (from
    // GetGameLegacyApi) and its folder, decides whether dgVoodoo2 is
    // already deployed there. Mirrors Test-DgVoodoo2UpToDate.
    public static DgVoodoo2Evaluation EvaluateDgVoodoo2(IReadOnlyCollection<string> apis, string exeDir)
    {
        if (apis.Count == 0)
        {
            return new DgVoodoo2Evaluation(false, true);
        }

        var requiredDlls = new List<string>();
        if (apis.Contains("D3D8")) requiredDlls.Add("D3D8.dll");
        if (apis.Contains("DDraw")) requiredDlls.Add("DDraw.dll");
        if (apis.Contains("Glide2x")) requiredDlls.Add("Glide2x.dll");
        if (apis.Contains("Glide3x")) requiredDlls.Add("Glide3x.dll");

        var upToDate = requiredDlls.All(dll => File.Exists(Path.Combine(exeDir, dll)));
        return new DgVoodoo2Evaluation(true, upToDate);
    }

    // Maps detected legacy APIs to the dgVoodoo2 DLL(s) each one needs,
    // restricted to whichever DLLs actually exist in the source folder.
    private static List<string> MapApisToAvailableDlls(IReadOnlyCollection<string> apis, IReadOnlyCollection<string> available)
    {
        var toDeploy = new List<string>();
        if (apis.Contains("D3D8")) toDeploy.AddRange(available.Where(dll => dll is "D3D8.dll" or "D3DImm.dll"));
        if (apis.Contains("DDraw")) toDeploy.AddRange(available.Where(dll => dll is "DDraw.dll" or "D3DImm.dll"));
        if (apis.Contains("Glide2x")) toDeploy.AddRange(available.Where(dll => dll == "Glide2x.dll"));
        if (apis.Contains("Glide3x")) toDeploy.AddRange(available.Where(dll => dll == "Glide3x.dll"));
        return toDeploy.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Deploys (or, with dryRun, just previews) dgVoodoo2 into selected
    // registered games' folders, deploying only the DLL(s) each game's
    // detected legacy API actually needs (falling back to every available
    // DLL if none of the needed ones are present, or if the game was
    // explicitly named in gameCodes despite no detected need). Existing
    // deployed DLLs are never overwritten; a per-game config in
    // dgVoodoo2PresetsPath always overwrites the destination, the global
    // dgVoodoo.conf never does. gameCodes null means "every game with a
    // detected legacy API" -- mirrors selection mode "A" in the original
    // script. An explicit gameCodes list mirrors mode "M" (manual pick),
    // including its "deploy everything if nothing detected" fallback for
    // a manually-named game. Mirrors Invoke-DgVoodoo2Setup.
    public static DgVoodoo2SetupResult ApplyDgVoodoo2Setup(TeknoParrotSettings settings, IReadOnlyCollection<string>? gameCodes, bool dryRun, Action<string>? log = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var sourceDir = settings.DgVoodoo2SourcePath;

        var deployed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var presetOverrides = 0;

        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
        {
            return new DgVoodoo2SetupResult(deployed, skipped, errors, presetOverrides, Array.Empty<string>());
        }

        var available = AllDgVoodoo2Dlls.Where(dll => File.Exists(Path.Combine(sourceDir, dll))).ToList();
        if (available.Count == 0)
        {
            log?.Invoke($"dgVoodoo2: no DLLs found in '{sourceDir}'. Expected one or more of: {string.Join(", ", AllDgVoodoo2Dlls)}.");
            return new DgVoodoo2SetupResult(deployed, skipped, errors, presetOverrides, available);
        }

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new DgVoodoo2SetupResult(deployed, skipped, errors, presetOverrides, available);
        }

        var presetsDir = !string.IsNullOrWhiteSpace(settings.DgVoodoo2PresetsPath)
            ? settings.DgVoodoo2PresetsPath
            : Path.Combine(AppContext.BaseDirectory, "dgVoodoo2Presets");
        var hasGlobalConf = File.Exists(Path.Combine(sourceDir, "dgVoodoo.conf"));

        var explicitSelection = gameCodes is { Count: > 0 };
        var codeFilter = explicitSelection ? new HashSet<string>(gameCodes!, StringComparer.OrdinalIgnoreCase) : null;

        foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var code = Path.GetFileNameWithoutExtension(file);

            try
            {
                var doc = XDocument.Load(file);
                var gamePath = ChildByLocalName(doc.Root, "GamePath")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
                {
                    // Default selection (no explicit gameCodes) never even
                    // considers a game with no valid path, same as the
                    // original script's detection scan -- only an
                    // explicitly-named game's broken path is worth
                    // reporting as skipped.
                    if (codeFilter is not null && codeFilter.Contains(code))
                    {
                        skipped.Add(code);
                    }
                    continue;
                }

                var apis = GetGameLegacyApi(gamePath);

                if (codeFilter is not null)
                {
                    if (!codeFilter.Contains(code))
                    {
                        continue;
                    }
                }
                else if (apis.Count == 0)
                {
                    // Default selection (no explicit gameCodes): only
                    // auto-detected games, mirroring selection mode "A".
                    continue;
                }

                var exeDir = Path.GetDirectoryName(gamePath);
                if (string.IsNullOrWhiteSpace(exeDir))
                {
                    skipped.Add(code);
                    continue;
                }

                List<string> toDeploy;
                if (apis.Count == 0)
                {
                    // Explicitly named despite no detected need: give the
                    // benefit of the doubt, same as a manual pick in the
                    // original script.
                    toDeploy = available;
                }
                else
                {
                    toDeploy = MapApisToAvailableDlls(apis, available);
                    if (toDeploy.Count == 0)
                    {
                        log?.Invoke($"dgVoodoo2: {code} -- detected [{string.Join(", ", apis)}] but none of those DLLs are in the source folder; deploying all available.");
                        toDeploy = available;
                    }
                }

                if (!dryRun)
                {
                    Directory.CreateDirectory(exeDir);
                    foreach (var dllName in toDeploy)
                    {
                        var destDll = Path.Combine(exeDir, dllName);
                        if (!File.Exists(destDll))
                        {
                            File.Copy(Path.Combine(sourceDir, dllName), destDll);
                        }
                    }
                }

                var perGameConf = Path.Combine(presetsDir, code + ".conf");
                var isPerGameConf = File.Exists(perGameConf);
                if (!dryRun)
                {
                    if (isPerGameConf)
                    {
                        File.Copy(perGameConf, Path.Combine(exeDir, "dgVoodoo.conf"), overwrite: true);
                    }
                    else if (hasGlobalConf)
                    {
                        var destConf = Path.Combine(exeDir, "dgVoodoo.conf");
                        if (!File.Exists(destConf))
                        {
                            File.Copy(Path.Combine(sourceDir, "dgVoodoo.conf"), destConf);
                        }
                    }
                }

                if (isPerGameConf)
                {
                    presetOverrides++;
                }

                log?.Invoke($"dgVoodoo2: deployed {string.Join(", ", toDeploy)} to {exeDir}" + (isPerGameConf ? " (config: per-game)" : ""));
                deployed.Add(code);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                log?.Invoke($"dgVoodoo2: error on {code} -- {ex.Message}");
                errors.Add(code);
            }
        }

        return new DgVoodoo2SetupResult(deployed, skipped, errors, presetOverrides, available);
    }
}

public sealed record DgVoodoo2Evaluation(bool Eligible, bool UpToDate);

public sealed record DgVoodoo2SetupResult(
    [property: JsonPropertyName("deployed_profiles")] IReadOnlyList<string> DeployedProfiles,
    [property: JsonPropertyName("skipped_profiles")] IReadOnlyList<string> SkippedProfiles,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles,
    [property: JsonPropertyName("preset_overrides")] int PresetOverrides,
    [property: JsonPropertyName("available_dlls")] IReadOnlyList<string> AvailableDlls);

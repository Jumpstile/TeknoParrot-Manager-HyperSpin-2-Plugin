using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phases 2 and 3 of ROADMAP.md: ports Invoke-CrosshairSetup, Export-CrosshairPreview,
// and Invoke-CursorHideSetup from the original PowerShell tool. The PCSX2x6 path
// also follows the RC3 ownership boundary: emulator-owned cursor settings are
// read-only, while TPM-owned crosshair PNGs are written under the emulator's
// resolved data root.
// The 321 curated crosshair PNGs from the original tool are bundled in the
// release package's Crosshairs/ folder next to the executable; the optional
// crosshairsPath setting overrides this with a different folder if the user
// wants their own set instead.
public static partial class TeknoParrotProfileScanner
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    // Resolves the folder crosshair operations should scan: the explicit
    // crosshairsPath setting if set, otherwise the bundled Crosshairs folder
    // shipped next to this executable.
    private static string ResolveCrosshairsPath(TeknoParrotSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.CrosshairsPath)
            ? settings.CrosshairsPath
            : Path.Combine(AppContext.BaseDirectory, "Crosshairs");

    public static bool IsValidPng(string path)
    {
        try
        {
            var header = new byte[8];
            using var stream = File.OpenRead(path);
            var read = 0;
            while (read < 8)
            {
                var n = stream.Read(header, read, 8 - read);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            return header.AsSpan().SequenceEqual(PngSignature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Writes a self-contained HTML grid preview of all crosshairs. Images are
    // referenced as relative paths (just the filename, since the preview is
    // written inside crosshairsPath itself) so the file works anywhere on the
    // same machine without embedding base64.
    private static string BuildCrosshairPreviewHtml(IReadOnlyList<string> crosshairPaths)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<title>TeknoParrot Crosshairs</title><style>");
        sb.Append("body{background:#111;color:#eee;font-family:monospace;padding:16px;margin:0}");
        sb.Append("h1{color:#4af;margin-bottom:4px}p{color:#888;margin-top:0;margin-bottom:16px}");
        sb.Append(".grid{display:flex;flex-wrap:wrap;gap:8px}");
        sb.Append(".cell{background:#222;border:1px solid #333;padding:6px;text-align:center;width:84px}");
        sb.Append(".cell:hover{border-color:#4af;background:#1a2a3a}");
        sb.Append(".cell img{width:64px;height:64px;image-rendering:pixelated;display:block;margin:0 auto 4px}");
        sb.Append(".num{color:#4af;font-size:12px}.name{color:#888;font-size:10px;word-break:break-all}");
        sb.Append("</style></head><body>");
        sb.Append("<h1>TeknoParrot Crosshairs</h1>");
        sb.Append("<p>Browse below, then pass the <span style=\"color:#4af\">file name</span> as p1Name/p2Name to deploy_crosshairs.</p>");
        sb.Append("<div class=\"grid\">");

        for (var i = 0; i < crosshairPaths.Count; i++)
        {
            var fileName = Path.GetFileName(crosshairPaths[i]);
            var stem = Path.GetFileNameWithoutExtension(crosshairPaths[i]);
            var rel = WebUtility.HtmlEncode(fileName);
            var stemHtml = WebUtility.HtmlEncode(stem);
            sb.Append($"<div class=\"cell\"><img src=\"{rel}\" alt=\"{stemHtml}\">");
            sb.Append($"<div class=\"num\">{i}</div><div class=\"name\">{stemHtml}</div></div>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    // Scans crosshairsPath for valid PNGs and writes an HTML preview grid next
    // to them. Read-only aside from writing the preview file itself.
    public static CrosshairPreviewResult PreviewCrosshairs(TeknoParrotSettings settings)
    {
        var crosshairsPath = ResolveCrosshairsPath(settings);
        if (!Directory.Exists(crosshairsPath))
        {
            return new CrosshairPreviewResult(false,
                new[] { $"Crosshairs folder was not found: {crosshairsPath}. Set crosshairsPath to use a different folder." },
                Array.Empty<string>(), Array.Empty<string>(), null);
        }

        var valid = new List<string>();
        var invalid = new List<string>();
        foreach (var path in Directory.EnumerateFiles(crosshairsPath, "*.png", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (IsValidPng(path))
            {
                valid.Add(path);
            }
            else
            {
                invalid.Add(Path.GetFileName(path));
            }
        }

        if (valid.Count == 0)
        {
            return new CrosshairPreviewResult(false,
                new[] { $"No valid PNG crosshairs found in: {crosshairsPath}" },
                Array.Empty<string>(), invalid, null);
        }

        var previewPath = Path.Combine(crosshairsPath, "TeknoParrot-Crosshairs-Preview.html");
        File.WriteAllText(previewPath, BuildCrosshairPreviewHtml(valid), new UTF8Encoding(false));

        return new CrosshairPreviewResult(true, Array.Empty<string>(),
            valid.Select(Path.GetFileNameWithoutExtension).Select(n => n!).ToArray(), invalid, previewPath);
    }

    private static readonly string[] ElfLdr2FolderCandidates = { "ElfLdr2", "ElfLoader2", "elf", "elf2", "ElfLdr" };
    private static readonly string[] Pcsx2FolderCandidates = { "pcsx2x6", "PCSX2x6", "pcsx2", "PCSX2" };

    private static string? FindFolder(string root, IReadOnlyList<string> candidates, Func<string, bool> fallbackMatch)
    {
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(root, candidate);
            if (Directory.Exists(path))
            {
                return path;
            }
        }

        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(dir => fallbackMatch(Path.GetFileName(dir)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // RC3's PCSX2x6 contract makes the emulator executable the presence
    // detector, resolves its data root from portable.txt, and rejects any
    // configured root that escapes the emulator folder. A missing or invalid
    // portable.txt is therefore never allowed to redirect a write elsewhere.
    private static string? ResolvePcsx2DataRoot(string pcsx2Dir, Action<string>? log)
    {
        try
        {
            var executablePath = Path.Combine(pcsx2Dir, "pcsx2-qtx64.exe");
            if (!File.Exists(executablePath))
            {
                log?.Invoke($"Crosshairs: Pcsx2x6 executable not found at {executablePath}");
                return null;
            }

            var dataRootName = "TeknoParrot";
            var portablePath = Path.Combine(pcsx2Dir, "portable.txt");
            if (File.Exists(portablePath))
            {
                var configuredRoot = File.ReadAllText(portablePath).Trim();
                if (!string.IsNullOrWhiteSpace(configuredRoot))
                {
                    dataRootName = configuredRoot;
                }
            }

            if (Path.IsPathRooted(dataRootName))
            {
                log?.Invoke($"Crosshairs: rejected rooted PCSX2 data root in {portablePath}");
                return null;
            }

            var emulatorRoot = Path.GetFullPath(pcsx2Dir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolvedRoot = Path.GetFullPath(Path.Combine(emulatorRoot, dataRootName));
            var emulatorPrefix = emulatorRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(resolvedRoot, emulatorRoot, StringComparison.OrdinalIgnoreCase) &&
                !resolvedRoot.StartsWith(emulatorPrefix, StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"Crosshairs: rejected PCSX2 data root outside {emulatorRoot}: {dataRootName}");
                return null;
            }

            return resolvedRoot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log?.Invoke($"Crosshairs: could not resolve Pcsx2x6 data root -- {ex.Message}");
            return null;
        }
    }

    // The emulator owns PCSX2.ini. The plugin only needs to know that the
    // first-run configuration has completed before placing TPM-owned PNGs.
    // Missing or partial section markers are treated as unknown and fail
    // closed; this plugin does not launch the emulator's first-run wizard.
    private static bool IsPcsx2Initialized(string dataRoot, Action<string>? log)
    {
        var iniPath = Path.Combine(dataRoot, "inis", "PCSX2.ini");
        if (!File.Exists(iniPath))
        {
            log?.Invoke($"Crosshairs: Pcsx2x6 is not initialized; PCSX2.ini was not found at {iniPath}");
            return false;
        }

        try
        {
            var sections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(iniPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
                {
                    sections.Add(trimmed[1..^1].Trim());
                }
            }

            var missing = new[] { "USB1", "USB2", "JVS" }
                .Where(section => !sections.Contains(section))
                .ToArray();
            if (missing.Length > 0)
            {
                log?.Invoke($"Crosshairs: Pcsx2x6 is not initialized; PCSX2.ini is missing sections: {string.Join(", ", missing)}");
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"Crosshairs: could not inspect PCSX2.ini -- {ex.Message}");
            return false;
        }
    }

    // Deploys p1Name/p2Name (matched by filename stem, case-insensitive,
    // against crosshairsPath) to every registered lightgun (GunGame=true)
    // profile's game folder. ElfLdr2 and Pcsx2x6 lightgun games share one
    // emulator folder each and are deployed to once regardless of how many
    // profiles use that emulator; Pcsx2x6 writes only TPM-owned PNGs beneath
    // its resolved data root after verifying initialization. Optionally also hides the Windows cursor for every
    // lightgun profile (see HideCursorForLightgunGames).
    public static CrosshairDeploymentResult DeployCrosshairs(TeknoParrotSettings settings, string p1Name, string p2Name, bool hideCursor, bool dryRun, Action<string>? log = null)
    {
        var crosshairsPath = ResolveCrosshairsPath(settings);
        if (!Directory.Exists(crosshairsPath))
        {
            return new CrosshairDeploymentResult(false,
                new[] { $"Crosshairs folder was not found: {crosshairsPath}. Set crosshairsPath to use a different folder." },
                0, 0, 0, null);
        }

        var pngs = Directory.EnumerateFiles(crosshairsPath, "*.png", SearchOption.TopDirectoryOnly)
            .Where(IsValidPng)
            .ToList();
        var p1Path = pngs.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), p1Name, StringComparison.OrdinalIgnoreCase));
        var p2Path = pngs.FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p), p2Name, StringComparison.OrdinalIgnoreCase));
        if (p1Path is null || p2Path is null)
        {
            var missing = string.Join(", ", new[] { p1Path is null ? p1Name : null, p2Path is null ? p2Name : null }.Where(n => n is not null));
            return new CrosshairDeploymentResult(false, new[] { $"Crosshair not found: {missing}" }, 0, 0, 0, null);
        }

        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new CrosshairDeploymentResult(false,
                new[] { "UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath." },
                0, 0, 0, null);
        }

        string? elfDir = null;
        string? pcsx2Dir = null;
        var elfDeployed = false;
        var pcsx2Deployed = false;
        var deployed = 0;
        var skipped = 0;
        var errors = 0;

        foreach (var profilePath in Directory.EnumerateFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = XDocument.Load(profilePath);
                if (document.Root is null || FirstElementValue(document, "GunGame") != "true")
                {
                    continue;
                }

                var emulatorType = FirstElementValue(document, "EmulatorType");

                if (emulatorType == "ElfLdr2")
                {
                    if (!elfDeployed)
                    {
                        elfDir ??= FindFolder(rootPath, ElfLdr2FolderCandidates, name => name.Contains("elf", StringComparison.OrdinalIgnoreCase));
                        var dest = elfDir ?? rootPath;
                        if (!dryRun)
                        {
                            File.Copy(p1Path, Path.Combine(dest, "P1.png"), overwrite: true);
                            File.Copy(p2Path, Path.Combine(dest, "P2.png"), overwrite: true);
                        }
                        log?.Invoke($"Crosshairs: deployed to ElfLdr2 folder {dest}");
                        elfDeployed = true;
                    }

                    deployed++;
                    continue;
                }

                if (emulatorType == "Pcsx2x6")
                {
                    if (!pcsx2Deployed)
                    {
                        pcsx2Dir ??= FindFolder(rootPath, Pcsx2FolderCandidates, name => name.StartsWith("pcsx2", StringComparison.OrdinalIgnoreCase));
                        if (pcsx2Dir is not null)
                        {
                            var dataRoot = ResolvePcsx2DataRoot(pcsx2Dir, log);
                            if (dataRoot is not null && IsPcsx2Initialized(dataRoot, log))
                            {
                                var crosshairsRoot = Path.Combine(dataRoot, "crosshairs");
                                var p1Dest = Path.Combine(crosshairsRoot, "P1.png");
                                var p2Dest = Path.Combine(crosshairsRoot, "P2.png");
                                if (!dryRun)
                                {
                                    Directory.CreateDirectory(crosshairsRoot);
                                    File.Copy(p1Path, p1Dest, overwrite: true);
                                    File.Copy(p2Path, p2Dest, overwrite: true);
                                }

                                log?.Invoke($"Crosshairs: deployed Pcsx2x6 crosshairs to {crosshairsRoot}");
                                deployed++;
                            }
                            else
                            {
                                skipped++;
                            }
                        }
                        else
                        {
                            log?.Invoke($"Crosshairs: Pcsx2x6 folder not found in {rootPath}");
                            skipped++;
                        }

                        pcsx2Deployed = true;
                    }
                    continue;
                }

                // Standard game: copy next to the game's own executable.
                var gamePath = FirstElementValue(document, "GamePath");
                var exeDir = string.IsNullOrWhiteSpace(gamePath) ? null : Path.GetDirectoryName(gamePath.Trim());
                if (string.IsNullOrWhiteSpace(exeDir) || !Directory.Exists(exeDir))
                {
                    skipped++;
                    continue;
                }

                if (!dryRun)
                {
                    File.Copy(p1Path, Path.Combine(exeDir, "P1.png"), overwrite: true);
                    File.Copy(p2Path, Path.Combine(exeDir, "P2.png"), overwrite: true);
                }

                log?.Invoke($"Crosshairs: deployed {Path.GetFileNameWithoutExtension(profilePath)} -> {exeDir}");
                deployed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                log?.Invoke($"Crosshairs: error on {Path.GetFileNameWithoutExtension(profilePath)} -- {ex.Message}");
                errors++;
            }
        }

        CursorHideResult? cursorHide = null;
        if (hideCursor)
        {
            cursorHide = HideCursorForLightgunGames(settings, dryRun);
        }

        return new CrosshairDeploymentResult(true, Array.Empty<string>(), deployed, skipped, errors, cursorHide);
    }

    private static readonly string[] CursorHideFieldNames = { "HideCursor", "Hide Cursor", "DisableCursor" };

    // Sets the cursor-hide field to 1 in every registered lightgun
    // UserProfile, skipping profiles with no such field or that are already
    // set. Backing up the whole UserProfiles folder before any mutation is
    // the caller's responsibility (matches the existing register/repair/
    // propagate-controls pattern), not done here.
    public static CursorHideResult HideCursorForLightgunGames(TeknoParrotSettings settings, bool dryRun)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new CursorHideResult(false,
                new[] { "UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath." },
                0, 0, 0, 0);
        }

        var updated = 0;
        var alreadySet = 0;
        var noField = 0;
        var errors = 0;

        foreach (var profilePath in Directory.EnumerateFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var document = XDocument.Load(profilePath);
                if (document.Root is null || FirstElementValue(document, "GunGame") != "true")
                {
                    continue;
                }

                var changed = false;
                var wasSet = false;
                foreach (var fieldName in CursorHideFieldNames)
                {
                    var fieldValue = ChildByLocalName(FindFieldInformation(document, fieldName), "FieldValue");
                    if (fieldValue is null)
                    {
                        continue;
                    }

                    if (fieldValue.Value == "1")
                    {
                        wasSet = true;
                        continue;
                    }

                    fieldValue.Value = "1";
                    changed = true;
                }

                if (changed)
                {
                    if (!dryRun)
                    {
                        SaveProfileDocument(document, profilePath);
                    }

                    updated++;
                }
                else if (wasSet)
                {
                    alreadySet++;
                }
                else
                {
                    noField++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                errors++;
            }
        }

        return new CursorHideResult(true, Array.Empty<string>(), updated, alreadySet, noField, errors);
    }
}

public sealed record CrosshairPreviewResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("valid")] IReadOnlyList<string> Valid,
    [property: JsonPropertyName("invalid")] IReadOnlyList<string> Invalid,
    [property: JsonPropertyName("preview_path")] string? PreviewPath);

public sealed record CrosshairDeploymentResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("deployed")] int Deployed,
    [property: JsonPropertyName("skipped")] int Skipped,
    [property: JsonPropertyName("error_count")] int ErrorCount,
    [property: JsonPropertyName("cursor_hide")] CursorHideResult? CursorHide);

public sealed record CursorHideResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("already_set")] int AlreadySet,
    [property: JsonPropertyName("no_field")] int NoField,
    [property: JsonPropertyName("error_count")] int ErrorCount);

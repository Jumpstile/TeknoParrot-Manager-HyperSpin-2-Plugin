using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AuthenticodeExaminer;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 4 of ROADMAP.md: ports Invoke-ReShadeSetup, Test-ReShadeDllSignature,
// Get-ReShadeLatestVersion, Get-ReShadeTargetInfo, Get-GameApiDll, and
// Get-ExeArchitecture from the original PowerShell tool. The user supplies
// their own ReShade DLL(s) via a settings path (same "user already has it"
// pattern as crosshairsPath) -- this plugin does not download ReShade
// itself. The only network call is a read-only version-check GET to
// reshade.me (new network/external-api-shaped permission, not a Safety
// Notes boundary change -- mirrors the existing GitHub profile-list fetch).
public static partial class TeknoParrotProfileScanner
{
    private static readonly Regex ReShadeVersionPattern = new(
        @"ReShade_Setup_(\d+\.\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

    // Scans the first 2 MB of a game exe for graphics API imports. Returns
    // the ReShade DLL name to deploy (e.g. "dxgi.dll"), or null if nothing
    // recognized was found. D3D12 is checked first: a title that imports
    // both d3d11 and d3d12 (e.g. a UWP-wrapped game) should be hooked at
    // the outer DX12 layer, not the inner DX11 layer.
    public static string? GetGameApiDll(string exePath)
    {
        var text = ReadAsciiPrefix(exePath);
        if (text is null)
        {
            return null;
        }

        if (text.Contains("d3d12.dll", StringComparison.OrdinalIgnoreCase)) return "d3d12.dll";
        if (text.Contains("d3d11.dll", StringComparison.OrdinalIgnoreCase) || text.Contains("dxgi.dll", StringComparison.OrdinalIgnoreCase)) return "dxgi.dll";
        if (text.Contains("d3d9.dll", StringComparison.OrdinalIgnoreCase)) return "d3d9.dll";
        if (text.Contains("opengl32.dll", StringComparison.OrdinalIgnoreCase)) return "opengl32.dll";
        return null;
    }

    private static string? ReadAsciiPrefix(string exePath)
    {
        try
        {
            var readLen = (int)Math.Min(new FileInfo(exePath).Length, 2 * 1024 * 1024);
            var buffer = new byte[readLen];
            using var stream = File.OpenRead(exePath);
            var read = 0;
            while (read < readLen)
            {
                var n = stream.Read(buffer, read, readLen - read);
                if (n == 0)
                {
                    break;
                }
                read += n;
            }

            return System.Text.Encoding.ASCII.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Reads the PE Optional Header's machine word to determine whether an
    // exe is 32-bit or 64-bit. Returns "x86", "x64", or null on error or an
    // unrecognized format.
    public static string? GetExeArchitecture(string exePath)
    {
        try
        {
            using var stream = File.OpenRead(exePath);
            var buffer = new byte[4];

            if (!ReadExact(stream, buffer, 2))
            {
                return null;
            }
            if (buffer[0] != 0x4D || buffer[1] != 0x5A) // "MZ"
            {
                return null;
            }

            stream.Seek(0x3C, SeekOrigin.Begin);
            if (!ReadExact(stream, buffer, 4))
            {
                return null;
            }
            var peOffset = BitConverter.ToInt32(buffer, 0);

            stream.Seek(peOffset + 4, SeekOrigin.Begin); // skip "PE\0\0"
            if (!ReadExact(stream, buffer, 2))
            {
                return null;
            }
            var machine = BitConverter.ToUInt16(buffer, 0);

            return machine switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
            {
                return false;
            }
            read += n;
        }

        return true;
    }

    // Resolves where ReShade would deploy for a given registered game
    // (target folder + DLL name) without touching anything. Mirrors
    // Get-ReShadeTargetInfo.
    public static ReShadeTargetInfo GetReShadeTargetInfo(XDocument doc, string gamePath, string exeDir)
    {
        var emulatorType = ChildByLocalName(doc.Root, "EmulatorType")?.Value.Trim() ?? "";

        var targetDir = exeDir;
        if (emulatorType.Contains("openparrot", StringComparison.OrdinalIgnoreCase))
        {
            var openParrotDir = Path.Combine(exeDir, "openparrot");
            if (Directory.Exists(openParrotDir))
            {
                targetDir = openParrotDir;
            }
        }

        if (emulatorType.Contains("budgieloader", StringComparison.OrdinalIgnoreCase))
        {
            return new ReShadeTargetInfo(targetDir, "opengl32.dll", true);
        }

        var detected = GetGameApiDll(gamePath);
        return detected is not null
            ? new ReShadeTargetInfo(targetDir, detected, true)
            : new ReShadeTargetInfo(targetDir, "dxgi.dll", false);
    }

    // Checks the Authenticode signature on a user-provided ReShade DLL.
    // Windows-only (AuthenticodeExaminer wraps native Windows APIs).
    // Informational, not a hard gate -- an invalid/missing signature is
    // reported but does not block deployment, since the user supplied this
    // file themselves. Mirrors Test-ReShadeDllSignature.
    public static ReShadeSignatureResult CheckReShadeDllSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ReShadeSignatureResult("NotSupportedOnThisPlatform", null);
        }

        try
        {
            var inspector = new FileInspector(path);
            var result = inspector.Validate();
            var signer = result == SignatureCheckResult.Valid
                ? inspector.GetSignatures().FirstOrDefault()?.SigningCertificate?.Subject
                : null;
            return new ReShadeSignatureResult(result.ToString(), signer);
        }
        catch (Exception)
        {
            return new ReShadeSignatureResult("Error", null);
        }
    }

    // Translates a signature check result into a plain-English explanation.
    // Status values are SignatureCheckResult enum names (real Authenticode
    // validation, via AuthenticodeExaminer/native Windows APIs), plus two
    // sentinels this plugin adds: "Error" (the check itself threw) and
    // "NotSupportedOnThisPlatform" (non-Windows -- AuthenticodeExaminer
    // wraps Windows-only native APIs). Mirrors Get-SignatureStatusText.
    public static string GetSignatureStatusText(string status) => status switch
    {
        "NoSignature" => "this file has no digital signature at all",
        "BadDigest" => "the file's contents don't match its signature -- it was modified after signing",
        "UnknownProvider" => "Windows couldn't find a provider to verify this file's signature",
        "UntrustedRoot" => "signed, but not by a certificate Windows trusts",
        "ExplicitDistrust" => "signed, but this system explicitly distrusts the certificate used",
        "CertificateExpired" => "the certificate used to sign this file has expired",
        "UnknownFailure" => "the file's signature is not valid, for an unknown reason",
        "RevokedCertificate" => "the certificate used to sign this file has been revoked",
        "UnknownSubject" => "this isn't a file type Windows recognizes for Authenticode",
        "NotSupportedOnThisPlatform" => "signature checking is only supported on Windows",
        "Error" => "the signature check itself failed unexpectedly",
        _ => $"signature could not be confirmed ({status})"
    };

    // Fetches the current ReShade version string from reshade.me. Returns
    // e.g. "6.7.3", or null if the site can't be reached. Read-only;
    // failure here never blocks deployment, it just skips the "newer
    // version available" notice. Mirrors Get-ReShadeLatestVersion.
    public static async Task<string?> GetReShadeLatestVersionAsync(HttpClient http, Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        try
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://reshade.me");
            request.Headers.UserAgent.ParseAdd($"TeknoParrotManagerHyperSpin2Plugin/{TeknoParrotManagerHyperSpin2PluginMain.PluginVersion}");
            using var response = await http.SendAsync(request, requestCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(requestCts.Token).ConfigureAwait(false);
            var match = ReShadeVersionPattern.Match(html);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log?.Invoke($"ReShade: could not reach reshade.me -- {ex.Message}");
            return null;
        }
    }

    // Deploys (or, with dryRun, just previews) ReShade into every selected
    // registered game's folder, picking the 32- or 64-bit source DLL based
    // on each game's detected architecture, and applying a preset .ini if
    // one is configured (a per-game override in ReShadePresetsPath always
    // wins over the global ReShadePresetPath). gameCodes filters to a
    // specific set of profile codes; null means every registered game.
    // Mirrors Invoke-ReShadeSetup, minus the interactive prompts.
    public static ReShadeSetupResult ApplyReShadeSetup(TeknoParrotSettings settings, IReadOnlyCollection<string>? gameCodes, bool dryRun, Action<string>? log = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");

        var sourceDll = settings.ReShadeSourceDllPath;
        var sourceDll32 = settings.ReShadeSourceDll32Path;
        var presetsDir = !string.IsNullOrWhiteSpace(settings.ReShadePresetsPath)
            ? settings.ReShadePresetsPath
            : Path.Combine(AppContext.BaseDirectory, "ReShadePresets");

        var deployed = new List<string>();
        var skipped = new List<string>();
        var errors = new List<string>();
        var presetOverrides = 0;

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new ReShadeSetupResult(deployed, skipped, errors, presetOverrides);
        }

        var codeFilter = gameCodes is { Count: > 0 } ? new HashSet<string>(gameCodes, StringComparer.OrdinalIgnoreCase) : null;

        foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (codeFilter is not null && !codeFilter.Contains(code))
            {
                continue;
            }

            try
            {
                var doc = XDocument.Load(file);
                var gamePath = ChildByLocalName(doc.Root, "GamePath")?.Value.Trim();
                if (string.IsNullOrWhiteSpace(gamePath) || !File.Exists(gamePath))
                {
                    skipped.Add(code);
                    continue;
                }

                var exeDir = Path.GetDirectoryName(gamePath);
                if (string.IsNullOrWhiteSpace(exeDir))
                {
                    skipped.Add(code);
                    continue;
                }

                var arch = GetExeArchitecture(gamePath);
                string activeDll;
                if (arch == "x86")
                {
                    if (string.IsNullOrWhiteSpace(sourceDll32) || !File.Exists(sourceDll32))
                    {
                        log?.Invoke($"ReShade: skipped {code} -- 32-bit game, no 32-bit ReShade DLL configured.");
                        skipped.Add(code);
                        continue;
                    }

                    activeDll = sourceDll32;
                }
                else if (arch is null or "x64")
                {
                    if (string.IsNullOrWhiteSpace(sourceDll) || !File.Exists(sourceDll))
                    {
                        log?.Invoke($"ReShade: skipped {code} -- no 64-bit ReShade DLL configured.");
                        skipped.Add(code);
                        continue;
                    }

                    activeDll = sourceDll;
                }
                else
                {
                    log?.Invoke($"ReShade: skipped {code} -- unsupported architecture ({arch}).");
                    skipped.Add(code);
                    continue;
                }

                var targetInfo = GetReShadeTargetInfo(doc, gamePath, exeDir);
                if (!targetInfo.ApiDetected)
                {
                    log?.Invoke($"ReShade: {code} -- graphics API not detected, defaulting to dxgi.dll.");
                }

                var destDll = Path.Combine(targetInfo.TargetDir, targetInfo.DllName);

                var perGamePreset = Path.Combine(presetsDir, code + ".ini");
                var effectivePreset = File.Exists(perGamePreset) ? perGamePreset
                    : (!string.IsNullOrWhiteSpace(settings.ReShadePresetPath) && File.Exists(settings.ReShadePresetPath)) ? settings.ReShadePresetPath
                    : null;
                var presetIsPerGame = effectivePreset == perGamePreset;

                if (!dryRun)
                {
                    Directory.CreateDirectory(targetInfo.TargetDir);
                    File.Copy(activeDll, destDll, overwrite: true);
                    if (effectivePreset is not null)
                    {
                        File.Copy(effectivePreset, Path.Combine(targetInfo.TargetDir, "ReShade.ini"), overwrite: true);
                    }
                }

                if (presetIsPerGame)
                {
                    presetOverrides++;
                }

                log?.Invoke($"ReShade: {code} -> {targetInfo.TargetDir} [{targetInfo.DllName}]" + (effectivePreset is not null ? $" (preset: {(presetIsPerGame ? "per-game" : "global")})" : ""));
                deployed.Add(code);
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                log?.Invoke($"ReShade: FAILED {code} -- {ex.Message}");
                errors.Add(code);
            }
        }

        return new ReShadeSetupResult(deployed, skipped, errors, presetOverrides);
    }
}

public sealed record ReShadeTargetInfo(string TargetDir, string DllName, bool ApiDetected);

public sealed record ReShadeSignatureResult(string Status, string? Signer);

public sealed record ReShadeSetupResult(
    [property: JsonPropertyName("deployed_profiles")] IReadOnlyList<string> DeployedProfiles,
    [property: JsonPropertyName("skipped_profiles")] IReadOnlyList<string> SkippedProfiles,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles,
    [property: JsonPropertyName("preset_overrides")] int PresetOverrides);

using System.Management;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 6 of ROADMAP.md: ports Get-DetectedGpuVendor, Get-GpuFixFieldNames,
// and Test-GpuFixUpToDate/Invoke-GpuFixSetup from the original PowerShell
// tool. Pure local detection (WMI) plus profile XML field toggling -- no
// network calls anywhere in this phase, so no new plugin.json permission
// is needed (Group A per ROADMAP.md).
public static partial class TeknoParrotProfileScanner
{
    private static readonly string[] FallbackBoolGpuFields = { "EnableAmdFix", "AMDCrashFix", "AMDFix" };
    private static readonly string[] FallbackDropdownGpuFields = { "GPU Fix" };

    private static readonly Regex AmdBoolFieldPattern = new(@"\bamd\b|\bradeon\b|AMDFix|AMDCrash", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GpuDropdownOptionPattern = new(@"^amd$|^nvidia$|^intel$|^new amd", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IgnoredAdapterNamePattern = new(@"microsoft|virtual|remote", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AmdVendorNamePattern = new(@"amd|radeon", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NvidiaVendorNamePattern = new(@"nvidia|geforce|rtx|gtx", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IntelVendorNamePattern = new(@"intel", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Read-only, best-effort GPU vendor detection via WMI. Windows-only --
    // returns Vendor=null on any other OS, on any WMI failure, or when no
    // real (non-virtual) adapter is found. The original script falls back
    // to an interactive prompt in this case; this plugin instead expects
    // the caller to pass an explicit vendor override to ApplyGpuFix when
    // this returns null.
    public static GpuVendorDetection DetectGpuVendor(Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new GpuVendorDetection(null, null);
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
            string? bestName = null;
            var bestRam = -1d;

            foreach (var found in searcher.Get())
            {
                using var adapter = found;
                var name = adapter["Name"] as string;
                if (string.IsNullOrWhiteSpace(name) || IgnoredAdapterNamePattern.IsMatch(name))
                {
                    continue;
                }

                var ram = adapter["AdapterRAM"] is { } ramValue ? Convert.ToDouble(ramValue) : 0d;
                if (ram > bestRam)
                {
                    bestRam = ram;
                    bestName = name;
                }
            }

            if (bestName is null)
            {
                return new GpuVendorDetection(null, null);
            }

            return new GpuVendorDetection(MatchVendorName(bestName), bestName);
        }
        catch (Exception ex)
        {
            log?.Invoke($"GpuFix: WMI detection failed -- {ex.Message}");
            return new GpuVendorDetection(null, null);
        }
    }

    // Pure name-matching logic, factored out of DetectGpuVendor so it's
    // unit-testable without a real WMI call (which would otherwise make
    // tests depend on whatever GPU happens to be in the CI/dev machine).
    internal static string? MatchVendorName(string adapterName) =>
        AmdVendorNamePattern.IsMatch(adapterName) ? "AMD"
        : NvidiaVendorNamePattern.IsMatch(adapterName) ? "NVIDIA"
        : IntelVendorNamePattern.IsMatch(adapterName) ? "Intel"
        : null;

    // Discovers GPU fix field names by scanning TeknoParrot GameProfiles at
    // runtime, so newly added games with new fix fields are covered
    // automatically without a plugin update. Always seeded with the
    // original tool's known field names first, then extended with whatever
    // GameProfiles scanning finds -- mirrors Get-GpuFixFieldNames exactly.
    public static GpuFixFieldNames DiscoverGpuFixFieldNames(string gameProfilesPath, Action<string>? log = null)
    {
        var boolFields = new HashSet<string>(FallbackBoolGpuFields, StringComparer.OrdinalIgnoreCase);
        var dropdownFields = new HashSet<string>(FallbackDropdownGpuFields, StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            return new GpuFixFieldNames(boolFields, dropdownFields, false);
        }

        foreach (var file in Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(file);
                var configValues = ChildByLocalName(doc.Root, "ConfigValues");
                foreach (var fieldInfo in ChildrenByLocalName(configValues, "FieldInformation"))
                {
                    var fieldName = ChildByLocalName(fieldInfo, "FieldName")?.Value.Trim();
                    var fieldType = ChildByLocalName(fieldInfo, "FieldType")?.Value.Trim();
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        continue;
                    }

                    if (fieldType == "Bool" && AmdBoolFieldPattern.IsMatch(fieldName))
                    {
                        boolFields.Add(fieldName);
                    }
                    else if (fieldType == "Dropdown")
                    {
                        var options = ChildrenByLocalName(ChildByLocalName(fieldInfo, "FieldOptions"), "string")
                            .Select(o => o.Value.Trim());
                        if (options.Any(GpuDropdownOptionPattern.IsMatch))
                        {
                            dropdownFields.Add(fieldName);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                log?.Invoke($"GpuFix: could not parse GameProfile '{Path.GetFileNameWithoutExtension(file)}' -- {ex.Message}");
            }
        }

        return new GpuFixFieldNames(boolFields, dropdownFields, true);
    }

    // Pure decision function: for a given profile and the field names
    // DiscoverGpuFixFieldNames found, determines whether the profile has
    // any GPU fix field at all (Eligible) and, if so, whether every such
    // field already matches the value expected for vendor (UpToDate).
    // Mirrors Test-GpuFixUpToDate.
    public static GpuFixEvaluation EvaluateGpuFix(XDocument doc, IReadOnlyCollection<string> boolFields, IReadOnlyCollection<string> dropdownFields, string vendor)
    {
        var eligible = false;
        var changes = new List<GpuFixChange>();

        foreach (var fieldName in boolFields)
        {
            var valueNode = ChildByLocalName(FindFieldInformation(doc, fieldName), "FieldValue");
            if (valueNode is null)
            {
                continue;
            }

            eligible = true;
            var newValue = vendor == "AMD" ? "1" : "0";
            if (valueNode.Value != newValue)
            {
                changes.Add(new GpuFixChange(fieldName, valueNode.Value, newValue));
            }
        }

        foreach (var fieldName in dropdownFields)
        {
            var field = FindFieldInformation(doc, fieldName);
            var valueNode = ChildByLocalName(field, "FieldValue");
            var options = ChildrenByLocalName(ChildByLocalName(field, "FieldOptions"), "string").Select(o => o.Value.Trim()).ToList();
            if (valueNode is null || options.Count == 0)
            {
                continue;
            }

            eligible = true;
            var newValue = "None";
            if (vendor == "AMD")
            {
                if (options.Contains("New AMD Driver")) newValue = "New AMD Driver";
                else if (options.Contains("AMD")) newValue = "AMD";
            }
            else if (vendor == "NVIDIA")
            {
                if (options.Contains("NVIDIA")) newValue = "NVIDIA";
            }
            else if (vendor == "Intel")
            {
                if (options.Contains("INTEL")) newValue = "INTEL";
            }

            if (valueNode.Value != newValue)
            {
                changes.Add(new GpuFixChange(fieldName, valueNode.Value, newValue));
            }
        }

        return new GpuFixEvaluation(eligible, eligible && changes.Count == 0, changes);
    }

    // Applies (or, with dryRun, just previews) GPU vendor fix fields across
    // every registered profile. vendor must be "AMD", "NVIDIA", or "Intel".
    // Mirrors Invoke-GpuFixSetup, minus the interactive prompt -- callers
    // pass an explicit vendor when DetectGpuVendor can't auto-detect one.
    public static GpuFixResult ApplyGpuFix(TeknoParrotSettings settings, string vendor, bool dryRun, Action<string>? log = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolveGameProfilesPathForSettings(settings);

        var fieldNames = DiscoverGpuFixFieldNames(gameProfilesPath, log);
        if (!fieldNames.GameProfilesFound)
        {
            log?.Invoke("GpuFix: GameProfiles folder not found -- using fallback field list.");
        }

        var updated = new List<string>();
        var errors = new List<string>();
        var unchanged = 0;

        if (!string.IsNullOrWhiteSpace(userProfilesPath) && Directory.Exists(userProfilesPath))
        {
            foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var doc = XDocument.Load(file);
                    var evaluation = EvaluateGpuFix(doc, fieldNames.BoolFields, fieldNames.DropdownFields, vendor);
                    if (evaluation.Changes.Count == 0)
                    {
                        unchanged++;
                        continue;
                    }

                    foreach (var change in evaluation.Changes)
                    {
                        var valueNode = ChildByLocalName(FindFieldInformation(doc, change.FieldName), "FieldValue");
                        if (valueNode is not null)
                        {
                            valueNode.Value = change.NewValue;
                        }

                        log?.Invoke($"GpuFix: {code} :: {change.FieldName} {change.OldValue} -> {change.NewValue}");
                    }

                    if (!dryRun)
                    {
                        SaveProfileDocument(doc, file);
                    }

                    updated.Add(code);
                }
                catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
                {
                    log?.Invoke($"GpuFix: FAILED {code} -- {ex.Message}");
                    errors.Add(code);
                }
            }
        }

        return new GpuFixResult(vendor, updated.Count, unchanged, errors.Count, updated, errors);
    }
}

public sealed record GpuVendorDetection(string? Vendor, string? Name);

public sealed record GpuFixFieldNames(HashSet<string> BoolFields, HashSet<string> DropdownFields, bool GameProfilesFound);

public sealed record GpuFixChange(string FieldName, string OldValue, string NewValue);

public sealed record GpuFixEvaluation(bool Eligible, bool UpToDate, IReadOnlyList<GpuFixChange> Changes);

public sealed record GpuFixResult(
    [property: JsonPropertyName("vendor")] string Vendor,
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("updated_profiles")] IReadOnlyList<string> UpdatedProfiles,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles);

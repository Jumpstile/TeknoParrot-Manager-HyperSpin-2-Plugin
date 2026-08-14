using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Read-only RC4 diagnostic. Unknown profile fields are reported, never written.
public static partial class TeknoParrotProfileScanner
{
    private static readonly HashSet<string> KnownProfileElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "GamePath", "GamePath2", "EmulationProfile", "ConfigValues", "GameProfileRevision", "ExecutableName",
        "ExecutableName2", "HasTwoExecutables", "LaunchSecondExecutableFirst", "EmulatorType", "Is64Bit",
        "ValidMd5", "GameName", "GameGenreInternal", "IconName", "RequiresAdmin", "GunGame", "DevOnly",
        "RequiresBepInEx", "IsLegacy", "GameVersion", "JoystickButtons", "Patreon", "xAxisMin", "xAxisMax",
        "yAxisMin", "yAxisMax", "InvertedMouseAxis", "ResetHint", "GasAxisMin", "GasAxisMax", "OnlineProfileURL",
        "OnlineIdFieldName", "OnlineIdType", "UseDirectionalPresses", "TestExecIs64Bit", "SecondExecutableArguments",
        "Requires4GBPatch", "LaunchSecondExecutableMinimized", "Use16BitAnalog", "RPCS3Config", "AllowSettingSync",
        "UseRemoteThread", "CustomArguments", "InvalidFiles"
    };

    private static readonly HashSet<string> KnownFieldTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bool", "Dropdown", "Text", "Slider", "DropdownIndex", "KeyCapture", "MonitorSelection", "Numeric"
    };

    public static ProfileSchemaDriftResult CheckProfileSchemaDrift(string gameProfilesPath, Action<string>? log = null)
    {
        var reports = new List<ProfileSchemaDriftItem>();
        if (!Directory.Exists(gameProfilesPath))
        {
            return new ProfileSchemaDriftResult(0, 0, reports, "GameProfiles folder was not found.");
        }

        foreach (var file in Directory.EnumerateFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(file);
                var unknownElements = doc.Root?.Elements()
                    .Select(element => element.Name.LocalName)
                    .Where(name => !KnownProfileElements.Contains(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();
                var unknownTypes = doc.Root?.Descendants()
                    .Where(element => string.Equals(element.Name.LocalName, "FieldInformation", StringComparison.OrdinalIgnoreCase))
                    .Select(field => field.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, "FieldType", StringComparison.OrdinalIgnoreCase))?.Value.Trim())
                    .Where(type => type is not null && !string.IsNullOrWhiteSpace(type) && !KnownFieldTypes.Contains(type))
                    .Select(type => type!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();

                if (unknownElements.Length > 0 || unknownTypes.Length > 0)
                {
                    reports.Add(new ProfileSchemaDriftItem(Path.GetFileNameWithoutExtension(file), unknownElements, unknownTypes));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                log?.Invoke($"ProfileSchema: could not inspect '{Path.GetFileNameWithoutExtension(file)}' -- {ex.Message}");
            }
        }

        return new ProfileSchemaDriftResult(CountScanned(gameProfilesPath), reports.Count, reports, null);
    }

    private static int CountScanned(string files)
        => Directory.EnumerateFiles(files, "*.xml", SearchOption.TopDirectoryOnly).Count();
}

public sealed record ProfileSchemaDriftResult(
    [property: JsonPropertyName("scanned_profiles")] int ScannedProfiles,
    [property: JsonPropertyName("drifted_profiles")] int DriftedProfiles,
    [property: JsonPropertyName("reports")] IReadOnlyList<ProfileSchemaDriftItem> Reports,
    [property: JsonPropertyName("note")] string? Note);

public sealed record ProfileSchemaDriftItem(
    [property: JsonPropertyName("profile")] string Profile,
    [property: JsonPropertyName("unknown_elements")] IReadOnlyList<string> UnknownElements,
    [property: JsonPropertyName("unknown_field_types")] IReadOnlyList<string> UnknownFieldTypes);

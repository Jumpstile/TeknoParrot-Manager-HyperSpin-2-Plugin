using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 7 (FFB Blaster path) of ROADMAP.md: ports Get-FFBBlasterFieldNames,
// Test-FFBBlasterUpToDate, and Invoke-FFBBlasterSetup. TeknoParrot's own
// built-in force feedback -- a per-game Bool field, paywalled (any paid
// TeknoParrot membership). This plugin can't verify subscription status,
// so enabling the field has no effect at all without one; the
// apply_ffb_blaster_setup action's own confirmationMessage states that
// prerequisite explicitly instead of guessing, mirroring the original's
// "do you have a membership? (Y/N)" prompt. Group A: pure local profile-
// field editing, no network calls, no new permission.
public static partial class TeknoParrotProfileScanner
{
    private static readonly Regex FfbBlasterFieldPattern = new(@"ffb.*blaster|blaster.*ffb", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Discovers the FFB Blaster field's identifying key by scanning
    // TeknoParrot GameProfiles at runtime -- never hardcoded. Newer
    // TeknoParrot builds put the identifying text on CategoryName; older
    // ones only have it on FieldName, so FieldName is checked as a
    // fallback when no CategoryName matches. Mirrors
    // Get-FFBBlasterFieldNames exactly, including that fallback.
    public static HashSet<string> DiscoverFfbBlasterFieldNames(string gameProfilesPath, Action<string>? log = null)
    {
        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(gameProfilesPath) || !Directory.Exists(gameProfilesPath))
        {
            return fields;
        }

        foreach (var file in Directory.GetFiles(gameProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(file);
                var configValues = ChildByLocalName(doc.Root, "ConfigValues");
                foreach (var fieldInfo in ChildrenByLocalName(configValues, "FieldInformation"))
                {
                    var fieldType = ChildByLocalName(fieldInfo, "FieldType")?.Value.Trim();
                    if (fieldType != "Bool")
                    {
                        continue;
                    }

                    var categoryName = ChildByLocalName(fieldInfo, "CategoryName")?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(categoryName) && FfbBlasterFieldPattern.IsMatch(categoryName))
                    {
                        fields.Add(categoryName);
                        continue;
                    }

                    var fieldName = ChildByLocalName(fieldInfo, "FieldName")?.Value.Trim();
                    if (!string.IsNullOrWhiteSpace(fieldName) && FfbBlasterFieldPattern.IsMatch(fieldName))
                    {
                        fields.Add(fieldName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
            {
                log?.Invoke($"FfbBlaster: could not parse GameProfile '{Path.GetFileNameWithoutExtension(file)}' -- {ex.Message}");
            }
        }

        return fields;
    }

    // Looks up a FieldInformation node by CategoryName first (a key
    // discovered via DiscoverFfbBlasterFieldNames on newer TeknoParrot
    // builds), falling back to FieldName (older builds where the
    // identifying text was only ever on FieldName). Mirrors the same
    // fallback inside Test-FFBBlasterUpToDate.
    private static IEnumerable<XElement> FindFieldInformationByCategoryOrName(XDocument doc, string key)
    {
        var configValues = ChildByLocalName(doc.Root, "ConfigValues");
        var byCategory = ChildrenByLocalName(configValues, "FieldInformation")
            .Where(fi => string.Equals(ChildByLocalName(fi, "CategoryName")?.Value.Trim(), key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byCategory.Count > 0)
        {
            return byCategory;
        }

        return ChildrenByLocalName(configValues, "FieldInformation")
            .Where(fi => string.Equals(ChildByLocalName(fi, "FieldName")?.Value.Trim(), key, StringComparison.OrdinalIgnoreCase));
    }

    // Pure decision function: for a given profile and the field keys
    // DiscoverFfbBlasterFieldNames found, determines whether the profile
    // has an FFB Blaster field at all (Eligible) and whether it is already
    // set to "1" (UpToDate). Mirrors Test-FFBBlasterUpToDate.
    public static FfbBlasterEvaluation EvaluateFfbBlaster(XDocument doc, IReadOnlyCollection<string> categories)
    {
        var eligible = false;
        var changes = new List<GpuFixChange>();

        foreach (var key in categories)
        {
            foreach (var fieldInfo in FindFieldInformationByCategoryOrName(doc, key))
            {
                var fieldType = ChildByLocalName(fieldInfo, "FieldType")?.Value.Trim();
                if (fieldType != "Bool")
                {
                    continue;
                }

                var valueNode = ChildByLocalName(fieldInfo, "FieldValue");
                if (valueNode is null)
                {
                    continue;
                }

                eligible = true;
                var fieldName = ChildByLocalName(fieldInfo, "FieldName")?.Value.Trim() ?? key;
                if (valueNode.Value != "1")
                {
                    changes.Add(new GpuFixChange(fieldName, valueNode.Value, "1"));
                }
            }
        }

        return new FfbBlasterEvaluation(eligible, eligible && changes.Count == 0, changes);
    }

    // Applies (or, with dryRun, just previews) the FFB Blaster field across
    // every registered profile. Mirrors Invoke-FFBBlasterSetup, minus the
    // membership-confirmation prompt -- the apply_ffb_blaster_setup
    // action's own confirmationMessage carries that prerequisite instead.
    public static FfbBlasterResult ApplyFfbBlasterSetup(TeknoParrotSettings settings, bool dryRun, Action<string>? log = null)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var gameProfilesPath = ResolveGameProfilesPathForSettings(settings);

        var fields = DiscoverFfbBlasterFieldNames(gameProfilesPath, log);
        if (fields.Count == 0)
        {
            log?.Invoke("FfbBlaster: no FFB Blaster field discovered in any GameProfile -- this TeknoParrot install may not support it yet.");
        }

        var updated = new List<string>();
        var errors = new List<string>();
        var unchanged = 0;
        var noField = 0;

        if (fields.Count > 0 && !string.IsNullOrWhiteSpace(userProfilesPath) && Directory.Exists(userProfilesPath))
        {
            foreach (var file in Directory.GetFiles(userProfilesPath, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var code = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var doc = XDocument.Load(file);
                    var evaluation = EvaluateFfbBlaster(doc, fields);
                    if (!evaluation.Eligible)
                    {
                        noField++;
                        continue;
                    }

                    if (evaluation.Changes.Count == 0)
                    {
                        unchanged++;
                        continue;
                    }

                    foreach (var change in evaluation.Changes)
                    {
                        // FieldName itself (already resolved by EvaluateFfbBlaster)
                        // is unambiguous within a single profile -- no need to
                        // re-apply the CategoryName fallback at write time.
                        var valueNode = ChildByLocalName(FindFieldInformation(doc, change.FieldName), "FieldValue");
                        if (valueNode is not null)
                        {
                            valueNode.Value = change.NewValue;
                        }

                        log?.Invoke($"FfbBlaster: {code} :: {change.FieldName} {change.OldValue} -> {change.NewValue}");
                    }

                    if (!dryRun)
                    {
                        SaveProfileDocument(doc, file);
                    }

                    updated.Add(code);
                }
                catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException)
                {
                    log?.Invoke($"FfbBlaster: FAILED {code} -- {ex.Message}");
                    errors.Add(code);
                }
            }
        }

        return new FfbBlasterResult(updated.Count, unchanged, noField, errors.Count, updated, errors);
    }
}

public sealed record FfbBlasterEvaluation(bool Eligible, bool UpToDate, IReadOnlyList<GpuFixChange> Changes);

public sealed record FfbBlasterResult(
    [property: JsonPropertyName("updated")] int Updated,
    [property: JsonPropertyName("unchanged")] int Unchanged,
    [property: JsonPropertyName("no_field")] int NoField,
    [property: JsonPropertyName("errors")] int Errors,
    [property: JsonPropertyName("updated_profiles")] IReadOnlyList<string> UpdatedProfiles,
    [property: JsonPropertyName("error_profiles")] IReadOnlyList<string> ErrorProfiles);

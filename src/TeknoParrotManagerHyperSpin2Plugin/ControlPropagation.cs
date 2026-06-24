using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace TeknoParrotManagerHyperSpin2Plugin;

// Phase 1 of ROADMAP.md: ports Invoke-ControlPropagation / Invoke-DeviceSurvey
// and their helpers from the original PowerShell tool. You bind ONE reference
// game per control type ("archetype") in TeknoParrotUI; this copies those
// bindings to every other profile of the same type, matched by button
// function so a wheel value never lands on a gun. A reference game's own
// button bindings are never touched. Its Input API setting is also left
// alone UNLESS the user explicitly names it as the wrong one via the
// canonicalArchetype override -- see the v0.99.12 regression note on
// PropagateControlsCore below before changing either of those rules.
public static partial class TeknoParrotProfileScanner
{
    private static readonly HashSet<string> KnownControlFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "button", "driving", "lightgun", "trackball", "analog", "spinner",
    };

    private static readonly JsonSerializerOptions ControlOverridesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Loads the optional per-game control overrides JSON file (mirrors the
    // original tool's TeknoParrot-Manager.overrides.json schema, just the
    // propagation-relevant subset: noSync/onlySync/datFile belong to other
    // phases). Fails soft to ControlOverrides.Empty on any error so a missing
    // or malformed file never blocks propagation.
    public static ControlOverrides LoadControlOverrides(string? path, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return ControlOverrides.Empty;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<ControlOverridesFile>(raw, ControlOverridesJsonOptions);
            if (parsed is null)
            {
                return ControlOverrides.Empty;
            }

            var result = new ControlOverrides();
            foreach (var code in parsed.NoPropagate ?? Array.Empty<string>())
            {
                result.NoPropagate.Add(code);
            }

            foreach (var (code, archetype) in parsed.ForceArchetype ?? new Dictionary<string, string>())
            {
                result.ForceArchetype[code] = archetype;
            }

            foreach (var (code, family) in parsed.FamilyOverride ?? new Dictionary<string, string>())
            {
                if (KnownControlFamilies.Contains(family))
                {
                    result.FamilyOverride[code] = family;
                }
                else
                {
                    log?.Invoke($"ControlOverrides: familyOverride for '{code}' has unknown family '{family}' -- ignored.");
                }
            }

            foreach (var (family, code) in parsed.CanonicalArchetype ?? new Dictionary<string, string>())
            {
                if (KnownControlFamilies.Contains(family))
                {
                    result.CanonicalArchetype[family] = code;
                }
                else
                {
                    log?.Invoke($"ControlOverrides: canonicalArchetype has unknown family '{family}' -- ignored.");
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log?.Invoke($"ControlOverrides: could not read '{path}' -- {ex.Message}");
            return ControlOverrides.Empty;
        }
    }

    // Config fields that describe input behaviour (aim mode, sensitivity, axis
    // handling). Safe to copy between same-type games so a propagated game
    // reproduces the reference game's feel. Copied only when the target
    // profile also defines the field, so nothing is ever invented.
    private static readonly string[] InputConfigFields =
    {
        "Use Relative Input",
        "Player 1 Relative Sensitivity",
        "Player 2 Relative Sensitivity",
        "HideCursor",
        "Reverse Y Axis",
        "Reverse Throttle Axis",
        "Use Keyboard/Button For Axis",
        "Keyboard/Button Axis X/Y Sensitivity",
        "Keyboard/Button Axis Throttle Sensitivity",
    };

    // Known carried-setting values that usually mean the reference game was
    // bound with a substitute device (keyboard, mouse) instead of its real
    // hardware. Surfaced as warnings on a "bound" report item so a bad
    // reference-game value is caught before it fans out to every other game
    // of that type.
    private static readonly (string Family, string Field, string Value, string Message)[] ConfigCarryWarnings =
    {
        ("driving", "Use Keyboard/Button For Axis", "True",
            "wheel/pedal axes will be read as digital keyboard/button input, not analog -- should be False if you bind with a real wheel."),
        ("lightgun", "Use Relative Input", "True",
            "gun aim will be read as relative mouse-style movement, not absolute screen position -- should usually be False for a real lightgun that reports absolute coordinates."),
    };

    private static XElement? ChildByLocalName(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> ChildrenByLocalName(XElement? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? Enumerable.Empty<XElement>();

    private static IEnumerable<XElement> GetButtonNodes(XDocument doc) =>
        ChildrenByLocalName(ChildByLocalName(doc.Root, "JoystickButtons"), "JoystickButtons");

    private static bool IsButtonBound(XElement button) =>
        ChildByLocalName(button, "RawInputButton") is not null ||
        ChildByLocalName(button, "DirectInputButton") is not null ||
        ChildByLocalName(button, "XInputButton") is not null;

    // Composite match key for a button: "InputMapping|AnalogType". AnalogType
    // is absent on many template buttons; it defaults to None on both sides
    // so a minimal template button still matches a bound archetype button.
    private static string? GetButtonKey(XElement button)
    {
        var inputMapping = ChildByLocalName(button, "InputMapping")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(inputMapping))
        {
            return null;
        }

        var analogType = ChildByLocalName(button, "AnalogType")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(analogType))
        {
            analogType = "None";
        }

        return $"{inputMapping}|{analogType}";
    }

    // Infers a profile's control family from its button set so bindings never
    // cross between game types (a wheel binding can never land on a gun).
    // "spinner" can't be auto-detected from AnalogType alone -- spinner games
    // must be assigned via the familyOverride control override.
    private static string GetProfileFamily(XDocument doc)
    {
        var hasWheel = false;
        var hasGun = false;
        var hasTrackball = false;
        var hasOtherAxis = false;

        foreach (var button in GetButtonNodes(doc))
        {
            var inputMapping = ChildByLocalName(button, "InputMapping")?.Value.Trim() ?? "";
            var analogType = ChildByLocalName(button, "AnalogType")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(analogType))
            {
                analogType = "None";
            }

            if (inputMapping is "P1Trackball" or "P2Trackball")
            {
                hasTrackball = true;
            }

            switch (analogType)
            {
                case "Wheel":
                case "Gas":
                case "Brake":
                    hasWheel = true;
                    break;
                // TeknoParrot uses analog joystick axes to represent lightgun aim.
                case "AnalogJoystick":
                case "AnalogJoystickReverse":
                    hasGun = true;
                    break;
                case "None":
                    break;
                default:
                    hasOtherAxis = true;
                    break;
            }
        }

        if (hasWheel) return "driving";
        if (hasGun) return "lightgun";
        if (hasTrackball) return "trackball";
        if (hasOtherAxis) return "analog";
        return "button";
    }

    private static XElement? FindFieldInformation(XDocument doc, string fieldName) =>
        ChildrenByLocalName(ChildByLocalName(doc.Root, "ConfigValues"), "FieldInformation")
            .FirstOrDefault(fi => ChildByLocalName(fi, "FieldName")?.Value.Trim() == fieldName);

    private static string? GetProfileInputApi(XDocument doc) =>
        ChildByLocalName(FindFieldInformation(doc, "Input API"), "FieldValue")?.Value.Trim();

    // Sets the Input API field, but only if the field exists. Normally only
    // succeeds if the profile's FieldOptions already lists the requested
    // API -- a RawInput binding will not work if the profile's API says
    // XInput. The one exception is "MergedInput": TeknoParrot's own UI
    // dynamically materializes "MergedInput" into a legacy profile's
    // FieldOptions the first time it's selected there, so an absent
    // FieldOptions entry for that value doesn't mean the profile can't use
    // it -- it just hasn't been touched in the TeknoParrot UI yet. Mirrored
    // here so a propagated XInput-style binding is actually usable without
    // requiring the user to manually re-toggle every target game first.
    private static bool SetProfileInputApi(XDocument doc, string api)
    {
        var field = FindFieldInformation(doc, "Input API");
        if (field is null)
        {
            return false;
        }

        var optionsNode = ChildByLocalName(field, "FieldOptions");
        var options = ChildrenByLocalName(optionsNode, "string").Select(o => o.Value.Trim()).ToList();
        if (!options.Contains(api))
        {
            if (api != "MergedInput" || optionsNode is null)
            {
                return false;
            }

            optionsNode.Add(new XElement(optionsNode.Name.Namespace + "string", api));
        }

        var valueNode = ChildByLocalName(field, "FieldValue");
        if (valueNode is null)
        {
            return false;
        }

        valueNode.Value = api;
        return true;
    }

    // Longest common prefix of a list of strings, used to turn a device's
    // bind names into a single device name. Returns "" for an empty list.
    private static string GetLongestCommonPrefix(IReadOnlyList<string> strings)
    {
        if (strings.Count == 0)
        {
            return "";
        }

        var prefix = strings[0];
        foreach (var s in strings)
        {
            var max = Math.Min(prefix.Length, s.Length);
            var i = 0;
            while (i < max && prefix[i] == s[i])
            {
                i++;
            }

            prefix = prefix[..i];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return prefix;
    }

    private static readonly string[] BindTagPriority = { "RawInputButton", "DirectInputButton", "XInputButton" };

    // Returns the distinct device names a profile is bound to. Buttons are
    // grouped by device path, and each device's name is the longest common
    // prefix of its bind names (e.g. "Ultimarc I-PAC A" + "Ultimarc I-PAC F"
    // -> "Ultimarc I-PAC"). Lets a caller confirm each game type uses the
    // device intended before copying its controls to other games.
    private static List<string> GetProfileDevices(XDocument doc)
    {
        var bindNamesByPath = new Dictionary<string, List<string>>();
        var tagByPath = new Dictionary<string, string>();

        foreach (var button in GetButtonNodes(doc))
        {
            foreach (var tag in BindTagPriority)
            {
                var bind = ChildByLocalName(button, tag);
                if (bind is null)
                {
                    continue;
                }

                var pathKey = ChildByLocalName(bind, "DevicePath")?.Value ?? tag;
                var bindName = ChildByLocalName(button, "BindName")?.Value ?? "";

                if (!bindNamesByPath.TryGetValue(pathKey, out var names))
                {
                    names = new List<string>();
                    bindNamesByPath[pathKey] = names;
                    tagByPath[pathKey] = tag;
                }

                if (!string.IsNullOrEmpty(bindName) && bindName != "None")
                {
                    names.Add(bindName);
                }

                break;
            }
        }

        var result = new List<string>();
        foreach (var (pathKey, names) in bindNamesByPath)
        {
            // The API comes from the binding element type, not a guess -- a
            // gamepad read via RawInput is labelled RawInput, not XInput.
            var api = tagByPath[pathKey] switch
            {
                "XInputButton" => "XInput",
                "DirectInputButton" => "DirectInput",
                _ => "RawInput",
            };

            var name = GetLongestCommonPrefix(names).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                // No usable bind names: fall back to a friendly device path
                // (e.g. "Windows Mouse Cursor"), but not a raw HID path.
                name = !string.IsNullOrEmpty(pathKey) && !pathKey.StartsWith(@"\\?\", StringComparison.Ordinal)
                    ? pathKey
                    : "(unnamed device)";
            }

            // Replace TeknoParrot's generic "Input Device N" label with the
            // actual API so the report is accurate and readable.
            if (name.StartsWith("Input Device ", StringComparison.Ordinal))
            {
                name = $"{api} Device {name["Input Device ".Length..]}";
            }
            else if (name == "Input Device")
            {
                name = $"{api} Device";
            }

            if (!result.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static Dictionary<string, string> GetConfigFieldMap(XDocument doc, IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, string>();
        foreach (var fi in ChildrenByLocalName(ChildByLocalName(doc.Root, "ConfigValues"), "FieldInformation"))
        {
            var fieldName = ChildByLocalName(fi, "FieldName")?.Value.Trim();
            if (fieldName is null || !names.Contains(fieldName))
            {
                continue;
            }

            var fieldValue = ChildByLocalName(fi, "FieldValue");
            if (fieldValue is not null)
            {
                result[fieldName] = fieldValue.Value;
            }
        }

        return result;
    }

    private static bool SetConfigField(XDocument doc, string name, string value)
    {
        var field = FindFieldInformation(doc, name);
        var fieldValue = ChildByLocalName(field, "FieldValue");
        if (fieldValue is null)
        {
            return false;
        }

        fieldValue.Value = value;
        return true;
    }

    // Returns warning strings for a family's carried config values: known
    // device-mismatch combos (see ConfigCarryWarnings) plus any
    // "...Sensitivity" field carried as literal 0, which would silently
    // disable aiming/axis response on every propagated game of that type.
    private static List<string> GetConfigCarryFlags(string family, IReadOnlyDictionary<string, string> configCarry)
    {
        var flags = new List<string>();
        foreach (var (warnFamily, field, value, message) in ConfigCarryWarnings)
        {
            if (warnFamily == family && configCarry.TryGetValue(field, out var actual) && actual == value)
            {
                flags.Add($"{field}={value} -- {message}");
            }
        }

        foreach (var (key, value) in configCarry)
        {
            if (key.EndsWith("Sensitivity", StringComparison.Ordinal) && value.Trim() == "0")
            {
                flags.Add($"{key}=0 -- this disables aiming/axis response entirely.");
            }
        }

        return flags;
    }

    // Scans UserProfiles and returns the bound-game pool: every profile the
    // user has bound to a meaningful degree (>= minBound bound buttons).
    // Each entry carries its family, Input API, and a map of (key -> bound
    // button node) used as the source of truth for copying. Includes
    // previously-propagated profiles intentionally: on re-runs they act as
    // archetypes for any newly registered games.
    private static List<ArchetypeEntry> BuildArchetypePool(string userProfilesDir, int minBound)
    {
        var pool = new List<ArchetypeEntry>();
        foreach (var path in Directory.EnumerateFiles(userProfilesDir, "*.xml", SearchOption.TopDirectoryOnly))
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch
            {
                continue;
            }

            if (document.Root is null)
            {
                continue;
            }

            var map = new Dictionary<string, XElement>();
            var boundCount = 0;
            foreach (var button in GetButtonNodes(document))
            {
                if (!IsButtonBound(button))
                {
                    continue;
                }

                var key = GetButtonKey(button);
                if (key is not null)
                {
                    map[key] = button;
                    boundCount++;
                }
            }

            if (boundCount < minBound)
            {
                continue;
            }

            pool.Add(new ArchetypeEntry(
                Code: Path.GetFileNameWithoutExtension(path),
                Path: path,
                Family: GetProfileFamily(document),
                InputApi: GetProfileInputApi(document),
                Devices: GetProfileDevices(document),
                ConfigCarry: GetConfigFieldMap(document, InputConfigFields),
                Map: map,
                BoundCount: boundCount));
        }

        return pool;
    }

    public static ControlPropagationResult PropagateControls(TeknoParrotSettings settings, ControlOverrides overrides, bool dryRun)
    {
        var rootPath = ResolveRootPath(settings);
        var userProfilesPath = ResolvePath(settings.UserProfilesPath, rootPath, "UserProfiles");
        var minBound = settings.MinBoundForArchetype > 0 ? settings.MinBoundForArchetype : 5;

        if (string.IsNullOrWhiteSpace(userProfilesPath) || !Directory.Exists(userProfilesPath))
        {
            return new ControlPropagationResult(false, dryRun,
                new[] { "UserProfiles folder was not found. Set userProfilesPath or teknoparrotRootPath." },
                Array.Empty<ControlPropagationItem>());
        }

        var pool = BuildArchetypePool(userProfilesPath, minBound);
        var items = PropagateControlsCore(userProfilesPath, pool, minBound, overrides, dryRun);
        return new ControlPropagationResult(true, dryRun, Array.Empty<string>(), items);
    }

    // For each UNbound (or barely-bound) profile, chooses the best same-family
    // reference game by key overlap, copies its bindings into matching
    // unbound buttons, carries its Input API, and records what was bound vs
    // left manual. Reference games (archetypes) are never modified.
    //
    // An archetype's own Input API is deliberately left alone, even when a
    // different archetype would be a "better" match for it. A prior version
    // of the original tool (v0.99.12) let an archetype's Input API be
    // "corrected" against the best non-self overlap match, reasoning that an
    // archetype could itself be sitting on a stale API. In practice this
    // broke a real user's library: almost every well-bound profile is
    // simultaneously a pool member (the same minBound threshold drives
    // both), so the "best overlap" heuristic -- fine for deciding what
    // bindings to copy -- ended up cross-correcting unrelated,
    // independently-correct archetypes against each other with no real
    // signal for which one was right. A deliberately-configured MergedInput
    // reference profile got silently flipped to DirectInput because an
    // unrelated archetype won the overlap comparison. Do not reintroduce
    // that "correction" without re-reading this note.
    private static List<ControlPropagationItem> PropagateControlsCore(
        string userProfilesDir, List<ArchetypeEntry> pool, int minBound, ControlOverrides overrides, bool dryRun)
    {
        var items = new List<ControlPropagationItem>();
        var poolByPath = pool.ToDictionary(s => s.Path, s => s, StringComparer.OrdinalIgnoreCase);

        foreach (var path in Directory.EnumerateFiles(userProfilesDir, "*.xml", SearchOption.TopDirectoryOnly))
        {
            var code = Path.GetFileNameWithoutExtension(path);

            if (poolByPath.TryGetValue(path, out var self))
            {
                TryCorrectCanonicalArchetypeApi(self, pool, overrides, dryRun, items);
                continue;
            }

            if (overrides.NoPropagate.Contains(code))
            {
                items.Add(ControlPropagationItem.SkippedOverride(code));
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Load(path);
            }
            catch
            {
                continue;
            }

            if (document.Root is null)
            {
                continue;
            }

            var buttons = GetButtonNodes(document).ToList();
            if (buttons.Count == 0)
            {
                continue;
            }

            var alreadyBoundCount = buttons.Count(IsButtonBound);
            var buttonsAlreadyBound = alreadyBoundCount >= minBound;

            var targetFamily = overrides.FamilyOverride.TryGetValue(code, out var overriddenFamily)
                ? overriddenFamily
                : GetProfileFamily(document);

            // If this game is pinned to a specific archetype via overrides, use
            // it -- this is an explicit user choice, so family is not enforced.
            ArchetypeEntry? best = null;
            var bestOverlap = 0;
            var forced = false;
            if (overrides.ForceArchetype.TryGetValue(code, out var wantCode))
            {
                best = pool.FirstOrDefault(s => s.Code == wantCode);
                forced = best is not null;
            }

            // Otherwise pick the best same-family archetype by how many of this
            // game's keys it can supply. Ties go to the more completely-bound
            // one. Computed even when this profile is already bound, since
            // that path also needs to know whether the archetype's Input API
            // can be retroactively applied.
            if (best is null)
            {
                foreach (var candidate in pool)
                {
                    if (candidate.Family != targetFamily)
                    {
                        continue;
                    }

                    var overlap = buttons.Count(b => GetButtonKey(b) is { } k && candidate.Map.ContainsKey(k));
                    if (overlap > bestOverlap || (overlap == bestOverlap && best is not null && candidate.BoundCount > best.BoundCount))
                    {
                        best = candidate;
                        bestOverlap = overlap;
                    }
                }

                if (best is null || bestOverlap == 0)
                {
                    items.Add(buttonsAlreadyBound
                        ? ControlPropagationItem.SkippedBound(code)
                        : ControlPropagationItem.NoArchetype(code, targetFamily));
                    continue;
                }
            }

            // A profile whose buttons are already configured is never re-bound,
            // but its Input API can still be retroactively corrected to match
            // its best-overlap archetype, independent of button binding.
            if (buttonsAlreadyBound)
            {
                var currentApi = GetProfileInputApi(document);
                var apiSet = best.InputApi is not null && best.InputApi != currentApi && SetProfileInputApi(document, best.InputApi);
                if (apiSet)
                {
                    if (!dryRun)
                    {
                        SaveProfileDocument(document, path);
                    }

                    items.Add(ControlPropagationItem.ApiFixed(code, best.Code, best.InputApi));
                }
                else
                {
                    items.Add(ControlPropagationItem.SkippedBound(code));
                }

                continue;
            }

            var boundNow = 0;
            var manual = new List<string>();
            foreach (var button in buttons.ToArray()) // snapshot before tree edits
            {
                if (IsButtonBound(button))
                {
                    continue;
                }

                var key = GetButtonKey(button);
                var buttonName = ChildByLocalName(button, "ButtonName")?.Value;

                if (key is not null && best.Map.TryGetValue(key, out var sourceButton))
                {
                    // Clone the archetype's whole bound node (preserving the
                    // exact element order TeknoParrot writes), then restore
                    // this game's own display name. The clone carries the
                    // real device + key.
                    var imported = new XElement(sourceButton);
                    var importedName = ChildByLocalName(imported, "ButtonName");
                    if (importedName is not null && buttonName is not null)
                    {
                        importedName.Value = buttonName;
                    }

                    button.ReplaceWith(imported);
                    boundNow++;
                }
                else if (!string.IsNullOrEmpty(buttonName))
                {
                    manual.Add(buttonName);
                }
            }

            var inputApiSet = best.InputApi is not null && SetProfileInputApi(document, best.InputApi);

            // Carry input-behaviour config (aim mode, sensitivity, axis
            // handling) from the archetype, but only fields this target also
            // defines.
            var configCarried = new List<string>();
            foreach (var (field, value) in best.ConfigCarry)
            {
                if (SetConfigField(document, field, value))
                {
                    configCarried.Add(field);
                }
            }

            if (!dryRun)
            {
                SaveProfileDocument(document, path);
            }

            items.Add(ControlPropagationItem.CreateBound(
                code, targetFamily, best.Code, best.InputApi, inputApiSet, boundNow, manual, configCarried, forced,
                GetConfigCarryFlags(targetFamily, best.ConfigCarry)));
        }

        return items;
    }

    // A reference game's bindings are never touched (see the note above
    // PropagateControlsCore). Its Input API may only be corrected when the
    // user has explicitly named, via the canonicalArchetype override, which
    // one reference game in this control type is the correct one -- never a
    // heuristic guess. Ported from teknoparrot-manager commit 64b217c
    // (issue #1 follow-up): lets a user fix a reference game that was itself
    // bound on the wrong Input API, without reintroducing the v0.99.12
    // auto-guess that broke an unrelated, correctly-configured reference
    // game.
    private static void TryCorrectCanonicalArchetypeApi(
        ArchetypeEntry self, List<ArchetypeEntry> pool, ControlOverrides overrides, bool dryRun, List<ControlPropagationItem> items)
    {
        if (!overrides.CanonicalArchetype.TryGetValue(self.Family, out var canonicalCode) || self.Code == canonicalCode)
        {
            return;
        }

        var canonical = pool.FirstOrDefault(s => s.Code == canonicalCode);
        if (canonical?.InputApi is null || canonical.InputApi == self.InputApi)
        {
            return;
        }

        XDocument document;
        try
        {
            document = XDocument.Load(self.Path);
        }
        catch
        {
            return;
        }

        if (document.Root is null || !SetProfileInputApi(document, canonical.InputApi))
        {
            return;
        }

        if (!dryRun)
        {
            SaveProfileDocument(document, self.Path);
        }

        // Update the in-memory pool entry too, not just the file on disk --
        // self is the same object instance referenced by pool, so this is
        // visible to every later iteration of this same run. Without this,
        // a non-archetype profile that propagates from self later in this
        // very run would still copy the now-stale pre-correction InputApi,
        // since the "best" archetype a target propagates from is read from
        // this same pool. Deliberately unconditional on dryRun, same as the
        // original: a dry-run preview should stay internally consistent
        // with itself even though nothing is actually written to disk.
        // Ported from teknoparrot-manager v0.99.20 (issue #1) after a real
        // tester's log showed exactly this: an archetype's correction and
        // another profile propagating from it with the old value happened
        // in the same run.
        self.InputApi = canonical.InputApi;

        items.Add(ControlPropagationItem.ApiFixedCanonical(self.Code, canonical.Code, canonical.InputApi));
    }

    // Resolves which device to recommend for each control family from what
    // the user says they have, preferring the purpose-built control and
    // falling back to the most versatile available. Read-only guidance: it
    // changes nothing. The actual copying happens via PropagateControls
    // after the user binds the recommended reference games and re-runs.
    public static DeviceSurveyPlan RunDeviceSurvey(DeviceSurveyAnswers answers)
    {
        var plan = new List<DeviceSurveyPlanItem>();

        if (answers.HasTrackball)
        {
            plan.Add(new DeviceSurveyPlanItem("Trackball games (Golden Tee, Silver Strike)", "your trackball"));
        }

        if (answers.HasArcade)
        {
            plan.Add(new DeviceSurveyPlanItem("Fighting / classic arcade", "your arcade stick + buttons"));
        }
        else if (answers.HasXbox)
        {
            plan.Add(new DeviceSurveyPlanItem("Fighting / classic arcade", "your Xbox pad"));
        }
        else if (answers.HasKeyboard)
        {
            plan.Add(new DeviceSurveyPlanItem("Fighting / classic arcade", "your keyboard"));
        }

        if (answers.HasWheel)
        {
            plan.Add(new DeviceSurveyPlanItem("Driving games", "your wheel + pedals"));
        }
        else if (answers.HasXbox)
        {
            plan.Add(new DeviceSurveyPlanItem("Driving games", "your Xbox pad (analog steering, triggers, gears)"));
        }
        else if (answers.HasSpinner)
        {
            plan.Add(new DeviceSurveyPlanItem("Driving games", "your spinner for steering, buttons for gas/brake"));
        }

        if (answers.HasGun)
        {
            plan.Add(new DeviceSurveyPlanItem("Lightgun games", "your lightgun"));
        }
        else if (answers.HasTrackball)
        {
            plan.Add(new DeviceSurveyPlanItem("Lightgun games (no gun)", "your trackball (relative/mouse aim)"));
        }
        else if (answers.HasXbox)
        {
            plan.Add(new DeviceSurveyPlanItem("Lightgun games (no gun)", "your Xbox right stick (analog aim)"));
        }

        if (answers.HasXbox)
        {
            plan.Add(new DeviceSurveyPlanItem("All other games", "your Xbox pad"));
        }
        else if (answers.HasArcade)
        {
            plan.Add(new DeviceSurveyPlanItem("All other games", "your arcade stick + buttons"));
        }
        else if (answers.HasKeyboard)
        {
            plan.Add(new DeviceSurveyPlanItem("All other games", "your keyboard"));
        }

        var gunFallbackNote = !answers.HasGun && plan.Any(p => p.GameType.Contains("Lightgun", StringComparison.Ordinal))
            ? "Gun games without a gun aim with mouse/cursor or stick input. Propagation also carries aim-mode settings (relative input, sensitivity, hide-cursor) between gun games, so they match your bound game's settings."
            : null;

        return new DeviceSurveyPlan(plan, gunFallbackNote);
    }
}

// Loaded from the optional controlOverridesPath setting (a JSON file, mirroring
// the original tool's TeknoParrot-Manager.overrides.json), or empty if unset.
public sealed class ControlOverrides
{
    public HashSet<string> NoPropagate { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ForceArchetype { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FamilyOverride { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    // Keyed by control type (button/driving/lightgun/trackball/analog/spinner),
    // valued by the profile code of the one reference game in that type whose
    // Input API is correct. Every other reference game in the same type gets
    // its own Input API corrected to match -- see TryCorrectCanonicalArchetypeApi.
    public Dictionary<string, string> CanonicalArchetype { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public static readonly ControlOverrides Empty = new();
}

// On-disk JSON shape for controlOverridesPath -- only the propagation-relevant
// fields from the original tool's overrides.json; noSync/onlySync/datFile
// belong to other phases and aren't read here.
internal sealed class ControlOverridesFile
{
    public string[]? NoPropagate { get; set; }
    public Dictionary<string, string>? ForceArchetype { get; set; }
    public Dictionary<string, string>? FamilyOverride { get; set; }
    public Dictionary<string, string>? CanonicalArchetype { get; set; }
}

internal sealed record ArchetypeEntry(
    string Code,
    string Path,
    string Family,
    string? InputApi,
    List<string> Devices,
    Dictionary<string, string> ConfigCarry,
    Dictionary<string, XElement> Map,
    int BoundCount)
{
    // Mutable, unlike every other property here: TryCorrectCanonicalArchetypeApi
    // updates this in place after a canonicalArchetype correction, so a later
    // target in the SAME run that picks this entry as its best-match archetype
    // (via the same pool list/dictionary, same object reference) sees the
    // corrected value instead of the stale pre-correction one. See the call
    // site for the full story (ported from teknoparrot-manager v0.99.20, issue #1).
    public string? InputApi { get; set; } = InputApi;
}

public sealed record ControlPropagationItem(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("family")] string? Family = null,
    [property: JsonPropertyName("archetype")] string? Archetype = null,
    [property: JsonPropertyName("archetype_api")] string? ArchetypeApi = null,
    [property: JsonPropertyName("api_set")] bool? ApiSet = null,
    [property: JsonPropertyName("bound")] int? Bound = null,
    [property: JsonPropertyName("manual")] IReadOnlyList<string>? Manual = null,
    [property: JsonPropertyName("config_carried")] IReadOnlyList<string>? ConfigCarried = null,
    [property: JsonPropertyName("forced")] bool? Forced = null,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? Warnings = null)
{
    public static ControlPropagationItem SkippedOverride(string code) => new(code, "skipped-override");
    public static ControlPropagationItem SkippedBound(string code) => new(code, "skipped-bound");
    public static ControlPropagationItem NoArchetype(string code, string family) => new(code, "no-archetype", Family: family);
    public static ControlPropagationItem ApiFixed(string code, string archetype, string? archetypeApi) =>
        new(code, "api-fixed", Archetype: archetype, ArchetypeApi: archetypeApi);
    public static ControlPropagationItem ApiFixedCanonical(string code, string archetype, string? archetypeApi) =>
        new(code, "api-fixed-canonical", Archetype: archetype, ArchetypeApi: archetypeApi);

    public static ControlPropagationItem CreateBound(
        string code, string family, string archetype, string? archetypeApi, bool apiSet, int bound,
        List<string> manual, List<string> configCarried, bool forced, List<string> warnings) =>
        new(code, "bound", family, archetype, archetypeApi, apiSet, bound, manual, configCarried, forced,
            warnings.Count > 0 ? warnings : null);
}

public sealed record ControlPropagationResult(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors,
    [property: JsonPropertyName("items")] IReadOnlyList<ControlPropagationItem> Items,
    [property: JsonPropertyName("backup_path")] string? BackupPath = null);

public sealed record DeviceSurveyAnswers(
    bool HasXbox = false,
    bool HasArcade = false,
    bool HasTrackball = false,
    bool HasSpinner = false,
    bool HasWheel = false,
    bool HasGun = false,
    bool HasKeyboard = false);

public sealed record DeviceSurveyPlanItem(
    [property: JsonPropertyName("game_type")] string GameType,
    [property: JsonPropertyName("bind_with")] string BindWith);

public sealed record DeviceSurveyPlan(
    [property: JsonPropertyName("plan")] IReadOnlyList<DeviceSurveyPlanItem> Plan,
    [property: JsonPropertyName("note")] string? Note);

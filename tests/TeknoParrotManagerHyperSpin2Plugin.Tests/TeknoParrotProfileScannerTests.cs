using System.Text.Json;
using TeknoParrotManagerHyperSpin2Plugin;
using Xunit;

namespace TeknoParrotManagerHyperSpin2Plugin.Tests;

public class TeknoParrotProfileScannerTests
{
    [Fact]
    public void Scan_parses_user_profiles_and_resolves_paths_from_root()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("InitialD8.exe");
        fixture.WriteProfile("ID8", "Initial D8 Infinity", executable);

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        Assert.Empty(result.Errors);
        Assert.Equal(fixture.RootPath, result.RootPath);
        Assert.Equal(fixture.ExecutablePath, result.ExecutablePath);
        Assert.Equal(fixture.UserProfilesPath, result.UserProfilesPath);
        var game = Assert.Single(result.Games);
        Assert.Equal("ID8", game.ProfileName);
        Assert.Equal("Initial D8 Infinity", game.Title);
        Assert.Equal(executable, game.GamePath);
        Assert.Empty(game.Warnings);
    }

    [Fact]
    public async Task Scan_prefers_description_title_and_reports_health_counts()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Batman.exe");
        fixture.WriteDescriptionProfile("Batman", "Batman Arcade", executable, "Batman.exe");
        fixture.WriteProfile("BrokenProfile", "", Path.Combine(fixture.RootPath, "Missing", "missing.exe"));

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);
        var batman = result.Games.Single(game => game.ProfileName == "Batman");

        Assert.Equal("Batman Arcade", batman.Title);
        var healthResponse = await TeknoParrotManagerHyperSpin2PluginMain.ProcessMessage(JsonSerializer.Serialize(new
        {
            method = "execute",
            data = new
            {
                action = "health_check",
                teknoparrotRootPath = fixture.RootPath,
                gamesRootPath = Path.Combine(fixture.RootPath, "Games")
            }
        }));
        var healthJson = JsonSerializer.Serialize(healthResponse);
        Assert.Contains("\"valid_game_paths\":1", healthJson);
        Assert.Contains("\"broken_game_paths\":1", healthJson);
    }

    [Fact]
    public void Scan_falls_back_to_filename_and_reports_missing_game_path()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("BrokenProfile", "", "");

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        var game = Assert.Single(result.Games);
        Assert.Equal("Broken Profile", game.Title);
        Assert.Contains(game.Warnings, warning => warning.Contains("Description and GameName were missing"));
        Assert.Contains(game.Warnings, warning => warning.Contains("GamePath is empty"));
    }

    [Fact]
    public void RegisterGames_creates_missing_user_profile_from_unique_template_without_overwrite()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Initial D Arcade Stage 8", "InitialD8.exe");
        fixture.WriteProfileTemplate("ID8", "Initial D8 Infinity", "InitialD8.exe");

        var preview = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: true);
        Assert.True(preview.Success);
        Assert.True(preview.DryRun);
        Assert.Single(preview.Registered);
        Assert.False(File.Exists(Path.Combine(fixture.UserProfilesPath, "ID8.xml")));

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);
        Assert.True(result.Success);
        Assert.Single(result.Registered);
        Assert.Equal(executable, result.Registered[0].GamePath);
        Assert.Contains("\"success\":true", JsonSerializer.Serialize(result));
        Assert.Contains("\"registered\":", JsonSerializer.Serialize(result));
        var profilePath = Path.Combine(fixture.UserProfilesPath, "ID8.xml");
        Assert.True(File.Exists(profilePath));
        Assert.Contains(executable, File.ReadAllText(profilePath));

        var secondRun = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);
        Assert.Contains("ID8", secondRun.AlreadyRegistered);
        Assert.Empty(secondRun.Registered);
    }

    [Fact]
    public void RegisterGames_uses_fuzzy_folder_match_for_shared_executables()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Initial D8", "game.exe");
        fixture.WriteProfileTemplate("InitialD8", "Initial D8 Infinity", "game.exe");
        fixture.WriteProfileTemplate("OtherSharedGame", "Other Shared Game", "game.exe");

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        var registered = Assert.Single(result.Registered);
        Assert.Equal("InitialD8", registered.Code);
        Assert.Equal("fuzzy", registered.MatchType);
        Assert.Equal(executable, registered.GamePath);
    }

    // Ported from teknoparrot-manager v0.99.19 (issue #15): two candidates
    // that both score at or above the auto-register threshold against the
    // same folder name must not be resolved by which one the loop happened
    // to iterate to last. "DaytonaChampionship" and
    // "DaytonaChampionshipUSADX" both score within FuzzyTieMargin (0.1) of
    // each other against "DaytonaChampionshipUSA" (best ~0.95) -- real
    // values measured against this exact implementation, not hand-picked
    // to merely look plausible.
    [Fact]
    public void SelectProfileCodeByFolderName_rejects_a_near_tie_between_two_strong_candidates()
    {
        var result = TeknoParrotProfileScanner.SelectProfileCodeByFolderName(
            "DaytonaChampionshipUSA", new[] { "DaytonaChampionship", "DaytonaChampionshipUSADX" });

        Assert.Null(result.Code);
    }

    [Fact]
    public void SelectProfileCodeByFolderName_still_auto_registers_a_clear_unambiguous_winner()
    {
        var result = TeknoParrotProfileScanner.SelectProfileCodeByFolderName(
            "InitialD8", new[] { "InitialD8", "InitialD7" });

        Assert.Equal("InitialD8", result.Code);
    }

    [Fact]
    public void RegisterGames_reports_shared_executable_ambiguous_instead_of_guessing_a_near_tie()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteGameExecutable("DaytonaChampionshipUSA", "game.exe");
        fixture.WriteProfileTemplate("DaytonaChampionship", "Daytona Championship", "game.exe");
        fixture.WriteProfileTemplate("DaytonaChampionshipUSADX", "Daytona Championship USA DX", "game.exe");

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        Assert.Empty(result.Registered);
        var ambiguous = Assert.Single(result.Ambiguous);
        Assert.Equal("shared-executable", ambiguous.Reason);
    }

    [Fact]
    public void RegisterGames_uses_dat_index_when_folder_name_does_not_fuzzy_match_any_template()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Mystery Game Folder", "game.exe");
        fixture.WriteProfileTemplate("InitialD8", "Initial D8 Infinity", "game.exe");
        fixture.WriteProfileTemplate("OtherSharedGame", "Other Shared Game", "game.exe");
        var datPath = fixture.WriteEggmanDat(("Mystery Game Folder", "OtherSharedGame", "game.exe"));
        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false, datIndex);

        Assert.True(result.Success);
        var registered = Assert.Single(result.Registered);
        Assert.Equal("OtherSharedGame", registered.Code);
        Assert.Equal("dat", registered.MatchType);
        Assert.Equal(executable, registered.GamePath);
        Assert.Empty(result.Ambiguous);
    }

    [Fact]
    public void RegisterGames_resolves_dat_profile_code_against_profile_set_when_local_filename_differs()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Mystery Game Folder", "game.exe");
        // Local template is filed under "OtherSharedGameNesica" but the dat's own
        // ProfileCode text is the slightly different "OtherSharedGame" -- this only
        // resolves to the real template via fuzzy matching against the known
        // profile-code set, the same way Resolve-ProfileCode does in the original tool.
        fixture.WriteProfileTemplate("OtherSharedGameNesica", "Other Shared Game", "game.exe");
        var datPath = fixture.WriteEggmanDat(("Mystery Game Folder", "OtherSharedGame", "game.exe"));
        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);
        var profileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OtherSharedGameNesica" };

        var result = TeknoParrotProfileScanner.RegisterGames(fixture.Settings, dryRun: false, datIndex, profileSet);

        var registered = Assert.Single(result.Registered);
        Assert.Equal("OtherSharedGameNesica", registered.Code);
        Assert.Equal(executable, registered.GamePath);
    }

    [Fact]
    public void BuildDatIndex_parses_every_game_despite_many_rom_entries()
    {
        using var fixture = new TeknoParrotFixture();
        var manyRoms = Enumerable.Range(0, 50).Select(i => (GameName: $"Game {i}", ProfileCode: $"Code{i}", Executable: "game.exe")).ToArray();
        var datPath = fixture.WriteEggmanDat(manyRoms);

        var datIndex = TeknoParrotProfileScanner.BuildDatIndex(datPath);

        Assert.Equal(50, datIndex.Count);
        Assert.Equal("Code0", datIndex[TeknoParrotProfileScanner.NormalizeGameKey("Game 0")].ProfileCode);
        Assert.Equal("Code49", datIndex[TeknoParrotProfileScanner.NormalizeGameKey("Game 49")].ProfileCode);
    }

    [Fact]
    public void ResolveProfileCode_falls_back_to_fuzzy_match_against_profile_set()
    {
        using var fixture = new TeknoParrotFixture();
        var profileSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AkaiKatanaShinNesica" };

        var resolved = TeknoParrotProfileScanner.ResolveProfileCode("AkaiKatanaShin", fixture.GameProfilesPath, profileSet);

        Assert.Equal("AkaiKatanaShinNesica", resolved);
    }

    [Fact]
    public void RepairGamePaths_uses_first_alternative_with_a_match_instead_of_pooling_across_alternatives()
    {
        using var fixture = new TeknoParrotFixture();
        // Two unrelated files happen to satisfy two different alternative names from
        // the same ExecutableName field: the real game's exe, and a same-named file
        // belonging to a completely different, unrelated game elsewhere in the games
        // root. Repair must use the first alternative with a match (here "Primary.exe")
        // and ignore the other -- pooling matches across all alternatives would see two
        // total candidates and wrongly report this as ambiguous instead of repairing it.
        var correctExecutable = fixture.WriteGameExecutable("DualExeGame", "Primary.exe");
        fixture.WriteGameExecutable("UnrelatedGame", "Secondary.exe");
        fixture.WriteDescriptionProfile("DualExe", "Dual Exe Game", Path.Combine(fixture.RootPath, "Missing", "missing.exe"), "Primary.exe;Secondary.exe");

        var result = TeknoParrotProfileScanner.RepairGamePaths(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        var repair = Assert.Single(result.Repairs);
        Assert.Equal("fixed", repair.Status);
        Assert.Equal(correctExecutable, repair.NewPath);
    }

    [Fact]
    public void RepairGamePaths_updates_broken_profile_when_executable_match_is_unique()
    {
        using var fixture = new TeknoParrotFixture();
        var executable = fixture.WriteGameExecutable("Batman", "Batman.exe");
        fixture.WriteProfileTemplate("Batman", "Batman Arcade", "Batman.exe");
        fixture.WriteDescriptionProfile("Batman", "Batman Arcade", Path.Combine(fixture.RootPath, "Old", "Batman.exe"), "Batman.exe");

        var result = TeknoParrotProfileScanner.RepairGamePaths(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        var repair = Assert.Single(result.Repairs);
        Assert.Equal("fixed", repair.Status);
        Assert.Equal(executable, repair.NewPath);
        Assert.Contains(executable, File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "Batman.xml")));
    }

    [Fact]
    public void Scan_reports_missing_user_profiles_as_error()
    {
        using var fixture = new TeknoParrotFixture(createUserProfiles: false);

        var result = TeknoParrotProfileScanner.Scan(fixture.Settings);

        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, error => error.Contains("UserProfiles folder was not found"));
    }

    [Fact]
    public void BackupProfiles_creates_timestamped_profile_copy()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("ID8", "Initial D8 Infinity", fixture.WriteGameExecutable("InitialD8.exe"));

        var response = TeknoParrotManagerHyperSpin2PluginMain.BackupProfiles(fixture.Settings);
        var backupPath = response.GetType().GetProperty("backup_path")?.GetValue(response)?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(backupPath));
        Assert.True(File.Exists(Path.Combine(backupPath!, "ID8.xml")));
    }

    [Fact]
    public async Task RestoreBackup_preserves_current_profiles_before_overwrite()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.WriteProfile("ID8", "Initial D8 Infinity", fixture.WriteGameExecutable("InitialD8.exe"));
        var backupResponse = TeknoParrotManagerHyperSpin2PluginMain.BackupProfiles(fixture.Settings);
        var backupPath = backupResponse.GetType().GetProperty("backup_path")?.GetValue(backupResponse)?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(backupPath));

        fixture.WriteProfile("ID8", "Changed Title", fixture.WriteGameExecutable("Changed.exe"));
        var message = JsonSerializer.Serialize(new
        {
            method = "execute",
            data = new
            {
                action = "restore_backup",
                teknoparrotRootPath = fixture.RootPath,
                backupPath
            }
        });

        var response = await TeknoParrotManagerHyperSpin2PluginMain.ProcessMessage(message);
        var responseJson = JsonSerializer.SerializeToElement(response);
        var preRestoreBackupPath = responseJson.TryGetProperty("pre_restore_backup_path", out var pathProp) ? pathProp.GetString() : null;

        Assert.False(string.IsNullOrWhiteSpace(preRestoreBackupPath));
        Assert.True(File.Exists(Path.Combine(preRestoreBackupPath!, "ID8.xml")));
        Assert.Contains("Initial D8 Infinity", File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "ID8.xml")));
    }

    [Fact]
    public void PropagateControls_copies_bindings_and_input_api_from_archetype_to_matching_unbound_profile()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.Settings.MinBoundForArchetype = 1;

        var archetypePath = fixture.WriteControlProfile(
            "DrivingArchetype", "RawInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true, DevicePath: "DEV1", BindName: "Axis X", ButtonName: "Steering"));
        var archetypeBefore = File.ReadAllText(archetypePath);

        fixture.WriteControlProfile(
            "DrivingTarget", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: false, ButtonName: "Steering Wheel"));

        var result = TeknoParrotProfileScanner.PropagateControls(fixture.Settings, ControlOverrides.Empty, dryRun: false);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Items, item => item.Code == "DrivingArchetype");
        var target = Assert.Single(result.Items, item => item.Code == "DrivingTarget");
        Assert.Equal("bound", target.Status);
        Assert.Equal("driving", target.Family);
        Assert.Equal("DrivingArchetype", target.Archetype);
        Assert.Equal("RawInput", target.ArchetypeApi);
        Assert.True(target.ApiSet);
        Assert.Equal(1, target.Bound);

        // The archetype itself must never be modified.
        Assert.Equal(archetypeBefore, File.ReadAllText(archetypePath));

        var targetXml = File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "DrivingTarget.xml"));
        Assert.Contains("<DevicePath>DEV1</DevicePath>", targetXml);
        Assert.Contains("<BindName>Axis X</BindName>", targetXml);
        // The target's own display name is preserved, not overwritten by the archetype's.
        Assert.Contains("<ButtonName>Steering Wheel</ButtonName>", targetXml);
        Assert.Contains("<FieldValue>RawInput</FieldValue>", targetXml);
    }

    // Note: there is no test here for the "already bound -> api-fixed" branch
    // in PropagateControlsCore. It is structurally unreachable in practice,
    // by design, inherited from the original tool: pool membership (being an
    // archetype) and "already bound" both use the same minBound threshold,
    // so any profile bound enough to take that branch already qualifies as
    // an archetype itself and gets filtered out by the sourcePaths check
    // first. Confirmed by attempting to construct exactly this scenario --
    // see the original tool's own comment on this at the top of
    // PropagateControlsCore ("a known limitation, not solved this round").

    [Fact]
    public void PropagateControls_respects_noPropagate_override()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.Settings.MinBoundForArchetype = 1;

        fixture.WriteControlProfile(
            "DrivingArchetype", "RawInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        fixture.WriteControlProfile(
            "SkippedDriving", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: false));

        var overrides = new ControlOverrides { NoPropagate = { "SkippedDriving" } };
        var result = TeknoParrotProfileScanner.PropagateControls(fixture.Settings, overrides, dryRun: false);

        var target = Assert.Single(result.Items, item => item.Code == "SkippedDriving");
        Assert.Equal("skipped-override", target.Status);
    }

    [Fact]
    public void PropagateControls_corrects_a_reference_games_own_input_api_when_canonicalArchetype_names_a_different_one()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.Settings.MinBoundForArchetype = 1;

        // Two driving reference games with conflicting Input API settings --
        // the user has decided DrivingCanonical is the correct one.
        var canonicalPath = fixture.WriteControlProfile(
            "DrivingCanonical", "RawInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        var wrongPath = fixture.WriteControlProfile(
            "DrivingWrongApi", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        var canonicalBefore = File.ReadAllText(canonicalPath);

        var overrides = new ControlOverrides { CanonicalArchetype = { ["driving"] = "DrivingCanonical" } };
        var result = TeknoParrotProfileScanner.PropagateControls(fixture.Settings, overrides, dryRun: false);

        var corrected = Assert.Single(result.Items, item => item.Code == "DrivingWrongApi");
        Assert.Equal("api-fixed-canonical", corrected.Status);
        Assert.Equal("DrivingCanonical", corrected.Archetype);
        Assert.Equal("RawInput", corrected.ArchetypeApi);
        Assert.Contains("<FieldValue>RawInput</FieldValue>", File.ReadAllText(wrongPath));

        // The designated canonical reference game itself is never touched.
        Assert.Equal(canonicalBefore, File.ReadAllText(canonicalPath));
        Assert.DoesNotContain(result.Items, item => item.Code == "DrivingCanonical");
    }

    // Ported from teknoparrot-manager v0.99.20 (issue #1): a just-corrected
    // archetype's Input API must be visible to other targets propagating
    // from it in the SAME run, not just written to disk. "DrivingWrongApi"
    // is corrected to RawInput by the canonicalArchetype override above;
    // "ZZZDrivingTarget" (named to enumerate after it) has no overlapping
    // key with the canonical archetype but does overlap with
    // "DrivingWrongApi", so it must propagate the corrected RawInput value,
    // not the stale pre-correction DirectInput one.
    [Fact]
    public void PropagateControls_uses_a_canonical_corrected_archetypes_new_api_for_later_targets_in_the_same_run()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.Settings.MinBoundForArchetype = 1;

        fixture.WriteControlProfile(
            "DrivingCanonical", "RawInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        fixture.WriteControlProfile(
            "DrivingWrongApi", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogY", "Gas", Bound: true));
        var targetPath = fixture.WriteControlProfile(
            "ZZZDrivingTarget", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogY", "Gas", Bound: false));

        var overrides = new ControlOverrides { CanonicalArchetype = { ["driving"] = "DrivingCanonical" } };
        var result = TeknoParrotProfileScanner.PropagateControls(fixture.Settings, overrides, dryRun: false);

        var target = Assert.Single(result.Items, item => item.Code == "ZZZDrivingTarget");
        Assert.Equal("bound", target.Status);
        Assert.Equal("RawInput", target.ArchetypeApi);
        Assert.Contains("<FieldValue>RawInput</FieldValue>", File.ReadAllText(targetPath));
    }

    [Fact]
    public void PropagateControls_leaves_reference_games_alone_when_no_canonicalArchetype_override_is_set()
    {
        using var fixture = new TeknoParrotFixture();
        fixture.Settings.MinBoundForArchetype = 1;

        var firstPath = fixture.WriteControlProfile(
            "DrivingFirst", "RawInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        var secondPath = fixture.WriteControlProfile(
            "DrivingSecond", "DirectInput", new[] { "RawInput", "DirectInput", "XInput" },
            new TeknoParrotFixture.ControlButton("P1AnalogX", "Wheel", Bound: true));
        var firstBefore = File.ReadAllText(firstPath);
        var secondBefore = File.ReadAllText(secondPath);

        var result = TeknoParrotProfileScanner.PropagateControls(fixture.Settings, ControlOverrides.Empty, dryRun: false);

        Assert.Empty(result.Items);
        Assert.Equal(firstBefore, File.ReadAllText(firstPath));
        Assert.Equal(secondBefore, File.ReadAllText(secondPath));
    }

    [Fact]
    public void RunDeviceSurvey_recommends_a_wheel_for_driving_games_when_present()
    {
        var plan = TeknoParrotProfileScanner.RunDeviceSurvey(new DeviceSurveyAnswers(HasWheel: true, HasXbox: true));

        var driving = Assert.Single(plan.Plan, item => item.GameType == "Driving games");
        Assert.Equal("your wheel + pedals", driving.BindWith);
    }

    [Fact]
    public void RunDeviceSurvey_falls_back_to_trackball_for_lightgun_games_without_a_gun()
    {
        var plan = TeknoParrotProfileScanner.RunDeviceSurvey(new DeviceSurveyAnswers(HasTrackball: true));

        var lightgun = Assert.Single(plan.Plan, item => item.GameType == "Lightgun games (no gun)");
        Assert.Equal("your trackball (relative/mouse aim)", lightgun.BindWith);
        Assert.NotNull(plan.Note);
    }

    [Fact]
    public void IsValidPng_accepts_real_signature_and_rejects_garbage()
    {
        using var fixture = new TeknoParrotFixture();
        var folder = fixture.CreateCrosshairsFolder();
        var validPath = fixture.WriteTestPng(folder, "valid.png");
        var garbagePath = Path.Combine(folder, "garbage.png");
        File.WriteAllText(garbagePath, "not a png");

        Assert.True(TeknoParrotProfileScanner.IsValidPng(validPath));
        Assert.False(TeknoParrotProfileScanner.IsValidPng(garbagePath));
    }

    [Fact]
    public void PreviewCrosshairs_lists_valid_pngs_skips_invalid_ones_and_writes_html_preview()
    {
        using var fixture = new TeknoParrotFixture();
        var folder = fixture.CreateCrosshairsFolder();
        fixture.WriteTestPng(folder, "Dot.png");
        fixture.WriteTestPng(folder, "Cross.png");
        File.WriteAllText(Path.Combine(folder, "NotAPng.png"), "garbage");
        fixture.Settings.CrosshairsPath = folder;

        var result = TeknoParrotProfileScanner.PreviewCrosshairs(fixture.Settings);

        Assert.True(result.Success);
        Assert.Equal(new[] { "Cross", "Dot" }, result.Valid.OrderBy(n => n));
        Assert.Contains("NotAPng.png", result.Invalid);
        Assert.NotNull(result.PreviewPath);
        Assert.True(File.Exists(result.PreviewPath));
        var html = File.ReadAllText(result.PreviewPath!);
        Assert.Contains("Dot.png", html);
        Assert.Contains("Cross.png", html);
    }

    [Fact]
    public void DeployCrosshairs_copies_chosen_images_to_the_lightgun_games_folder()
    {
        using var fixture = new TeknoParrotFixture();
        var folder = fixture.CreateCrosshairsFolder();
        fixture.WriteTestPng(folder, "Dot.png");
        fixture.WriteTestPng(folder, "Cross.png");
        fixture.Settings.CrosshairsPath = folder;

        var exePath = fixture.WriteGameExecutable("LethalEnforcers", "game.exe");
        fixture.WriteLightgunProfile("LethalEnforcers", exePath);

        var result = TeknoParrotProfileScanner.DeployCrosshairs(fixture.Settings, "Dot", "Cross", hideCursor: false, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(1, result.Deployed);
        var exeDir = Path.GetDirectoryName(exePath)!;
        Assert.True(File.Exists(Path.Combine(exeDir, "P1.png")));
        Assert.True(File.Exists(Path.Combine(exeDir, "P2.png")));
    }

    [Fact]
    public void DeployCrosshairs_fails_cleanly_when_a_named_crosshair_does_not_exist()
    {
        using var fixture = new TeknoParrotFixture();
        var folder = fixture.CreateCrosshairsFolder();
        fixture.WriteTestPng(folder, "Dot.png");
        fixture.Settings.CrosshairsPath = folder;

        var result = TeknoParrotProfileScanner.DeployCrosshairs(fixture.Settings, "Dot", "DoesNotExist", hideCursor: false, dryRun: false);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("DoesNotExist"));
    }

    [Fact]
    public void HideCursorForLightgunGames_sets_field_only_on_lightgun_profiles_that_define_it()
    {
        using var fixture = new TeknoParrotFixture();
        var exePath = fixture.WriteGameExecutable("HasCursorField", "game.exe");
        fixture.WriteLightgunProfile("HasCursorField", exePath, cursorFieldName: "HideCursor", cursorFieldValue: "0");

        var exePath2 = fixture.WriteGameExecutable("NoCursorField", "game2.exe");
        fixture.WriteLightgunProfile("NoCursorField", exePath2);

        var result = TeknoParrotProfileScanner.HideCursorForLightgunGames(fixture.Settings, dryRun: false);

        Assert.True(result.Success);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, result.NoField);

        var xml = File.ReadAllText(Path.Combine(fixture.UserProfilesPath, "HasCursorField.xml"));
        Assert.Contains("<FieldValue>1</FieldValue>", xml);
    }
}

# Changelog

## 0.11.0

- Add dgVoodoo2 setup (ROADMAP.md Phase 5), porting the original PowerShell tool's `Invoke-DgVoodoo2Setup`/`Get-GameLegacyApi`/`Test-DgVoodoo2UpToDate`. Two new actions: `preview_dgvoodoo2_setup` (dry-run) and `apply_dgvoodoo2_setup`. The DLLs are user-supplied via `dgVoodoo2SourcePath` (same "user already has it" pattern as `crosshairsPath`/`reShadeSourceDllPath`) -- this plugin does not download dgVoodoo2. Zero network calls in this phase, so no new permission entry was needed.
- Detects each game's legacy graphics API usage (DirectX 8, DirectDraw, Glide 2x/3x) by scanning the exe for known DLL imports, then deploys only the DLL(s) that API actually needs (falling back to every available DLL if none of the needed ones are present in the source folder). Existing deployed DLLs are never overwritten.
- Default action scope (no explicit `gameCodes`) only targets games with a detected legacy API, mirroring the original script's "auto-detected games only" selection mode. An explicit `gameCodes` list mirrors its "manual pick" mode, including deploying every available DLL to a named game that has no detected need (giving the user's explicit choice the benefit of the doubt, same as the original).
- Per-game `dgVoodoo.conf` overrides via `dgVoodoo2PresetsPath` (`<ProfileCode>.conf`, always overwrites) alongside the global conf in the source folder (never overwrites an existing deployment) -- same convention as ReShade's preset handling.
- 17 new tests (97/97 passing) covering legacy API detection, the up-to-date check, and deploy/dry-run/overwrite/selection-mode behavior.

## 0.10.0

- Add ReShade setup (ROADMAP.md Phase 4), porting the original PowerShell tool's `Invoke-ReShadeSetup`/`Test-ReShadeDllSignature`/`Get-ReShadeLatestVersion`/`Get-ReShadeTargetInfo`/`Get-GameApiDll`/`Get-ExeArchitecture`. Three new actions: `check_reshade_update` (read-only -- file version + Authenticode signature status + reshade.me version check), `preview_reshade_setup` (dry-run), and `apply_reshade_setup`. The 64-bit ReShade DLL is required (`reShadeSourceDllPath` setting); a 32-bit DLL (`reShadeSourceDll32Path`) and a preset `.ini` (`reShadePresetPath`, with a per-game override folder via `reShadePresetsPath`) are optional. This plugin does not download ReShade itself -- same "user already has it" pattern as `crosshairsPath`.
- Detects each game's exe architecture (32/64-bit, via the PE Optional Header's machine word) and graphics API (DirectX 9/11/12 or OpenGL, by scanning the exe for known DLL imports) to pick the right ReShade DLL and destination filename automatically. OpenParrot games get an `openparrot` subfolder redirect; BudgieLoader games always use `opengl32.dll`.
- Real Authenticode signature verification via the new `AuthenticodeExaminer` NuGet dependency (Windows-only, wraps native Windows trust-verification APIs) -- informational, not a hard gate, matching the original script's reasoning: the user supplied this file themselves, so an invalid/untrusted signature is surfaced loudly but doesn't block setup.
- New `network`/`reshade-version-check` permission for the read-only reshade.me version check -- same risk tier as the existing GitHub profile-list fetch, not a Safety Notes boundary change (the DLL itself is still always user-supplied, never downloaded by this plugin).
- `AuthenticodeExaminer` pulled in a vulnerable transitive `System.Security.Cryptography.Xml` 8.0.1 (NU1903, two known high-severity CVEs) -- pinned an explicit direct reference to 10.0.9 to override it. No vulnerability warnings remain.
- 27 new tests covering exe architecture/API detection, target-folder resolution (openparrot/BudgieLoader special cases), signature status text mapping, the reshade.me version parser, and deploy/dry-run/filtering behavior (80/80 passing).

## 0.9.1

- Add a "Buy Me a Coffee" sponsor link (https://buymeacoffee.com/jumpstile) to README.md/README.txt and the wiki Home page. `.github/FUNDING.yml` already had `buy_me_a_coffee: jumpstile` configured, so GitHub's native Sponsor button already pointed here -- this just makes it visible to anyone reading the docs directly. Entirely optional, never required to use the plugin.

## 0.9.0

- Add GPU compatibility fix (ROADMAP.md Phase 6), porting the original PowerShell tool's `Get-DetectedGpuVendor`/`Get-GpuFixFieldNames`/`Test-GpuFixUpToDate`/`Invoke-GpuFixSetup`. Two new actions: `preview_gpu_fix` (dry-run) and `apply_gpu_fix`. Auto-detects your GPU vendor (AMD/NVIDIA/Intel) via a local WMI query (`Win32_VideoController`, Windows-only -- no-ops to "undetected" on Linux) and toggles the matching vendor-fix field in every registered profile that has one. Field names are discovered by scanning `GameProfiles` at runtime (seeded with the original tool's known field names, extended with whatever else is found), so newly added games with new fix fields are covered without a plugin update. Pure local WMI detection plus profile XML field edits -- no network calls, no new `plugin.json` permission needed (Group A per ROADMAP.md). An explicit `vendor` override can be passed in the action payload for when auto-detection fails or isn't on Windows; the plugin never guesses a vendor on its own.
- `System.Management` added as a new NuGet dependency (first one this project has needed) for the WMI query above.

## 0.8.1

- Simplify the wording on every plugin Settings field and Action (label/description/confirmation text) for a non-technical audience -- no more JSON key names or internal terminology (e.g. `controlOverridesPath`'s description used to enumerate `noPropagate`/`forceArchetype`/`familyOverride`/`canonicalArchetype` directly; now it's a one-line "advanced, leave blank unless a guide told you to use it"). Behavior, setting keys, and action ids are all unchanged -- text only.
- Mark the Eggman/RomVault collection dat as "optional, but recommended" (was just "optional") in the Settings screen, the setup wizard, and both dat-related actions, since it resolves a lot of games this plugin otherwise couldn't.
- Credit Eggman (https://github.com/Eggmansworld/TeknoParrot) for maintaining the collection dat, in the relevant settings/action text and a new README "Credits" section.

## 0.8.0

- Add live download of the Eggman/RomVault collection dat, porting the original PowerShell tool's `Get-EggmanDatRelease`/`Invoke-EggmanDatDownload`. Two new actions: `check_eggman_dat_update` (read-only -- queries `Eggmansworld/TeknoParrot`'s latest GitHub release and reports the asset name/size without downloading) and `download_eggman_dat` (downloads the matching `TeknoParrot*Collection*RomVault*.zip` asset into this plugin's own `EggmanDat` folder). Carries over the original script's safety checks: the release's `browser_download_url` is validated against a `github.com`/`githubusercontent.com` host pattern before fetching, the release filename is sanitized via `Path.GetFileName` and a containment check before it's ever joined into a save path, and the download streams to a `.tmp` file first so an interrupted download never leaves a half-written file at the name `BuildDatIndex` reads from. The dat is data, never executed -- this does not change the "never download/install/run third-party runtime binaries" boundary, just the one specific community data file the original tool also fetched live. Does not auto-update the `eggmanDatPath` setting; the downloaded path is reported back for the user to paste in (or re-run the wizard).
- New permission entry (`network`/`eggman-dat-download`) makes this new outbound call explicit and separate from the existing read-only `external-api` permission, which remains scoped to the small teknogods profile-code list fetch.
- Re-enable the `directory`/`file` settings field types from the (reverted) earlier attempt, now that HyperHQ is shipping native browse-dialog support for them.

## 0.7.0

- Simplify the plugin Settings screen: removed `executablePath`, `userProfilesPath`, `gameProfilesPath`, `iconsPath`, and `backupPath` from `plugin.json`'s top-level `settings` array. All five are pure subfolder overrides of `teknoparrotRootPath` (`TeknoParrotUi.exe`, `UserProfiles`, `GameProfiles`, `Icons`, and `Backups/HyperHQ` respectively) that `Program.cs`'s `ResolvePath`/`ResolveRootPath` already auto-derive when left unset -- nothing changed in that resolution logic, only what the generic Settings page shows. `gamesRootPath` stays, since it points at wherever the user's extracted games actually live and can't be reliably guessed from the TeknoParrot folder.
- These five settings remain settable for non-standard setups via "Run Setup Wizard" -> TeknoParrot Paths, which already labels them "(advanced override)" (since v0.4.0) and writes to the same settings store as the Settings page.
- Tried switching every folder/file path field to HyperHQ's documented `directory`/`file` settings types in hopes of a native browse dialog; confirmed in the actual HyperHQ Settings UI that neither type renders one (plain text box either way), so reverted all of them back to `string`. The HyperHQ plugin docs are inconsistent on this -- one page's example uses `"type": "directory"` in a settings array, but the API reference's type enum doesn't list it at all, and real-world behavior matches the latter. No native path-browsing exists yet in this HyperHQ build; revisit if/when HyperHQ documents and ships it for real.

## 0.6.0

- **Breaking:** renamed the plugin from "TeknoParrot Tools" (id `teknoparrot-tools`) to "TeknoParrot Manager - HyperSpin 2 Plugin" (id `teknoparrot-manager-hyperspin2-plugin`) to match this repository's actual name. Any existing HyperHQ installation will need to remove and re-add the plugin, since HyperHQ identifies plugins by id. The C# project/namespace, test project, executable filename (`TeknoParrotManagerHyperSpin2Plugin.exe`), and release ZIP filename prefix were all renamed to match.
- Fix `plugin.json`'s `author` field, which said the placeholder "HyperSpin Team" -- now "Jumpstile".

## 0.5.0

- Add an optional `canonicalArchetype` field to the control-overrides JSON (`controlOverridesPath`): if two of your already-bound reference games for the same control type disagree on their Input API setting, this lets you name the one that's correct, and the plugin fixes the other one's Input API to match. A reference game's button bindings are still never touched -- only this one Input-API field, and only when you've explicitly said which reference game is right. Ported from teknoparrot-manager commit 64b217c (issue #1 follow-up); does not reintroduce the v0.99.12 heuristic-guess regression noted in 0.3.0 below.
- Simplify control-propagation wording across the plugin UI and docs (settings, action buttons, README) to plain language for first-time users -- "reference game" instead of "archetype" wherever it's user-facing; the JSON override keys themselves (`forceArchetype`, `canonicalArchetype`, etc.) are unchanged for compatibility with the original tool's overrides file format.

## 0.4.0

- Add crosshair deployment (ROADMAP.md Phase 2): deploys a chosen P1/P2 crosshair PNG pair to every registered lightgun game. ElfLdr2 and PCSX2x6 lightgun games share one emulator folder each and are deployed to once regardless of how many profiles use that emulator; PCSX2x6 additionally updates `PCSX2.ini`'s `cursor_path` for both USB ports (backing up the ini first). Standard games get the images copied next to their own executable.
- Bundles the original tool's 321 curated crosshair PNGs in the release package's `Crosshairs/` folder. The optional `crosshairsPath` setting points at a different folder instead if you have your own set.
- Add a crosshair preview action that validates every PNG (checks the real file signature, not just the extension) and writes a browsable HTML grid.
- Add cursor-hide setup (ROADMAP.md Phase 3): sets the cursor-hide field in every registered lightgun profile that defines one, skipping profiles that have no such field or are already set. Can also be run automatically right after deploying crosshairs.
- Simplify the first-run wizard's TeknoParrot Paths step: the four fields that are normally just subfolders of TeknoParrot Folder (`TeknoParrotUi.exe`, `UserProfiles`, `GameProfiles`, `Icons`) are now visually demoted to "(advanced override)" fields at the bottom of the form, since showing all seven fields at once made them look redundant.

## 0.3.0

- Add control binding propagation (ROADMAP.md Phase 1): copies control bindings from a bound reference game ("archetype") to unbound profiles of the same control type (driving/lightgun/trackball/analog/button), matched by button function so a wheel value never lands on a gun. Carries the archetype's Input API and input-behaviour config (aim mode, sensitivity, axis handling) to the target, but only fields the target also defines. Reference games are never modified -- including their own Input API, deliberately, after a real regression in the original tool where "correcting" an archetype's API against an unrelated best-overlap match silently flipped a user's deliberately-configured profile.
- Add an optional `controlOverridesPath` setting (a JSON file) to exclude specific games from propagation, pin a game to a specific archetype, or override its auto-detected control family.
- Add a read-only device survey action that recommends which control to bind for each game type based on what devices you have. Changes nothing.
- Add `minBoundForArchetype` setting (default 5) controlling how many bound buttons a profile needs before it's treated as a reference game for propagation.

## 0.2.0

- Add Eggman/RomVault collection-dat disambiguation to profile registration: folders whose executable is shared by many unrelated titles, and that don't fuzzy-match any candidate profile code, can now be resolved via the dat's authoritative game-name -> ProfileCode mapping. Configured via the new optional `eggmanDatPath` setting (a local .dat or .zip the user already has -- this plugin still does not download third-party files itself).
- Add a live read-only fetch of the full teknogods/TeknoParrotUI profile-code list (falls back to the local GameProfiles listing on any failure) so a dat-resolved ProfileCode that doesn't exactly match a local template filename can still be fuzzy-resolved to the right one.
- Fix: an ambiguous shared-executable report for a folder that a later registration pass went on to resolve cleanly (via a different exe in the same folder, or the new dat/profile-set passes) is no longer surfaced as still needing attention.
- Fix: GamePath repair now tries each alternative executable name in order and stops at the first one with an on-disk match, instead of pooling matches across every alternative -- a profile with multiple alternative names (e.g. "a.exe;b.exe") was being wrongly flagged "ambiguous" whenever an unrelated file elsewhere in the games root happened to share one of the other alternative names.

## 0.1.0

- Add optional TeknoParrot Manager - HyperSpin 2 Plugin with profile scanning, dry-run import preview, HyperHQ system/emulator/game sync, and profile backup/restore actions.
- Align the HyperHQ first-run wizard with the established plugin-page form/action flow and expose a health-check button.
- Add source-aligned profile registration, unique GamePath repair, Description title parsing, profile health counts, and TeknoParrot profile-name launch arguments.

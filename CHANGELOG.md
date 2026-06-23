# Changelog

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

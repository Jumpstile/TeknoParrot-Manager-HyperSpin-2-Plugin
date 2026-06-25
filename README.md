# TeknoParrot Manager - HyperSpin 2 Plugin

![TeknoParrot Manager - HyperSpin 2 Plugin](banner.jpg)

[![Sponsor](https://img.shields.io/badge/Sponsor-Buy%20Me%20a%20Coffee-FFDD00?logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/jumpstile)

Standalone HyperSpin 2 / HyperHQ plugin for TeknoParrot profile maintenance and HyperHQ import.

This repository packages the TeknoParrot tools plugin as its own buildable .NET project with a GitHub Actions release flow. It is intended to complement TeknoParrot and TeknoParrot Manager by exposing the HyperHQ-specific profile and import behavior as an optional plugin instead of baking that behavior directly into HyperHQ.

The implementation was built after reviewing [Jumpstile/teknoparrot-manager](https://github.com/Jumpstile/teknoparrot-manager). It does not copy or bundle that repository's source code.

This is a from-scratch C# port, not a fork -- there's no upstream branch to
merge from. `.github/workflows/watch-upstream.yml` watches that repo's
`main` branch on a daily schedule and opens a tracking issue here (labeled
`upstream-sync`) whenever new commits land, so a human can review and
decide what's worth porting. See ROADMAP.md for what's already ported.

## Table of Contents

- [What It Does](#what-it-does)
- [Glossary](#glossary)
- [Relationship To TeknoParrot Manager](#relationship-to-teknoparrot-manager)
- [HyperHQ Import Contract](#hyperhq-import-contract)
- [Project Layout](#project-layout)
- [Build And Test](#build-and-test)
- [GitHub Releases](#github-releases)
- [HyperHQ Runtime](#hyperhq-runtime)
- [Safety Notes](#safety-notes)
- [Credits](#credits)
- [Support This Project](#support-this-project)

## What It Does

- Validates TeknoParrot folder structure, `TeknoParrotUi.exe`, `UserProfiles`, `GameProfiles`, and `Icons`.
- Scans `UserProfiles/*.xml` and reports valid, broken, empty, and missing `GamePath` values.
- Registers missing user profiles from `GameProfiles/*.xml` templates when a unique executable match is found, a fuzzy folder-name match clears the confidence threshold, or (optionally, but recommended) the Eggman/RomVault collection dat resolves a shared-executable game by name.
- Resolves a dat-provided ProfileCode that doesn't exactly match a local template filename against the live teknogods/TeknoParrotUI profile-code list (falls back to the local GameProfiles listing if unreachable).
- Can check for and download the latest Eggman/RomVault collection dat itself (`check_eggman_dat_update` / `download_eggman_dat`), porting the original PowerShell tool's `Get-EggmanDatRelease`/`Invoke-EggmanDatDownload`. Only runs on explicit user action; the dat is data, never executed. The dat is optional but recommended -- it resolves a lot of games this plugin otherwise couldn't. It's community-maintained by [Eggman](https://github.com/Eggmansworld/TeknoParrot); credit to them for keeping it up to date.
- Repairs broken or empty `GamePath` values only when the matching executable is unambiguous.
- Copies control bindings from one game you've already bound (the "reference game" for that control type -- driving/lightgun/trackball/analog/button) to every other unbound game of the same type, matched by button function so a wheel value never lands on a gun. A reference game's own bindings are never changed by this. An optional control-overrides JSON file (`controlOverridesPath`) can pin a game to a specific reference game, override its auto-detected control type, exclude it from this copying entirely, or -- if two reference games of the same type disagree on their Input API setting -- tell the plugin which one is correct so the other one gets fixed to match (`canonicalArchetype`).
- Offers a read-only device survey that recommends which control to bind for each game type based on what devices you have.
- Deploys a chosen pair of P1/P2 crosshair images (321 bundled, or your own via `crosshairsPath`) to every registered lightgun game, including ElfLdr2 and PCSX2 (with `PCSX2.ini` `cursor_path` updates) special cases, and generates an HTML preview grid to browse them.
- Hides the Windows cursor for every registered lightgun game that defines a cursor-hide field.
- Detects your GPU vendor (AMD/NVIDIA/Intel) via a local WMI query and applies the matching compatibility fix field to every registered profile that has one (`preview_gpu_fix` / `apply_gpu_fix`). Pure local detection plus profile XML edits -- no network calls.
- Installs ReShade (`preview_reshade_setup` / `apply_reshade_setup`) using a user-supplied DLL (this plugin never downloads ReShade itself), auto-detecting each game's exe architecture and graphics API to pick the right DLL and destination filename, with Authenticode signature verification and a read-only `check_reshade_update` version check against reshade.me.
- Installs dgVoodoo2 (`preview_dgvoodoo2_setup` / `apply_dgvoodoo2_setup`) using user-supplied DLLs (this plugin never downloads dgVoodoo2 itself), auto-detecting which registered games use DirectX 8, DirectDraw, or Glide and deploying only the DLL(s) each one needs. Zero network calls.
- Updates an already-installed BepInEx to the latest version (`preview_bepinex_update` / `apply_bepinex_update`), downloaded straight from BepInEx's own official GitHub Releases. Never installs BepInEx for the first time.
- Sets up force feedback (`preview_ffb_blaster_setup` / `apply_ffb_blaster_setup` for TeknoParrot's own FFB Blaster, `preview_ffb_plugin_setup` / `apply_ffb_plugin_setup` for the free, open-source FFB Arcade Plugin covering a different game set). A game covered by both prefers native FFB Blaster by default.
- Installs PostgreSQL 8.3 (`apply_postgres_install`, self-elevated for that one step) and configures/backs up/restores per-game databases (`preview_postgres_game_setup` / `apply_postgres_game_setup` / `backup_postgres_databases` / `restore_postgres_backup`) for the small number of older titles that need it. Windows only.
- Extracts game ZIPs from a configured source folder -- a NAS share or local staging drive -- into your Games Folder (`preview_autosync` / `apply_autosync`), skipping anything already extracted and up to date. Supports an optional second "supplementary" source folder synced the same way. See AutoSync's own section below.
- Backs up and restores profile XML files, including a pre-restore backup before overwrite.
- Creates and syncs the canonical HyperHQ system `Arcade (TeknoParrot)`.
- Imports TeknoParrot profile XML files as launchable HyperHQ games.
- Exposes a HyperHQ first-run wizard and plugin-page buttons for setup, health checks, registration preview, repair preview, control propagation preview, device survey, sync preview, sync, backup, and restore.

## Glossary

Terms used throughout this file and the codebase, in the order you're likely to need them.

- **GameProfile** -- the template TeknoParrot ships for one specific game (e.g. `StreetFighterIII3rdStrike.xml`), living in TeknoParrot's `GameProfiles` folder. Defines what fields/buttons that game has, but isn't itself pointed at your copy of the game.
- **UserProfile** -- your own copy of a `GameProfile`, created when a game is registered. Lives in `UserProfiles`, one file per registered game. Holds the real `GamePath`, control bindings, and per-game settings.
- **Profile Code** -- the filename (without `.xml`) shared by a `GameProfile` and its `UserProfile`, used throughout this plugin's actions and logs to refer to one specific game.
- **GamePath** -- the field inside a `UserProfile` pointing at that game's actual executable on disk. Registration is, at its core, finding the right exe and writing it into this field.
- **Registration** -- matching an extracted game folder to the correct `GameProfile` and creating its `UserProfile` with the right `GamePath`.
- **AutoSync** -- extracts game ZIPs from a configured source folder (a NAS share, a local staging drive) into the games install folder, skipping anything already extracted and up to date.
- **Fuzzy matching** -- how this plugin figures out which `GameProfile` an extracted folder belongs to when the executable name alone is ambiguous (shared by several games), via Dice bigram similarity against each candidate profile's code.
- **Collection Dat** -- an optional community-maintained index (the Eggmansworld TeknoParrot collection) mapping exact ZIP names to the right profile code. Used instead of fuzzy matching when available, since it's exact rather than a best guess.
- **Control propagation** -- this plugin's way of avoiding rebinding every game in a control family by hand: bind one game per type in TeknoParrot's own UI, and this plugin copies those bindings to every other unbound game of that type.
- **Archetype / reference game** -- a game with enough buttons already bound (by you, in TeknoParrot's own UI) to be used as the source for control propagation. Never modified by propagation itself -- only ever a source, never a target.
- **Preview / Apply** -- every action that changes something on disk has a matching preview action (`preview_*`) that reports exactly what would happen first, without writing anything.
- **`gameCodes`** -- an optional list of specific Profile Codes most actions accept to limit themselves to just those games, instead of every eligible game.
- **Group A / Group B** -- this project's own internal classification (see ROADMAP.md), not a TeknoParrot or HyperHQ term: Group A features need no new permission (local-only, or operate on files the user already supplied); Group B features download and run/install a third-party binary, requiring an explicit Safety Notes boundary update and the user's own go-ahead before they ship.

## Relationship To TeknoParrot Manager

TeknoParrot Manager includes many broader Windows setup and game-modification workflows. This plugin intentionally keeps the HyperHQ surface narrower:

- Included: profile discovery, missing profile registration (with dat-index and profile-code fuzzy fallback), unique path repair, control binding propagation, device survey, crosshair deployment, cursor-hide setup, GPU compatibility fix, ReShade setup, dgVoodoo2 setup, BepInEx update check, force feedback setup, PostgreSQL setup, AutoSync (ZIP extraction from a NAS/source folder), health reporting, backups, HyperHQ system/emulator/game import, and wizard/button integration -- every feature on the original project's roadmap for this plugin is now ported, plus AutoSync added after a direct user request. See ROADMAP.md.

That boundary is deliberate. HyperHQ should remain the launcher and library manager, while the plugin extends TeknoParrot support where HyperHQ needs structured profile and import behavior.

## HyperHQ Import Contract

- System name: `Arcade (TeknoParrot)`
- System reference ID: `97d957bb-1490-4c1f-b698-08dd285234a8`
- Allowed extensions: `exe|xml|zip`
- Emulator: `TeknoParrot`
- Emulator command: `--profile="%rom.filename%.xml" --startMinimized`
- Game ROM path: the configured `UserProfiles/*.xml` profile path

## Project Layout

```text
.
|-- TeknoParrotHyperHQPlugin.sln
|-- plugin.json
|-- icon.jpg
|-- banner.jpg
|-- CHANGELOG.md
|-- src/
|   |-- TeknoParrotManagerHyperSpin2Plugin/
|   |   |-- TeknoParrotManagerHyperSpin2Plugin.csproj
|   |   `-- Program.cs
|   `-- HyperHQPluginCommon/
|       |-- HyperImportModels.cs
|       `-- PluginSocketIOClient.cs
`-- tests/
    `-- TeknoParrotManagerHyperSpin2Plugin.Tests/
        |-- TeknoParrotManagerHyperSpin2Plugin.Tests.csproj
        |-- PluginManifestTests.cs
        |-- TeknoParrotFixture.cs
        |-- TeknoParrotImportPayloadTests.cs
        `-- TeknoParrotProfileScannerTests.cs
```

The `src` folder contains all buildable plugin source. `src/HyperHQPluginCommon` contains the small HyperHQ plugin transport/import contract needed to run this project as a standalone repository. `banner.jpg` is repo/wiki decoration only (used at the top of this README) -- it is not part of the release package; `icon.jpg` is the plugin icon HyperHQ actually uses, and is the one bundled into release ZIPs.

## Build And Test

Requirements:

- .NET SDK with `net10.0` support

Commands:

```powershell
dotnet restore .\TeknoParrotHyperHQPlugin.sln
dotnet build .\TeknoParrotHyperHQPlugin.sln
dotnet test .\tests\TeknoParrotManagerHyperSpin2Plugin.Tests\TeknoParrotManagerHyperSpin2Plugin.Tests.csproj
dotnet run --project .\src\TeknoParrotManagerHyperSpin2Plugin\TeknoParrotManagerHyperSpin2Plugin.csproj -- --version
```

Basic stdio smoke test:

```powershell
'{"id":"status","method":"execute","data":{"action":"get_status"}}' | dotnet run --project .\src\TeknoParrotManagerHyperSpin2Plugin\TeknoParrotManagerHyperSpin2Plugin.csproj --no-build
```

## GitHub Releases

The repository includes a GitHub Actions workflow at `.github/workflows/release.yml`.

It runs on:

- Version tags shaped like `v0.1.0`
- Manual `workflow_dispatch`

The workflow uses `plugin.json` as the source of truth. On tag builds, the tag version must match the `version` field in `plugin.json` or the workflow fails.

Release command:

```powershell
git tag v0.2.0
git push origin v0.2.0
```

The workflow restores, tests, publishes a Windows x64 self-contained single-file executable, validates package contents, creates a GitHub release, and uploads:

```text
teknoparrot-manager-hyperspin2-plugin-v0.2.0-win-x64.zip
```

The ZIP contains only the HyperHQ runtime files. `.md` docs are git/dev-facing
only and are deliberately not packaged -- `README.txt`, `CHANGELOG.txt`, and
`QUICKSTART.txt` are the newbie-friendly equivalents that ship instead:

- `TeknoParrotManagerHyperSpin2Plugin.exe`
- `plugin.json`
- `README.txt`, `CHANGELOG.txt`, `QUICKSTART.txt`
- `icon.jpg`
- `Crosshairs/` (321 curated crosshair PNGs used by the crosshair deployment action)
- Any additional root-level `*.json` files, if added later
- Any `Icons/` folder, if added later

## HyperHQ Runtime

The plugin manifest is `plugin.json`. HyperHQ launches `TeknoParrotManagerHyperSpin2Plugin.exe` and communicates over Socket.IO when available, with stdio as the fallback path.

Supported direct methods:

- `initialize`
- `updateSettings`
- `execute`
- `getStatus`
- `get_status`
- `onboardingStepExecute`
- `onboarding/step-execute`
- `shutdown`

Supported execute actions:

- `run_setup_wizard`
- `scan_profiles`
- `scan_games`
- `health_check`
- `get_status`
- `status`
- `preview_registration`
- `check_eggman_dat_update`
- `download_eggman_dat`
- `register_games`
- `repair_game_paths`
- `device_survey`
- `preview_control_propagation`
- `propagate_controls`
- `preview_crosshairs`
- `deploy_crosshairs`
- `hide_cursor`
- `preview_gpu_fix`
- `apply_gpu_fix`
- `check_reshade_update`
- `preview_reshade_setup`
- `apply_reshade_setup`
- `preview_dgvoodoo2_setup`
- `apply_dgvoodoo2_setup`
- `preview_sync`
- `sync_games`
- `backup_profiles`
- `restore_backup`
- `onboardingStepExecute`

## Safety Notes

- Registration and repair support dry-run preview before writing profile XML files.
- Existing user profiles are not overwritten during registration.
- Game path repair writes only when there is a unique executable match.
- Restore creates a pre-restore backup of current profiles before replacing files.
- The optional `eggmanDatPath` setting points at a collection dat -- either one the user already has, or one fetched live via `download_eggman_dat` (see below). Either way, the dat is parsed as data; it is never executed. ReShade and dgVoodoo2 are the same pattern: `reShadeSourceDllPath`/`reShadeSourceDll32Path`/`dgVoodoo2SourcePath` must point at files the user already has -- this plugin never downloads either tool itself, and dgVoodoo2 setup makes no network calls at all.
- BepInEx update check (`preview_bepinex_update`/`apply_bepinex_update`) is the first exception to "never downloads a third-party binary": it downloads BepInEx's official release ZIP from BepInEx's own GitHub Releases and extracts it into a game's folder. It is strictly an UPDATE -- it never installs BepInEx for the first time, and only touches a game that already has a 64-bit BepInEx install (32-bit installs, and games without BepInEx at all, are left untouched). The asset's download URL is host-validated against `github.com`/`githubusercontent.com` before fetching, the release filename is sanitized and containment-checked before being used as a save path, the ZIP is extracted with a zip-slip guard (rejecting any entry that tries to traverse outside the destination folder), and the existing install is backed up before anything is overwritten. The plugin itself never runs the downloaded code -- it only places files; BepInEx's own existing Doorstop/winhttp.dll loader (already present from the prior install) is what loads the update, the next time the game's exe is launched.
- FFB plugin setup (`preview_ffb_plugin_setup`/`apply_ffb_plugin_setup`) is the second exception: it downloads two small DLLs (`MAME32.dll`/`MAME64.dll`) and a live game-compatibility table directly from the free, open-source `mightymikem/FFBArcadePlugin` GitHub repo. The destination filename (read from that live table) is containment-checked before any write, an existing file at the destination is never overwritten, and the plugin itself never runs the downloaded code. FFB Blaster (`preview_ffb_blaster_setup`/`apply_ffb_blaster_setup`, TeknoParrot's own built-in force feedback) is a separate, local-only field toggle with no network calls at all -- it only has an effect with a paid TeknoParrot membership, which this plugin cannot verify, so the action's confirmation message states that prerequisite explicitly instead of guessing. A game covered by both is skipped for the third-party plugin by default (native preferred); an explicit `gameCodes` list overrides that for named games.
- PostgreSQL setup (`apply_postgres_install`, plus the per-game `preview_postgres_game_setup`/`apply_postgres_game_setup`/`backup_postgres_databases`/`restore_postgres_backup` actions) is a different kind of exception from the other two: installing PostgreSQL 8.3 (required by a small number of older Incredible Technologies titles -- Golden Tee Live, Power Putt Live, and similar) runs an MSI installer, creates a Windows service and a local Windows user account, and needs Administrator privileges for that one step -- a UAC prompt elevates only a one-shot re-launch of this plugin's own executable for just that step, never this plugin's normal long-running process or HyperHQ itself. The installer comes from a community repackaging (`Eggmansworld/tp-it-guides` on GitHub, the same source already used for this plugin's Collection Dat) since PostgreSQL 8.3 itself is long discontinued by the PostgreSQL project. The database superuser password is encrypted (Windows DPAPI via `System.Security.Cryptography.ProtectedData`, tied to this Windows user and machine) and saved so you aren't asked for it on every action; it is never logged or included in any action's response. This plugin never installs PostgreSQL if it's already present, never reinstalls over a working install, and its partial-install cleanup logic only ever touches a service, user account, or registry entry it can independently confirm belongs to its own install -- never an unrelated PostgreSQL install at a different path. Windows-only; unsupported on Linux.
- Outside HyperHQ's own Socket.IO channel, the plugin makes six kinds of outbound network calls, all read-only or explicitly user-triggered: a fetch of the public teknogods/TeknoParrotUI profile-code list (fails soft to the local GameProfiles listing on any error); the optional Eggman/RomVault collection dat check/download, which only runs when the user explicitly triggers `check_eggman_dat_update` or `download_eggman_dat` (the download's release filename is sanitized via `Path.GetFileName` plus a containment check before being joined into a save path, and its `browser_download_url` is validated against a `github.com`/`githubusercontent.com` host pattern before being fetched); the optional `check_reshade_update` version check against reshade.me, which never downloads anything, just compares version strings; the optional BepInEx update check/download described above, which only runs when the user explicitly triggers `preview_bepinex_update` or `apply_bepinex_update`; the optional FFB plugin table/DLL fetch described above, which only runs when the user explicitly triggers `preview_ffb_plugin_setup` or `apply_ffb_plugin_setup`; and the optional PostgreSQL installer guide fetch described above, which only runs when the user explicitly triggers `apply_postgres_install`.

## Credits

- The Eggman/RomVault collection dat (used to resolve shared-executable registration matches, and downloadable directly from the plugin) is community-maintained by **Eggman** -- https://github.com/Eggmansworld/TeknoParrot. This plugin does not create or maintain that data, just fetches and reads it.

## Support This Project

This plugin is free to use. If it's been useful to you and you'd like to support continued development: [Buy Me a Coffee](https://buymeacoffee.com/jumpstile). Completely optional -- never required to use any feature of this plugin.

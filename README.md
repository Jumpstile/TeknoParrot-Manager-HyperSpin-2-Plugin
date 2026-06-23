# TeknoParrot Manager HyperSpin 2 Plugin

Standalone HyperSpin 2 / HyperHQ plugin for TeknoParrot profile maintenance and HyperHQ import.

This repository packages the TeknoParrot tools plugin as its own buildable .NET project with a GitHub Actions release flow. It is intended to complement TeknoParrot and TeknoParrot Manager by exposing the HyperHQ-specific profile and import behavior as an optional plugin instead of baking that behavior directly into HyperHQ.

The implementation was built after reviewing [Jumpstile/teknoparrot-manager](https://github.com/Jumpstile/teknoparrot-manager). It does not copy or bundle that repository's source code.

## What It Does

- Validates TeknoParrot folder structure, `TeknoParrotUi.exe`, `UserProfiles`, `GameProfiles`, and `Icons`.
- Scans `UserProfiles/*.xml` and reports valid, broken, empty, and missing `GamePath` values.
- Registers missing user profiles from `GameProfiles/*.xml` templates when a unique executable match is found, a fuzzy folder-name match clears the confidence threshold, or (optionally) an Eggman/RomVault collection dat resolves a shared-executable game by name.
- Resolves a dat-provided ProfileCode that doesn't exactly match a local template filename against the live teknogods/TeknoParrotUI profile-code list (falls back to the local GameProfiles listing if unreachable).
- Repairs broken or empty `GamePath` values only when the matching executable is unambiguous.
- Backs up and restores profile XML files, including a pre-restore backup before overwrite.
- Creates and syncs the canonical HyperHQ system `Arcade (TeknoParrot)`.
- Imports TeknoParrot profile XML files as launchable HyperHQ games.
- Exposes a HyperHQ first-run wizard and plugin-page buttons for setup, health checks, registration preview, repair preview, sync preview, sync, backup, and restore.

## Relationship To TeknoParrot Manager

TeknoParrot Manager includes many broader Windows setup and game-modification workflows. This plugin intentionally keeps the HyperHQ surface narrower:

- Included: profile discovery, missing profile registration (with dat-index and profile-code fuzzy fallback), unique path repair, health reporting, backups, HyperHQ system/emulator/game import, and wizard/button integration.
- Not included in this initial plugin: control propagation, ReShade installation, dgVoodoo2 setup, GPU fixes, FFB setup, Postgres setup, BepInEx deployment, crosshair deployment, and other binary/game-modification operations.

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
|-- icon.svg
|-- CHANGELOG.md
|-- src/
|   |-- TeknoParrotToolsPlugin/
|   |   |-- TeknoParrotToolsPlugin.csproj
|   |   `-- Program.cs
|   `-- HyperHQPluginCommon/
|       |-- HyperImportModels.cs
|       `-- PluginSocketIOClient.cs
`-- tests/
    `-- TeknoParrotToolsPlugin.Tests/
        |-- TeknoParrotToolsPlugin.Tests.csproj
        |-- PluginManifestTests.cs
        |-- TeknoParrotFixture.cs
        |-- TeknoParrotImportPayloadTests.cs
        `-- TeknoParrotProfileScannerTests.cs
```

The `src` folder contains all buildable plugin source. `src/HyperHQPluginCommon` contains the small HyperHQ plugin transport/import contract needed to run this project as a standalone repository.

## Build And Test

Requirements:

- .NET SDK with `net10.0` support

Commands:

```powershell
dotnet restore .\TeknoParrotHyperHQPlugin.sln
dotnet build .\TeknoParrotHyperHQPlugin.sln
dotnet test .\tests\TeknoParrotToolsPlugin.Tests\TeknoParrotToolsPlugin.Tests.csproj
dotnet run --project .\src\TeknoParrotToolsPlugin\TeknoParrotToolsPlugin.csproj -- --version
```

Basic stdio smoke test:

```powershell
'{"id":"status","method":"execute","data":{"action":"get_status"}}' | dotnet run --project .\src\TeknoParrotToolsPlugin\TeknoParrotToolsPlugin.csproj --no-build
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
teknoparrot-tools-v0.2.0-win-x64.zip
```

The ZIP contains only the HyperHQ runtime files:

- `TeknoParrotToolsPlugin.exe`
- `plugin.json`
- `CHANGELOG.md`
- `icon.svg`
- Any additional root-level `*.json` files, if added later
- Any `Icons/` folder, if added later

## HyperHQ Runtime

The plugin manifest is `plugin.json`. HyperHQ launches `TeknoParrotToolsPlugin.exe` and communicates over Socket.IO when available, with stdio as the fallback path.

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
- `register_games`
- `repair_game_paths`
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
- The plugin does not download, install, or modify third-party runtime binaries. The optional `eggmanDatPath` setting points at a collection dat the user already has -- this plugin never fetches it.
- The only outbound network call this plugin makes on its own (outside HyperHQ's own Socket.IO channel) is a read-only fetch of the public teknogods/TeknoParrotUI profile-code list, used solely to resolve dat-based registration matches. It fails soft to the local GameProfiles listing on any error.

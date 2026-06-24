# Roadmap to full TeknoParrot Manager parity

This plugin currently covers profile scan/register/repair/backup/restore
and HyperHQ import (see README's "What It Does"). The original PowerShell
TeknoParrot Manager has eight more features this plugin doesn't have yet.
This file tracks them as phases; check them off and update CHANGELOG.md as
each one lands. CLAUDE.md's "see the approved plan at commit time" line
refers to this file.

## Important boundary to resolve before Phase B starts

**Corrected after reading the actual source** (an earlier version of this
section over-classified several phases as Group B without checking --
verify against `tpm.ps1` before trusting old groupings in this file
again):

- **Group A -- no new permission needed.** Pure XML field manipulation,
  local-only detection (WMI, file inspection), or deploying a file the
  *user* already supplied via a settings path (same pattern as
  `crosshairsPath`). This covers more than it first looks like:
  - **Update (v0.8.0):** `eggmanDatPath` no longer strictly fits this
    description -- the plugin can now optionally fetch the collection dat
    itself (`download_eggman_dat`, ported from the original script's
    `Invoke-EggmanDatDownload`), gated behind its own dedicated
    `network`/`eggman-dat-download` permission. It still doesn't fit
    Group B below, since the dat is data the plugin only ever reads, never
    a binary it runs.
  - GPU compatibility fix -- 100% local (WMI GPU detection + profile field
    toggle), no network at all.
  - dgVoodoo2 setup -- 100% local. The user supplies the DLLs via a
    settings path; the plugin only deploys and detects, never fetches.
  - ReShade setup -- the DLL is also user-supplied. The *only* network
    call is a read-only version-check GET to reshade.me (same risk tier
    as the GitHub profile-list fetch already shipped in v0.2.0) --
    needs one new read-only permission entry, not a boundary change.
  - FFB Blaster (the paid-membership path) -- a confirmation prompt plus
    a profile field toggle, no download.
- **Group B -- downloads and runs a third-party binary the plugin fetches
  itself.** Only three things actually do this:
  - FFB's *free third-party plugin* path downloads a DLL from
    `raw.githubusercontent.com/mightymikem/FFBArcadePlugin`.
  - BepInEx update check downloads a release zip from GitHub's releases
    API.
  - PostgreSQL setup downloads and runs an installer, and creates a
    Windows service and user account.

  These three directly contradict this plugin's README Safety Notes
  line: *"The plugin does not download, install, or modify third-party
  runtime binaries."* That line was a deliberate design boundary in the
  adopted base, not an oversight.

Each of these three needs its own explicit decision before it ships:
update the Safety Notes boundary and add the corresponding `permissions`
entry to `plugin.json` (network + file-write scopes per feature,
mirroring how the dat-index work added an explicit network permission
entry), gated by per-feature settings so a user who never touches FFB's
free plugin, BepInEx, or Postgres never has those permissions exercised.

**Update (v0.12.0):** BepInEx update check is the first of these three to
ship -- the user explicitly agreed to widen the Safety Notes boundary for
it specifically, after reviewing a concrete implementation plan. This
does *not* blanket-approve the other two (FFB's free plugin path,
PostgreSQL setup) -- each still needs its own explicit go-ahead when its
turn comes, same as this one got. Everything else (Group A, plus the rest
of Phase 7 -- FFB Blaster's paid path) has no such conflict and can
proceed without that conversation.

## Phase order

### Phase 1 -- Control propagation (Group A, highest complexity in this group) -- DONE (v0.3.0)
Port `Invoke-ControlPropagation` + `Invoke-DeviceSurvey`
(tpm.ps1:6025, 6194). Binds one reference game per control type in
TeknoParrotUI; copies those bindings to every other profile of the same
type, matched by button function so a wheel axis never lands on a gun.
Has real accumulated regression history worth preserving exactly (see the
archetype-Input-API comment at tpm.ps1:6034-6052 -- a v0.99.12 regression
where "correcting" an archetype's own Input API against the best overlap
match silently flipped a deliberately-configured profile). Needs:
button-node comparison (`Get-ButtonNodes`/`Get-ButtonKey`/
`Test-ButtonIsBound`), archetype pooling (`Build-ArchetypePool`), profile
family/Input-API helpers (`Get-ProfileFamily`, `Get-ProfileInputApi`,
`Set-ProfileInputApi`), and a device-survey wizard step (new `form`/
`async-action` onboarding steps) feeding `noPropagate`/`forceArchetype`/
`familyOverride` overrides. Extended in v0.5.0 with `canonicalArchetype`
(teknoparrot-manager commit 64b217c, issue #1 follow-up): an explicit,
user-named exception to "reference games are never modified" that lets
one reference game per control type be designated correct, so every
other reference game of that type gets its own Input API setting
corrected to match -- never a heuristic guess, to avoid repeating the
v0.99.12 regression.

### Phase 2 -- Crosshair deployment (Group A) -- DONE (v0.4.0)
Port `Invoke-CrosshairSetup` + `Export-CrosshairPreview` (tpm.ps1:2293,
1063). The original ships 321 curated crosshair PNGs in a `Crosshairs/`
folder plus an HTML preview; per the earlier base-adoption decision these
ship as packaged plugin assets under `assets/` (resolved, not revisited
here -- see project memory). Maps to a `selection-list` wizard/action step
since HyperHQ has no native image-preview step type.

### Phase 3 -- Cursor-hide setup (Group A, smallest) -- DONE (v0.4.0)
Port `Invoke-CursorHideSetup` (tpm.ps1:2507). Pure profile XML field
writes (PCSX2 cursor path fields per `Set-Pcsx2CursorPaths`, tpm.ps1:2241)
-- no third-party downloads, no new permissions.

### Phase 4 -- ReShade setup (Group A + one new read-only permission) -- DONE (v0.10.0)
Port `Invoke-ReShadeSetup` + `Test-ReShadeDllSignature` +
`Get-ReShadeLatestVersion`/`Get-ReShadeTargetInfo`/`Get-GameApiDll`/
`Get-ExeArchitecture` (tpm.ps1:1300, 1229, 1262, 1275, 1105, 1132). The
64/32-bit DLLs are user-supplied via a settings path
(`reShadeSourceDllPath`/`reShadeSourceDll32Path`, same "user already has
it" pattern as `eggmanDatPath`) -- this plugin does not download ReShade.
Auto-detects 32/64-bit per game (PE Optional Header machine word) and
graphics API (DirectX 9/11/12 or OpenGL, by scanning for known DLL
imports) to pick the right destination DLL name, with OpenParrot
subfolder and BudgieLoader (always opengl32.dll) special cases. Verifies
the DLL's Authenticode signature before deploying (continues with a
warning if unsigned/untrusted, since the user supplied it themselves) via
the new `AuthenticodeExaminer` NuGet dependency (Windows-only, wraps
native Windows trust-verification APIs -- pulled in a vulnerable
transitive `System.Security.Cryptography.Xml` 8.0.1, pinned to 10.0.9 to
fix). Does one read-only GET to reshade.me to report version drift, gated
by a new `network`/`reshade-version-check` permission entry, mirroring
the existing GitHub profile-list fetch -- not a Safety Notes boundary
change. The interactive game-picker and preset-choice prompts from the
original script don't apply to a non-interactive plugin -- replaced with
an optional `gameCodes` filter in the action payload (default: every
registered game) and settings-driven preset configuration
(`reShadePresetPath` global, `reShadePresetsPath` for per-game overrides,
same `<ProfileCode>.ini` naming convention as the original).

### Phase 5 -- dgVoodoo2 setup (Group A) -- DONE (v0.11.0)
Port `Invoke-DgVoodoo2Setup` + `Get-GameLegacyApi` + `Test-DgVoodoo2UpToDate`
(tpm.ps1:1629, 1579, 1614). DirectX 8/DirectDraw/Glide-to-DX11/12
interception layer. DLLs are user-supplied via a settings path
(`dgVoodoo2SourcePath`); no network calls anywhere in this phase, so no
new `plugin.json` permission was needed. Detects legacy API usage by
scanning each game's exe for known DLL imports, deploys only the DLL(s)
that API needs (falls back to every available DLL if none of the needed
ones are bundled, or if a game is explicitly named via `gameCodes`
despite no detected need -- same "benefit of the doubt" behavior as the
original script's manual-pick path). Existing deployed DLLs are never
overwritten; a per-game `dgVoodoo2PresetsPath` config always overwrites,
the global conf never does -- same convention as ReShade's presets.

### Phase 6 -- GPU compatibility fix (Group A, despite the numbering -- corrected) -- DONE (v0.9.0)
Port `Invoke-GpuFixSetup` + `Get-DetectedGpuVendor` +
`Get-GpuFixFieldNames`/`Test-GpuFixUpToDate` (tpm.ps1:2111, 1798, 1827,
1932). Auto-detects AMD/NVIDIA/Intel via a local WMI query
(`Win32_VideoController`, no network) and applies the matching profile
field fix -- pure XML field toggling, same category as cursor-hide.
Originally miscategorized as Group B in this file; verified against the
source and corrected. No third-party binary involved, no permission
boundary blocker -- pulled forward ahead of Phase 4/5 as planned. Field
names are discovered from `GameProfiles` at runtime (seeded with the
original tool's fallback list, extended with whatever else is found).
`System.Management` added as the project's first NuGet dependency for the
WMI query; guarded with `OperatingSystem.IsWindows()` since the project
also targets `linux-x64` (detection no-ops to "undetected" there, same as
any other GPU-detection failure -- the caller can still pass an explicit
vendor override).

### Phase 7 -- Force feedback (Group B)
Port `Invoke-FFBBlasterSetup` + `Invoke-FFBPluginSetup` +
`Get-FFBPluginGameMap`/`Invoke-FFBPluginDownload` (tpm.ps1:4158, 3913,
3843, 3879). Two independent paths -- TeknoParrot's own FFB Blaster
(requires a paid TeknoParrot membership to function, the plugin can still
deploy the field config) and a free third-party plugin covering a
different game set -- with a conflict-resolution prompt for games covered
by both.

When this phase starts, carry forward this upstream fix too (found via
the sync check, 2026-06-24, before this phase existed -- re-check for
anything newer at that point too): **v0.99.25** split
`Invoke-FFBPluginSetup`'s single "no match" skip counter into two --
`skippedNoMatch` (game isn't in the plugin's supported-games table at
all) and `skippedDllMissing` (game IS matched, but the 32-bit/64-bit
plugin DLL hasn't been downloaded locally yet). The original combined
counter made a fixable "go download the DLL" case look identical to a
genuinely unsupported game. Keep these as two separate result counts in
the C# port's result record.

### Phase 8 -- BepInEx update check (Group B, update-only by design) -- DONE (v0.12.0)
Ported `Invoke-BepInExUpdateCheck` + `Get-BepInExInstalledVersion`/
`Get-BepInExInstalledArch`/`Get-BepInExLatestRelease` into `BepInEx.cs`.
Deliberately never fresh-installs BepInEx, only updates an already-present
64-bit install -- preserved exactly, per the original's own safety choice.
First Group B feature shipped: widened the Safety Notes boundary, added
the `bepinex-update` network permission, and introduced two genuinely new
pieces of shared infrastructure this plugin had never needed before --
`ExtractZipSafe` (zip-slip-safe extraction, `Program.cs`) and
`LogDownloadAudit` (SHA256 download audit logging, `Program.cs`). Two new
actions: `preview_bepinex_update` (dry-run) and `apply_bepinex_update`.

### Phase 9 -- PostgreSQL setup (Group B, highest risk in this group)
Port `Install-Postgres83` + `Invoke-PostgresGameSetup` +
`Test-GameNeedsPostgres`/`Test-PostgresInstalled`/
`Test-PostgresPassword` + `Backup-PostgresDatabases`/
`Invoke-RestorePostgresBackup` (tpm.ps1:3534, 3739, 1997, 2052, 2092,
6653, 6712). For Incredible Technologies titles (Golden Tee Live, Power
Putt Live, etc). Installing PostgreSQL itself creates a Windows service
and a Windows user account, and requires Administrator privileges --
needs the self-elevation helper noted in the original architecture plan
(`ProcessStartInfo { Verb = "runas" }` for just this step, not the whole
plugin process) rather than the original script's "close and re-run as
Administrator" instruction.

When this phase starts, port these two upstream hardenings too (found via
the v0.99.20 -> v0.99.23 sync, 2026-06-24, before this phase existed --
make sure to re-check for anything newer at that point too):
- **Issue #3**: every Postgres helper that currently would set
  `$env:PGPASSWORD` for a psql.exe/pg_dump.exe/etc. call should instead
  write a locked-down (`icacls`) temporary `.pgpass` file and point
  `PGPASSFILE` at it -- avoids the password being visible in the child
  process's own environment block (Task Manager/Process Explorer/WMI) for
  the duration of the call. `New-PostgresPgPassFile`/
  `Remove-PostgresPgPassFile` in the PS source (tpm.ps1 ~2148) are the
  reference implementation.
- **Issue #4**: before any destructive `msiexec /x` uninstall of a
  detected partial/failed PostgreSQL install, cross-check PostgreSQL's own
  `HKLM:\SOFTWARE\PostgreSQL\Installations\*` registry record (written by
  the EnterpriseDB installer, independent of the generic Windows Installer
  Uninstall key) against the expected install directory -- refuse to
  uninstall only on an explicit mismatch (record exists, points elsewhere),
  never on the record's mere absence (a partial install may never have
  reached the stage that writes it). `Test-PostgresInstallationsRegistry`
  in the PS source (tpm.ps1 ~3602) is the reference implementation.
- **v0.99.24** (found in the same sync as above, applied 2026-06-24):
  `Test-PostgresInstallationsRegistry`'s and the partial-install removal
  check's path comparisons were changed from `-like`/`-notlike` to
  `-ieq`/`-ine` -- `-like` is a wildcard pattern match in PowerShell, not
  exact equality, so a literal path comparison using it could
  mis-match/miss if either path ever contained `[`, `]`, or `*`. No direct
  equivalent risk exists in C# (`string.Equals(..., OrdinalIgnoreCase)` is
  already exact, never wildcard), but keep this in mind if any future
  helper here is tempted to reach for a "looks like a path matcher"
  library/regex instead of plain exact-equality comparison.

## Out of scope (confirmed, not revisited)
LaunchBox direct integration, LaunchBox XML export, RetroBat/Batocera
naming mode -- HyperSpin 2 only per the original project decision.

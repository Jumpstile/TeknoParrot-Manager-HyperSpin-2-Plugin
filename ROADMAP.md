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

Before any of those three ship, they need an explicit decision: update
the Safety Notes boundary and add the corresponding `permissions` entries
to `plugin.json` (network + file-write scopes per feature, mirroring how
the dat-index work added an explicit network permission entry), gated by
per-feature settings so a user who never touches FFB's free plugin,
BepInEx, or Postgres never has those permissions exercised. Everything
else above has no such conflict and can proceed without that
conversation -- including the rest of Phase 7 (FFB Blaster's paid path).

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

### Phase 4 -- ReShade setup (Group A + one new read-only permission)
Port `Invoke-ReShadeSetup` + `Test-ReShadeDllSignature` +
`Get-ReShadeLatestVersion`/`Get-ReShadeTargetInfo` (tpm.ps1:1300, 1229,
1262, 1275). The 64/32-bit DLLs are user-supplied via a settings path
(new `reShadeSourceDll`/`reShadeSourceDll32`, same "user already has it"
pattern as `eggmanDatPath`) -- this plugin does not download ReShade.
Auto-detects 32/64-bit per game, verifies the DLL's Authenticode
signature before deploying (continues with a warning if unsigned, since
the user supplied it themselves), and does one read-only GET to
reshade.me to report version drift (add a `network`/`external-api`
permission entry for this, mirroring the existing GitHub profile-list
fetch -- not a Safety Notes boundary change).

### Phase 5 -- dgVoodoo2 setup (Group A)
Port `Invoke-DgVoodoo2Setup` + `Test-DgVoodoo2UpToDate` (tpm.ps1:1578,
1563). DirectX 8/DirectDraw/Glide-to-DX11/12 interception layer. DLLs are
user-supplied via a settings path (`dgVoodoo2SourceDir`); no network
calls anywhere in this phase.

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

### Phase 8 -- BepInEx update check (Group B, update-only by design)
Port `Invoke-BepInExUpdateCheck` + `Get-BepInExInstalledVersion`/
`Get-BepInExInstalledArch`/`Get-BepInExLatestRelease` (tpm.ps1:4375, 4349,
4364, 4310). Deliberately never fresh-installs BepInEx, only updates an
already-present 64-bit install -- preserve that constraint exactly, it's a
safety choice in the original, not a gap.

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

## Out of scope (confirmed, not revisited)
LaunchBox direct integration, LaunchBox XML export, RetroBat/Batocera
naming mode -- HyperSpin 2 only per the original project decision.

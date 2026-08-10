# TeknoParrot Manager RC3 -> HyperSpin 2 Plugin Reconciliation

Evidence captured 2026-08-09; release-checkout validation updated 2026-08-09. This record maps the manager RC3 state to the
HyperSpin 2 plugin without treating the plugin as a source fork or copying
standalone-manager behavior that does not belong in HyperHQ.

## Repository identity and baseline

### TeknoParrot Manager

- Remote: `https://github.com/Jumpstile/teknoparrot-manager.git`
- Local checkout: `//OMVNAS/Arcade/Emulators/TeknoParrot/Scripts`
- Branch: `codex/seo`
- HEAD reviewed: [`35c2657739614434de8ec6aba0c8987c4eb0dd60`](https://github.com/Jumpstile/teknoparrot-manager/commit/35c2657739614434de8ec6aba0c8987c4eb0dd60)
- Parent: `8c2276d793fb483690207641cd8858b15b97fcd7`
- RC3 release tag: [`v1.0-RC3`](https://github.com/Jumpstile/teknoparrot-manager/releases/tag/v1.0-RC3), commit [`df7ebe043a3416440ea439305a99c3d524ceb7c2`](https://github.com/Jumpstile/teknoparrot-manager/commit/df7ebe043a3416440ea439305a99c3d524ceb7c2)
- The reviewed worktree was clean. The current HEAD is post-tag documentation cleanup; it is not the release tag itself.

### HyperSpin 2 plugin

- Remote: `https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin.git`
- Baseline checkout: `work/TeknoParrot-Manager-HyperSpin-2-Plugin`
- Branch: `main`
- Baseline HEAD before this staging pass: [`705d050e70c8e1a9c6fb017653e0d0e15530c45e`](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/commit/705d050e70c8e1a9c6fb017653e0d0e15530c45e)
- Baseline parent: `f6b8828d3f68f6801e3c65f2a62a3b8fa5366d76`
- Existing upstream sync marker: `.github/sync-state/teknoparrot-manager.sha` = `17236ab3e4bb38fd030c85a03d5ed0c160f9b69b`
- The baseline worktree was clean before this local staging pass. Changes below are intentionally local and uncommitted pending maintainer review.

## RC3 range reviewed

The plugin's generated tracking issue [#23](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/issues/23)
covers manager commits `3bac3b8...17236ab`. The manager's later RC3
publication/documentation commits through `35c2657` were also checked so the
release tag and current repository state were not conflated.

| Manager commit | Result for the plugin |
| --- | --- |
| [`b30f19d`](https://github.com/Jumpstile/teknoparrot-manager/commit/b30f19d) / [manager issue #211](https://github.com/Jumpstile/teknoparrot-manager/issues/211) | Applicable subset: PCSX2x6 presence detection, `portable.txt` data-root resolution, initialized-config checks, and the ownership boundary between emulator cursor settings and TPM crosshair PNGs. The standalone interactive first-run action and manager ECVF harness are not ported. |
| [`1be8349`](https://github.com/Jumpstile/teknoparrot-manager/commit/1be8349) | Product-roadmap/architecture documentation separation; manager-only documentation. |
| [`33a14da`](https://github.com/Jumpstile/teknoparrot-manager/commit/33a14da) | RC3 packaging and ECVF guidance; no plugin runtime change. |
| [`96a3954`](https://github.com/Jumpstile/teknoparrot-manager/commit/96a3954) | Manager ContractRegistry collection-boundary test correction, including WinPS 5.1 evidence; no plugin source or test harness port. |
| [`17236ab`](https://github.com/Jumpstile/teknoparrot-manager/commit/17236ab) | Accidental verification file addition; not ported. |
| [`dccc208`](https://github.com/Jumpstile/teknoparrot-manager/commit/dccc208) | Removal of the accidental verification file; not ported. |
| [`df7ebe0`](https://github.com/Jumpstile/teknoparrot-manager/commit/df7ebe0) | Manager RC3 dual-license terms; recorded for rights review, not copied into the from-scratch plugin and not treated as an integration grant. |
| [`6fc09e2`](https://github.com/Jumpstile/teknoparrot-manager/commit/6fc09e2) | RC3 publication documentation; no plugin runtime change. |
| [`ed6a5ce`](https://github.com/Jumpstile/teknoparrot-manager/commit/ed6a5ce) | Repository-description and packaging documentation; no plugin runtime change. |
| [`ac2f8ae`](https://github.com/Jumpstile/teknoparrot-manager/commit/ac2f8ae) | Retired release reference cleanup; no plugin runtime change. |
| [`8c2276d`](https://github.com/Jumpstile/teknoparrot-manager/commit/8c2276d) | Auto-update documentation refresh; no plugin runtime change. |
| [`35c2657`](https://github.com/Jumpstile/teknoparrot-manager/commit/35c2657) | Discoverability documentation; no plugin runtime change. |

## Ported behavior and boundary

The plugin's previous PCSX2x6 crosshair path wrote legacy `cursor_path`
fields under `[USB Port 1 guncon2]` and `[USB Port 2 guncon2]`, and placed
`P1.png`/`P2.png` beside the emulator executable. The staged RC3 adaptation
now:

1. Requires `pcsx2-qtx64.exe` as the PCSX2x6 presence detector.
2. Resolves the data root from `portable.txt`, defaulting to `TeknoParrot`.
3. Rejects rooted or path-traversing data-root values that escape the
   PCSX2x6 folder.
4. Requires `inis/PCSX2.ini` with `[USB1]`, `[USB2]`, and `[JVS]` section
   markers before writing anything.
5. Writes only `DataRoot/crosshairs/P1.png` and `P2.png` when the operation is
   applied.
6. Leaves `PCSX2.ini` byte-for-byte unchanged and does not launch the
   emulator's first-run setup. Unknown or incomplete state fails closed.

The upstream contract evidence is in the manager checkout at
`contracts/pcsx2x6/contract.json` and `contracts/pcsx2x6/evidence.md`. It
pins the emulator source observation to
[`c6e731ac0b9859011d358c021b7e2c9c95296a93`](https://github.com/PS2Homebrew-arcade/pcsx2x6/commit/c6e731ac0b9859011d358c021b7e2c9c95296a93),
defines `portable.txt` as a literal relative data-root leaf with an empty
value defaulting to `TeknoParrot`, and marks the USB cursor settings
`NeverWrite` while keeping crosshair PNG placement TPM-owned.

The plugin remains a from-scratch C# implementation. No manager source,
manager test harness, ECVF contract files, standalone menu workflow, or
manager release/license file is copied or bundled by this change.

## Rights/compliance boundary

The manager's RC3 `LICENSE` and `COMMERCIAL-LICENSE.md` were reviewed as
evidence that integration or redistribution rights must not be inferred from
source review alone. The rights holder then approved standalone distribution
of the HyperSpin 2 plugin v0.16.0 and later, under the same license as
TeknoParrot Manager. The approval is recorded in [Issue #25](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/issues/25), which carries `upstream-sync`, `type:investigation`, `priority:high`, `component:release`, and `status:ready`.

The approved boundary is standalone distribution only. Release packages must
not bundle TeknoParrot Manager source, assets, license files, or branding
beyond factual compatibility references. The plugin's matching terms are in
the repository LICENSE file and the release workflow packages that file.

## Local staged files

- `src/TeknoParrotManagerHyperSpin2Plugin/Crosshairs.cs`
- `tests/TeknoParrotManagerHyperSpin2Plugin.Tests/TeknoParrotProfileScannerTests.cs`
- `.github/workflows/watch-upstream.yml`
- `README.md`
- `README.txt`
- `ROADMAP.md`
- `CHANGELOG.md`
- `docs/UPSTREAM-RC3-RECONCILIATION.md`
- `LICENSE`
- `.github/workflows/release.yml`

Verification results are appended to this record after the focused and full
test commands are run.

## Verification results

- Full W: checkout test run: `dotnet test .\tests\TeknoParrotManagerHyperSpin2Plugin.Tests\TeknoParrotManagerHyperSpin2Plugin.Tests.csproj --verbosity minimal`: **183/183 passed**, 0 failed, 0 skipped.
- The required `dotnet build .\src\TeknoParrotManagerHyperSpin2Plugin\TeknoParrotManagerHyperSpin2Plugin.csproj -warnaserror` gate passes with 0 warnings and 0 errors using the local-output-path workaround; `System.Security.Cryptography.Xml` is 10.0.10 and the live NuGet vulnerability audit reports no vulnerable packages.
- `EnableNETAnalyzers=true` and `AnalysisMode=All` remain enabled. The `.editorconfig` records narrow wire-contract/application-boundary exceptions, while transport correctness and zip containment checks remain covered by code and tests.
- No SecurityCodeScan package/configuration is present in the repository; the external security-review gate remains pending.
- `clawpatch doctor`: passed. The externally run review `20260810T032625-b3fc10` reported 0 findings after reviewing 0 items; no substantive source-review coverage is claimed beyond that result.
- `plugin.json`: version `0.16.0`; metadata and packaged docs are synchronized locally.
- `git diff --check`: passed. Git reported only the repository's normal LF-to-CRLF normalization notices.
- The prior NU1903 advisories were resolved by the 10.0.10 update; no vulnerability warning suppression was added.
- The repository `LICENSE` now matches the approved TeknoParrot Manager license terms; the release workflow copies it into the standalone ZIP.
- Self-contained win-x64 publish succeeded locally for v0.16.0; package validation found one executable, LICENSE, all three packaged TXT docs, icon.jpg, and 321 crosshair PNGs.
## GitHub governance record

- Milestone: [TPM RC3 plugin reconciliation](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/milestone/1), explicitly not a release commitment.
- [Issue #23](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/issues/23) was labeled `upstream-sync`, `type:investigation`, `priority:medium`, `component:sync`, and `status:verified`, then closed as `completed` after the review comment recorded the applicability decision and test evidence.
- [Issue #24](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/issues/24) tracks the code task with `upstream-sync`, `type:enhancement`, `priority:high`, `component:profiles`, `component:export`, and `status:ready`.
- [Issue #25](https://github.com/Jumpstile/TeknoParrot-Manager-HyperSpin-2-Plugin/issues/25) records the rights-holder approval for standalone distribution with `upstream-sync`, `type:investigation`, `priority:high`, `component:release`, and `status:ready`.
- Issues #24 and #25 both relate back to #23; neither is represented as completed by the closure of the review issue.

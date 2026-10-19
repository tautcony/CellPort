# Candidate runtime resource inventory

**Disposition: G0-B blocked; nothing in this inventory is imported into the
CellPort release or deployed to the current module.** This is an audit record
of the external source-audit snapshot, not a compatibility approval.

## Fixed references and observed artifacts

| Resource | Source evidence | Observed artifact | License / distribution | Decision |
|---|---|---|---|---|
| `qdc507_aprv3.ko` | External MaVo snapshot `moluncn/mavo`, tag `v0.1.2`, commit `0443dfdaf8aec086fd76ba2ee9152fd908114524`; its third-party notice points to `the-modem-distro/quectel_eg25_kernel` commit `82ed00908b3e8efc3ff0de27d2b5a7c0524ecd7f` | 36,664 bytes; SHA-256 `3d82d3dec4f1e323201bba87156df9d41438e08314097353f2607f9117211d4a` | GPL-2.0 text is present. Exact corresponding source/build inputs and complete corresponding-source distribution package were not found in the snapshot. | Do not import or redistribute pending source and compliance review. |
| `qdc507_voice.ko` | Same external MaVo tag and v1 resource manifest; no matching build recipe or artifact-specific module report was found | 999,236 bytes; SHA-256 `ed3821682d5309969a01c764192c83feff9669c61ef237c69475cd1619cf296c` | Marked as part of the GPL-2.0 module bundle; exact source/build provenance is unresolved. | Do not import or redistribute. |
| `mavo-pcm-bridge.armv7` | `module/mavo_pcm_bridge.c` and `scripts/build_pcm_bridge_armel.sh` at the same MaVo tag/commit; script pins a Debian builder image digest and cross-toolchain package versions | 17,860 bytes; SHA-256 `88d47c15e61d1428a59c821fed804c2e6490e82859a085062f21966b58d167fc` | MaVo root license is MIT; it dynamically loads the vendor audio library, whose redistribution terms are not established here. | **Incompatible:** its route start and cleanup write `/sys/class/android_usb/f_audio/audio_enable`; this violates the required route-only contract. Do not import or use. |

## Audit findings

- The v1 `Resources/ModuleVoice/manifest.json` has no per-file digest, fixed
  source revision, full firmware/build match, ABI fingerprint, or explicit
  redistribution condition. It cannot be promoted automatically to v2.
- The snapshot's `MODULE-REPORT.md` describes `qdc507_afe.ko` with SHA-256
  `8559986a13485bde6c111f90bcb8a2bd191b4d0517b66b446f944590b41fdacb` and an
  APR artifact with SHA-256
  `9f2cc3d2c376c30588e6334c01de560d2748232f3c069667930c2f90b3697e25`.
  Neither record matches the listed `qdc507_voice.ko` artifact, and the APR
  digest differs from the actual v1 bundle. The report is not accepted as
  provenance for those binaries.
- The helper's reproducible build script is useful source/build evidence, but
  a reproducible binary does not waive its USB side effect or clear the
  vendor audio library's distribution terms.
- No route-only helper source, matching helper ABI, module calibration proof,
  or target-device ELF/ABI report is available. A matching kernel release
  string alone would not close those gaps.

## Required evidence to unblock G0-B

1. Obtain the complete, fixed module source revision and build inputs matching
   each exact `.ko` digest, including modification history and required GPL
   corresponding-source/notice materials.
2. Obtain the vendor library's source or written redistribution terms and the
   exact ARM ABI/loadable dependencies.
3. Build a new helper from reviewable source whose start/stop paths never write
   `functions`, `enable`, or `audio_enable`; audit the complete source and
   generated writes, then record a new digest.
4. Verify target firmware/build, ELF attributes, loader, library ABI, module
   symbols/vermagic and calibration behavior. Keep the profile candidate until
   those artifacts and controlled hardware evidence are complete.

The audit was performed against the local source-audit snapshot at the fixed
MaVo tag/commit above. No external source was copied into this repository and
no binary was added to the CellPort package.

# Flying Probe Device Host Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Report each flying probe computer's latest status to QMS under its configured `device_id`.

**Architecture:** The XP-compatible `IniShareSync.exe` writes one atomic JSON snapshot per device to the existing SCBGX share every minute. The SCBGX collector validates and forwards only new snapshots with its existing QMS credentials; QMS maps `hostName=device_id` through the existing equipment mapping.

**Tech Stack:** Python 3.4-compatible standard library and Windows APIs; Windows Task Scheduler; .NET Framework 4 C#; Vue 3/TypeScript; existing QMS host-status API.

**Spec:** `docs/superpowers/specs/2026-10-22-flying-probe-device-host-status-design.md`

## Global Constraints

- Preserve the existing five-minute INI sync, TXT payloads, pending events and checkpoints.
- Device snapshots go to `target_root/<device_id>/host-status.json`; no QMS token or direct QMS HTTP call on the device.
- Snapshot fields: `schemaVersion=1`, `deviceId`, UTC `sampledAt`, nullable `cpuPercent`, physical memory totals/available bytes, system disk totals/free bytes and `uptimeSeconds`.
- Device status mode runs every 60 seconds independently of file sync; QMS stores latest only and uses existing 300-second offline threshold.
- Keep Python 3.4 and XP 32-bit compatibility without new dependencies; retain the known XP 49.7-day uptime-counter limit in documentation.
- Relay uses `hostName=deviceId`; keep SCBGX's own row separate as a listener computer.

## Review Focus

- A Windows-reserved `device_id` must be rejected by Task 1's config test; two machines whose IDs collide after trim/case folding must be caught by Task 5's rollout audit before either is installed.
- A failed atomic replacement must leave the prior complete snapshot readable; Task 1 tests this.
- An unreadable `D:\WTD\JOBS` must not suppress the separate status mode; Task 1 tests this.
- A mismatched, oversized or malformed shared JSON file must not be forwarded or block valid devices; Task 3 tests this.
- A QMS 500 or timeout must leave the snapshot eligible for the next relay cycle without marking TXT delivery failed; Task 3 tests this.

---

### Task 1: Device-side sample and latest snapshot

**Files (`hr_sync` repository):**
- Create: `ini_share_sync/src/ini_share_sync/host_status.py`
- Modify: `ini_share_sync/src/ini_share_sync/main.py`, `ini_share_sync/src/ini_share_sync/config.py`
- Create: `ini_share_sync/tests/test_host_status.py`
- Modify: `ini_share_sync/tests/test_config_and_discovery.py`

**Interfaces:** `capture_host_status(device_id) -> dict` returns the spec's JSON fields; `write_host_status(target_root, device_id, sample) -> Path` atomically replaces one snapshot; `run_host_status(config_path, application_dir) -> int` returns 0 on success, 1 on error, 3 when its independent lock is held. `main(argv)` dispatches `--status-only` without calling `run_sync`.

- [ ] Add `test_capture_has_v1_fields_and_valid_ranges`, `test_status_mode_uses_configured_device_id_without_source_root`, `test_rejects_windows_reserved_device_id`, and `test_failed_replace_preserves_previous_snapshot` in the listed test files. Assert the exact JSON keys, UTC timestamp, valid CPU and capacity bounds, exit code 0 without `source_root`, a config error for `CON`, `AUX`, trailing dot/space and forbidden characters, and unchanged previous JSON after a mocked replace failure.
- [ ] From `ini_share_sync` in Windows CMD run `set "PYTHONPATH=src" && python -m unittest tests.test_host_status tests.test_config_and_discovery`; expect the new tests to fail because status mode is missing.
- [ ] Implement `capture_host_status(device_id)` and `write_host_status(target_root, device_id, sample)` in `host_status.py`; use XP-compatible `ctypes`, two CPU counter reads, UTF-8 JSON and same-directory atomic replacement. Implement `run_host_status(config_path, application_dir)` with its own lock and error log, and dispatch `main(argv)` on `--status-only` without calling `run_sync`.
- [ ] Run the focused command and `set "PYTHONPATH=src" && python -m unittest discover -s tests` in Windows CMD; expect all tests to pass and the original `run_sync` path to remain unchanged.
- [ ] Commit in `hr_sync`: `feat: capture flying probe computer status by device id`.

### Task 2: Install the one-minute status task

**Files (`hr_sync` repository):**
- Modify: `ini_share_sync/install_sync_task.ps1`, `ini_share_sync/remove_sync_task.ps1`, `ini_share_sync/README.md`
- Modify: `ini_share_sync/tests/test_main.py`

**Interfaces:** `IniShareSyncStatus` invokes the same packaged EXE with `--status-only` every one minute; the existing `IniShareSync` and `IniShareSyncStartup` invocations retain their prior command lines and cadence.

- [ ] Extend `test_task_scripts_create_and_remove_compatible_schtasks_jobs` to assert `IniShareSyncStatus` runs `/SC MINUTE /MO 1` with `--status-only` on XP, uses `<Interval>PT1M</Interval>` and `<Arguments>--status-only</Arguments>` on modern Windows, and is removed by the uninstall script. Extend `test_task_scripts_support_safe_whatif_execution` to assert the new task is not created or removed under `-WhatIf`; assert existing five-minute and startup commands retain their prior arguments.
- [ ] From `ini_share_sync` on Windows run `set "PYTHONPATH=src" && python -m unittest tests.test_main`; expect the new assertions to fail before implementation.
- [ ] Add the status task to both installer branches and removal; document per-machine unique `device_id`, the sidecar location, XP uptime wrap, installation and rollback in `README.md`.
- [ ] Run the focused command and `set "PYTHONPATH=src" && python -m unittest discover -s tests` on Windows; expect all tests to pass.
- [ ] Commit in `hr_sync`: `feat: schedule flying probe computer status separately`.

### Task 3: Relay only new valid device snapshots

**Files (`qms-edge-agent` repository):**
- Create: `tools/flying-probe-collector/HostStatusRelay.cs`
- Modify: `tools/flying-probe-collector/CollectorRuntime.cs`, `tools/flying-probe-collector/build.cmd`, `tools/flying-probe-collector/test.cmd`, `tools/flying-probe-collector/README.md`
- Modify: `tools/flying-probe-collector/CollectorTests.cs`

**Interfaces:** `HostStatusRelay(string sourceRoot, string checkpointPath, Func<HostStatusSample,bool> send, Action<string> log)` exposes `ForwardOnce(Func<bool> shouldStop)`. It reads `host-status.json` in immediate device directories, rejects files over 4096 bytes or invalid schema/identity/ranges, and uses a dedicated `ScanCheckpointStore` keyed by `host-status/<deviceId>` with the SHA-256 of the successful snapshot. `CollectorRuntime` runs it on the existing host worker, separately catching failures from its own SCBGX sample. `HostStatusSender.Send` remains the HTTP boundary.

- [ ] Add `RelayForwardsTwoDeviceIds`, `RelayRejectsInvalidSnapshotAndContinues`, `RelayDoesNotResendAfterRestart`, `RelayRetriesAfterSendFailure`, and `RelayFailureDoesNotStopTxtDelivery` to `CollectorTests.cs`. Assert `HostName` is the directory-matching `deviceId`; reject a missing/incorrect schema, mismatched ID, more than 4096 bytes, invalid timestamp, CPU outside 0–100, nonpositive totals, available/free greater than totals, and negative uptime; assert a valid neighboring device continues. Assert only success records the fingerprint, a fresh relay instance skips unchanged content, and TXT delivery still runs after relay failure.
- [ ] Run `tools\flying-probe-collector\test.cmd` on Windows; confirm the new tests fail for missing relay behavior.
- [ ] Implement `HostStatusRelay` with the specified constructor and `ForwardOnce(Func<bool> shouldStop)`; read only immediate child directories, parse bounded JSON, validate QMS field ranges, compute SHA-256 over accepted file bytes, and persist the fingerprint only after `send(sample)` returns true. In `CollectorRuntime.cs`, create a dedicated checkpoint file next to `settings.CheckpointFile` and call relay once per host-worker cycle with its own exception boundary; add the source to both build scripts and document the sidecar/relay behavior.
- [ ] Run the full collector and GUI tests and build script on Windows; confirm TXT sync and SCBGX local status still work independently.
- [ ] Commit in `qms-edge-agent`: `feat: relay flying probe device computer status`.

### Task 4: Name the QMS source correctly

**Files (`qms` worktree):**
- Modify: `frontend/src/views/EquipmentRuntimeMonitorView.vue`, `frontend/src/views/EquipmentGatewayMappingView.vue`

**Interfaces:** The existing QMS API, database and mapping key are unchanged. The monitor labels `hostName` as a source identifier that may be a device `device_id` or the SCBGX computer name; its online calculation remains based on `receivedAt`.

- [ ] Change only the affected labels and help text; keep the existing mapping and status API calls.
- [ ] Run `npm run build` in `frontend`; confirm Vue/TypeScript compilation passes.
- [ ] Commit in the QMS worktree: `fix: label device host status sources accurately`.

### Task 5: Build and verify the deployment path

**Files:** Release outputs only; preserve each site's `sync_config.json`, INI state, QMS credentials and data files.

**Interfaces:** The device-side package contains the updated EXE and task installer; SCBGX runs the updated collector; QMS runs a JAR including the existing V43 collation migration and Task 4 frontend.

- [ ] Run full Windows tests, build the XP x86 folder package with `build_exe_xp32.ps1`, and smoke-test `IniShareSync.exe --status-only` on a real flying probe computer. Check CPU, memory, disk, task cadence and the `device_id` folder.
- [ ] Audit all target machines' `sync_config.json` values for uniqueness after trim/case folding; create or verify the QMS equipment mapping for each `device_id` before enabling that device's status task.
- [ ] Back up and update QMS and the SCBGX collector, then verify QMS health, a fresh sidecar forwarded once, and the matching equipment shown online. Keep backups available for rollback.
- [ ] Stop or disconnect the test device status task long enough to verify that QMS shows it offline, while SCBGX remains a separate listener row; restart and confirm recovery.
- [ ] Record the exact device/SCBGX/QMS versions and any site machines not yet reachable; do not claim XP field verification from an SCBGX-only test.

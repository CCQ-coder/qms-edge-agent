# Flying Probe Collector Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an independent Windows program that reads flying-probe TXT snapshots and reliably submits them to QMS Telemetry v1.

**Architecture:** A polling scanner turns stable files into normalized events. Two local JSON stores hold pending events and the last enqueued snapshot per file; an HTTP sender removes only accepted events. The program has its own configuration, log, process, and release folder.

**Tech Stack:** C# compatible with the Windows .NET Framework 4.x `csc.exe`; `System.Web.Script.Serialization.JavaScriptSerializer`; `HttpWebRequest`; no new package dependencies.

**Spec:** `docs/superpowers/specs/2026-10-14-flying-probe-collector-design.md`

## Global Constraints

- Read only `\\Scbgx\数字化\飞针机/<设备名>/<料号>.txt` at exactly one directory depth; never modify source files.
- Parse `Test_Counters 2 2 0 01:27:09` as tested, good, bad, last-test-time; keep the fourth value as a string.
- Use file creation time for `attributes.startTime` and modification time for `deviceTime` and `attributes.updatedAt`, formatted `yyyy-MM-ddTHH:mm:ss` in local time.
- Use event type `FLYING_PROBE_TEST_COUNTERS`, `machiningCount` for tested boards, and `materialNo` for the TXT stem.
- Send `{ "events": [event] }` to `POST /api/v1/equipment/telemetry` with `X-Gateway-Token`; Bearer Token is optional in configuration.
- Only HTTP `2xx` removes an event. Scanner and sender must remain independent of the existing `JingdeEapForwarder.exe` process and state.
- Do not stage or alter the already modified `README.md`, `CHANGELOG.md`, `tools/hoson-mes-mock`, or unrelated untracked files while working.

## File Map

- `tools/flying-probe-collector/ProbeRecords.cs`: counter line parser and QMS event construction.
- `tools/flying-probe-collector/LocalStores.cs`: atomic JSON persistence for pending events and scanned-file fingerprints.
- `tools/flying-probe-collector/ProbeScanner.cs`: one-level enumeration, two-scan file stability, and enqueue/checkpoint order.
- `tools/flying-probe-collector/QmsSender.cs`: one-event HTTP POST and per-event retry behavior.
- `tools/flying-probe-collector/Program.cs`: INI loading, logging, polling loop, and process lifecycle.
- `tools/flying-probe-collector/CollectorTests.cs`: executable tests for parsing, mapping, stores, scanning, and HTTP behavior.
- `tools/flying-probe-collector/test.cmd`, `build.cmd`, `start-collector.cmd`, `FlyingProbeCollector.ini`, `README.md`: test, release, operation, and configuration.

## Review Focus

1. A line containing `Test_Counters` with a missing/extra value must produce no QMS event; Task 1 tests this.
2. A file still changing while scanned must not enqueue a partial snapshot; Task 3 tests the two-pass and post-read checks.
3. A corrupt queue or scan-state JSON must stop recovery rather than silently erase data; Task 2 tests this.
4. One QMS `400` response must retain its event without preventing an unrelated event from being sent; Task 4 tests this.
5. Shared-folder loss must preserve existing pending events and checkpoints until access returns; Task 3 tests this.

---

### Task 1: Parse TXT counters and construct QMS events

**Files:** Create `tools/flying-probe-collector/ProbeRecords.cs`, `CollectorTests.cs`, `test.cmd`.

**Interfaces:** Produce `ProbeCounters Parse(string content)` and `Dictionary<string, object> CreateEvent(string machine, string material, string relativePath, DateTime createdLocal, DateTime modifiedLocal, ProbeCounters counters)` on `ProbeRecords`; `ProbeCounters` exposes `Tested`, `Good`, `Bad`, `LastTestTime`.

- [ ] **Step 1: Write failing tests.** In `CollectorTests.cs`, `ParseSample` asserts `2`, `2`, `0`, `"01:27:09"` from the supplied surrounding TXT excerpt; `RejectMalformed` asserts `FormatException` for missing marker, missing/extra value, negative/overflow count, and invalid minute/second; `MapEvent` asserts `machineName`, `materialNo`, `deviceTime`, `machiningCount`, all specified attributes, and equal `recordNo` for identical input but different `recordNo` after a count or timestamp change.
- [ ] **Step 2: Run `test.cmd` and verify red.** It compiles `CollectorTests.cs` with the currently available production sources using `/main:CollectorTests`, then runs the temporary EXE. Expected: parser/mapping tests fail because their behavior is absent.
- [ ] **Step 3: Implement the interfaces in `ProbeRecords.cs`.** Match only a complete whitespace-separated `Test_Counters` line; accept non-negative `Int32` counts and `hours:00-59:00-59`. Build `recordNo` as `FPT-` plus SHA-256 hex of UTF-8 relative path, full-precision creation and modification ticks, and four parsed values. Use the spec's exact QMS field names; no inferred status.
- [ ] **Step 4: Run `test.cmd`; require all Task 1 tests to pass.**
- [ ] **Step 5: Commit only Task 1 files** with message `feat: parse flying probe counter snapshots`.

### Task 2: Persist pending events and scan checkpoints

**Files:** Create `tools/flying-probe-collector/LocalStores.cs`; modify `CollectorTests.cs`, `test.cmd`.

**Interfaces:** Produce `PendingEventStore(string path)`, `bool Enqueue(Dictionary<string, object> eventItem)`, `IList<Dictionary<string, object>> Snapshot()`, `void Remove(string recordNo)`; produce `ScanCheckpointStore(string path)`, `bool Contains(string relativePath, string recordNo)`, `void Record(string relativePath, string recordNo)`.

- [ ] **Step 1: Write failing tests.** `RecoverPending` enqueues event A, constructs a new store, and asserts A remains; duplicate A is not added twice; removing A survives another construction. `RecoverCheckpoint` records path `FPT-01/MAT-1.txt` and a record number, reconstructs the store, and asserts the same pair is recognized and a new record number is not. `RejectCorruptStores` writes malformed JSON at each path and asserts construction fails without rewriting either file. `FailedWriteKeepsMemory` uses an unwritable destination and asserts no in-memory event/checkpoint advance.
- [ ] **Step 2: Run `test.cmd`; require the new store tests to fail.**
- [ ] **Step 3: Implement both stores in `LocalStores.cs`.** Write UTF-8 JSON to a temporary sibling, then atomically move/replace the target on the local filesystem. Apply in-memory changes only after successful persistence. Do not treat an existing malformed file as empty.
- [ ] **Step 4: Run `test.cmd`; require all Task 1–2 tests to pass.**
- [ ] **Step 5: Commit only Task 2 files** with message `feat: persist flying probe delivery state`.

### Task 3: Scan stable files and enqueue each snapshot once

**Files:** Create `tools/flying-probe-collector/ProbeScanner.cs`; modify `CollectorTests.cs`, `test.cmd`.

**Interfaces:** Produce `ProbeScanner(string root, PendingEventStore pending, ScanCheckpointStore checkpoints, Action<string> log)` and `void ScanOnce()`; the scanner uses Task 1 event construction and Task 2 stores. Keep its observed file metadata in memory between scans.

- [ ] **Step 1: Write failing tests.** `FirstScanWaitsForStability` creates `FPT-01/MAT-1.txt`, calls `ScanOnce()` twice, and asserts one event with directory/file names and file timestamps. `UpdatesCreateOneNewEvent` changes the counters and modification time, calls twice, and asserts exactly one additional event; unchanged further scans add none; a new scanner with recovered stores adds none. `IgnoreNestedAndNonTxt` asserts deeper files and non-TXT files are skipped. `RetryInvalidOrChangingFile` asserts a malformed file or changed file metadata between scans adds no event, then a stable corrected file does. `MetadataChangedAfterRead` calls the scanner's metadata comparison with a changed file and asserts false. `MissingRootKeepsState` removes/renames the root and asserts pending/checkpoint data is unchanged; after restoring it, scanning resumes.
- [ ] **Step 2: Run `test.cmd`; require the scanner tests to fail.**
- [ ] **Step 3: Implement `ProbeScanner.ScanOnce()` and `static bool MetadataUnchanged(FileInfo file, long length, DateTime writeUtc)`.** Enumerate immediate child directories and `.txt` files only; compare length/last-write UTC ticks with the prior scan; call `MetadataUnchanged` after reading the file. Queue first, then record the `recordNo` checkpoint. Catch and log per-file read/format failures so other files proceed; preserve stores on root enumeration failure.
- [ ] **Step 4: Run `test.cmd`; require all Task 1–3 tests to pass.**
- [ ] **Step 5: Commit only Task 3 files** with message `feat: poll stable flying probe files`.

### Task 4: Upload independently to QMS and retry failures

**Files:** Create `tools/flying-probe-collector/QmsSender.cs`; modify `CollectorTests.cs`, `test.cmd`.

**Interfaces:** Produce `QmsSender(string baseUrl, string gatewayToken, string bearerToken, int timeoutSeconds, PendingEventStore pending, Action<string> log)` and `void SendPending()`.

- [ ] **Step 1: Write failing tests.** A local TCP HTTP fixture asserts `POST /api/v1/equipment/telemetry`, JSON `events` with exactly one event, `X-Gateway-Token`, and optional `Authorization: Bearer`; HTTP `204` removes that event. `RejectOneContinueNext` serves `400` then `200` and asserts the first remains while the second is removed. `NetworkFailureRetains` asserts an unreachable endpoint leaves the queue unchanged. `NoBearerWhenEmpty` asserts no Authorization header when the optional token is blank.
- [ ] **Step 2: Run `test.cmd`; require the HTTP tests to fail.**
- [ ] **Step 3: Implement `QmsSender.SendPending()`.** Take a queue snapshot and POST each event separately with `HttpWebRequest`; remove only the record number accepted with `2xx`. Log short error details without printing either token. Continue to other pending records after a failed request.
- [ ] **Step 4: Run `test.cmd`; require all Task 1–4 tests to pass.**
- [ ] **Step 5: Commit only Task 4 files** with message `feat: submit flying probe telemetry to QMS`.

### Task 5: Package and run the independent program

**Files:** Create `tools/flying-probe-collector/Program.cs`, `build.cmd`, `start-collector.cmd`, `FlyingProbeCollector.ini`, `README.md`; modify `CollectorTests.cs`, `test.cmd`.

**Interfaces:** `Program.Main(string[] args)` loads an INI next to the EXE (or `--config <path>`), creates independent stores and log, then repeatedly calls `ScanOnce()` and `SendPending()` at the configured interval until Ctrl+C. `Settings.Load(string path)` validates `SourceRoot`, `ScanIntervalSeconds`, `QmsBaseUrl`, `GatewayToken`, `RequestTimeoutSeconds`, `QueueFile`, `CheckpointFile`, and `LogFile`; `BearerToken` may be blank.

- [ ] **Step 1: Write failing tests.** `LoadSettingsRelativeToIni` asserts storage paths resolve against the INI directory even when the current working directory differs, the default source root equals `\\Scbgx\数字化\飞针机`, and placeholder/missing QMS settings fail validation. `BuildReleaseLayout` asserts `build.cmd` produces `FlyingProbeCollector.exe`, its INI, launcher, and README in `outputs/flying-probe-collector` without overwriting an existing site INI.
- [ ] **Step 2: Run `test.cmd`; require the new tests to fail.**
- [ ] **Step 3: Implement `Program.cs` and the release files.** Use the repository's .NET Framework compiler pattern. The sample INI contains placeholder tokens only. The README documents installation, QMS device-name mapping, local files, first-run backfill, and a real-site verification procedure. Keep release state separate from `outputs/jingde-eap-forwarder`.
- [ ] **Step 4: Run `test.cmd`, `build.cmd`, `scripts/build.cmd`, and `powershell -ExecutionPolicy Bypass -File scripts/verify-qms-telemetry-contract.ps1`; require all to pass.** Run `git diff --check` and inspect the release folder.
- [ ] **Step 5: Commit only flying-probe files** with message `feat: package standalone flying probe collector`.

## Completion Check

- Re-read the spec and verify each table field and failure behavior against the implementation.
- Confirm only flying-probe files and the plan were changed by this task; preserve unrelated user work.
- Report that the UNC share and live QMS mapping still require verification on the field computer if they remain inaccessible here.

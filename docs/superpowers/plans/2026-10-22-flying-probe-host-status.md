# Flying Probe Host Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Send the flying probe collector computer's current health to QMS every 60 seconds and keep one latest row per tenant and host.

**Architecture:** QMS gets a dedicated gateway host-status API and a tenant-scoped latest-state table. The Windows collector samples host metrics in a separate worker and posts to the configured QMS URL with its existing credentials; host failures never enter or delay the TXT event queue.

**Tech Stack:** QMS Java 21, Spring Boot 3.5, JdbcTemplate, MySQL/Flyway; collector C#/.NET Framework 4.x, Win32 APIs, `HttpWebRequest`.

**Spec:** `docs/superpowers/specs/2026-10-22-flying-probe-host-status-design.md`

## Global Constraints

- QMS source: `/Users/congee/project/qms` on `main`; collector source: `/Users/congee/project/qms-edge-agent`.
- POST and GET path: `/api/v1/gateway/host-status`; POST uses existing `QmsBaseUrl`, `X-Gateway-Token`, optional Bearer Token and timeout.
- Payload fields and units are exactly those in the spec; `sampledAt` is UTC ISO 8601 with `Z`; `cpuPercent` may be null on the first sample.
- One row per `(tenant_code, host_name)`; no sample history, new dependency, setting, or GUI control.
- Collector starts sampling immediately, then every 60 seconds; host-status failure cannot stop or delay TXT synchronization.

## Review Focus

1. Invalid `Authorization` alongside a valid gateway token must fail, never fall back to gateway-only access (Task 2 test).
2. Identical host names in different tenants must remain separate, and GET must read only the current tenant (Task 1 and 2 tests).
3. Missing or impossible metrics, especially available bytes greater than total bytes, must fail without a database write (Task 1 and 2 tests).
4. First CPU sample, unchanged counters, and counter reset must not divide by zero or send a non-finite percentage (Task 3 test).
5. Redirect, timeout, or disk-read failure must not stop TXT delivery or leak either token to logs (Task 4 and 5 tests).

---

### Task 1: QMS latest-state storage

**Files:**
- Create: `/Users/congee/project/qms/backend/src/main/resources/db/migration/V39__add_gateway_host_status.sql`
- Create: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/integration/api/GatewayHostStatusRequest.java`
- Create: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/integration/api/GatewayHostStatusResponse.java`
- Create: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/integration/application/GatewayHostStatusApplicationService.java`
- Test: `/Users/congee/project/qms/backend/src/test/java/com/pcbqms/integration/application/GatewayHostStatusApplicationServiceTest.java`

**Interfaces:** `GatewayHostStatusApplicationService(JdbcTemplate)` produces `void record(GatewayHostStatusRequest request)` and `List<GatewayHostStatusResponse> listLatest()`. Request fields: `String hostName`, `OffsetDateTime sampledAt`, nullable `BigDecimal cpuPercent`, and `Long` for the five byte/uptime values; response returns UTC `Instant sampledAt` plus server `Instant receivedAt`.

- [ ] **Step 1: Write failing tests.** `recordRejectsImpossibleMetrics`: available > total or negative value throws before JDBC update. `upsertUsesTenantAndNormalizedHost`: captured JDBC arguments contain `TenantContext.tenantCode()` and uppercase host; SQL is an upsert. `listLatestScopesTenant`: query receives only the current tenant and maps `receivedAt`.
- [ ] **Step 2: Verify red.** In QMS `backend`, run `mvn -Dtest=GatewayHostStatusApplicationServiceTest test`; expect compilation failure because request/service types are absent.
- [ ] **Step 3: Implement.** Migration creates `qms_gateway_host_status` with composite primary key `(tenant_code, host_name)`, exact metric columns, `sampled_at`, `received_at`; service trims and uppercases host with `Locale.ROOT`, validates cross-field byte relationships, and performs one `INSERT ... ON DUPLICATE KEY UPDATE` with `JdbcTemplate`, reading and writing through `TenantContext.tenantCode()`. Request uses Bean Validation for host length 1–160, nonnull sampled time, CPU 0–100, positive memory/disk totals, nonnegative available bytes and uptime. GET response uses the same metric names plus `receivedAt`.
- [ ] **Step 4: Verify green.** Run the focused test and `mvn verify`; expect zero failures.
- [ ] **Step 5: Commit QMS files.** Stage only Task 1's five listed paths; run `git diff --cached --check`, then `git commit -m "feat: store latest gateway host status"`.

### Task 2: QMS routes and gateway security

**Files:**
- Create: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/integration/api/GatewayHostStatusController.java`
- Modify: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/config/ProductionSecurityConfiguration.java`
- Modify: `/Users/congee/project/qms/backend/src/main/java/com/pcbqms/common/tenancy/GatewayTelemetryTenantContextFilter.java`
- Modify: `/Users/congee/project/qms/backend/src/test/java/com/pcbqms/config/ProductionSecurityConfigurationTest.java`
- Modify: `/Users/congee/project/qms/backend/src/test/java/com/pcbqms/common/tenancy/GatewayTelemetryTenantContextFilterTest.java`
- Create: `/Users/congee/project/qms/backend/src/test/java/com/pcbqms/integration/api/GatewayHostStatusControllerTest.java`
- Modify: `/Users/congee/project/qms/README.md`

**Interfaces:** `POST /api/v1/gateway/host-status` calls `GatewayIntegrationAuthentication.verify(token)` then `record(@Valid GatewayHostStatusRequest)` and returns HTTP 204; authenticated `GET` calls `listLatest()` and returns `ApiResponse<List<GatewayHostStatusResponse>>` for the current tenant.

- [ ] **Step 1: Write failing tests.** Controller tests assert POST validates required fields and token, GET returns response rows. Extend security/filter tests for valid gateway-only POST, invalid/missing token, inactive tenant, invalid `Authorization` with valid gateway token, and gateway-only GET rejection.
- [ ] **Step 2: Verify red.** Run `mvn -Dtest=GatewayHostStatusControllerTest,ProductionSecurityConfigurationTest,GatewayTelemetryTenantContextFilterTest test`; expect new route tests to fail.
- [ ] **Step 3: Implement.** Add controller; permit only the new POST through production HTTP authorization, extend existing gateway tenant filter to the new POST path, preserve JWT tenant handling when `Authorization` is present. Document request, response, credentials and server-first rollout in QMS README.
- [ ] **Step 4: Verify green.** Run focused tests and `mvn verify`; expect zero failures.
- [ ] **Step 5: Commit QMS files.** Stage only Task 2's listed paths; run `git diff --cached --check`, then `git commit -m "feat: accept gateway host status"`.

### Task 3: Windows host sampling

**Files:**
- Create: `tools/flying-probe-collector/HostStatus.cs`
- Modify: `tools/flying-probe-collector/CollectorTests.cs`
- Modify: `tools/flying-probe-collector/build.cmd`
- Modify: `tools/flying-probe-collector/test.cmd`

**Interfaces:** `HostStatusSample` holds the eight payload fields with `DateTime SampledAtUtc` and nullable `double CpuPercent`; `HostStatusSampler.Capture(): HostStatusSample` retains the previous CPU counters between calls. `CpuTimes` holds `ulong Idle`, `Kernel`, and `User`; `HostStatusSampler.CalculateCpuPercent(CpuTimes? previous, CpuTimes current): double?` handles their deltas.

- [ ] **Step 1: Write failing tests.** Synthetic CPU counter deltas assert 0–100 percent, first/zero-delta/reset samples yield null; Windows `Capture()` asserts nonempty host, UTC time, positive memory/disk totals, available ≤ total, nonnegative uptime.
- [ ] **Step 2: Verify red.** On Windows run `tools\flying-probe-collector\test.cmd`; expect missing sampler/sample type compilation failure.
- [ ] **Step 3: Implement.** Use `GetSystemTimes`, `GlobalMemoryStatusEx`, `GetTickCount64`, `Environment.MachineName`, and `DriveInfo` for the system drive; keep CPU delta math in one testable method. Add `HostStatus.cs` to both EXE builds and tests, without new assemblies or config.
- [ ] **Step 4: Verify green.** Run `test.cmd`; expect all collector and GUI tests to pass.
- [ ] **Step 5: Commit collector files.** Stage only Task 3's listed paths; run `git diff --cached --check`, then `git commit -m "feat: sample collector host status"`.

### Task 4: Host-status HTTP delivery

**Files:**
- Create: `tools/flying-probe-collector/QmsHttpPoster.cs`
- Create: `tools/flying-probe-collector/HostStatusSender.cs`
- Modify: `tools/flying-probe-collector/QmsSender.cs`
- Modify: `tools/flying-probe-collector/CollectorTests.cs`
- Modify: `tools/flying-probe-collector/build.cmd`
- Modify: `tools/flying-probe-collector/test.cmd`

**Interfaces:** `QmsHttpPoster.Post(string path, object body, out int status): string` owns existing timeout, token headers, no-redirect and response behavior; `HostStatusSender.Send(HostStatusSample sample): bool` posts one sample and logs failures without credentials.

- [ ] **Step 1: Write failing tests.** Local HTTP fixture asserts endpoint, ISO UTC time, numeric units, gateway/Bearer headers and blank-Bearer omission; redirects, non-2xx and timeout return failure without following login redirects or exposing tokens. Existing `QmsSender` tests pin unchanged file-event behavior.
- [ ] **Step 2: Verify red.** On Windows run `test.cmd`; expect missing sender type or wrong endpoint failure.
- [ ] **Step 3: Implement.** Move existing `QmsSender.Upload` HTTP mechanics into `QmsHttpPoster` without changing file event serialization; use it for host status, reject non-2xx, catch network/IO errors and log only safe status/error class. Add new files to both build scripts.
- [ ] **Step 4: Verify green.** Run `test.cmd`; expect all tests to pass.
- [ ] **Step 5: Commit collector files.** Stage only Task 4's listed paths; run `git diff --cached --check`, then `git commit -m "feat: send collector host status to QMS"`.

### Task 5: Independent scheduling, stop, and release

**Files:**
- Modify: `tools/flying-probe-collector/CollectorRuntime.cs`
- Modify: `tools/flying-probe-collector/CollectorTests.cs`
- Modify: `tools/flying-probe-collector/GuiTests.cs`
- Modify: `tools/flying-probe-collector/README.md`
- Modify: `docs/qms-telemetry-contract.md`

**Interfaces:** `HostStatusWorker.Run(WaitHandle stop, TimeSpan interval, Action tick, Action<string> log): void` calls `tick` immediately and after each wait, logging exceptions; runtime starts it in a second long-running task with a 60-second interval. `StopAsync()` and `Completion` await both workers while the TXT worker retains ownership of the queue lock.

- [ ] **Step 1: Write failing tests.** Runtime with a path-aware local HTTP fixture asserts initial host POST, failed host POST still allows TXT event success, and stop waits for any in-flight host request but starts no next request. Adapt existing runtime and GUI slow-request fixtures to route responses by path. Test `HostStatusWorker.Run` with a short interval and a throwing `tick`: it logs the error, continues, and stops without waiting 60 seconds. Test that a fatal TXT worker failure also ends the host worker so the window exits.
- [ ] **Step 2: Verify red.** On Windows run `test.cmd`; expect runtime behavior tests to fail before the second worker exists.
- [ ] **Step 3: Implement.** Add the independent host worker, catch sampling and delivery errors within it, share the stop event, include it in `Completion` and `StopAsync()`, and signal stop if either worker exits fatally. Retain existing queue lock/error behavior. Document rollout, log behavior and latest-only semantics in collector README and the local contract.
- [ ] **Step 4: Verify green and release.** On Windows run `tools\flying-probe-collector\test.cmd`, `tools\flying-probe-collector\build.cmd`, `scripts\verify-qms-telemetry-contract.ps1`, and `git diff --check`; on QMS run `mvn verify`. Check a staging MySQL migration/upsert and one live Windows sample; report any unavailable checks explicitly.
- [ ] **Step 5: Commit collector files.** Stage only Task 5's listed paths; run `git diff --cached --check`, then `git commit -m "feat: schedule flying probe host status reporting"`.

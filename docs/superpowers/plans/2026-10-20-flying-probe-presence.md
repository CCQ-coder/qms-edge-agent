# 飞针机控制电脑在线与 TXT 活跃状态 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 QMS 根据采集电脑对飞针机控制电脑的网络探测显示在线状态，并在 TXT 持续更新时显示运行中，避免无新测试数据时误报离线。

**Architecture:** 采集器把 TXT 扫描、待发补传和网络探测分成独立循环，网络状态与最新 TXT 变化通过不排队的网关请求上报。QMS 在现有 V34 运行通道表中保存网络、共享目录和 TXT 活跃三路状态，并在设备实时列表中优先计算飞针机状态；旧遥测继续负责测试计数，不决定飞针机在线。

**Tech Stack:** C#/.NET Framework 4.x/WinForms 与现有 `csc.exe` 脚本；Java 21/Spring Boot/MyBatis-Plus/MySQL；Vue 3/TypeScript/Node 22。

**Spec:** `docs/superpowers/specs/2026-10-20-flying-probe-presence-design.md`

## Global Constraints

- 本计划跨两个仓库：`tools/flying-probe-collector/**` 属于本采集器仓库；`backend/**`、`frontend/**`、`scripts/package_windows.py` 属于 QMS 仓库。下文采集器裸文件名均位于 `tools/flying-probe-collector/`，QMS Java 裸文件名均位于 `backend/src/main/java/com/pcbqms/equipment/` 对应包。QMS 从 `42ea39ac` 建独立工作区，采集器从本分支执行；不混入 QMS 主工作区尚未发布的 V35/V36。
- “在线”仅证明控制电脑网络可达；“运行中”还要求采集器近 10 分钟实际观察到 TXT 元数据变化。默认探测 60 秒、连续 3 次失败判离线、状态上报 5 分钟失效；未配置主机名或扫描链路失效显示“未确认”。
- 主机名每轮重新解析，允许 DHCP 换 IP；不假设设备目录名就是主机名。`FlyingProbeCollector.hosts.json` 与 INI 同目录，发布时保留现场文件。
- 状态 POST 为 `/api/v1/equipment/presence/flying-probe`，只使用 `X-Gateway-Token` 和固定租户；任何 `Authorization` 请求头均被拒绝。网关模式下原 TXT POST 也不发送旧 Bearer。
- 状态上报不进入现有待发队列或同步历史，失败仅保留最新状态下轮重试；旧 TXT 补传不得改变飞针机在线状态。保留现有计数、去重、历史与非飞针机在线规则。
- QMS 只复用 V34 的 `qms_equipment_runtime_channel`，来源固定为 `__FPT_NETWORK__`、`__FPT_SOURCE__`、`__FPT_TXT_ACTIVITY__`；不新增 Flyway 迁移。带偏移的观察时间只转换一次到上海时区，过期判定使用 QMS 接收时间。
- 用户自行部署 QMS 发布包；实施阶段只构建、验证并交付新包，不连接生产设备或写入生产 QMS。

## Review Focus

1. 映射 JSON 损坏或主机名留空时，TXT 仍同步且设备为“未确认”，不能用旧 IP 冒充在线；Task 4、7 测试。
2. DHCP 换址、Ping 被禁而 TCP 可用、单台 DNS 超时，其他设备的探测仍按时完成；Task 6 测试。
3. UNC 扫描卡住时网络探测仍继续、目录状态在 5 分钟后变“未确认”；7000 条待发请求不阻断扫描或探测；Task 5、7 测试。
4. 有效或过期 Bearer 与网关令牌同时存在时，状态接口不能进入 JWT 租户，采集器不能发送 Bearer；Task 1、7 测试。
5. 历史 TXT 补传、失序观察和只有状态通道而无遥测的设备，不能造成错误在线、漏行或旧 `legacyState` 覆盖；Task 1、2、3 测试。

## File Map

- QMS 接入：新建 `backend/src/main/java/com/pcbqms/equipment/api/FlyingProbePresenceRequest.java`、`FlyingProbePresenceResponse.java`、`FlyingProbePresenceController.java`；新建 `equipment/application/FlyingProbePresenceApplicationService.java`；新建 `equipment/domain/EquipmentRuntimeChannel.java`、`equipment/infrastructure/persistence/EquipmentRuntimeChannelMapper.java`。三个既有安全配置/过滤器仅增加精确 POST 路由。
- QMS 展示：新建 `equipment/application/FlyingProbePresenceStatusResolver.java`；修改 `EquipmentRuntimeStatusApplicationService.java`、`EquipmentRuntimeStatusResponse.java`、`EquipmentTelemetryRecordMapper.java`、`frontend/src/types/qms.ts`、`frontend/src/utils/display-text.ts`、`frontend/src/views/EquipmentRuntimeMonitorView.vue`。
- 采集器：新建 `DeviceHosts.cs`、`ScanSummary.cs`、`NetworkProbe.cs`、`PresenceState.cs`、`PresenceSender.cs`、`DevicePresenceView.cs`、`FlyingProbeCollector.hosts.json` 模板和 `package.ps1`；修改 `Program.cs`、`ProbeScanner.cs`、`CollectorRuntime.cs`、`LocalStores.cs`、`SyncHistory.cs`、`QmsSender.cs`、`CollectorForm.cs`、`build.cmd`、`test.cmd`、`README.md` 与现有测试文件。

---

### Task 1: QMS 固定租户状态接口与单调存储

**Files:** QMS 新建 `backend/src/main/java/com/pcbqms/equipment/{api/FlyingProbePresenceRequest.java,api/FlyingProbePresenceResponse.java,api/FlyingProbePresenceController.java,application/FlyingProbePresenceApplicationService.java,domain/EquipmentRuntimeChannel.java,infrastructure/persistence/EquipmentRuntimeChannelMapper.java}`；修改 `config/ProductionSecurityConfiguration.java`、`common/tenancy/JwtTenantContextFilter.java`、`common/tenancy/GatewayTelemetryTenantContextFilter.java`；新增 `backend/src/test/java/com/pcbqms/equipment/application/FlyingProbePresenceApplicationServiceTest.java`，扩充对应三个安全测试类。

**Interfaces:** `FlyingProbePresenceRequest(String machineName, OffsetDateTime observedAt, String networkState, String sourceState, OffsetDateTime lastTxtChangeAt)`；`FlyingProbePresenceResponse(boolean accepted, String equipmentId)`；`FlyingProbePresenceApplicationService.receive(request)` 通过 `mappingService.resolve(null, machineName)` 取固定租户设备。`EquipmentRuntimeChannelMapper.upsertIfNewer(channel): int` 和 `selectReservedByTenant(tenantCode): List<EquipmentRuntimeChannel>` 供 Task 2 使用。

- [ ] **Step 1: 写失败测试。** `gatewayPresencePostUsesFixedTenant` 断言只有正确网关令牌和已配置活跃租户返回 2xx；`presenceWithBearerIsRejected` 断言即使 JWT 有效且属于另一租户也返回 401；GET 和其他 POST 仍需原有认证。服务测试断言未知映射不能写入、`2026-10-20T09:00:00+08:00` 正确转成本地时间、观察时间偏离接收时间超过 5 分钟被拒绝、TXT 时间晚于探测时间被拒绝、旧 observedAt 返回 `accepted=false` 且不覆盖新行、无 TXT 变化时仅写网络/来源两行、状态上报不调用遥测写入。
- [ ] **Step 2: 运行针对性 Maven 测试确认 RED。** 在 QMS 的 `backend` 运行 `mvn -B -Dtest=ProductionSecurityConfigurationTest,GatewayTelemetryTenantContextFilterTest,JwtTenantContextFilterTest,FlyingProbePresenceApplicationServiceTest test`；预期新增断言失败。
- [ ] **Step 3: 实现认证入口。** 仅为新 POST 增加 `permitAll`/JWT 跳过/网关过滤器匹配；显式拒绝任何 `Authorization` 头；Controller 再校验 `X-Gateway-Token`，保持原 `/telemetry` 认证行为。
- [ ] **Step 4: 实现请求、服务和 V34 通道读写。** 校验两种状态枚举、观察时间与当前时间偏差；以事务和 MySQL 唯一键条件更新保证旧时间不能覆盖新值，网络与目录行每次写，TXT 行仅在 `lastTxtChangeAt` 前进时写；`last_received_at` 使用服务端时间。
- [ ] **Step 5: 运行同一测试确认 GREEN，提交 QMS 改动。** 预期新增和原安全测试通过；提交 `feat: accept flying probe gateway presence`。

### Task 2: QMS 运行状态合成与无遥测设备并表

**Files:** QMS 新建 `backend/src/main/java/com/pcbqms/equipment/application/FlyingProbePresenceStatusResolver.java`、`backend/src/test/java/com/pcbqms/equipment/application/EquipmentRuntimeStatusApplicationServiceTest.java`；修改 `EquipmentRuntimeStatusApplicationService.java`、`equipment/api/EquipmentRuntimeStatusResponse.java`、`equipment/infrastructure/persistence/EquipmentTelemetryRecordMapper.java`。

**Interfaces:** `FlyingProbePresenceStatusResolver.resolve(network, source, txt, now, ttl, activityWindow): FlyingProbePresenceStatusResolver.PresenceDecision`，其中 `PresenceDecision` 给出 `effectiveStatus`、`online`、最后探测和 TXT 变化时间；`EquipmentTelemetryRecordMapper.selectFlyingProbeEquipmentIds(tenantCode): List<String>` 按事件类型 `FLYING_PROBE_TEST_COUNTERS` 查询，用于无通道旧设备的“未确认”回退。响应新增 `statusSource`（`FLYING_PROBE` 或 `TELEMETRY`）、`lastProbeAt`、`lastTxtChangeAt`；原字段保持兼容。

- [ ] **Step 1: 写失败测试。** 覆盖网络/目录上报在第 300 秒失效、UNKNOWN/UNREADABLE、连续失败后的 OFFLINE、ONLINE 且 600 秒内 TXT 变化为 RUNNING、活动超窗为 ONLINE；仅有通道无遥测的设备仍出现在 `list()`，无通道但有飞针机事件历史为 UNKNOWN；延迟 TXT 补传不改变网络结论；非飞针机仍按原 300 秒阈值和权限过滤。历史设备 ID 缓存按租户隔离且不会每 5 秒重查全表。
- [ ] **Step 2: 运行 `mvn -B -Dtest=EquipmentRuntimeStatusApplicationServiceTest test` 确认 RED。** 预期缺少新合成逻辑导致断言失败。
- [ ] **Step 3: 实现合成。** 批量读取租户运行行、三个保留通道和飞针机历史设备 ID，历史 ID 结果按租户缓存 10 分钟，避免监控页每次刷新扫描遥测表；合并设备 ID 后复用原租户及工序权限检查。飞针机只看三通道，其他设备保留 `EquipmentRuntimeStatusResponse.from`。配置键 `qms.flying-probe.presence-ttl-seconds` 默认 300、`qms.flying-probe.activity-window-seconds` 默认 600；对 presence-only 行允许旧业务字段为空。
- [ ] **Step 4: 运行针对性测试确认 GREEN，提交 QMS 改动。** 在 `backend` 运行 `mvn -B -Dtest=EquipmentRuntimeStatusApplicationServiceTest test`；提交 `feat: derive flying probe runtime status from presence`。全量 Maven 验证留到 Task 8。

### Task 3: QMS 页面显示真实飞针机状态

**Files:** QMS 修改 `frontend/src/types/qms.ts`、`frontend/src/utils/display-text.ts`、`frontend/src/views/EquipmentRuntimeMonitorView.vue`；新增 `frontend/src/utils/runtime-status-choice.ts`、`frontend/tests/runtime-status-choice.test.ts`。

**Interfaces:** `chooseRuntimeLabel(statusSource: string | undefined, effectiveLabel: string, legacyLabel: string | undefined): string`；飞针机返回有效状态文字，其他设备沿用旧 `legacyState` 优先逻辑。

- [ ] **Step 1: 写失败测试。** Node 测试断言 `chooseRuntimeLabel('FLYING_PROBE','运行中','离线') === '运行中'`、普通设备仍返回旧文案，并断言 `statusText` 把 RUNNING/ONLINE/OFFLINE/UNKNOWN 映射为“运行中/在线/离线/未确认”；在线计数在 Task 2 后端测试确认。
- [ ] **Step 2: 在 QMS `frontend` 运行 `npm ci`，再运行 `node --experimental-strip-types --test tests/runtime-status-choice.test.ts` 与 `npm run build` 确认 RED。** Node 22 为既定版本；预期缺少工具函数或响应字段时报错。
- [ ] **Step 3: 实现类型与页面。** 增加 RUNNING/UNKNOWN 标签和标记颜色；飞针机行显示状态来源、最后探测、最后 TXT 变化；在线数依据后端 `online`；调整仅写精雕/锣机和统一离线阈值的说明；时区显示复用已转换的本地时间，不再额外加 8 小时。
- [ ] **Step 4: 运行 Node 测试与 `npm run build` 确认 GREEN，并用四状态假响应检查页面，提交 QMS 改动。** 提交 `feat: show flying probe presence in runtime monitor`。

### Task 4: 采集器设备主机名配置

**Files:** 采集器新建 `tools/flying-probe-collector/DeviceHosts.cs`、`FlyingProbeCollector.hosts.json`；修改 `Program.cs`、`build.cmd`、`test.cmd`、`CollectorTests.cs`。

**Interfaces:** `DeviceHosts.Load(string path): DeviceHosts`、`Save(string path): void`；`HostTarget` 保存目录名、主机名、探测方式 ICMP/TCP 和 TCP 端口；`DeviceHosts.ProbeIntervalSeconds` 默认为 60、`FailureThreshold` 默认为 3。`Settings.HostsFile` 总是解析为 INI 同目录的 `FlyingProbeCollector.hosts.json`；缺失文件载入空映射，损坏文件保留原内容并让 TXT 采集继续。

- [ ] **Step 1: 写失败测试。** `HostsFollowConfigDirectory` 覆盖 `--config`；`BlankHostLoadsWithoutChangingIniValidation`、`InvalidTcpPortRejected`、`BrokenJsonDoesNotReplaceSavedMap`、`ReleaseBuildPreservesHosts` 断言空映射安全、无效值被拒绝、失败保存不改磁盘和已生效配置、构建不覆盖现场文件；坏映射下 TXT 继续运行的跨组件断言放在 Task 7。
- [ ] **Step 2: 运行 `& .\tools\flying-probe-collector\test.cmd` 确认 RED。** 预期新测试编译或断言失败。
- [ ] **Step 3: 实现映射格式与路径。** 使用现有 `AtomicJson.Write` 原子写；每轮加载最新已保存映射，主机名不解析时保持 UNKNOWN；`build.cmd` 仅在输出目录不存在映射时复制空模板，编译脚本均加入新源文件。
- [ ] **Step 4: 运行 `test.cmd` 与 `build.cmd` 确认 GREEN，提交采集器改动。** 提交 `feat: configure flying probe host mappings`。

### Task 5: 扫描摘要与并发队列

**Files:** 采集器新建 `tools/flying-probe-collector/ScanSummary.cs`；修改 `ProbeScanner.cs`、`LocalStores.cs`、`SyncHistory.cs`、`CollectorRuntime.cs`、`Program.cs`、`CollectorTests.cs`、`build.cmd`、`test.cmd`。

**Interfaces:** `ScanSummary.ObserveFile(machine, file, length, lastWriteUtc, observedUtc)` 首见只建基线；`MarkReadable(machine, observedUtc)`、`MarkUnreadable(machine, observedUtc)`、`Snapshot(nowUtc)` 返回最近扫描成功、来源状态及最近 TXT 变化。`ProbeScanner` 保留现有构造器，新增接受 `ScanSummary` 的重载。`CollectorRuntime.Completion` 和 `StopAsync()` 覆盖所有后台循环。

- [ ] **Step 1: 写失败测试。** 首次见旧文件无 TXT 活跃、元数据变化记录本地观察时间、目录列举报错即 UNREADABLE、单个 TXT 解析失败不改变目录可读状态、超过 300 秒无成功扫描为 UNKNOWN；慢 HTTP 补传期间扫描继续发现第二个文件；并发 `Enqueue`/`Remove` 不丢事件；停止后所有循环结束才释放独占锁。
- [ ] **Step 2: 运行 `test.cmd` 确认 RED。** 预期扫描摘要及并发断言失败。
- [ ] **Step 3: 实现摘要和分离循环。** 扫描器每设备报告目录结果与文件戳；`PendingEventStore` 用同一锁保护快照、持久化、替换内存列表；`SyncHistory` 在追加锁内串行化；扫描与发送各自循环，独占锁在所有循环退出后释放。同步日志写入也串行化，避免多个工作线程写同一文件冲突。
- [ ] **Step 4: 运行 `test.cmd` 与 `build.cmd` 确认 GREEN，提交采集器改动。** 提交 `refactor: scan independently from pending delivery`。

### Task 6: 独立主机探测和状态机

**Files:** 采集器新建 `tools/flying-probe-collector/NetworkProbe.cs`、`PresenceState.cs`；修改 `CollectorRuntime.cs`、`CollectorTests.cs`、`build.cmd`、`test.cmd`。

**Interfaces:** `IHostProbe.Probe(HostTarget target, int timeoutMilliseconds, Func<bool> shouldStop): bool?`（null 为取消/无配置）；`PresenceTracker(int failureThreshold).Observe(string machine, bool? reachable, DateTime observedUtc): string` 返回 ONLINE/OFFLINE/UNKNOWN；`PresenceReport` 含 machineName、observedAt、networkState、sourceState、lastTxtChangeAt。生产探测每次先解析主机名，ICMP 为默认，TCP 连接仅用配置端口。

- [ ] **Step 1: 写失败测试。** 首次 1～2 败为 UNKNOWN、第 3 败 OFFLINE、成功恢复立即 ONLINE、成功后 1～2 败仍 ONLINE；假 DNS 首轮与次轮给不同 IP 时实际探测新 IP；禁 Ping 可切 TCP；单设备解析/探测超时不阻塞其他设备；停止信号不产生新的探测。
- [ ] **Step 2: 运行 `test.cmd` 确认 RED。** 预期缺少新探测接口和状态机。
- [ ] **Step 3: 实现有界并发。** 每台探测含 DNS 的总预算默认 3000 毫秒、最多 4 台并行；超时后忽略迟到结果，未完成任务不得为同一设备重复堆积新任务；网络任务只读取 Task 5 摘要，不访问 UNC；配置缺失直接 UNKNOWN。
- [ ] **Step 4: 运行 `test.cmd` 与 `build.cmd` 确认 GREEN，提交采集器改动。** 提交 `feat: probe flying probe control hosts`。

### Task 7: 独立状态上报与旧 Bearer 清理

**Files:** 采集器新建 `tools/flying-probe-collector/PresenceSender.cs`；修改 `QmsSender.cs`、`CollectorRuntime.cs`、`Program.cs`、`CollectorTests.cs`、`build.cmd`、`test.cmd`。

**Interfaces:** `PresenceSender.Send(PresenceReport report): PresenceSendResult` 单次 POST 到新接口；`PresenceSendResult` 含 HTTP 状态、`accepted` 和错误摘要。运行器每轮把最新状态覆盖内存旧状态，不调用 `PendingEventStore` 或 `SyncHistory`；`QmsSender.Upload` 在 GatewayToken 非空时不附加 Authorization。

- [ ] **Step 1: 写失败测试。** 本地 HTTP 夹具断言状态 JSON 字段、带偏移时间、只含网关头、无 `events[]` 包装；模拟 401/超时后状态队列不增长且下一轮只发送最新值，错误日志不含网关令牌；旧 INI Bearer 非空时两个 POST 均不发送 Authorization；损坏或空白主机映射不阻断 TXT 同步；TXT 上报仍写原历史、成功才删除待发事件；慢状态请求不阻塞扫描。
- [ ] **Step 2: 运行 `test.cmd` 确认 RED。** 更新旧 `AcceptedPostRemovesEvent` 对 Bearer 的断言，预期新请求/认证断言失败。
- [ ] **Step 3: 实现状态请求与运行器第三循环。** 请求超时和错误摘要独立于 TXT 同步；成功/失败回调向 GUI 报告，不写同步历史；扫描/发送/状态循环共享停止信号，`StopAsync` 等所有工作结束。无效映射只影响状态上报，TXT 同步继续。
- [ ] **Step 4: 运行 `test.cmd`、`build.cmd` 确认 GREEN，提交采集器改动。** 提交 `feat: report flying probe presence to qms`。

### Task 8: 现场配置窗口与发布验收

**Files:** 采集器新建 `tools/flying-probe-collector/DevicePresenceView.cs`、`package.ps1`；修改 `CollectorForm.cs`、`GuiTests.cs`、`build.cmd`、`test.cmd`、`README.md`。QMS 发布使用现有 `scripts/package_windows.py`，不修改服务器文件。

**Interfaces:** `DevicePresenceView(DeviceHosts hosts, Action<DeviceHosts> save)` 提供目录名→主机名、ICMP/TCP/端口、最近探测、网络/TXT/上报状态；`Update(PresenceReport report, PresenceSendResult result)` 由 GUI 经现有 `BeginInvoke` 调用，映射另设保存按钮并加入 `HasUnsavedChanges()`。

- [ ] **Step 1: 写失败 GUI 测试。** 未配置设备显示待配置、保存后重启仍保留映射、无效端口不可启动探测但 TXT 同步可启动、映射保存失败不启用未落盘值、运行中不可改映射、关闭窗口停止三个循环；旧 Bearer 字段提示网关模式不使用。
- [ ] **Step 2: 运行 `test.cmd` 确认 RED。** 预期新控件/交互断言失败。
- [ ] **Step 3: 实现窗口与文档。** 在现有配置、概况、同步历史之外增加独立设备状态区；保存映射单独原子写，保留现有 INI 保存行为；更新 README 的现场主机名验证、动态 IP、旧 Bearer 清理和“已入队≠QMS 已入库”说明。
- [ ] **Step 4: 运行采集器 `test.cmd`、`build.cmd`，QMS `mvn -B verify`、`npm run build` 确认 GREEN。** 在各自仓库根目录执行 `powershell -NoProfile -File tools/flying-probe-collector/package.ps1` 和 `python scripts/package_windows.py`；采集器脚本用全新暂存目录只装两个 EXE、README、启动脚本及仓库内空白配置模板，绝不打入输出目录的现场令牌。检查两个新 ZIP 的清单、QMS 包含 V34 且不含 V35/V36。
- [ ] **Step 5: 提交采集器改动并做跨仓库审查。** 提交 `feat: show flying probe presence and ship configuration`；复核两个仓库只含计划内文件、全量测试结果及打包时间。QMS 交付发布包由用户自行部署，现场逐台验证主机名、3 次失败、10 分钟回退、5 分钟链路失效和 TXT 同步。

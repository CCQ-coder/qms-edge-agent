# 飞针机采集器窗口 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让飞针机采集器双击打开可配置、可启停、可查看持久同步记录的 Windows 窗口。

**Architecture:** 保留现有解析、队列、检查点和 QMS 协议，抽出共用运行器供 WinForms 与控制台入口调用。用结构化 JSON Lines 记录入队和每次上报结果，窗口在后台采集线程之外读取和展示历史。

**Tech Stack:** C#、.NET Framework 4.x、WinForms、仓库现有 `csc.exe` 构建脚本与自包含测试程序。

**Spec:** `docs/superpowers/specs/2026-10-15-flying-probe-gui-design.md`

## Global Constraints

- 发布目录中的 `FlyingProbeCollector.exe` 为 WinForms；保留 `FlyingProbeCollector.Console.exe` 供命令行使用。
- 共用原有 INI、待发队列、扫描检查点与 QMS Telemetry v1 协议；同一队列不允许两个进程并发采集。
- 令牌输入框遮挡内容；历史文件不保存网关令牌、Bearer Token 或完整请求头。
- 历史为队列目录下 `history/yyyy-MM-dd.jsonl`（默认 `data/history/yyyy-MM-dd.jsonl`），按日期追加，响应摘要最多 8 KiB；旧记录不自动删除。
- 关闭窗口停止采集；已开始的 QMS 请求最多等待配置的请求超时，停止后不能再开始下一条发送。
- 无效配置或共享目录不可用时，窗口仍可打开；保存配置不要求共享目录此时可访问。
- `main` 上现有未提交文件由用户所有，不修改或清理；开发使用已存在的独立工作区。

## Review Focus

1. 历史文件混有损坏行时，有效记录仍可查看且界面报告损坏行数；Task 2 测试。
2. QMS 已返回 `2xx` 但历史写入失败时，待发事件仍保留；Task 2 测试。
3. INI 中令牌仍是示例值时，GUI 窗口仍出现且不启动采集；Task 4 测试。
4. 停止命令发生在待发队列有多条事件时，当前请求之后不再开始下一条；Task 3 测试。
5. 网关令牌、Bearer Token 不出现在历史文件、UI 详情或错误信息中；Task 2 和 Task 5 测试。

---

### Task 1: 可编辑且安全保存的配置

**Files:**
- Create: `tools/flying-probe-collector/SettingsDocument.cs`
- Modify: `tools/flying-probe-collector/Program.cs`
- Modify: `tools/flying-probe-collector/CollectorTests.cs`
- Modify: `tools/flying-probe-collector/build.cmd`, `test.cmd`

**Interfaces:**
- Consumes: 现有 `Settings.Load(string path)` 的校验、默认值与相对路径语义。
- Produces: `SettingsDocument.Load(string path)`, `Get(string key, string fallback)`, `Set(string key, string value)`, `Validate(): Settings`, `Save()`；`Settings.Load` 保留严格校验入口。

- [ ] **Step 1: 写失败测试** `SettingsDraftCanOpenPlaceholder`：示例 INI 可加载为文档，`Validate()` 明确拒绝占位令牌，但 `Get` 仍提供可编辑值；`SavePreservesOtherEntries`：修改五个窗口字段后重读，未知配置项、存储路径与未修改字段仍保持原值；写失败不改变原 INI。
- [ ] **Step 2: 运行测试确认 RED**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期新增断言失败。
- [ ] **Step 3: 实现配置文档**。将 `Settings` 解析校验移到 `SettingsDocument.cs` 可复用的内部函数；缺失文件由 GUI 取得默认草稿，控制台 `Settings.Load` 仍对缺失或无效配置报错。保存用临时文件和原子替换，保留未知行与未编辑值。
- [ ] **Step 4: 运行全套测试确认 GREEN**。运行同一 `test.cmd`；预期原有 24 项和新增配置测试通过。
- [ ] **Step 5: 提交** `feat: edit flying probe configuration safely`。

### Task 2: 持久同步历史和结构化上报结果

**Files:**
- Create: `tools/flying-probe-collector/SyncHistory.cs`
- Modify: `tools/flying-probe-collector/ProbeScanner.cs`, `QmsSender.cs`, `CollectorTests.cs`, `build.cmd`, `test.cmd`

**Interfaces:**
- Consumes: 原有事件字典的 `recordNo`、`machineName`、`materialNo`、`attributes` 字段与待发队列接口。
- Produces: `SyncRecord`（时间、阶段、事件字段、HTTP 状态、请求 JSON、限长响应/错误摘要）；`SyncHistory(string directory, params string[] secrets)`、`Append(SyncRecord record)` 与 `ReadPage(DateTime day, int skip, int take, out int invalidLines): IList<SyncRecord>`；扫描器与发送器的可选 `Action<SyncRecord>` 回调，旧构造器保持可用。

- [ ] **Step 1: 写失败测试** `HistorySurvivesRestart`：入队、失败、重试成功记录跨实例可读；`MalformedHistoryLineIsReported`：混合有效/损坏行返回有效项与错误数；`AcceptedWithoutHistoryStaysQueued`：让记录回调写入失败，模拟 QMS `204` 后事件仍在队列；`HistoryNeverContainsTokens`：令 QMS 响应回显双令牌，磁盘历史仍不含两者；响应摘要 UTF-8 长度不超过 8192 字节。
- [ ] **Step 2: 运行测试确认 RED**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期新增历史与可靠性断言失败。
- [ ] **Step 3: 实现历史和回调**。每日 JSONL 追加，允许写入时并发只读，逐行容错读取并分页；扫描器在队列持久化后通知入队，发送器捕获 HTTP 状态及限长响应。成功记录持久化后才调用 `pending.Remove`；失败事件保留队列。回调只接收事件体，不接收认证头；写盘前遮盖响应与错误中的配置令牌。
- [ ] **Step 4: 运行全套测试确认 GREEN**。运行同一 `test.cmd`；预期原有和新增测试通过。
- [ ] **Step 5: 提交** `feat: persist flying probe sync history`。

### Task 3: GUI 与控制台共用采集运行器

**Files:**
- Create: `tools/flying-probe-collector/CollectorRuntime.cs`
- Modify: `tools/flying-probe-collector/Program.cs`, `QmsSender.cs`, `CollectorTests.cs`, `build.cmd`, `test.cmd`

**Interfaces:**
- Consumes: `Settings`, `SyncHistory`, 扫描器与发送器回调；既有 `CollectorOwnership` 锁。
- Produces: `CollectorRuntime(Settings settings, Action<string> log, Action<SyncRecord> onRecord, Action<int> onPending)`、`Start()`、`StopAsync(): Task`、`IsRunning`；`QmsSender.SendPending(Func<bool> shouldStop)`，无参旧入口保持可用。

- [ ] **Step 1: 写失败测试** `RuntimeStartStopReleasesLock`：以临时目录启动、异步扫描后停止，第二个实例可获取同一队列锁；`StopDoesNotSendNextPendingEvent`：本地 HTTP 服务返回第一条后发出停止请求，第二条未发送而仍在队列；控制台入口仍能启动并响应 Ctrl+C。
- [ ] **Step 2: 运行测试确认 RED**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期运行器/停止行为断言失败。
- [ ] **Step 3: 实现运行器**。单后台线程顺序执行扫描和发送；停止信号打断扫描间隔并在每条发送前检查，等待当前请求结束后释放锁；以回调报告状态和待发数。改造控制台 `Program` 使用运行器，维持 `--config` 与退出码。
- [ ] **Step 4: 运行全套测试确认 GREEN**。运行同一 `test.cmd`；预期原有和新增测试通过。
- [ ] **Step 5: 提交** `refactor: share flying probe collector runtime`。

### Task 4: 可配置的 WinForms 主窗口

**Files:**
- Create: `tools/flying-probe-collector/GuiProgram.cs`, `CollectorForm.cs`, `GuiTests.cs`
- Modify: `tools/flying-probe-collector/test.cmd`

**Interfaces:**
- Consumes: `SettingsDocument`、`CollectorRuntime`、`SyncRecord`。
- Produces: `GuiProgram.Main(string[] args)` 作为窗口入口；`CollectorForm(string configPath)` 提供配置、概况、启停状态，并留出 `SyncHistoryView` 控件位置。

- [ ] **Step 1: 写失败测试** `InvalidConfigOpensWindow`：STA 线程构造窗口，示例令牌使状态为“待配置”且无锁文件；`MaskedTokensAndSave`：两个令牌框为密码模式，修改配置后可保存并重新校验；`StartStopKeepsUiResponsive`：后台请求未结束时窗口仍响应状态更新，停止后可编辑；关闭窗口最终释放锁。
- [ ] **Step 2: 运行 GUI 测试确认 RED**。`test.cmd` 编译运行独立 `GuiTests.exe`，引用 `System.Windows.Forms.dll` 与 `System.Drawing.dll`；预期新增断言失败。
- [ ] **Step 3: 实现窗口壳**。`GuiProgram` 用 `/target:winexe` 入口；`CollectorForm` 用截图相近的分组与可缩放布局，UI 线程使用 `BeginInvoke` 更新。有效配置自动启动；无效配置仅提示；运行中禁用配置编辑；关闭时等待 `StopAsync` 后退出。
- [ ] **Step 4: 运行全套测试确认 GREEN**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期控制台测试和 GUI 测试通过。
- [ ] **Step 5: 提交** `feat: add flying probe desktop controls`。

### Task 5: 同步记录表与详情

**Files:**
- Create: `tools/flying-probe-collector/SyncHistoryView.cs`
- Modify: `tools/flying-probe-collector/CollectorForm.cs`, `GuiTests.cs`, `test.cmd`

**Interfaces:**
- Consumes: `SyncHistory.ReadPage(...)` 与运行器的 `onRecord` 回调。
- Produces: `SyncHistoryView(SyncHistory history)`；`ShowDay(DateTime day)`、`ShowRecent()`、`AddLive(SyncRecord record)`，显示表格、日期筛选、分页及选中记录的请求/响应详情。

- [ ] **Step 1: 写失败测试** `HistoryViewShowsSavedAndLiveRecords`：重启后可见旧日记录，实时结果追加至当前表；`SelectingRowShowsDetailsWithoutSecrets`：所选记录展示设备、料号、请求事件与结果，但无令牌；`PaginationKeepsRowsBounded`：超过一页时只呈现当前页，向前加载能看到更早记录。
- [ ] **Step 2: 运行 GUI 测试确认 RED**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期新增记录视图断言失败。
- [ ] **Step 3: 实现记录视图**。表格只读、按时间排序；筛选日期与“加载更早”分页；选中行更新两个只读详情框，响应/错误以结构化记录展示；历史损坏行计数在状态区提示。实时回调经 UI 线程调度。
- [ ] **Step 4: 运行全套测试确认 GREEN**。运行同一 `test.cmd`；预期控制台与 GUI 测试全部通过。
- [ ] **Step 5: 提交** `feat: show flying probe sync history`。

### Task 6: 双入口发布与现场说明

**Files:**
- Modify: `tools/flying-probe-collector/build.cmd`, `start-collector.cmd`, `README.md`, `CollectorTests.cs`, `GuiTests.cs`

**Interfaces:**
- Consumes: GUI 与控制台 `Main` 入口及前五任务的共用文件。
- Produces: 发布目录中的 `FlyingProbeCollector.exe`、`FlyingProbeCollector.Console.exe`、INI、启动脚本和说明。

- [ ] **Step 1: 写失败测试** `BuildReleaseLayout` 扩展：两个 EXE 均存在，重新构建保留现场 INI；`GuiExeOpensWithPlaceholder`：启动 GUI 程序时占位令牌不会令进程立即退出，窗口可关闭；发布目录的启动脚本启动 GUI。
- [ ] **Step 2: 运行测试确认 RED**。运行 `cmd /c tools\flying-probe-collector\test.cmd`；预期发布布局与 GUI 启动断言失败。
- [ ] **Step 3: 更新构建与说明**。用 `/target:winexe /main:GuiProgram` 构建主 EXE，用 `/target:exe /main:Program` 构建控制台版；保留现场 INI，启动脚本指向窗口版。说明配置、启停、历史目录、重试、直接双击及令牌保密。
- [ ] **Step 4: 全量验证**。运行 `test.cmd`、`build.cmd`、`scripts\build.cmd`、`scripts\verify-qms-telemetry-contract.ps1` 和 `scripts\verify-release-smoke.ps1`；预期全部退出 0。在 Windows 上目视检查窗口在 100%/125% 缩放和缩小窗口时没有关键控件被遮挡。
- [ ] **Step 5: 提交** `feat: package flying probe desktop collector`。

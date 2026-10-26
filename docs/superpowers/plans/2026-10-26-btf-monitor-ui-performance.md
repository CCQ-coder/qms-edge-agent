# 标特福监控界面性能优化实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 25 台设备各每秒一条状态时，保留现有卡片界面，并让待绘制内容有界、合并 UI 刷新。

**Architecture:** 网络回调仍逐条落盘和入队，只把显示内容写入线程安全缓冲区。WinForms 每 250 毫秒取一批，按设备追加文本、一次更新布局；每秒计算一次在线状态。磁盘记录及 QMS/S1 发送语义不变。

**Tech Stack:** C# / .NET Framework 4、WinForms、仓库内 `csc.exe` 构建脚本。

**Spec:** `docs/superpowers/specs/2026-10-26-btf-monitor-ui-performance-design.md`

## Global Constraints

- 每台设备最多暂存 200 条待显示快照；全局日志最多暂存 1000 条。
- 卡片文本超过 240 行时修剪到最新 200 行；全局日志超过 1000 行时修剪到最新 800 行。
- UI 每 250 毫秒批量刷新，稳态下设备在线数每秒更新一次（按钮操作立即更新）；在线判断依据网络回调接收时刻。
- `BtfServer` 的记录写盘、QMS/S1 入队、网络超时及队列持久化行为不变。
- 当前主目录有一份未跟踪的客户报价表；不得将它或 `outputs/` 构建产物加入提交。

## Review Focus

- 单设备突发超过 200 条：仅最旧的待显示快照被淘汰，落盘和上报路径不受影响（任务 1、2）。
- 设备名大小写变化：同一台设备仍对应一张卡片，单机消息顺序不乱（任务 1、2）。
- 清空显示时缓冲区尚有旧消息：旧消息不能在下一次刷新后重现（任务 1、2）。
- 停止监听后仍有最终日志，关闭窗口后不再访问控件；重新启动可继续显示（任务 2）。
- UI 忙碌超过一秒：在线状态按接收时间计算，不按迟到的绘制时间计算（任务 2）。

---

### Task 1: 有界显示缓冲区

**Files:**
- Create: `tools/btf-monitor/BtfDisplayBuffer.cs`
- Modify: `tools/btf-monitor/BtfTests.cs`
- Modify: `tools/btf-monitor/test.cmd`
- Modify: `tools/btf-monitor/build.cmd`

**Interfaces:**
- `BtfDisplayBuffer` 提供 `void AddSnapshot(BtfSnapshot snapshot, DateTime receivedUtc)`、`void AddLog(string line)`、`BtfDisplayBatch Drain()`、`void Clear()`，均可从网络线程调用。
- `BtfDisplayBatch.Machines` 为按不区分大小写的设备名索引的 `Dictionary<string, List<BtfDisplaySnapshot>>`，`Logs` 为有序的 `List<string>`；`Drain` 原子地取出并清空待显示数据。
- `BtfDisplaySnapshot.Snapshot` 保存原快照，`ReceivedUtc` 保存接收 UTC 时间。

- [ ] **Step 1: 添加失败测试。** 在 `BtfTests.cs` 注册 `DisplayBufferBoundsAndPreservesOrder`、`DisplayBufferClearAndDrain`：25 台各追加 205 条，断言每台只留 `5..204`；日志追加 1005 条，只留 `5..1004`；大小写设备名合并；`Drain` 后为空；`Clear` 后旧数据不再出现。
- [ ] **Step 2: 运行 `tools\btf-monitor\test.cmd`，确认新测试因缺少缓冲区实现而失败。**
- [ ] **Step 3: 实现上述三个类型。** 用一个锁保护按设备划分的有界队列和全局日志队列；`Drain` 在锁内转移快照，锁外供 UI 消费。将新文件加入两个批处理脚本的编译源列表。
- [ ] **Step 4: 运行 `tools\btf-monitor\test.cmd`，确认全部测试通过。**
- [ ] **Step 5: 仅提交任务 1 涉及的四个文件，提交说明 `perf: bound BTF display updates`。**

### Task 2: WinForms 批量绘制

**Files:**
- Modify: `tools/btf-monitor/BtfForm.cs:20-269`
- Modify: `tools/btf-monitor/BtfTests.cs`

**Interfaces:**
- `BtfMainForm.ReceiveSnapshot` 只将快照与 `DateTime.UtcNow` 入缓冲；`Report` 继续写磁盘日志，但只将显示行入缓冲。
- `BtfMainForm.FlushDisplay` 由 250 毫秒 UI 定时器调用，批量创建卡片、追加文本和日志；稳态下 `UpdateStatus` 至多每秒调用一次，按钮操作立即调用。

- [ ] **Step 1: 添加失败的界面行为测试。** 合成 25 台快照及多条日志，在手动触发一次 `FlushDisplay` 前后检查卡片数量、每台最近行、全局日志顺序；另测 `ClearDisplay` 清掉未绘制内容、延迟刷新使用原接收时间，并验证超过阈值的文本仍有界。测试不启动 TCP 监听。
- [ ] **Step 2: 运行 `tools\btf-monitor\test.cmd`，确认新增测试对当前逐条绘制行为失败。**
- [ ] **Step 3: 接入 `BtfDisplayBuffer`。** 移除每条记录的 `BeginInvoke`，定时器批量取出；同批新增设备只调用一次 `RebuildGrid`；按设备用 `AppendText` 追加本批内容，仅越过 240 行时重设为最近 200 行；全局日志一次追加本批内容，越过 1000 行时保留最近 800 行。仅状态变化时改标题，在线计算使用接收 UTC 时刻。清空显示同时清空缓冲；关闭时停止定时器。
- [ ] **Step 4: 运行 `tools\btf-monitor\test.cmd`，确认全部测试通过；特别检查 `TcpToQmsAndS1` 仍通过。**
- [ ] **Step 5: 仅提交任务 2 涉及的两个文件，提交说明 `perf: batch BTF monitor rendering`。**

### Task 3: 构建与交付验证

**Files:**
- Build output only: `outputs/btf-monitor/BtfMonitor.exe`（不提交）。

- [ ] **Step 1: 运行 `tools\btf-monitor\test.cmd` 和 `tools\btf-monitor\build.cmd`，确认退出码为 0。**
- [ ] **Step 2: 用 25 台合成快照和上报日志的测试确认缓冲区数量有界、一次刷新覆盖所有设备；运行 `git diff --check` 并检查只有预期源码和文档变更。**
- [ ] **Step 3: 从本次构建输出制作发布 ZIP，使用仓库示例配置，不包含现场配置、日志或队列；检查 ZIP 条目和 EXE 哈希后交付。**

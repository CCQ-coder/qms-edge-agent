# 飞针机设备端电脑状态上报设计

## 目标与边界

飞针机电脑上运行的是 `hr_sync/ini_share_sync` 的 `IniShareSync.exe`。每台电脑从本机 `sync_config.json` 读取唯一的 `device_id`，采集该电脑的 CPU、物理内存、系统盘和开机时长，并把最新状态送到 QMS。状态必须按 `device_id` 关联飞针机设备台账。SCBGX 上的 `FlyingProbeCollector.exe` 仍负责接收 TXT 并向 QMS 发送文件数据；它本机现有的状态行只代表监听电脑，不代表任何飞针机。

设备侧已能写入 `\\Scbgx\数字化\飞针机`。状态沿该共享目录中转，不在 Windows XP 设备上保存 QMS 网关令牌，也不要求设备直连 QMS HTTP 端口。QMS 继续只保存每个来源的最新状态。原有每五分钟 INI 同步任务、文件内容、目标 TXT 和去重状态不改变。

## 数据流与身份

1. `IniShareSync.exe --status-only` 每分钟在飞针机电脑上运行一次，从本机 `sync_config.json` 读取 `target_root` 与 `device_id`。采样不依赖 `D:\WTD\JOBS` 是否可读，也不扫描或复制 INI。
2. 设备端将一个 UTF-8 JSON 快照写入 `target_root/<device_id>/host-status.json`，先写同目录临时文件，再原子替换。字段为 `schemaVersion`（固定为 1）、`deviceId`、`sampledAt`（UTC ISO 8601）、`cpuPercent`（可为空）、`memoryTotalBytes`、`memoryAvailableBytes`、`systemDiskTotalBytes`、`systemDiskFreeBytes`、`uptimeSeconds`。不包含令牌、用户信息或文件内容。每台设备只有这一份最新快照。
3. SCBGX 监听程序在现有扫描周期内读取每个设备目录的 `host-status.json`，只接受大小受限、格式有效、`deviceId` 与所在目录名一致且数值在 QMS 接口允许范围内的快照。文件尚在写入、无效或不可读时记日志，继续处理其它设备和 TXT。
4. SCBGX 将已验证的快照发送到现有 `POST /api/v1/gateway/host-status`，使用自己已有的 QMS 地址和网关令牌。请求的 `hostName` 使用 `deviceId`，其它状态字段按现有接口发送。QMS 现有设备网关映射以同一个 `device_id` 关联设备台账；Windows 电脑名不参与身份判断。QMS 页面将 `hostName` 列标为“来源标识”，兼容飞针机设备编号与 SCBGX 监听电脑名。

`device_id` 必须在飞针机电脑之间唯一；按 Windows 与 QMS 的匹配规则，忽略首尾空格和英文大小写后仍须唯一。部署前核对每台机器的配置及 QMS 映射，并拒绝不能作为 Windows 单层目录名的编号；重复编号会写入同一共享目录，因此不以 Windows 电脑名自动覆盖或猜测设备身份。

## 调度、失败与在线判定

安装脚本为状态模式新增每分钟一次的 `IniShareSyncStatus` 计划任务。原有 `IniShareSync` 每五分钟同步 INI 和 XP 的开机同步任务保持原调度。状态任务使用独立运行锁，避免同一状态任务重叠，同时不阻塞文件同步。

SCBGX 对每个设备保存**成功转发**的快照指纹；只有内容变化才再次发送，收到 QMS 2xx 后才持久化指纹。QMS、共享目录或设备临时故障时，下一轮重试当前最新快照。旧快照不会因为监听程序重复扫描而刷新 QMS 的 `receivedAt`；设备停止产出新快照后，QMS 继续按现有五分钟阈值显示离线。状态错误单独记录，不把文件同步标成失败，也不让一个设备的错误阻断其它设备。

SCBGX 自身现有状态上报保留为单独的监听电脑来源；其设备映射保持“SCBGX - 飞针机采集电脑”，不作为飞针机设备的健康状态。

## 平台与安全

设备端保持 Python 3.4 与 32 位 Windows XP 包兼容，不新增第三方依赖。CPU 使用两次 Windows 系统计数计算短时使用率；首次或异常差值可上报 `null`。内存和系统盘使用 Windows API。XP 无 `GetTickCount64`，开机时长使用可用的 XP 系统计数；其 32 位计数约 49.7 天回绕，作为已知显示限制写入部署说明并在 XP 实机核对。Windows 7 及更新系统可使用 64 位计数。

共享目录继续使用现有 Windows 账户权限。设备端快照不存凭据。SCBGX 校验 JSON 和目录身份后，才使用已配置的网关令牌转发；日志不得写出令牌。

## 实现范围与验证

- `hr_sync/ini_share_sync`：新增状态采样与快照写入，扩展 CLI、计划任务脚本、XP/Win7 打包说明和相关测试；保留原文件同步语义。
- `qms-edge-agent/tools/flying-probe-collector`：新增快照读取、校验、成功指纹及转发，复用现有 QMS HTTP 发送能力；更新构建、测试和说明。
- QMS：复用已部署的最新状态接口、表和设备映射；仅调整前端列名/说明，避免把 `device_id` 称作 Windows 电脑名。先完成尚待安装的 QMS 字段排序规则修复包。

验证包括：设备端本地测试快照字段、配置编号、原子替换、状态失败不影响 INI；SCBGX 测试多设备目录、编号不符、无效数值、旧快照不重发、HTTP 失败后重试、TXT 同步不受影响；最后在一台真实飞针机电脑上校验 XP/Win7 指标与计划任务，并在 QMS 核对对应设备的在线与离线变化。实际设备端部署需取得该机器的远程入口；SCBGX 只能验证中转，不能证明飞针机本机采样正常。

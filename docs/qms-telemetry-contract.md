# QMS Telemetry v1 接入契约

本文件描述 QMS Edge Agent 作为客户端必须保持的兼容边界；服务端契约的权威来源是 QMS。

| 项目 | 值 |
| --- | --- |
| 请求 | `POST {QmsBaseUrl}/api/v1/equipment/telemetry` |
| 请求体 | `{ "events": [...] }` |
| 认证 | `X-Gateway-Token` |
| 成功 | 任意 HTTP `2xx` |
| 幂等 | QMS 按稳定事件记录号处理重复上报 |

设备请求被验证并安全写入 Edge Agent 本地队列后，监听器即可按原协议返回成功；这不表示 QMS 已完成处理。仅当 QMS 返回 `2xx` 时，发送批次才会从 `data\\pending-events.json` 删除。网络失败、非 `2xx` 或 QMS 暂不可用时，队列必须保留并按配置重试。

接口字段、认证机制或错误语义发生破坏性变化前，QMS 必须发布新的契约版本；Edge Agent 在明确支持前继续使用 v1。

## 飞针机电脑状态

飞针机采集程序每 60 秒使用相同的 QMS 地址、`X-Gateway-Token` 和可选 Bearer Token，发送一次 `POST {QmsBaseUrl}/api/v1/gateway/host-status`。请求体为单个对象：`hostName`、UTC `sampledAt`、可为 `null` 的 `cpuPercent`、`memoryTotalBytes`、`memoryAvailableBytes`、`systemDiskTotalBytes`、`systemDiskFreeBytes`、`uptimeSeconds`。大小均以字节为单位，开机时间以秒为单位；CPU 为 0–100 的百分比。QMS 按租户和电脑名保存最新一条，并用服务端的 `receivedAt` 标识最近接收时间。需登录的 `GET /api/v1/gateway/host-status` 返回 `{ "data": [...] }`。

这条状态请求不使用 TXT 待发队列；非 `2xx`、重定向或网络故障只记录安全的失败摘要，下轮重新采集。部署时先升级 QMS 接口和数据库，再更新采集程序。

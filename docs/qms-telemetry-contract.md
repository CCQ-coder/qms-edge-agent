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

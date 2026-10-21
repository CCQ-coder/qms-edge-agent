# Changelog

## Unreleased

- 将森泉 MES Mock 的源码、示例配置、构建脚本和测试从 QMS 的 `gateway/hoson-mes-mock` 迁入 `tools/hoson-mes-mock`，并在根目录 README 说明设备工具各自的用途。
- 根据 2026-10-17 科视现场报文修正 `assets`、空字段、OEE、报警和 `/Heart` 解析，并保留 `/HeartBeat` 兼容入口。
- 在主监听器和实际接收 4524 报文的科视进程中增加到 S1 的 MQTT 3.1.1/QoS 1 转发、独立持久队列、物模型映射模板及图形配置。

## v1.0.0-extracted — 2026-10-14

- 从 QMS 仓库抽离精德、森泉、科视设备监听器，建立本项目作为唯一源码。
- 保留 .NET Framework/WinForms、INI 配置、设备 HTTP 协议、QMS Telemetry v1、持久化队列与现场启动方式。
- 本版本不改变设备协议或遥测行为。

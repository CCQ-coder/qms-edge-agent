# QMS Edge Agent

QMS Edge Agent 是部署在设备网段中间电脑上的 Windows 监听程序，是精德 EAP、森泉 MES 与科视 LDI 设备适配器的唯一源码仓库。它先将归一化事件落入本地持久化队列，再异步发送至 QMS Telemetry v1 接口；因此 QMS 短暂不可用时，设备侧上报不会因云端延迟而丢失。

## 仓库中的设备工具

| 工具 | 用途 | 源码与构建 | 运行场景 |
| --- | --- | --- | --- |
| 精德 EAP / 森泉 MES / 科视 LDI 设备监听器 | 接收现场设备上报，归一化后持久化并转发至 QMS Telemetry v1；科视数据可同时通过 MQTT QoS 1 转发至 S1 | `src/JingdeEapForwarder.cs`、`src/S1Mqtt.cs`；运行 `scripts\build.cmd`，产物在 `outputs\jingde-eap-forwarder` | 现场中间电脑；森泉设备上报默认监听 TCP 6680 |
| 森泉 MES Mock | 模拟厂家 MES 服务端，按设备请求的 `method` 返回可编辑的示例应答，供联调和测试使用 | [`tools/hoson-mes-mock`](tools/hoson-mes-mock/README.md)；运行 `tools\hoson-mes-mock\build.cmd`，产物在 `outputs\hoson-mes-mock` | 测试电脑或隔离测试网段；默认监听 TCP 4523 的 `POST /api/mes/invoke` |
| 科视光学 MES Mock / S1 转发器 | 在 `192.168.1.71:4524` 接收科视 HTTP/TLS 上报、记录原始报文，并可用独立持久队列通过 MQTT QoS 1 转发到 S1 | [`tools/kst-mes-mock`](tools/kst-mes-mock/README.md)；运行 `tools\kst-mes-mock\build.cmd`，产物在 `outputs\kst-mes-mock` | 测试或现场中间电脑；打开窗口后手动启动 |

森泉 MES Mock 来自原 QMS 仓库的 `gateway/hoson-mes-mock`，使用厂家提供的 MES 服务端 DEMO 应答配置。它只模拟 MES 应答，不接收设备遥测，也不向 QMS 转发事件；现场正式接入使用上表的设备监听器。Mock 的启动、配置、日志和测试方法见其[独立说明](tools/hoson-mes-mock/README.md)。

## 快速构建

在已安装 .NET Framework 4.x 编译器的 Windows 计算机上运行：

```bat
scripts\build.cmd
```

发布目录为 `outputs\jingde-eap-forwarder`。将该目录整体复制到现场中间电脑后，可运行 `start-gateway.cmd`，首次联调可执行 `JingdeEapForwarder.exe --self-test`。

详细安装、配置、端口、防火墙、本地队列、S1 MQTT 模板和故障恢复说明见 [部署指南](docs/README.md)。与 QMS 的稳定接入边界见 [QMS Telemetry v1 契约](docs/qms-telemetry-contract.md)。

QMS 后端拥有遥测服务端契约、设备映射、鉴权、幂等和业务数据；本仓库不读取 QMS 数据库，也不包含 QMS 后端源码。

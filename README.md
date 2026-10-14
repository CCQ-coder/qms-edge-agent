# QMS Edge Agent

QMS Edge Agent 是部署在设备网段中间电脑上的 Windows 监听程序，是精德 EAP、森泉 MES 与科视 LDI 设备适配器的唯一源码仓库。它先将归一化事件落入本地持久化队列，再异步发送至 QMS Telemetry v1 接口；因此 QMS 短暂不可用时，设备侧上报不会因云端延迟而丢失。

## 快速构建

在已安装 .NET Framework 4.x 编译器的 Windows 计算机上运行：

```bat
scripts\build.cmd
```

发布目录为 `outputs\jingde-eap-forwarder`。将该目录整体复制到现场中间电脑后，可运行 `start-gateway.cmd`，首次联调可执行 `JingdeEapForwarder.exe --self-test`。

详细安装、配置、端口、防火墙、本地队列和故障恢复说明见 [部署指南](docs/README.md)。与 QMS 的稳定接入边界见 [QMS Telemetry v1 契约](docs/qms-telemetry-contract.md)。

QMS 后端拥有遥测服务端契约、设备映射、鉴权、幂等和业务数据；本仓库不读取 QMS 数据库，也不包含 QMS 后端源码。

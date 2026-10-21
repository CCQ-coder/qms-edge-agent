# 标特福锣机监听与 S1 转发

`BtfMonitor.exe` 是可独立运行的 Windows 监听程序，参考厂商提供的 `WorkingMonitor` 协议与监控窗口。设备主动连接本机 TCP `6100`，依次发送 `#Username`、`#Filename`、`#Operator`、`#Spindles`、`#RoutLength`、`#ScreenText` 指令及固定 80 字节统计帧。程序按设备分窗显示记录，提供系统、ERP/QMS 和 S1 MQTT 参数页面，以及暂停接收、清空显示、启动/停止监听。

## 构建与启动

在 Windows 上运行 `build.cmd`，发布包在 `outputs\btf-monitor`。双击 `start-monitor.cmd` 启动。只允许一个进程占用同一 IP 的 TCP `6100`；部署此程序前应停止旧版 `WorkingMonitor.exe`，并将设备配置的监控服务器 IP 指向本机。若端口被占用，程序会明确显示启动错误。可用 `BtfMonitor.Console.exe --headless` 无窗口运行。更新运行中的程序时，先退出旧进程，再替换可执行文件并重启；保留原有 `BtfMonitor.json` 和 `data` 目录，使设备凭据及待补传记录继续可用。

## QMS 接入

在“设置 → ERP / QMS 参数”启用上报，填写 QMS 地址、端口、URL `/hessian/monitor`、Method `Monitor` 和上报间隔（秒）。此程序使用与旧版软件一致的 Hessian 1 `Monitor(String)` 格式。QMS 端仍需配置 `QMS_BTF_HESSIAN_ALLOWED_IPS`、`QMS_BTF_HESSIAN_TENANT_CODE` 和设备网关映射。每台设备按间隔上报，状态改变时立即上报。快照先写入 `data\pending-qms`，收到 QMS 的 Hessian `OK` 后才从待发队列确认；网络故障时保留并重试。

## S1 MQTT

先在 S1 创建并发布 `s1-btf-model-template.json` 列出的物模型标识符，再为每台锣机创建 S1 设备。在“设置 → S1 MQTT 参数”填写 Broker、默认产品 ID、默认用户 ID 和上报间隔（秒），并在设备表中按现场 `#Username` 填写 S1 设备编号、MQTT 用户名和密码。S1 上报间隔可设为 1～3600 秒，默认 1 秒；各现场窗口分别计时，状态变化时立即上报。同一窗口可配置多个 S1 产品：在设备行填写产品 ID、用户 ID 即可覆盖默认值。源码配置不包含实际凭据；运行配置位于 `outputs/btf-monitor/BtfMonitor.json`。当前可达的 S1 Broker 为 `171.35.40.79:1883`。

2026-10-20 已在 S1 发布产品“标特福锣机”（产品 ID `19`、用户 ID `1`），导入 10 个属性和 1 个报警事件，并在此产品下创建 22 台设备。设备显示名称依次为“标特福锣机#1号”至“标特福锣机#22号”，设备编号保持 `CNC01` 至 `CNC22`，用于监听映射和 MQTT 主题。设备批量导入文件为 `outputs/btf-monitor/s1-btf-22-devices.xlsx`，物模型导入文件为 `outputs/btf-monitor/s1-btf-model.xlsx`。本机运行配置已启用 S1，映射 `CNC#01` 至 `CNC#22`；每台设备可单独覆盖产品和用户 ID。已验证 `CNC01` 与 `CNC22` 的 MQTT CONNECT 成功，尚需现场机台报文验证实际数据上报。

程序发布 MQTT 3.1.1、QoS 1。属性主题为 `/{productId}/{deviceNumber}/property/post`，报警事件主题为 `/{productId}/{deviceNumber}/event/post`；正文为带 `id`、`value`、`ts`、`remark` 的 JSON 数组。`remark` 保留现场设备名。现场名称 `CNC#14`、`CNC-14`、`CNC14` 会匹配同一条锣机映射；多产品窗口名如 `CNC-22_1`、`CNC-22_2` 也会匹配 `CNC#22` 的 S1 映射，两路数据发往同一台 `CNC22`，窗口仍分别显示。每台 S1 设备有独立的持久队列，收到 `PUBACK` 才确认；单台设备发送失败不会阻塞其他设备。未配置映射的现场设备存入 `data\pending-s1\unmapped`，补齐映射并重启后自动转入相应设备队列。

S1 设备在线状态由 MQTT 连接维持。收到现场数据后，即使上报间隔较长，程序也会在 MQTT 连接空闲时发送 `PINGREQ` 并检查 `PINGRESP`；连接失效且仍有现场数据时会自动重连。多产品窗口共用映射设备的在线状态。现场数据超过“空闲断线时间”或两倍 MQTT 保活时间（取较大值）仍未恢复时，程序主动断开 S1 连接。当前建议将“系统参数 → 空闲断线时间”设为 120 秒，避免短暂的数据间歇把正在运行的锣机误判为离线；若确实停止上报，则最多约 120 秒后显示离线。更新已部署的程序需要替换可执行文件并重启，仅调整保活参数而不更新程序无法补上 MQTT 心跳。

`BtfMonitor.json` 中保存 MQTT 密码，请限制此文件的读取权限。设备原始快照按接收日期保存在 `records`，启动时和跨天时清理超过“记录保留天数”的日期目录；`logs` 保存监听及转发状态。“清空显示”仅清空窗口文本，不删除磁盘记录和待发队列。

运行 `test.cmd` 可验证分帧、字段解析、队列恢复、S1 物模型和 Hessian 编码。需要现场机台验证实际报文、设备名映射、QMS 白名单及 S1 Broker 凭据。
